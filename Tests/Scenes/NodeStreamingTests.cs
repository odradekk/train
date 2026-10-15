using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeStreamingTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public async Task Near_parts_are_interactive_while_far_work_is_blocked()
    {
        var snapshot = Snapshot(true, nearVehicle: true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles();
        var instance = ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        vehicles.Prepare(snapshot.Content, instance);
        await Task.WhenAll(vehicles.PendingWork);
        await using var scene = Open(snapshot, vehicles, build: (data, from, old, token) =>
        {
            if (!new NodeRange(default, NodeStreaming.NearCells).Intersects(from))
            { entered.TrySetResult(); release.Wait(token); }
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            while (!entered.Task.IsCompleted)
            {
                scene.Pump();
                if (!entered.Task.IsCompleted) await Task.WhenAny(Task.WhenAll(scene.PendingWork), entered.Task);
            }
            var frame = Present(scene, snapshot);
            Assert.AreEqual(SceneReadiness.Partial, frame.Readiness);
            var hit = (ScenePick.Hit)frame.Pick(Ray(scene.RetainedChunks.Keys.First()));
            Assert.AreEqual(new PickSubject.Terrain(NodeFixtures.Location), hit.Value.Subject);
            var subject = new WorldObjectId.Instance(instance.Id);
            var pose = ((Lookup<ScenePose>.Found)frame.FindPose(subject)).Value;
            var cellCenter = pose.Position + Vector3.Transform(new Vector3(.5f, .5f, .5f), pose.Rotation);
            var businessHit = (ScenePick.Hit)frame.Pick(new(cellCenter + Vector3.UnitY * 20, -Vector3.UnitY, 100));
            Assert.AreEqual(new PickSubject.WorldObject(subject, instance.Design, new PartId(1)), businessHit.Value.Subject);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Unbuilt_and_unloaded_regions_are_fogged_and_not_pickable()
    {
        var snapshot = Snapshot(true);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles);
        await Settle(scene, default);
        var loaded = scene.RetainedChunks.Keys.First();
        var frame = Present(scene, snapshot);
        Assert.IsInstanceOfType<ScenePick.Hit>(frame.Pick(Ray(loaded)));
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(Ray(new(288, 288))));
        await Settle(scene, new(288, 0, 288));
        var after = Present(scene, snapshot, frame);
        Assert.IsInstanceOfType<ScenePick.NotReady>(after.Pick(Ray(loaded)));
        Assert.IsInstanceOfType<Lookup<double>.Missing>(after.TerrainHeight(loaded.X + .5, loaded.Z + .5));
        Assert.IsTrue(after.Update.Removed.Length > 0);
        Assert.IsInstanceOfType<ScenePick.Hit>(frame.Pick(Ray(loaded)));
    }

    [TestMethod]
    public async Task Moving_focus_unloads_parts_outside_retention()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache);
        await Settle(scene, default);
        var before = scene.RetainedChunks;
        var focus = new ScenePoint(288, 0, 288);
        await Settle(scene, focus);
        var retention = new NodeRange(focus, NodeStreaming.RetainCells);
        Assert.IsTrue(before.Keys.Any(from => !retention.Intersects(from)));
        Assert.IsTrue(scene.RetainedChunks.Keys.All(retention.Intersects));
        Assert.AreEqual(scene.RetainedChunks.Count,
            cache.Read(NodeFixtures.NodeId)!.Parts.Keys.Count(key => key.StartsWith("terrain:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Streaming_parts_match_one_shot_build()
    {
        var snapshot = Snapshot(false);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var reference = NodeFixtures.Build(snapshot, layout);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, build: (data, from, old, token) => data.BuildChunk(from, old, token));
        var collected = new Dictionary<GridColumn, NodeScene.Part>();
        foreach (var focus in new[] { new ScenePoint(-16, 0, -16), new ScenePoint(16, 0, 16) })
        {
            await Settle(scene, focus);
            foreach (var part in scene.RetainedChunks) collected[part.Key] = part.Value;
        }
        Assert.AreEqual(reference.Parts.Keys.Count(key => key.StartsWith("terrain:", StringComparison.Ordinal)), collected.Count);
        foreach (var (from, part) in collected)
        {
            var expected = reference.Parts[NodeScene.ChunkKey(from)];
            Assert.AreEqual(expected.Inputs, part.Inputs);
            Assert.AreEqual(expected.Origin, part.Origin);
            CollectionAssert.AreEqual(expected.Geometry.Geometry.Objects.Select(item => item.Pose).ToArray(),
                part.Geometry.Geometry.Objects.Select(item => item.Pose).ToArray());
            CollectionAssert.AreEquivalent(expected.Heights.ToArray(), part.Heights.ToArray());
            var expectedMeshes = expected.Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().ToArray();
            var actualMeshes = part.Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().ToArray();
            Assert.AreEqual(expectedMeshes.Length, actualMeshes.Length);
            for (var i = 0; i < expectedMeshes.Length; i++)
            {
                Assert.AreEqual(expectedMeshes[i].Mesh.Material, actualMeshes[i].Mesh.Material);
                CollectionAssert.AreEqual(expectedMeshes[i].Mesh.Vertices.ToArray(), actualMeshes[i].Mesh.Vertices.ToArray());
                CollectionAssert.AreEqual(expectedMeshes[i].Mesh.TriangleIndices.ToArray(), actualMeshes[i].Mesh.TriangleIndices.ToArray());
            }
        }
    }

    [TestMethod]
    public async Task Cancelled_work_cannot_attach_or_publish_cache()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<GridColumn>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache, (data, from, old, token) =>
        {
            entered.TrySetResult(from);
            release.Wait();
            return SmallPart(data, from, old, CancellationToken.None);
        });
        await scene.BuildWork!;
        scene.Pump();
        var blocked = await entered.Task;
        var pending = scene.PendingWork;
        try { scene.Pump(focus: new(288, 0, 288)); }
        finally { release.Set(); }
        await Task.WhenAll(pending);
        scene.Pump();
        Assert.IsFalse(scene.RetainedChunks.ContainsKey(blocked));
        Assert.IsFalse(cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(NodeScene.ChunkKey(blocked)));
    }

    [TestMethod]
    public async Task Reopen_reuses_only_retained_parts()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        ImmutableDictionary<GridColumn, NodeScene.Part> retained;
        await using var vehicles = new SceneVehicles();
        await using (var scene = Open(snapshot, vehicles, cache))
        {
            await Settle(scene, default);
            await Settle(scene, new(288, 0, 288));
            retained = scene.RetainedChunks;
        }
        await using var reopened = Open(snapshot, vehicles, cache);
        await reopened.BuildWork!;
        reopened.Pump(focus: new(288, 0, 288));
        Assert.AreEqual(retained.Count, reopened.RetainedChunks.Count);
        foreach (var (from, part) in retained) Assert.AreSame(part, reopened.RetainedChunks[from]);
    }

    [TestMethod]
    public async Task Closing_scene_drains_chunk_work_without_publishing_it()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<GridColumn>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, cache, (data, from, old, token) =>
        {
            entered.TrySetResult(from);
            release.Wait();
            return SmallPart(data, from, old, CancellationToken.None);
        });
        await scene.BuildWork!;
        scene.Pump();
        var blocked = await entered.Task;
        var closing = scene.DisposeAsync().AsTask();
        try { Assert.IsFalse(closing.IsCompleted); }
        finally { release.Set(); await closing; }
        Assert.IsFalse(scene.RetainedChunks.ContainsKey(blocked));
        Assert.IsFalse(cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(NodeScene.ChunkKey(blocked)));
    }

    [TestMethod]
    public async Task Node_fog_regions_remain_bounded()
    {
        var snapshot = Snapshot(true);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles);
        await scene.BuildWork!;
        for (var i = 0; i < 20; i++)
        {
            scene.Pump(focus: new(i % 2 == 0 ? -288 : 288, 0, 0));
            var frame = Present(scene, snapshot);
            Assert.IsTrue(frame.Update.Environment.FoggedRegions.Length <= NodeStreaming.MaximumTerrainFogRegions);
            await Task.WhenAll(scene.PendingWork);
        }
    }

    private static SceneWorldSnapshot Snapshot(bool large, bool nearVehicle = false)
    {
        var source = NodeFixtures.Content();
        var layout = NodeFixtures.Layout with
        {
            Plots = [],
            SceneModel = NodeFixtures.Layout.SceneModel with
            {
                Scene = SceneFixtures.Projection(large ? [SceneFixtures.Cell(new(-320, 0, -320)), SceneFixtures.Cell(new(319, 0, 319), 2)] : []) with
                { OccupiedBounds = large ? new(new(-320, 0, -320), new(319, 0, 319)) : null }
            }
        };
        var content = new FixedContentView
        {
            Generation = source.Generation, Core = source.Core, Voxels = source.Voxels,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = source.FindDesign(SceneFixtures.Design) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = source.FindVoxelAppearance(SceneFixtures.Voxel) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = source.FindTerrain(RailwayFixtures.Terrain) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = source.FindRailwayAppearance(RailwayFixtures.Rule) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(layout) }
        };
        var original = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: [], content: content);
        var node = original.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId);
        var train = NodeFixtures.Train();
        var vehicle = RailwayFixtures.Instance(train: train.Id) with { Location = NodeFixtures.Location };
        return new(new FixedCoreReadView
        {
            Position = original.Core.Position, InputIdentity = original.Core.InputIdentity,
            World = original.Core.World with { Nodes = [node], Edges = [],
                Trains = nearVehicle ? [new(train.Id, train.Position, train.Activity)] : [] },
            Trains = nearVehicle ? new Dictionary<TrainId, Lookup<TrainView>>
                { [train.Id] = new Lookup<TrainView>.Found(train) } : [],
            Instances = nearVehicle ? new Dictionary<InstanceId, Lookup<InstanceView>>
                { [vehicle.Id] = new Lookup<InstanceView>.Found(vehicle) } : [],
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            { [new(1)] = new Lookup<SettlementView>.Found(NodeFixtures.Settlement(0) with { Plots = [] }) }
        }, content);
    }

    private static NodeScene Open(SceneWorldSnapshot snapshot, SceneVehicles vehicles, NodeScene.Cache? cache = null,
        Func<NodeScene.Preparation, GridColumn, NodeScene.Part?, CancellationToken, NodeScene.Part>? build = null)
    {
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        return new(snapshot, NodeFixtures.NodeId, cache ?? new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
            return id;
        }, vehicles, buildChunk: build ?? SmallPart);
    }

    private static NodeScene.Part SmallPart(NodeScene.Preparation data, GridColumn from, NodeScene.Part? old, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var geometry = WorldSceneSession.BuildGeometry(id =>
        {
            var root = id("");
            var grid = new VoxelGrid([new(new(from.X, 0, from.Z), new(VoxelOrientation.PxPy, SceneFixtures.Cube,
                null, null, new PickSubject.Terrain(NodeFixtures.Location), []))]);
            return new([new(root, null, SceneFixtures.Identity, null, DesignGeometry.Meshes(grid, false), [], [])],
                [new(root, SceneFixtures.Identity, grid)], []);
        });
        return new(data.ChunkInputs(from), geometry, ImmutableDictionary<GridColumn, double>.Empty.Add(from, 1));
    }

    private static async Task Settle(NodeScene scene, ScenePoint focus)
    {
        await scene.BuildWork!;
        do
        {
            scene.Pump(focus: focus);
            await Task.WhenAll(scene.PendingWork);
            scene.Pump(focus: focus);
        } while (scene.PendingWork.Length > 0);
    }

    private static SceneFrame Present(NodeScene scene, SceneWorldSnapshot snapshot, SceneFrame? previous = null) =>
        scene.Present(snapshot, Request, new(new(Guid.Empty), new(1), new(1), 1), previous, default);

    private static SceneRay Ray(GridColumn from) => new(new(from.X + .5f, 20, from.Z + .5f), -Vector3.UnitY, 100);
}

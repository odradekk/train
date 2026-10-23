using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeLifecycleTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static SceneFrame Present(WorldSceneSession session, ISceneFrame? displayed = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request, displayed)).Value;
    private static WorldSceneSession Open(SceneWorldSnapshot snapshot,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built>? build = null) =>
        new(new(Guid.NewGuid()), snapshot, new(), new(), build);

    [TestMethod]
    public async Task MissingRailwayAppearanceReturnsFailedFrameWithoutThrowingRequest()
    {
        var content = NodeFixtures.Content();
        content = new()
        {
            Generation = content.Generation, Core = content.Core, Voxels = content.Voxels,
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(NodeFixtures.Layout) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = content.FindDesign(SceneFixtures.Design) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = content.FindTerrain(RailwayFixtures.Terrain) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = content.FindVoxelAppearance(SceneFixtures.Voxel),
                [NodeFixtures.Concrete] = content.FindVoxelAppearance(NodeFixtures.Concrete)
            },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Missing([]) }
        };
        await using var session = Open(NodeFixtures.Snapshot(trains: [], content: content));
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location)));
        await AwaitExpectedBuildFailure(session.PendingBuild);
        AssertFailed(Present(session));
    }

    [TestMethod]
    public async Task BackgroundNodeBuildFailureReturnsFailedFrame()
    {
        await using var session = Open(NodeFixtures.Snapshot(trains: []),
            (_, _, _, _) => throw new RailwayBuildException("Controlled node build failure."));
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location)));
        await AwaitExpectedBuildFailure(session.PendingBuild);
        AssertFailed(Present(session));
    }

    [TestMethod]
    public async Task FailedCandidateIsDiscardedWithoutFaultingCurrentRailway()
    {
        var train = RailwayFixtures.Train(1, direction: TravelDirection.Reverse, speed: 1);
        var snapshot = Small(train, 0);
        var attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Open(snapshot, (_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            throw new RailwayBuildException("Controlled candidate failure.");
        });
        session.RequestView(new WorldSceneTarget.Follow(train.Id, new(1)));
        var line = await RailwayFixtures.Settled(session, Request);
        await entered.Task;
        foreach (var work in session.PendingPrebuilds) await AwaitExpectedBuildFailure(work);
        line = Present(session, line);
        Assert.AreNotEqual(SceneReadiness.Failed, line.Readiness);
        Assert.IsFalse(line.Diagnostics.Any(problem => problem.Code == "scene_internal_fault" || problem.Code == "build_failed"));
        Assert.IsNull(session.PrebuiltLocation);
        Assert.AreEqual(1, attempts);
        session.Observe(Small(train with { SpeedMetersPerSecond = 0 }, 1), []);
        line = Present(session, line);
        session.Observe(Small(train, 2), []);
        line = Present(session, line);
        foreach (var work in session.PendingPrebuilds) await AwaitExpectedBuildFailure(work);
        line = Present(session, line);
        Assert.AreEqual(2, attempts, "Reentering the prefetch range permits a new candidate after leaving it.");
        Assert.IsNull(session.PrebuiltLocation);
        session.Observe(Small(NodeFixtures.Train(enteredFrom: new(1)), 3), []);
        Present(session, line);
        await AwaitExpectedBuildFailure(session.PendingBuild);
        AssertFailed(Present(session));
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(false, 0)]
    public async Task CountChangesPreserveGeometryAndOnlyTrackChangesReportPendingTrain(bool platforms, int openingTracks)
    {
        var train = NodeFixtures.Train(enteredFrom: new(1),
            activity: platforms ? TrainActivity.Servicing : TrainActivity.Waiting);
        var initial = NodeFixtures.Snapshot(platforms: 1, tracks: openingTracks, trackLimit: 2, trains: openingTracks == 0 ? [] : [train]);
        var builds = 0;
        await using var session = Open(initial, (input, node, _, token) =>
        {
            Interlocked.Increment(ref builds);
            var layout = NodeLayout.Build(input, node, token);
            return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("test-ground",
                new(new("", "fixed"), WorldSceneSession.BuildGeometry(id => DesignGeometry.Build(input.Content,
                    SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))])),
                    null, SceneFixtures.Identity, id)), ImmutableDictionary<GridColumn, double>.Empty)), []);
        });
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var before = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(SceneReadiness.Ready, before.Readiness);
        var geometry = before.Update.Upserts.Where(item => item.Subject is null).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        Assert.IsTrue(geometry.Length > 0);
        var changed = NodeFixtures.Snapshot(platforms: platforms ? 2 : 1, tracks: platforms ? 1 : openingTracks + 1,
            trackLimit: 2, trains: [train], steps: 1);
        session.Observe(changed, []);
        var after = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(SceneReadiness.Ready, after.Readiness);
        Assert.AreEqual(platforms ? 1 : 2, builds);
        if (platforms)
        {
            var afterGeometry = after.Update.Upserts.Where(item => item.Subject is null).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
            Assert.AreEqual(geometry.Length, afterGeometry.Length);
            for (var index = 0; index < geometry.Length; index++) Assert.AreSame(geometry[index], afterGeometry[index]);
        }
        Assert.IsFalse(after.Diagnostics.Any(problem => problem.Code == "railway_update_pending"));
        Assert.AreEqual(before.NodePanBounds, after.NodePanBounds);
        if (openingTracks == 0)
        {
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
            Assert.IsInstanceOfType<Lookup<ScenePose>.Found>(after.FindPose(subject));
            Assert.IsTrue(after.Objects.Where(item => item.Subject == subject).Any(item => !item.Visuals.IsEmpty));
        }
    }

    [TestMethod]
    public async Task AddedEntryTrackKeepsUntouchedPartsAndBuildsBallast()
    {
        var ballast = new VoxelDefinitionId("test:ballast");
        var basis = NodeFixtures.Content(rules: RailwayFixtures.Rules with { BallastVoxel = ballast, MinimumCurveRadiusMeters = 1 });
        var vehicle = RailwayFixtures.Vehicle();
        var vehicleHash = vehicle is DesignEvaluation.Complete complete ? complete.Design : SceneFixtures.Design;
        var designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        {
            [vehicleHash] = new ContentLookup<DesignEvaluation>.Found(vehicle),
            [NodeFixtures.StationDesign] = basis.FindDesign(NodeFixtures.StationDesign)
        };
        if (vehicleHash != SceneFixtures.Design) designs[SceneFixtures.Design] = basis.FindDesign(SceneFixtures.Design);
        var content = new FixedContentView
        {
            Generation = basis.Generation, Core = basis.Core,
            Voxels = [new(NodeFixtures.Concrete, null), new(SceneFixtures.Voxel, null), new(ballast, null)],
            Designs = designs,
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = basis.FindSettlementLayout(new(1)) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = basis.FindVoxelAppearance(SceneFixtures.Voxel),
                [NodeFixtures.Concrete] = basis.FindVoxelAppearance(NodeFixtures.Concrete),
                [ballast] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
                {
                    Definition = ballast,
                    Presentation = new VoxelPresentation.Cube([new("cube")], MaterialCategory.PaintedMetal, 1,
                        new("test:ballast"), new("test:ballast"), new("test:ballast"), new("test:ballast"))
                })
            },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = basis.FindTerrain(RailwayFixtures.Terrain) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = basis.FindRailwayAppearance(RailwayFixtures.Rule) }
        };
        var initial = NodeFixtures.Snapshot(platforms: 0, tracks: 0, trackLimit: 2, trains: [], content: content);
        initial = RailwayFixtures.ReplaceCore(initial, node: initial.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        initial = RailwayFixtures.ReplaceCore(initial, node: initial.Core.World.Nodes[1] with { Position = new(40, 0) });
        initial = RailwayFixtures.ReplaceCore(initial, node: initial.Core.World.Nodes[2] with { Position = new(30, 40) });
        initial = RailwayFixtures.ReplaceCore(initial, edge: initial.Core.World.Edges[0] with { LengthMeters = 50 });
        // The other entry already has a track, so the shared terrain key already includes ballast.
        initial = RailwayFixtures.ReplaceCore(initial, edge: initial.Core.World.Edges[1] with { Tracks = 1, LengthMeters = 50 });
        var changed = RailwayFixtures.ReplaceCore(initial, edge: initial.Core.World.Edges[0] with { Tracks = 1 });
        var route = NodeLayout.Build(changed, NodeFixtures.NodeId);
        var added = route.EntryRoutes(route.Entries.Single(item => item.Edge.Value == 1), 1).First();
        var at = added.Sample(Math.Min(64, added.Length / 2)).Position;
        var focus = new ScenePoint(at.X / 2, at.Y, at.Z / 2);
        var bedChunk = new GridColumn((int)Math.Floor(at.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor(at.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        var ids = new Dictionary<string, SceneObjectId>();
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(initial, NodeFixtures.NodeId, new(), key =>
            ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1), vehicles, new(), _ => null,
            buildChunk: (data, from, old, token) =>
            {
                if (old?.Inputs == data.ChunkInputs(from)) return old;
                if (data.BedChunks.ContainsKey(from) && Math.Abs(from.X - bedChunk.X) <= VoxelGrid.ChunkSize * 2 &&
                    Math.Abs(from.Z - bedChunk.Z) <= VoxelGrid.ChunkSize * 2)
                    return data.BuildChunk(from, old, token);
                var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
                    [new SceneVertex(new(from.X, 0, from.Z), Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)], [0, 0, 0]);
                return new(data.ChunkInputs(from), WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null,
                    [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])], [], [])), ImmutableDictionary<GridColumn, double>.Empty);
            });
        var before = await Settle(scene, initial, focus);
        scene.Present(changed, Request, NodeFixtures.NextStamp(scene), before, default);
        await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        var held = scene.Present(changed, Request, NodeFixtures.NextStamp(scene), before, default);
        var settled = await Settle(scene, changed);
        var after = scene.Present(changed, Request, NodeFixtures.NextStamp(scene), held, default);

        var boundary = ids["node:boundary:2:"];
        Assert.AreSame(Mesh(before, boundary), Mesh(held, boundary));
        Assert.AreSame(Mesh(held, boundary), Mesh(after, boundary));
        Assert.IsFalse(after.Update.Upserts.Any(item => item.Id == boundary));
        var terrain = ids.Where(pair => TerrainRoot.IsMatch(pair.Key)).Select(pair => pair.Value)
            .Where(id => Mesh(before, id) is not null && Mesh(held, id) is not null && Mesh(after, id) is not null).ToArray();
        var affected = terrain.Where(id => !ReferenceEquals(Mesh(held, id), Mesh(after, id))).ToArray();
        var untouched = terrain.Where(id => ReferenceEquals(Mesh(before, id), Mesh(after, id))).ToArray();
        Assert.IsTrue(affected.Length > 0, "The new bed must rebuild a displayed chunk.");
        Assert.IsTrue(untouched.Length > 0, "Chunks outside the new bed keep their mesh.");
        foreach (var id in affected)
        {
            Assert.AreSame(Mesh(before, id), Mesh(held, id), "The previous chunk stays until its replacement connects.");
            Assert.IsTrue(after.Update.Upserts.Any(item => item.Id == id));
        }
        foreach (var id in untouched) Assert.IsFalse(after.Update.Upserts.Any(item => item.Id == id));
        Assert.IsTrue(affected.Any(id => after.Objects.Single(item => item.Id == id).Visuals.OfType<SceneVisual.Geometry>()
            .Any(visual => visual.Mesh.Material is SceneMaterial.Voxel material && material.Texture == new AssetRevision("test:ballast"))),
            "The new entry route has ballast.");
        var changedRails = ids.Where(pair =>
        {
            var earlier = Rail(before, pair.Value);
            var later = Rail(after, pair.Value);
            return earlier is not null && later is not null && !ReferenceEquals(earlier, later);
        }).Select(pair => pair.Value).ToArray();
        Assert.IsTrue(changedRails.Length > 0, "A changed entry track count must rebuild a displayed rail.");
        foreach (var id in changedRails)
        {
            Assert.IsTrue(held.Objects.Any(item => item.Id == id), "The previous rail stays in the frame until its replacement connects.");
            Assert.AreSame(Rail(before, id), Rail(held, id));
        }
        Assert.AreEqual(before.NodePanBounds, settled.NodePanBounds);
        Assert.IsFalse(after.Diagnostics.Any(problem => problem.Code == "railway_update_pending"));
    }

    private static readonly System.Text.RegularExpressions.Regex TerrainRoot = new("^node:terrain:-?\\d+:-?\\d+:$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static SceneMesh? Mesh(SceneFrame frame, SceneObjectId id) =>
        frame.Objects.FirstOrDefault(item => item.Id == id)?.Visuals.OfType<SceneVisual.Geometry>().Select(visual => visual.Mesh).FirstOrDefault();
    private static SceneMesh? Rail(SceneFrame frame, SceneObjectId id) =>
        frame.Objects.FirstOrDefault(item => item.Id == id)?.Visuals.OfType<SceneVisual.Geometry>()
            .Select(visual => visual.Mesh).FirstOrDefault(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel });

    private static async Task<SceneFrame> Settle(NodeScene scene, SceneWorldSnapshot snapshot, ScenePoint? focus = null)
    {
        if (scene.BuildWork is not null) await scene.BuildWork;
        if (focus is { } point) scene.Pump(snapshot, point);
        SceneFrame? frame = null;
        SceneFrame? displayed = null;
        while (true)
        {
            var work = scene.PendingWork;
            await SceneFixtures.AwaitPendingWork(work);
            var next = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), displayed, default);
            var changed = frame is null || !frame.Objects.Select(item => item.Id).ToHashSet().SetEquals(next.Objects.Select(item => item.Id));
            frame = next;
            if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) displayed = frame;
            if (work.Length == 0 && scene.PendingWork.Length == 0 && !changed && frame.Readiness is not SceneReadiness.Loading) break;
        }
        return frame;
    }

    private static SceneWorldSnapshot Small(TrainView train, ulong steps)
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [train], steps: steps,
            content: NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with { Position = new(4, 0) });
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[2] with { Position = new(0, -4) });
        snapshot = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with { LengthMeters = 4 });
        return RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[1] with { LengthMeters = 4 });
    }

    private static async Task AwaitExpectedBuildFailure(Task? work)
    {
        if (work is null) return;
        try { await work; }
        catch (RailwayBuildException) { }
    }

    private static void AssertFailed(SceneFrame frame)
    {
        Assert.AreEqual(SceneReadiness.Failed, frame.Readiness);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "build_failed"));
        Assert.IsFalse(frame.Diagnostics.Any(problem => problem.Code == "scene_internal_fault"));
    }
}

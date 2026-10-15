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
    public async Task Streaming_rail_parts_match_row_order_build()
    {
        var snapshot = RailSnapshot(false);
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var reference = prepared.Rails.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value.Build());
        Assert.IsTrue(reference.Count > 0);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles);
        var collected = new Dictionary<string, NodeScene.Part>();
        foreach (var segment in prepared.Rails.Values.OrderBy(segment => segment.Key, StringComparer.Ordinal))
        {
            var center = (segment.Bounds.Minimum + segment.Bounds.Maximum) / 2;
            await Settle(scene, new(center.X, center.Y, center.Z));
            foreach (var part in scene.RetainedRails) collected[part.Key] = part.Value;
        }
        CollectionAssert.AreEquivalent(reference.Keys.ToArray(), collected.Keys.ToArray());
        foreach (var (key, part) in collected)
        {
            var expected = reference[key];
            Assert.AreEqual(expected.Inputs, part.Inputs);
            Assert.AreEqual(expected.Origin, part.Origin);
            CollectionAssert.AreEqual(expected.Geometry.Geometry.Objects.Select(item => item.Pose).ToArray(),
                part.Geometry.Geometry.Objects.Select(item => item.Pose).ToArray());
            var expectedMeshes = expected.Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().ToArray();
            var actualMeshes = part.Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().ToArray();
            Assert.AreEqual(expectedMeshes.Length, actualMeshes.Length);
            Assert.IsTrue(actualMeshes.Length > 0);
            for (var index = 0; index < expectedMeshes.Length; index++)
            {
                Assert.AreEqual(expectedMeshes[index].Mesh.Material, actualMeshes[index].Mesh.Material);
                CollectionAssert.AreEqual(expectedMeshes[index].Mesh.Vertices.ToArray(), actualMeshes[index].Mesh.Vertices.ToArray());
                CollectionAssert.AreEqual(expectedMeshes[index].Mesh.TriangleIndices.ToArray(), actualMeshes[index].Mesh.TriangleIndices.ToArray());
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
            var prepared = NodeScene.Prepare(snapshot, scene.Layout!);
            var range = new NodeRange(new(i % 2 == 0 ? -288 : 288, 0, 0), NodeStreaming.PrefetchCells);
            var railBounds = prepared.Rails.Values.Where(segment => range.Intersects(segment.Bounds) && !scene.RetainedRails.ContainsKey(segment.Key))
                .Select(segment => segment.Bounds).ToArray();
            var railFog = railBounds.Length;
            foreach (var bounds in railBounds)
                Assert.AreEqual(railBounds.Count(item => item == bounds), frame.Update.Environment.FoggedRegions.Count(item => item == bounds));
            Assert.IsTrue(frame.Update.Environment.FoggedRegions.Length - railFog <= NodeStreaming.MaximumTerrainFogRegions);
            Assert.IsTrue(frame.Update.Environment.FoggedRegions.Length >= railFog);
            await Task.WhenAll(scene.PendingWork);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(20)]
    public void Preparation_defers_all_rail_routes_into_bounded_arc_segments(int stationHeight)
    {
        var template = NodeFixtures.Layout;
        var plot = template.Plots[0];
        template = template with { Plots = template.Plots.SetItem(0, plot with { At = plot.At with { Y = stationHeight } }) };
        var snapshot = NodeFixtures.Snapshot(content: NodeFixtures.Content(
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }, layout: template));
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        foreach (var prefix in new[] { "entry:", "station-tracks:", "parking-tracks:" })
        {
            Assert.IsFalse(prepared.Global.Parts.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal)));
            Assert.IsTrue(prepared.Rails.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal)), prefix);
        }
        var layout = prepared.Global.Layout;
        var expectedRoutes = new Dictionary<string, double>();
        foreach (var entry in layout.Entries)
        {
            var tracks = snapshot.Core.World.Edges.Single(edge => edge.Id == entry.Edge).Tracks;
            foreach (var (route, number) in layout.EntryRoutes(entry, tracks).Select((route, number) => (route, number)))
                expectedRoutes.Add($"entry:{entry.Edge.Value}:{number}", route.Length);
        }
        foreach (var (route, number) in layout.StationRoutes(true).Select((route, number) => (route, number)))
            expectedRoutes.Add($"station-tracks:{number}", route.Length);
        foreach (var (line, number) in layout.Parking.Select((line, number) => (line, number)))
            expectedRoutes.Add($"parking-tracks:{number}", line.Length);
        var actualRoutes = prepared.Rails.Values.GroupBy(segment => segment.Key[..segment.Key.LastIndexOf(':')]).ToArray();
        CollectionAssert.AreEquivalent(expectedRoutes.Keys.ToArray(), actualRoutes.Select(group => group.Key).ToArray());
        foreach (var route in actualRoutes)
        {
            var segments = route.OrderBy(segment => segment.Begin).ToArray();
            Assert.AreEqual(0.0, segments[0].Begin);
            Assert.AreEqual(expectedRoutes[route.Key], segments[^1].End);
            CollectionAssert.AreEqual(Enumerable.Range(0, segments.Length).ToArray(),
                segments.Select(segment => int.Parse(segment.Key[(segment.Key.LastIndexOf(':') + 1)..])).ToArray());
            for (var index = 1; index < segments.Length; index++) Assert.AreEqual(segments[index - 1].End, segments[index].Begin);
        }
        foreach (var (key, segment) in prepared.Rails)
        {
            Assert.AreEqual(key, segment.Key);
            Assert.IsTrue(segment.End > segment.Begin && segment.End - segment.Begin <= RailwayCoordinates.SegmentLengthCells);
            var part = segment.Build();
            var vertices = part.Geometry.Geometry.Objects.SelectMany(item => item.Visuals)
                .OfType<SceneVisual.Geometry>().SelectMany(visual => visual.Mesh.Vertices).Select(vertex => vertex.Position).ToArray();
            Assert.IsTrue(vertices.Length > 0);
            foreach (var vertex in vertices)
                Assert.IsTrue(vertex.X >= segment.Bounds.Minimum.X && vertex.X <= segment.Bounds.Maximum.X &&
                    vertex.Y >= segment.Bounds.Minimum.Y && vertex.Y <= segment.Bounds.Maximum.Y &&
                    vertex.Z >= segment.Bounds.Minimum.Z && vertex.Z <= segment.Bounds.Maximum.Z, key);
            Assert.AreSame(part, segment.Build(part));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Adjacent_rail_segments_share_the_same_rail_cross_section(bool entries)
    {
        var snapshot = entries ? NodeFixtures.Snapshot(content: NodeFixtures.Content()) : RailSnapshot();
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var compared = 0;
        foreach (var route in prepared.Rails.Values.Where(segment => !entries || segment.Key.StartsWith("entry:", StringComparison.Ordinal)).GroupBy(segment => segment.Key[..segment.Key.LastIndexOf(':')]))
        {
            var segments = route.OrderBy(segment => segment.Begin).ToArray();
            for (var index = 1; index < segments.Length; index++)
            {
                var before = segments[index - 1];
                var after = segments[index];
                Assert.AreEqual(before.End, after.Begin);
                var joint = after.Sample(after.Begin);
                var first = Section(before, joint);
                var second = Section(after, joint);
                Assert.AreEqual(first.Length, second.Length);
                foreach (var point in first) Assert.IsTrue(second.Any(other => Vector3.Distance(point, other) < .001f));
                compared++;
            }
        }
        Assert.IsTrue(compared > 0);

        static Vector3[] Section(NodeScene.RailSegment segment, RailwaySample joint)
        {
            var position = RailwayCoordinates.Relative(joint.Position, default);
            var vertices = segment.Build().Geometry.Geometry.Objects.SelectMany(item => item.Visuals)
                .OfType<SceneVisual.Geometry>().Where(visual => visual.Mesh.Material == new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel))
                .SelectMany(visual => visual.Mesh.Vertices).Select(vertex => vertex.Position)
                .Where(vertex => Math.Abs(Vector3.Dot(vertex - position, joint.Forward)) < .001f).Distinct().ToArray();
            Assert.IsTrue(vertices.Length > 0);
            return vertices;
        }
    }

    [TestMethod]
    public async Task Moving_focus_unloads_rail_segments_and_reopen_reuses_retained_cache()
    {
        var snapshot = RailSnapshot();
        var cache = new NodeScene.Cache();
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        ImmutableDictionary<string, NodeScene.Part> retained;
        var line = prepared.Global.Layout.Parking.First();
        var currentFocus = RailwayCoordinates.Add(line.Start, line.Forward * 64);
        var started = new List<(ScenePoint Focus, string Key)>();
        await using var vehicles = new SceneVehicles();
        await using (var scene = Open(snapshot, vehicles, cache, buildRail: (_, segment, old, token) =>
        {
            started.Add((currentFocus, segment.Key));
            return segment.Build(old, token);
        }))
        {
            await Settle(scene, currentFocus);
            AssertStartedWithinPrefetch(currentFocus);
            var before = scene.RetainedRails;
            Assert.IsTrue(before.Count > 0);
            var oldFrame = Present(scene, snapshot);
            var focus = RailwayCoordinates.Add(line.Start, line.Forward * (float)(line.Length - 64));
            currentFocus = focus;
            await Settle(scene, focus);
            AssertStartedWithinPrefetch(focus);
            retained = scene.RetainedRails;
            Assert.IsTrue(retained.Count > 0);
            var retention = new NodeRange(focus, NodeStreaming.RetainCells);
            Assert.IsTrue(before.Keys.Any(key => !retention.Intersects(prepared.Rails[key].Bounds)));
            Assert.IsTrue(retained.Keys.All(key => retention.Intersects(prepared.Rails[key].Bounds)));
            CollectionAssert.AreEquivalent(retained.Keys.ToArray(), cache.Read(NodeFixtures.NodeId)!.Parts.Keys
                .Where(prepared.Rails.ContainsKey).ToArray());
            Assert.IsTrue(Present(scene, snapshot, oldFrame).Update.Removed.Length > 0);
        }
        await using var reopened = Open(snapshot, vehicles, cache);
        await reopened.BuildWork!;
        reopened.Pump(focus: currentFocus);
        foreach (var (key, part) in retained) Assert.AreSame(part, reopened.RetainedRails[key]);

        void AssertStartedWithinPrefetch(ScenePoint focus)
        {
            var prefetch = new NodeRange(focus, NodeStreaming.PrefetchCells);
            var keys = started.Where(item => item.Focus == focus).Select(item => item.Key).ToHashSet();
            Assert.IsTrue(keys.Count > 0);
            Assert.IsTrue(keys.All(key => prefetch.Intersects(prepared.Rails[key].Bounds)));
            var outside = prepared.Rails.Values.Where(segment => !prefetch.Intersects(segment.Bounds)).ToArray();
            Assert.IsTrue(outside.Length > 0);
            Assert.IsTrue(outside.All(segment => !keys.Contains(segment.Key)));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancelled_rail_work_cannot_attach_or_publish_cache(bool close)
    {
        var snapshot = RailSnapshot();
        var cache = new NodeScene.Cache();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<NodeScene.RailSegment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeBuilds = 0;
        var concurrentBuilds = 0;
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, cache, build: (data, from, old, token) =>
        {
            if (Interlocked.Increment(ref activeBuilds) != 1) Interlocked.Exchange(ref concurrentBuilds, 1);
            try { return SmallPart(data, from, old, token); }
            finally { Interlocked.Decrement(ref activeBuilds); }
        }, buildRail: (data, segment, old, token) =>
        {
            if (Interlocked.Increment(ref activeBuilds) != 1) Interlocked.Exchange(ref concurrentBuilds, 1);
            try
            {
                entered.TrySetResult(segment);
                release.Wait();
                return segment.Build(old);
            }
            finally { Interlocked.Decrement(ref activeBuilds); }
        });
        Task? closing = null;
        try
        {
            await scene.BuildWork!;
            var layout = scene.Layout!;
            var line = layout.Parking.First();
            var focus = RailwayCoordinates.Add(line.Start, line.Forward * 64);
            for (var attempt = 0; !entered.Task.IsCompleted; attempt++)
            {
                Assert.IsTrue(attempt < 1000, "Rail work did not start within the finite node work set.");
                scene.Pump(focus: focus);
                Assert.IsTrue(entered.Task.IsCompleted || scene.PendingWork.Length > 0, "No work remains, but no rail builder started.");
                if (!entered.Task.IsCompleted) await Task.WhenAny(Task.WhenAll(scene.PendingWork), entered.Task);
            }
            var blocked = await entered.Task;
            var pending = scene.PendingWork;
            try
            {
                Assert.AreEqual(0, concurrentBuilds, "Terrain and rail builders must not run concurrently.");
                Assert.AreEqual(1, pending.Length, "Terrain and rails share one serial build queue.");
                if (close) { closing = scene.DisposeAsync().AsTask(); Assert.IsFalse(closing.IsCompleted); }
                else scene.Pump(focus: new(10000, 0, 10000));
            }
            finally { release.Set(); }
            await Task.WhenAll(pending);
            if (closing is not null) await closing;
            else scene.Pump(focus: new(10000, 0, 10000));
            Assert.IsFalse(scene.RetainedRails.ContainsKey(blocked.Key));
            Assert.IsFalse(cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(blocked.Key));
        }
        finally
        {
            release.Set();
            if (closing is null) await scene.DisposeAsync();
            else await closing;
        }
    }

    private static SceneWorldSnapshot RailSnapshot(bool large = true)
    {
        var terrainSnapshot = Snapshot(true);
        var source = NodeFixtures.Content();
        var layout = NodeFixtures.Layout with
        {
            SceneModel = NodeFixtures.Layout.SceneModel with
            {
                Scene = SceneFixtures.Projection([]) with { OccupiedBounds = large ? new(new(-320, 0, -320), new(319, 0, 319)) : null }
            }
        };
        return new(terrainSnapshot.Core, new FixedContentView
        {
            Generation = source.Generation, Core = source.Core, Voxels = source.Voxels,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [NodeFixtures.StationDesign] = source.FindDesign(NodeFixtures.StationDesign) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = source.FindVoxelAppearance(SceneFixtures.Voxel) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = source.FindTerrain(RailwayFixtures.Terrain) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = source.FindRailwayAppearance(RailwayFixtures.Rule) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(layout) }
        });
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
        Func<NodeScene.Preparation, GridColumn, NodeScene.Part?, CancellationToken, NodeScene.Part>? build = null,
        Func<NodeScene.Preparation, NodeScene.RailSegment, NodeScene.Part?, CancellationToken, NodeScene.Part>? buildRail = null)
    {
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        return new(snapshot, NodeFixtures.NodeId, cache ?? new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
            return id;
        }, vehicles, buildChunk: build ?? SmallPart, buildRail: buildRail);
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

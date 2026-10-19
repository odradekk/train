using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
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
    public async Task Dispatch_prioritizes_the_chunk_crossed_between_samples_at_a_corner()
    {
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(Snapshot(true), vehicles, build: (data, from, old, token) =>
        {
            started.Enqueue(from);
            if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            release.Wait(token);
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: default(ScenePoint), following: new(default, [new(-1, 200, 2), new(2, 200, -1)], []));
            await filled.Task;
            CollectionAssert.AreEquivalent(new[] { new GridColumn(-32, 0), new GridColumn(0, 0) }, started.ToArray(),
                "The path clips the corner of (0,0) between its samples; it must precede the lateral (-32,-32) chunk and later crossings.");
            scene.SetBuildAllowed(false);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Dispatch_starts_the_earliest_path_crossing_before_later_zero_distance_parts()
    {
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(Snapshot(true), vehicles, build: (data, from, old, token) =>
        {
            started.Enqueue(from);
            if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            release.Wait(token);
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: default(ScenePoint), following: new(default, [new(16, 200, 0), new(-16, 200, 0)], []));
            await filled.Task;
            CollectionAssert.AreEquivalent(new[] { new GridColumn(0, -32), new GridColumn(0, 0) }, started.ToArray(),
                "Both build slots must serve the first path point before the lexically earlier parts at the later point.");
            scene.SetBuildAllowed(false);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Dispatch_uses_the_path_when_following_and_the_focus_otherwise(bool following)
    {
        var snapshot = Snapshot(true);
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, build: (data, from, old, token) =>
        {
            started.Enqueue(from);
            if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            release.Wait(token);
            return SmallPart(data, from, old, token);
        });
        ScenePoint[] path = [new(48, 200, 0)];
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: default(ScenePoint), following: following ? new(default, path, []) : null);
            await filled.Task;
            var expected = following
                ? new[] { new GridColumn(32, -32), new GridColumn(32, 0) }
                : new[] { new GridColumn(-32, -32), new GridColumn(-32, 0) };
            CollectionAssert.AreEquivalent(expected, started.ToArray());
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, started.Count);
            if (following) Assert.IsTrue(started.All(from => from.X == 32),
                "Path parts ahead must start before side parts nearer the vehicle focus, ignoring sample height.");
            scene.SetBuildAllowed(false);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Loading_dispatch_finishes_the_near_circle_before_the_far_followed_path()
    {
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(Snapshot(true), vehicles, build: (data, from, old, token) =>
        {
            started.Enqueue(from);
            if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            release.Wait(token);
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: default(ScenePoint), following: new(default, [new(160, 0, 0)], []));
            await filled.Task;
            CollectionAssert.AreEquivalent(new[] { new GridColumn(64, -32), new GridColumn(64, 0) }, started.ToArray(),
                "While Loading, both slots must serve the 32 m near circle before parts at the far path point.");
            scene.SetBuildAllowed(false);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Ahead_circle_builds_and_retains_parts_beyond_the_train_retention_circle()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        var ahead = new ScenePoint(288, 0, 0);
        var started = new ConcurrentQueue<GridColumn>();
        HashSet<GridColumn>? expected = null;
        var completed = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache, build: (data, from, old, token) =>
        {
            if (expected is not null)
            {
                started.Enqueue(from);
                if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) blocked.TrySetResult();
                release.Wait(token);
            }
            var geometry = WorldSceneSession.BuildGeometry(id =>
                new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], []));
            var part = new NodeScene.Part(data.ChunkInputs(from), geometry,
                ImmutableDictionary<GridColumn, double>.Empty.Add(from, 1));
            if (expected is not null && Interlocked.Increment(ref completed) == expected.Count) allCompleted.TrySetResult();
            return part;
        });
        try
        {
            await Settle(scene, snapshot, default);
            var original = Chunks(cache).Keys.ToHashSet();
            expected = new NodeRange(ahead, NodeStreaming.PrefetchCells).Chunks(scene.Layout!)
                .Where(from => !original.Contains(from)).ToHashSet();
            Assert.IsTrue(expected.Any(from => !new NodeRange(default, NodeStreaming.RetainCells).Intersects(from)));
            scene.Pump(focus: default(ScenePoint), following: new(ahead, [], []));
            await blocked.Task;
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, started.Count,
                "Predictive work must keep the existing bounded build slots.");
            release.Set();
            await allCompleted.Task;
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            scene.Pump(focus: default(ScenePoint), following: new(ahead, [], []));
            scene.Pump(focus: default(ScenePoint), following: new(ahead, [], []));

            CollectionAssert.AreEquivalent(expected.ToArray(), started.ToArray());
            var retained = Chunks(cache);
            Assert.IsTrue(expected.All(retained.ContainsKey),
                "Completed predictive parts must survive both queued-result and admitted-part retirement.");
            Assert.IsTrue(original.All(retained.ContainsKey), "The original prefetch circle remains covered.");
            Assert.IsTrue(retained.Keys.All(from => new NodeRange(default, NodeStreaming.RetainCells).Intersects(from) ||
                new NodeRange(ahead, NodeStreaming.PrefetchCells).Intersects(from)));
        }
        finally { release.Set(); }
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    public async Task Cached_parts_match_an_independent_cold_build_after_rebinding(bool changeTerrain, bool changeGeneration, bool changeAppearance)
    {
        var original = Snapshot(false);
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        ImmutableDictionary<string, NodeScene.Part> before;
        await using (var first = Open(original, vehicles, cache, (data, from, old, token) => data.BuildChunk(from, old, token)))
        {
            await Settle(first, original, default);
            before = cache.Read(NodeFixtures.NodeId)!.Parts;
        }
        var input = Snapshot(false, relief: changeTerrain ? 2 : 0);
        if (changeGeneration)
        {
            var content = (FixedContentView)input.Content;
            input = input with { Content = new FixedContentView
            {
                Generation = new(2), Core = content.Core, Voxels = content.Voxels,
                Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                { [SceneFixtures.Design] = content.FindDesign(SceneFixtures.Design) },
                VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
                { [SceneFixtures.Voxel] = changeAppearance ? new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
                    { Presentation = ((VoxelPresentation.Cube)SceneFixtures.Cube.Presentation) with { Top = new("changed-top") } }) :
                    content.FindVoxelAppearance(SceneFixtures.Voxel) },
                Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
                { [RailwayFixtures.Terrain] = content.FindTerrain(RailwayFixtures.Terrain) },
                RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
                { [RailwayFixtures.Rule] = content.FindRailwayAppearance(RailwayFixtures.Rule) },
                SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
                { [new(1)] = content.FindSettlementLayout(new(1)) }
            } };
        }
        await using var warm = Open(input, vehicles, cache, (data, from, old, token) => data.BuildChunk(from, old, token));
        await Settle(warm, input, default);
        var reused = cache.Read(NodeFixtures.NodeId)!.Parts;
        var coldCache = new NodeScene.Cache();
        await using var cold = Open(input, vehicles, coldCache, (data, from, old, token) => data.BuildChunk(from, old, token));
        await Settle(cold, input, default);
        var expected = coldCache.Read(NodeFixtures.NodeId)!.Parts;
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), reused.Keys.ToArray());
        foreach (var (key, part) in reused)
        {
            NodeFixtures.AssertPartEqual(expected[key], part);
            if (!changeTerrain && !changeAppearance) Assert.AreSame(before[key], part, "Generation alone does not invalidate unchanged values.");
        }
        if (changeTerrain || changeAppearance) Assert.IsTrue(reused.Any(pair => before[pair.Key].Inputs != pair.Value.Inputs));
    }

    [TestMethod]
    public void Rebound_turnaround_mesh_matches_a_cold_build_after_outer_tracks_switch_ends()
    {
        var original = WithOuterTracks(new(1));
        var changed = WithOuterTracks(new(2));
        var before = NodeScene.Prepare(original, NodeLayout.Build(original, NodeFixtures.NodeId));
        var rebound = NodeScene.Prepare(changed, NodeLayout.Build(changed, NodeFixtures.NodeId));
        var moved = rebound.Rails.Values.FirstOrDefault(segment =>
            segment.Key.StartsWith("turnaround:", StringComparison.Ordinal) &&
            before.Rails.TryGetValue(segment.Key, out var previous) &&
            segment.Begin == previous.Begin && segment.End == previous.End &&
            segment.Sample(segment.Begin).Position != previous.Sample(previous.Begin).Position);
        Assert.IsNotNull(moved, "Switching the built outer tracks must change geometry under a retained turnaround key.");
        var old = before.Rails[moved.Key].Build();
        var reused = moved.Build(old);
        var expected = moved.Build();
        NodeFixtures.AssertPartEqual(expected, reused);

        static SceneWorldSnapshot WithOuterTracks(EdgeId built)
        {
            var snapshot = NodeFixtures.Snapshot(platforms: 0, platformLengthMeters: 0, tracks: 1, trackLimit: 2, trains: [],
                content: NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
                    layout: NodeFixtures.Layout with { Plots = [] }));
            var edge = snapshot.Core.World.Edges.Single(item => item.Id == built);
            return RailwayFixtures.ReplaceCore(snapshot, edge: edge with { Tracks = 2 });
        }
    }

    [TestMethod]
    public async Task Pausing_a_candidate_allows_inflight_parts_to_finish_without_refilling_slots()
    {
        var snapshot = Snapshot(true);
        var started = 0;
        using var release = new ManualResetEventSlim();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, build: (data, from, old, token) =>
        {
            if (Interlocked.Increment(ref started) == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            release.Wait();
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump();
            await filled.Task;
            scene.SetBuildAllowed(false);
            release.Set();
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            scene.Pump(allowBuild: false);
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, started);
            Assert.IsTrue(scene.HasPendingBuilds, "Unstarted nearby parts still give the current scene priority.");
            Assert.AreEqual(0, scene.PendingWork.Length);
        }
        finally { release.Set(); await scene.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Background_layout_reads_do_not_run_on_the_pump_thread()
    {
        using var content = new SceneFixtures.BlockedAppearanceView(NodeFixtures.Content(), blockRailway: true);
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], content: content);
        var unblock = Task.Run(async () => { await content.Entered.Task; content.Release.Set(); });
        await using var vehicles = new SceneVehicles();
        // A pool caller could be reused by the background task after this test awaits.
        var caller = await Task.Factory.StartNew(() =>
        {
            var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), SceneFixtures.NextId(),
                vehicles, new(), _ => null, RailwayFixtures.EmptyNode, background: true);
            scene.Pump(allowBuild: false);
            return (Scene: scene, Thread: Environment.CurrentManagedThreadId);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await using var scene = caller.Scene;
        await unblock;
        Assert.AreNotEqual(caller.Thread, content.ReadThread);
        await scene.BuildWork!;
        scene.Pump(allowBuild: false);
        Assert.IsNotNull(scene.Layout);
        Assert.AreEqual(NodeLayout.Build(snapshot, NodeFixtures.NodeId).PanBounds(default), scene.Layout.PanBounds(default));
    }

    [TestMethod]
    public async Task Completed_builders_refill_slots_without_another_pump()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replenished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new ManualResetEventSlim();
        using var others = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, cache, (data, from, old, _) =>
        {
            started.Enqueue(from);
            if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            if (started.Count > RailwayCoordinates.MaxConcurrentBuilds) replenished.TrySetResult();
            (from == new GridColumn(0, 0) ? first : others).Wait();
            return SmallPart(data, from, old, default);
        });
        Task? closing = null;
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: new(16, 0, 16));
            await filled.Task;
            first.Set();
            await replenished.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            CollectionAssert.AreEquivalent(new[] { new GridColumn(0, 0), new(-32, 0), new(0, -32) }, started.ToArray());
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, scene.PendingWork.Count(work => !work.IsCompleted));
            Assert.IsTrue(Chunks(cache).IsEmpty, "Background completion must not publish parts before Pump.");
            closing = scene.DisposeAsync().AsTask();
            Assert.IsFalse(closing.IsCompleted, "Closing must wait for builders started outside Pump.");
        }
        finally
        {
            first.Set(); others.Set();
            await (closing ?? scene.DisposeAsync().AsTask());
        }
        Assert.AreEqual(3, started.Count, "Closing must stop automatic dispatch.");
    }

    [TestMethod]
    public async Task Parts_fill_available_slots_in_priority_order_and_commit_without_waiting_for_the_batch()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replenished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        using var others = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache, (data, from, old, token) =>
        {
            started.Enqueue(from);
            if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            if (started.Count > RailwayCoordinates.MaxConcurrentBuilds) replenished.TrySetResult();
            (from == new GridColumn(0, 0) ? first : from == new GridColumn(-32, 0) ? second : others).Wait(token);
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: new(16, 0, 16));
            var pending = scene.PendingWork;
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, pending.Length);
            await filled.Task;
            CollectionAssert.AreEquivalent(new[] { new GridColumn(0, 0), new(-32, 0) }, started.ToArray());
            scene.Pump();
            Assert.AreEqual(pending.Length, started.Count, "Pumping a full queue must not start duplicate work.");

            second.Set();
            await Task.WhenAny(pending);
            scene.Pump();
            await replenished.Task;
            Assert.IsTrue(Chunks(cache).ContainsKey(new(-32, 0)), "A completed part must commit while the nearest part is blocked.");
            Assert.IsFalse(Chunks(cache).ContainsKey(new(0, 0)));
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, scene.PendingWork.Count(work => !work.IsCompleted));
        }
        finally { first.Set(); second.Set(); others.Set(); }
        await Settle(scene, snapshot, new(16, 0, 16));
        var prepared = NodeScene.Prepare(snapshot, scene.Layout!);
        CollectionAssert.AreEquivalent(new NodeRange(new(16, 0, 16), NodeStreaming.PrefetchCells).Chunks(scene.Layout!).ToArray(), Chunks(cache).Keys.ToArray());
        foreach (var (from, part) in Chunks(cache)) NodeFixtures.AssertPartEqual(SmallPart(prepared, from, null, default), part);
    }

    [TestMethod]
    public async Task Cancelled_builders_keep_their_slots_until_they_exit()
    {
        var snapshot = Snapshot(true);
        var started = 0;
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replenished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var newRange = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        var cache = new NodeScene.Cache();
        await using var scene = Open(snapshot, vehicles, cache, (data, from, old, _) =>
        {
            var count = Interlocked.Increment(ref started);
            if (count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
            if (count == RailwayCoordinates.MaxConcurrentBuilds * 2) replenished.TrySetResult();
            (count <= RailwayCoordinates.MaxConcurrentBuilds ? release : newRange).Wait();
            return SmallPart(data, from, old, default);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: new(-288, 0, -288));
            await filled.Task;
            var retiring = scene.PendingWork;
            scene.Pump(focus: new(288, 0, 288));
            scene.Pump();
            CollectionAssert.AreEquivalent(retiring, scene.PendingWork);
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, Volatile.Read(ref started));
            release.Set();
            await SceneFixtures.AwaitPendingWork(retiring);
            await replenished.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            scene.Pump();
            Assert.IsTrue(Chunks(cache).IsEmpty, "Cancelled results must not commit even when their builders ignore cancellation.");
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, scene.PendingWork.Count(work => !work.IsCompleted));
            newRange.Set();
            await Settle(scene, snapshot, new(288, 0, 288));
            Assert.IsTrue(Chunks(cache).Keys.All(new NodeRange(new(288, 0, 288), NodeStreaming.PrefetchCells).Intersects));
        }
        finally { release.Set(); newRange.Set(); }
    }

    [TestMethod]
    public async Task Near_parts_are_interactive_while_far_work_is_blocked()
    {
        var snapshot = Snapshot(true, nearVehicle: true, overviewPlots: true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles();
        var instance = ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        vehicles.Prepare(snapshot.Content, instance);
        await SceneFixtures.AwaitPendingWork(vehicles.PendingWork);
        var cache = new NodeScene.Cache();
        await using var scene = Open(snapshot, vehicles, cache, build: (data, from, old, token) =>
        {
            if (!new NodeRange(default, NodeStreaming.NearCells).Intersects(from))
            { entered.TrySetResult(); release.Wait(token); }
            return SmallPart(data, from, old, token);
        });
        try
        {
            await scene.BuildWork!;
            scene.Pump();
            var near = new NodeRange(default, NodeStreaming.NearCells).Chunks(scene.Layout!).ToArray();
            while (!near.All(Chunks(cache).ContainsKey))
            {
                scene.Pump();
                if (!near.All(Chunks(cache).ContainsKey)) await Task.WhenAny(scene.PendingWork);
            }
            await entered.Task;
            var frame = Present(scene, snapshot);
            Assert.AreEqual(SceneReadiness.Partial, frame.Readiness);
            Assert.IsNotNull(frame.NodeOverview);
            var distantPlot = frame.NodeOverview.Plots.Single();
            Assert.AreEqual(1, distantPlot.TemplatePlotNumber);
            Assert.IsTrue(distantPlot.Corners.All(point => point.X > NodeStreaming.NearCells && point.Y > NodeStreaming.NearCells));
            var hit = (ScenePick.Hit)frame.Pick(Ray(Chunks(cache).Keys.First()));
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
        var cache = new NodeScene.Cache();
        await using var scene = Open(snapshot, vehicles, cache);
        await Settle(scene, snapshot, default);
        var loaded = Chunks(cache).Keys.First();
        var frame = Present(scene, snapshot);
        Assert.IsInstanceOfType<ScenePick.Hit>(frame.Pick(Ray(loaded)));
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(Ray(new(288, 288))));
        await Settle(scene, snapshot, new(288, 0, 288));
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
        await Settle(scene, snapshot, default);
        var before = Chunks(cache);
        var focus = new ScenePoint(288, 0, 288);
        await Settle(scene, snapshot, focus);
        var retention = new NodeRange(focus, NodeStreaming.RetainCells);
        Assert.IsTrue(before.Keys.Any(from => !retention.Intersects(from)));
        Assert.IsTrue(Chunks(cache).Keys.All(retention.Intersects));
    }

    [TestMethod]
    public async Task Streaming_parts_match_one_shot_build()
    {
        var snapshot = Snapshot(false);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var reference = NodeFixtures.Build(snapshot, layout);
        await using var vehicles = new SceneVehicles();
        var cache = new NodeScene.Cache();
        await using var scene = Open(snapshot, vehicles, cache, build: (data, from, old, token) => data.BuildChunk(from, old, token));
        var collected = new Dictionary<GridColumn, NodeScene.Part>();
        foreach (var focus in new[] { new ScenePoint(-16, 0, -16), new ScenePoint(16, 0, 16) })
        {
            await Settle(scene, snapshot, focus);
            foreach (var part in Chunks(cache)) collected[part.Key] = part.Value;
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
        var cache = new NodeScene.Cache();
        await using var scene = Open(snapshot, vehicles, cache);
        var collected = new Dictionary<string, NodeScene.Part>();
        foreach (var segment in prepared.Rails.Values.OrderBy(segment => segment.Key, StringComparer.Ordinal))
        {
            var center = (segment.Bounds.Minimum + segment.Bounds.Maximum) / 2;
            await Settle(scene, snapshot, new(center.X, center.Y, center.Z));
            foreach (var part in Rails(cache)) collected[part.Key] = part.Value;
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
        await SceneFixtures.AwaitPendingWork(pending);
        scene.Pump();
        Assert.IsFalse(Chunks(cache).ContainsKey(blocked));
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
            await Settle(scene, snapshot, default);
            await Settle(scene, snapshot, new(288, 0, 288));
            retained = Chunks(cache);
        }
        await using var reopened = Open(snapshot, vehicles, cache);
        await reopened.BuildWork!;
        reopened.Pump(focus: new(288, 0, 288));
        Assert.IsTrue(Chunks(cache).Keys.All(new NodeRange(new(288, 0, 288), NodeStreaming.NearCells).Intersects));
        await Settle(reopened, snapshot, new(288, 0, 288));
        Assert.AreEqual(retained.Count, Chunks(cache).Count);
        foreach (var (from, part) in retained) Assert.AreSame(part, Chunks(cache)[from]);
    }

    [TestMethod]
    public void Range_prioritizes_nearest_bounds_instead_of_their_centers()
    {
        var range = new NodeRange(default, 8);
        var longNear = new SceneBounds(new(4, 0, -1), new(200, 1, 1));
        var shortFar = new SceneBounds(new(12, 0, -1), new(14, 1, 1));
        Assert.IsTrue(range.DistanceSquared(longNear) < range.DistanceSquared(shortFar));
        Assert.IsTrue(range.Intersects(longNear));
        Assert.IsFalse(range.Intersects(shortFar));
    }

    [TestMethod]
    public async Task Reopen_rebuilds_retained_chunks_when_terrain_inputs_change()
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        ImmutableDictionary<GridColumn, NodeScene.Part> retained;
        await using (var scene = Open(snapshot, vehicles, cache))
        {
            await Settle(scene, snapshot, default);
            retained = Chunks(cache);
        }
        var changed = Snapshot(true, relief: 2);
        await using var reopened = Open(changed, vehicles, cache);
        await Settle(reopened, changed, default);
        var rebuilt = Chunks(cache);
        CollectionAssert.AreEquivalent(retained.Keys.ToArray(), rebuilt.Keys.ToArray());
        Assert.IsTrue(retained.Count > 0);
        foreach (var (from, old) in retained)
        {
            Assert.AreNotSame(old, rebuilt[from]);
            Assert.AreNotEqual(old.Inputs, rebuilt[from].Inputs);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(8)]
    [DataRow(64)]
    public async Task Present_moving_follow_prefetches_from_the_train_head_at_the_measured_display_rate(int rate)
    {
        var layout = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-640, 0, -640), new(639, 0, 639)) } } };
        var train = NodeFixtures.Train(vehicles: [new(1), new(2)], activity: TrainActivity.Running, speed: 10) with
        { LengthMeters = 40, Position = new TrainPosition.Node(NodeFixtures.NodeId, null, null, null, null, NodeTravelPhase.Approaching, 200, null) };
        SceneWorldSnapshot Snapshot(ulong steps) => NodeFixtures.EmptyNodeSnapshot(layout,
            [train with { Position = ((TrainPosition.Node)train.Position) with { DistanceMeters = steps == 0 ? 199 : 200 } }],
            design: RailwayFixtures.Vehicle(lengthCells: 40), steps: steps, travelMeters: 240);
        var initial = Snapshot(0);
        var current = Snapshot((ulong)rate * 60);
        long now = 0;
        var displayRate = new DisplayRate { Timestamp = () => now };
        var cache = new NodeScene.Cache();
        var started = new ConcurrentDictionary<GridColumn, byte>();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(initial, vehicles, cache, build: (data, from, old, token) =>
        {
            started[from] = 0;
            return SmallPart(data, from, old, token);
        }, displayRate: displayRate);
        await scene.BuildWork!;
        var target = new WorldSceneTarget.Follow(new(1), new(2));
        scene.Present(initial, Request, NodeFixtures.NextStamp(scene), null, default, target);
        now = Stopwatch.Frequency;
        SceneFrame? displayed = null;
        do
        {
            var frame = scene.Present(current, Request, NodeFixtures.NextStamp(scene), displayed, default, target);
            if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) displayed = frame;
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        } while (scene.HasPendingBuilds);
        // This straight node maps 200 m to Z-80 cells. Two 40-cell vehicles put the head at -40,
        // and the followed rear vehicle at -100. A 10 m/s train advances 10*rate cells in 0.5 s.
        var lead = Math.Min(10 * rate, 320);
        var ahead = new ScenePoint(0, 0, -40 + lead);
        var demanded = new NodeRange(ahead, 160).Chunks(scene.Layout!).ToArray();
        Assert.IsTrue(demanded.All(started.ContainsKey), "The entire predicted circle must be built from the train head.");
        Assert.IsFalse(started.Keys.Any(from => from.Z > ahead.Z + 160),
            "A unit or sign error must not move prefetch beyond the predicted circle.");
        Assert.IsFalse(started.ContainsKey(new(0, 448)), "The lead remains capped at 160 m even at 64x.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(8)]
    [DataRow(64)]
    public async Task Candidate_prefetch_starts_at_the_predicted_head_and_uses_the_shared_rate(int rate)
    {
        var layout = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-640, 0, -640), new(639, 0, 639)) } } };
        var train = RailwayFixtures.Train(0, direction: TravelDirection.Reverse, speed: 10) with { LengthMeters = 26 };
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [train], platformLengthMeters: 0,
            content: NodeFixtures.Content(layout: layout, design: RailwayFixtures.Vehicle(lengthCells: 52)));
        long now = 0;
        var rateWindow = new DisplayRate { Timestamp = () => now };
        rateWindow.Update(0);
        now = Stopwatch.Frequency;
        rateWindow.Update(rate);
        var started = new ConcurrentDictionary<GridColumn, byte>();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, build: (data, from, old, token) =>
        {
            started[from] = 0;
            return SmallPart(data, from, old, token);
        }, displayRate: rateWindow);
        await scene.BuildWork!;
        do
        {
            scene.Pump(snapshot, enteringTrain: train);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        } while (scene.HasPendingBuilds);

        var entry = scene.Layout!.EntryFor(new(1));
        var approach = scene.Layout.Approach(entry, scene.Layout.Bypass);
        var route = NodeRailPath.Join(approach.Inlet, approach.Branch);
        // With no platform, the complete inbound path maps to the core's 80 m approach.
        var mappedSpeed = route.Length / 80 * 10;
        var lead = Math.Min(mappedSpeed * rate * .5, 320);
        var predicted = new NodeRange(route.Sample(26 + lead).Position, 160);
        var boundary = new NodeRange(scene.Layout.TrainBoundary(entry).Position, 160);
        Assert.IsTrue(predicted.Chunks(scene.Layout).All(started.ContainsKey),
            "The first candidate must build the circle ahead of the predicted head, using the session rate.");
        Assert.IsTrue(started.Keys.All(from => boundary.Intersects(from) || predicted.Intersects(from)),
            "The candidate must retain the existing two circles and capped lead.");
    }

    [TestMethod]
    public async Task Present_follow_uses_vehicle_node_coordinates_with_nonzero_origin()
    {
        var snapshot = Snapshot(true, nearVehicle: true);
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache);
        await scene.BuildWork!;
        var origin = new ScenePoint(-288, 0, -288);
        SceneFrame? displayed = null;
        SceneFrame frame;
        int previousObjects;
        do
        {
            previousObjects = displayed?.Objects.Length ?? -1;
            frame = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), displayed, origin, new WorldSceneTarget.Follow(new(1), new(1)));
            if (frame.Readiness is not SceneReadiness.Loading and not SceneReadiness.Failed) displayed = frame;
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        } while (scene.PendingWork.Length > 0 || scene.HasPendingBuilds || frame.Objects.Length != previousObjects);
        var pose = ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))))).Value;
        var nodePosition = RailwayCoordinates.Add(origin, pose.Position);
        Assert.IsTrue(new NodeRange(nodePosition, NodeStreaming.PrefetchCells).Chunks(scene.Layout!).All(Chunks(cache).ContainsKey));
        Assert.IsFalse(Chunks(cache).ContainsKey(new(288, 288)), "The displayed vehicle position must not become a node-space focus.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("session")]
    [DataRow("request")]
    [DataRow("space")]
    public async Task Present_location_uses_camera_origin_and_focus_only_for_matching_stamp(string stalePart)
    {
        var snapshot = Snapshot(true);
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache);
        await scene.BuildWork!;
        var stamp = new SceneFrameStamp(new(Guid.Empty), new(1), new(1), 2);
        var stale = stalePart.Length > 0;
        var cameraStamp = stamp with { Revision = 1 };
        cameraStamp = stalePart switch
        {
            "session" => cameraStamp with { Session = new(Guid.NewGuid()) },
            "request" => cameraStamp with { Request = new(2) },
            "space" => cameraStamp with { Space = new(2) },
            _ => cameraStamp
        };
        var camera = new SceneFrame(cameraStamp, new(256, 0, 256), 0, SceneReadiness.Ready, [], [], [], []);
        var request = Request with { Camera = new(camera, new(32, 0, 32)) };
        var bounds = scene.Layout!.PanBounds(default);
        var focus = stale ? new ScenePoint((bounds.Minimum.X + bounds.Maximum.X) / 2, 0,
            (bounds.Minimum.Y + bounds.Maximum.Y) / 2) : new ScenePoint(288, 0, 288);
        var demanded = new NodeRange(focus, NodeStreaming.PrefetchCells).Chunks(scene.Layout!).ToArray();
        SceneFrame? displayed = null;
        var previousRemaining = int.MaxValue;
        int remaining;
        do
        {
            var pending = scene.PendingWork;
            var frame = scene.Present(snapshot, request, NodeFixtures.NextStamp(scene), displayed, default, new WorldSceneTarget.Location(NodeFixtures.Location));
            if (frame.Readiness is not SceneReadiness.Loading and not SceneReadiness.Failed) displayed = frame;
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            remaining = demanded.Count(from => !Chunks(cache).ContainsKey(from));
            Assert.IsTrue(remaining < previousRemaining || pending.Length > 0 || scene.HasPendingBuilds,
                "Displaying the current demand must admit geometry or await real build work.");
            previousRemaining = remaining;
        } while (remaining > 0 || scene.HasPendingBuilds);
        Assert.IsTrue(demanded.All(Chunks(cache).ContainsKey));
        Assert.IsFalse(Chunks(cache).ContainsKey(stale ? new(288, 288) : new(0, 0)));
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
        Assert.IsFalse(Chunks(cache).ContainsKey(blocked));
    }

    [TestMethod]
    public async Task Node_fog_regions_remain_bounded()
    {
        var layout = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-640, 0, -640), new(639, 0, 639)) } } };
        var train = NodeFixtures.Train(activity: TrainActivity.Running, speed: 320) with
        { Position = new TrainPosition.Node(NodeFixtures.NodeId, null, null, null, null, NodeTravelPhase.Approaching, 240, null) };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, [train], travelMeters: 240);
        await using var vehicles = new SceneVehicles();
        var cache = new NodeScene.Cache();
        await using var scene = Open(snapshot, vehicles, cache);
        await scene.BuildWork!;
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        SceneFrame? displayed = null;
        for (var i = 0; i < 20; i++)
        {
            var frame = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), displayed, default, target);
            if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) displayed = frame;
            // At the node centre, 320 m/s reaches the 160 m lead cap; the head contributes another 4 cells.
            var retained = new NodeRange(default, 192).Window(scene.Layout!);
            var ahead = new NodeRange(new(0, 0, 324), 160).Window(scene.Layout!);
            var columns = (Math.Max(retained.Right, ahead.Right) - Math.Min(retained.Left, ahead.Left)) / 32;
            var rows = (Math.Max(retained.Front, ahead.Front) - Math.Min(retained.Back, ahead.Back)) / 32;
            var fog = frame.Update.Environment.FoggedRegions
                .Where(bounds => bounds.Minimum.Y == -128 && bounds.Maximum.Y == 128).ToArray();
            Assert.IsTrue(fog.Length <= 4 + rows * ((columns + 1) / 2),
                "Merged runs are bounded by the combined retention and forward-prefetch window.");
            var connected = Chunks(cache);
            foreach (var from in new NodeRange(default, 10000).Chunks(scene.Layout!))
            {
                var point = new Vector3(from.X + 16, 0, from.Z + 16);
                var covered = fog.Any(bounds => point.X > bounds.Minimum.X && point.X < bounds.Maximum.X &&
                    point.Z > bounds.Minimum.Z && point.Z < bounds.Maximum.Z);
                Assert.AreEqual(!connected.ContainsKey(from), covered,
                    $"Terrain chunk {from} must be fogged exactly when its geometry is absent.");
            }
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
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
        foreach (var (route, number) in layout.TurnRoutes().Select((route, number) => (route, number)))
            expectedRoutes.Add($"turnaround:{number}", route.Length);
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
                    vertex.Z >= segment.Bounds.Minimum.Z && vertex.Z <= segment.Bounds.Maximum.Z,
                    $"{key}: vertex {vertex}, bounds {segment.Bounds}");
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
        var started = new ConcurrentQueue<(ScenePoint Focus, string Key)>();
        await using var vehicles = new SceneVehicles();
        await using (var scene = Open(snapshot, vehicles, cache, buildRail: (_, segment, old, token) =>
        {
            started.Enqueue((currentFocus, segment.Key));
            return segment.Build(old, token);
        }))
        {
            await Settle(scene, snapshot, currentFocus);
            AssertStartedWithinPrefetch(currentFocus);
            var before = Rails(cache);
            Assert.IsTrue(before.Count > 0);
            var oldFrame = Present(scene, snapshot);
            var focus = RailwayCoordinates.Add(line.Start, line.Forward * (float)(line.Length - 64));
            currentFocus = focus;
            await Settle(scene, snapshot, focus);
            AssertStartedWithinPrefetch(focus);
            retained = Rails(cache);
            Assert.IsTrue(retained.Count > 0);
            var retention = new NodeRange(focus, NodeStreaming.RetainCells);
            Assert.IsTrue(before.Keys.Any(key => !retention.Intersects(prepared.Rails[key].Bounds)));
            Assert.IsTrue(retained.Keys.All(key => retention.Intersects(prepared.Rails[key].Bounds)));
            Assert.IsTrue(Present(scene, snapshot, oldFrame).Update.Removed.Length > 0);
        }
        await using var reopened = Open(snapshot, vehicles, cache);
        await reopened.BuildWork!;
        reopened.Pump(focus: currentFocus);
        await Settle(reopened, snapshot, currentFocus);
        foreach (var (key, part) in retained) Assert.AreSame(part, Rails(cache)[key]);

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
    public async Task PendingRailFogDoesNotCoverTheAttachedCurvedRail()
    {
        var snapshot = NodeFixtures.Snapshot(content: NodeFixtures.Content());
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var segment = prepared.Rails.Values.First(part => Vector3.Dot(part.Sample(part.Begin).Forward,
            part.Sample(part.End).Forward) < .99f);
        var part = new NodeScene.Part(segment.Inputs, WorldSceneSession.BuildGeometry(_ => new([], [], [])),
            ImmutableDictionary<GridColumn, double>.Empty);
        var cache = new NodeScene.Cache();
        var owner = cache.Open(NodeFixtures.NodeId);
        cache.Publish(NodeFixtures.NodeId, owner, prepared.Global with { Parts = prepared.Global.Parts.Add(segment.Key, part) });
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, cache,
            build: (data, from, old, token) => { release.Wait(token); return SmallPart(data, from, old, token); },
            buildRail: (_, pending, old, token) => { release.Wait(token); return pending.Build(old, token); });
        try
        {
            await scene.BuildWork!;
            scene.Pump(focus: segment.Sample((segment.Begin + segment.End) / 2).Position);
            var frame = Present(scene, snapshot);
            Assert.IsTrue(Rails(cache).ContainsKey(segment.Key));
            var fog = frame.Update.Environment.FoggedRegions.Where(box => box.Minimum.Y == segment.Bounds.Minimum.Y &&
                box.Maximum.Y == segment.Bounds.Maximum.Y).ToArray();
            Assert.IsTrue(fog.Length > 0, "Other rails are still pending and must retain construction fog.");
            for (var arc = segment.Begin + 1; arc < segment.End - 1; arc += 2)
            {
                var point = segment.Sample(arc).Position;
                Assert.IsFalse(fog.Any(box => point.X > box.Minimum.X && point.X < box.Maximum.X &&
                    point.Z > box.Minimum.Z && point.Z < box.Maximum.Z), "Pending rail AABBs must not hide the attached curve.");
            }
        }
        finally { release.Set(); }
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
        var overBudget = 0;
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, cache, build: (data, from, old, token) =>
        {
            if (Interlocked.Increment(ref activeBuilds) > RailwayCoordinates.MaxConcurrentBuilds) Interlocked.Exchange(ref overBudget, 1);
            try { return SmallPart(data, from, old, token); }
            finally { Interlocked.Decrement(ref activeBuilds); }
        }, buildRail: (data, segment, old, token) =>
        {
            if (Interlocked.Increment(ref activeBuilds) > RailwayCoordinates.MaxConcurrentBuilds) Interlocked.Exchange(ref overBudget, 1);
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
                Assert.AreEqual(0, overBudget, "Terrain and rail builders must share the concurrency limit.");
                Assert.IsTrue(pending.Count(work => !work.IsCompleted) <= RailwayCoordinates.MaxConcurrentBuilds);
                if (close) { closing = scene.DisposeAsync().AsTask(); Assert.IsFalse(closing.IsCompleted); }
                else scene.Pump(focus: new(10000, 0, 10000));
            }
            finally { release.Set(); }
            await SceneFixtures.AwaitPendingWork(pending);
            if (closing is not null) await closing;
            else scene.Pump(focus: new(10000, 0, 10000));
            Assert.IsFalse(Rails(cache).ContainsKey(blocked.Key));
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

    private static SceneWorldSnapshot Snapshot(bool large, bool nearVehicle = false, double relief = 0, bool overviewPlots = false)
    {
        var layout = NodeFixtures.Layout with
        {
            Plots = overviewPlots ? [new(1, PlotSize.Small, new(288, 0, 288), VoxelOrientation.PxPy)] : [],
            SceneModel = NodeFixtures.Layout.SceneModel with
            {
                Scene = SceneFixtures.Projection(large ? [SceneFixtures.Cell(new(-320, 0, -320)), SceneFixtures.Cell(new(319, 0, 319), 2)] : []) with
                { OccupiedBounds = large ? new(new(-320, 0, -320), new(319, 0, 319)) : null }
            }
        };
        return NodeFixtures.EmptyNodeSnapshot(layout, nearVehicle ? [NodeFixtures.Train()] : [], relief);
    }

    private static ImmutableDictionary<GridColumn, NodeScene.Part> Chunks(NodeScene.Cache cache) =>
        cache.Read(NodeFixtures.NodeId)!.Parts.Where(item => item.Key.StartsWith("terrain:", StringComparison.Ordinal))
            .ToImmutableDictionary(item =>
            {
                var coordinates = item.Key.Split(':');
                return new GridColumn(int.Parse(coordinates[1]), int.Parse(coordinates[2]));
            }, item => item.Value);

    private static ImmutableDictionary<string, NodeScene.Part> Rails(NodeScene.Cache cache) =>
        cache.Read(NodeFixtures.NodeId)!.Parts.Where(item =>
            item.Key.StartsWith("entry:", StringComparison.Ordinal) ||
            item.Key.StartsWith("station-tracks:", StringComparison.Ordinal) ||
            item.Key.StartsWith("turnaround:", StringComparison.Ordinal) ||
            item.Key.StartsWith("parking-tracks:", StringComparison.Ordinal)).ToImmutableDictionary();

    private static NodeScene Open(SceneWorldSnapshot snapshot, SceneVehicles vehicles, NodeScene.Cache? cache = null,
        Func<NodeScene.Preparation, GridColumn, NodeScene.Part?, CancellationToken, NodeScene.Part>? build = null,
        Func<NodeScene.Preparation, NodeScene.RailSegment, NodeScene.Part?, CancellationToken, NodeScene.Part>? buildRail = null,
        DisplayRate? displayRate = null)
    {
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        return new(snapshot, NodeFixtures.NodeId, cache ?? new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
            return id;
        }, vehicles, displayRate ?? new(), _ => null, buildChunk: build ?? SmallPart, buildRail: buildRail);
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

    private static async Task Settle(NodeScene scene, SceneWorldSnapshot snapshot, ScenePoint focus)
    {
        await scene.BuildWork!;
        SceneFrame? frame = null;
        int previousObjects;
        do
        {
            previousObjects = frame?.Objects.Length ?? -1;
            scene.Pump(focus: focus);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            frame = Present(scene, snapshot);
        } while (scene.HasPendingBuilds || frame.Readiness is SceneReadiness.Loading || frame.Objects.Length != previousObjects);
    }

    private static SceneFrame Present(NodeScene scene, SceneWorldSnapshot snapshot, SceneFrame? previous = null)
    {
        var state = NodeFixtures.Displays.GetValue(scene, static _ => new());
        var frame = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), previous ?? state.Displayed, default);
        if (frame.Readiness is not SceneReadiness.Loading and not SceneReadiness.Failed) state.Displayed = frame;
        return frame;
    }

    private static SceneRay Ray(GridColumn from) => new(new(from.X + .5f, 20, from.Z + .5f), -Vector3.UnitY, 100);
}

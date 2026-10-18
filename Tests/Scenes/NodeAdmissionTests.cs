using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeAdmissionTests
{
    [TestMethod]
    [DataRow(3000, 8)]
    [DataRow(6000, 8)]
    [DataRow(12000, 4)]
    [DataRow(16000, 3)]
    public async Task Completed_path_parts_admit_in_sorted_order_up_to_the_limit_then_wait_for_the_displayed_receipt(
        int verticesPerPart, int expectedParts)
    {
        await using var run = new AdmissionRun(_ => (verticesPerPart, 1), Snapshot(followedTrain: true));
        await run.BuildAt(new(-192, 0, 0));
        await run.DisplayAll();
        await run.BuildAt(new(-96, 0, -96));
        await run.BuildAt(new(0, 0, 96));
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        var offered = run.Present(target);
        var queued = run.Completed.Keys.Where(from => !run.Admitted.ContainsKey(from)).ToHashSet();
        var earlier = new GridColumn(0, 32);
        var later = new GridColumn(0, 96);
        GridColumn[] path = [new(-32, 32), earlier, new(-32, 64), new(0, 64),
            new(-32, 96), later, new(-32, 128), new(0, 128)];
        Assert.IsTrue(path.All(queued.Contains));
        var frozen = run.Admitted.Keys.ToHashSet();
        run.Present(target);
        CollectionAssert.AreEquivalent(frozen.ToArray(), run.Admitted.Keys.ToArray());

        run.Displayed = offered;
        var critical = run.Present(target);
        var added = run.Admitted.Keys.Where(from => !frozen.Contains(from)).ToArray();
        // The 52m train spans Z44..148; all four crossed rows share the batch budget.
        CollectionAssert.AreEquivalent(path.Take(expectedParts).ToArray(), added);
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Found>(critical.Locate(new(earlier.X + .5f, 1, earlier.Z + .5f)));
        Assert.IsTrue(added.Length * verticesPerPart <= NodeStreaming.AdmissionVertexLimit && added.Length < NodeStreaming.AdmissionVisualLimit);
        if (expectedParts < path.Length)
            Assert.IsTrue((added.Length + 1) * verticesPerPart > NodeStreaming.AdmissionVertexLimit,
                "The next completed part must wait when it would exceed the batch limit.");
        var admitted = run.Admitted.Keys.ToArray();
        run.Present(target);
        CollectionAssert.AreEquivalent(admitted, run.Admitted.Keys.ToArray(), "The next batch still needs its display receipt.");
    }

    [TestMethod]
    public async Task Partial_admission_prioritizes_the_path_and_still_waits_for_a_displayed_receipt()
    {
        await using var run = new AdmissionRun(_ => (24000, 1));
        await run.BuildAt(default);
        await run.DisplayAll();
        var before = run.Admitted.Keys.ToHashSet();
        ScenePoint[] path = [new(304, 200, 16)];
        await run.BuildAt(new(192, 0, 0), priorityPath: path);
        var forward = new GridColumn(288, 0);
        var side = new GridColumn(192, 64);
        Assert.IsFalse(before.Contains(forward));
        Assert.IsFalse(before.Contains(side));
        Assert.IsTrue(run.Admitted.ContainsKey(forward), "A path part must enter before a side part closer to the focus.");
        Assert.IsTrue(run.Completed.ContainsKey(side));
        Assert.IsFalse(run.Admitted.ContainsKey(side));
        var added = run.Admitted.Keys.Where(from => !before.Contains(from)).ToArray();
        Assert.IsTrue(added.Length > 0 && added.Length * 24000 <= NodeStreaming.AdmissionVertexLimit);
        var offered = run.Present();
        var frozen = run.Admitted.Keys.ToArray();
        run.Scene.Pump(focus: new(192, 0, 0), following: new(new(192, 0, 0), path, []));
        CollectionAssert.AreEquivalent(frozen, run.Admitted.Keys.ToArray());
        run.Displayed = offered;
        run.Present();
        Assert.IsTrue(run.Admitted.Count > frozen.Length, "A matching receipt must release the next admission batch.");
    }

    [TestMethod]
    public async Task First_follow_frame_admits_a_completed_terrain_layer_beyond_the_64m_path()
    {
        await using var run = new AdmissionRun(_ => (12000, 1),
            Snapshot(followedTrain: true, trainLengthMeters: 26, travelMeters: 240));
        await run.BuildAt(new(0, 0, 160));
        var reserve = new GridColumn(0, 288);
        Assert.IsTrue(run.Completed.ContainsKey(reserve));
        Assert.IsFalse(run.Admitted.ContainsKey(reserve));

        var target = new WorldSceneTarget.Follow(new(1), new(1));
        run.Present(target);
        await SceneFixtures.AwaitPendingWork(run.Scene.PendingWork);
        var first = run.Present(target);

        // The head is Z122. The layer starting at Z288 needs an 80m initial path,
        // including its 16m margin; the old 64m path leaves this completed layer queued.
        Assert.AreNotEqual(SceneReadiness.Loading, first.Readiness);
        Assert.IsTrue(run.Admitted.ContainsKey(reserve), "The first display target needs a completed layer in reserve for display lag.");
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Found>(first.Locate(new(.5f, 1, 288.5f)));
    }

    [TestMethod]
    public async Task Loading_admits_completed_path_parts_beyond_the_near_circle_but_keeps_side_parts_queued()
    {
        await using var run = new AdmissionRun(_ => (12000, 1));
        ScenePoint[] path = Enumerable.Range(0, 5).Select(index => new ScenePoint(index * 32, 200, 0)).ToArray();
        await run.BuildAt(default, path);
        var forward = new GridColumn(128, 0);
        var side = new GridColumn(0, 96);
        Assert.IsFalse(new NodeRange(default, NodeStreaming.NearCells).Intersects(forward));
        Assert.IsTrue(run.Completed.ContainsKey(forward));
        Assert.IsTrue(run.Admitted.ContainsKey(forward), "Loading must admit the path through 64m ahead regardless of height or budget.");
        Assert.IsTrue(run.Completed.ContainsKey(side));
        Assert.IsFalse(run.Admitted.ContainsKey(side), "A completed side part outside the near circle must wait for a batch.");
        Assert.IsTrue(run.Admitted.Count * 12000 > NodeStreaming.AdmissionVertexLimit);
        var first = run.Present();
        Assert.AreEqual(SceneReadiness.Partial, first.Readiness);
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Found>(first.Locate(new(forward.X + .5f, 1, forward.Z + .5f)));
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Missing>(first.Locate(new(side.X + .5f, 1, side.Z + .5f)));
    }

    [TestMethod]
    [DataRow("session")]
    [DataRow("request")]
    [DataRow("space")]
    [DataRow("revision")]
    public async Task A_receipt_outside_the_offered_batch_scope_does_not_unlock_admission(string mismatch)
    {
        await using var run = new AdmissionRun(_ => (3, 1));
        await run.BuildAt(default);
        var offered = run.Present();
        Assert.AreEqual(SceneReadiness.Partial, offered.Readiness);
        var before = run.Admitted.Keys.ToArray();
        var stamp = offered.Update.Frame;
        stamp = mismatch switch
        {
            "session" => stamp with { Session = new(Guid.NewGuid()) },
            "request" => stamp with { Request = new(stamp.Request.Value + 1) },
            "space" => stamp with { Space = new(stamp.Space.Value + 1) },
            _ => stamp with { Revision = stamp.Revision - 1 }
        };
        run.Displayed = new(stamp, offered.Origin, offered.SimulationSeconds, offered.Readiness,
            offered.Objects, [], [], []);
        run.Present();
        CollectionAssert.AreEquivalent(before, run.Admitted.Keys.ToArray());
        run.Displayed = offered;
        run.Present();
        Assert.IsTrue(run.Admitted.Count > before.Length, "A matching receipt must still unlock the waiting batch.");
    }

    [TestMethod]
    public async Task Loading_admits_all_completed_near_parts_without_a_budget_or_receipt()
    {
        await using var run = new AdmissionRun(_ => (12000, 1));
        await run.BuildAt(default);
        var near = new NodeRange(default, NodeStreaming.NearCells).Chunks(run.Scene.Layout!).ToArray();
        Assert.IsTrue(near.Length * 12000 > NodeStreaming.AdmissionVertexLimit);
        Assert.IsTrue(near.All(from => run.Admitted.ContainsKey(from)));
        Assert.IsTrue(run.Completed.Keys.Any(from => !new NodeRange(default, NodeStreaming.NearCells).Intersects(from)));
        Assert.IsTrue(run.Admitted.Keys.All(from => new NodeRange(default, NodeStreaming.NearCells).Intersects(from)),
            "Completed outer parts must wait while the first near area is loading.");
        var first = run.Present();
        Assert.AreNotEqual(SceneReadiness.Loading, first.Readiness);
        Assert.IsTrue(near.All(from => first.Locate(new(from.X + .5f, 1, from.Z + .5f)) is Lookup<WorldLocation>.Found));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Batches_obey_both_budgets_prioritize_near_parts_and_wait_for_the_displayed_receipt(bool vertexLimited)
    {
        await using var run = new AdmissionRun(_ => (12000, vertexLimited ? 1 : 20));
        await run.BuildAt(default);
        var first = run.Present();
        var before = run.Admitted.Keys.ToHashSet();
        run.Present();
        run.Present();
        CollectionAssert.AreEquivalent(before.ToArray(), run.Admitted.Keys.ToArray());

        run.Displayed = first;
        var batch = run.Present();
        var added = run.Admitted.Where(pair => !before.Contains(pair.Key)).ToArray();
        Assert.IsTrue(added.Length > 0);
        var visuals = added.SelectMany(pair => pair.Value.Geometry.Geometry.Objects).SelectMany(item => item.Visuals).ToArray();
        Assert.IsTrue(visuals.Length <= NodeStreaming.AdmissionVisualLimit);
        Assert.IsTrue(visuals.OfType<SceneVisual.Geometry>().Sum(item => (long)item.Mesh.Vertices.Length) <= NodeStreaming.AdmissionVertexLimit);
        var remaining = run.Completed.Keys.Where(from => !run.Admitted.ContainsKey(from)).ToArray();
        Assert.IsTrue(remaining.Length > 0);
        Assert.IsTrue(added.Max(pair => Distance(pair.Key)) <= remaining.Min(Distance));

        var frozen = run.Admitted.Keys.ToArray();
        run.Present();
        CollectionAssert.AreEquivalent(frozen, run.Admitted.Keys.ToArray());
        run.Displayed = batch;
        run.Present();
        Assert.IsTrue(run.Admitted.Count > frozen.Length);
    }

    [TestMethod]
    public async Task Completed_queued_parts_are_absent_from_cache_mapping_and_picking()
    {
        await using var run = new AdmissionRun(_ => (12000, 1));
        await run.BuildAt(default);
        var frame = run.Present();
        var queued = run.Completed.Keys.First(from => !run.Admitted.ContainsKey(from));
        Assert.IsFalse(run.Cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(NodeScene.ChunkKey(queued)));
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Missing>(frame.Locate(new(queued.X + .5f, 1, queued.Z + .5f)));
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(new(queued.X + .5f, 20, queued.Z + .5f), -Vector3.UnitY, 100)));
    }

    [TestMethod]
    public async Task Queued_parts_outside_retention_are_discarded_and_rebuilt_when_focus_returns()
    {
        await using var run = new AdmissionRun(_ => (12000, 1));
        await run.BuildAt(default);
        var first = run.Present();
        var queued = run.Completed.Keys.Where(from => !run.Admitted.ContainsKey(from)).OrderBy(from => from.X).First();
        var previousBuilds = run.BuildCounts[queued];
        var away = new ScenePoint(288, 0, 288);
        if (new NodeRange(away, NodeStreaming.RetainCells).Intersects(queued)) away = new(-288, 0, -288);
        Assert.IsFalse(new NodeRange(away, NodeStreaming.RetainCells).Intersects(queued));
        await run.BuildAt(away);
        Assert.IsFalse(run.Admitted.ContainsKey(queued));
        Assert.IsFalse(run.Cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(NodeScene.ChunkKey(queued)));
        run.Displayed = first;
        await run.BuildAt(default);
        Assert.IsTrue(run.BuildCounts[queued] > previousBuilds, "An unadmitted discarded result must not survive through the shared cache.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Oversized_part_gets_a_batch_of_its_own_and_does_not_block_later_parts(bool vertices)
    {
        var oversized = new GridColumn(96, 0);
        await using var run = new AdmissionRun(from => from == oversized
            ? vertices ? (NodeStreaming.AdmissionVertexLimit + 3, 1) : (3, NodeStreaming.AdmissionVisualLimit + 1)
            : (12000, 1));
        await run.BuildAt(default);
        var first = run.Present();
        Assert.IsFalse(run.Admitted.ContainsKey(oversized));
        run.Displayed = first;
        var observed = false;
        for (var batch = 0; batch < run.Completed.Count + 1; batch++)
        {
            var before = run.Admitted.Keys.ToHashSet();
            var frame = run.Present();
            var added = run.Admitted.Keys.Where(from => !before.Contains(from)).ToArray();
            if (added.Contains(oversized))
            {
                Assert.AreEqual(1, added.Length, "An oversized part must not share its batch.");
                var count = run.Admitted.Count;
                run.Present();
                Assert.AreEqual(count, run.Admitted.Count);
                run.Displayed = frame;
                run.Present();
                Assert.IsTrue(run.Admitted.Count > count, "The oversized batch must permit subsequent progress after acknowledgment.");
                observed = true;
                break;
            }
            run.Displayed = frame;
        }
        Assert.IsTrue(observed, "The completed oversized part must eventually be admitted.");
    }

    [TestMethod]
    public async Task Display_backlog_does_not_keep_the_node_build_slot_pending()
    {
        await using var run = new AdmissionRun(_ => (12000, 1));
        await run.BuildAt(default);
        await run.DisplayAll();
        run.Displayed = run.Present();
        await run.BuildAt(new(192, 0, 0));
        var offered = run.Present();
        Assert.IsTrue(offered.Update.Upserts.Length > 0);
        Assert.AreEqual(0, run.Scene.PendingWork.Length);
        Assert.IsFalse(run.Scene.HasPendingBuilds,
            "Completed parts and an unacknowledged display batch must allow the outgoing line candidate to build.");
        var waiting = run.Present();
        CollectionAssert.AreEqual(offered.Update.Upserts.Select(item => item.Id).ToArray(),
            waiting.Update.Upserts.Select(item => item.Id).ToArray(),
            "The same unacknowledged batch must remain offered while build work is already idle.");
    }

    private static double Distance(GridColumn from) => new NodeRange(default, 0).DistanceSquared(new(
        new(from.X, -128, from.Z), new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize)));

    private sealed class AdmissionRun : IAsyncDisposable
    {
        private readonly SceneWorldSnapshot snapshot;
        private readonly SceneVehicles vehicles = new();
        private ulong revision;
        private ScenePoint focus;
        internal readonly NodeScene.Cache Cache = new();
        internal readonly ConcurrentDictionary<GridColumn, NodeScene.Part> Completed = new();
        internal readonly ConcurrentDictionary<GridColumn, int> BuildCounts = new();
        internal NodeScene Scene { get; }
        internal SceneFrame? Displayed { get; set; }
        internal Dictionary<GridColumn, NodeScene.Part> Admitted => Cache.Read(NodeFixtures.NodeId)!.Parts
            .Where(pair => pair.Key.StartsWith("terrain:", StringComparison.Ordinal)).ToDictionary(pair =>
            {
                var parts = pair.Key.Split(':');
                return new GridColumn(int.Parse(parts[1]), int.Parse(parts[2]));
            }, pair => pair.Value);

        internal AdmissionRun(Func<GridColumn, (int Vertices, int Visuals)> size, SceneWorldSnapshot? snapshot = null)
        {
            this.snapshot = snapshot ?? Snapshot();
            ulong next = 0;
            var ids = new Dictionary<string, SceneObjectId>();
            Scene = new(this.snapshot, NodeFixtures.NodeId, Cache, key =>
            {
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
                return id;
            }, vehicles, new(), buildChunk: (data, from, old, token) =>
            {
                token.ThrowIfCancellationRequested();
                var shape = size(from);
                var part = Part(data, from, shape.Vertices, shape.Visuals);
                Completed[from] = part;
                BuildCounts.AddOrUpdate(from, 1, (_, count) => count + 1);
                return part;
            });
        }

        internal async Task BuildAt(ScenePoint focus, IReadOnlyList<ScenePoint>? loadingPath = null,
            IReadOnlyList<ScenePoint>? priorityPath = null)
        {
            this.focus = focus;
            await Scene.BuildWork!;
            Scene.Pump(focus: focus, following: new(focus, priorityPath ?? [], loadingPath ?? []));
            while (Scene.PendingWork.Length > 0)
            {
                await SceneFixtures.AwaitPendingWork(Scene.PendingWork);
                Scene.Pump(focus: focus, following: new(focus, priorityPath ?? [], loadingPath ?? []));
            }
        }

        internal async Task DisplayAll()
        {
            var demanded = new NodeRange(focus, NodeStreaming.PrefetchCells).Chunks(Scene.Layout!).ToArray();
            var previousRemaining = int.MaxValue;
            while (true)
            {
                var pending = Scene.PendingWork;
                await SceneFixtures.AwaitPendingWork(pending);
                var frame = Present();
                if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) Displayed = frame;
                Assert.AreNotEqual(SceneReadiness.Failed, frame.Readiness);
                var remaining = demanded.Count(from => !Admitted.ContainsKey(from));
                if (remaining == 0 && !Scene.HasPendingBuilds)
                {
                    Displayed = Present();
                    return;
                }
                Assert.IsTrue(remaining < previousRemaining || pending.Length > 0 || Scene.PendingWork.Length > 0,
                    "Displaying the active demand must admit geometry or await real build work.");
                previousRemaining = remaining;
            }
        }

        internal SceneFrame Present(WorldSceneTarget? target = null) => Scene.Present(snapshot, new(1, null, SceneDebugLayer.None),
            new(new(Guid.Empty), new(1), new(1), ++revision), Displayed, default, target);

        public async ValueTask DisposeAsync()
        {
            await Scene.DisposeAsync();
            await vehicles.DisposeAsync();
        }
    }

    private static readonly ConcurrentDictionary<int, ImmutableArray<SceneVertex>> VertexArrays = new();

    private static NodeScene.Part Part(NodeScene.Preparation data, GridColumn from, int vertices, int visuals)
    {
        var vertexCount = Math.Max(3, vertices / visuals);
        var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
            VertexArrays.GetOrAdd(vertexCount, static count => Enumerable.Range(0, count).Select(index =>
                new SceneVertex(Vector3.UnitY + (index % 3 == 1 ? Vector3.UnitZ : index % 3 == 2 ? Vector3.UnitX : Vector3.Zero),
                    Vector3.UnitY, default, default, ExposedFaceEdges.None, 0, 0)).ToImmutableArray()), [0, 1, 2]);
        var geometry = WorldSceneSession.BuildGeometry(id =>
        {
            var root = id("");
            var pose = new ScenePose(new(from.X, 0, from.Z), Quaternion.Identity);
            var grid = new VoxelGrid([new(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube,
                null, null, new PickSubject.Terrain(NodeFixtures.Location), []))]);
            return new([new(root, null, pose, null,
                Enumerable.Range(0, visuals).Select(_ => (SceneVisual)new SceneVisual.Geometry(mesh, new(false, 0, 0, false))).ToImmutableArray(), [], [])],
                [new(root, pose, grid)], []);
        });
        return new(data.ChunkInputs(from), geometry, ImmutableDictionary<GridColumn, double>.Empty.Add(from, 1));
    }

    private static SceneWorldSnapshot Snapshot(bool followedTrain = false, double trainLengthMeters = 52, double travelMeters = 80)
    {
        var layout = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-320, 0, -320), new(319, 0, 319)) } } };
        ImmutableArray<TrainView> trains = followedTrain
            ? [NodeFixtures.Train(activity: TrainActivity.Running) with
            { LengthMeters = trainLengthMeters, Position = new TrainPosition.Node(NodeFixtures.NodeId, null, null,
                NodeTravelPhase.Departing, travelMeters + 48, null) }]
            : [];
        return NodeFixtures.EmptyNodeSnapshot(layout, trains,
            design: followedTrain ? RailwayFixtures.Vehicle(lengthCells: (int)(trainLengthMeters * 2)) : null, travelMeters: travelMeters);
    }
}

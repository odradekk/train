using System.Collections.Concurrent;
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
public sealed class NodeAdmissionTests
{
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

    private static double Distance(GridColumn from) => new NodeRange(default, 0).DistanceSquared(new(
        new(from.X, -128, from.Z), new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize)));

    private sealed class AdmissionRun : IAsyncDisposable
    {
        private readonly SceneWorldSnapshot snapshot = Snapshot();
        private readonly SceneVehicles vehicles = new();
        private ulong revision;
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

        internal AdmissionRun(Func<GridColumn, (int Vertices, int Visuals)> size)
        {
            ulong next = 0;
            var ids = new Dictionary<string, SceneObjectId>();
            Scene = new(snapshot, NodeFixtures.NodeId, Cache, key =>
            {
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
                return id;
            }, vehicles, buildChunk: (data, from, old, token) =>
            {
                token.ThrowIfCancellationRequested();
                var shape = size(from);
                var part = Part(data, from, shape.Vertices, shape.Visuals);
                Completed[from] = part;
                BuildCounts.AddOrUpdate(from, 1, (_, count) => count + 1);
                return part;
            });
        }

        internal async Task BuildAt(ScenePoint focus)
        {
            await Scene.BuildWork!;
            Scene.Pump(focus: focus);
            while (Scene.PendingWork.Length > 0)
            {
                await SceneFixtures.AwaitPendingWork(Scene.PendingWork);
                Scene.Pump(focus: focus);
            }
        }

        internal SceneFrame Present() => Scene.Present(snapshot, new(1, null, SceneDebugLayer.None),
            new(new(Guid.Empty), new(1), new(1), ++revision), Displayed, default);

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

    private static SceneWorldSnapshot Snapshot()
    {
        var source = NodeFixtures.Content();
        var layout = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-320, 0, -320), new(319, 0, 319)) } } };
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
        var original = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: [], content: content, platformLengthMeters: 0);
        var node = original.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId);
        return new(new FixedCoreReadView
        {
            Position = original.Core.Position, InputIdentity = original.Core.InputIdentity,
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [node.Id] = original.Core.FindNode(node.Id) },
            World = original.Core.World with { Nodes = [node], Edges = [], Trains = [] },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            { [new(1)] = new Lookup<SettlementView>.Found(NodeFixtures.Settlement(0) with { Plots = [] }) }
        }, content);
    }
}

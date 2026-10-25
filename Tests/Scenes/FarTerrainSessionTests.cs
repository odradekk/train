using System.Numerics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainSessionTests
{
    [TestMethod]
    public async Task Ready_node_does_not_wait_for_pending_far_terrain()
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], instances: []);
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), SceneFixtures.NextId(),
            vehicles, new(), _ => null, generate: RailwayFixtures.EmptyNode);
        scene.SetFarBuildAllowed(false);
        await scene.BuildWork!;
        scene.Pump();
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var first = scene.Present(snapshot, request, stamp, null, default);
        Assert.AreEqual(SceneReadiness.Ready, first.Readiness);

        scene.SetTerrainViewRadius(256);
        scene.Pump();
        var next = scene.Present(snapshot, request, stamp with { Revision = 2 }, first, default);

        Assert.IsTrue(scene.HasPendingCoarseTerrain, "The fixture must still require L2 terrain.");
        Assert.AreEqual(SceneReadiness.Ready, next.Readiness,
            "Only the required L0 terrain, tracks and instances determine Ready.");
        Assert.IsInstanceOfType<Lookup<double>.Missing>(next.DisplayTerrainHeight(200, 200));
    }

    [TestMethod]
    public async Task Cancelling_session_candidate_drains_queued_far_work_without_publication()
    {
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0,
            content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle(),
                rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }),
            trains: [RailwayFixtures.Train(3, speed: 10)], instances: [RailwayFixtures.Instance()]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new(), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var current = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Present(session, current);
        Assert.IsInstanceOfType<WorldLocation.Node>(session.PrebuiltLocation);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        var candidate = Field<object>(session, "candidate");
        var node = (NodeScene)candidate.GetType().GetProperty("Node")!.GetValue(candidate)!;
        var terrain = Field<FarTerrain>(node, "farTerrain");
        FarTerrainView before;
        Task[] queued;
        // Hold publication until RequestView has revoked the candidate's lifetime.
        lock (Field<object>(node, "workGate"))
        lock (Field<object>(terrain, "gate"))
        {
            node.SetTerrainViewRadius(1);
            node.SetFarBuildAllowed(true);
            node.Pump(snapshot);
            before = terrain.View;
            queued = node.PendingFarWork;
            Assert.IsTrue(queued.Any(task => !task.IsCompleted), "The candidate must have queued real far work before cancellation.");
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
            Assert.IsNull(session.PrebuiltLocation);
        }
        await SceneFixtures.AwaitPendingWork(queued);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        Assert.AreSame(before, terrain.View, "Cancelled work must not publish a late tile view.");
        var after = Present(session, current);
        Assert.IsNull(after.Node);
        Assert.IsNull(session.PrebuiltLocation);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Founded_successor_activates_when_near_prepared_and_reuses_displayed_outside_tiles(bool fine) =>
        FoundedHandover(fine, late: false);

    [TestMethod]
    public Task Founded_successor_inherits_old_tiles_completed_after_it_opened() =>
        FoundedHandover(fine: false, late: true);

    private static async Task FoundedHandover(bool fine, bool late)
    {
        var founded = NodeFixtures.EmptyNodeSnapshot(NodeFixtures.Layout with { Plots = [] }, []);
        var empty = RailwayFixtures.ReplaceCore(founded, node: founded.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), empty, new(), new(),
            (snapshot, id, previous, token) =>
            {
                if (Interlocked.Increment(ref builds) == 2)
                {
                    started.TrySetResult();
                    release.Wait(token);
                }
                return RailwayFixtures.EmptyNode(snapshot, id, previous, token);
            });
        try
        {
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
            var displayed = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
            var oldNode = Field<NodeScene>(session, "node");
            oldNode.SetFarBuildAllowed(false);
            await SceneFixtures.AwaitPendingWork(oldNode.PendingFarWork);
            oldNode.SetFineTerrainBuildAllowed(false);
            var oldTerrain = Field<FarTerrain>(oldNode, "farTerrain");
            FarTerrainView oldView;
            if (!late)
            {
                oldNode.SetTerrainViewRadius(1);
                oldNode.Pump(empty);
                oldTerrain.Demand(new(512, 0, 512), 1);
                oldTerrain.SetFineBuildAllowed(fine);
                while (oldTerrain.HasRunnableWork) oldTerrain.Advance(65536, CancellationToken.None);
            }
            else
            {
                Assert.IsTrue(oldTerrain.View.Tiles.IsEmpty, "No old tile may exist before the successor opens.");
                Assert.IsInstanceOfType<Lookup<double>.Missing>(displayed.DisplayTerrainHeight(
                    256 - displayed.Origin.X, 256 - displayed.Origin.Z));
            }
            // Neither worker can admit a tile while Observe opens the blocked successor.
            lock (Field<object>(oldNode, "workGate"))
            lock (Field<object>(oldTerrain, "gate"))
            {
                displayed = Present(session, displayed, radius: 1);
                oldNode.SetFarBuildAllowed(false);
                session.Observe(founded, []);
                displayed = Present(session, displayed, radius: 1);
                oldNode.SetFarBuildAllowed(false);
            }
            await started.Task;
            await SceneFixtures.AwaitPendingWork(oldNode.PendingFarWork);
            var successor = Field<NodeScene>(session, "successor");
            var terrain = Field<FarTerrain>(successor, "farTerrain");
            lock (Field<object>(successor, "workGate"))
            lock (Field<object>(terrain, "gate"))
            {
                release.Set();
                successor.BuildWork!.GetAwaiter().GetResult();
                if (late)
                {
                    successor.SetTerrainViewRadius(1);
                    successor.SetFarBuildAllowed(false);
                    successor.Pump(founded);
                    Assert.IsTrue(successor.NearPrepared);
                    Assert.IsTrue(terrain.HasPendingCoarse);
                    Assert.IsTrue(terrain.View.Tiles.IsEmpty,
                        "Successor binding must happen before the old scene completes its tile.");
                    Assert.IsTrue(oldTerrain.View.Tiles.IsEmpty);
                    oldTerrain.Demand(new(512, 0, 512), 1);
                    oldTerrain.SetFineBuildAllowed(false);
                    while (oldTerrain.HasRunnableWork) oldTerrain.Advance(65536, CancellationToken.None);
                    Assert.IsFalse(oldTerrain.HasPendingCoarse);
                    Assert.IsTrue(terrain.View.Tiles.IsEmpty,
                        "The already-bound successor must not acquire late tiles through cache initialization.");
                }
                oldView = oldTerrain.View;
                Assert.IsFalse(oldView.Tiles.IsEmpty);
                Assert.AreEqual(fine, oldView.Tiles.Any(tile => tile.SpacingCells == TerrainTile.NearSpacingCells));

                var waiting = Present(session, displayed, radius: 1);
                Assert.IsTrue(successor.NearPrepared, "L0 must already allow handover in this fixture.");
                Assert.IsTrue(successor.HasPendingCoarseTerrain);
                Assert.AreSame(successor.Layout, waiting.Node, "NearPrepared must activate the successor without waiting for L2.");
                var bounds = successor.Layout!.Bounds;
                var points = OutsideWitnesses(oldView, bounds);
                Assert.IsTrue(points.Any(point => Math.Abs(point.X) < TerrainTile.ParentCells &&
                    Math.Abs(point.Z) < TerrainTile.ParentCells),
                    "Witnesses must include old parents intersecting the new node layout.");
                if (!late) AssertDisplayed(displayed, points);
                foreach (var tile in oldView.Tiles)
                    Assert.IsTrue(terrain.View.Tiles.Contains(tile),
                        "Handover must inherit the same old tile, including tiles completed after successor binding.");
                AssertDisplayed(waiting, points);
                displayed = waiting;
                // The preparation gate was released only while both publication gates were held,
                // so the successor worker has never admitted a job and cannot race this loop.
                for (var step = 0; step < 20000 && terrain.HasPendingCoarse; step++)
                {
                    terrain.Advance(128, CancellationToken.None);
                    displayed = Present(session, displayed, radius: 1);
                    AssertDisplayed(displayed, points);
                }
                Assert.IsFalse(terrain.HasPendingCoarse, "Replacement L2 terrain must finish within the bounded fixture.");
                Assert.IsFalse(terrain.View.Tiles.Any(oldView.Tiles.Contains),
                    "The changed parent must eventually replace the inherited tiles.");
                displayed = Present(session, displayed, radius: 1);
                AssertDisplayed(displayed, points);
            }
            Assert.AreNotSame(oldNode.Layout, displayed.Node);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Railway_rebinding_keeps_displayed_terrain_through_break_and_repair_frames()
    {
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0);
        await using var vehicles = new SceneVehicles();
        await using var scene = new RailwayScene(snapshot, RailwayFixtures.Edge, new(), SceneFixtures.NextId(),
            vehicles, new(), _ => null, RailwayFixtures.EmptySegment);
        scene.SetFarBuildAllowed(false);
        scene.SetFineTerrainBuildAllowed(false);
        scene.SetTerrainViewRadius(1024);
        var target = new WorldSceneTarget.Location(RailwayFixtures.Location);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var displayed = await RailwayFixtures.Settled(scene, snapshot, target, request, stamp);
        var terrain = Field<FarTerrain>(scene, "farTerrain");
        while (terrain.HasRunnableWork) terrain.Advance(65536, CancellationToken.None);
        displayed = scene.Present(snapshot, null, target, request, stamp, displayed);
        var tiles = terrain.View.Tiles.ToArray();
        var points = tiles.Select(tile => new ScenePoint(tile.Origin.X + tile.SizeCells / 2,
            0, tile.Origin.Z + tile.SizeCells / 2)).ToArray();
        Assert.IsTrue(points.Length > 0);
        AssertDisplayed(displayed, points);
        var broken = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with
            { Breaks = [new(new(1), 1, 32)] });
        var repairing = RailwayFixtures.ReplaceCore(broken, edge: broken.Core.World.Edges[0] with
            { Works = [new(new(1), RailwayWorkKind.RepairBreak, 1, new(1), new(1), [], .25, [])] });
        var repaired = RailwayFixtures.ReplaceCore(repairing, edge: repairing.Core.World.Edges[0] with
            { Breaks = [], Works = [] });
        foreach (var changed in new[] { broken, repairing, repaired })
        {
            stamp = stamp with { Revision = stamp.Revision + 1 };
            displayed = scene.Present(changed, snapshot, target, request, stamp, displayed);
            AssertDisplayed(displayed, points);
            foreach (var tile in tiles)
                Assert.IsTrue(terrain.View.Tiles.Contains(tile), "A break or repair without flatten must preserve the terrain tile.");
            for (var step = 0; step < 20000 && terrain.HasRunnableWork; step++)
            {
                terrain.Advance(65536, CancellationToken.None);
                displayed = scene.Present(changed, snapshot, target, request, stamp, displayed);
                AssertDisplayed(displayed, points);
            }
            Assert.IsFalse(terrain.HasRunnableWork);
            snapshot = changed;
        }
    }

    [TestMethod]
    public async Task Visible_far_terrain_removes_fog_outside_the_required_L0_window()
    {
        var layout = NodeFixtures.Layout with
        {
            Plots = [],
            SceneModel = NodeFixtures.Layout.SceneModel with
            {
                Scene = SceneFixtures.Projection([]) with
                { OccupiedBounds = new(new(0, 0, 0), new(255, 0, 15)) }
            }
        };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, []);
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), SceneFixtures.NextId(),
            vehicles, new(), _ => null,
            buildChunk: (data, from, old, token) => new(data.ChunkInputs(from),
                WorldSceneSession.BuildGeometry(_ => new([], [], [])),
                System.Collections.Immutable.ImmutableDictionary<GridColumn, double>.Empty));
        scene.SetFarBuildAllowed(false);
        scene.SetFineTerrainBuildAllowed(false);
        scene.SetTerrainViewRadius(1);
        await scene.BuildWork!;
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var before = scene.Present(snapshot, request, stamp, null, default);
        const float x = 240.5f, z = 8.5f;
        Assert.IsInstanceOfType<Lookup<double>.Missing>(before.TerrainHeight(x, z));
        Assert.IsTrue(FogCovers(before), "Unbuilt static L0 terrain must remain fogged before far terrain appears.");

        var terrain = Field<FarTerrain>(scene, "farTerrain");
        while (terrain.HasPendingCoarse) terrain.Advance(128, CancellationToken.None);
        var after = scene.Present(snapshot, request, stamp with { Revision = 2 }, before, default);

        Assert.IsInstanceOfType<Lookup<double>.Missing>(after.TerrainHeight(x, z),
            "The far terrain must not turn the unloaded L0 chunk into precise geometry.");
        Assert.IsInstanceOfType<Lookup<double>.Found>(after.DisplayTerrainHeight(x, z));
        Assert.IsFalse(FogCovers(after), "Visible far terrain must replace fog outside the required L0 window.");
        Assert.IsTrue(FogCovers(before), "An older immutable frame must retain its own fog.");

        static bool FogCovers(SceneFrame frame) => frame.Update.Environment.FoggedRegions.Any(box =>
            x >= box.Minimum.X && x <= box.Maximum.X && z >= box.Minimum.Z && z <= box.Maximum.Z);
    }

    private static ScenePoint[] OutsideWitnesses(FarTerrainView view, ScenePanBounds bounds) => view.Tiles
        .SelectMany(tile => new[] { .25, .75 }.SelectMany(x => new[] { .25, .75 }.Select(z =>
            new ScenePoint(tile.Origin.X + tile.SizeCells * x, 0, tile.Origin.Z + tile.SizeCells * z))))
        .Where(point => point.X < bounds.Minimum.X || point.X > bounds.Maximum.X ||
            point.Z < bounds.Minimum.Y || point.Z > bounds.Maximum.Y).ToArray();

    private static void AssertDisplayed(SceneFrame frame, IEnumerable<ScenePoint> points)
    {
        foreach (var point in points)
            Assert.IsInstanceOfType<Lookup<double>.Found>(frame.DisplayTerrainHeight(
                point.X - frame.Origin.X, point.Z - frame.Origin.Z), $"Displayed terrain disappeared at {point}.");
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static SceneFrame Present(WorldSceneSession session, SceneFrame displayed, double? radius = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(
            new(1, radius is { } extent ? new(displayed, Vector3.Zero, extent) : null, SceneDebugLayer.None), displayed)).Value;

}

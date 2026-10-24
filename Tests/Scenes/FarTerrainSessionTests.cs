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
    public async Task Founded_successor_keeps_old_far_terrain_until_its_coarse_terrain_is_ready()
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
            oldNode.SetFineTerrainBuildAllowed(false);
            oldNode.SetTerrainViewRadius(1);
            oldNode.Pump(empty);
            var oldTerrain = Field<FarTerrain>(oldNode, "farTerrain");
            while (oldTerrain.HasPendingCoarse) oldTerrain.Advance(128, CancellationToken.None);
            displayed = Present(session, displayed, radius: 1);
            Assert.IsFalse(oldTerrain.View.Tiles.IsEmpty);
            var tile = oldTerrain.View.Tiles[0];
            var point = new Vector3((float)(tile.Origin.X + 512 - displayed.Origin.X), 0,
                (float)(tile.Origin.Z + 512 - displayed.Origin.Z));
            Assert.IsInstanceOfType<Lookup<double>.Found>(displayed.DisplayTerrainHeight(point.X, point.Z));

            session.Observe(founded, []);
            displayed = Present(session, displayed, radius: 1);
            await started.Task;
            var successor = Field<NodeScene>(session, "successor");
            var terrain = Field<FarTerrain>(successor, "farTerrain");
            lock (Field<object>(terrain, "gate"))
            {
                release.Set();
                successor.BuildWork!.GetAwaiter().GetResult();
                var waiting = Present(session, displayed, radius: 1);
                Assert.IsTrue(successor.NearPrepared, "L0 must already allow handover in this fixture.");
                Assert.IsTrue(successor.HasPendingCoarseTerrain);
                Assert.AreSame(displayed.Node, waiting.Node, "L0 readiness alone must not activate the successor.");
                Assert.IsInstanceOfType<Lookup<double>.Found>(waiting.DisplayTerrainHeight(point.X, point.Z));
                displayed = waiting;
            }
            for (var attempt = 0; attempt < 100 && ReferenceEquals(displayed.Node, oldNode.Layout); attempt++)
            {
                await SceneFixtures.AwaitPendingWork(successor.PendingFarWork);
                displayed = Present(session, displayed, radius: 1);
            }
            Assert.AreNotSame(oldNode.Layout, displayed.Node);
            Assert.IsFalse(successor.HasPendingCoarseTerrain);
            Assert.IsInstanceOfType<Lookup<double>.Found>(displayed.DisplayTerrainHeight(point.X, point.Z));
        }
        finally { release.Set(); }
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

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static SceneFrame Present(WorldSceneSession session, SceneFrame displayed, double? radius = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(
            new(1, radius is { } extent ? new(displayed, Vector3.Zero, extent) : null, SceneDebugLayer.None), displayed)).Value;

}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodePrebuildTests
{
    [TestMethod]
    public async Task A_pending_node_candidate_does_not_stop_the_active_railway_display_range()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var train = RailwayFixtures.Train(280);
        var initial = RailwayFixtures.Snapshot(lengthMeters: 449, directMeters: 449, relief: 0,
            content: content, trains: [train], instances: [RailwayFixtures.Instance()]);
        var path = new RailwayPath(initial, RailwayFixtures.Edge, default);
        var middle = (int)(280 * RailwayCoordinates.CellsPerMeter / RailwayCoordinates.SegmentLengthCells);
        var radius = RailwayCoordinates.ViewRadiusSegments;
        var cache = new RailwayCache();
        cache.Publish(RailwayFixtures.Edge, cache.OpenSession(RailwayFixtures.Edge),
            Enumerable.Range(middle - radius, 2 * radius + 1)
                .Select(index => RailwayFixtures.EmptySegment(initial, path, index, default)));
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), initial, cache, new(),
            (current, node, previous, token) =>
            {
                entered.TrySetResult();
                release.Task.Wait(token);
                return RailwayFixtures.EmptyNode(current, node, previous, token);
            });
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(train.Id, new(1)));
            var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
            var displayed = await RailwayFixtures.Settled(session, request);
            await entered.Task;
            Assert.AreEqual(SceneReadiness.Ready, displayed.Readiness);
            var building = session.PendingPrebuilds;
            Assert.IsTrue(building.Any(work => !work.IsCompleted));

            // The newly needed final metre lies beyond focal ±64 m while the node builder remains blocked.
            var moved = train with { Position = ((TrainPosition.Edge)train.Position) with { DistanceMeters = 340 } };
            session.Observe(RailwayFixtures.Snapshot(lengthMeters: 449, directMeters: 449, relief: 0, content: content, steps: 1,
                trains: [moved], instances: [RailwayFixtures.Instance()]), []);
            var active = await RailwayFixtures.Settled(session, request, displayed);
            Assert.AreEqual(SceneReadiness.Ready, active.Readiness,
                "The full railway display range must still receive build slots during candidate preparation.");
            Assert.IsTrue(building.Any(work => !work.IsCompleted), "The test must not rely on the candidate completing.");
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task A_known_front_node_starts_prebuilding_before_the_last_prefetch_range()
    {
        var train = RailwayFixtures.Train(400, direction: TravelDirection.Reverse, speed: 10);
        var snapshot = RailwayFixtures.Snapshot(content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle()),
            trains: [train], instances: [RailwayFixtures.Instance()]);
        var oldThresholdCells = RailwayScene.PrefetchSegments(train.SpeedMetersPerSecond, 1) * RailwayCoordinates.SegmentLengthCells;
        Assert.IsTrue(((TrainPosition.Edge)train.Position).DistanceMeters * RailwayCoordinates.CellsPerMeter > oldThresholdCells,
            "The train must remain outside the former final-prefetch trigger range.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new(),
            (current, node, previous, token) =>
            {
                entered.TrySetResult();
                return RailwayFixtures.EmptyNode(current, node, previous, token);
            });
        session.RequestView(new WorldSceneTarget.Follow(train.Id, new(1)));
        var frame = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.AreNotEqual(SceneReadiness.Loading, frame.Readiness);
        Assert.AreEqual(new WorldLocation.Node(new(1)), session.PrebuiltLocation,
            "Once the active display is prepared, its known front node must start without reaching the old trigger range.");
        await entered.Task;
    }

    [TestMethod]
    public async Task A_vehicle_that_leaves_the_scene_without_a_ready_body_does_not_block_follow_prebuilding()
    {
        using var content = new SceneFixtures.BlockedAppearanceView(RailwayFixtures.Content(
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }, design: RailwayFixtures.Vehicle()));
        var followed = RailwayFixtures.Train(0, direction: TravelDirection.Reverse, speed: 10);
        var second = RailwayFixtures.Train(1, vehicles: [new(2)], trainId: 2);
        var leaving = RailwayFixtures.Train(2, vehicles: [new(3)], trainId: 3);
        var instances = new[] { RailwayFixtures.Instance(), RailwayFixtures.Instance(2), RailwayFixtures.Instance(3) };
        var initial = RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [followed, second, leaving], instances: [.. instances]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), initial,
            new(), new(), RailwayFixtures.EmptyNode);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            var first = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(request, null)).Value;
            await content.Entered.Task;
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, session.PendingObjectBuilds.Length);
            Assert.IsTrue(first.Objects.Any(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(3))) &&
                item.Visuals.IsEmpty), "The third visible vehicle must wait for a body build slot.");
            Assert.IsNull(session.PrebuiltLocation);

            var moved = leaving with
            {
                Position = new TrainPosition.Node(new(2), RailwayFixtures.Edge, 1, null, null, NodeTravelPhase.Approaching, 0, null)
            };
            var current = RailwayFixtures.Snapshot(4, 4, 0, steps: 1, content: content,
                trains: [followed, second, moved], instances: [.. instances]);
            session.Observe(current, []);
            content.Release.Set();
            var ready = await RailwayFixtures.Settled(session, request);

            Assert.AreEqual(SceneReadiness.Ready, ready.Readiness);
            Assert.IsInstanceOfType<Lookup<InstanceView>.Found>(current.Core.FindInstance(new InstanceId.Vehicle(new(3))));
            Assert.AreEqual(new WorldLocation.Node(new(1)), session.PrebuiltLocation,
                "A living vehicle outside the current scene must not leave a stale unstarted body request blocking the candidate.");
        }
        finally { content.Release.Set(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Current_scene_builds_prevent_starting_a_follow_candidate(bool atNode)
    {
        using var content = new SceneFixtures.BlockedAppearanceView(NodeFixtures.Content(
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        var snapshot = atNode
            ? NodeFixtures.Snapshot(platforms: 0, trains: [NodeFixtures.Train(nextEdge: new(1), speed: 10)], content: content)
            : RailwayFixtures.Snapshot(4, 4, 0, content: content,
                trains: [RailwayFixtures.Train(0, direction: TravelDirection.Reverse, speed: 10)],
                instances: [RailwayFixtures.Instance()]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot,
            new(), new(), RailwayFixtures.EmptyNode);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            if (atNode) await session.PendingBuild!;
            session.Present(request, null);
            await content.Entered.Task;
            Assert.IsNull(session.PrebuiltLocation);
            Assert.AreEqual(0, session.PendingPrebuilds.Length);
            content.Release.Set();
            await RailwayFixtures.Settled(session, request);
            WorldLocation expected = atNode ? new WorldLocation.Edge(new(1), 0) : new WorldLocation.Node(new(1));
            Assert.AreEqual(expected, session.PrebuiltLocation);
        }
        finally { content.Release.Set(); }
    }
}

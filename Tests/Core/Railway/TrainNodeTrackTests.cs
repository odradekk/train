using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainNodeTrackTests
{
    [TestMethod]
    public void Arrival_keeps_the_actual_second_track_separate_from_the_first_exit_track()
    {
        var state = State(count: 1, tracks: 2, content: Content());
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Edge(new(1), 2, TravelDirection.Reverse, .001),
            SpeedMetersPerSecond = 1, Activity = TrainActivity.Running,
            Plan = new(false, [new PlanStop.Station(new(1), 1, []), new PlanStop.Waypoint(new(2))])
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        Step(session);
        var arrived = (TrainPosition.Node)View(session, 1).Position;
        Assert.AreEqual(2, arrived.EnteredTrack);
        Assert.IsNull(arrived.NextTrack);
        Until(session, () => View(session, 1).Position is TrainPosition.Node { Phase: NodeTravelPhase.Departing });
        var departing = (TrainPosition.Node)View(session, 1).Position;
        Assert.AreEqual(2, departing.EnteredTrack);
        Assert.AreEqual(1, departing.NextTrack);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge);
        Assert.AreEqual(1, ((TrainPosition.Edge)View(session, 1).Position).Track);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void A_second_track_arrival_stops_or_passes_through_then_uses_the_second_exit_track(bool station)
    {
        var state = State(count: 2, tracks: 2, content: Content());
        var world = state.World;
        var train = world.Trains[0] with
        {
            Position = new TrainPosition.Edge(new(1), 2, TravelDirection.Reverse, .001),
            SpeedMetersPerSecond = 1, Activity = TrainActivity.Running,
            Plan = new(false, [station ? new PlanStop.Station(new(1), 1, []) : new PlanStop.Waypoint(new(1)),
                new PlanStop.Waypoint(new(3))])
        };
        var fixedDeparture = world.Trains[1] with
        {
            Position = new TrainPosition.Node(new(1), null, null, new(2), 1, NodeTravelPhase.Departing, 3, null),
            Plan = new(false, [new PlanStop.Waypoint(new(3))])
        };
        using var session = new CoreSession(state with { World = world with
        {
            Nodes = world.Nodes.Add(world.Nodes[1] with { Id = new(3), Position = new(10, 10) }),
            Edges = world.Edges.Add(world.Edges[0] with { Id = new(2), End = new(3) }),
            Trains = [train, fixedDeparture], Next = world.Next with { Node = 4, Edge = 3 }
        } });
        Step(session);
        Assert.AreEqual(2, ((TrainPosition.Node)View(session, 1).Position).EnteredTrack);
        if (station)
        {
            Until(session, () => View(session, 1).Activity == TrainActivity.Servicing);
            Assert.IsNull(((TrainPosition.Node)View(session, 1).Position).NextTrack);
        }
        Until(session, () => View(session, 1).Position is TrainPosition.Node { Phase: NodeTravelPhase.Departing });
        Assert.AreEqual(2, ((TrainPosition.Node)View(session, 1).Position).NextTrack);
        Assert.IsTrue(View(session, 1).SpeedMetersPerSecond > 0);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge { Id.Value: 2 });
        Assert.AreEqual(2, ((TrainPosition.Edge)View(session, 1).Position).Track);
    }

    [TestMethod]
    public void Opposite_fixed_departures_can_progress_after_both_occupied_tracks_clear()
    {
        var state = State(count: 4, tracks: 2) with { Position = new(5, 0) };
        var trains = state.World.Trains;
        var forward = trains[0] with
        {
            Position = new TrainPosition.Node(new(1), null, null, new(1), 1, NodeTravelPhase.Departing,
                WorldFixture.ApproachLength + WorldFixture.PlatformLength + WorldFixture.DepartureLength, null),
            Activity = TrainActivity.Waiting, QueueEnteredStep = 0
        };
        var reverse = trains[1] with
        {
            Position = new TrainPosition.Node(new(2), null, null, new(1), 2, NodeTravelPhase.Departing,
                WorldFixture.ApproachLength + WorldFixture.DepartureLength, null),
            Activity = TrainActivity.Waiting, QueueEnteredStep = 0,
            Plan = new(false, [new PlanStop.Waypoint(new(1))])
        };
        var clearingForward = trains[2] with
        {
            Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 19.999),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 1
        };
        var clearingReverse = trains[3] with
        {
            Position = new TrainPosition.Edge(new(1), 2, TravelDirection.Reverse, .001),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 1,
            Plan = new(false, [new PlanStop.Waypoint(new(1))])
        };
        using var session = new CoreSession(state with { World = state.World with
        { Trains = [forward, reverse, clearingForward, clearingReverse] } });
        var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        _ = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Step(session);
        Assert.IsTrue(session.State.World.TrackHandoffs.IsEmpty);
        for (var step = 0; step < 32; step++) Step(session);
        Assert.IsTrue(View(session, 1).Position is TrainPosition.Edge
            || View(session, 2).Position is TrainPosition.Edge, "两条轨均已清空，对向固定出轨的列车不能永久互等交让。");
    }

    [TestMethod]
    public void Starting_then_stopping_without_motion_does_not_reverse_the_vehicle_order()
    {
        var state = State(count: 1, tracks: 2);
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 2, new(1), null, NodeTravelPhase.Docked,
                WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2, null)
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        Submit(session, new CoreCommand.StartTrain(new(1)));
        Submit(session, new CoreCommand.StopTrain(new(1)));
        Assert.AreEqual(((TrainPosition.Node)train.Position).DistanceMeters, ((TrainPosition.Node)View(session, 1).Position).DistanceMeters);
        Assert.AreEqual(0.0, View(session, 1).SpeedMetersPerSecond);
        Assert.AreEqual(train.OrderFacing, View(session, 1).OrderFacing);
    }

    private static CoreContentProjection Content() => WorldFixture.Content with
    {
        RailwayRules = [WorldFixture.Content.RailwayRules[0] with { ApproachLengthMeters = 2, DepartureLengthMeters = 2 }]
    };
}

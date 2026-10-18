using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class NodeExitTrackTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void A_previous_steps_fixed_exit_uses_the_other_track_for_same_or_opposite_departures(bool opposite)
    {
        var state = State(2, 2, content: DepartureContent());
        var first = Exit(state.World.Trains[0], 1, 1);
        var node = opposite ? 2UL : 1UL;
        var second = Exit(state.World.Trains[1], node, null) with
        {
            Position = new TrainPosition.Node(new(node), null, null, new(1), null, NodeTravelPhase.Docked,
                2 + (node == 1 ? WorldFixture.PlatformLength / 2 : 0), null), Activity = TrainActivity.Running
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [first, second] } });
        Step(session);
        Assert.AreEqual(1, Node(session, 1).NextTrack);
        Assert.AreEqual(2, Node(session, 2).NextTrack);
        Step(session);
        Assert.AreEqual(2, Node(session, 2).NextTrack);
    }

    [TestMethod]
    public void When_all_tracks_have_exit_intents_the_smallest_open_track_is_the_fallback()
    {
        var state = State(3, 2, content: DepartureContent());
        var departing = Exit(state.World.Trains[2], 1, null) with
        {
            Position = new TrainPosition.Node(new(1), null, null, new(1), null, NodeTravelPhase.Docked,
                2 + WorldFixture.PlatformLength / 2, null), Activity = TrainActivity.Running
        };
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [Exit(state.World.Trains[0], 1, 1), Exit(state.World.Trains[1], 2, 2), departing] } });
        Step(session);
        Assert.AreEqual(1, Node(session, 3).NextTrack);
    }

    [TestMethod]
    public void Admission_uses_the_fixed_track_even_when_a_lower_track_is_empty()
    {
        var state = State(1, 2);
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [Exit(state.World.Trains[0], 1, 2) with { Activity = TrainActivity.Running }] } });
        Step(session);
        Assert.IsTrue(View(session, 1).Position is TrainPosition.Edge { Track: 2 });
    }

    [TestMethod]
    [DataRow("break", 1, 2)]
    [DataRow("repair", 1, 2)]
    [DataRow("removed", 2, 1)]
    public void An_unavailable_fixed_track_changes_to_an_open_track(string change, int previous, int expected)
    {
        var state = State(1, 2, content: DepartureContent());
        var edge = state.World.Edges[0];
        var settlement = state.World.Settlements[0].Id;
        ImmutableArray<RailwayWorkState> jobs = [];
        edge = change switch
        {
            "break" => edge with { Breaks = [new(new(1), 1, 10)] },
            "repair" => edge with { Breaks = [new(new(1), 1, 10)] },
            "removed" => edge with { Tracks = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        if (change == "repair")
            jobs = [new(new(1), edge.Id, RailwayWorkKind.RepairBreak, new(1), settlement, 0, 0, [],
                [new("insufficient_goods", "MaterialSource.Inventory", new WorldObjectId.Settlement(settlement), ["voxel:fixture:material"])])];
        var train = Exit(state.World.Trains[0], 1, previous) with { Activity = TrainActivity.Running, Position =
            new TrainPosition.Node(new(1), new(1), 2, new(1), previous, NodeTravelPhase.Departing,
                2 + WorldFixture.PlatformLength / 2, null) };
        using var session = new CoreSession(state with { World = state.World with { Edges = [edge], Trains = [train],
            RailwayJobs = jobs, Next = state.World.Next with { Break = 2, Job = jobs.IsEmpty ? state.World.Next.Job : 2 } } });
        Step(session);
        Assert.AreEqual(expected, Node(session, 1).NextTrack);
        Assert.AreEqual(2, Node(session, 1).EnteredTrack);
        if (change != "repair")
        {
            var capture = WorldFixture.Success(session.Capture(CapturePurpose.Save));
            var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
            Assert.AreEqual(capture.Encode(), decoded.Encode());
        }
    }

    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(1, 2, null)]
    [DataRow(1, null, null)]
    [DataRow(0, 2, null)]
    [DataRow(0, null, null)]
    public void With_no_open_track_a_present_old_track_is_retained_and_otherwise_the_choice_stays_empty(
        int tracks, int? previous, int? expected)
    {
        var state = State(1, tracks);
        var edge = state.World.Edges[0] with { Breaks = tracks == 0 ? [] : [new(new(1), 1, 10)] };
        using var session = new CoreSession(state with { World = state.World with { Edges = [edge],
            Trains = [Exit(state.World.Trains[0], 1, previous) with { Activity = TrainActivity.Waiting, QueueEnteredStep = 0 }],
            Next = state.World.Next with { Break = 2 } } });
        Step(session);
        Assert.AreEqual(expected, Node(session, 1).NextTrack);
        Step(session);
        Assert.AreEqual(expected, Node(session, 1).NextTrack);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode()));
        Assert.AreEqual(saved.Encode(), decoded.Encode());
        var closed = session.State;
        var reopened = closed with { World = closed.World with { Edges = [edge with { Tracks = 1, Breaks = [] }] } };
        using var open = new CoreSession(reopened);
        Step(open);
        Assert.IsTrue(View(open, 1).Position is TrainPosition.Edge { Track: 1 });
    }

    [TestMethod]
    public void A_handoff_on_another_fixed_track_does_not_leave_both_directions_waiting_forever()
    {
        var state = State(2, 2) with { Position = new(5, 0) };
        using var session = new CoreSession(state with { World = state.World with
        {
            TrackHandoffs = [new(new(1), 1, TravelDirection.Reverse)],
            Trains = [Exit(state.World.Trains[0], 1, 1) with { Activity = TrainActivity.Waiting, QueueEnteredStep = 0 },
                Exit(state.World.Trains[1], 2, 2) with { Activity = TrainActivity.Waiting, QueueEnteredStep = 1 }]
        } });
        Step(session);
        Assert.IsTrue(View(session, 2).Position is TrainPosition.Edge { Track: 2, Direction: TravelDirection.Reverse });
        Step(session);
        Assert.IsTrue(View(session, 1).Position is TrainPosition.Edge { Track: 1, Direction: TravelDirection.Forward });
    }

    private static TrainState Exit(TrainState train, ulong node, int? track) => train with
    {
        Position = new TrainPosition.Node(new(node), null, null, new(1), track, NodeTravelPhase.Departing,
            WorldFixture.ApproachLength + (node == 1 ? WorldFixture.PlatformLength : 0) + WorldFixture.DepartureLength, null),
        Activity = TrainActivity.Stopped,
        Plan = new(false, [new PlanStop.Waypoint(new(node == 1 ? 2UL : 1UL))])
    };

    private static TrainPosition.Node Node(ICoreSession session, ulong id) => (TrainPosition.Node)View(session, id).Position;

    private static CoreContentProjection DepartureContent() => WorldFixture.Content with
    { RailwayRules = [WorldFixture.Content.RailwayRules[0] with { ApproachLengthMeters = 2, DepartureLengthMeters = 2 }] };
}

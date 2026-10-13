using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Railway.TrainDispatchFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainDispatchTests
{
    [TestMethod]
    public void Older_same_direction_waiter_cannot_take_the_first_track_cleared_for_the_opposite_queue()
    {
        var state = State() with { Position = new(5, 0) };
        var trains = state.World.Trains;
        trains = [OnEdge(trains[0], 19.99, 2), Queued(trains[1], 1, 1, 2), Queued(trains[2], 2, 2, 1)];
        using var session = new CoreSession(state with { World = state.World with { Trains = trains } });
        Step(session);
        Assert.IsInstanceOfType<TrainPosition.Node>(View(session, 1).Position);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual(new TrackHandoff(new(1), 1, TravelDirection.Reverse), session.State.World.TrackHandoffs.Single());
        Step(session);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)View(session, 3).Position).Direction);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.IsNull(View(session, 2).NodeTransit!.NextEdge);
        Assert.AreEqual("track_wait", View(session, 2).Blockers.Single().Code);
        Assert.AreEqual(TravelDirection.Reverse, ((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Tracks.Single().DirectionLock);
        Assert.IsTrue(session.State.World.TrackHandoffs.IsEmpty);
        for (var step = 0; step < 1000 && View(session, 2).Position is TrainPosition.Node; step++) Step(session);
        Assert.IsInstanceOfType<TrainPosition.Edge>(View(session, 2).Position);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Same_step_requests_use_train_id_and_see_prior_grants_independent_of_list_order(int tracks)
    {
        foreach (var reversed in new[] { false, true })
        {
            var state = State(2, tracks);
            var trains = state.World.Trains.Select(t => t with { Activity = TrainActivity.Running }).ToImmutableArray();
            if (reversed) trains = trains.Reverse().ToImmutableArray();
            using var session = new CoreSession(state with { World = state.World with { Trains = trains } });
            Step(session);
            Assert.AreEqual(1, ((TrainPosition.Edge)View(session, 1).Position).Track);
            if (tracks == 1)
            {
                Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
                Assert.AreEqual(new TrainPosition.Node(new(1)), View(session, 2).Position);
            }
            else Assert.AreEqual(2, ((TrainPosition.Edge)View(session, 2).Position).Track);
        }
    }

    [TestMethod]
    public void Compatible_locked_track_is_chosen_before_a_lower_numbered_empty_track()
    {
        var state = State(2, 2);
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(state.World.Trains[0], 4, 1, track: 2), state.World.Trains[1] with { Activity = TrainActivity.Running }] } });
        Step(session);
        Assert.AreEqual(2, ((TrainPosition.Edge)View(session, 2).Position).Track);
    }

    [TestMethod]
    public void Opposite_waiting_also_closes_the_same_direction_track_with_enough_entry_space()
    {
        var state = State() with { Position = new(5, 0) };
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(state.World.Trains[0], 8, 2), Queued(state.World.Trains[1], 1, 1, 2), Queued(state.World.Trains[2], 2, 2, 1)] } });
        Step(session);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 3).Activity);
    }

    [TestMethod]
    public void Every_committed_following_step_preserves_center_spacing_and_order_in_both_directions()
    {
        foreach (var reverse in new[] { false, true })
        {
            var state = State(2, length: 100);
            var first = OnEdge(state.World.Trains[0], reverse ? 60 : 40, 1, reverse);
            var second = OnEdge(state.World.Trains[1], reverse ? 70 : 30, 8, reverse);
            using var session = new CoreSession(state with { World = state.World with { Trains = [first, second] } });
            var gap = (View(session, 1).LengthMeters + View(session, 2).LengthMeters) / 2 + WorldFixture.Content.RailwayRules[0].MinimumGapMeters;
            for (var step = 0; step < 1200; step++)
            {
                Step(session);
                if (View(session, 1).Position is not TrainPosition.Edge a || View(session, 2).Position is not TrainPosition.Edge b) continue;
                Assert.IsTrue((reverse ? b.DistanceMeters - a.DistanceMeters : a.DistanceMeters - b.DistanceMeters) >= gap,
                    $"第 {step} 步：{a.DistanceMeters:R}，{b.DistanceMeters:R}；要求 {gap:R}");
            }
        }
    }

    [TestMethod]
    public void Waypoint_conflict_forces_zero_speed_at_the_node_and_reports_waiting_once()
    {
        var state = State(2);
        var arriving = OnEdge(state.World.Trains[0], 19.99, 2) with { Plan = Plan(1) };
        using var session = new CoreSession(state with { World = state.World with { Trains = [arriving, OnEdge(state.World.Trains[1], 10, 1)] } });
        var result = session.Advance(1);
        var train = View(session, 1);
        Assert.AreEqual(new TrainPosition.Node(new(2)), train.Position);
        Assert.AreEqual(0.0, train.SpeedMetersPerSecond);
        Assert.AreEqual(TrainActivity.Waiting, train.Activity);
        Assert.IsNull(train.NodeTransit!.NextEdge);
        var changed = result.Events.Select(e => e.Event).OfType<CoreEvent.TrainActivityChanged>().Single(e => e.Train.Value == 1);
        Assert.AreEqual("track_wait", changed.Reasons.Single().Code);
        Assert.IsFalse(session.Advance(1).Events.Any(e => e.Event is CoreEvent.TrainActivityChanged c && c.Train.Value == 1));
    }

    [TestMethod]
    public void Platform_queue_starts_dwell_only_on_admission_and_release_is_available_next_step()
    {
        using var session = StationSession();
        Step(session);
        Assert.AreEqual(2UL, View(session, 1).RemainingDwellSteps);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual("platform_wait", View(session, 2).Blockers.Single().Code);
        Assert.AreEqual(1, session.Read().World.Nodes[0].OccupiedPlatforms);
        Assert.AreEqual(0, session.State.World.Nodes[0].OccupiedPlatforms);
        Step(session); Step(session);
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Step(session);
        Assert.AreEqual(TrainActivity.Servicing, View(session, 2).Activity);
        Assert.AreEqual(2UL, View(session, 2).RemainingDwellSteps);
    }

    [TestMethod]
    public void Stopping_releases_platform_and_resuming_requeues_with_remaining_dwell()
    {
        using var session = StationSession();
        Step(session); Step(session);
        Submit(session, new CoreCommand.StopTrain(new(1)));
        Assert.AreEqual(1UL, View(session, 1).RemainingDwellSteps);
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        Submit(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.Waiting, View(session, 1).Activity);
        Assert.AreEqual(2UL, session.State.World.Trains[0].QueueEnteredStep);
        Step(session);
        Assert.AreEqual(TrainActivity.Servicing, View(session, 2).Activity);
        Submit(session, new CoreCommand.StopTrain(new(1)));
        Assert.IsNull(session.State.World.Trains[0].QueueEnteredStep);
        Submit(session, new CoreCommand.StopTrain(new(2)));
        Submit(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.Servicing, View(session, 1).Activity);
        Assert.AreEqual(1UL, View(session, 1).RemainingDwellSteps);
    }

    [TestMethod]
    public void Replacing_plan_and_disbanding_release_the_platform()
    {
        using var session = StationSession();
        Step(session);
        Submit(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        Step(session);
        Assert.AreEqual(TrainActivity.Servicing, View(session, 2).Activity);
        Submit(session, new CoreCommand.DisbandTrain(new(2)));
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
    }

    [TestMethod]
    public void Reducing_platform_count_preserves_current_service_but_blocks_new_admission()
    {
        using var session = StationSession();
        Step(session);
        var changed = WorldFixture.Content with { Identity = new(new string('E', 64)), Designs = WorldFixture.Content.Designs
            .Select(d => d.Design == WorldFixture.Station ? d with { Parts = d.Parts.Select(p => p with { Capabilities = [] }).ToImmutableArray() } : d).ToImmutableArray() };
        Submit(session, new CoreCommand.PublishContent(changed));
        Assert.AreEqual(0, session.Read().World.Nodes[0].Platforms);
        Assert.AreEqual(1, session.Read().World.Nodes[0].OccupiedPlatforms);
        Step(session); Step(session); Step(session);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        Submit(session, new CoreCommand.PublishContent(WorldFixture.Content));
        Step(session);
        Assert.AreEqual(TrainActivity.Servicing, View(session, 2).Activity);
    }

    [TestMethod]
    public void Break_turnaround_waits_for_the_unaffected_front_train_and_reverses_the_whole_stopped_group()
    {
        var state = State();
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(state.World.Trains[0], 19.99, 2), OnEdge(state.World.Trains[1], 10, 2), OnEdge(state.World.Trains[2], 6, 2)] } });
        Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 11));
        for (var step = 0; step < 1000 && session.State.World.Trains[1].ReturningFromBreak is null; step++) Step(session);
        Assert.IsInstanceOfType<TrainPosition.Node>(View(session, 1).Position);
        foreach (var id in new ulong[] { 2, 3 })
        {
            Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)View(session, id).Position).Direction);
            Assert.AreEqual(TrainOrderFacing.LastEndLeading, View(session, id).OrderFacing);
            Assert.AreEqual(new BreakId(1), session.State.World.Trains[(int)id - 1].ReturningFromBreak);
            Assert.AreEqual(0.0, View(session, id).SpeedMetersPerSecond);
        }
        Assert.IsTrue(((TrainPosition.Edge)View(session, 2).Position).DistanceMeters
            > ((TrainPosition.Edge)View(session, 3).Position).DistanceMeters);
        for (var step = 0; step < 1000 && session.State.World.Trains.Any(t => t.ReturningFromBreak is not null); step++) Step(session);
        foreach (var id in new ulong[] { 2, 3 })
        {
            Assert.AreEqual(new TrainPosition.Node(new(1)), View(session, id).Position);
            Assert.AreEqual(TrainActivity.NoRoute, View(session, id).Activity);
            Assert.IsNull(session.State.World.Trains[(int)id - 1].ReturningFromBreak);
        }
    }

    [TestMethod]
    [DataRow(10.0)]
    [DataRow(10.001)]
    public void Immediate_break_stops_at_its_center_and_zero_traction_does_not_repeat_the_turn(double distance)
    {
        var state = State(1);
        using var session = new CoreSession(state with { World = state.World with { Trains = [OnEdge(state.World.Trains[0], 10, 8)] } });
        var broken = Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, distance));
        Assert.IsInstanceOfType<CoreEvent.RailBroken>(broken.Events.Single().Event);
        Step(session);
        Assert.AreEqual(distance, ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)View(session, 1).Position).Direction);
        var unpowered = WorldFixture.Content with { Identity = new(new string('E', 64)), Designs = WorldFixture.Content.Designs.Select(d =>
            d.Design == WorldFixture.Vehicle ? d with { Parts = d.Parts.Select(p => p with { Capabilities = [new PartCapability.Control()] }).ToImmutableArray() } : d).ToImmutableArray() };
        Submit(session, new CoreCommand.PublishContent(unpowered));
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), unpowered, WorldFixture.Options));
        using var restored = candidate.Activate();
        session.Advance(5);
        restored.Advance(5);
        Assert.AreEqual(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Checkpoint,
            WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Checkpoint);
        Assert.AreEqual(distance, ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, View(session, 1).OrderFacing);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)View(session, 1).Position).Direction);
    }

    [TestMethod]
    public void Another_break_on_return_stops_the_train_without_turning_again()
    {
        var state = State(1);
        using var session = new CoreSession(state with { World = state.World with { Trains = [OnEdge(state.World.Trains[0], 10, 8)] } });
        Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 10)); Step(session);
        Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 9.99));
        for (var step = 0; step < 30; step++) Step(session);
        Assert.AreEqual(9.99, ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters);
        Assert.AreEqual(0.0, View(session, 1).SpeedMetersPerSecond);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)View(session, 1).Position).Direction);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, View(session, 1).OrderFacing);
    }

    [TestMethod]
    [DataRow(2UL, 1, 0.0, "object_missing", "Edge")]
    [DataRow(1UL, 2, 0.0, "invalid_state", "Break")]
    [DataRow(1UL, 1, 21.0, "invalid_state", "Break")]
    public void Invalid_world_break_requests_are_recorded_without_changing_world(ulong edge, int track, double distance, string code, string field)
    {
        using var session = new CoreSession(State(1));
        var before = session.State;
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.DebugAddRailBreak(new(edge), track, distance), CommandSource.AutomationTest);
        var problem = ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
        Assert.AreEqual(code, problem.Code); Assert.AreEqual(field, problem.Field);
        Assert.AreSame(before.World, session.State.World);
        Assert.AreEqual(before.NextCommandNumber + 1, session.State.NextCommandNumber);
        Assert.AreEqual(result.Receipt, session.ReadRecording().Commands.Single().Result);
    }

    private static CoreSession StationSession()
    {
        var state = State(2);
        return new(state with { World = state.World with { Trains = state.World.Trains.Select(t => t with
        { Activity = TrainActivity.Running, Plan = new(false, [new PlanStop.Station(new(1), 2, []), new PlanStop.Waypoint(new(2))]) }).ToImmutableArray() } });
    }
    private static RunPlan Plan(ulong node) => new(false, [new PlanStop.Waypoint(new(node))]);
    private static TrainState Queued(TrainState train, ulong node, ulong age, ulong target) => train with
    { Position = new TrainPosition.Node(new(node)), Activity = TrainActivity.Waiting, QueueEnteredStep = age, Plan = Plan(target) };
    private static TrainState OnEdge(TrainState train, double distance, double speed, bool reverse = false, int track = 1) => train with
    { Position = new TrainPosition.Edge(new(1), track, reverse ? TravelDirection.Reverse : TravelDirection.Forward, distance),
        Transit = null, Activity = TrainActivity.Running, SpeedMetersPerSecond = speed, Plan = Plan(reverse ? 1UL : 2UL) };
}

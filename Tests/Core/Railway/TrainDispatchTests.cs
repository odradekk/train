using System.Collections.Immutable;
using System.Text.Json;
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
        Assert.AreEqual(new EdgeId(1), ((TrainPosition.Node)View(session, 2).Position).NextEdge);
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
                Assert.AreEqual(WorldFixture.Node(new(1), NodeTravelPhase.Departing,
                    WorldFixture.ApproachLength + WorldFixture.PlatformLength + WorldFixture.DepartureLength, nextEdge: new(1)), View(session, 2).Position);
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
    [DataRow(1)]
    [DataRow(2)]
    public void Moving_arrival_needs_braking_room_to_join_a_locked_track_or_uses_an_empty_track(int tracks)
    {
        var state = TwoEdges(2, tracks, 30);
        var leading = OnEdge(state.World.Trains[0], 1.5, 0) with
        { Position = new TrainPosition.Edge(new(2), 1, TravelDirection.Forward, 1.5), Plan = Plan(3) };
        var arriving = OnEdge(state.World.Trains[1], 29.95, 8) with { Plan = Plan(3) };
        using var session = new CoreSession(state with { World = state.World with { Trains = [leading, arriving] } });
        Step(session);
        Assert.AreEqual(WorldFixture.Node(new(2), NodeTravelPhase.Approaching, 0, enteredFrom: new(1), nextEdge: new(2)), View(session, 2).Position);
        Assert.AreEqual(TrainActivity.Running, View(session, 2).Activity);
        if (tracks == 2) Assert.IsTrue(View(session, 2).SpeedMetersPerSecond > 7);
        double availableBrakingDistance = 0;
        for (var step = 0; step < 1000 && View(session, 2).Position is TrainPosition.Node; step++)
        {
            var leader = View(session, 1);
            var minimumGap = (leader.LengthMeters + View(session, 2).LengthMeters) / 2
                + session.State.Content.RailwayRules[0].MinimumGapMeters;
            if (leader.Position is TrainPosition.Edge edge)
                availableBrakingDistance = edge.DistanceMeters - minimumGap;
            Step(session);
        }
        var position = (TrainPosition.Edge)View(session, 2).Position;
        Assert.AreEqual(new EdgeId(2), position.Id);
        Assert.AreEqual(tracks, position.Track);
        if (tracks == 2) Assert.IsTrue(View(session, 2).SpeedMetersPerSecond > 0);
        if (tracks == 1)
        {
            var speed = View(session, 2).SpeedMetersPerSecond;
            var braking = session.State.Content.RailwayRules[0].BrakingDeceleration;
            Assert.IsTrue(availableBrakingDistance + 1e-12 >= speed * speed / (2 * braking),
                "同向已占用铁轨的入口必须留足当前速度的制动距离。");
        }
    }

    [TestMethod]
    public void Multi_train_running_without_breaks_obeys_the_braking_rate_on_every_committed_track_step()
    {
        var state = TwoEdges(4, 1, 5);
        state = state with { World = state.World with { Trains = state.World.Trains.Select(t => t with
        { Activity = TrainActivity.Running, Plan = Plan(3) }).ToImmutableArray() } };
        WorldStateValidation.Validate(state.World);
        using var session = new CoreSession(state);
        var maximumDrop = WorldFixture.Content.RailwayRules[0].BrakingDeceleration * SimulationTime.StepSeconds;
        var deceleratingSteps = 0;
        for (var step = 0; step < 1000; step++)
        {
            var before = session.Read().World.Trains.ToDictionary(t => t.Id, t => View(session, t.Id.Value));
            Step(session);
            foreach (var train in session.Read().World.Trains.Select(t => View(session, t.Id.Value)))
                if (before[train.Id].Position is TrainPosition.Edge a && train.Position is TrainPosition.Edge b
                    && a.Id == b.Id && a.Track == b.Track)
                {
                    var drop = before[train.Id].SpeedMetersPerSecond - train.SpeedMetersPerSecond;
                    if (drop > 0) deceleratingSteps++;
                    Assert.IsTrue(drop <= maximumDrop + 1e-9, $"第 {step} 步，列车 {train.Id.Value} 减速 {drop:R}");
                }
        }
        Assert.IsTrue(deceleratingSteps > 0);
        Assert.IsTrue(session.Read().World.Trains.All(t => t.Position == WorldFixture.Node(new(3), NodeTravelPhase.Docked, WorldFixture.ApproachLength, enteredFrom: new(2))));
    }

    [TestMethod]
    public void Increasing_gap_keeps_the_follower_still_until_the_leader_opens_space()
    {
        var state = State();
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(state.World.Trains[0], 1.5, 1), OnEdge(state.World.Trains[1], 0, 8),
                state.World.Trains[2] with { Activity = TrainActivity.Running }] } });
        var before = session.State.World.Trains.Select(t => t.Position).ToArray();
        var changed = WorldFixture.Content with { Identity = new(new string('E', 64)), RailwayRules =
            [WorldFixture.Content.RailwayRules[0] with { MinimumGapMeters = 2 }] };
        Submit(session, new CoreCommand.PublishContent(changed));
        CollectionAssert.AreEqual(before, session.State.World.Trains.Select(t => t.Position).ToArray());
        var required = (View(session, 1).LengthMeters + View(session, 2).LengthMeters) / 2 + 2;
        var previousGap = 1.5;
        var resumed = false;
        for (var step = 0; step < 200; step++)
        {
            var leading = ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters;
            var following = ((TrainPosition.Edge)View(session, 2).Position).DistanceMeters;
            Step(session);
            var after = ((TrainPosition.Edge)View(session, 2).Position).DistanceMeters;
            var gap = ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters - after;
            if (leading - following < required)
            {
                Assert.AreEqual(following, after);
                Assert.AreEqual(0.0, View(session, 2).SpeedMetersPerSecond);
                Assert.IsTrue(gap >= previousGap);
                var departure = (TrainPosition.Node)View(session, 3).Position;
                Assert.AreEqual(new NodeId(1), departure.Id);
                Assert.AreEqual(NodeTravelPhase.Departing, departure.Phase);
                Assert.AreEqual(new EdgeId(1), departure.NextEdge);
                Assert.IsTrue(departure.DistanceMeters <= WorldFixture.ApproachLength + WorldFixture.PlatformLength + WorldFixture.DepartureLength);
            }
            else if (after > following)
            {
                Assert.IsTrue(gap >= required);
                Assert.IsTrue(View(session, 2).SpeedMetersPerSecond > 0);
                resumed = true;
                break;
            }
            previousGap = gap;
        }
        Assert.IsTrue(resumed, "前车拉开间距后，后车应恢复跟车。");
    }

    [TestMethod]
    public void Waypoint_conflict_forces_zero_speed_at_the_node_and_reports_waiting_once()
    {
        var state = State(2);
        var arriving = OnEdge(state.World.Trains[0], 19.99, 2) with { Plan = Plan(1) };
        using var session = new CoreSession(state with { World = state.World with { Trains = [arriving, OnEdge(state.World.Trains[1], 10, 1)] } });
        var events = new List<CoreEvent>();
        for (var step = 0; step < 1000 && View(session, 1).Activity != TrainActivity.Waiting; step++)
            events.AddRange(session.Advance(1).Events.Select(e => e.Event));
        var train = View(session, 1);
        Assert.AreEqual(WorldFixture.Node(new(2), NodeTravelPhase.Departing,
            WorldFixture.ApproachLength + WorldFixture.DepartureLength, enteredFrom: new(1), nextEdge: new(1)), train.Position);
        Assert.AreEqual(0.0, train.SpeedMetersPerSecond);
        Assert.AreEqual(TrainActivity.Waiting, train.Activity);
        Assert.AreEqual(new EdgeId(1), ((TrainPosition.Node)train.Position).NextEdge);
        var changed = events.OfType<CoreEvent.TrainActivityChanged>().Single(e => e.Train.Value == 1);
        Assert.AreEqual("track_wait", changed.Reasons.Single().Code);
        Assert.IsFalse(session.Advance(1).Events.Any(e => e.Event is CoreEvent.TrainActivityChanged c && c.Train.Value == 1));
    }

    [TestMethod]
    public void Platform_queue_starts_dwell_only_on_admission_and_release_is_available_next_step()
    {
        using var session = StationSession();
        Until(session, () => View(session, 1).Activity == TrainActivity.Servicing);
        Assert.AreEqual(2UL, View(session, 1).RemainingDwellSteps);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual("platform_wait", View(session, 2).Blockers.Single().Code);
        var occupied = session.Read().World.Nodes[0];
        var reservation = ((TrainPosition.Node)View(session, 1).Position).PlatformNumber;
        Assert.IsNotNull(reservation);
        Assert.AreEqual(1, occupied.OccupiedPlatforms);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge);
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        Assert.AreEqual(1, occupied.OccupiedPlatforms);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.IsNull(((TrainPosition.Node)View(session, 2).Position).PlatformNumber);
        Step(session);
        Assert.AreEqual(reservation, ((TrainPosition.Node)View(session, 2).Position).PlatformNumber);
        Until(session, () => View(session, 2).Activity == TrainActivity.Servicing);
        Assert.AreEqual(2UL, View(session, 2).RemainingDwellSteps);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Consecutive_stations_at_the_same_node_keep_the_platform_without_requeueing(bool competitor)
    {
        var state = State(competitor ? 2 : 1);
        var first = state.World.Trains[0] with { Activity = TrainActivity.Running,
            Plan = new(false, [new PlanStop.Station(new(1), 3, []), new PlanStop.Station(new(1), 3, []), new PlanStop.Waypoint(new(2))]) };
        var trains = competitor ? ImmutableArray.Create(first, state.World.Trains[1] with
        { Activity = TrainActivity.Running, Plan = new(false, [new PlanStop.Station(new(1), 2, [])]) }) : [first];
        using var session = new CoreSession(state with { World = state.World with { Trains = trains } });
        Until(session, () => View(session, 1).Activity == TrainActivity.Servicing);
        var platform = ((TrainPosition.Node)View(session, 1).Position).PlatformNumber;
        for (var step = 0; step < 3; step++)
        {
            Step(session);
            Assert.AreEqual(TrainActivity.Servicing, View(session, 1).Activity);
            Assert.AreEqual(platform, ((TrainPosition.Node)View(session, 1).Position).PlatformNumber);
            Assert.AreEqual(1, session.Read().World.Nodes[0].OccupiedPlatforms);
            if (competitor) Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        }
        Assert.AreEqual(1, View(session, 1).NextStopIndex);
        Assert.AreEqual(3UL, View(session, 1).RemainingDwellSteps);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge);
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        if (competitor)
        {
            Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
            Assert.IsNull(((TrainPosition.Node)View(session, 2).Position).PlatformNumber);
            Step(session);
            Assert.AreEqual(platform, ((TrainPosition.Node)View(session, 2).Position).PlatformNumber);
            Until(session, () => View(session, 2).Activity == TrainActivity.Servicing);
        }
    }

    [TestMethod]
    public void Stopping_keeps_platform_and_resuming_preserves_remaining_dwell()
    {
        using var session = StationSession();
        Until(session, () => View(session, 1).Activity == TrainActivity.Servicing);
        Step(session);
        var platform = ((TrainPosition.Node)View(session, 1).Position).PlatformNumber;
        Submit(session, new CoreCommand.StopTrain(new(1)));
        Assert.AreEqual(1UL, View(session, 1).RemainingDwellSteps);
        Assert.AreEqual(1, session.Read().World.Nodes[0].OccupiedPlatforms);
        Step(session);
        Assert.AreEqual(TrainActivity.Waiting, View(session, 2).Activity);
        Assert.AreEqual(platform, ((TrainPosition.Node)View(session, 1).Position).PlatformNumber);
        Submit(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.Servicing, View(session, 1).Activity);
        Assert.AreEqual(1UL, View(session, 1).RemainingDwellSteps);
    }

    [TestMethod]
    public void Replacing_plan_keeps_reservation_until_departure_and_disband_releases_it()
    {
        using var session = StationSession();
        Until(session, () => View(session, 1).Activity == TrainActivity.Servicing);
        var platform = ((TrainPosition.Node)View(session, 1).Position).PlatformNumber;
        Submit(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        Assert.AreEqual(1, session.Read().World.Nodes[0].OccupiedPlatforms);
        Assert.AreEqual(platform, ((TrainPosition.Node)View(session, 1).Position).PlatformNumber);
        Until(session, () => View(session, 1).Position is TrainPosition.Edge);
        Until(session, () => View(session, 2).Activity == TrainActivity.Servicing);
        Submit(session, new CoreCommand.StopTrain(new(2)));
        Submit(session, new CoreCommand.DisbandTrain(new(2)));
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
    }

    [TestMethod]
    public void Reducing_platform_count_preserves_current_service_but_blocks_new_admission()
    {
        using var session = StationSession();
        Until(session, () => View(session, 1).Activity == TrainActivity.Servicing);
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
        Assert.AreEqual(1, ((TrainPosition.Node)View(session, 2).Position).PlatformNumber);
        Until(session, () => View(session, 2).Activity == TrainActivity.Servicing);
        Assert.AreEqual(2UL, View(session, 2).RemainingDwellSteps);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Unpowered_stationary_train_on_an_unbroken_track_keeps_its_direction_and_order_facing(bool reverse)
    {
        var content = WorldFixture.Content with { Designs = WorldFixture.Content.Designs.Select(d =>
            d.Design == WorldFixture.Vehicle ? d with { Parts = d.Parts.Select(p => p with
            { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Propulsion).ToImmutableArray() }).ToImmutableArray() } : d).ToImmutableArray() };
        var state = State(1, content: content);
        var facing = reverse ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading;
        var train = OnEdge(state.World.Trains[0], 10, 0, reverse) with { OrderFacing = facing };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        for (var step = 0; step < 20; step++)
        {
            Step(session);
            Assert.AreEqual(train.Position, View(session, 1).Position);
            Assert.AreEqual(0.0, View(session, 1).SpeedMetersPerSecond);
            Assert.AreEqual(facing, View(session, 1).OrderFacing);
        }
    }

    [TestMethod]
    public void Break_turnaround_waits_for_the_unaffected_front_train_and_reverses_the_whole_stopped_group()
    {
        var state = State();
        using var session = new CoreSession(state with { World = state.World with { Trains =
            [OnEdge(state.World.Trains[0], 12, 0), OnEdge(state.World.Trains[1], 10, 2), OnEdge(state.World.Trains[2], 6, 2)] } });
        Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 11));
        Step(session);
        for (var step = 0; step < 1000 && (View(session, 2).SpeedMetersPerSecond != 0 || View(session, 3).SpeedMetersPerSecond != 0); step++) Step(session);
        Assert.IsInstanceOfType<TrainPosition.Edge>(View(session, 1).Position);
        Assert.AreEqual(0.0, View(session, 2).SpeedMetersPerSecond);
        Assert.AreEqual(0.0, View(session, 3).SpeedMetersPerSecond);
        for (var step = 0; step < 1000 && View(session, 1).Position is TrainPosition.Edge; step++)
        {
            foreach (var id in new ulong[] { 2, 3 })
                Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)View(session, id).Position).Direction);
            Step(session);
        }
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
        Until(session, () => View(session, 2).Activity == TrainActivity.NoRoute && View(session, 3).Activity == TrainActivity.NoRoute);
        foreach (var id in new ulong[] { 2, 3 })
        {
            Assert.AreEqual(WorldFixture.Node(new(1), NodeTravelPhase.Docked,
                WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2, enteredFrom: new(1)), View(session, id).Position);
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
    public void Already_passed_break_does_not_prevent_turning_for_a_new_forward_break()
    {
        var state = State(1);
        using var session = new CoreSession(state with { World = state.World with { Trains = [OnEdge(state.World.Trains[0], 10, 2)] } });
        Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 5));
        Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 11));
        for (var step = 0; step < 1000 && session.State.World.Trains[0].ReturningFromBreak is null; step++) Step(session);
        Assert.AreEqual(new BreakId(2), session.State.World.Trains[0].ReturningFromBreak);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)View(session, 1).Position).Direction);
        for (var step = 0; step < 1000; step++)
        {
            Step(session);
            if (View(session, 1).SpeedMetersPerSecond == 0 && ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters == 5) break;
        }
        Assert.AreEqual(5.0, ((TrainPosition.Edge)View(session, 1).Position).DistanceMeters);
        Assert.AreEqual(0.0, View(session, 1).SpeedMetersPerSecond);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, View(session, 1).OrderFacing);
    }

    [TestMethod]
    public void Platform_wait_at_a_zero_dwell_station_does_not_count_as_an_opposite_track_request()
    {
        var state = State();
        var world = state.World;
        world = world with { Nodes = world.Nodes.SetItem(0, world.Nodes[0] with { Platforms = 0 }),
            Instances = world.Instances.Select(i => i.Id is InstanceId.Building ? i with
            { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() } : i).ToImmutableArray(),
            Trains = [OnEdge(world.Trains[0], 10, 1, true), Queued(world.Trains[1], 2, 0, 1), world.Trains[2] with
            { Activity = TrainActivity.Waiting, QueueEnteredStep = 0, Plan = new(false,
                [new PlanStop.Station(new(1), 0, []), new PlanStop.Waypoint(new(2))]) }] };
        using var session = new CoreSession(state with { World = world });
        Step(session);
        Assert.AreEqual("platform_wait", View(session, 3).Blockers.Single().Code);
        Assert.IsInstanceOfType<TrainPosition.Edge>(View(session, 2).Position);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Broken_tracks_leave_no_saved_handoff_and_do_not_reorder_requests_for_the_open_track(bool returning)
    {
        var state = State(2, 2) with { Position = new(5, 0) };
        var world = state.World;
        if (returning)
            world = world with
            {
                Edges = [world.Edges[0] with { Breaks = [new(new(1), 1, 20)] }],
                Next = world.Next with { Break = 2 },
                Trains = [world.Trains[0] with { Position = WorldFixture.Node(new(2)), Plan = Plan(1) },
                    OnEdge(world.Trains[1], 0.01, 2, reverse: true) with { ReturningFromBreak = new(1), Plan = Plan(2) }]
            };
        else
            world = world with { Trains = [Queued(world.Trains[0], 1, 1, 2), Queued(world.Trains[1], 2, 3, 1)],
                TrackHandoffs = [new(new(1), 1, TravelDirection.Reverse)] };
        using var session = new CoreSession(state with { World = world });
        if (returning) Step(session);
        else Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 20));
        using var saved = JsonDocument.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Checkpoint.CanonicalText);
        Assert.AreEqual(0, saved.RootElement.GetProperty("world").GetProperty("track_handoffs").GetArrayLength());
        if (returning) Submit(session, new CoreCommand.StartTrain(new(1)));
        Step(session);
        var admitted = (TrainPosition.Edge)View(session, 1).Position;
        Assert.AreEqual(2, admitted.Track);
        Assert.AreEqual(returning ? TravelDirection.Reverse : TravelDirection.Forward, admitted.Direction);
        Until(session, () => View(session, 2).Activity == TrainActivity.Waiting);
        var waiting = (TrainPosition.Node)View(session, 2).Position;
        Assert.AreEqual(NodeTravelPhase.Departing, waiting.Phase);
        Assert.AreEqual(new EdgeId(1), waiting.NextEdge);
    }

    [TestMethod]
    [DataRow(2UL, 1, 0.0, "object_missing", "Edge")]
    [DataRow(1UL, 2, 0.0, "invalid_state", "Break")]
    [DataRow(1UL, 1, 21.0, "invalid_state", "Break")]
    public void Invalid_world_break_requests_are_recorded_without_changing_world(ulong edge, int track, double distance, string code, string field)
    {
        using var session = new CoreSession(State(1));
        var before = session.State;
        var view = session.Read().World;
        var checkpoint = WorldFixture.Success(session.Capture(CapturePurpose.Save)).Checkpoint;
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.DebugAddRailBreak(new(edge), track, distance), CommandSource.AutomationTest);
        var problem = ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
        Assert.AreEqual(code, problem.Code); Assert.AreEqual(field, problem.Field);
        Assert.AreEqual(view.Edges[0].Breaks.Length, session.Read().World.Edges[0].Breaks.Length);
        Assert.AreEqual(view.Trains[0].Position, View(session, 1).Position);
        Assert.AreEqual(view.Trains[0].Activity, View(session, 1).Activity);
        using var beforeJson = JsonDocument.Parse(checkpoint.CanonicalText);
        using var afterJson = JsonDocument.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Checkpoint.CanonicalText);
        Assert.AreEqual(beforeJson.RootElement.GetProperty("world").GetRawText(), afterJson.RootElement.GetProperty("world").GetRawText());
        Assert.AreEqual(before.NextCommandNumber + 1, session.State.NextCommandNumber);
        Assert.AreEqual(result.Receipt, session.ReadRecording().Commands.Single().Result);
    }

    private static CoreSession StationSession()
    {
        var state = State(2);
        return new(state with { World = state.World with { Trains = state.World.Trains.Select(t => t with
        { Activity = TrainActivity.Running, Plan = new(false, [new PlanStop.Station(new(1), 2, []), new PlanStop.Waypoint(new(2))]) }).ToImmutableArray() } });
    }
    private static CoreState TwoEdges(int count, int tracks, double length)
    {
        var state = State(count, tracks, length);
        var world = state.World;
        return state with { World = world with
        {
            Nodes = world.Nodes.Add(world.Nodes[1] with { Id = new(3), Position = new(10, 0) }),
            Edges = world.Edges.Add(world.Edges[0] with { Id = new(2), Start = new(2), End = new(3) }),
            Next = world.Next with { Node = 4, Edge = 3 }
        } };
    }
    private static RunPlan Plan(ulong node) => new(false, [new PlanStop.Waypoint(new(node))]);
    private static TrainState Queued(TrainState train, ulong node, ulong age, ulong target) => train with
    { Position = WorldFixture.Node(new(node), NodeTravelPhase.Departing,
            WorldFixture.ApproachLength + (node == 1 ? WorldFixture.PlatformLength : 0) + WorldFixture.DepartureLength,
            nextEdge: new(1)), Activity = TrainActivity.Waiting, QueueEnteredStep = age, Plan = Plan(target) };
    private static TrainState OnEdge(TrainState train, double distance, double speed, bool reverse = false, int track = 1) => train with
    { Position = new TrainPosition.Edge(new(1), track, reverse ? TravelDirection.Reverse : TravelDirection.Forward, distance),
        Activity = TrainActivity.Running, SpeedMetersPerSecond = speed, Plan = Plan(reverse ? 1UL : 2UL) };
}

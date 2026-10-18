using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class NodeTraversalTests
{
    [TestMethod]
    public void Through_train_slows_before_entry_crosses_the_midpoint_and_keeps_moving_until_exit()
    {
        using var session = new CoreSession(State());
        var entered = false;
        var departed = false;
        Until(session, () => Train(session).Position is TrainPosition.Edge { Id.Value: 2 }, () =>
        {
            var train = Train(session);
            if (train.Position is not TrainPosition.Node { Id.Value: 1 } node) return;
            entered = true;
            Assert.IsTrue(train.SpeedMetersPerSecond is > 0 and <= 15.000000001);
            Assert.IsNull(node.PlatformNumber);
            Assert.AreEqual(node.DistanceMeters < 128 ? 0 : 1, train.NextStopIndex);
            if (node.Phase == NodeTravelPhase.Departing) departed = true;
            var location = (Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Node(new(1)));
            CollectionAssert.Contains(location.Value.Trains.ToArray(), train.Id);
            Assert.IsTrue(((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Tracks.All(t => !t.Trains.Contains(train.Id)));
        }, checkBraking: true);
        Assert.IsTrue(entered);
        Assert.IsTrue(departed);
        Assert.AreEqual(TrainOrderFacing.FirstEndLeading, Train(session).OrderFacing);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Empty_node_has_no_platform_segment_and_station_stops_need_no_reservation(bool station)
    {
        var state = State();
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 800),
            Plan = new(false, station ? [Station(2), Waypoint(3)] : [Waypoint(2), Waypoint(3)])
        };
        using var session = new CoreSession(state with { World = state.World with
        {
            Edges = state.World.Edges.SetItem(1, state.World.Edges[1] with { Start = new(2) }), Trains = [train]
        } });
        Assert.AreEqual(0.0, ((Lookup<NodeView>.Found)session.Read().FindNode(new(2))).Value.PlatformLengthMeters);
        var observed = false;
        if (station)
        {
            Until(session, () => Train(session).Activity == TrainActivity.Servicing);
            Assert.AreEqual(80.0, ((TrainPosition.Node)Train(session).Position).DistanceMeters);
            Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
            Assert.IsNull(((TrainPosition.Node)Train(session).Position).PlatformNumber);
        }
        Until(session, () => Train(session).Position is TrainPosition.Edge { Id.Value: 2 }, () =>
        {
            if (Train(session).Position is not TrainPosition.Node { Id.Value: 2 } node) return;
            observed = true;
            Assert.IsNull(node.PlatformNumber);
            if (!station)
            {
                Assert.IsTrue(Train(session).SpeedMetersPerSecond > 0);
                Assert.AreEqual(node.DistanceMeters < 80 ? 0 : 1, Train(session).NextStopIndex);
            }
        });
        Assert.IsTrue(observed, "必须实际观察到空节点内的运行，不能直接从入线切换到出线。");
    }

    [TestMethod]
    [DataRow(NodeTravelPhase.Approaching, 100.0, 1UL, 128.0)]
    [DataRow(NodeTravelPhase.Departing, 150.0, 3UL, 80.0)]
    public void Stop_command_stops_before_midpoint_or_at_the_next_node_after_midpoint(
        NodeTravelPhase phase, double distance, ulong destination, double stoppedDistance)
    {
        var state = State();
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 1, new(2), phase == NodeTravelPhase.Departing ? 1 : null, phase, distance, null),
            SpeedMetersPerSecond = 5,
            NextStopIndex = phase == NodeTravelPhase.Departing ? 1 : 0
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        Accepted(session, new CoreCommand.StopTrain(new(1)));
        Until(session, () => Train(session).Activity == TrainActivity.Stopped);
        var position = (TrainPosition.Node)Train(session).Position;
        Assert.AreEqual(new NodeId(destination), position.Id);
        Assert.AreEqual(NodeTravelPhase.Docked, position.Phase);
        Assert.AreEqual(stoppedDistance, position.DistanceMeters);
        Assert.IsNull(position.PlatformNumber);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
    }

    [TestMethod]
    public void Station_reservation_precedes_service_and_survives_stop_plan_change_and_departure()
    {
        using var session = new CoreSession(State(station: true));
        Until(session, () => Train(session).Position is TrainPosition.Node { Id.Value: 1 }
            && Train(session).SpeedMetersPerSecond == 0, () =>
        {
            var train = Train(session);
            if (train.Position is TrainPosition.Node { Id.Value: 1 } node)
            {
                Assert.AreEqual(1, node.PlatformNumber);
                Assert.IsTrue(train.SpeedMetersPerSecond <= 15 + 1e-9);
                if (node.Phase == NodeTravelPhase.Approaching) Assert.AreEqual(0UL, train.RemainingDwellSteps);
            }
        });
        Assert.AreEqual(128.0, ((TrainPosition.Node)Train(session).Position).DistanceMeters);
        Assert.AreEqual(NodeTravelPhase.Docked, ((TrainPosition.Node)Train(session).Position).Phase);
        Assert.AreEqual(TrainActivity.Servicing, Train(session).Activity);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(4UL, Train(session).RemainingDwellSteps);
        Step(session);
        Assert.AreEqual(3UL, Train(session).RemainingDwellSteps);
        Accepted(session, new CoreCommand.StopTrain(new(1)));
        Assert.AreEqual(1, session.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [Waypoint(3)])));
        Assert.AreEqual(1, ((TrainPosition.Node)Train(session).Position).PlatformNumber);
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Until(session, () => Train(session).Position is TrainPosition.Edge { Id.Value: 2 }, () =>
        {
            if (Train(session).Position is TrainPosition.Node node)
            {
                Assert.AreEqual(1, node.PlatformNumber);
                Assert.AreEqual(1, session.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
            }
        });
        Assert.AreEqual(0, session.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
    }

    [TestMethod]
    public void Full_station_brakes_before_entry_waits_inside_and_frees_the_incoming_track()
    {
        using var session = new CoreSession(State(station: true, blocker: true));
        Assert.IsNull(Train(session).PlatformQueueEnteredStep);
        ulong? enteredStep = null;
        Until(session, () => Train(session).Position is TrainPosition.Node { Phase: NodeTravelPhase.Waiting }, () =>
        {
            var train = Train(session);
            if (train.Position is TrainPosition.Node { Id.Value: 1 } node)
            {
                Assert.IsTrue(train.SpeedMetersPerSecond <= Math.Sqrt(2 * 0.7 * 80) + 1e-8);
                Assert.IsNull(node.PlatformNumber);
                if (node.Phase == NodeTravelPhase.Approaching && train.Blockers.Any(problem => problem.Code == "platform_wait"))
                {
                    Assert.IsNotNull(train.PlatformQueueEnteredStep);
                    enteredStep ??= train.PlatformQueueEnteredStep;
                    Assert.AreEqual(enteredStep, train.PlatformQueueEnteredStep);
                }
            }
        }, checkBraking: true);
        Assert.IsNotNull(enteredStep, "The station request is visible while the train is still approaching.");
        var waiting = Train(session);
        Assert.AreEqual(enteredStep, waiting.PlatformQueueEnteredStep);
        Assert.AreEqual(80.0, ((TrainPosition.Node)waiting.Position).DistanceMeters);
        Assert.AreEqual(0.0, waiting.SpeedMetersPerSecond);
        Assert.AreEqual("platform_wait", waiting.Blockers.Single().Code);
        Assert.IsTrue(((Lookup<EdgeView>.Found)session.Read().FindEdge(new(1))).Value.Tracks.All(t => t.Trains.IsEmpty));
        Accepted(session, new CoreCommand.DisbandTrain(new(2)));
        Until(session, () => Train(session).Activity == TrainActivity.Servicing);
        Assert.AreEqual(1, ((TrainPosition.Node)Train(session).Position).PlatformNumber);
        Assert.IsNull(Train(session).PlatformQueueEnteredStep);
        Assert.AreEqual(128.0, ((TrainPosition.Node)Train(session).Position).DistanceMeters);
    }

    [TestMethod]
    public void Earlier_waiter_claims_the_last_free_platform_so_the_arriving_train_brakes_to_the_waiting_point()
    {
        var state = State(station: true, blocker: true);
        var arriving = state.World.Trains[0] with
        {
            Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Reverse, 60),
            SpeedMetersPerSecond = 14
        };
        var earlier = state.World.Trains[1] with
        {
            Position = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 80, null),
            Activity = TrainActivity.Waiting, QueueEnteredStep = 0,
            Plan = new(false, [new PlanStop.Station(new(1), 50000, [])]), NextStopIndex = 0
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [arriving, earlier] } });
        Assert.AreEqual(1, session.Read().World.Nodes[0].Platforms);
        Assert.AreEqual(0, session.Read().World.Nodes[0].OccupiedPlatforms);
        Step(session);
        Assert.AreEqual(14 - 0.7 / 60, Train(session).SpeedMetersPerSecond, 1e-10);
        Assert.AreEqual(1, ((TrainPosition.Node)((Lookup<TrainView>.Found)session.Read().FindTrain(new(2))).Value.Position).PlatformNumber);
        Until(session, () => Train(session).Position is TrainPosition.Node { Phase: NodeTravelPhase.Waiting },
            checkBraking: true);
        var waiting = (TrainPosition.Node)Train(session).Position;
        Assert.AreEqual(80.0, waiting.DistanceMeters);
        Assert.IsNull(waiting.PlatformNumber);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual("platform_wait", Train(session).Blockers.Single().Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Late_station_request_waits_at_midpoint_without_rejecting_plan_or_start(bool stopped)
    {
        var state = State(blocker: true);
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 1, new(2), null,
                stopped ? NodeTravelPhase.Docked : NodeTravelPhase.Approaching, stopped ? 128 : 100, null),
            SpeedMetersPerSecond = stopped ? 0 : 5,
            Activity = stopped ? TrainActivity.Stopped : TrainActivity.Running
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = state.World.Trains.SetItem(0, train) } });
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [Station(1), Waypoint(3)])));
        if (stopped) Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(stopped ? 128.0 : 100.0, ((TrainPosition.Node)Train(session).Position).DistanceMeters);
        Until(session, () => Train(session).Activity == TrainActivity.Waiting
            && Train(session).Position is TrainPosition.Node { Phase: NodeTravelPhase.Docked });
        var waiting = Train(session);
        Assert.AreEqual(128.0, ((TrainPosition.Node)waiting.Position).DistanceMeters);
        Assert.IsNull(((TrainPosition.Node)waiting.Position).PlatformNumber);
        Assert.AreEqual("platform_wait", waiting.Blockers.Single().Code);
        Assert.AreEqual(0UL, session.State.World.Trains[0].QueueEnteredStep);
        Accepted(session, new CoreCommand.DisbandTrain(new(2)));
        Step(session);
        Assert.AreEqual(TrainActivity.Servicing, Train(session).Activity);
        Assert.AreEqual(4UL, Train(session).RemainingDwellSteps);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void New_local_station_after_midpoint_keeps_the_exit_and_is_served_after_return(bool waitingAtExit)
    {
        var state = State();
        state = state with { World = state.World with
            { Edges = state.World.Edges.Select(edge => edge with { LengthMeters = 100 }).ToImmutableArray() } };
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 1, new(2), 1, NodeTravelPhase.Departing,
                waitingAtExit ? 256 : 150, null),
            SpeedMetersPerSecond = waitingAtExit ? 0 : 5,
            Activity = waitingAtExit ? TrainActivity.Waiting : TrainActivity.Running,
            NextStopIndex = 1, QueueEnteredStep = waitingAtExit ? 0UL : null
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [Station(1), Waypoint(2)])));
        var position = (TrainPosition.Node)Train(session).Position;
        Assert.AreEqual(new EdgeId(2), position.NextEdge);
        Assert.AreEqual(NodeTravelPhase.Departing, position.Phase);
        Assert.IsNull(position.PlatformNumber);
        Until(session, () => Train(session).Position is TrainPosition.Edge { Id.Value: 2 }, () =>
        {
            Assert.AreEqual(0, session.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
            Assert.AreEqual(0, Train(session).NextStopIndex);
        });
        Until(session, () => Train(session).Activity == TrainActivity.Servicing);
        Assert.AreEqual(new NodeId(1), ((TrainPosition.Node)Train(session).Position).Id);
        Assert.AreEqual(1, ((TrainPosition.Node)Train(session).Position).PlatformNumber);
        Assert.AreEqual(4UL, Train(session).RemainingDwellSteps);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Departing_plan_change_consumes_leading_local_waypoints_but_defers_a_local_station(
        bool waitingAtExit, bool station)
    {
        var state = State();
        var position = new TrainPosition.Node(new(1), new(1), 1, new(2), 1, NodeTravelPhase.Departing,
            waitingAtExit ? 256 : 150, null);
        var train = state.World.Trains[0] with
        {
            Position = position, SpeedMetersPerSecond = waitingAtExit ? 0 : 5,
            Activity = waitingAtExit ? TrainActivity.Waiting : TrainActivity.Running,
            NextStopIndex = 1, QueueEnteredStep = waitingAtExit ? 0UL : null
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        ImmutableArray<PlanStop> stops = station ? [Waypoint(1), Waypoint(1), Station(1), Waypoint(3)]
            : [Waypoint(1), Waypoint(1), Waypoint(3)];
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, stops)));
        Assert.AreEqual(position, Train(session).Position);
        Assert.AreEqual(2, Train(session).NextStopIndex);
        var saved = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(saved.Checkpoint, Success(CoreFactory.Create().DecodeState(saved.Encode())).Checkpoint);
        Until(session, () => Train(session).Position is TrainPosition.Edge { Id.Value: 2 }, () =>
        {
            Assert.AreEqual(2, Train(session).NextStopIndex);
            Assert.AreEqual(0, session.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
        });
    }

    [TestMethod]
    public void Completed_departing_plan_restores_through_the_edge_and_approach_then_stops_at_the_next_midpoint()
    {
        var state = State();
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 1, new(2), 1, NodeTravelPhase.Departing, 150, 1),
            SpeedMetersPerSecond = 5
        };
        using var session = new CoreSession(state with { World = state.World with
        { Trains = [train], Edges = state.World.Edges.SetItem(1, state.World.Edges[1] with { LengthMeters = 100 }) } });
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [Waypoint(1), Waypoint(1)])));
        Assert.IsNull(Train(session).NextStopIndex);
        Assert.IsTrue(session.State.World.Trains[0].StopRequested);
        var restart = (SubmissionResult.Completed)session.Submit(new CoreCommand.StartTrain(new(1)), CommandSource.AutomationTest);
        Assert.AreEqual("plan_completed", ((CommandDecision.Rejected)restart.Receipt.Decision).Problems.Single().Code);
        var saved = Success(session.Capture(CapturePurpose.Save));
        using var restored = Restore(saved, state.Content);
        Assert.AreEqual(1, ((TrainPosition.Node)Train(restored).Position).PlatformNumber);
        Until(restored, () => Train(restored).Position is TrainPosition.Edge { Id.Value: 2 });
        var onEdge = Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(onEdge.Checkpoint, Success(CoreFactory.Create().DecodeState(onEdge.Encode())).Checkpoint);
        Assert.AreEqual(0, restored.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
        Until(restored, () => Train(restored).Position is TrainPosition.Node { Id.Value: 3, Phase: NodeTravelPhase.Approaching });
        Assert.IsTrue(restored.State.World.Trains[0].StopRequested);
        Assert.IsNull(Train(restored).NextStopIndex);
        using var entering = Restore(Success(restored.Capture(CapturePurpose.Save)), state.Content);
        Until(entering, () => Train(entering).Activity == TrainActivity.Stopped, checkBraking: true);
        Assert.AreEqual(new TrainPosition.Node(new(3), new(2), 1, null, null, NodeTravelPhase.Docked, 80, null), Train(entering).Position);
        Assert.AreEqual(0.0, Train(entering).SpeedMetersPerSecond);
        Assert.IsFalse(entering.State.World.Trains[0].StopRequested);
        Assert.IsNull(session.Advance((uint)(entering.Read().Position.CompletedSteps - saved.Position.CompletedSteps)).Fault);
        Assert.AreEqual(Success(entering.Capture(CapturePurpose.Save)).Checkpoint, Success(session.Capture(CapturePurpose.Save)).Checkpoint);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Completed_plan_at_the_exit_stays_stopped_and_reserved_after_restore(bool trackRejected)
    {
        var state = State(blocker: trackRejected);
        var position = new TrainPosition.Node(new(1), new(1), 1, new(2), 1, NodeTravelPhase.Departing, 256, 1);
        var train = state.World.Trains[0] with
        {
            Position = position, SpeedMetersPerSecond = trackRejected ? 5 : 0,
            Activity = trackRejected ? TrainActivity.Running : TrainActivity.Waiting,
            QueueEnteredStep = trackRejected ? null : 0UL
        };
        var world = state.World with { Trains = state.World.Trains.SetItem(0, train) };
        if (trackRejected) world = world with
        {
            Edges = world.Edges.SetItem(1, world.Edges[1] with { LengthMeters = 10000 }),
            Trains = world.Trains.SetItem(1, world.Trains[1] with
            {
                Position = new TrainPosition.Edge(new(2), 1, TravelDirection.Reverse, 5000),
                Activity = TrainActivity.Running, SpeedMetersPerSecond = 1,
                Plan = new(false, [Waypoint(1)]), NextStopIndex = 0,
                CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, [])
            })
        };
        using var session = new CoreSession(state with { World = world });
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [Waypoint(1)])));
        if (trackRejected)
        {
            Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
            Assert.IsTrue(session.State.World.Trains[0].StopRequested);
            Step(session);
        }
        Assert.AreEqual(TrainActivity.Stopped, Train(session).Activity);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(position, Train(session).Position);
        Assert.IsNull(session.State.World.Trains[0].QueueEnteredStep);
        Assert.IsFalse(session.State.World.Trains[0].StopRequested);
        using var restored = Restore(Success(session.Capture(CapturePurpose.Save)), state.Content);
        Step(restored);
        Assert.AreEqual(TrainActivity.Stopped, Train(restored).Activity);
        Assert.AreEqual(position, Train(restored).Position);
        Assert.AreEqual(1, restored.Read().World.Nodes.Single(n => n.Id.Value == 1).OccupiedPlatforms);
    }

    [TestMethod]
    public void Completed_plan_stays_restorable_if_departure_capability_is_lost_before_the_exit()
    {
        var state = State();
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 1, new(2), 1, NodeTravelPhase.Departing, 150, 1),
            SpeedMetersPerSecond = 5
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [Waypoint(1)])));
        var unpowered = state.Content with { Identity = new(new string('E', 64)), Designs = state.Content.Designs.Select(d => d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Propulsion).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        Accepted(session, new CoreCommand.PublishContent(unpowered));
        Until(session, () => Train(session).SpeedMetersPerSecond == 0, checkBraking: true);
        Assert.IsTrue(Train(session).Position is TrainPosition.Node
            { Id.Value: 1, Phase: NodeTravelPhase.Departing, DistanceMeters: > 128 and < 256 });
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
        Assert.IsTrue(session.State.World.Trains[0].StopRequested);
        using var restored = Restore(Success(session.Capture(CapturePurpose.Save)), unpowered);
        Assert.AreEqual(Train(session).Position, Train(restored).Position);
        Step(session);
        Step(restored);
        Assert.AreEqual(TrainActivity.Stopped, Train(restored).Activity);
        Assert.IsFalse(restored.State.World.Trains[0].StopRequested);
        Assert.AreEqual(Train(session).Position, Train(restored).Position);
        Assert.AreEqual(Success(session.Capture(CapturePurpose.Save)).Checkpoint,
            Success(restored.Capture(CapturePurpose.Save)).Checkpoint);
    }

    [TestMethod]
    public void Reversal_stops_at_midpoint_and_changes_facing_only_on_actual_reverse_departure()
    {
        var state = State();
        var train = state.World.Trains[0] with { Plan = new(false, [Waypoint(1), Waypoint(2)]) };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        Until(session, () => Train(session).Position is TrainPosition.Node { Phase: NodeTravelPhase.Docked });
        Assert.AreEqual(128.0, ((TrainPosition.Node)Train(session).Position).DistanceMeters);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(TrainOrderFacing.FirstEndLeading, Train(session).OrderFacing);
        var saved = Success(session.Capture(CapturePurpose.Save));
        using var candidate = Success(CoreFactory.Create().PrepareRestore(Success(CoreFactory.Create().DecodeState(saved.Encode())), state.Content, Options));
        using var restored = candidate.Activate();
        Until(session, () => Train(session).Position is TrainPosition.Edge { Id.Value: 1, Direction: TravelDirection.Forward });
        Assert.IsNull(restored.Advance((uint)(session.Read().Position.CompletedSteps - saved.Position.CompletedSteps)).Fault);
        Assert.AreEqual(Success(session.Capture(CapturePurpose.Save)).Checkpoint, Success(restored.Capture(CapturePurpose.Save)).Checkpoint);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, Train(session).OrderFacing);
    }

    [TestMethod]
    public void Blocked_exit_is_predicted_but_queue_age_starts_only_at_actual_exit()
    {
        var state = State(blocker: true);
        var world = state.World;
        var other = world.Trains[1] with
        {
            Position = new TrainPosition.Edge(new(2), 1, TravelDirection.Reverse, 5000),
            SpeedMetersPerSecond = 1, Activity = TrainActivity.Running,
            Plan = new(false, [Waypoint(1)]), NextStopIndex = 0,
            CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, [])
        };
        using var session = new CoreSession(state with { World = world with
        {
            Edges = world.Edges.SetItem(1, world.Edges[1] with { LengthMeters = 10000 }),
            Trains = world.Trains.SetItem(1, other)
        } });
        Until(session, () => Train(session).Activity == TrainActivity.Waiting
            && Train(session).Position is TrainPosition.Node { Phase: NodeTravelPhase.Departing }, () =>
        {
            var train = Train(session);
            if (train.Position is TrainPosition.Node { DistanceMeters: < 256 })
                Assert.IsNull(session.State.World.Trains[0].QueueEnteredStep);
        }, checkBraking: true);
        Assert.AreEqual(256.0, ((TrainPosition.Node)Train(session).Position).DistanceMeters);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(session.Read().Position.CompletedSteps, session.State.World.Trains[0].QueueEnteredStep);
        Assert.IsNull(Train(session).PlatformQueueEnteredStep, "An outgoing track request is not a platform request.");
    }

    [TestMethod]
    [DataRow(NodeTravelPhase.Waiting, 80.0, 0.0, false, TrainActivity.Stopped, 30.0, 20.0, NodeTravelPhase.Waiting)]
    [DataRow(NodeTravelPhase.Docked, 128.0, 0.0, false, TrainActivity.Stopped, 30.0, 68.0, NodeTravelPhase.Docked)]
    [DataRow(NodeTravelPhase.Departing, 256.0, 0.0, false, TrainActivity.Stopped, 30.0, 146.0, NodeTravelPhase.Departing)]
    [DataRow(NodeTravelPhase.Approaching, 100.0, 5.0, true, TrainActivity.Running, 30.0, 68.0, NodeTravelPhase.Docked)]
    [DataRow(NodeTravelPhase.Approaching, 100.0, 5.0, false, TrainActivity.Running, 30.0, 100.0, NodeTravelPhase.Departing)]
    [DataRow(NodeTravelPhase.Departing, 250.0, 5.0, false, TrainActivity.Running, 30.0, 146.0, NodeTravelPhase.Departing)]
    [DataRow(NodeTravelPhase.Departing, 168.0, 0.0, false, TrainActivity.Stopped, 190.0, 168.0, NodeTravelPhase.Departing)]
    [DataRow(NodeTravelPhase.Departing, 256.0, 0.0, false, TrainActivity.Waiting, 190.0, 306.0, NodeTravelPhase.Departing)]
    public void Publication_and_restore_adapt_node_positions_without_rejecting_scale_changes(
        NodeTravelPhase phase, double distance, double speed, bool station, TrainActivity activity,
        double departureLength, double expected, NodeTravelPhase expectedPhase)
    {
        var state = State(station);
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(1), new(1), 1, new(2), phase == NodeTravelPhase.Departing ? 1 : null, phase, distance, station ? 1 : null),
            SpeedMetersPerSecond = speed, Activity = activity,
            QueueEnteredStep = activity == TrainActivity.Waiting ? 0UL : null
        };
        using var session = new CoreSession(state with { World = state.World with { Trains = [train] } });
        var saved = Success(session.Capture(CapturePurpose.Save));
        var changed = state.Content with { Identity = new(new string('E', 64)), RailwayRules = [state.Content.RailwayRules[0] with
            { ApproachLengthMeters = 20, DepartureLengthMeters = departureLength }],
            SettlementTemplates = [state.Content.SettlementTemplates[0] with { PlatformLengthMeters = 128 }] };
        Accepted(session, new CoreCommand.PublishContent(changed));
        using var candidate = Success(CoreFactory.Create().PrepareRestore(Success(CoreFactory.Create().DecodeState(saved.Encode())), changed, Options));
        using var restored = candidate.Activate();
        foreach (var view in new[] { session.Read(), restored.Read() })
        {
            var found = (Lookup<TrainView>.Found)view.FindTrain(new(1));
            var position = (TrainPosition.Node)found.Value.Position;
            Assert.AreEqual(expected, position.DistanceMeters);
            Assert.AreEqual(expectedPhase, position.Phase);
            Assert.AreEqual(station ? 1 : (int?)null, position.PlatformNumber);
            if (station && phase == NodeTravelPhase.Approaching) Assert.AreEqual(0.0, found.Value.SpeedMetersPerSecond);
            var node = ((Lookup<NodeView>.Found)view.FindNode(new(1))).Value;
            Assert.AreEqual(20.0, node.ApproachLengthMeters);
            Assert.AreEqual(96.0, node.PlatformLengthMeters);
            Assert.AreEqual(departureLength, node.DepartureLengthMeters);
        }
    }

    [TestMethod]
    public void Lowered_speed_limit_brakes_instead_of_clamping_the_saved_speed()
    {
        var state = State();
        var train = state.World.Trains[0] with { Position = new TrainPosition.Node(new(1), new(1), 1, new(2), null,
            NodeTravelPhase.Approaching, 10, null), SpeedMetersPerSecond = 20 };
        var original = state.Content with { RailwayRules = [state.Content.RailwayRules[0] with { NodeSpeedLimitMetersPerSecond = 25 }] };
        using var session = new CoreSession(state with { Content = original, World = state.World with { Trains = [train] } });
        var changed = original with { Identity = new(new string('E', 64)), RailwayRules = [original.RailwayRules[0] with { NodeSpeedLimitMetersPerSecond = 10 }] };
        Accepted(session, new CoreCommand.PublishContent(changed));
        Assert.AreEqual(20.0, Train(session).SpeedMetersPerSecond);
        Step(session);
        Assert.AreEqual(20 - 0.7 / 60, Train(session).SpeedMetersPerSecond, 1e-12);
    }

    private static CoreState State(bool station = false, bool blocker = false)
    {
        var input = Input with { Start = Input.Start with
        { Trains = Enumerable.Range(0, blocker ? 2 : 1).Select(_ => new StartingTrainProjection([Vehicle], [])).ToImmutableArray() } };
        var content = Content with
        {
            RailwayRules = [Content.RailwayRules[0] with { ApproachLengthMeters = 80, DepartureLengthMeters = 80,
                NodeSpeedLimitMetersPerSecond = 15, BrakingDeceleration = 0.7 }],
            SettlementTemplates = [Content.SettlementTemplates[0] with { PlatformLengthMeters = 96 }],
            Designs = Content.Designs.SetItem(0, Content.Designs[0] with
            { Parts = [Content.Designs[0].Parts[0] with { Capabilities = [new PartCapability.Control(), new PartCapability.Propulsion(100, 30)] }] })
        };
        using var candidate = Prepare(input, content);
        using var session = (CoreSession)candidate.Activate();
        var state = session.State;
        var world = state.World;
        var town = world.Nodes[0];
        var arriving = world.Trains[0] with { Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Reverse, 200),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 15.1,
            Plan = new(false, station ? [Station(1), Waypoint(3)] : [Waypoint(1), Waypoint(3)]), NextStopIndex = 0,
            CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, []) };
        world = world with
        {
            Nodes = [town, town with { Id = new(2), Position = new(1, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
                town with { Id = new(3), Position = new(2, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 }],
            Edges = [new(new(1), new(1), new(2), 1000, town.Terrain, 1, 2, [], []),
                new(new(2), new(1), new(3), 1000, town.Terrain, 1, 2, [], [])],
            Trains = world.Trains.SetItem(0, arriving), Next = world.Next with { Node = 4, Edge = 3 }
        };
        if (blocker) world = world with { Trains = world.Trains.SetItem(1, world.Trains[1] with
            { Position = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Docked, 128, 1) }) };
        WorldStateValidation.Validate(world);
        return state with { World = world };
    }

    private static TrainView Train(ICoreSession session) => ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
    private static CoreSession Restore(ICoreCapture saved, CoreContentProjection content)
    {
        using var candidate = Success(CoreFactory.Create().PrepareRestore(Success(CoreFactory.Create().DecodeState(saved.Encode())), content, Options));
        return (CoreSession)candidate.Activate();
    }
    private static PlanStop.Waypoint Waypoint(ulong id) => new(new(id));
    private static PlanStop.Station Station(ulong id) => new(new(id), 4, []);
    private static void Accepted(ICoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
    }
    private static void Step(ICoreSession session)
    {
        var result = session.Advance(1);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(1u, result.CompletedSteps);
    }
    private static void Until(CoreSession session, Func<bool> reached, Action? observed = null,
        bool checkBraking = false)
    {
        for (var step = 0; step < 15000 && !reached(); step++)
        {
            var speed = Train(session).SpeedMetersPerSecond;
            Step(session);
            if (checkBraking) Assert.IsTrue(Train(session).SpeedMetersPerSecond >= speed - 0.7 / 60 - 1e-8,
                "正常预测制动不能使用瞬间归零。");
            observed?.Invoke();
        }
        Assert.IsTrue(reached(), $"未到达目标：{Train(session)}");
    }
}

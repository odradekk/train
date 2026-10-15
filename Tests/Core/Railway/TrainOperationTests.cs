using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainOperationTests
{
    private static readonly VoxelDefinitionId TrainPart = new("fixture:train-part");
    private static CoreContentProjection Content => WorldFixture.Content with
    {
        Goods = WorldFixture.Content.Goods.Add(WorldFixture.Content.Goods[0] with { Good = new GoodId.Voxel(TrainPart) }),
        Designs = WorldFixture.Content.Designs.Select(d => d.Design == Vehicle ? d with
        {
            Parts = d.Parts.Select(p => p with
            { Definition = new CoreDefinitionReference.Voxel(TrainPart), RepairGood = new GoodId.Voxel(TrainPart) }).ToImmutableArray()
        } : d).ToImmutableArray()
    };

    [TestMethod]
    public void Disband_and_form_preserve_member_order_and_never_reuse_train_or_inventory_identity()
    {
        using var session = Session();
        var original = Train(session);
        var disband = Accepted(session, new CoreCommand.DisbandTrain(original.Id));
        CollectionAssert.AreEqual(Array.Empty<VehicleId>(), ((CoreEvent.TrainCompositionChanged)disband.Events.Single().Event).Vehicles.ToArray());
        Assert.IsInstanceOfType<Lookup<TrainView>.Missing>(session.Read().FindTrain(original.Id));
        var vehicle = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.IsNull(((InstanceDetails.Vehicle)vehicle.Details).Train);
        Assert.AreEqual(new WorldLocation.Node(new(1)), vehicle.Location);
        var form = Accepted(session, new CoreCommand.FormTrain([new(1)]));
        var created = ((CommandDecision.Accepted)form.Receipt.Decision).Created;
        CollectionAssert.AreEqual(new WorldObjectId[] { new WorldObjectId.Train(new(2)), new WorldObjectId.Inventory(new(3)) }, created.ToArray());
        var composed = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(2))).Value;
        Assert.AreEqual(TrainActivity.Stopped, composed.Activity);
        Assert.AreEqual(WorldFixture.Node(new(1)), composed.Position);
        CollectionAssert.AreEqual(new VehicleId[] { new(1) }, ((CoreEvent.TrainCompositionChanged)form.Events.Single().Event).Vehicles.ToArray());
        Assert.IsTrue(session.State.World.Settlements[0].ParkedVehicles.IsEmpty);
    }

    [TestMethod]
    public void Rejected_compositions_and_loaded_disband_are_atomic_but_recorded()
    {
        using var session = Session();
        Rejected(session, new CoreCommand.FormTrain([]), "composition_empty");
        Rejected(session, new CoreCommand.FormTrain([new(1), new(1)]), "vehicle_duplicate");
        Rejected(session, new CoreCommand.FormTrain([new(99)]), "object_missing", "OrderedVehicles",
            new WorldObjectId.Instance(new InstanceId.Vehicle(new(99))));
        Rejected(session, new CoreCommand.FormTrain([new(1)]), "vehicle_in_train");
        var state = session.State;
        using var loaded = new CoreSession(state with { World = state.World with
        { Inventories = state.World.Inventories.Select(i => i.Owner is InventoryOwner.Train ? i with
            { Goods = [new(new GoodId.Voxel(Material), 1)] } : i).ToImmutableArray() } });
        Rejected(loaded, new CoreCommand.DisbandTrain(new(1)), "inventory_not_empty");
        Assert.AreEqual(4, session.ReadRecording().Commands.Length);
    }

    [TestMethod]
    public void Form_preserves_reversed_vehicle_order()
    {
        var state = State();
        using var session = new CoreSession(state with { World = state.World with
        {
            Instances = state.World.Instances.Add(state.World.Instances.Single(i => i.Id is InstanceId.Vehicle) with
                { Id = new InstanceId.Vehicle(new(2)) }),
            Trains = [state.World.Trains[0] with { Vehicles = [new(1), new(2)] }],
            Next = state.World.Next with { Vehicle = 3 }
        } });
        Accepted(session, new CoreCommand.DisbandTrain(new(1)));
        Accepted(session, new CoreCommand.FormTrain([new(2), new(1)]));
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(2))).Value;
        CollectionAssert.AreEqual(new VehicleId[] { new(2), new(1) }, train.Vehicles.ToArray());
    }

    [TestMethod]
    [DataRow("control_missing")]
    [DataRow("traction_missing")]
    [DataRow("overloaded")]
    public void Start_rejects_the_actual_departure_capability_problem(string code)
    {
        var state = State();
        var design = state.World.Designs.Single(d => d.Design == Vehicle);
        var capabilities = design.Parts[0].Capabilities.Where(c =>
            code != "control_missing" || c is not PartCapability.Control).Where(c =>
            code != "traction_missing" || c is not PartCapability.Propulsion).ToImmutableArray();
        var changed = design with { Parts = [design.Parts[0] with { Capabilities = capabilities }] };
        using var session = new CoreSession(state with { World = state.World with
        {
            Designs = state.World.Designs.Select(d => d.Design == Vehicle ? changed : d).ToImmutableArray(),
            Inventories = state.World.Inventories.Select(i => code == "overloaded" && i.Owner is InventoryOwner.Train
                ? i with { Goods = [new(new GoodId.Voxel(Material), 100)] } : i).ToImmutableArray()
        } });
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        Rejected(session, new CoreCommand.StartTrain(new(1)), code);
    }

    [TestMethod]
    public void Missing_content_after_dwell_takes_priority_over_a_blocked_route()
    {
        var state = State();
        using var session = new CoreSession(state with
        {
            Content = state.Content with { Goods = state.Content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)).ToImmutableArray() },
            World = state.World with
            {
                Inventories = state.World.Inventories.Select(i => i.Owner is InventoryOwner.Train
                    ? i with { Goods = [new(new GoodId.Voxel(Material), 1)] } : i).ToImmutableArray(),
                Edges = state.World.Edges.Select(e => e with { Tracks = 0 }).ToImmutableArray(),
                Trains = [state.World.Trains[0] with {
                    Position = WorldFixture.Node(new(1), NodeTravelPhase.Docked, ApproachLength + PlatformLength / 2, platform: 1),
                    Plan = new(true, [new PlanStop.Station(new(1), 1, []), new PlanStop.Waypoint(new(2))]),
                    NextStopIndex = 0, Activity = TrainActivity.Servicing, RemainingDwellSteps = 1,
                    CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, []) }]
            }
        });
        var result = session.Advance(1);
        Assert.AreEqual(TrainActivity.Waiting, Train(session).Activity);
        Assert.AreEqual("content_missing", result.Events.Select(e => e.Event).OfType<CoreEvent.TrainActivityChanged>().Single().Reasons.Single().Code);
    }

    [TestMethod]
    public void Empty_and_degenerate_plans_and_missing_targets_are_rejected()
    {
        using var session = Session();
        Rejected(session, new CoreCommand.StartTrain(new(1)), "plan_missing");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [])), "plan_empty");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(1), new(true,
            [new PlanStop.Waypoint(new(2)), new PlanStop.Waypoint(new(2))])), "plan_degenerate");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Waypoint(new(2)), new PlanStop.Station(new(99), 0, [])])), "object_missing",
            "Plan.Stops[1].Node", new WorldObjectId.Node(new(99)));
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(99), Plan(2)), "object_missing", "Train",
            new WorldObjectId.Train(new(99)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Station_at_empty_or_hostile_node_is_accepted_and_completes_dwell(bool hostile)
    {
        var state = hostile ? Hostile(State()) : State();
        using var session = new CoreSession(state);
        var target = new NodeId(hostile ? 1UL : 2UL);
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(target, 3, [])])));
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        AdvanceUntil(session, t => t.Activity == TrainActivity.Servicing);
        Assert.AreEqual(WorldFixture.Node(target, NodeTravelPhase.Docked, ApproachLength + (hostile ? PlatformLength / 2 : 0), enteredFrom: hostile ? null : new(1)), Train(session).Position);
        Assert.AreEqual(3UL, Train(session).RemainingDwellSteps);
        var capture = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(capture.Checkpoint, Success(CoreFactory.Create().DecodeState(capture.Encode())).Checkpoint);
        session.Advance(2);
        Assert.AreEqual(TrainActivity.Servicing, Train(session).Activity);
        Assert.AreEqual(1UL, Train(session).RemainingDwellSteps);
        session.Advance(1);
        Assert.AreEqual(TrainActivity.Stopped, Train(session).Activity);
        Assert.IsNull(Train(session).NextStopIndex);
    }

    [TestMethod]
    public void Start_cancels_pending_stop_on_edge_and_keeps_running_through_the_front_node()
    {
        using var session = Moving(Plan(3), 9.99, 2);
        Accepted(session, new CoreCommand.StopTrain(new(1)));
        Assert.IsTrue(session.State.World.Trains[0].StopRequested);
        var before = session.State.World.Trains[0];
        var result = Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(before with { StopRequested = false }, session.State.World.Trains[0]);
        Assert.IsTrue(result.Events.IsEmpty);
        session.Advance(1);
        Assert.AreEqual(WorldFixture.Node(new(2), NodeTravelPhase.Approaching, 0, enteredFrom: new(1), nextEdge: new(2)), Train(session).Position);
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
        Assert.AreEqual(Math.Sqrt(6), Train(session).SpeedMetersPerSecond, 1e-12);
    }

    [TestMethod]
    [DataRow(TrainActivity.Running)]
    [DataRow(TrainActivity.Servicing)]
    [DataRow(TrainActivity.Waiting)]
    [DataRow(TrainActivity.NoRoute)]
    public void Start_is_idempotently_accepted_outside_stopped_even_with_missing_cargo(TrainActivity activity)
    {
        using var loaded = LoadedSession();
        var state = loaded.State;
        var train = state.World.Trains[0] with { Activity = activity, Plan = Plan(2), NextStopIndex = 0,
            CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, []) };
        if (activity == TrainActivity.Servicing)
            train = train with { Position = WorldFixture.Node(new(1), NodeTravelPhase.Docked, ApproachLength + PlatformLength / 2, platform: 1),
                Plan = new(false, [new PlanStop.Station(new(1), 3, [])]), RemainingDwellSteps = 2 };
        if (activity == TrainActivity.Running) train = train with { Position = WorldFixture.Node(new(1), NodeTravelPhase.Departing, ApproachLength + PlatformLength + DepartureLength, nextEdge: new(1)) };
        using var session = new CoreSession(state with { Content = state.Content with
            { Goods = state.Content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)).ToImmutableArray() },
            World = state.World with { Trains = [train] } });
        var result = Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(train, session.State.World.Trains[0]);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void Formation_and_disband_reject_access_and_location_problems_atomically()
    {
        var ownershipState = State();
        using var notOwned = new CoreSession(ownershipState with { World = ownershipState.World with
        { Instances = ownershipState.World.Instances.Select(i => i.Id is InstanceId.Vehicle
            ? i with { Ownership = SettlementRelation.NonHostile } : i).ToImmutableArray() } });
        Rejected(notOwned, new CoreCommand.FormTrain([new(1)]), "not_owned", "OrderedVehicles",
            new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))));
        using var hostile = new CoreSession(Hostile(State()));
        Rejected(hostile, new CoreCommand.DisbandTrain(new(1)), "access_denied", "Train.Position", new WorldObjectId.Train(new(1)));
        var parkedWorld = hostile.State.World with
        {
            Trains = [], Inventories = hostile.State.World.Inventories.Where(i => i.Owner is not InventoryOwner.Train).ToImmutableArray(),
            Instances = hostile.State.World.Instances.Select(i => i.Details is InstanceDetails.Vehicle vehicle
                ? i with { Details = vehicle with { Train = null } } : i).ToImmutableArray(),
            Settlements = hostile.State.World.Settlements.Select(s => s with { ParkedVehicles = [new(1)] }).ToImmutableArray()
        };
        using var hostileParked = new CoreSession(hostile.State with { World = parkedWorld });
        Rejected(hostileParked, new CoreCommand.FormTrain([new(1)]), "access_denied", "OrderedVehicles", new WorldObjectId.Settlement(new(1)));
        var state = State();
        using var emptyNode = new CoreSession(state with { World = state.World with
        { Trains = [state.World.Trains[0] with { Position = WorldFixture.Node(new(2)) }] } });
        Rejected(emptyNode, new CoreCommand.DisbandTrain(new(1)), "settlement_unavailable", "Train.Position", new WorldObjectId.Train(new(1)));
    }

    [TestMethod]
    public void Parked_vehicles_in_different_settlements_cannot_form_a_train()
    {
        using var candidate = Prepare(Input with
        {
            Configuration = Input.Configuration with { WidthMeters = 40, HeightMeters = 20 },
            Start = Input.Start with { Trains = [Input.Start.Trains[0], Input.Start.Trains[0]] }
        });
        using var original = (CoreSession)candidate.Activate();
        Accepted(original, new CoreCommand.DisbandTrain(new(1)));
        Accepted(original, new CoreCommand.DisbandTrain(new(2)));
        var world = original.State.World;
        var source = world.Settlements.Single(s => s.ParkedVehicles.Contains(new(2)));
        var destination = world.Settlements.First(s => s.Id != source.Id);
        world = world with { Settlements = world.Settlements.Select(s => s.Id == source.Id
            ? s with { ParkedVehicles = [new(1)] } : s.Id == destination.Id
            ? s with { ParkedVehicles = [new(2)] } : s).ToImmutableArray() };
        using var session = new CoreSession(original.State with { World = world });
        Rejected(session, new CoreCommand.FormTrain([new(1), new(2)]), "vehicles_not_colocated", "OrderedVehicles",
            new WorldObjectId.Instance(new InstanceId.Vehicle(new(2))));
    }

    [TestMethod]
    public void Formation_reports_exhausted_domain_identities_atomically()
    {
        using var original = Session();
        Accepted(original, new CoreCommand.DisbandTrain(new(1)));
        var state = original.State;
        using var session = new CoreSession(state with { World = state.World with
        { Next = state.World.Next with { Train = ulong.MaxValue } } });
        Rejected(session, new CoreCommand.FormTrain([new(1)]), "identity_exhausted", "World.Next");
    }

    [TestMethod]
    public void Invalid_automatic_goods_rules_throw_before_taking_a_command_number()
    {
        using var session = Session();
        var before = session.State;
        var plan = new RunPlan(false, [new PlanStop.Station(new(1), 1,
            [new(new AutomaticGoodsAction.Load(), new GoodId.Voxel(Material), new(1), -1)])]);
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.ReplaceRunPlan(new(1), plan), CommandSource.AutomationTest));
        Assert.AreEqual(before, session.State);
        Assert.IsTrue(session.ReadRecording().Commands.IsEmpty);
    }

    [TestMethod]
    public void Current_and_repeated_targets_are_consumed_in_order_and_completed_plan_cannot_restart()
    {
        using var session = Session();
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Waypoint(new(1)), new PlanStop.Waypoint(new(1)), new PlanStop.Waypoint(new(2)), new PlanStop.Waypoint(new(2))])));
        Assert.AreEqual(0, Train(session).NextStopIndex);
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        var startEvents = AdvanceUntil(session, t => t.Position is TrainPosition.Edge { Id.Value: 1 });
        Assert.AreEqual(2, Train(session).NextStopIndex);
        Assert.AreEqual(TravelDirection.Forward, ((TrainPosition.Edge)Train(session).Position).Direction);
        Assert.IsFalse(startEvents.Any(e => e is CoreEvent.TrainArrived));
        var arrivals = AdvanceUntil(session, t => t.Activity == TrainActivity.Stopped);
        CollectionAssert.AreEqual(new NodeId[] { new(2) }, arrivals.OfType<CoreEvent.TrainArrived>().Select(e => e.Node).ToArray());
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.IsNotNull(Train(session).Plan);
        Assert.IsNull(Train(session).NextStopIndex);
        Rejected(session, new CoreCommand.StartTrain(new(1)), "plan_completed");
    }

    [TestMethod]
    public void Endpoint_step_discards_remainder_retains_speed_and_member_locations_follow_the_train()
    {
        using var session = Moving(Plan(3), 9.99, 2);
        var old = session.Read();
        var result = session.Advance(1);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(WorldFixture.Node(new(2), NodeTravelPhase.Approaching, 0, enteredFrom: new(1), nextEdge: new(2)), Train(session).Position);
        Assert.AreEqual(Math.Sqrt(6), Train(session).SpeedMetersPerSecond, 1e-12);
        Assert.AreEqual(new EdgeId(1), ((TrainPosition.Node)Train(session).Position).EnteredFrom);
        Assert.AreEqual(NodeTravelPhase.Approaching, ((TrainPosition.Node)Train(session).Position).Phase);
        var events = result.Events.Select(e => e.Event).Where(e => e is CoreEvent.TrainArrived or CoreEvent.TrainActivityChanged).ToArray();
        Assert.AreEqual(1, events.Length);
        Assert.IsInstanceOfType<CoreEvent.TrainArrived>(events[0]);
        Assert.IsInstanceOfType<TrainPosition.Edge>(((Lookup<TrainView>.Found)old.FindTrain(new(1))).Value.Position);
        AdvanceUntil(session, t => t.Position is TrainPosition.Edge { Id.Value: 2 });
        Assert.AreEqual(new EdgeId(2), ((TrainPosition.Edge)Train(session).Position).Id);
        var location = ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Edge(new(2), 0))).Value;
        CollectionAssert.AreEqual(new TrainId[] { new(1) }, location.Trains.ToArray());
        CollectionAssert.AreEqual(new InstanceId[] { new InstanceId.Vehicle(new(1)) }, location.Instances.ToArray());
        var member = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(((TrainPosition.Edge)Train(session).Position).DistanceMeters, ((WorldLocation.Edge)member.Location).DistanceMeters);
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(session).Position);
        Assert.IsTrue(Train(session).RecentReports.IsEmpty);
        Assert.AreEqual(EncounterAction.Retreat, Train(session).Engagement.Encounter);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Braking_for_station_beyond_waypoint_preserves_hand_calculated_speed_in_both_directions(bool reverse)
    {
        // b=1，从初速 √40 制动驶过10米后 v²=20；节点有长度使制动起点略后移。
        using var session = Moving(Plan(reverse ? 1UL : 3UL), reverse ? 10 : 0, Math.Sqrt(40), reverse);
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Waypoint(new(2)), new PlanStop.Station(new(reverse ? 1UL : 3UL), 3, [])])));
        AdvanceUntil(session, t => t.Position is TrainPosition.Node { Id.Value: 2, Phase: NodeTravelPhase.Approaching });
        var addedDistance = ApproachLength + DepartureLength + ApproachLength + (reverse ? PlatformLength / 2 : 0);
        Assert.AreEqual(Math.Sqrt(20 + 2 * addedDistance), Train(session).SpeedMetersPerSecond, 1e-11);
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
        AdvanceUntil(session, t => t.Position is TrainPosition.Edge { } edge && edge.Id == new EdgeId(reverse ? 1UL : 2UL));
        Assert.AreEqual(1, Train(session).NextStopIndex);
        Assert.AreEqual(reverse ? TravelDirection.Reverse : TravelDirection.Forward, ((TrainPosition.Edge)Train(session).Position).Direction);
    }

    [TestMethod]
    public void Returning_on_the_entering_edge_flips_facing_only_when_it_really_enters()
    {
        using var session = Moving(new(false, [new PlanStop.Station(new(1), 2, [])]), 9.99, 2);
        AdvanceUntil(session, t => t.Position is TrainPosition.Node { Phase: NodeTravelPhase.Docked });
        Assert.AreEqual(0d, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(TrainOrderFacing.FirstEndLeading, Train(session).OrderFacing);
        AdvanceUntil(session, t => t.Position is TrainPosition.Edge { Direction: TravelDirection.Reverse });
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, Train(session).OrderFacing);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)Train(session).Position).Direction);
    }

    [TestMethod]
    public void Late_manual_stop_brakes_to_the_front_node_without_disbanding_on_the_edge()
    {
        using var session = Moving(Plan(3), 9.99, 2);
        Rejected(session, new CoreCommand.DisbandTrain(new(1)), "train_not_stationary_at_node");
        Accepted(session, new CoreCommand.StopTrain(new(1)));
        var events = AdvanceUntil(session, t => t.Activity == TrainActivity.Stopped);
        Assert.AreEqual(WorldFixture.Node(new(2), NodeTravelPhase.Docked, ApproachLength, enteredFrom: new(1), nextEdge: new(2)), Train(session).Position);
        Assert.AreEqual(TrainActivity.Stopped, Train(session).Activity);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(1, events.OfType<CoreEvent.TrainArrived>().Count());
        Assert.AreEqual(1, events.OfType<CoreEvent.TrainActivityChanged>().Count());
    }

    [TestMethod]
    public void Replacing_on_edge_first_reaches_the_forward_node_and_can_force_a_late_stop()
    {
        using var session = Moving(Plan(3), 9.99, 2);
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(session).Position);
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
        AdvanceUntil(session, t => t.Activity == TrainActivity.Stopped);
        Assert.AreEqual(WorldFixture.Node(new(2), NodeTravelPhase.Docked, ApproachLength, enteredFrom: new(1)), Train(session).Position);
        Assert.AreEqual(TrainActivity.Stopped, Train(session).Activity);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.IsNull(Train(session).NextStopIndex);
    }

    [TestMethod]
    public void Service_pause_and_resume_preserve_dwell_and_replace_abandons_remaining_service()
    {
        using var session = Session();
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Station(new(1), 3, []), new PlanStop.Waypoint(new(2))])));
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        AdvanceUntil(session, t => t.Activity == TrainActivity.Servicing);
        session.Advance(1);
        Assert.AreEqual(2UL, Train(session).RemainingDwellSteps);
        Accepted(session, new CoreCommand.StopTrain(new(1)));
        session.Advance(4);
        Assert.AreEqual(2UL, Train(session).RemainingDwellSteps);
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.Servicing, Train(session).Activity);
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
        Assert.AreEqual(0UL, Train(session).RemainingDwellSteps);
        Assert.AreEqual(0, Train(session).NextStopIndex);
        Assert.AreEqual(1, session.Read().World.Nodes.Single(n => n.Id == new NodeId(1)).OccupiedPlatforms);
        AdvanceUntil(session, t => t.Position is TrainPosition.Edge);
        Assert.IsTrue(session.Read().World.Nodes.All(n => n.OccupiedPlatforms == 0));
    }

    [TestMethod]
    public void Stationary_servicing_train_can_disband_without_a_stop_command()
    {
        using var session = Session();
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(new(1), 5, [])])));
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        Accepted(session, new CoreCommand.DisbandTrain(new(1)));
        Assert.IsTrue(session.State.World.Trains.IsEmpty);
    }

    [TestMethod]
    public void No_route_stops_at_node_and_retries_when_track_becomes_enterable()
    {
        var state = State();
        using var blocked = new CoreSession(state with { World = state.World with
        { Edges = state.World.Edges.Select(e => e with { Tracks = 0 }).ToImmutableArray() } });
        Accepted(blocked, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        var start = Accepted(blocked, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(TrainActivity.NoRoute, Train(blocked).Activity);
        Assert.AreEqual("no_route", ((CoreEvent.TrainActivityChanged)start.Events.Single().Event).Reasons.Single().Code);
        Assert.IsFalse(blocked.Advance(3).Events.Any(e => e.Event is CoreEvent.TrainArrived or CoreEvent.TrainActivityChanged));
        using var available = new CoreSession(blocked.State with { World = blocked.State.World with
        { Edges = blocked.State.World.Edges.Select(e => e with { Tracks = 1 }).ToImmutableArray() } });
        Assert.IsNull(available.Advance(1).Fault);
        Assert.AreEqual(TrainActivity.Running, Train(available).Activity);
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(available).Position);
    }

    [TestMethod]
    public void Moving_train_stops_at_front_node_when_the_next_edge_loses_its_tracks_and_capture_decodes()
    {
        using var moving = Moving(Plan(3), 9.99, 2);
        var state = moving.State;
        using var session = new CoreSession(state with { World = state.World with
        { Edges = state.World.Edges.Select(e => e.Id == new EdgeId(2) ? e with { Tracks = 0 } : e).ToImmutableArray() } });
        var events = AdvanceUntil(session, t => t.Activity == TrainActivity.NoRoute);
        Assert.AreEqual(new NodeId(2), ((TrainPosition.Node)Train(session).Position).Id);
        Assert.AreEqual(0d, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(TrainActivity.NoRoute, Train(session).Activity);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(new EdgeId(1), ((TrainPosition.Node)Train(session).Position).EnteredFrom);
        Assert.IsNull(((TrainPosition.Node)Train(session).Position).NextEdge);
        Assert.AreEqual(1, events.OfType<CoreEvent.TrainArrived>().Count());
        var activity = events.OfType<CoreEvent.TrainActivityChanged>().Single();
        Assert.AreEqual(TrainActivity.Running, activity.Before);
        Assert.AreEqual(TrainActivity.NoRoute, activity.After);
        Assert.AreEqual("no_route", activity.Reasons.Single().Code);
        var capture = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(capture.Checkpoint, Success(CoreFactory.Create().DecodeState(capture.Encode())).Checkpoint);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_cargo_after_restore_or_republication_is_visible_and_prevents_start(bool restore)
    {
        using var original = LoadedSession();
        Accepted(original, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        var missing = Content with { Identity = new(new string('E', 64)),
            Goods = Content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)).ToImmutableArray(), MissingReferences =
            [new(new CoreDefinitionReference.Voxel(Material), "Content.Goods")] };
        var saved = Success(CoreFactory.Create().DecodeState(Success(original.Capture(CapturePurpose.Save)).Encode()));
        using var candidate = Success(CoreFactory.Create().PrepareRestore(saved, missing, Options));
        using var session = (CoreSession)candidate.Activate();
        if (!restore) Accepted(session, new CoreCommand.PublishContent(missing with { Identity = new(new string('F', 64)) }));
        var train = Train(session);
        Assert.AreEqual(1.0, train.MassTons);
        Assert.AreEqual(100.0, train.Acceleration);
        var problem = train.Blockers.Single();
        Assert.AreEqual("content_missing", problem.Code);
        Assert.AreEqual("Train.Inventory", problem.Field);
        CollectionAssert.AreEqual(new[] { "voxel:" + Material.Value }, problem.RelatedIdentities.ToArray());
        Rejected(session, new CoreCommand.StartTrain(new(1)), "content_missing", "Train.Inventory",
            new WorldObjectId.Train(new(1)));
    }

    [TestMethod]
    public void Running_with_missing_cargo_reaches_station_and_waits_after_dwell_then_retries()
    {
        using var original = LoadedSession();
        Accepted(original, new CoreCommand.ReplaceRunPlan(new(1), new(true,
            [new PlanStop.Station(new(2), 2, []), new PlanStop.Waypoint(new(3))])));
        Accepted(original, new CoreCommand.StartTrain(new(1)));
        original.Advance(1);
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(original).Position);
        var missing = Content with { Identity = new(new string('E', 64)),
            Goods = Content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Voxel(Material), "Content.Goods")] };
        var saved = Success(CoreFactory.Create().DecodeState(Success(original.Capture(CapturePurpose.Save)).Encode()));
        using var candidate = Success(CoreFactory.Create().PrepareRestore(saved, missing, Options));
        using var session = (CoreSession)candidate.Activate();
        Accepted(session, new CoreCommand.PublishContent(missing with { Identity = new(new string('F', 64)) }));
        AdvanceUntil(session, t => t.Activity == TrainActivity.Servicing);
        session.Advance(2);
        Assert.AreEqual(TrainActivity.Waiting, Train(session).Activity);
        Assert.AreEqual("content_missing", Train(session).Blockers.Single().Code);
        Assert.IsFalse(session.Advance(3).Events.Any(e => e.Event is CoreEvent.TrainArrived or CoreEvent.TrainActivityChanged));
        Accepted(session, new CoreCommand.PublishContent(Content));
        session.Advance(1);
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
    }

    private static CoreState State()
    {
        var state = WorldFixture.State(content: Content);
        var node = state.World.Nodes[0] with { Position = new(0, 0) };
        return state with { World = state.World with
        {
            Nodes = [node, node with { Id = new(2), Position = new(1, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
                node with { Id = new(3), Position = new(2, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 }],
            Edges = [new(new(1), new(1), new(2), 10, node.Terrain, 1, 2, [], []),
                new(new(2), new(2), new(3), 10, node.Terrain, 1, 2, [], [])],
            Next = state.World.Next with { Node = 4, Edge = 3 }
        } };
    }
    private static CoreSession Session() => new(State());
    private static CoreState Hostile(CoreState state) => state with { World = state.World with
    {
        Settlements = state.World.Settlements.Select(s => s with { Relation = SettlementRelation.Hostile }).ToImmutableArray(),
        Nodes = state.World.Nodes.Select(n => n.Occupant is NodeOccupant.Settlement settlement
            ? n with { Occupant = settlement with { Relation = SettlementRelation.Hostile } } : n).ToImmutableArray(),
        Instances = state.World.Instances.Select(i => i.Id is InstanceId.Building
            ? i with { Ownership = SettlementRelation.Hostile } : i).ToImmutableArray()
    } };
    private static CoreSession LoadedSession()
    {
        var state = State();
        return new(state with { World = state.World with { Inventories = state.World.Inventories.Select(i =>
            i.Owner is InventoryOwner.Train ? i with { Goods = [new(new GoodId.Voxel(Material), 2)] } : i).ToImmutableArray() } });
    }
    private static CoreSession Moving(RunPlan plan, double distance, double speed, bool reverse = false)
    {
        var state = State();
        return new(state with { World = state.World with { Trains = [state.World.Trains[0] with
        { Position = new TrainPosition.Edge(new(reverse ? 2UL : 1UL), 1, reverse ? TravelDirection.Reverse : TravelDirection.Forward, distance),
            Plan = plan, NextStopIndex = 0, Activity = TrainActivity.Running, SpeedMetersPerSecond = speed,
            CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, []) }] } });
    }
    private static RunPlan Plan(ulong node) => new(false, [new PlanStop.Waypoint(new(node))]);
    private static TrainView Train(CoreSession session) => ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
    private static SubmissionResult.Completed Accepted(CoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        return result;
    }
    private static void Rejected(CoreSession session, CoreCommand command, string code, string? field = null, WorldObjectId? subject = null)
    {
        var before = session.State;
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        var problem = ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
        Assert.AreEqual(code, problem.Code);
        if (field is not null) Assert.AreEqual(field, problem.Field);
        if (subject is not null) Assert.AreEqual(subject, problem.Subject);
        Assert.AreSame(before.World, session.State.World);
        Assert.AreSame(before.Content, session.State.Content);
        Assert.AreEqual(before.NextCommandNumber, result.Receipt.Number);
        Assert.AreEqual(before.NextCommandNumber + 1, session.State.NextCommandNumber);
        Assert.AreEqual(before.Position, result.Receipt.Before);
        Assert.AreEqual(new SimulationPosition(before.Position.CompletedSteps, before.Position.Sequence + 1), result.Receipt.After);
        Assert.AreEqual(result.Receipt.After, session.State.Position);
        Assert.IsNull(session.State.Fault);
        Assert.IsNull(result.Diagnostic);
        Assert.IsTrue(result.Events.IsEmpty);
    }
    private static ImmutableArray<CoreEvent> AdvanceUntil(CoreSession session, Func<TrainView, bool> reached)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        for (var step = 0; step < 5000 && !reached(Train(session)); step++)
        {
            var result = session.Advance(1);
            Assert.IsNull(result.Fault);
            events.AddRange(result.Events.Select(e => e.Event));
        }
        Assert.IsTrue(reached(Train(session)), "有限步内未到达目标状态。");
        return events.ToImmutable();
    }
}

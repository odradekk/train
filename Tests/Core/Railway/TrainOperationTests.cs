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
        Assert.AreEqual(new NodeTransitView(null, null), composed.NodeTransit);
        CollectionAssert.AreEqual(new VehicleId[] { new(1) }, ((CoreEvent.TrainCompositionChanged)form.Events.Single().Event).Vehicles.ToArray());
        Assert.IsTrue(session.State.World.Settlements[0].ParkedVehicles.IsEmpty);
    }

    [TestMethod]
    public void Rejected_compositions_and_loaded_disband_are_atomic_but_recorded()
    {
        using var session = Session();
        Rejected(session, new CoreCommand.FormTrain([]), "composition_empty");
        Rejected(session, new CoreCommand.FormTrain([new(1), new(1)]), "vehicle_duplicate");
        Rejected(session, new CoreCommand.FormTrain([new(99)]), "vehicle_missing");
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
            Content = state.Content with { Goods = [] },
            World = state.World with
            {
                Inventories = state.World.Inventories.Select(i => i.Owner is InventoryOwner.Train
                    ? i with { Goods = [new(new GoodId.Voxel(Material), 1)] } : i).ToImmutableArray(),
                Edges = state.World.Edges.Select(e => e with { Tracks = 0 }).ToImmutableArray(),
                Trains = [state.World.Trains[0] with { Plan = new(true,
                    [new PlanStop.Station(new(1), 1, []), new PlanStop.Waypoint(new(2))]),
                    NextStopIndex = 0, Activity = TrainActivity.Servicing, RemainingDwellSteps = 1 }]
            }
        });
        var result = session.Advance(1);
        Assert.AreEqual(TrainActivity.Waiting, Train(session).Activity);
        Assert.AreEqual("content_missing", ((CoreEvent.TrainActivityChanged)result.Events.Single().Event).Reasons.Single().Code);
    }

    [TestMethod]
    public void Empty_and_degenerate_plans_and_invalid_stations_are_rejected()
    {
        using var session = Session();
        Rejected(session, new CoreCommand.StartTrain(new(1)), "plan_missing");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [])), "plan_empty");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(1), new(true,
            [new PlanStop.Waypoint(new(2)), new PlanStop.Waypoint(new(2))])), "plan_degenerate");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Station(new(2), 0, [])])), "station_unavailable");
        Rejected(session, new CoreCommand.ReplaceRunPlan(new(99), Plan(2)), "train_missing");
    }

    [TestMethod]
    public void Automatic_goods_rules_throw_before_taking_a_command_number()
    {
        using var session = Session();
        var before = session.State;
        var plan = new RunPlan(false, [new PlanStop.Station(new(1), 1,
            [new(new AutomaticGoodsAction.Load(), new GoodId.Voxel(Material), new(1), 1)])]);
        Assert.ThrowsExactly<NotImplementedException>(() => session.Submit(new CoreCommand.ReplaceRunPlan(new(1), plan), CommandSource.AutomationTest));
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
        var start = Accepted(session, new CoreCommand.StartTrain(new(1)));
        Assert.AreEqual(2, Train(session).NextStopIndex);
        Assert.AreEqual(new EdgeId(1), Train(session).NodeTransit!.NextEdge);
        Assert.AreEqual(1, start.Events.Length);
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
        Assert.AreEqual(new TrainPosition.Node(new(2)), Train(session).Position);
        Assert.AreEqual(Math.Sqrt(6), Train(session).SpeedMetersPerSecond, 1e-12);
        Assert.AreEqual(new NodeTransitView(new(1), new(2)), Train(session).NodeTransit);
        Assert.AreEqual(1, result.Events.Length);
        Assert.IsInstanceOfType<CoreEvent.TrainArrived>(result.Events[0].Event);
        Assert.IsInstanceOfType<TrainPosition.Edge>(((Lookup<TrainView>.Found)old.FindTrain(new(1))).Value.Position);
        session.Advance(1);
        Assert.AreEqual(new EdgeId(2), ((TrainPosition.Edge)Train(session).Position).Id);
        var location = ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Edge(new(2), 0))).Value;
        CollectionAssert.AreEqual(new TrainId[] { new(1) }, location.Trains.ToArray());
        CollectionAssert.AreEqual(new InstanceId[] { new InstanceId.Vehicle(new(1)) }, location.Instances.ToArray());
        var member = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(((TrainPosition.Edge)Train(session).Position).DistanceMeters, ((WorldLocation.Edge)member.Location).DistanceMeters);
        Assert.IsNull(Train(session).NodeTransit);
        Assert.IsNull(Train(session).CurrentRun);
        Assert.IsTrue(Train(session).RecentReports.IsEmpty);
        Assert.AreEqual(EncounterAction.Retreat, Train(session).Engagement.Encounter);
    }

    [TestMethod]
    public void Returning_on_the_entering_edge_flips_facing_only_when_it_really_enters()
    {
        using var session = Moving(new(false, [new PlanStop.Station(new(1), 2, [])]), 9.99, 2);
        session.Advance(1);
        Assert.AreEqual(new EdgeId(1), Train(session).NodeTransit!.NextEdge);
        Assert.AreEqual(TrainOrderFacing.FirstEndLeading, Train(session).OrderFacing);
        session.Advance(1);
        Assert.AreEqual(TrainOrderFacing.LastEndLeading, Train(session).OrderFacing);
        Assert.AreEqual(TravelDirection.Reverse, ((TrainPosition.Edge)Train(session).Position).Direction);
    }

    [TestMethod]
    public void Late_manual_stop_brakes_to_the_front_node_without_disbanding_on_the_edge()
    {
        using var session = Moving(Plan(3), 9.99, 2);
        Rejected(session, new CoreCommand.DisbandTrain(new(1)), "train_not_stationary_at_node");
        Accepted(session, new CoreCommand.StopTrain(new(1)));
        var result = session.Advance(1);
        Assert.AreEqual(new TrainPosition.Node(new(2)), Train(session).Position);
        Assert.AreEqual(TrainActivity.Stopped, Train(session).Activity);
        Assert.AreEqual(0.0, Train(session).SpeedMetersPerSecond);
        Assert.AreEqual(2, result.Events.Length);
        Assert.IsInstanceOfType<CoreEvent.TrainArrived>(result.Events[0].Event);
        Assert.IsInstanceOfType<CoreEvent.TrainActivityChanged>(result.Events[1].Event);
    }

    [TestMethod]
    public void Replacing_on_edge_first_reaches_the_forward_node_and_can_force_a_late_stop()
    {
        using var session = Moving(Plan(3), 9.99, 2);
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(session).Position);
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
        session.Advance(1);
        Assert.AreEqual(new TrainPosition.Node(new(2)), Train(session).Position);
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
        Assert.AreEqual(TrainActivity.Servicing, Train(session).Activity);
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
        Assert.IsTrue(blocked.Advance(3).Events.IsEmpty);
        using var available = new CoreSession(blocked.State with { World = blocked.State.World with
        { Edges = blocked.State.World.Edges.Select(e => e with { Tracks = 1 }).ToImmutableArray() } });
        Assert.IsNull(available.Advance(1).Fault);
        Assert.AreEqual(TrainActivity.Running, Train(available).Activity);
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(available).Position);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_cargo_after_publish_or_restore_is_visible_and_prevents_start(bool restore)
    {
        using var original = LoadedSession();
        Accepted(original, new CoreCommand.ReplaceRunPlan(new(1), Plan(2)));
        var missing = Content with { Identity = new(new string('E', 64)), Goods = [], MissingReferences =
            [new(new CoreDefinitionReference.Voxel(Material), "Content.Goods")] };
        ICoreSession session = original;
        if (restore)
        {
            var saved = Success(CoreFactory.Create().DecodeState(Success(original.Capture(CapturePurpose.Save)).Encode()));
            using var candidate = Success(CoreFactory.Create().PrepareRestore(saved, missing, Options));
            session = candidate.Activate();
        }
        else Accepted(original, new CoreCommand.PublishContent(missing));
        try
        {
            var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
            Assert.AreEqual(1.0, train.MassTons);
            Assert.AreEqual(100.0, train.Acceleration);
            var problem = train.Blockers.Single();
            Assert.AreEqual("content_missing", problem.Code);
            Assert.AreEqual("Train.Inventory", problem.Field);
            CollectionAssert.AreEqual(new[] { "voxel:" + Material.Value }, problem.RelatedIdentities.ToArray());
            Assert.IsInstanceOfType<CommandDecision.Rejected>(((SubmissionResult.Completed)session.Submit(new CoreCommand.StartTrain(new(1)), CommandSource.AutomationTest)).Receipt.Decision);
        }
        finally { if (restore) session.Dispose(); }
    }

    [TestMethod]
    public void Running_with_missing_cargo_reaches_station_and_waits_after_dwell_then_retries()
    {
        using var session = LoadedSession();
        Accepted(session, new CoreCommand.ReplaceRunPlan(new(1), new(true,
            [new PlanStop.Waypoint(new(2)), new PlanStop.Station(new(1), 2, [])])));
        Accepted(session, new CoreCommand.StartTrain(new(1)));
        session.Advance(1);
        Assert.IsInstanceOfType<TrainPosition.Edge>(Train(session).Position);
        Accepted(session, new CoreCommand.PublishContent(Content with { Identity = new(new string('E', 64)), Goods = [] }));
        AdvanceUntil(session, t => t.Activity == TrainActivity.Servicing);
        session.Advance(2);
        Assert.AreEqual(TrainActivity.Waiting, Train(session).Activity);
        Assert.AreEqual("content_missing", Train(session).Blockers.Single().Code);
        Assert.IsTrue(session.Advance(3).Events.IsEmpty);
        Accepted(session, new CoreCommand.PublishContent(Content));
        session.Advance(1);
        Assert.AreEqual(TrainActivity.Running, Train(session).Activity);
    }

    private static CoreState State()
    {
        var state = WorldFixture.State();
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
    private static CoreSession LoadedSession()
    {
        var state = State();
        return new(state with { World = state.World with { Inventories = state.World.Inventories.Select(i =>
            i.Owner is InventoryOwner.Train ? i with { Goods = [new(new GoodId.Voxel(Material), 2)] } : i).ToImmutableArray() } });
    }
    private static CoreSession Moving(RunPlan plan, double distance, double speed)
    {
        var state = State();
        return new(state with { World = state.World with { Trains = [state.World.Trains[0] with
        { Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, distance), Transit = null,
            Plan = plan, NextStopIndex = 0, Activity = TrainActivity.Running, SpeedMetersPerSecond = speed }] } });
    }
    private static RunPlan Plan(ulong node) => new(false, [new PlanStop.Waypoint(new(node))]);
    private static TrainView Train(CoreSession session) => ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
    private static SubmissionResult.Completed Accepted(CoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        return result;
    }
    private static void Rejected(CoreSession session, CoreCommand command, string code)
    {
        var before = session.State;
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.AreEqual(code, ((CommandDecision.Rejected)result.Receipt.Decision).Problems[0].Code);
        Assert.AreSame(before.World, session.State.World);
        Assert.AreEqual(before.NextCommandNumber + 1, session.State.NextCommandNumber);
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

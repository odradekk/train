using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

public sealed partial class TrainFormationTests
{
    [TestMethod]
    public void Replacement_preserves_train_identity_goods_plan_progress_and_run_totals()
    {
        var state = State();
        var original = state.World.Trains[0] with
        {
            Plan = new(false, [new PlanStop.Station(new(2), 3, [])]), NextStopIndex = 0,
            RemainingDwellSteps = 2, OrderFacing = TrainOrderFacing.LastEndLeading,
            CurrentRun = new(new(0, 0), new(7), new(11), 3, 17, 23, [])
        };
        state = state with { World = state.World with { Trains = [original],
            Inventories = state.World.Inventories.Select(i => i.Id == original.Inventory
                ? i with { Goods = [new(new GoodId.Voxel(Material), 7)] } : i).ToImmutableArray() } };
        using var session = new CoreSession(state);
        var before = Train(session);
        var goods = ((Lookup<InventoryView>.Found)session.Read().FindInventory(before.Inventory)).Value;

        var result = Accepted(session, new CoreCommand.ReplaceTrainFormation(before.Id, [new(2), new(1)]));

        var after = Train(session);
        CollectionAssert.AreEqual(new VehicleId[] { new(2), new(1) }, after.Vehicles.ToArray());
        Assert.AreEqual(before.Position, after.Position);
        Assert.AreEqual(before.Plan!.Loop, after.Plan!.Loop);
        Assert.IsTrue(before.Plan.Stops.SequenceEqual(after.Plan.Stops));
        Assert.IsTrue(before.Blockers.SequenceEqual(after.Blockers));
        Assert.IsTrue(before.RecentReports.SequenceEqual(after.RecentReports));
        Assert.AreEqual(before with { Vehicles = after.Vehicles, LengthMeters = after.LengthMeters,
            MassTons = after.MassTons, TractionKilonewtons = after.TractionKilonewtons,
            Acceleration = after.Acceleration, MaximumSpeedMetersPerSecond = after.MaximumSpeedMetersPerSecond,
            Plan = after.Plan, Blockers = after.Blockers, RecentReports = after.RecentReports }, after);
        var changed = (CoreEvent.TrainCompositionChanged)result.Events.Single().Event;
        Assert.AreEqual(before.Id, changed.Train);
        CollectionAssert.AreEqual(new VehicleId[] { new(2), new(1) }, changed.Vehicles.ToArray());
        var inventory = ((Lookup<InventoryView>.Found)session.Read().FindInventory(after.Inventory)).Value;
        Assert.AreEqual(goods.Id, inventory.Id);
        CollectionAssert.AreEqual(goods.Stacks.ToArray(), inventory.Stacks.ToArray());
        Assert.AreEqual(20L, inventory.Capacity);
        Assert.IsTrue(((CommandDecision.Accepted)result.Receipt.Decision).Created.IsEmpty);
        Assert.IsFalse(result.Events.Any(e => e.Event is CoreEvent.RunReported));
    }

    [TestMethod]
    public void Replacement_parks_removed_vehicle_at_the_same_settlement()
    {
        using var session = new CoreSession(State());
        Accepted(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(2)]));
        var removed = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(new InstanceDetails.Vehicle(SizeClass.Small, null), removed.Details);
        Assert.AreEqual(new WorldLocation.Node(((TrainPosition.Node)Train(session).Position).Id), removed.Location);
        Accepted(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(1), new(2)]));
        CollectionAssert.AreEqual(new VehicleId[] { new(1), new(2) }, Train(session).Vehicles.ToArray());
    }

    [TestMethod]
    [DataRow(8L, true)]
    [DataRow(9L, false)]
    public void Replacement_requires_capacity_for_all_existing_goods(long quantity, bool fits)
    {
        var state = State();
        state = state with { World = state.World with { Inventories = state.World.Inventories.Select(i =>
            i.Owner is InventoryOwner.Train ? i with { Goods = [new(new GoodId.Voxel(Material), quantity)] } : i).ToImmutableArray() } };
        using var session = new CoreSession(state);
        var command = new CoreCommand.ReplaceTrainFormation(new(1), [new(2)]);
        if (fits) Accepted(session, command);
        else ReplacementRejected(session, command, "insufficient_capacity", "Train.Inventory.Capacity");
    }

    [TestMethod]
    [DataRow("missing", "object_missing", "Train")]
    [DataRow("moving", "train_not_stationary_at_node", "Train.Position")]
    [DataRow("active", "invalid_state", "Train.Activity")]
    [DataRow("no-settlement", "settlement_unavailable", "Train.Position")]
    [DataRow("hostile", "access_denied", "Train.Position")]
    public void Replacement_checks_train_before_invalid_vehicle_list(string scenario, string code, string field)
    {
        var state = State();
        var train = state.World.Trains[0];
        train = scenario switch
        {
            "moving" => train with { SpeedMetersPerSecond = 1 },
            "active" => train with { Activity = TrainActivity.Waiting },
            "no-settlement" => train with { Position = global::Train.Core.Tests.World.WorldFixture.Node(new(2)) },
            _ => train
        };
        state = state with { World = state.World with { Trains = [train],
            Settlements = state.World.Settlements.Select(s => scenario == "hostile" ? s with { Relation = SettlementRelation.Hostile } : s).ToImmutableArray() } };
        using var session = new CoreSession(state);
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(new(scenario == "missing" ? 99UL : 1UL), []), code, field);
    }

    [TestMethod]
    public void Replacement_rejects_empty_duplicate_missing_and_nonowned_vehicles_atomically()
    {
        using var session = new CoreSession(State());
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(new(1), []), "composition_empty", "OrderedVehicles");
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(99), new(99)]), "vehicle_duplicate", "OrderedVehicles");
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(99)]), "object_missing", "OrderedVehicles");
        var state = State();
        state = state with { World = state.World with { Instances = state.World.Instances.Select(i =>
            i.Id == new InstanceId.Vehicle(new(2)) ? i with { Ownership = SettlementRelation.NonHostile } : i).ToImmutableArray() } };
        using var nonowned = new CoreSession(state);
        ReplacementRejected(nonowned, new CoreCommand.ReplaceTrainFormation(new(1), [new(1), new(2)]), "not_owned", "OrderedVehicles");
    }

    [TestMethod]
    [DataRow(5L, true)]
    [DataRow(6L, false)]
    public void Replacement_capacity_includes_internal_reservations(long reserved, bool fits)
    {
        // 当前公开命令不会给列车仓库创建预留；这里直接覆盖容量规则，不作为存档恢复证据。
        var state = State();
        state = state with { World = state.World with { Inventories = state.World.Inventories.Select(i =>
            i.Owner is InventoryOwner.Train ? i with { Goods = [new(new GoodId.Voxel(Material), 3)],
                Reservations = [new(new(99), reserved, [new(new GoodId.Voxel(Material), reserved)])] } : i).ToImmutableArray() } };
        using var session = new CoreSession(state);
        var command = new CoreCommand.ReplaceTrainFormation(new(1), [new(2)]);
        if (fits)
        {
            Accepted(session, command);
            var inventory = ((Lookup<InventoryView>.Found)session.Read().FindInventory(Train(session).Inventory)).Value;
            Assert.AreEqual(5L, inventory.Reserved);
            Assert.AreEqual(3L, inventory.Used);
            Assert.AreEqual(0L, inventory.Available);
        }
        else ReplacementRejected(session, command, "insufficient_capacity", "Train.Inventory.Capacity");
    }

    [TestMethod]
    public void Replacement_rejects_vehicle_owned_by_another_train()
    {
        using var session = new CoreSession(State());
        Accepted(session, new CoreCommand.FormTrain([new(2)]));
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(2), new(1)]),
            "vehicle_in_train", "OrderedVehicles");
    }
    [TestMethod]
    [DataRow(ConstructionKind.Deconstruction, false)]
    [DataRow(ConstructionKind.Repair, true)]
    [DataRow(ConstructionKind.Refit, true)]
    public void Replacement_only_blocks_deconstruction_jobs(ConstructionKind kind, bool allowed)
    {
        var state = State();
        var job = new global::Train.Core.Construction.ConstructionJobState(new(1), new(1),
            new(new(1), CapabilityKind.Construction), SecondVehicle, new InstanceId.Vehicle(new(2)), null, 0,
            new("fixture:construction"), Revision, 10, 0, 0, [], [], 0, ConstructionState.Paused, 0, [], []) { Kind = kind };
        state = state with { World = state.World with { ConstructionJobs = [job] } };
        using var session = new CoreSession(state);
        var command = new CoreCommand.ReplaceTrainFormation(new(1), [new(2), new(1)]);
        if (allowed) Accepted(session, command);
        else ReplacementRejected(session, command, "instance_locked", "OrderedVehicles");
    }

    [TestMethod]
    [DataRow(TrainActivity.Stopped)]
    [DataRow(TrainActivity.Retreating)]
    public void Preview_and_replacement_reject_train_combat_and_retreat_before_vehicle_checks(TrainActivity activity)
    {
        var state = State();
        var train = state.World.Trains[0] with { Activity = activity };
        var combat = new global::Train.Core.Combat.CombatState(new(1),
            new WorldLocation.Node(((TrainPosition.Node)train.Position).Id), 0,
            [new(new CombatController.Train(train.Id), 0, false, 0, null)], []);
        state = state with { World = state.World with { Trains = [train],
            Combats = activity == TrainActivity.Stopped ? [combat] : [] } };
        using var session = new CoreSession(state);
        var preview = ((CoreResult<TrainFormationPreview>.Failure)session.Read()
            .PreviewTrainFormation([new(2), new(1)], train.Id)).Problems.Single();
        Assert.AreEqual("invalid_state", preview.Code);
        Assert.AreEqual("Train.Activity", preview.Field);
        Assert.AreEqual(new WorldObjectId.Train(train.Id), preview.Subject);
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(train.Id, [new(2), new(1)]),
            "invalid_state", "Train.Activity");
    }

    [TestMethod]
    public void Replacement_rejects_a_parked_vehicle_in_combat()
    {
        var state = State();
        var node = ((TrainPosition.Node)state.World.Trains[0].Position).Id;
        var combat = new global::Train.Core.Combat.CombatState(new(1), new WorldLocation.Node(node), 0,
            [new(new CombatController.Settlement(new(1)), 0, false, 0, null)], []);
        state = state with { World = state.World with { Combats = [combat] } };
        using var session = new CoreSession(state);
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(2), new(1)]),
            "invalid_state", "OrderedVehicles");
    }
    [TestMethod]
    public void Replacement_rejects_a_vehicle_parked_at_another_node()
    {
        using var original = TrainRecoveryFixture.New();
        var state = ((CoreSession)original).State;
        var train = state.World.Trains[0];
        var source = state.World.Settlements.Single(s => s.Node == ((TrainPosition.Node)train.Position).Id);
        var destination = state.World.Settlements.First(s => s.Id != source.Id);
        var instance = state.World.Instances.Single(i => i.Id == new InstanceId.Vehicle(train.Vehicles[0]));
        state = state with { World = state.World with
        {
            Instances = state.World.Instances.Add(instance with { Id = new InstanceId.Vehicle(new(99)),
                Details = ((InstanceDetails.Vehicle)instance.Details) with { Train = null } }),
            Settlements = state.World.Settlements.Select(s => s.Id == destination.Id
                ? s with { ParkedVehicles = s.ParkedVehicles.Add(new(99)) } : s).ToImmutableArray()
        } };
        using var session = new CoreSession(state);
        ReplacementRejected(session, new CoreCommand.ReplaceTrainFormation(train.Id, [train.Vehicles[0], new(99)]),
            "vehicles_not_colocated", "OrderedVehicles");
    }
    private static void ReplacementRejected(CoreSession session, CoreCommand command, string code, string field)
    {
        var before = session.State;
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        var problem = ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
        Assert.AreEqual(code, problem.Code);
        Assert.AreEqual(field, problem.Field);
        Assert.AreSame(before.World, session.State.World);
        Assert.AreSame(before.Content, session.State.Content);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNull(result.Diagnostic);
    }
}

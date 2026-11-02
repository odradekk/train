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
    [DataRow(false, "control", "control_missing")]
    [DataRow(true, "control", "control_missing")]
    [DataRow(false, "traction", "traction_missing")]
    [DataRow(true, "traction", "traction_missing")]
    [DataRow(false, "below", "overloaded")]
    [DataRow(true, "below", "overloaded")]
    [DataRow(false, "exact", null)]
    [DataRow(true, "exact", null)]
    [DataRow(false, "above", null)]
    [DataRow(true, "above", null)]
    [DataRow(true, "cargo-missing", "content_missing")]
    public void Preview_departure_matches_start_after_forming_or_replacing(bool replace, string scenario, string? expectedCode)
    {
        using var session = new CoreSession(DepartureState(scenario));
        if (!replace) Accepted(session, new CoreCommand.DisbandTrain(new(1)));
        var preview = Success(session.Read().PreviewTrainFormation([new(2), new(1)], replace ? new(1) : null));
        Assert.IsTrue(preview.Blockers.IsEmpty);
        Assert.AreEqual(expectedCode, preview.DepartureProblem?.Code);
        if (scenario == "cargo-missing")
            CollectionAssert.AreEqual(new[] { "voxel:fixture:missing-a", "voxel:fixture:missing-z" },
                preview.DepartureProblem!.RelatedIdentities.ToArray());
        Assert.AreEqual(replace && expectedCode is not null ? new WorldObjectId.Train(new(1)) : null, preview.DepartureProblem?.Subject);
        var formed = Accepted(session, replace
            ? new CoreCommand.ReplaceTrainFormation(new(1), [new(2), new(1)])
            : new CoreCommand.FormTrain([new(2), new(1)]));
        var id = replace ? new TrainId(1) : ((CommandDecision.Accepted)formed.Receipt.Decision).Created.OfType<WorldObjectId.Train>().Single().Id;
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;
        Assert.AreEqual(preview.LengthMeters, train.LengthMeters);
        Assert.AreEqual(preview.MassTons, train.MassTons);
        Assert.AreEqual(preview.TractionKilonewtons, train.TractionKilonewtons);
        Accepted(session, new CoreCommand.ReplaceRunPlan(id, new(false,
            [new PlanStop.Station(((TrainPosition.Node)train.Position).Id, 1, [])])));
        var start = (SubmissionResult.Completed)session.Submit(new CoreCommand.StartTrain(id), CommandSource.AutomationTest);
        if (expectedCode is null) Assert.IsInstanceOfType<CommandDecision.Accepted>(start.Receipt.Decision);
        else
        {
            var actual = ((CommandDecision.Rejected)start.Receipt.Decision).Problems.Single();
            Assert.AreEqual(new WorldObjectId.Train(id), actual.Subject);
            SameProblem(preview.DepartureProblem!, actual with { Subject = preview.DepartureProblem!.Subject });
        }
    }

    [TestMethod]
    public void Preview_and_replace_reject_invalid_arrays_and_zero_ids_at_the_boundary()
    {
        using var session = new CoreSession(State());
        Assert.ThrowsExactly<ArgumentException>(() => session.Read().PreviewTrainFormation(default, new(1)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Read().PreviewTrainFormation([new(0)], new(1)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Read().PreviewTrainFormation([new(1)], new(0)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.ReplaceTrainFormation(new(1), default), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.ReplaceTrainFormation(new(1), [new(0)]), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.ReplaceTrainFormation(new(0), [new(1)]), CommandSource.AutomationTest));
    }

    [TestMethod]
    [DataRow("empty", "composition_empty")]
    [DataRow("duplicate", "vehicle_duplicate")]
    [DataRow("missing", "object_missing")]
    public void Preview_and_replace_agree_on_composition_failures(string scenario, string code)
    {
        using var session = new CoreSession(State());
        ImmutableArray<VehicleId> vehicles = scenario switch
        { "empty" => [], "duplicate" => [new(99), new(99)], _ => [new(99)] };
        var before = session.State.World;
        var problem = ((CoreResult<TrainFormationPreview>.Failure)session.Read().PreviewTrainFormation(vehicles, new(1))).Problems.Single();
        Assert.AreEqual(code, problem.Code);
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.ReplaceTrainFormation(new(1), vehicles), CommandSource.AutomationTest);
        SameProblem(problem, ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single());
        Assert.AreSame(before, session.State.World);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void Preview_keeps_metrics_when_identity_or_capacity_blocks_the_change(bool replace, bool inventoryExhausted)
    {
        var state = State();
        state = state with { World = state.World with
        {
            Next = state.World.Next with { Train = inventoryExhausted ? state.World.Next.Train : ulong.MaxValue,
                Inventory = inventoryExhausted ? ulong.MaxValue : state.World.Next.Inventory },
            Inventories = state.World.Inventories.Select(i => i.Owner is InventoryOwner.Train
                ? i with { Goods = [new(new GoodId.Voxel(Material), 9)] } : i).ToImmutableArray()
        } };
        using var session = new CoreSession(state);
        if (!replace)
        {
            // 解散要求仓库为空；身份耗尽与货物容量是两种独立的预览阻碍。
            var empty = state with { World = state.World with { Inventories = state.World.Inventories.Select(i => i.Owner is InventoryOwner.Train
                ? i with { Goods = [] } : i).ToImmutableArray() } };
            using var newFormation = new CoreSession(empty);
            Accepted(newFormation, new CoreCommand.DisbandTrain(new(1)));
            var preview = Success(newFormation.Read().PreviewTrainFormation([new(2), new(1)], null));
            Assert.AreEqual("identity_exhausted", preview.Blockers.Single().Code);
            Assert.AreEqual(7.0, preview.LengthMeters);
            Assert.AreEqual(30.0, preview.MassTons);
            Assert.AreEqual(50.0, preview.TractionKilonewtons);
            Assert.AreEqual(20L, preview.Capacity);
        }
        else
        {
            var preview = Success(session.Read().PreviewTrainFormation([new(2)], new(1)));
            Assert.AreEqual("insufficient_capacity", preview.Blockers.Single().Code);
            Assert.AreEqual(5.0, preview.LengthMeters);
            Assert.AreEqual(29.0, preview.MassTons);
            Assert.AreEqual(20.0, preview.TractionKilonewtons);
            Assert.AreEqual(8L, preview.Capacity);
        }
    }

    [TestMethod]
    [DataRow("destroyed")]
    [DataRow("missing")]
    public void Preview_and_actual_train_ignore_destroyed_or_missing_parts(string scenario)
    {
        var state = State();
        state = state with { World = state.World with
        { Instances = state.World.Instances.Select(i => i.Id == new InstanceId.Vehicle(new(2)) && scenario == "destroyed"
            ? i with { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() } : i).ToImmutableArray() },
            Content = state.Content with { Designs = state.Content.Designs.Select(d => d.Design == SecondVehicle && scenario == "missing"
                ? d with { Parts = d.Parts.Select(p => p with { Missing = true }).ToImmutableArray() } : d).ToImmutableArray() } };
        using var session = new CoreSession(state);
        var preview = Success(session.Read().PreviewTrainFormation([new(2), new(1)], new(1)));
        Assert.AreEqual(7.0, preview.LengthMeters);
        Assert.AreEqual(30.0, preview.MassTons);
        Assert.AreEqual(30.0, preview.TractionKilonewtons);
        Assert.AreEqual(12L, preview.Capacity);
        Accepted(session, new CoreCommand.ReplaceTrainFormation(new(1), [new(2), new(1)]));
        var train = Train(session);
        Assert.AreEqual(preview.LengthMeters, train.LengthMeters);
        Assert.AreEqual(preview.MassTons, train.MassTons);
        Assert.AreEqual(preview.TractionKilonewtons, train.TractionKilonewtons);
        Assert.AreEqual(preview.Capacity, ((Lookup<InventoryView>.Found)session.Read().FindInventory(train.Inventory)).Value.Capacity);
    }

    [TestMethod]
    public void Preview_allows_capacity_exactly_equal_to_used_and_reserved()
    {
        // 公开命令不会创建列车仓库预留；这是预览容量计算的内部状态边界。
        var state = State();
        state = state with { World = state.World with { Inventories = state.World.Inventories.Select(i =>
            i.Owner is InventoryOwner.Train ? i with { Goods = [new(new GoodId.Voxel(Material), 3)],
                Reservations = [new(new(99), 5, [new(new GoodId.Voxel(Material), 5)])] } : i).ToImmutableArray() } };
        using var session = new CoreSession(state);
        var preview = Success(session.Read().PreviewTrainFormation([new(2)], new(1)));
        Assert.AreEqual(8L, preview.Capacity);
        Assert.IsTrue(preview.Blockers.IsEmpty);
    }
    private static CoreState DepartureState(string scenario)
    {
        var state = State();
        var traction = scenario switch { "below" => 29, "above" => 31, "traction" => 0, _ => 30 };
        var designs = state.World.Designs.Select(d => d.Design == Vehicle || d.Design == SecondVehicle
            ? d with { Parts = d.Parts.Select(p => p with { Capabilities = d.Design == Vehicle
                ? scenario == "control" ? [new PartCapability.Propulsion(traction, 12), new PartCapability.Storage(12)]
                    : [new PartCapability.Control(), new PartCapability.Propulsion(traction, 12), new PartCapability.Storage(12)]
                : [new PartCapability.Storage(8)] }).ToImmutableArray() } : d).ToImmutableArray();
        return state with { Content = state.Content with { Designs = designs,
            RailwayRules = state.Content.RailwayRules.Select(r => r with { MinimumAcceleration = 1 }).ToImmutableArray() },
            World = state.World with { Designs = designs, Inventories = state.World.Inventories.Select(i =>
                i.Owner is InventoryOwner.Train && scenario == "cargo-missing" ? i with
                { Goods = [new(new GoodId.Voxel(new("fixture:missing-z")), 2), new(new GoodId.Voxel(new("fixture:missing-a")), 1)] } : i).ToImmutableArray() } };
    }

    private static void SameProblem(CoreProblem expected, CoreProblem actual)
    {
        Assert.AreEqual(expected.Code, actual.Code);
        Assert.AreEqual(expected.Field, actual.Field);
        Assert.AreEqual(expected.Subject, actual.Subject);
        CollectionAssert.AreEqual(expected.RelatedIdentities.ToArray(), actual.RelatedIdentities.ToArray());
    }
}

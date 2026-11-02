using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Production;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed partial class SettlementNetChangeTests
{
    [TestMethod]
    [DataRow(false, SettlementRelation.PlayerOwned)]
    [DataRow(false, SettlementRelation.NonHostile)]
    [DataRow(false, SettlementRelation.Hostile)]
    [DataRow(true, SettlementRelation.PlayerOwned)]
    public void Production_and_recovery_use_actual_work_without_a_market_rule(bool recovery, SettlementRelation relation)
    {
        var content = Content(work: 10, rate: 20, laborPerWork: 2, recovery: recovery,
            stock: [new(Input, 10), new(OtherProduct, 10)],
            inputs: [new(Input, 2), new(OtherProduct, 4)]);
        // Ten residents provide four labor: actual work is two/s, despite capability twenty/s.
        content = content with { PopulationRules = [content.PopulationRules[0] with { LaborRatio = .4 }] };
        using var original = Owned(content, relation);
        var missing = Changed(content) with { MarketRules = [], MissingReferences =
            [new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Market), "market")] };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(original.Capture(CapturePurpose.Save)), missing, WorldFixture.Options));
        using var session = candidate.Activate();
        AssertRate(session, Product, 0);
        Step(session, 2);
        Assert.AreEqual(2d, Read(session).ActualWorkPerSecond, 1e-12);
        // Current labor now permits five work/s; the estimate must keep the previous actual two/s.
        Publish(session, Changed(missing, 'E') with
        { PopulationRules = [missing.PopulationRules[0] with { LaborRatio = 1 }] });
        AssertRate(session, Input, -.4);
        AssertRate(session, OtherProduct, -.8);
        AssertRate(session, Product, .6);
    }

    [TestMethod]
    public void Extraction_adds_its_output_without_charging_deposit_as_inventory_input()
    {
        using var candidate = ExtractionFixture.Prepare(ExtractionFixture.Content(batch: 2, work: 10));
        using var session = candidate.Activate();
        Step(session, 2);
        Assert.AreEqual(60d, ExtractionFixture.Read(session).ActualWorkPerSecond, 1e-12);
        AssertRate(session, Input, 12);
    }

    [TestMethod]
    public void Current_plan_coefficients_apply_to_previous_actual_work_while_batch_stays_frozen()
    {
        var content = WithAlternate(Content(work: 10, rate: 2));
        using var session = Owned(content);
        Step(session, 2);
        var result = (SubmissionResult.Completed)session.Submit(
            new CoreCommand.SetProductionPlan(Facility, Alternate, true, 0), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        Assert.AreEqual(Process, Read(session).Batch!.Process);
        Assert.AreEqual(2d, Read(session).ActualWorkPerSecond, 1e-12);
        // New plan: five inputs and four outputs per ten work. Old output is no longer projected.
        AssertRate(session, Input, -1);
        AssertRate(session, OtherProduct, .8);
        AssertRate(session, Product, 0);
    }

    [TestMethod]
    public void Disabling_current_plan_excludes_still_running_frozen_batch()
    {
        using var session = Owned(Content(work: 10, rate: 2));
        Step(session, 2);
        var retained = SettlementView(session.Read());
        var result = (SubmissionResult.Completed)session.Submit(
            new CoreCommand.SetProductionPlan(Facility, Process, false, 0), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        Assert.AreEqual(2d, Read(session).ActualWorkPerSecond, 1e-12);
        Assert.IsNotNull(Read(session).Batch);
        AssertRate(session, Input, 0);
        AssertRate(session, Product, 0);
        Assert.AreEqual(.6, retained.Market.Goods.Single(g => g.Good == Product).ExpectedNetChangePerSecond, 1e-12);
    }

    [TestMethod]
    [DataRow(100L, ProductionState.WaitingForInputs)]
    [DataRow(1L, ProductionState.OutputBlocked)]
    public void Current_stopped_state_excludes_last_nonzero_work(long capacity, ProductionState expectedState)
    {
        using var session = Owned(Content(work: 1, rate: 60, capacity: capacity, stock: [new(Input, 2)]));
        Step(session, 2);
        Assert.AreEqual(60d, Read(session).ActualWorkPerSecond, 1e-12);
        Assert.AreEqual(expectedState, Read(session).State);
        AssertRate(session, Input, 0);
        AssertRate(session, Product, 0);
    }

    [TestMethod]
    public void Missing_process_excludes_retained_actual_work()
    {
        var content = Content(work: 10, rate: 2);
        using var original = Owned(content);
        Step(original, 2);
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var missing = Changed(content) with { Processes = [], MissingReferences =
            [new(new CoreDefinitionReference.Process(Process), "process")] };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, missing, WorldFixture.Options));
        using var session = candidate.Activate();
        Assert.AreEqual(2d, Read(session).ActualWorkPerSecond, 1e-12);
        Assert.AreEqual(ProductionState.ProcessMissing, Read(session).State);
        AssertRate(session, Input, 0);
        AssertRate(session, Product, 0);
    }

    [TestMethod]
    [DataRow(4L, -.9, 0d)]
    [DataRow(3L, -.4, -1d)]
    public void Fuel_uses_current_first_stocked_definition_and_merges_with_raw_material_consumption(
        long initialProduct, double productRate, double fuelRate)
    {
        var content = Content(work: 10, rate: 2, powerPerWork: 3,
            stock: [new(Product, initialProduct), new(Fuel, 10)], inputs: [new(Product, 2)], outputs: [new(OtherProduct, 3)]);
        // Product sorts before Fuel: two units enter the batch, then one powers the actual work step.
        content = content with { Goods = content.Goods.Select(g => g.Good == Product
            ? g with { FuelEnergyPerUnit = 12 } : g.Good == Fuel
                ? g with { FuelEnergyPerUnit = 6 } : g).Reverse().ToImmutableArray() };
        using var session = Owned(content);
        Step(session, 2);
        AssertRate(session, Product, productRate); // -.4 material plus -.5 fuel only while stocked.
        AssertRate(session, Fuel, fuelRate);
        // Publishing a fuel definition changes this estimate immediately, without another work step.
        Publish(session, Changed(content) with { Goods = content.Goods.Select(g => g.Good == Product
            ? g with { FuelEnergyPerUnit = null } : g).ToImmutableArray() });
        AssertRate(session, Product, -.4);
        AssertRate(session, Fuel, -1);
    }

    [TestMethod]
    public void Stored_energy_with_no_stocked_fuel_does_not_invent_a_fuel_flow()
    {
        var content = Content(work: 10, rate: 2, powerPerWork: 3, stock: [new(Input, 10), new(Fuel, 1)]);
        using var session = Owned(content);
        Step(session, 2); // First work step opens one whole unit, leaving energy but no fuel stock.
        Assert.AreEqual(0L, Stock(session, Fuel));
        Assert.AreEqual(2d, Read(session).ActualWorkPerSecond, 1e-12);
        AssertRate(session, Product, .6);
        AssertRate(session, Fuel, 0);
    }

    [TestMethod]
    [DataRow(ProductionState.ZeroCapability)]
    [DataRow(ProductionState.ConditionsNotMet)]
    [DataRow(ProductionState.Locked)]
    public void Current_facility_unavailability_excludes_previous_actual_work(ProductionState unavailable)
    {
        var content = Content(work: 10, rate: 2);
        using var original = Owned(content);
        Step(original, 2);
        var state = original.State;
        var world = state.World;
        var instance = new InstanceId.Building(Facility.Building);
        if (unavailable == ProductionState.ZeroCapability)
            world = world with { Instances = world.Instances.Select(i => i.Id == instance
                ? i with { StructureDurability = 0 } : i).ToImmutableArray() };
        else if (unavailable == ProductionState.ConditionsNotMet)
            content = content with { Processes = [content.Processes[0] with { AllowedTerrainTypes = [WorldFixture.Hills] }] };
        else
        {
            var job = new ConstructionJobState(new(world.Next.Job), Settlement, Facility,
                world.Instances.Single(i => i.Id == instance).Design, instance, null, 0,
                world.Source.Configuration.Rules.Construction, WorldFixture.Revision, 1, 3, 0, [], [], 0,
                ConstructionState.Working, 0, [], [])
            { Funding = JobFunding.OwnInventory, Kind = ConstructionKind.Repair };
            world = world with { ConstructionJobs = [job] };
        }
        using var session = new CoreSession(state with { World = world, Content = content });
        Assert.AreEqual(2d, Read(session).ActualWorkPerSecond, 1e-12);
        Assert.AreEqual(unavailable, Read(session).State);
        AssertRate(session, Product, 0);
        AssertRate(session, Input, 0);
        AssertRate(session, Fuel, 0);
    }

    [TestMethod]
    public void Missing_output_definition_does_not_erase_known_process_quantities_or_stock_row()
    {
        var content = Content(work: 10, rate: 2, stock: [new(Input, 10), new(Product, 1)]);
        using var original = Owned(content);
        Step(original, 2);
        var missing = Changed(content) with
        {
            Goods = content.Goods.Where(g => g.Good != Product).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Voxel(((GoodId.Voxel)Product).Definition), "product")]
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(original.Capture(CapturePurpose.Save)), missing, WorldFixture.Options));
        using var session = candidate.Activate();
        Assert.AreEqual(ProductionState.Running, Read(session).State);
        AssertRate(session, Product, .6);
        AssertRate(session, Input, -.4);
    }

    private static SettlementView SettlementView(ICoreReadView view)
        => ((Lookup<SettlementView>.Found)view.FindSettlement(Settlement)).Value;

    private static void AssertRate(ICoreSession session, GoodId good, double expected)
        => Assert.AreEqual(expected, SettlementView(session.Read()).Market.Goods.Single(g => g.Good == good)
            .ExpectedNetChangePerSecond, 1e-12, good.ToString());
}

using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Settlements;
using Train.Core.Tests.Production;
using Train.Core.Tests.World;
using Train.Core.World;
using InventoryFixture = Train.Core.Tests.Inventory.InventoryFixture;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class MarketStockTargetsTests
{
    [TestMethod]
    [DataRow(6d, .5, 3d)]
    [DataRow(100d, .2, 2d)]
    public void Public_potential_consumption_shares_power_and_labor_without_mutating_world(
        double power, double laborRatio, double firstRate)
    {
        var content = TwoPlans(capacity: 100, firstInputs: 2, secondInputs: 2, powerPerWork: 2);
        content = content with
        {
            Processes = content.Processes.Select(p => p with { LaborPerWork = 1 }).ToImmutableArray(),
            PopulationRules = [content.PopulationRules[0] with { LaborRatio = laborRatio }],
            Designs = content.Designs.Select(d => d with { Parts = d.Parts.Select(p => p with
            { Capabilities = p.Capabilities.Select<PartCapability, PartCapability>(c => c switch
                {
                    PartCapability.Production => new PartCapability.Production(10),
                    PartCapability.Power => new PartCapability.Power(power),
                    _ => c
                }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray()
        };
        using var candidate = Prepare(content);
        using var session = (CoreSession)candidate.Activate();
        var before = session.State.World;
        var view = session.Read();
        var settlement = ((Lookup<SettlementView>.Found)view.FindSettlement(Settlement)).Value;
        var facilities = settlement.Facilities.Where(f => f.Process is not null).OrderBy(f => f.Priority).ToArray();
        Assert.AreEqual(2, facilities.Length);
        // Each batch consumes two materials for two work, and ten energy buys one fuel.
        Assert.AreEqual(firstRate, facilities[0].PotentialConsumption.Single(g => g.Good == Input).UnitsPerSecond, 1e-12);
        Assert.AreEqual(firstRate / 5, facilities[0].PotentialConsumption.Single(g => g.Good == Fuel).UnitsPerSecond, 1e-12);
        Assert.IsEmpty(facilities[1].PotentialConsumption);
        foreach (var facility in facilities)
        {
            var individual = ((Lookup<FacilityView>.Found)view.FindFacility(facility.Key)).Value;
            CollectionAssert.AreEqual(facility.PotentialConsumption.ToArray(), individual.PotentialConsumption.ToArray());
        }
        Assert.AreSame(before, session.State.World);
    }

    [TestMethod]
    public void Midperiod_publication_keeps_frozen_consumption_targets_but_uses_current_labor_ratio()
    {
        var content = Market(Content(enabled: false, stock: []));
        content = content with
        {
            PopulationRules = [content.PopulationRules[0] with
            {
                ConsumptionIntervalTicks = 120, StatisticsPeriodTicks = 720,
                Needs = content.PopulationRules[0].Needs.Select(n => n with { AmountPerPersonSecond = .2 }).ToImmutableArray()
            }],
            MarketRules = [content.MarketRules[0] with { BasicReserveHours = 1d / 3600 }]
        };
        using var candidate = Prepare(content);
        using var session = (CoreSession)candidate.Activate();
        Step(session);
        var frozen = session.State.World.Settlements.Single().PopulationState.Period!.Rule;
        var food = new GoodId.Voxel(new("fixture:food"));
        var clothing = new GoodId.Voxel(new("fixture:clothing"));
        AssertTargets(Calculate(session.State.World, content), new(food, 2), new(clothing, 2));
        var changed = Changed(content) with { PopulationRules = [content.PopulationRules[0] with
        {
            Revision = new(new string('A', 64)), LaborRatio = .8,
            Needs = content.PopulationRules[0].Needs.Select(n => n with { AmountPerPersonSecond = .4 }).ToImmutableArray()
        }] };
        Publish(session, changed);
        Assert.AreEqual(frozen, session.State.World.Settlements.Single().PopulationState.Period!.Rule);
        AssertTargets(Calculate(session.State.World, changed), new(food, 2), new(clothing, 2));
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(Settlement)).Value;
        Assert.AreEqual(8d, settlement.Population.AvailableLabor);
        var period = (PopulationExecution.InProgress)settlement.Population.Execution;
        Assert.IsTrue(period.Period.Needs.All(n => n.RequiredPerSecond == 2));
    }

    [TestMethod]
    public void Goods_with_value_but_no_enabled_use_have_no_target()
    {
        var content = Market(Content(enabled: false));
        AssertTargets(Calculate(State(content).World, content));
    }

    [TestMethod]
    public void Midperiod_potential_consumption_uses_current_labor_instead_of_frozen_ratio()
    {
        var content = Content(rate: 10, laborPerWork: 1);
        content = content with { PopulationRules = [content.PopulationRules[0] with
        { LaborRatio = .5, ConsumptionIntervalTicks = 120, StatisticsPeriodTicks = 720 }] };
        using var candidate = Prepare(content);
        using var session = (CoreSession)candidate.Activate();
        Step(session);
        var frozen = session.State.World.Settlements.Single().PopulationState.Period!.Rule;
        var changed = Changed(content) with { PopulationRules = [content.PopulationRules[0] with
        { Revision = new(new string('A', 64)), LaborRatio = .8 }] };
        Publish(session, changed);
        Assert.AreEqual(.5, frozen.LaborRatio);
        Assert.AreEqual(frozen, session.State.World.Settlements.Single().PopulationState.Period!.Rule);
        // Ten residents supply eight labor: eight work/second consumes eight inputs/second.
        Assert.AreEqual(8d, Read(session).PotentialConsumption.Single(g => g.Good == Input).UnitsPerSecond, 1e-12);
    }

    [TestMethod]
    public void Missing_inputs_and_blocked_outputs_keep_targets_but_disabled_plans_do_not()
    {
        var content = Market(Content(stock: []));
        var world = State(content).World;
        AssertTargets(Calculate(world, content), new GoodsQuantity(Input, 2));
        var settlement = world.Settlements.Single();
        var blocked = settlement with { ProductionFacilities = settlement.ProductionFacilities.Select(f =>
            f with { OutputBuffer = [new(Product, 3)] }).ToImmutableArray() };
        AssertTargets(Calculate(Replace(world, blocked), content), new GoodsQuantity(Input, 2));
        var disabled = blocked with { Template = blocked.Template with
        { ProductionPlans = blocked.Template.ProductionPlans.Select(p => p with { Enabled = false }).ToImmutableArray() } };
        AssertTargets(Calculate(Replace(world, disabled), content));
    }

    [TestMethod]
    public void Ordinary_material_stock_does_not_reduce_retained_target()
    {
        var empty = Market(Content(stock: []));
        var stocked = Market(Content(stock: [new(Input, 70), new(Product, 10)]));
        AssertTargets(Calculate(State(empty).World, empty), new GoodsQuantity(Input, 2));
        AssertTargets(Calculate(State(stocked).World, stocked), new GoodsQuantity(Input, 2));
    }

    [TestMethod]
    public void Active_batch_needs_only_frozen_remaining_energy_and_does_not_buy_inputs_twice()
    {
        var content = Market(Content(work: 20, powerPerWork: 1));
        content = content with { Goods = content.Goods.Select(g => g.Good == Fuel
            ? g with { FuelEnergyPerUnit = 4 } : g).ToImmutableArray() };
        var world = State(content).World;
        var settlement = world.Settlements.Single();
        settlement = settlement with { ProductionFacilities = settlement.ProductionFacilities.Select(f => f with
        { Batch = new(Process, WorldFixture.Revision, [new(Product, 3)], 10, 3, 0, 6) }).ToImmutableArray() };
        // The frozen batch needs (10 - 6) * 3 = 12 energy, hence three fuel units.
        AssertTargets(Calculate(Replace(world, settlement), content), new GoodsQuantity(Fuel, 3));
    }

    [TestMethod]
    public void Shared_energy_and_whole_fuel_surplus_are_spent_once_across_plans()
    {
        var content = TwoPlans(capacity: 10, firstInputs: 0, secondInputs: 0, powerPerWork: 3);
        var world = State(content).World;
        var settlement = world.Settlements.Single() with { FuelEnergyRemaining = 5 };
        // Two batches need six energy each; the pool supplies five and one fuel supplies ten.
        AssertTargets(Calculate(Replace(world, settlement), content), new GoodsQuantity(Fuel, 1));
    }

    [TestMethod]
    public void Failed_minimum_combination_preserves_capacity_and_shared_fuel_for_next_plan()
    {
        var content = TwoPlans(capacity: 3, firstInputs: 3, secondInputs: 2, powerPerWork: 3);
        var world = State(content).World;
        var settlement = world.Settlements.Single() with { FuelEnergyRemaining = 5 };
        AssertTargets(Calculate(Replace(world, settlement), content), new(Fuel, 1), new(OtherProduct, 2));
    }

    [TestMethod]
    public void Both_minimum_batches_precede_higher_priority_extra_reserves()
    {
        var content = TwoPlans(capacity: 5, firstInputs: 2, secondInputs: 2);
        content = content with { MarketRules = [content.MarketRules[0] with { ProductionReserveHours = 1d / 900 }] };
        // At one work/second and two work/batch, four seconds require four materials.
        // The first pass reserves two each; only one extra unit fits for the first plan.
        AssertTargets(Calculate(State(content).World, content), new(Input, 3), new(OtherProduct, 2));
    }

    [TestMethod]
    public void Extra_reserves_subtract_minimum_batches_when_capacity_is_sufficient()
    {
        var content = TwoPlans(capacity: 10, firstInputs: 2, secondInputs: 2);
        content = content with { MarketRules = [content.MarketRules[0] with { ProductionReserveHours = 1d / 900 }] };
        // Four seconds at one work/second consume two batches, or four inputs per plan.
        AssertTargets(Calculate(State(content).World, content), new(Input, 4), new(OtherProduct, 4));
    }

    [TestMethod]
    public void Extra_material_fuel_does_not_supply_energy_to_the_next_plan()
    {
        var content = TwoPlans(capacity: 100, firstInputs: 2, secondInputs: 2, powerPerWork: 3);
        content = content with
        {
            Processes = content.Processes.Select(p => p.Details is ProcessDetails.Production details && details.Inputs.Any(g => g.Good == Input)
                ? p with { Details = details with { Inputs = [new(Fuel, 2)] } } : p).ToImmutableArray(),
            MarketRules = [content.MarketRules[0] with { ProductionReserveHours = 1d / 900 }]
        };
        // Minimum batches burn two fuel and leave eight energy; the first extra batch
        // consumes six energy and two material fuel. The next needs one more fuel.
        // Four material fuel plus three burned fuel; the second plan needs four inputs.
        AssertTargets(Calculate(State(content).World, content), new(Fuel, 7), new(OtherProduct, 4));
    }

    [TestMethod]
    public void Extra_reserves_carry_whole_fuel_surplus_to_the_next_plan()
    {
        var content = TwoPlans(capacity: 100, firstInputs: 0, secondInputs: 0, powerPerWork: 3);
        content = content with { MarketRules = [content.MarketRules[0] with { ProductionReserveHours = 1d / 600 }] };
        // Minimum batches consume twelve energy and leave eight from two fuel.
        // Each extra reserve needs twelve: the first buys one fuel and leaves six,
        // so the second also buys only one. Total thirty-six energy needs four fuel.
        AssertTargets(Calculate(State(content).World, content), new GoodsQuantity(Fuel, 4));
    }

    [TestMethod]
    public void Food_substitution_and_physical_remainders_leave_production_before_improvement()
    {
        var content = Market(Content(capacity: 5));
        content = content with
        {
            PopulationRules = [content.PopulationRules[0] with { Needs = content.PopulationRules[0].Needs.Select(n => n with
            { AmountPerPersonSecond = n.Need == PopulationNeed.Food ? 1 : n.Need == PopulationNeed.ImprovedDiet ? 2 : .125 }).ToImmutableArray() }],
            MarketRules = [content.MarketRules[0] with { BasicReserveHours = 1d / 3600, ImprovementReserveHours = 1d / 3600 }]
        };
        var world = State(content).World;
        var staple = new GoodId.Voxel(new("fixture:food"));
        var prepared = new GoodId.Voxel(new("fixture:prepared-food"));
        var settlement = world.Settlements.Single() with { Population = 2 };
        var otherNeeds = content.PopulationRules[0].ConsumptionGoods.Where(g => !g.Satisfies.Any(c =>
            c.Need is PopulationNeed.Food or PopulationNeed.ImprovedDiet)).Select(g =>
                new PopulationConsumptionRemainder(g.Good, 1)).ToImmutableArray();
        settlement = settlement with { PopulationState = settlement.PopulationState with { ConsumptionRemaining = otherNeeds } };
        world = Replace(world, settlement);
        AssertTargets(Calculate(world, content, preparedStock: 2), new(Input, 2), new(prepared, 3));
        settlement = settlement with { PopulationState = settlement.PopulationState with
        { ConsumptionRemaining = otherNeeds.Add(new(staple, 1)).Add(new(prepared, 1)) } };
        AssertTargets(Calculate(Replace(world, settlement), content), new(Input, 2), new(prepared, 3));
    }

    [TestMethod]
    public void Active_lease_reserves_full_quota_and_expired_lease_only_occupied_space()
    {
        var content = InventoryFixture.Content(duration: 2);
        content = content with { MarketRules = [content.MarketRules[0] with { BasicReserveHours = 1 }] };
        using var session = InventoryFixture.Session(content);
        var lease = InventoryFixture.Rent(session, 20, 2);
        Assert.AreEqual(80L, Calculate(session.State.World, content).Sum(g => g.Quantity));
        InventoryFixture.Service(session, dwell: 2);
        InventoryFixture.Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Input, 7));
        InventoryFixture.Step(session);
        Assert.AreEqual(93L, Calculate(session.State.World, content, step: session.State.Position.CompletedSteps).Sum(g => g.Quantity));
    }

    private static CoreContentProjection Market(CoreContentProjection content) => content with
    { MarketRules = [content.MarketRules[0] with { BasicReserveHours = 0, ProductionReserveHours = 0, ImprovementReserveHours = 0 }] };

    private static CoreContentProjection TwoPlans(long capacity, long firstInputs, long secondInputs, double powerPerWork = 0)
    {
        var content = Market(Content(capacity: capacity, rate: 1, powerPerWork: powerPerWork,
            inputs: firstInputs == 0 ? [] : [new(Input, firstInputs)]));
        var process = content.Processes[0] with
        { Details = new ProcessDetails.Production(new("fixture:second"), secondInputs == 0 ? [] : [new(OtherProduct, secondInputs)], [new(Product, 1)]) };
        return content with
        {
            Goods = content.Goods.Select(g => g.Good == Fuel ? g with { FuelEnergyPerUnit = 10 } : g).ToImmutableArray(),
            Processes = content.Processes.Add(process),
            SettlementTemplates = [content.SettlementTemplates[0] with
            {
                Plots = content.SettlementTemplates[0].Plots.Add(new(3, PlotSize.Small, WorldFixture.FactoryBuilding)),
                ProductionPlans = content.SettlementTemplates[0].ProductionPlans.Add(new(3, CapabilityKind.Production,
                    new ProcessRef.Production(new("fixture:second")), true, 1))
            }]
        };
    }

    private static WorldState Replace(WorldState world, SettlementState settlement) => world with { Settlements = [settlement] };

    private static ImmutableArray<GoodsQuantity> Calculate(WorldState world, CoreContentProjection content,
        long stapleStock = 0, long preparedStock = 0, ulong step = 0) =>
        new MarketStockTargets(world, content, world.Settlements.Single(), step).Calculate(stapleStock, preparedStock);

    private static void AssertTargets(ImmutableArray<GoodsQuantity> actual, params GoodsQuantity[] expected) =>
        CollectionAssert.AreEquivalent(expected, actual.ToArray());
}

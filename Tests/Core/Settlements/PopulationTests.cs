using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Settlements;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.PopulationFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class PopulationTests
{
    [TestMethod]
    public void Finite_demand_above_integer_inventory_range_produces_shortage_without_freezing()
    {
        var rule = Rule() with { Needs = Rule().Needs.Select(n => n with { AmountPerPersonSecond = 1e20 }).ToImmutableArray() };
        using var session = Start(Content(rule, []));
        var events = Advance(session, 2);
        Assert.AreEqual(6, events.OfType<CoreEvent.PopulationShortageChanged>().Count(e => e.Shortage));
        var report = Reports(events).Single();
        Assert.AreEqual(0, report.BasicSatisfaction);
        Assert.AreEqual(0, report.ImprovementSatisfaction);
        Assert.AreEqual(10L, report.After);
    }

    [TestMethod]
    public void Basic_satisfaction_uses_weights_and_improvement_cannot_cancel_shortage()
    {
        var rule = Rule() with { MaximumLossPerSecond = 3, ImprovementGrowthPerSecond = 100,
            Needs = Rule().Needs.Select(n => n with { Weight = n.Need == PopulationNeed.Food ? 7 : 3 }).ToImmutableArray() };
        using var session = Start(Content(rule, Stock(food: 100, clothing: 0, improvement: 100)));
        var report = Reports(Advance(session, 2)).Single();
        Assert.AreEqual(.7, report.BasicSatisfaction, 1e-12);
        Assert.AreEqual(1, report.ImprovementSatisfaction, 1e-12);
        Assert.AreEqual(PopulationSettlementReason.ShortageLoss, report.Reason);
        Assert.IsTrue(report.After <= report.Before);
    }

    [TestMethod]
    public void Full_partial_and_absent_basics_produce_distinct_growth_and_loss()
    {
        var rule = Rule() with { BaseGrowthPerSecond = 3, MaximumLossPerSecond = 3 };
        using var full = Start(Content(rule, Stock(100, 100)));
        using var partial = Start(Content(rule, Stock(100, 0)));
        using var absent = Start(Content(rule, []));
        var growing = Reports(Advance(full, 2)).Single();
        var declining = Reports(Advance(partial, 2)).Single();
        var starving = Reports(Advance(absent, 2)).Single();
        Assert.AreEqual(11L, growing.After);
        Assert.AreEqual(.5, declining.BasicSatisfaction, 1e-12);
        Assert.IsTrue(declining.After > starving.After);
        Assert.AreEqual(9L, starving.After);
    }

    [TestMethod]
    public void A_small_observed_shortage_cannot_round_into_the_full_supply_growth_branch()
    {
        var original = Rule();
        var rule = original with
        {
            BaseGrowthPerSecond = 3, MaximumLossPerSecond = 3,
            Needs = original.Needs.Select(n => n.Class == PopulationNeedClass.Basic
                ? n with { AmountPerPersonSecond = 3, Weight = n.Need == PopulationNeed.Food ? 100 : 1 } : n).ToImmutableArray(),
            ConsumptionGoods = original.ConsumptionGoods.Select(g => g.Good == Clothing
                ? g with { Satisfies = [new(PopulationNeed.Clothing, Math.BitDecrement(1))] } : g).ToImmutableArray()
        };
        using var session = Start(Content(rule, [new(Food, 1), new(Clothing, 1)]));
        var report = Reports(Advance(session, 2)).Single();
        Assert.IsTrue(report.BasicSatisfaction < 1);
        Assert.AreEqual(PopulationSettlementReason.ShortageLoss, report.Reason);
        Assert.AreEqual(10L, report.After);
    }

    [TestMethod]
    public void Fractional_growth_and_loss_accumulate_across_periods()
    {
        var rule = Rule() with { BaseGrowthPerSecond = 1, MaximumLossPerSecond = 1 };
        using var growing = Start(Content(rule, Stock(100, 100)));
        using var declining = Start(Content(rule, []));
        var gains = Reports(Advance(growing, 8));
        var losses = Reports(Advance(declining, 8));
        CollectionAssert.AreEqual(new long[] { 10, 10, 11, 11 }, gains.Select(r => r.After).ToArray());
        CollectionAssert.AreEqual(new long[] { 10, 10, 9, 9 }, losses.Select(r => r.After).ToArray());
        Assert.AreEqual(PopulationSettlementReason.Growth, gains[0].Reason);
        Assert.AreEqual(PopulationSettlementReason.ShortageLoss, losses[0].Reason);
    }

    [TestMethod]
    public void Minimum_discards_negative_debt_and_only_reports_arrival_once()
    {
        var starving = Content(Rule() with { MaximumLossPerSecond = 1 }, []);
        using var session = Start(starving);
        Assert.AreEqual(10L, Reports(Advance(session, 2)).Single().After);
        // 首期余量为 -1/3；下一期流失 53/6，只有计入旧余量才会触底。
        var rapidLoss = starving with { Identity = Identity('A'), PopulationRules = [Rule() with { MaximumLossPerSecond = 26.5 }] };
        Publish(session, rapidLoss);
        var events = Advance(session, 4);
        Assert.AreEqual(1, events.OfType<CoreEvent.PopulationMinimumReached>().Count());
        Assert.IsTrue(Reports(events).All(r => r.After == 1));
        Assert.IsTrue(Reports(events).All(r => r.Reason == PopulationSettlementReason.MinimumReached));
        var fed = starving with { Identity = Identity('B'), PopulationRules = [Rule()] };
        Publish(session, fed);
        // Trade is not implemented for NPC settlements yet; seed restored supply while retaining all population history.
        var state = session.State;
        using var replenished = new CoreSession(state with { World = state.World with
        {
            Inventories = state.World.Inventories.Select(i => i.Id == PopulationFixture.Inventory(session.Read()) ? i with
                { Goods = Stock(20, 20) } : i).ToImmutableArray()
        } });
        var stableEvents = Advance(replenished, 2);
        var stable = Reports(stableEvents).Single();
        Assert.AreEqual(PopulationSettlementReason.Stable, stable.Reason);
        Assert.AreEqual(1L, stable.After);
        Publish(replenished, fed with { Identity = Identity('C'), PopulationRules = [Rule() with { BaseGrowthPerSecond = 30 }] });
        var recovery = Advance(replenished, 2);
        Assert.AreEqual(2L, Reports(recovery).Single().After);
        Assert.AreEqual(PopulationSettlementReason.Growth, Reports(recovery).Single().Reason);
        CollectionAssert.AreEquivalent(new[] { PopulationNeed.Food, PopulationNeed.Clothing },
            stableEvents.OfType<CoreEvent.PopulationShortageChanged>().Where(e => !e.Shortage).Select(e => e.Need).ToArray());
    }

    [TestMethod]
    public void Prepared_food_is_taken_once_and_small_consumption_reuses_purchased_units()
    {
        var rule = Rule() with { Needs = Rule().Needs.Select(n => n with { AmountPerPersonSecond = 6 }).ToImmutableArray() };
        using var prepared = Start(Content(rule, [new(PreparedFood, 10), new(Clothing, 10)]));
        var retained = prepared.Read();
        var first = Advance(prepared, 1);
        Assert.AreEqual(9L, Quantity(prepared.Read(), PreparedFood));
        Assert.AreEqual(10L, Quantity(retained, PreparedFood));
        Assert.IsFalse(first.OfType<CoreEvent.PopulationShortageChanged>().Any(e => e.Need is PopulationNeed.Food or PopulationNeed.ImprovedDiet));
        var reports = Reports(Advance(prepared, 3));
        Assert.IsTrue(reports.All(report => report.BasicSatisfaction == 1));
        Assert.AreEqual(6L, Quantity(prepared.Read(), PreparedFood));
        Assert.IsTrue(reports.All(report => Math.Abs(report.ImprovementSatisfaction - .25) < 1e-12));

        using var small = Start(Content(Rule(), [new(PreparedFood, 10), new(Clothing, 10)]));
        Advance(small, 4);
        Assert.AreEqual(9L, Quantity(small.Read(), PreparedFood));
        Advance(small, 4);
        Assert.AreEqual(8L, Quantity(small.Read(), PreparedFood));
    }

    [TestMethod]
    public void Shortages_emit_only_transitions_and_zero_steps_do_not_consume_or_settle()
    {
        using var session = Start(Content(Rule(), []));
        var before = Capture(session);
        Assert.IsEmpty(Advance(session, 0));
        Assert.AreEqual(before.Encode(), Capture(session).Encode());
        var events = Advance(session, 6);
        Assert.AreEqual(6, events.OfType<CoreEvent.PopulationShortageChanged>().Count());
        Assert.IsTrue(events.OfType<CoreEvent.PopulationShortageChanged>().All(e => e.Shortage));
        Assert.AreEqual(3, Reports(events).Length);
    }

    [TestMethod]
    public void Nonconsumption_steps_preserve_started_population_and_inventory_arrays_and_emit_no_shortages()
    {
        using var session = Start(Content(Rule() with { ConsumptionIntervalTicks = 2, StatisticsPeriodTicks = 4 }, []));
        Assert.IsEmpty(Advance(session, 1).OfType<CoreEvent.PopulationShortageChanged>().ToArray());
        Assert.AreEqual(6, Advance(session, 1).OfType<CoreEvent.PopulationShortageChanged>().Count());
        var before = session.State.World;
        Assert.IsNotNull(before.Settlements.Single().PopulationState.Period);
        var skipped = Advance(session, 1);
        Assert.IsEmpty(skipped.OfType<CoreEvent.PopulationShortageChanged>().ToArray());
        Assert.IsEmpty(Reports(skipped));
        Assert.AreEqual(before.Settlements, session.State.World.Settlements);
        Assert.AreEqual(before.Inventories, session.State.World.Inventories);
        var settled = Advance(session, 1);
        Assert.AreEqual(1, Reports(settled).Length);
        Assert.IsEmpty(settled.OfType<CoreEvent.PopulationShortageChanged>().ToArray());
    }

    [TestMethod]
    public void Residents_cannot_take_food_from_trains_or_leased_inventory()
    {
        var content = Content(Rule(), []);
        content = content with { MarketRules = [content.MarketRules[0] with
            { LeaseDurationTicks = 100, RentableCapacityRatio = new(10000) }] };
        using var session = Start(content, [new(Food, 5), new(Clothing, 5)]);
        var rent = (SubmissionResult.Completed)session.Submit(new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(1), 20, 100),
            new(1000)), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(rent.Receipt.Decision);
        var lease = rent.Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        session.Submit(new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(new(1), 100, [])])), CommandSource.AutomationTest);
        session.Submit(new CoreCommand.StartTrain(new(1)), CommandSource.AutomationTest);
        for (var step = 0; step < 100 && session.State.World.Trains.Single().Activity != TrainActivity.Servicing; step++)
            Advance(session, 1);
        Assert.AreEqual(TrainActivity.Servicing, session.State.World.Trains.Single().Activity);
        var unloaded = (SubmissionResult.Completed)session.Submit(new CoreCommand.UnloadGoods(new(1), new(1), lease.Inventory, Food, 3), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(unloaded.Receipt.Decision);
        var report = Reports(Advance(session, 2)).Single();
        Assert.AreEqual(0, report.BasicSatisfaction);
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
        var cargo = ((Lookup<InventoryView>.Found)session.Read().FindInventory(train.Inventory)).Value;
        Assert.AreEqual(2L, cargo.Stacks.Single(g => g.Good == Food).Quantity);
        Assert.AreEqual(5L, cargo.Stacks.Single(g => g.Good == Clothing).Quantity);
        var stored = ((Lookup<InventoryView>.Found)session.Read().FindInventory(lease.Inventory)).Value;
        Assert.AreEqual(3L, stored.Stacks.Single(g => g.Good == Food).Quantity);
    }

    [TestMethod]
    public void Zero_progress_uses_new_rule_and_final_consumption_is_included_before_settlement()
    {
        var original = Content(Rule() with { StatisticsPeriodTicks = 8 }, Stock(100, 100, 100));
        using var session = Start(original);
        var nextRule = Rule() with { ConsumptionIntervalTicks = 2, StatisticsPeriodTicks = 4,
            Revision = new(new string('F', 64)), BaseGrowthPerSecond = 1.5 };
        Publish(session, original with { Identity = Identity('A'), PopulationRules = [nextRule] });
        Assert.IsEmpty(Reports(Advance(session, 3)));
        var report = Reports(Advance(session, 1)).Single();
        Assert.AreEqual(nextRule.Revision, report.ExecutionRevision);
        Assert.AreEqual(1, report.BasicSatisfaction);
        Assert.AreEqual(1, report.ImprovementSatisfaction);
        Assert.AreEqual(11L, report.After);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(8)]
    public void Current_labor_changes_immediately_but_started_period_keeps_its_duration(int length)
    {
        var original = Content(Rule() with { StatisticsPeriodTicks = 4, BaseGrowthPerSecond = 6 }, Stock(100, 100));
        using var session = Start(original);
        Advance(session, 1);
        var next = original with { Identity = Identity('A'), PopulationRules = [Rule() with
            { StatisticsPeriodTicks = length, LaborRatio = .8, Revision = new(new string('F', 64)) }] };
        Publish(session, next);
        Assert.AreEqual(8, PopulationOperations.Begin(session.State.World, next, new(1), session.Read().Position.CompletedSteps).AvailableLabor);
        Assert.IsEmpty(Reports(Advance(session, 2)));
        var frozen = Reports(Advance(session, 1)).Single();
        Assert.AreEqual(WorldFixture.Revision, frozen.ExecutionRevision);
        Assert.AreEqual(14L, frozen.After);
        Assert.AreEqual(4UL, frozen.Position.CompletedSteps);
        Assert.IsEmpty(Reports(Advance(session, (uint)length - 1)));
        var updated = Reports(Advance(session, 1)).Single();
        Assert.AreEqual(new SemanticRevision(new string('F', 64)), updated.ExecutionRevision);
        Assert.AreEqual((ulong)(4 + length), updated.Position.CompletedSteps);
        Assert.AreEqual(PopulationSettlementReason.Stable, updated.Reason);
    }

    [TestMethod]
    public void Basic_consumption_precedes_facilities_and_facilities_precede_improvement()
    {
        var content = Content(Rule(), [new(Food, 1), new(PreparedFood, 1), new(Clothing, 1)]);
        using var session = Start(content);
        var world = session.State.World;
        var population = PopulationOperations.Begin(world, content, new(1), session.Read().Position.CompletedSteps);
        FacilityRequest[] facilities = [new FacilityRequest.Batch(new(new(1), CapabilityKind.Production), 0,
            [new(Food, 1)]), new FacilityRequest.Batch(new(new(1), CapabilityKind.Extraction), 1, [new(PreparedFood, 1)]),
            new FacilityRequest.Work(new(new(1), CapabilityKind.Construction), 2, 20, 0, 2)];
        var consumption = population.Consumption!;
        var resolved = SettlementResources.Resolve(world, content, new(1), population.AvailableLabor,
            consumption.BasicRequests, facilities.ToImmutableArray(), consumption.PrepareImprovement,
            SettlementResources.Fuels(world, content).ToImmutableArray());
        Assert.AreEqual(CapabilityKind.Extraction, resolved.AcceptedBatches.Single().Capability);
        Assert.AreEqual(2.5, resolved.Work.Single().WorkPerSecond);
        var complete = PopulationOperations.Complete(resolved.World, new(1), population, resolved.ImprovementGrantedUnits, new(1, 0));
        Assert.IsTrue(complete.Events.OfType<CoreEvent.PopulationShortageChanged>().Any(e => e.Need == PopulationNeed.ImprovedDiet && e.Shortage));
    }
}

internal static class PopulationFixture
{
    internal static GoodId Food => Good("food");
    internal static GoodId Clothing => Good("clothing");
    internal static GoodId PreparedFood => Good("prepared-food");
    internal static GoodId Good(string name) => new GoodId.Voxel(new("fixture:" + name));
    internal static CoreInputId Identity(char value) => new(new string(value, 64));
    internal static PopulationRuleProjection Rule() => WorldFixture.PopulationRule() with { StatisticsPeriodTicks = 2 };
    internal static ImmutableArray<GoodsQuantity> Stock(long food = 100, long clothing = 100, long improvement = 0)
        => WorldFixture.PopulationRule().ConsumptionGoods.Select(g => new GoodsQuantity(g.Good,
            g.Good == Food ? food : g.Good == Clothing ? clothing : improvement)).Where(g => g.Quantity > 0).ToImmutableArray();
    internal static CoreContentProjection Content(PopulationRuleProjection rule, ImmutableArray<GoodsQuantity> stock)
    {
        var content = WorldFixture.Content;
        return content with { PopulationRules = [rule], SettlementTemplates = [content.SettlementTemplates[0] with { SpecialInitialInventory = stock }],
            Designs = content.Designs.Select(d => d.Design == WorldFixture.Station ? d with { Parts = d.Parts.Select(p => p with
                { Capabilities = p.Capabilities.Add(new PartCapability.Storage(10000)) }).ToImmutableArray() } : d).ToImmutableArray() };
    }
    internal static CoreSession Start(CoreContentProjection content, ImmutableArray<GoodsQuantity> cargo = default)
    {
        var input = cargo.IsDefault ? WorldFixture.Input : WorldFixture.Input with
            { Start = WorldFixture.Input.Start with { Trains = [new([WorldFixture.Vehicle], cargo)] } };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareNew(input, content, WorldFixture.Options));
        return (CoreSession)candidate.Activate();
    }
    internal static InventoryId Inventory(ICoreReadView view)
        => ((Lookup<InventoryView>.Found)view.FindInventory(new(1))).Value.Id;
    internal static long Quantity(ICoreReadView view, GoodId good)
        => ((Lookup<InventoryView>.Found)view.FindInventory(Inventory(view))).Value.Stacks.Where(s => s.Good == good).Sum(s => s.Quantity);
    internal static ICoreCapture Capture(ICoreSession session) => WorldFixture.Success(session.Capture(CapturePurpose.Save));
    internal static ImmutableArray<CoreEvent> Advance(ICoreSession session, uint steps)
    {
        var result = session.Advance(steps);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.AreEqual(steps, result.CompletedSteps);
        return result.Events.Select(e => e.Event).ToImmutableArray();
    }
    internal static PopulationSettlementReport[] Reports(IEnumerable<CoreEvent> events)
        => events.OfType<CoreEvent.PopulationSettled>().Select(e => e.Report).ToArray();
    internal static void Publish(ICoreSession session, CoreContentProjection content)
    {
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
    }
}

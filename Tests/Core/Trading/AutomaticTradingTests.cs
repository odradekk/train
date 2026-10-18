using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Inventory;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Trading;

[TestClass]
public sealed class AutomaticTradingTests
{
    private static readonly GoodId Clothing = InventoryTradeTests.Clothing;
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Completing_a_loop_reports_it_and_immediately_starts_the_next_run()
    {
        using var original = InventoryTradeTests.Session();
        var state = original.State;
        var node = state.World.Nodes.Single(n => n.Id == state.World.Settlements.Single().Node);
        using var session = new CoreSession(state with { World = state.World with
        {
            Nodes = [node, node with { Id = new(2), Position = new(1, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 }],
            Edges = [new(new(1), node.Id, new(2), 10, node.Terrain, 1, 2, [], [])],
            Next = state.World.Next with { Node = 3, Edge = 2 }
        } });
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(true,
            [new PlanStop.Station(node.Id, 0, []), new PlanStop.Waypoint(new(2))])));
        var started = Train(session).CurrentRun!.StartedAt;
        Accept(session, new CoreCommand.StartTrain(new(1)));
        RunReport? report = null;
        for (var i = 0; i < 5000 && report is null; i++)
            report = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.RunReported>().SingleOrDefault()?.Report;
        Assert.IsNotNull(report);
        Assert.AreEqual(RunEndReason.LoopCompleted, report.Reason);
        Assert.AreEqual(started, report.Totals.StartedAt);
        Assert.AreEqual(report.EndedAt, Train(session).CurrentRun!.StartedAt);
        Assert.AreEqual(0L, Train(session).CurrentRun!.TransportedUnits);
        Assert.AreEqual(0d, report.Totals.LoadedCapacityRatio);
        Assert.AreEqual(0d, Train(session).CurrentRun!.LoadedCapacityRatio);
        Assert.AreEqual(0, Train(session).NextStopIndex);
    }

    [TestMethod]
    [DataRow(55L, 2L, "insufficient_money")]
    [DataRow(1000L, 1L, "insufficient_goods")]
    public void Same_step_trains_trade_by_identity_and_reports_filter_by_train(long money, long stock, string blocker)
    {
        var content = InventoryTradeTests.Content();
        using var original = InventoryFixture.Session(content, trains: 2);
        var state = original.State;
        var settlement = state.World.Settlements.Single();
        using var session = new CoreSession(state with
        {
            Content = content with { MarketRules = [] },
            World = state.World with
            {
                Money = new(money),
                Nodes = state.World.Nodes.Select(n => n.Id == settlement.Node ? n with { Platforms = 2 } : n).ToImmutableArray(),
                Trains = state.World.Trains.Reverse().ToImmutableArray(),
                Inventories = state.World.Inventories.Select(i => i with
                    { Goods = i.Id == settlement.Inventory ? [new(Voxel, stock)] : [] }).ToImmutableArray()
            }
        });
        foreach (var id in new TrainId[] { new(2), new(1) })
        {
            Accept(session, new CoreCommand.ReplaceRunPlan(id, Plan(session,
                [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)])));
            Accept(session, new CoreCommand.StartTrain(id));
        }
        Accept(session, new CoreCommand.PublishContent(Changed(content, 'F')));
        var evaluations = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.AutomaticTradeEvaluated>().ToArray();
        CollectionAssert.AreEqual(new TrainId[] { new(1), new(2) }, evaluations.Select(e => e.Train).ToArray());
        Assert.AreEqual(1L, evaluations[0].Results.Single().CompletedQuantity);
        Assert.AreEqual(0L, evaluations[1].Results.Single().CompletedQuantity);
        Assert.AreEqual(blocker, evaluations[1].Results.Single().IncompleteReasons.Single().Code);
        foreach (var id in new TrainId[] { new(2), new(1) })
            Accept(session, new CoreCommand.ReplaceRunPlan(id, Plan(session, [])));
        var reports = session.Read().Economy.RecentRunReports;
        CollectionAssert.AreEqual(new TrainId[] { new(2), new(1) }, reports.Select(r => r.Train).ToArray());
        foreach (var id in new TrainId[] { new(1), new(2) })
        {
            var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;
            AssertReports(reports.Where(r => r.Train == id).ToImmutableArray(), train.RecentReports);
        }
    }

    [TestMethod]
    public void Expired_lease_allows_automatic_withdrawal_but_refuses_deposit()
    {
        using var session = InventoryTradeTests.Session(cargo: [new(Voxel, 2), new(Composite, 1)]);
        var lease = Rent(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Voxel, 2));
        Step(session);
        Step(session);
        Step(session);
        Assert.IsTrue(Lease(session, lease.Id).Expired);
        var wallet = session.Read().Economy.Wallet;
        var results = Evaluate(session,
            [new(new AutomaticGoodsAction.Load(), Voxel, lease.Inventory, 2),
             new(new AutomaticGoodsAction.Unload(), Composite, lease.Inventory, 0)]).Results;
        Assert.AreEqual(2L, results[0].CompletedQuantity);
        Assert.IsTrue(results[0].IncompleteReasons.IsEmpty);
        Assert.AreEqual(0L, results[1].CompletedQuantity);
        Assert.AreEqual("access_denied", results[1].IncompleteReasons.Single().Code);
        Assert.AreEqual("LocalInventory", results[1].IncompleteReasons.Single().Field);
        Assert.AreEqual(2L, Quantity(session, Voxel));
        Assert.AreEqual(1L, Quantity(session, Composite));
        Assert.IsTrue(ReadInventory(session, lease.Inventory).Stacks.IsEmpty);
        Assert.AreEqual(wallet, session.Read().Economy.Wallet);
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Compressed_food_stock_measures_one_real_18000_unit_automatic_evaluation()
    {
        var staple = new GoodId.Voxel(new("fixture:food"));
        var prepared = InventoryTradeTests.PreparedFood;
        var content = InventoryTradeTests.Content(capacity: 100);
        content = content with
        {
            MarketRules = [content.MarketRules[0] with { BasicReserveHours = 1 }],
            Designs = content.Designs.Select(d => d.Kind is DesignKind.Vehicle ? d with
            {
                Parts = d.Parts.Select(p => p with
                { Capabilities = p.Capabilities.Select<PartCapability, PartCapability>(c =>
                    c is PartCapability.Storage ? new PartCapability.Storage(200000) : c).ToImmutableArray() }).ToImmutableArray()
            } : d).ToImmutableArray()
        };
        using var session = InventoryTradeTests.Session(content,
            local: [new(staple, 27000), new(prepared, 27000)], money: 1_000_000_000);
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session,
            [Rule(session, new AutomaticGoodsAction.Buy(null), staple, 18000)])));
        var stopwatch = Stopwatch.StartNew();
        var start = Accept(session, new CoreCommand.StartTrain(new(1)));
        var evaluation = start.Events.Select(e => e.Event).OfType<CoreEvent.AutomaticTradeEvaluated>().SingleOrDefault()
            ?? NextEvaluation(session);
        stopwatch.Stop();
        TestContext.WriteLine($"single automatic evaluation: staple=prepared=27000, local capacity=100, buy=18000; elapsed={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
        var result = evaluation.Results.Single();
        Assert.AreEqual(18000L, result.RequestedQuantity);
        Assert.AreEqual(18000L, result.CompletedQuantity);
        Assert.IsTrue(result.IncompleteReasons.IsEmpty);
        Assert.AreEqual(18000L, Quantity(session, staple));
        Assert.AreEqual(9000L, ReadInventory(session, session.State.World.Settlements.Single().Inventory)
            .Stacks.Single(s => s.Good == staple).Quantity);
        // 全部中点库存都超过压缩目标，单价固定为 100 × 50% × 110% = 55，总额为 18000 × 55 = 990000。
        Assert.AreEqual(new Money(990000), result.ActualAmount);
        Assert.AreEqual(new Money(999010000), session.Read().Economy.Wallet);
    }

    [TestMethod]
    [DataRow(103L)]
    [DataRow(102L)]
    public void Buy_limit_checks_each_unit_and_keeps_the_affordable_prefix(long maximumUnitPrice)
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 13)]);
        var result = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(new(maximumUnitPrice)), Clothing, 3)]).Results.Single();
        Assert.AreEqual(3L, result.RequestedQuantity);
        Assert.AreEqual(2L, result.CompletedQuantity);
        Assert.AreEqual(new Money(199), result.ActualAmount);
        Assert.AreEqual("price_condition_failed", result.IncompleteReasons.Single().Code);
        Assert.AreEqual("MaximumUnitPrice", result.IncompleteReasons.Single().Field);
        Assert.AreEqual(new Money(9801), session.Read().Economy.Wallet);
        Assert.AreEqual(2L, Quantity(session, Clothing));
        Assert.AreEqual(new Money(199), Train(session).CurrentRun!.PurchasePayments);
    }

    [TestMethod]
    public void Sell_limit_keeps_only_units_meeting_the_minimum()
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 7)], cargo: [new(Clothing, 3)]);
        // Midpoints 7.5, 8.5, 9.5 produce receipts 101, 96, 92.
        var result = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Sell(new(96)), Clothing, 0)]).Results.Single();
        Assert.AreEqual(2L, result.CompletedQuantity);
        Assert.AreEqual(new Money(197), result.ActualAmount);
        Assert.AreEqual("MinimumUnitPrice", result.IncompleteReasons.Single().Field);
        Assert.AreEqual(1L, Quantity(session, Clothing));
        Assert.AreEqual(2L, Train(session).CurrentRun!.TransportedUnits);
    }

    [TestMethod]
    [DataRow(false, "insufficient_money")]
    [DataRow(true, "insufficient_capacity")]
    public void Same_price_buy_stops_inside_the_segment(bool capacityLimited, string blocker)
    {
        var content = InventoryTradeTests.Content();
        content = content with { Goods = content.Goods.Select(g => g with { MassTons = .001 }).ToImmutableArray() };
        using var session = InventoryTradeTests.Session(content, local: [new(Voxel, 10)],
            cargo: capacityLimited ? [new(Composite, 998)] : [], money: capacityLimited ? 10000 : 139);
        var result = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 5)]).Results.Single();
        Assert.AreEqual(2L, result.CompletedQuantity);
        Assert.AreEqual(new Money(110), result.ActualAmount);
        Assert.AreEqual(blocker, result.IncompleteReasons.Single().Code);
        Assert.AreEqual(2L, Quantity(session, Voxel));
        Assert.AreEqual(new Money(capacityLimited ? 9890 : 29), session.Read().Economy.Wallet);
    }

    [TestMethod]
    public void Buy_limit_does_not_price_an_overflowing_unconsumed_tail()
    {
        var content = InventoryTradeTests.Content(value: long.MaxValue,
            prices: new(new(10000), new(5000), new(20000), new(1000)));
        using var session = InventoryTradeTests.Session(content, local: [new(Clothing, 30)], money: long.MaxValue);
        var result = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(new(0)), Clothing, 20)]).Results.Single();
        Assert.AreEqual(0L, result.CompletedQuantity);
        Assert.AreEqual(new Money(0), result.ActualAmount);
        Assert.AreEqual("price_condition_failed", result.IncompleteReasons.Single().Code);
        Assert.IsNull(session.Read().Fault);
        Assert.AreEqual(new Money(long.MaxValue), session.Read().Economy.Wallet);
    }

    [TestMethod]
    public void Rules_share_wallet_and_execute_in_array_order()
    {
        using var session = InventoryTradeTests.Session(local: [new(Voxel, 10), new(Clothing, 13)], money: 150);
        var results = Evaluate(session,
            [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1),
             Rule(session, new AutomaticGoodsAction.Buy(null), Clothing, 1)]).Results;
        CollectionAssert.AreEqual(new long[] { 1, 0 }, results.Select(r => r.CompletedQuantity).ToArray());
        Assert.AreEqual("insufficient_money", results[1].IncompleteReasons.Single().Code);
        Assert.AreEqual(new Money(95), session.Read().Economy.Wallet);
    }

    [TestMethod]
    public void Rules_share_remaining_train_capacity()
    {
        var content = InventoryTradeTests.Content();
        content = content with { Goods = content.Goods.Select(g => g with { MassTons = .001 }).ToImmutableArray() };
        using var session = InventoryTradeTests.Session(content, local: [new(Voxel, 10), new(Clothing, 13)], cargo: [new(Composite, 999)]);
        var results = Evaluate(session,
            [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1),
             Rule(session, new AutomaticGoodsAction.Buy(null), Clothing, 1)]).Results;
        CollectionAssert.AreEqual(new long[] { 1, 0 }, results.Select(r => r.CompletedQuantity).ToArray());
        Assert.AreEqual("insufficient_capacity", results[1].IncompleteReasons.Single().Code);
    }

    [TestMethod]
    public void Targets_are_holdings_and_reevaluation_does_not_buy_again()
    {
        using var session = InventoryTradeTests.Session(local: [new(Voxel, 10)], cargo: [new(Voxel, 2)]);
        var first = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 3)]);
        Assert.AreEqual(1L, first.Results.Single().RequestedQuantity);
        Assert.AreEqual(3L, Quantity(session, Voxel));
        var next = NextEvaluation(session);
        Assert.AreEqual(0L, next.Results.Single().RequestedQuantity);
        Assert.AreEqual(0L, next.Results.Single().CompletedQuantity);
        Assert.IsTrue(Train(session).CurrentRun!.IncompleteRules.IsEmpty);
    }

    [TestMethod]
    public void Owned_load_preserves_restock_target_and_unload_is_free()
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 13)], cargo: [new(Voxel, 4), new(Clothing, 2)],
            relation: SettlementRelation.PlayerOwned);
        var results = Evaluate(session,
            [Rule(session, new AutomaticGoodsAction.Load(), Clothing, 10),
             Rule(session, new AutomaticGoodsAction.Unload(), Voxel, 2)]).Results;
        Assert.AreEqual(8L, results[0].RequestedQuantity);
        Assert.AreEqual(3L, results[0].CompletedQuantity);
        Assert.AreEqual(2L, results[1].RequestedQuantity);
        Assert.AreEqual(2L, results[1].CompletedQuantity);
        Assert.AreEqual(5L, Quantity(session, Clothing));
        Assert.AreEqual(2L, Quantity(session, Voxel));
        Assert.IsTrue(results.All(r => r.ActualAmount == new Money(0)));
        Assert.AreEqual(new Money(10000), session.Read().Economy.Wallet);
        var run = Train(session).CurrentRun!;
        Assert.AreEqual(new Money(0), run.PurchasePayments);
        Assert.AreEqual(new Money(0), run.SalesReceipts);
        Assert.AreEqual(2L, run.TransportedUnits);
    }

    [TestMethod]
    public void Changed_relation_does_not_turn_a_buy_into_free_loading()
    {
        using var session = InventoryTradeTests.Session(local: [new(Voxel, 10)], relation: SettlementRelation.PlayerOwned);
        var result = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)]).Results.Single();
        Assert.AreEqual(0L, result.CompletedQuantity);
        Assert.IsFalse(result.IncompleteReasons.IsEmpty);
        Assert.AreEqual(0L, Quantity(session, Voxel));
    }

    [TestMethod]
    public void Duplicate_goods_are_a_business_rejection_with_the_rule_field()
    {
        using var session = InventoryTradeTests.Session();
        var rule = Rule(session, new AutomaticGoodsAction.Load(), Voxel, 1);
        var result = Submit(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, [rule, rule])));
        var problem = ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
        Assert.AreEqual("plan_duplicate_good", problem.Code);
        Assert.AreEqual("Plan.Stops[0].GoodsRules[1].Good", problem.Field);
    }

    [TestMethod]
    public void Stop_and_start_keep_run_and_pause_automatic_evaluation()
    {
        using var session = InventoryTradeTests.Session(local: [new(Voxel, 10)]);
        Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)]);
        var run = Train(session).CurrentRun;
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Assert.IsFalse(session.Advance(10).Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated or CoreEvent.RunReported));
        AssertRun(run!, Train(session).CurrentRun!);
        var start = Accept(session, new CoreCommand.StartTrain(new(1)));
        Assert.IsFalse(start.Events.Any(e => e.Event is CoreEvent.RunReported));
        Assert.AreEqual(0L, NextEvaluation(session).Results.Single().CompletedQuantity);
        Assert.AreEqual(run!.StartedAt, Train(session).CurrentRun!.StartedAt);
    }

    [TestMethod]
    public void Zero_dwell_evaluates_once_before_completing_the_run()
    {
        using var session = InventoryTradeTests.Session(local: [new(Voxel, 10)]);
        Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)], 0);
        var events = session.Advance(5).Events.Select(e => e.Event).ToArray();
        Assert.IsFalse(events.OfType<CoreEvent.AutomaticTradeEvaluated>().Any());
        Assert.IsNull(Train(session).CurrentRun);
        Assert.AreEqual(RunEndReason.PlanCompleted, Train(session).RecentReports.Last().Reason);
    }

    [TestMethod]
    public void Manual_transfers_belong_to_the_current_run_and_replacement_and_disband_report_it()
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 13)]);
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, [])));
        var started = Train(session).CurrentRun!.StartedAt;
        var settlement = session.State.World.Settlements.Single();
        Accept(session, new CoreCommand.BuyGoods(new(1), settlement.Id, settlement.Inventory, Clothing, 2, new(199)));
        Assert.AreEqual(new Money(199), Train(session).CurrentRun!.PurchasePayments);
        Assert.AreEqual(-199L, Train(session).CurrentRun!.NetCashFlow);
        Assert.AreEqual(0L, Train(session).CurrentRun!.TransportedUnits);
        var replacement = Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, [])));
        var report = replacement.Events.Select(e => e.Event).OfType<CoreEvent.RunReported>().Single().Report;
        Assert.AreEqual(RunEndReason.PlanReplaced, report.Reason);
        Assert.AreEqual(started, report.Totals.StartedAt);
        Assert.AreEqual(new Money(199), report.Totals.PurchasePayments);
        Assert.IsNull(report.Totals.LoadedCapacityRatio);
        Assert.AreEqual(new Money(0), Train(session).CurrentRun!.PurchasePayments);
        var lease = Rent(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Clothing, 2));
        var disband = Accept(session, new CoreCommand.DisbandTrain(new(1)));
        var final = disband.Events.Select(e => e.Event).OfType<CoreEvent.RunReported>().Single().Report;
        Assert.AreEqual(RunEndReason.TrainDisbanded, final.Reason);
        AssertReports([final], [session.Read().Economy.RecentRunReports.Last()]);
    }

    [TestMethod]
    public void Missing_market_freezes_even_free_rules_and_resumes_the_same_stop()
    {
        var input = InventoryTradeTests.Content();
        input = input with { MarketRules = input.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 4 }).ToImmutableArray() };
        using var original = InventoryTradeTests.Session(input, local: [new(Voxel, 10)], relation: SettlementRelation.PlayerOwned);
        Accept(original, new CoreCommand.StopTrain(new(1)));
        Accept(original, new CoreCommand.ReplaceRunPlan(new(1), Plan(original,
            [Rule(original, new AutomaticGoodsAction.Load(), Voxel, 1)])));
        var content = original.State.Content;
        using var session = new CoreSession(original.State with { Content = content with { MarketRules = [] } });
        Accept(session, new CoreCommand.StartTrain(new(1)));
        Assert.IsFalse(session.Advance(3).Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        Assert.AreEqual(0L, Quantity(session, Voxel));
        Accept(session, new CoreCommand.PublishContent(Changed(content, 'F')));
        Assert.AreEqual(1L, Step(session).Events.Select(e => e.Event).OfType<CoreEvent.AutomaticTradeEvaluated>()
            .Single().Results.Single().CompletedQuantity);
    }

    [TestMethod]
    public void Sell_keeps_the_nonzero_target_holding()
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 7)], cargo: [new(Clothing, 3)]);
        var result = Evaluate(session, [Rule(session, new AutomaticGoodsAction.Sell(null), Clothing, 1)]).Results.Single();
        Assert.AreEqual(2L, result.RequestedQuantity);
        Assert.AreEqual(2L, result.CompletedQuantity);
        Assert.AreEqual(new Money(197), result.ActualAmount);
        Assert.IsTrue(result.IncompleteReasons.IsEmpty);
        Assert.AreEqual(1L, Quantity(session, Clothing));
    }

    [TestMethod]
    public void Moving_arrivals_evaluate_immediately_and_each_new_stop_resets_the_interval()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 10 }).ToImmutableArray() };
        using var original = InventoryTradeTests.Session(content, local: [new(Voxel, 10)]);
        using var session = WithRailway(original);
        var home = session.State.World.Settlements.Single().Node;
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Waypoint(new(2)), new PlanStop.Station(home, 1,
                [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)]),
             new PlanStop.Waypoint(new(2)), new PlanStop.Station(home, 100,
                [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 2)])])));
        var start = Accept(session, new CoreCommand.StartTrain(new(1)));
        Assert.IsFalse(start.Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        var first = AdvanceToService(session, 1).OfType<CoreEvent.AutomaticTradeEvaluated>().Single();
        Assert.AreEqual(1L, first.Results.Single().CompletedQuantity);
        var departure = Step(session);
        Assert.AreNotEqual(TrainActivity.Servicing, Train(session).Activity);
        Assert.IsFalse(departure.Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        var second = AdvanceToService(session, 3).OfType<CoreEvent.AutomaticTradeEvaluated>().Single();
        Assert.AreEqual(1L, second.Results.Single().RequestedQuantity);
        Assert.AreEqual(1L, second.Results.Single().CompletedQuantity);
        Assert.AreEqual(2L, Quantity(session, Voxel));
    }

    [TestMethod]
    [DataRow(0UL)]
    [DataRow(2UL)]
    public void Empty_station_rules_preserve_activity_events_and_never_evaluate(ulong dwell)
    {
        using var original = InventoryTradeTests.Session();
        using var session = WithRailway(original);
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(false,
            [new PlanStop.Station(session.State.World.Settlements.Single().Node, dwell, []), new PlanStop.Waypoint(new(2))])));
        var events = Accept(session, new CoreCommand.StartTrain(new(1))).Events.Select(e => e.Event).ToList();
        Assert.IsNull(session.State.World.Trains.Single().AutomaticEvaluationRemainingSteps);
        for (ulong i = 0; i < dwell; i++) events.AddRange(Step(session).Events.Select(e => e.Event));
        Assert.IsFalse(events.OfType<CoreEvent.AutomaticTradeEvaluated>().Any());
        var changes = events.OfType<CoreEvent.TrainActivityChanged>().Select(e => (e.Before, e.After)).ToArray();
        CollectionAssert.AreEqual(dwell == 0
            ? new[] { (TrainActivity.Stopped, TrainActivity.Running) }
            : new[] { (TrainActivity.Stopped, TrainActivity.Servicing), (TrainActivity.Servicing, TrainActivity.Running) }, changes);
        Assert.IsNull(session.State.World.Trains.Single().AutomaticEvaluationRemainingSteps);
    }

    [TestMethod]
    public void Platform_wait_entry_uses_the_completed_step_when_the_lease_expires()
    {
        using var original = InventoryFixture.Session(InventoryTradeTests.Content(), trains: 2);
        var state = original.State;
        var firstInventory = state.World.Trains.Single(t => t.Id.Value == 1).Inventory;
        using var session = new CoreSession(state with { World = state.World with
        { Inventories = state.World.Inventories.Select(i => i.Owner is InventoryOwner.Train
            ? i with { Goods = i.Id == firstInventory ? [] : [new(Voxel, 1)] } : i).ToImmutableArray() } });
        Service(session);
        var lease = Rent(session);
        var home = session.State.World.Settlements.Single().Node;
        Accept(session, new CoreCommand.ReplaceRunPlan(new(2), new(false,
            [new PlanStop.Station(home, 100, [new(new AutomaticGoodsAction.Unload(), Voxel, lease.Inventory, 0)])])));
        Accept(session, new CoreCommand.StartTrain(new(2)));
        var waiting = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(2))).Value;
        Assert.AreEqual(TrainActivity.Waiting, waiting.Activity);
        Assert.AreEqual("platform_wait", waiting.Blockers.Single().Code);
        while (session.Read().Position.CompletedSteps + 1 < lease.ExpiresAtStep) Step(session);
        Accept(session, new CoreCommand.DisbandTrain(new(1)));
        var events = Step(session).Events.Select(e => e.Event).ToArray();
        var result = events.OfType<CoreEvent.AutomaticTradeEvaluated>().Single().Results.Single();
        Assert.AreEqual(0L, result.CompletedQuantity);
        Assert.AreEqual("access_denied", result.IncompleteReasons.Single().Code);
        Assert.AreEqual("LocalInventory", result.IncompleteReasons.Single().Field);
        Assert.IsFalse(events.OfType<CoreEvent.GoodsTransferred>().Any());
        Assert.AreEqual(lease.Id, events.OfType<CoreEvent.LeaseExpired>().Single().Lease);
        Assert.IsTrue(Array.FindIndex(events, e => e is CoreEvent.LeaseExpired)
            < Array.FindIndex(events, e => e is CoreEvent.AutomaticTradeEvaluated));
        Assert.IsTrue(ReadInventory(session, lease.Inventory).Stacks.IsEmpty);
    }

    private static CoreSession WithRailway(CoreSession original)
    {
        var state = original.State;
        var node = state.World.Nodes.Single(n => n.Id == state.World.Settlements.Single().Node);
        return new(state with { World = state.World with
        {
            Nodes = [node with { Position = new(0, 0) },
                node with { Id = new(2), Position = new(1, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 }],
            Edges = [new(new(1), node.Id, new(2), 10, node.Terrain, 1, 2, [], [])],
            Next = state.World.Next with { Node = 3, Edge = 2 }
        } });
    }

    private static ImmutableArray<CoreEvent> AdvanceToService(CoreSession session, int stopIndex)
    {
        for (var i = 0; i < 5000; i++)
        {
            var events = Step(session).Events.Select(e => e.Event).ToImmutableArray();
            if (Train(session).Activity == TrainActivity.Servicing && Train(session).NextStopIndex == stopIndex)
                return events;
            Assert.IsFalse(events.OfType<CoreEvent.AutomaticTradeEvaluated>().Any());
        }
        Assert.Fail("5000 步内未到达指定停站。");
        return [];
    }

    [TestMethod]
    public void Missing_market_mid_countdown_freezes_the_remaining_interval()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 4 }).ToImmutableArray() };
        using var original = InventoryTradeTests.Session(content, local: [new(Voxel, 10)]);
        Evaluate(original, [Rule(original, new AutomaticGoodsAction.Buy(null), Voxel, 1)]);
        AssertNoEvaluation(original);
        using var session = new CoreSession(original.State with { Content = content with { MarketRules = [] } });
        Assert.IsFalse(session.Advance(5).Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        Accept(session, new CoreCommand.PublishContent(Changed(content, 'F')));
        AssertNoEvaluation(session);
        AssertNoEvaluation(session);
        Assert.AreEqual(1, Step(session).Events.Count(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
    }

    [TestMethod]
    public void Missing_market_appends_reports_without_trimming_until_the_next_report_with_rules()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { RecentReportLimit = 2 }).ToImmutableArray() };
        using var original = InventoryTradeTests.Session(content);
        using var session = new CoreSession(original.State with { Content = content with { MarketRules = [] } });
        var expected = session.Read().Economy.RecentRunReports.ToList();
        for (var i = 0; i < 4; i++)
            expected.Add(Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, [])))
                .Events.Select(e => e.Event).OfType<CoreEvent.RunReported>().Single().Report);
        AssertReports(expected.ToImmutableArray(), session.Read().Economy.RecentRunReports);
        Accept(session, new CoreCommand.PublishContent(Changed(content, 'F')));
        AssertReports(expected.ToImmutableArray(), session.Read().Economy.RecentRunReports);
        expected.Add(Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, [])))
            .Events.Select(e => e.Event).OfType<CoreEvent.RunReported>().Single().Report);
        AssertReports(expected.TakeLast(2).ToImmutableArray(), session.Read().Economy.RecentRunReports);
    }

    [TestMethod]
    public void A_stop_that_finishes_without_market_rules_is_not_evaluated_after_publication()
    {
        using var original = InventoryTradeTests.Session(local: [new(Voxel, 10)]);
        Accept(original, new CoreCommand.StopTrain(new(1)));
        Accept(original, new CoreCommand.ReplaceRunPlan(new(1), Plan(original,
            [Rule(original, new AutomaticGoodsAction.Buy(null), Voxel, 1)], 1)));
        var content = original.State.Content;
        using var session = new CoreSession(original.State with { Content = content with { MarketRules = [] } });
        var start = Accept(session, new CoreCommand.StartTrain(new(1)));
        Assert.IsFalse(start.Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        AssertNoEvaluation(session);
        Assert.IsNull(Train(session).CurrentRun);
        Accept(session, new CoreCommand.PublishContent(Changed(content, 'F')));
        Assert.IsFalse(session.Advance(4).Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        Assert.AreEqual(0L, Quantity(session, Voxel));
    }

    [TestMethod]
    public void Stop_and_start_preserve_the_remaining_evaluation_countdown()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 3 }).ToImmutableArray() };
        using var session = InventoryTradeTests.Session(content, local: [new(Voxel, 10)]);
        Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)]);
        AssertNoEvaluation(session);
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Assert.IsFalse(session.Advance(10).Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        var start = Accept(session, new CoreCommand.StartTrain(new(1)));
        Assert.IsFalse(start.Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        AssertNoEvaluation(session);
        Assert.AreEqual(1, Step(session).Events.Count(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
    }

    [TestMethod]
    public void Recent_report_limit_is_global_and_train_reports_are_its_filtered_view()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { RecentReportLimit = 2 }).ToImmutableArray() };
        using var session = InventoryTradeTests.Session(content);
        var generated = new List<RunReport>();
        for (var i = 0; i < 4; i++)
            generated.Add(Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, [])))
                .Events.Select(e => e.Event).OfType<CoreEvent.RunReported>().Single().Report);
        var reports = session.Read().Economy.RecentRunReports;
        Assert.AreEqual(2, reports.Length);
        Assert.IsTrue(reports.All(r => r.Reason == RunEndReason.PlanReplaced));
        AssertReports(generated.TakeLast(2).ToImmutableArray(), reports);
        AssertReports(reports, Train(session).RecentReports);
        Assert.IsTrue(reports[0].EndedAt.Sequence < reports[1].EndedAt.Sequence);
    }

    [TestMethod]
    public void Completing_a_previously_blocked_rule_removes_it_from_run_problems()
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 13)]);
        Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(new(55)), Clothing, 1)]);
        Assert.AreEqual(1, Train(session).CurrentRun!.IncompleteRules.Length);
        var content = session.State.Content;
        Accept(session, new CoreCommand.PublishContent(Changed(content with
        { Goods = content.Goods.Select(g => g with { BaseValue = new(0) }).ToImmutableArray() }, 'F')));
        Assert.AreEqual(1L, NextEvaluation(session).Results.Single().CompletedQuantity);
        Assert.IsTrue(Train(session).CurrentRun!.IncompleteRules.IsEmpty);
    }

    [TestMethod]
    public void Manual_sales_contribute_receipts_and_transported_units()
    {
        using var session = InventoryTradeTests.Session(local: [new(Clothing, 9)], cargo: [new(Clothing, 1)]);
        var settlement = session.State.World.Settlements.Single();
        Accept(session, new CoreCommand.SellGoods(new(1), settlement.Id, settlement.Inventory, Clothing, 1, new(92)));
        var run = Train(session).CurrentRun!;
        Assert.AreEqual(new Money(92), run.SalesReceipts);
        Assert.AreEqual(92L, run.NetCashFlow);
        Assert.AreEqual(1L, run.TransportedUnits);
        Assert.AreEqual(new Money(0), run.ServicePayments);
    }

    [TestMethod]
    [DataRow(2000L, 2d / 7)]
    [DataRow(250L, 8d / 7)]
    public void Loaded_ratio_weights_capacity_by_edge_length_and_entry_capacity(long capacity, double expected)
    {
        var input = InventoryTradeTests.Content();
        input = input with { Goods = input.Goods.Select(g => g with { MassTons = .001 }).ToImmutableArray() };
        using var original = InventoryTradeTests.Session(input, cargo: [new(Voxel, 500)]);
        var state = original.State;
        var node = state.World.Nodes.Single(n => n.Id == state.World.Settlements.Single().Node);
        using var session = new CoreSession(state with { World = state.World with
        {
            Nodes = [node with { Position = new(0, 0) },
                node with { Id = new(2), Position = new(1, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 },
                node with { Id = new(3), Position = new(2, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 }],
            Edges = [new(new(1), node.Id, new(2), 10, node.Terrain, 1, 2, [], []),
                new(new(2), new(2), new(3), 30, node.Terrain, 1, 2, [], [])],
            Next = state.World.Next with { Node = 4, Edge = 3 }
        } });
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Waypoint(new(3))])));
        Assert.IsNull(Train(session).CurrentRun!.LoadedCapacityRatio);
        Accept(session, new CoreCommand.StartTrain(new(1)));
        AdvanceUntil(session, t => t.Position is TrainPosition.Node { Id.Value: 2 });
        Assert.AreEqual(.5, Train(session).CurrentRun!.LoadedCapacityRatio!.Value, 1e-12);
        var content = session.State.Content;
        Accept(session, new CoreCommand.PublishContent(Changed(content with
        {
            Designs = content.Designs.Select(d => d.Kind is DesignKind.Vehicle ? d with
            {
                Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Select(c =>
                    c is PartCapability.Storage ? new PartCapability.Storage(capacity) : c).ToImmutableArray() }).ToImmutableArray()
            } : d).ToImmutableArray()
        }, 'F')));
        AdvanceUntil(session, t => t.Position is TrainPosition.Edge { Id.Value: 2 });
        Assert.AreEqual(expected, Train(session).CurrentRun!.LoadedCapacityRatio!.Value, 1e-12);
    }

    private static void AdvanceUntil(CoreSession session, Func<TrainView, bool> reached)
    {
        for (var i = 0; i < 5000 && !reached(Train(session)); i++) Step(session);
        Assert.IsTrue(reached(Train(session)), "5000 步内未到达目标位置。");
    }

    [TestMethod]
    public void Interval_update_takes_effect_only_after_the_current_countdown()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 3 }).ToImmutableArray() };
        using var session = InventoryTradeTests.Session(content, local: [new(Voxel, 10)]);
        Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)]);
        AssertNoEvaluation(session);
        Accept(session, new CoreCommand.PublishContent(Changed(content with
        { MarketRules = content.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 1 }).ToImmutableArray() }, 'F')));
        AssertNoEvaluation(session);
        Assert.AreEqual(1, Step(session).Events.Count(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
        Assert.AreEqual(1, Step(session).Events.Count(e => e.Event is CoreEvent.AutomaticTradeEvaluated));
    }

    [TestMethod]
    public void Departure_wins_when_the_evaluation_interval_and_dwell_end_together()
    {
        var content = InventoryTradeTests.Content();
        content = content with { MarketRules = content.MarketRules.Select(r => r with { AutomaticEvaluationIntervalTicks = 3 }).ToImmutableArray() };
        using var session = InventoryTradeTests.Session(content, local: [new(Voxel, 10)]);
        Evaluate(session, [Rule(session, new AutomaticGoodsAction.Buy(null), Voxel, 1)], 3);
        AssertNoEvaluation(session);
        AssertNoEvaluation(session);
        var events = Step(session).Events.Select(e => e.Event).ToArray();
        Assert.IsFalse(events.OfType<CoreEvent.AutomaticTradeEvaluated>().Any());
        Assert.AreEqual(RunEndReason.PlanCompleted, events.OfType<CoreEvent.RunReported>().Single().Report.Reason);
    }

    private static void AssertNoEvaluation(CoreSession session)
        => Assert.IsFalse(Step(session).Events.Any(e => e.Event is CoreEvent.AutomaticTradeEvaluated));

    private static void AssertReports(ImmutableArray<RunReport> expected, ImmutableArray<RunReport> actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].Train, actual[i].Train);
            Assert.AreEqual(expected[i].EndedAt, actual[i].EndedAt);
            Assert.AreEqual(expected[i].Reason, actual[i].Reason);
            AssertRun(expected[i].Totals, actual[i].Totals);
        }
    }

    private static void AssertRun(RunAccumulatorView expected, RunAccumulatorView actual)
    {
        Assert.AreEqual(expected.StartedAt, actual.StartedAt);
        Assert.AreEqual(expected.SalesReceipts, actual.SalesReceipts);
        Assert.AreEqual(expected.PurchasePayments, actual.PurchasePayments);
        Assert.AreEqual(expected.ServicePayments, actual.ServicePayments);
        Assert.AreEqual(expected.NetCashFlow, actual.NetCashFlow);
        Assert.AreEqual(expected.TransportedUnits, actual.TransportedUnits);
        Assert.AreEqual(expected.LoadedCapacityRatio, actual.LoadedCapacityRatio);
        Assert.AreEqual(expected.IncompleteRules.Length, actual.IncompleteRules.Length);
        for (var i = 0; i < expected.IncompleteRules.Length; i++)
        {
            var left = expected.IncompleteRules[i];
            var right = actual.IncompleteRules[i];
            Assert.AreEqual(left.StopIndex, right.StopIndex);
            Assert.AreEqual(left.RuleIndex, right.RuleIndex);
            Assert.AreEqual(left.Action, right.Action);
            Assert.AreEqual(left.Good, right.Good);
            Assert.AreEqual(left.RequestedQuantity, right.RequestedQuantity);
            Assert.AreEqual(left.CompletedQuantity, right.CompletedQuantity);
            Assert.AreEqual(left.ActualAmount, right.ActualAmount);
            Assert.AreEqual(left.IncompleteReasons.Length, right.IncompleteReasons.Length);
            for (var j = 0; j < left.IncompleteReasons.Length; j++)
            {
                var a = left.IncompleteReasons[j];
                var b = right.IncompleteReasons[j];
                Assert.AreEqual(a.Code, b.Code);
                Assert.AreEqual(a.Field, b.Field);
                Assert.AreEqual(a.Subject, b.Subject);
                CollectionAssert.AreEqual(a.RelatedIdentities.ToArray(), b.RelatedIdentities.ToArray());
            }
        }
    }

    private static AutomaticGoodsRule Rule(CoreSession session, AutomaticGoodsAction action, GoodId good, long target)
        => new(action, good, session.State.World.Settlements.Single().Inventory, target);
    private static RunPlan Plan(CoreSession session, ImmutableArray<AutomaticGoodsRule> rules, ulong dwell = 100)
        => new(false, [new PlanStop.Station(session.State.World.Settlements.Single().Node, dwell, rules)]);
    private static CoreEvent.AutomaticTradeEvaluated Evaluate(CoreSession session, ImmutableArray<AutomaticGoodsRule> rules, ulong dwell = 100)
    {
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.ReplaceRunPlan(new(1), Plan(session, rules, dwell)));
        var start = Accept(session, new CoreCommand.StartTrain(new(1)));
        return start.Events.Select(e => e.Event).OfType<CoreEvent.AutomaticTradeEvaluated>().SingleOrDefault() ?? NextEvaluation(session);
    }
    private static CoreEvent.AutomaticTradeEvaluated NextEvaluation(CoreSession session)
    {
        for (var i = 0; i < 100; i++)
        {
            var evaluation = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.AutomaticTradeEvaluated>().SingleOrDefault();
            if (evaluation is not null) return evaluation;
        }
        Assert.Fail("100 步内未发生自动交易评估。");
        return null!;
    }
    private static TrainView Train(CoreSession session) => ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
    private static long Quantity(CoreSession session, GoodId good)
        => ReadInventory(session, Train(session).Inventory).Stacks.Where(s => s.Good == good).Sum(s => s.Quantity);
}

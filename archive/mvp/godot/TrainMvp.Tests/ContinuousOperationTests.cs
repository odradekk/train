using TrainMvp.App;
using TrainMvp.Domain;
using Xunit;

namespace TrainMvp.Tests;

public sealed class ContinuousOperationTests
{
    private static GameSession CreateSession()
    {
        var scenario = ApprovedMvpCatalog.Create();
        return new GameSession(new SimulationEngine(scenario), scenario);
    }

    [Fact]
    public void State_flow_reaches_finished_observation_and_cannot_advance()
    {
        var session = CreateSession();
        Assert.Equal(AppPhase.Planning, session.Phase);
        var first = session.StartLoop();
        Assert.Equal(AppPhase.Running, session.Phase);
        Assert.Equal(0, first.Report.RepositionTicks);
        session.ShowReport();
        Assert.Equal(AppPhase.Report, session.Phase);
        session.CloseReport();
        Assert.Equal(AppPhase.FinishedObservation, session.Phase);
        Assert.False(session.CanStart);
        Assert.Throws<InvalidOperationException>(() => session.StartLoop());
        Assert.Throws<InvalidOperationException>(() => session.ShowReport());
        session.ReturnToPlanning();
        Assert.Equal(AppPhase.Planning, session.Phase);
        Assert.Equal(32, session.State.Cash);
        Assert.Equal(8, session.State.Holding(GoodIds.Grain));
        Assert.Equal(StationIds.WestBarn, session.State.LocationId);
    }

    [Fact]
    public void Start_change_enters_repositioning_then_running_without_changing_loop_ticks()
    {
        var session = CreateSession();
        session.ReplacePlan(new TradePlan(
            [StationIds.NorthMine, StationIds.WestBarn],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 1),
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1)
            ]));
        var hop = session.Engine.FindPath(StationIds.WestBarn, StationIds.NorthMine);
        var result = session.StartLoop();
        Assert.Equal(AppPhase.Repositioning, session.Phase);
        Assert.Equal(hop.Ticks, result.Report.RepositionTicks);
        Assert.Equal(session.Engine.DescribeLoop(session.Plan).TotalTicks, result.Report.LoopTicks);
        session.BeginRunning();
        Assert.Equal(AppPhase.Running, session.Phase);
        session.ShowReport();
        Assert.Equal(result.Report.CashAfter - result.Report.CashBefore, result.Report.Profit);
    }

    [Fact]
    public void Two_and_three_loops_keep_state_and_compare_only_adjacent_previous()
    {
        var session = CreateSession();
        var first = session.StartLoop();
        Assert.Null(session.PreviousReport);
        session.ShowReport();
        session.ReturnToPlanning();
        Assert.Equal(32, session.State.Cash);

        var second = session.StartLoop();
        Assert.Equal(first.Report.Profit, session.PreviousReport!.Profit);
        Assert.Equal(-48, session.PreviousReport.Profit);
        Assert.Equal(0, second.Report.Profit);
        session.ShowReport();
        session.ReturnToPlanning();

        var third = session.StartLoop();
        Assert.Equal(0, session.PreviousReport!.Profit);
        Assert.NotEqual(-48, session.PreviousReport.Profit);
        Assert.Equal(0, third.Report.Profit);
        Assert.Equal(second.Report.SoldQuantity, session.PreviousReport.SoldQuantity);
        Assert.Equal(second.Report.CargoUsedAfter, session.PreviousReport.CargoUsedAfter);
    }

    [Fact]
    public void Report_lists_complete_fields_and_only_executed_market_changes()
    {
        var session = CreateSession();
        var result = session.StartLoop();
        var report = result.Report;
        Assert.Equal(-48, report.Profit);
        Assert.Equal(80, report.CashBefore);
        Assert.Equal(32, report.CashAfter);
        Assert.Equal(6, report.LoopTicks);
        Assert.Equal(8, report.CargoAfter[GoodIds.Grain]);
        Assert.Equal(8, report.CargoUsedAfter);
        Assert.Equal(8, report.Capacity);
        Assert.Contains(report.IncompleteRules, rule => rule.StationId == StationIds.SouthMarket);
        Assert.Equal("南市卖粮未成交：现价 16 低于门槛 17。", report.NextHint);
        Assert.All(report.KeyMarketChanges, change =>
            Assert.Contains(report.Stops.Where(stop => stop.Traded).SelectMany(stop => stop.Rules),
                rule => rule.StationId == change.StationId && rule.GoodId == change.GoodId));
        Assert.DoesNotContain(report.KeyMarketChanges, change => change.StationId == StationIds.Hub);
        var west = report.KeyMarketChanges.Single(change =>
            change.StationId == StationIds.WestBarn && change.GoodId == GoodIds.Grain);
        Assert.Equal(8, west.Before.Supply);
        Assert.Equal(0, west.After.Supply);
        Assert.Equal(6, west.Before.Price);
    }

    [Fact]
    public void Hint_uses_first_incomplete_rule_in_stop_then_rule_order()
    {
        var session = CreateSession();
        session.ReplacePlan(new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 1, 8),
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 17, 0)
            ]));
        var result = session.StartLoop();
        var first = result.Report.IncompleteRules[0];
        Assert.Equal(StationIds.WestBarn, first.StationId);
        Assert.Contains("西仓", result.Report.NextHint);
        Assert.DoesNotContain("17", result.Report.NextHint);
    }

    [Fact]
    public void All_filled_rules_use_comparison_hint()
    {
        var session = CreateSession();
        session.SaveRule(new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0));
        var result = session.StartLoop();
        Assert.Equal(80, result.Report.Profit);
        Assert.Empty(result.Report.IncompleteRules);
        Assert.Equal("本圈无未完成交易；请自行比较利润、耗时和压舱货物。", result.Report.NextHint);
    }

    [Fact]
    public void Reset_restores_fixed_initial_scene_and_preset()
    {
        var session = CreateSession();
        session.StartLoop();
        session.ShowReport();
        session.ReturnToPlanning();
        session.SaveRule(new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0));
        session.ResetScenario();
        Assert.Equal(AppPhase.Planning, session.Phase);
        Assert.Equal(80, session.State.Cash);
        Assert.Equal(0, session.State.CargoUsed);
        Assert.Equal(StationIds.WestBarn, session.State.LocationId);
        Assert.Equal(new[] { StationIds.WestBarn, StationIds.SouthMarket }, session.Plan.Stops);
        Assert.Equal(17, session.Plan.RulesAt(StationIds.SouthMarket)[0].Threshold);
        Assert.Null(session.PreviousReport);
        Assert.Null(session.CurrentRun);
        Assert.Equal(6, session.Engine.QuotePrice(session.State, StationIds.WestBarn, GoodIds.Grain));
        Assert.Equal(16, session.Engine.QuotePrice(session.State, StationIds.SouthMarket, GoodIds.Grain));
    }
}

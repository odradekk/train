using TrainMvp.Domain;
using Xunit;

namespace TrainMvp.Tests;

public sealed class RouteAndRepositionTests
{
    private static SimulationEngine CreateEngine() => new(ApprovedMvpCatalog.Create());

    [Fact]
    public void Zero_or_one_stop_cannot_start()
    {
        var scenario = ApprovedMvpCatalog.Create();
        var empty = new TradePlan([], []);
        var one = new TradePlan([StationIds.WestBarn], []);
        Assert.Contains(TradePlan.Validate(empty, scenario), issue => issue.Code == "too_few_stops");
        Assert.Contains(TradePlan.Validate(one, scenario), issue => issue.Code == "too_few_stops");
        Assert.False(TradePlan.CanStart(empty, scenario));
        Assert.False(TradePlan.CanStart(one, scenario));
    }

    [Fact]
    public void Duplicate_stop_cannot_be_added()
    {
        var plan = ApprovedMvpCatalog.Create().PresetPlan;
        var after = plan.AddStop(StationIds.WestBarn);
        Assert.Equal(plan.Stops, after.Stops);
        var invalid = new TradePlan([StationIds.WestBarn, StationIds.WestBarn], plan.Rules);
        Assert.Contains(TradePlan.Validate(invalid, ApprovedMvpCatalog.Create()), issue => issue.Code == "duplicate_stop");
    }

    [Fact]
    public void Hub_to_capital_tie_keeps_ordinal_direct_path()
    {
        var engine = CreateEngine();
        var path = engine.FindPath(StationIds.Hub, StationIds.Capital);
        Assert.Equal(3, path.Ticks);
        Assert.Equal(new[] { StationIds.Hub, StationIds.Capital }, path.Path);
        Assert.Equal(path.Path, engine.FindPath(StationIds.Hub, StationIds.Capital).Path);
    }

    [Fact]
    public void North_mine_east_tea_north_post_early_pass_is_transit_only()
    {
        var engine = CreateEngine();
        var plan = new TradePlan(
            [StationIds.NorthMine, StationIds.EastTea, StationIds.NorthPost],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 6),
                new TradeRule(StationIds.EastTea, GoodIds.Tea, TradeAction.Buy, 40, 1),
                new TradeRule(StationIds.NorthPost, GoodIds.Coal, TradeAction.Sell, 12, 0)
            ]);
        var outbound = engine.FindPath(StationIds.NorthMine, StationIds.EastTea);
        Assert.Contains(StationIds.NorthPost, outbound.Path);
        Assert.Equal(new[] { StationIds.NorthPost }, outbound.Path.Skip(1).Take(outbound.Path.Count - 2));

        var result = engine.RunLoop(WithPlan(engine, plan));
        var firstTravel = result.Events.OfType<TravelEvent>().First();
        Assert.Contains(StationIds.NorthPost, firstTravel.Transits);

        var traded = result.Report.Stops.Where(stop => stop.Traded).ToArray();
        Assert.Equal(
            new[] { StationIds.NorthMine, StationIds.EastTea, StationIds.NorthPost },
            traded.Select(stop => stop.StationId));
        Assert.Equal(1, traded.Count(stop => stop.StationId == StationIds.NorthPost));
        Assert.False(result.Report.Stops[^1].Traded);
    }

    [Fact]
    public void Each_stop_trades_once_and_return_does_not()
    {
        var engine = CreateEngine();
        var result = engine.RunLoop(engine.CreateInitialState());
        var traded = result.Report.Stops.Where(stop => stop.Traded).Select(stop => stop.StationId).ToArray();
        Assert.Equal(traded, traded.Distinct(StringComparer.Ordinal));
        Assert.False(result.Report.Stops[^1].Traded);
    }

    [Fact]
    public void Start_change_repositions_without_trading_or_loop_profit()
    {
        var engine = CreateEngine();
        var state = engine.CreateInitialState();
        state.Plan = new TradePlan(
            [StationIds.NorthMine, StationIds.WestBarn],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 1),
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1)
            ]);

        var hop = engine.FindPath(StationIds.WestBarn, StationIds.NorthMine);
        var result = engine.RunLoop(state);
        Assert.Equal(hop.Ticks, result.Report.RepositionTicks);
        Assert.Equal(StationIds.WestBarn, result.Report.RepositionFromId);
        Assert.Equal(StationIds.NorthMine, result.Report.RepositionToId);
        Assert.Equal(hop.Path, result.Report.RepositionPath);
        Assert.Contains(result.Events, item => item is RepositionEvent);

        var geometry = engine.DescribeLoop(state.Plan);
        Assert.Equal(geometry.TotalTicks, result.Report.LoopTicks);
        Assert.NotEqual(result.Report.RepositionTicks, result.Report.LoopTicks);
        Assert.Equal(result.Report.CashAfter - 80, result.Report.Profit);
        Assert.DoesNotContain(result.Events.TakeWhile(item => item is not TradeEvent), item => item is TradeEvent);
    }

    [Fact]
    public void Reposition_restores_markets_by_reposition_ticks()
    {
        var engine = CreateEngine();
        var withHop = engine.CreateInitialState();
        withHop.Plan = new TradePlan(
            [StationIds.NorthMine, StationIds.WestBarn],
            [
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 1),
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1)
            ]);
        var alreadyThere = withHop.Clone();
        alreadyThere.LocationId = StationIds.NorthMine;
        withHop.GetMarket(StationIds.Frontier, GoodIds.Grain).Demand = 0;
        alreadyThere.GetMarket(StationIds.Frontier, GoodIds.Grain).Demand = 0;

        var hop = engine.FindPath(StationIds.WestBarn, StationIds.NorthMine);
        Assert.True(hop.Ticks > 0);
        var restored = engine.RunLoop(withHop);
        var baseline = engine.RunLoop(alreadyThere);
        Assert.Equal(hop.Ticks, restored.Report.RepositionTicks);
        Assert.Equal(0, baseline.Report.RepositionTicks);

        var restoredDemand = restored.StateAfter.GetMarket(StationIds.Frontier, GoodIds.Grain).Demand;
        var baselineDemand = baseline.StateAfter.GetMarket(StationIds.Frontier, GoodIds.Grain).Demand;
        Assert.Equal(baselineDemand + hop.Ticks, restoredDemand);
        Assert.Equal(hop.Ticks + restored.Report.LoopTicks, restoredDemand);
    }

    private static SimulationState WithPlan(SimulationEngine engine, TradePlan plan)
    {
        var state = engine.CreateInitialState();
        state.Plan = plan;
        return state;
    }
}

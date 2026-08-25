using TrainMvp.Domain;
using Xunit;

namespace TrainMvp.Tests;

public sealed class GrainCoalAcceptanceTests
{
    public static TradePlan CreateGrainCoalPlan(int grainTarget, int coalTarget)
    {
        return new TradePlan(
            [StationIds.WestBarn, StationIds.NorthMine, StationIds.SouthMarket, StationIds.SouthFactory],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, grainTarget),
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, coalTarget),
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0),
                new TradeRule(StationIds.SouthFactory, GoodIds.Coal, TradeAction.Sell, 20, 0)
            ]);
    }

    [Fact]
    public void Grain_coal_targets_eight_and_six_leave_coal_unfilled_for_cargo()
    {
        var engine = new SimulationEngine(ApprovedMvpCatalog.Create());
        var state = engine.CreateInitialState();
        state.Plan = CreateGrainCoalPlan(8, 6);
        var result = engine.RunLoop(state);
        var coal = result.Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.StationId == StationIds.NorthMine && rule.GoodId == GoodIds.Coal);
        Assert.Equal(RuleOutcomeKind.None, coal.Outcome);
        Assert.Equal(RuleFailureCause.InsufficientCargoSpace, coal.Cause);
        Assert.Equal(6, coal.ExpectedQuantity);
        Assert.Equal(0, coal.ActualQuantity);
        var grainBuy = result.Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.StationId == StationIds.WestBarn && rule.GoodId == GoodIds.Grain);
        Assert.Equal(8, grainBuy.ActualQuantity);
        Assert.Equal(0, result.StateAfter.Holding(GoodIds.Coal));
    }

    [Fact]
    public void Grain_coal_targets_four_and_four_yield_one_loop_profit_112()
    {
        var engine = new SimulationEngine(ApprovedMvpCatalog.Create());
        var state = engine.CreateInitialState();
        state.Plan = CreateGrainCoalPlan(4, 4);
        var result = engine.RunLoop(state);
        Assert.Equal(112, result.Report.Profit);
        Assert.Equal(0, result.StateAfter.Holding(GoodIds.Grain));
        Assert.Equal(0, result.StateAfter.Holding(GoodIds.Coal));
        Assert.All(
            result.Report.Stops.Where(stop => stop.Traded).SelectMany(stop => stop.Rules),
            rule => Assert.Equal(RuleOutcomeKind.Filled, rule.Outcome));
    }
}

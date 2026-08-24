using TrainMvp.Domain;
using Xunit;

namespace TrainMvp.Tests;

public sealed class RuleContractTests
{
    private static SimulationEngine CreateEngine() => new(ApprovedMvpCatalog.Create());

    [Fact]
    public void Buy_and_sell_thresholds_include_equality()
    {
        var engine = CreateEngine();
        var state = engine.CreateInitialState();
        Assert.Equal(6, engine.QuotePrice(state, StationIds.WestBarn, GoodIds.Grain));
        Assert.Equal(16, engine.QuotePrice(state, StationIds.SouthMarket, GoodIds.Grain));
        state.Plan = new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 6, 1),
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 16, 0)
            ]);
        var result = engine.RunLoop(state);
        var buy = result.Report.Stops.SelectMany(stop => stop.Rules).Single(rule => rule.Action == TradeAction.Buy);
        var sell = result.Report.Stops.SelectMany(stop => stop.Rules).Single(rule => rule.Action == TradeAction.Sell);
        Assert.Equal(RuleOutcomeKind.Filled, buy.Outcome);
        Assert.Equal(RuleOutcomeKind.Filled, sell.Outcome);
    }

    [Fact]
    public void Price_miss_and_target_already_met_keep_cause_order()
    {
        var engine = CreateEngine();
        var failed = engine.RunLoop(engine.CreateInitialState());
        var sell = failed.Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.StationId == StationIds.SouthMarket);
        Assert.Equal(RuleFailureCause.PriceConditionNotMet, sell.Cause);
        Assert.Equal(8, sell.ExpectedQuantity);

        var met = engine.CreateInitialState();
        met.Plan = met.Plan.WithThreshold(StationIds.SouthMarket, GoodIds.Grain, 12);
        met.Cargo[GoodIds.Grain] = 0;
        var buyAlready = new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1),
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0)
            ]);
        met.Plan = buyAlready;
        met.Cargo[GoodIds.Grain] = 1;
        var after = engine.RunLoop(met);
        var buy = after.Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.StationId == StationIds.WestBarn);
        Assert.Equal(RuleOutcomeKind.None, buy.Outcome);
        Assert.Equal(RuleFailureCause.TargetAlreadyMet, buy.Cause);
        Assert.Equal(0, buy.ExpectedQuantity);
    }

    [Fact]
    public void Quantity_limits_and_buy_cause_priority()
    {
        var engine = CreateEngine();
        var supply = engine.CreateInitialState();
        supply.GetMarket(StationIds.WestBarn, GoodIds.Grain).Supply = 0;
        supply.Plan = new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8)]);
        var noSupply = engine.RunLoop(supply).Report.Stops.SelectMany(stop => stop.Rules).First();
        Assert.Equal(RuleFailureCause.InsufficientSupply, noSupply.Cause);

        var space = engine.CreateInitialState();
        space.Cargo[GoodIds.Tea] = 8;
        space.Plan = new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8)]);
        var noSpace = engine.RunLoop(space).Report.Stops.SelectMany(stop => stop.Rules).First();
        Assert.Equal(RuleFailureCause.InsufficientCargoSpace, noSpace.Cause);

        var funds = engine.CreateInitialState();
        funds.Cash = 5;
        funds.Plan = new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8)]);
        var noFunds = engine.RunLoop(funds).Report.Stops.SelectMany(stop => stop.Rules).First();
        Assert.Equal(RuleFailureCause.InsufficientFunds, noFunds.Cause);
        Assert.Equal(8, noFunds.ExpectedQuantity);
        Assert.Equal(0, noFunds.ActualQuantity);
    }

    [Fact]
    public void Partial_fill_uses_the_tightest_constraint()
    {
        var engine = CreateEngine();
        var state = engine.CreateInitialState();
        state.GetMarket(StationIds.WestBarn, GoodIds.Grain).Supply = 3;
        state.Plan = new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8),
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0)
            ]);
        var buy = engine.RunLoop(state).Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.Action == TradeAction.Buy);
        Assert.Equal(RuleOutcomeKind.Partial, buy.Outcome);
        Assert.Equal(8, buy.ExpectedQuantity);
        Assert.Equal(3, buy.ActualQuantity);
        Assert.Equal(RuleFailureCause.InsufficientSupply, buy.Cause);
    }

    [Fact]
    public void Same_station_sell_frees_cash_and_space_for_later_buy()
    {
        var engine = CreateEngine();
        var sellFirst = SetupMixedNorthMine(engine, sellGrainFirst: true);
        var buyFirst = SetupMixedNorthMine(engine, sellGrainFirst: false);
        var soldThenBought = engine.RunLoop(sellFirst);
        var boughtFirst = engine.RunLoop(buyFirst);

        var coalAfterSellFirst = soldThenBought.Report.Stops
            .SelectMany(stop => stop.Rules)
            .Single(rule => rule.GoodId == GoodIds.Coal);
        Assert.Equal(RuleOutcomeKind.Filled, coalAfterSellFirst.Outcome);
        Assert.Equal(2, coalAfterSellFirst.ActualQuantity);

        var coalBuyFirst = boughtFirst.Report.Stops
            .SelectMany(stop => stop.Rules)
            .Single(rule => rule.GoodId == GoodIds.Coal);
        Assert.Equal(RuleOutcomeKind.None, coalBuyFirst.Outcome);
        Assert.Equal(RuleFailureCause.InsufficientCargoSpace, coalBuyFirst.Cause);
        Assert.NotEqual(soldThenBought.StateAfter.Cash, boughtFirst.StateAfter.Cash);
    }

    [Fact]
    public void One_rule_per_stop_good_and_numeric_bounds()
    {
        var scenario = ApprovedMvpCatalog.Create();
        var first = new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8);
        var replaced = first with { Threshold = 12, TargetHolding = 4 };
        var plan = new TradePlan([StationIds.WestBarn, StationIds.SouthMarket], [first]).UpsertRule(replaced);
        Assert.Single(plan.RulesAt(StationIds.WestBarn));
        Assert.Equal(12, plan.RulesAt(StationIds.WestBarn)[0].Threshold);
        Assert.Equal(4, plan.RulesAt(StationIds.WestBarn)[0].TargetHolding);

        Assert.Contains(
            TradePlan.ValidateRule(plan, scenario, first with { Threshold = 0 }),
            issue => issue.Code == "threshold");
        Assert.Contains(
            TradePlan.ValidateRule(plan, scenario, first with { TargetHolding = 0 }),
            issue => issue.Code == "buy_target");
        Assert.Contains(
            TradePlan.ValidateRule(plan, scenario, first with { TargetHolding = 9 }),
            issue => issue.Code == "buy_target");
        Assert.Contains(
            TradePlan.ValidateRule(
                plan,
                scenario,
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 9)),
            issue => issue.Code == "sell_target");
    }

    [Fact]
    public void Invalid_buy_or_sell_side_and_capacity_competition_are_warnings_or_errors()
    {
        var scenario = ApprovedMvpCatalog.Create();
        var buyAtDemand = new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Buy, 18, 8);
        var sellAtSupply = new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Sell, 12, 0);
        var plan = new TradePlan([StationIds.WestBarn, StationIds.SouthMarket], [buyAtDemand]);
        Assert.Contains(TradePlan.ValidateRule(plan, scenario, buyAtDemand), issue => issue.Code == "no_supply");
        Assert.Contains(TradePlan.ValidateRule(plan, scenario, sellAtSupply), issue => issue.Code == "no_demand");

        var competing = scenario.PresetPlan.UpsertRule(
            new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8));
        Assert.False(competing.HasCapacityCompetition(scenario.CargoCapacity));
        var over = competing.UpsertRule(new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 6));
        over = over.AddStop(StationIds.NorthMine);
        Assert.True(over.HasCapacityCompetition(scenario.CargoCapacity));
        Assert.Equal(14, over.BuyTargetOccupancy);
        Assert.DoesNotContain(TradePlan.Validate(over, scenario), issue => issue.Code.Contains("capacity", StringComparison.Ordinal));
    }

    [Fact]
    public void Buy_occupancy_uses_max_per_good_and_capacity_from_state()
    {
        var scenario = ApprovedMvpCatalog.Create();
        var sameGood = new TradePlan(
            [StationIds.WestBarn, StationIds.NorthMine],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 4),
                new TradeRule(StationIds.NorthMine, GoodIds.Grain, TradeAction.Buy, 18, 4)
            ]);
        Assert.Equal(4, sameGood.BuyTargetOccupancy);
        Assert.False(sameGood.HasCapacityCompetition(scenario.CargoCapacity));

        var grainAndCoal = new TradePlan(
            [StationIds.WestBarn, StationIds.NorthMine],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8),
                new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 6)
            ]);
        Assert.Equal(14, grainAndCoal.BuyTargetOccupancy);
        Assert.True(grainAndCoal.HasCapacityCompetition(scenario.CargoCapacity));
        Assert.Empty(TradePlan.Validate(grainAndCoal, scenario));
        Assert.True(TradePlan.CanStart(grainAndCoal, scenario));
    }

    private static SimulationState SetupMixedNorthMine(SimulationEngine engine, bool sellGrainFirst)
    {
        var state = engine.CreateInitialState();
        state.LocationId = StationIds.NorthMine;
        state.Cash = 0;
        state.Cargo[GoodIds.Grain] = 8;
        state.GetMarket(StationIds.NorthMine, GoodIds.Grain).Demand = 8;
        var sell = new TradeRule(StationIds.NorthMine, GoodIds.Grain, TradeAction.Sell, 1, 0);
        var buy = new TradeRule(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 2);
        var west = new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1);
        var rules = sellGrainFirst ? new[] { sell, buy, west } : new[] { buy, sell, west };
        state.Plan = new TradePlan([StationIds.NorthMine, StationIds.WestBarn], rules);
        return state;
    }
}

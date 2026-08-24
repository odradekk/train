using TrainMvp.Domain;
using Xunit;

namespace TrainMvp.Tests;

public sealed class GrainLoopAcceptanceTests
{
    private static SimulationEngine CreateEngine()
    {
        return new SimulationEngine(ApprovedMvpCatalog.Create());
    }

    [Fact]
    public void Initial_grain_prices_match_approved_matrix()
    {
        var engine = CreateEngine();
        var state = engine.CreateInitialState();

        Assert.Equal(6, engine.QuotePrice(state, StationIds.WestBarn, GoodIds.Grain));
        Assert.Equal(16, engine.QuotePrice(state, StationIds.SouthMarket, GoodIds.Grain));
    }

    [Fact]
    public void West_barn_to_south_market_uses_hub_and_six_tick_loop()
    {
        var engine = CreateEngine();
        var path = engine.FindPath(StationIds.WestBarn, StationIds.SouthMarket);
        Assert.Equal(new[] { StationIds.WestBarn, StationIds.Hub, StationIds.SouthMarket }, path.Path);
        Assert.Equal(3, path.Ticks);

        var geometry = engine.DescribeLoop(engine.CreateInitialState().Plan);
        Assert.Equal(6, geometry.TotalTicks);
        Assert.Equal(new[] { StationIds.Hub }, geometry.Segments[0].Transits);
        Assert.True(geometry.Segments[1].IsReturn);
    }

    [Fact]
    public void Hub_is_transit_only_and_return_to_start_does_not_trade()
    {
        var engine = CreateEngine();
        var result = engine.RunLoop(engine.CreateInitialState());

        Assert.Contains(result.Events, item => item is TransitEvent transit && transit.StationId == StationIds.Hub);
        Assert.DoesNotContain(result.Report.Stops, stop => stop.StationId == StationIds.Hub && stop.Traded);

        var returnStop = result.Report.Stops[^1];
        Assert.Equal(StationIds.WestBarn, returnStop.StationId);
        Assert.False(returnStop.Traded);
        Assert.Empty(returnStop.Rules);

        var tradedStops = result.Report.Stops.Where(stop => stop.Traded).Select(stop => stop.StationId).ToArray();
        Assert.Equal(new[] { StationIds.WestBarn, StationIds.SouthMarket }, tradedStops);
    }

    [Fact]
    public void Preset_first_loop_fails_at_south_market_threshold()
    {
        var engine = CreateEngine();
        var result = engine.RunLoop(engine.CreateInitialState());

        Assert.Equal(-48, result.Report.Profit);
        Assert.Equal(32, result.StateAfter.Cash);
        Assert.Equal(8, result.StateAfter.Holding(GoodIds.Grain));
        Assert.Equal(8, result.StateAfter.CargoUsed);
        Assert.Equal("南市卖粮未成交：现价 16 低于门槛 17。", result.Report.NextHint);

        var sell = result.Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.StationId == StationIds.SouthMarket && rule.GoodId == GoodIds.Grain);
        Assert.Equal(RuleOutcomeKind.None, sell.Outcome);
        Assert.Equal(RuleFailureCause.PriceConditionNotMet, sell.Cause);
        Assert.Equal(16, sell.QuotedPrice);
        Assert.Equal(17, sell.Threshold);
        Assert.Equal(8, sell.ExpectedQuantity);
        Assert.Equal(0, sell.ActualQuantity);
    }

    [Fact]
    public void Unmodified_five_loops_are_minus_forty_eight_then_zeros()
    {
        var engine = CreateEngine();
        var state = engine.CreateInitialState();
        var profits = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var result = engine.RunLoop(state);
            profits.Add(result.Report.Profit);
            state = result.StateAfter;
        }

        Assert.Equal(new[] { -48, 0, 0, 0, 0 }, profits);
    }

    [Fact]
    public void Lowering_south_market_threshold_to_twelve_yields_eighty_then_fifty_four()
    {
        var engine = CreateEngine();
        var state = engine.CreateInitialState();
        state.Plan = state.Plan.WithThreshold(StationIds.SouthMarket, GoodIds.Grain, 12);

        var profits = new List<int>();
        int? secondLoopGrainPrice = null;
        for (var i = 0; i < 3; i++)
        {
            var result = engine.RunLoop(state);
            profits.Add(result.Report.Profit);
            if (i == 1)
            {
                secondLoopGrainPrice = result.Report.Stops
                    .SelectMany(stop => stop.Rules)
                    .Single(rule => rule.StationId == StationIds.SouthMarket && rule.GoodId == GoodIds.Grain)
                    .QuotedPrice;
            }

            state = result.StateAfter;
        }

        Assert.Equal(new[] { 80, 54, 54 }, profits);
        Assert.Equal(15, secondLoopGrainPrice);
    }

    [Fact]
    public void Price_uses_away_from_zero_and_identical_inputs_repeat()
    {
        var fourteenPointFive = 10.0 * (1.0 + 0.6 * 0.75);
        Assert.Equal(14.5, fourteenPointFive);
        Assert.Equal(15, (int)Math.Round(fourteenPointFive, MidpointRounding.AwayFromZero));
        Assert.Equal(14, (int)Math.Round(fourteenPointFive, MidpointRounding.ToEven));

        var engine = CreateEngine();
        var first = engine.RunLoop(engine.CreateInitialState());
        var second = engine.RunLoop(engine.CreateInitialState());
        Assert.Equal(first.Report.Profit, second.Report.Profit);
        Assert.Equal(first.Report.NextHint, second.Report.NextHint);
        Assert.Equal(first.Events.Count, second.Events.Count);
        Assert.Equal(
            first.Events.Select(item => item.ToString()),
            second.Events.Select(item => item.ToString()));
    }
}

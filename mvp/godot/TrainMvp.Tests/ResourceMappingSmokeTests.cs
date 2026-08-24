using TrainMvp.Domain;
using TrainMvp.Domain.Definitions;
using Xunit;

namespace TrainMvp.Tests;

public sealed class ResourceMappingSmokeTests
{
    [Fact]
    public void Production_resource_mapping_matches_catalog_and_preset_failure()
    {
        var catalog = ApprovedMvpCatalog.Create();
        var mapped = ScenarioMapper.ToDomain(ScenarioResourceFactory.CreateApproved());

        Assert.Equal(catalog.StartingCash, mapped.StartingCash);
        Assert.Equal(catalog.CargoCapacity, mapped.CargoCapacity);
        Assert.Equal(catalog.StartingStationId, mapped.StartingStationId);
        Assert.Equal(catalog.Goods.Keys.OrderBy(id => id, StringComparer.Ordinal), mapped.Goods.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(catalog.StationOrder, mapped.StationOrder);
        Assert.Equal(catalog.Edges.Count, mapped.Edges.Count);
        Assert.Equal(catalog.PresetPlan.Stops, mapped.PresetPlan.Stops);
        Assert.Equal(catalog.PresetPlan.Rules, mapped.PresetPlan.Rules);

        var grain = mapped.Goods[GoodIds.Grain];
        Assert.Equal(catalog.Goods[GoodIds.Grain], grain);

        var westBarnGrain = mapped.InitialMarkets[new MarketKey(StationIds.WestBarn, GoodIds.Grain)];
        var catalogWestBarn = catalog.InitialMarkets[new MarketKey(StationIds.WestBarn, GoodIds.Grain)];
        Assert.Equal(catalogWestBarn.Supply, westBarnGrain.Supply);
        Assert.Equal(catalogWestBarn.SupplyCap, westBarnGrain.SupplyCap);
        Assert.Equal(catalogWestBarn.DemandCap, westBarnGrain.DemandCap);

        var catalogEngine = new SimulationEngine(catalog);
        var mappedEngine = new SimulationEngine(mapped);
        Assert.Equal(6, mappedEngine.QuotePrice(mappedEngine.CreateInitialState(), StationIds.WestBarn, GoodIds.Grain));
        Assert.Equal(16, mappedEngine.QuotePrice(mappedEngine.CreateInitialState(), StationIds.SouthMarket, GoodIds.Grain));

        var catalogRun = catalogEngine.RunLoop(catalogEngine.CreateInitialState());
        var mappedRun = mappedEngine.RunLoop(mappedEngine.CreateInitialState());
        Assert.Equal(catalogRun.Report.Profit, mappedRun.Report.Profit);
        Assert.Equal(catalogRun.Report.NextHint, mappedRun.Report.NextHint);

        var sell = mappedRun.Report.Stops.SelectMany(stop => stop.Rules)
            .Single(rule => rule.StationId == StationIds.SouthMarket && rule.GoodId == GoodIds.Grain);
        Assert.Equal(8, sell.ExpectedQuantity);
        Assert.Equal(0, sell.ActualQuantity);
        Assert.Equal(RuleOutcomeKind.None, sell.Outcome);
        Assert.Equal(RuleFailureCause.PriceConditionNotMet, sell.Cause);
    }
}

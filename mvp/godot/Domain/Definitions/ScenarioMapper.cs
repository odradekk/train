namespace TrainMvp.Domain.Definitions;

public static class ScenarioMapper
{
    public static ScenarioData ToDomain(ScenarioDefinition definition)
    {
        var goods = definition.Goods.Select(good => new GoodSpec(
            good.Id,
            good.DisplayName,
            good.BasePrice,
            good.Sensitivity,
            good.MinPrice,
            good.MaxPrice)).ToArray();

        var stations = definition.Stations.Select(station => new StationSpec(
            station.Id,
            station.DisplayName,
            station.GridX,
            station.GridY,
            station.Role)).ToArray();

        var edges = definition.Edges.Select(edge => new RailEdgeSpec(
            edge.StationAId,
            edge.StationBId,
            edge.Ticks)).ToArray();

        var markets = new Dictionary<MarketKey, MarketSlot>();
        foreach (var station in definition.Stations)
        {
            foreach (var market in station.Markets)
            {
                markets[new MarketKey(station.Id, market.GoodId)] = new MarketSlot(
                    market.Supply,
                    market.SupplyCap,
                    market.SupplyRestore,
                    market.Demand,
                    market.DemandCap,
                    market.DemandRestore);
            }
        }

        var rules = definition.PresetPlan.Rules.Select(rule => new TradeRule(
            rule.StationId,
            rule.GoodId,
            rule.IsBuy ? TradeAction.Buy : TradeAction.Sell,
            rule.Threshold,
            rule.TargetHolding)).ToArray();
        var plan = new TradePlan(definition.PresetPlan.StopIds.ToArray(), rules);

        return new ScenarioData(
            goods,
            stations,
            edges,
            markets,
            definition.StartingCash,
            definition.CargoCapacity,
            definition.StartingStationId,
            plan);
    }
}

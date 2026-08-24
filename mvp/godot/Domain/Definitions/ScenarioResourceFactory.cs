namespace TrainMvp.Domain.Definitions;

public static class ScenarioResourceFactory
{
    public static ScenarioDefinition CreateApproved()
    {
        var scenario = new ScenarioDefinition
        {
            StartingCash = MvpContent.StartingCash,
            CargoCapacity = MvpContent.CargoCapacity,
            StartingStationId = MvpContent.StartingStationId
        };

        foreach (var good in MvpContent.Goods)
        {
            scenario.Goods.Add(new GoodDefinition
            {
                Id = good.Id,
                DisplayName = good.Name,
                BasePrice = good.BasePrice,
                Sensitivity = good.Sensitivity,
                MinPrice = good.MinPrice,
                MaxPrice = good.MaxPrice
            });
        }

        var markets = MvpContent.CreateInitialMarkets();
        foreach (var station in MvpContent.Stations)
        {
            var definition = new StationDefinition
            {
                Id = station.Id,
                DisplayName = station.Name,
                GridX = station.GridX,
                GridY = station.GridY,
                Role = station.Role
            };
            foreach (var good in MvpContent.Goods)
            {
                var slot = markets[new MarketKey(station.Id, good.Id)];
                definition.Markets.Add(new StationGoodDefinition
                {
                    GoodId = good.Id,
                    Supply = slot.Supply,
                    SupplyCap = slot.SupplyCap,
                    SupplyRestore = slot.SupplyRestore,
                    Demand = slot.Demand,
                    DemandCap = slot.DemandCap,
                    DemandRestore = slot.DemandRestore
                });
            }

            scenario.Stations.Add(definition);
        }

        foreach (var edge in MvpContent.Edges)
        {
            scenario.Edges.Add(new RailEdgeDefinition
            {
                StationAId = edge.A,
                StationBId = edge.B,
                Ticks = edge.Ticks
            });
        }

        var plan = MvpContent.CreatePresetPlan();
        scenario.PresetPlan = new TradePlanDefinition();
        foreach (var stop in plan.Stops)
        {
            scenario.PresetPlan.StopIds.Add(stop);
        }

        foreach (var rule in plan.Rules)
        {
            scenario.PresetPlan.Rules.Add(new TradeRuleDefinition
            {
                StationId = rule.StationId,
                GoodId = rule.GoodId,
                IsBuy = rule.Action == TradeAction.Buy,
                Threshold = rule.Threshold,
                TargetHolding = rule.TargetHolding
            });
        }

        return scenario;
    }
}

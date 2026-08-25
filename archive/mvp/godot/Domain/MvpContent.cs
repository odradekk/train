namespace TrainMvp.Domain;

public static class MvpContent
{
    public static IReadOnlyList<GoodSpec> Goods { get; } =
    [
        new(GoodIds.Grain, "粮", 10, 0.6, 6, 18),
        new(GoodIds.Coal, "煤", 20, 0.5, 12, 32),
        new(GoodIds.Tea, "茶", 40, 0.4, 24, 64)
    ];

    public static IReadOnlyList<StationSpec> Stations { get; } =
    [
        new(StationIds.NorthMine, "北矿", 0, 0, "煤供给"),
        new(StationIds.NorthPost, "北驿", 1, 0, "弱煤需求 / 北向中转"),
        new(StationIds.EastTea, "东茶", 2, 0, "茶供给"),
        new(StationIds.WestBarn, "西仓", 0, 1, "粮供给"),
        new(StationIds.Hub, "中枢", 1, 1, "弱综合需求 / 路网中心"),
        new(StationIds.EastPost, "东驿", 2, 1, "弱茶需求 / 东向中转"),
        new(StationIds.WestPost, "西驿", 0, 2, "弱粮需求 / 西向中转"),
        new(StationIds.SouthMarket, "南市", 1, 2, "粮需求"),
        new(StationIds.Harbor, "港湾", 2, 2, "茶需求 + 弱粮需求"),
        new(StationIds.SouthFactory, "南厂", 0, 3, "煤需求"),
        new(StationIds.Capital, "都门", 1, 3, "综合需求"),
        new(StationIds.Frontier, "边站", 2, 3, "路远、高上限综合需求")
    ];

    public static IReadOnlyList<RailEdgeSpec> Edges { get; } =
    [
        new(StationIds.NorthMine, StationIds.NorthPost, 2),
        new(StationIds.NorthPost, StationIds.EastTea, 2),
        new(StationIds.NorthMine, StationIds.WestBarn, 3),
        new(StationIds.NorthPost, StationIds.Hub, 2),
        new(StationIds.EastTea, StationIds.EastPost, 2),
        new(StationIds.WestBarn, StationIds.Hub, 2),
        new(StationIds.Hub, StationIds.EastPost, 2),
        new(StationIds.WestBarn, StationIds.WestPost, 2),
        new(StationIds.Hub, StationIds.SouthMarket, 1),
        new(StationIds.EastPost, StationIds.Harbor, 3),
        new(StationIds.WestPost, StationIds.SouthMarket, 2),
        new(StationIds.SouthMarket, StationIds.Harbor, 2),
        new(StationIds.WestPost, StationIds.SouthFactory, 3),
        new(StationIds.Hub, StationIds.SouthFactory, 2),
        new(StationIds.SouthMarket, StationIds.Capital, 2),
        new(StationIds.SouthFactory, StationIds.Capital, 2),
        new(StationIds.Harbor, StationIds.Capital, 2),
        new(StationIds.Capital, StationIds.Frontier, 4),
        new(StationIds.Harbor, StationIds.Frontier, 5),
        new(StationIds.Hub, StationIds.Capital, 3)
    ];

    public static IReadOnlyDictionary<MarketKey, MarketSlot> CreateInitialMarkets()
    {
        var markets = new Dictionary<MarketKey, MarketSlot>();
        foreach (var station in Stations)
        {
            foreach (var good in Goods)
            {
                markets[new MarketKey(station.Id, good.Id)] = Zero();
            }
        }

        Set(markets, StationIds.NorthMine, GoodIds.Coal, 6, 6, 1, 0, 0, 0);
        Set(markets, StationIds.WestBarn, GoodIds.Grain, 8, 8, 1, 0, 0, 0);
        Set(markets, StationIds.EastTea, GoodIds.Tea, 6, 6, 1, 0, 0, 0);
        Set(markets, StationIds.NorthPost, GoodIds.Coal, 0, 0, 0, 3, 3, 1);
        Set(markets, StationIds.WestPost, GoodIds.Grain, 0, 0, 0, 3, 3, 1);
        Set(markets, StationIds.Hub, GoodIds.Grain, 0, 0, 0, 3, 3, 0);
        Set(markets, StationIds.Hub, GoodIds.Coal, 0, 0, 0, 2, 2, 0);
        Set(markets, StationIds.Hub, GoodIds.Tea, 0, 0, 0, 2, 2, 0);
        Set(markets, StationIds.EastPost, GoodIds.Tea, 0, 0, 0, 2, 2, 1);
        Set(markets, StationIds.SouthFactory, GoodIds.Grain, 0, 0, 0, 3, 3, 1);
        Set(markets, StationIds.SouthFactory, GoodIds.Coal, 0, 0, 0, 6, 6, 1);
        Set(markets, StationIds.SouthMarket, GoodIds.Grain, 0, 0, 0, 8, 8, 1);
        Set(markets, StationIds.Harbor, GoodIds.Grain, 0, 0, 0, 4, 4, 1);
        Set(markets, StationIds.Harbor, GoodIds.Tea, 0, 0, 0, 4, 4, 1);
        Set(markets, StationIds.Frontier, GoodIds.Grain, 0, 0, 0, 10, 10, 1);
        Set(markets, StationIds.Frontier, GoodIds.Coal, 0, 0, 0, 6, 6, 1);
        Set(markets, StationIds.Frontier, GoodIds.Tea, 0, 0, 0, 4, 4, 1);
        Set(markets, StationIds.Capital, GoodIds.Grain, 0, 0, 0, 6, 6, 1);
        Set(markets, StationIds.Capital, GoodIds.Coal, 0, 0, 0, 5, 5, 1);
        Set(markets, StationIds.Capital, GoodIds.Tea, 0, 0, 0, 4, 4, 1);
        return markets;
    }

    public static TradePlan CreatePresetPlan()
    {
        return new TradePlan(
            [StationIds.WestBarn, StationIds.SouthMarket],
            [
                new TradeRule(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 8),
                new TradeRule(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 17, 0)
            ]);
    }

    public const int StartingCash = 80;
    public const int CargoCapacity = 8;
    public const string StartingStationId = StationIds.WestBarn;

    private static MarketSlot Zero() => new(0, 0, 0, 0, 0, 0);

    private static void Set(
        Dictionary<MarketKey, MarketSlot> markets,
        string stationId,
        string goodId,
        int supply,
        int supplyCap,
        int supplyRestore,
        int demand,
        int demandCap,
        int demandRestore)
    {
        markets[new MarketKey(stationId, goodId)] = new MarketSlot(
            supply, supplyCap, supplyRestore, demand, demandCap, demandRestore);
    }
}

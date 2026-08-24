namespace TrainMvp.Domain;

public sealed record GoodSpec(
    string Id,
    string Name,
    int BasePrice,
    double Sensitivity,
    int MinPrice,
    int MaxPrice);

public sealed record StationSpec(
    string Id,
    string Name,
    int GridX,
    int GridY,
    string Role);

public sealed record RailEdgeSpec(string A, string B, int Ticks);

public sealed record MarketKey(string StationId, string GoodId);

public sealed class MarketSlot
{
    public MarketSlot(
        int supply,
        int supplyCap,
        int supplyRestore,
        int demand,
        int demandCap,
        int demandRestore)
    {
        Supply = supply;
        SupplyCap = supplyCap;
        SupplyRestore = supplyRestore;
        Demand = demand;
        DemandCap = demandCap;
        DemandRestore = demandRestore;
    }

    public int Supply { get; set; }
    public int SupplyCap { get; }
    public int SupplyRestore { get; }
    public int Demand { get; set; }
    public int DemandCap { get; }
    public int DemandRestore { get; }

    public MarketSlot Clone()
    {
        return new MarketSlot(Supply, SupplyCap, SupplyRestore, Demand, DemandCap, DemandRestore);
    }
}

public sealed class ScenarioData
{
    public ScenarioData(
        IReadOnlyList<GoodSpec> goods,
        IReadOnlyList<StationSpec> stations,
        IReadOnlyList<RailEdgeSpec> edges,
        IReadOnlyDictionary<MarketKey, MarketSlot> initialMarkets,
        int startingCash,
        int cargoCapacity,
        string startingStationId,
        TradePlan presetPlan)
    {
        Goods = goods.ToDictionary(good => good.Id, StringComparer.Ordinal);
        Stations = stations.ToDictionary(station => station.Id, StringComparer.Ordinal);
        StationOrder = stations.Select(station => station.Id).ToArray();
        Edges = edges.ToArray();
        InitialMarkets = initialMarkets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone());
        StartingCash = startingCash;
        CargoCapacity = cargoCapacity;
        StartingStationId = startingStationId;
        PresetPlan = presetPlan;
    }

    public IReadOnlyDictionary<string, GoodSpec> Goods { get; }
    public IReadOnlyDictionary<string, StationSpec> Stations { get; }
    public IReadOnlyList<string> StationOrder { get; }
    public IReadOnlyList<RailEdgeSpec> Edges { get; }
    public IReadOnlyDictionary<MarketKey, MarketSlot> InitialMarkets { get; }
    public int StartingCash { get; }
    public int CargoCapacity { get; }
    public string StartingStationId { get; }
    public TradePlan PresetPlan { get; }

    public string StationName(string stationId)
    {
        return Stations.TryGetValue(stationId, out var station) ? station.Name : stationId;
    }

    public string GoodName(string goodId)
    {
        return Goods.TryGetValue(goodId, out var good) ? good.Name : goodId;
    }

    public MarketSlot InitialMarket(string stationId, string goodId)
    {
        return InitialMarkets[new MarketKey(stationId, goodId)];
    }

    public bool HasSupply(string stationId, string goodId)
    {
        return InitialMarket(stationId, goodId).SupplyCap > 0;
    }

    public bool HasDemand(string stationId, string goodId)
    {
        return InitialMarket(stationId, goodId).DemandCap > 0;
    }

    public IReadOnlyList<string> TradableGoods(string stationId)
    {
        return Goods.Keys
            .Where(goodId => HasSupply(stationId, goodId) || HasDemand(stationId, goodId))
            .ToArray();
    }
}

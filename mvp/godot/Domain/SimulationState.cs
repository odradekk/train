namespace TrainMvp.Domain;

public sealed class SimulationState
{
    public SimulationState(
        ScenarioData scenario,
        int cash,
        int capacity,
        IReadOnlyDictionary<string, int> cargo,
        string locationId,
        IReadOnlyDictionary<MarketKey, MarketSlot> markets,
        TradePlan plan)
    {
        Scenario = scenario;
        Cash = cash;
        Capacity = capacity;
        Cargo = cargo.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        LocationId = locationId;
        Markets = markets.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        Plan = plan;
    }

    public ScenarioData Scenario { get; }
    public int Cash { get; set; }
    public int Capacity { get; }
    public Dictionary<string, int> Cargo { get; }
    public string LocationId { get; set; }
    public Dictionary<MarketKey, MarketSlot> Markets { get; }
    public TradePlan Plan { get; set; }

    public int CargoUsed => Cargo.Values.Sum();

    public int RemainingCapacity => Capacity - CargoUsed;

    public int Holding(string goodId)
    {
        return Cargo.TryGetValue(goodId, out var amount) ? amount : 0;
    }

    public MarketSlot GetMarket(string stationId, string goodId)
    {
        return Markets[new MarketKey(stationId, goodId)];
    }

    public SimulationState Clone()
    {
        return new SimulationState(Scenario, Cash, Capacity, Cargo, LocationId, Markets, Plan);
    }

    public static SimulationState FromScenario(ScenarioData scenario)
    {
        var cargo = scenario.Goods.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        return new SimulationState(
            scenario,
            scenario.StartingCash,
            scenario.CargoCapacity,
            cargo,
            scenario.StartingStationId,
            scenario.InitialMarkets,
            scenario.PresetPlan);
    }
}

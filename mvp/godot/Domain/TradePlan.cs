namespace TrainMvp.Domain;

public enum TradeAction
{
    Buy,
    Sell
}

public sealed record TradeRule(
    string StationId,
    string GoodId,
    TradeAction Action,
    int Threshold,
    int TargetHolding);

public sealed class TradePlan
{
    public TradePlan(IReadOnlyList<string> stops, IReadOnlyList<TradeRule> rules)
    {
        Stops = stops.ToArray();
        Rules = rules.ToArray();
    }

    public IReadOnlyList<string> Stops { get; }

    public IReadOnlyList<TradeRule> Rules { get; }

    public IReadOnlyList<TradeRule> RulesAt(string stationId)
    {
        return Rules.Where(rule => rule.StationId == stationId).ToArray();
    }

    public TradePlan WithThreshold(string stationId, string goodId, int threshold)
    {
        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Price threshold must be a positive integer.");
        }

        var nextRules = Rules
            .Select(rule => rule.StationId == stationId && rule.GoodId == goodId
                ? rule with { Threshold = threshold }
                : rule)
            .ToArray();
        return new TradePlan(Stops, nextRules);
    }
}

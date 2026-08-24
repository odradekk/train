namespace TrainMvp.Domain;

public enum RuleOutcomeKind
{
    Filled,
    Partial,
    None
}

public enum RuleFailureCause
{
    None,
    PriceConditionNotMet,
    TargetAlreadyMet,
    InsufficientSupply,
    InsufficientDemand,
    InsufficientCargoSpace,
    InsufficientFunds
}

public sealed record MarketSnapshot(int Supply, int Demand, int Price);

public sealed record MarketChange(
    string StationId,
    string GoodId,
    MarketSnapshot Before,
    MarketSnapshot After);

public sealed record RuleResult(
    string StationId,
    string GoodId,
    TradeAction Action,
    int Threshold,
    int TargetHolding,
    int QuotedPrice,
    int ExpectedQuantity,
    int ActualQuantity,
    int CashDelta,
    RuleOutcomeKind Outcome,
    RuleFailureCause Cause);

public sealed record StopResult(
    string StationId,
    bool Traded,
    int TravelTicks,
    IReadOnlyList<string> Path,
    IReadOnlyList<RuleResult> Rules);

public sealed class RunReport
{
    public required int Profit { get; init; }
    public required int CashBefore { get; init; }
    public required int CashAfter { get; init; }
    public required int LoopTicks { get; init; }
    public required IReadOnlyDictionary<string, int> CargoAfter { get; init; }
    public required int CargoUsedAfter { get; init; }
    public required int Capacity { get; init; }
    public required IReadOnlyList<StopResult> Stops { get; init; }
    public required IReadOnlyList<MarketChange> MarketChanges { get; init; }
    public required string NextHint { get; init; }
    public required int SoldQuantity { get; init; }
    public int RepositionTicks { get; init; }
    public string? RepositionFromId { get; init; }
    public string? RepositionToId { get; init; }
    public IReadOnlyList<string> RepositionPath { get; init; } = [];

    public static string FormatCause(RuleFailureCause cause)
    {
        return cause switch
        {
            RuleFailureCause.None => "",
            RuleFailureCause.PriceConditionNotMet => "价格条件不满足",
            RuleFailureCause.TargetAlreadyMet => "目标已满足",
            RuleFailureCause.InsufficientSupply => "供给不足",
            RuleFailureCause.InsufficientDemand => "需求不足",
            RuleFailureCause.InsufficientCargoSpace => "货舱不足",
            RuleFailureCause.InsufficientFunds => "资金不足",
            _ => cause.ToString()
        };
    }

    public static string FormatOutcome(RuleOutcomeKind outcome)
    {
        return outcome switch
        {
            RuleOutcomeKind.Filled => "成交",
            RuleOutcomeKind.Partial => "部分成交",
            RuleOutcomeKind.None => "未成交",
            _ => outcome.ToString()
        };
    }

    public static string FormatAction(TradeAction action)
    {
        return action == TradeAction.Buy ? "买入" : "卖出";
    }

    public static string BuildNextHint(ScenarioData scenario, IReadOnlyList<StopResult> stops)
    {
        foreach (var stop in stops)
        {
            if (!stop.Traded)
            {
                continue;
            }

            foreach (var rule in stop.Rules)
            {
                if (rule.Outcome == RuleOutcomeKind.Filled)
                {
                    continue;
                }

                return FormatRuleHint(scenario, rule);
            }
        }

        return "本圈无未完成交易；请自行比较利润、耗时和压舱货物。";
    }

    private static string FormatRuleHint(ScenarioData scenario, RuleResult rule)
    {
        var station = scenario.StationName(rule.StationId);
        var good = scenario.GoodName(rule.GoodId);
        var verb = rule.Action == TradeAction.Buy ? "买" : "卖";
        if (rule.Outcome == RuleOutcomeKind.None && rule.Cause == RuleFailureCause.PriceConditionNotMet)
        {
            var relation = rule.Action == TradeAction.Sell ? "低于" : "高于";
            return $"{station}{verb}{good}未成交：现价 {rule.QuotedPrice} {relation}门槛 {rule.Threshold}。";
        }

        return $"{station}{verb}{good}{FormatOutcome(rule.Outcome)}：报价 {rule.QuotedPrice} / 门槛 {rule.Threshold}，期望 {rule.ExpectedQuantity} / 实际 {rule.ActualQuantity}，{FormatCause(rule.Cause)}。";
    }
}

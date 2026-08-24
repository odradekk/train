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

public sealed record PlanIssue(string Code, string Message);

public sealed class TradePlan
{
    public TradePlan(IReadOnlyList<string> stops, IReadOnlyList<TradeRule> rules)
    {
        Stops = stops.ToArray();
        Rules = rules.ToArray();
    }

    public IReadOnlyList<string> Stops { get; }

    public IReadOnlyList<TradeRule> Rules { get; }

    public int BuyTargetOccupancy =>
        Rules
            .Where(rule => rule.Action == TradeAction.Buy)
            .GroupBy(rule => rule.GoodId, StringComparer.Ordinal)
            .Sum(group => group.Max(rule => rule.TargetHolding));

    public bool HasCapacityCompetition(int capacity)
    {
        return BuyTargetOccupancy > capacity;
    }

    public IReadOnlyList<TradeRule> RulesAt(string stationId)
    {
        return Rules.Where(rule => rule.StationId == stationId).ToArray();
    }

    public TradePlan AddStop(string stationId)
    {
        if (Stops.Contains(stationId, StringComparer.Ordinal))
        {
            return this;
        }

        return new TradePlan(Stops.Append(stationId).ToArray(), Rules);
    }

    public TradePlan RemoveStop(string stationId)
    {
        return new TradePlan(
            Stops.Where(id => id != stationId).ToArray(),
            Rules.Where(rule => rule.StationId != stationId).ToArray());
    }

    public TradePlan MoveStop(int index, int delta)
    {
        var next = index + delta;
        if (index < 0 || index >= Stops.Count || next < 0 || next >= Stops.Count)
        {
            return this;
        }

        var stops = Stops.ToArray();
        (stops[index], stops[next]) = (stops[next], stops[index]);
        return new TradePlan(stops, Rules);
    }

    public TradePlan UpsertRule(TradeRule rule)
    {
        var rules = Rules.ToList();
        var existing = rules.FindIndex(item =>
            item.StationId == rule.StationId && item.GoodId == rule.GoodId);
        if (existing >= 0)
        {
            rules[existing] = rule;
        }
        else
        {
            rules.Add(rule);
        }

        return new TradePlan(Stops, rules);
    }

    public TradePlan RemoveRule(string stationId, string goodId)
    {
        return new TradePlan(
            Stops,
            Rules.Where(rule => !(rule.StationId == stationId && rule.GoodId == goodId)).ToArray());
    }

    public TradePlan MoveRule(string stationId, int index, int delta)
    {
        var local = RulesAt(stationId).ToList();
        var next = index + delta;
        if (index < 0 || index >= local.Count || next < 0 || next >= local.Count)
        {
            return this;
        }

        (local[index], local[next]) = (local[next], local[index]);
        var rebuilt = new List<TradeRule>();
        var inserted = false;
        foreach (var rule in Rules)
        {
            if (rule.StationId != stationId)
            {
                rebuilt.Add(rule);
                continue;
            }

            if (!inserted)
            {
                rebuilt.AddRange(local);
                inserted = true;
            }
        }

        return new TradePlan(Stops, rebuilt);
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

    public static IReadOnlyList<PlanIssue> Validate(TradePlan plan, ScenarioData scenario)
    {
        var issues = new List<PlanIssue>();
        if (plan.Stops.Count < 2)
        {
            issues.Add(new PlanIssue("too_few_stops", "贸易环线至少需要 2 个交易停靠站。"));
        }

        if (plan.Stops.Distinct(StringComparer.Ordinal).Count() != plan.Stops.Count)
        {
            issues.Add(new PlanIssue("duplicate_stop", "交易停靠站不能重复。"));
        }

        foreach (var rule in plan.Rules)
        {
            issues.AddRange(ValidateRule(plan, scenario, rule));
        }

        return issues;
    }

    public static IReadOnlyList<PlanIssue> ValidateRule(TradePlan plan, ScenarioData scenario, TradeRule rule)
    {
        var issues = new List<PlanIssue>();
        if (!plan.Stops.Contains(rule.StationId, StringComparer.Ordinal))
        {
            issues.Add(new PlanIssue("rule_stop", "只能为已加入贸易环线的交易停靠站配置规则。"));
        }

        if (rule.Threshold < 1)
        {
            issues.Add(new PlanIssue("threshold", "阈值必须是正整数。"));
        }

        if (rule.Action == TradeAction.Buy)
        {
            if (!scenario.HasSupply(rule.StationId, rule.GoodId))
            {
                issues.Add(new PlanIssue("no_supply", "无供给站点不能买入。"));
            }

            if (rule.TargetHolding is < 1 or > 8)
            {
                issues.Add(new PlanIssue("buy_target", "买入目标持有量必须在 1 到 8 之间。"));
            }
        }
        else
        {
            if (!scenario.HasDemand(rule.StationId, rule.GoodId))
            {
                issues.Add(new PlanIssue("no_demand", "无需求站点不能卖出。"));
            }

            if (rule.TargetHolding is < 0 or > 8)
            {
                issues.Add(new PlanIssue("sell_target", "卖出目标持有量必须在 0 到 8 之间。"));
            }
        }

        return issues;
    }

    public static bool CanStart(TradePlan plan, ScenarioData scenario)
    {
        return Validate(plan, scenario).Count == 0;
    }
}

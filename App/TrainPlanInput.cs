using System.Collections.Immutable;
using System.Globalization;
using Train.Core.Contracts;

namespace Train.App;

internal sealed record PlanRowInput(NodeId Node, bool Station, string DwellSeconds, ulong? UnchangedDwellSteps = null,
    IReadOnlyList<GoodsRuleInput>? GoodsRules = null);
internal sealed record GoodsRuleInput(TradeAction Action, GoodId? Good, InventoryId? Inventory,
    string TargetQuantity, string UnitPrice);

internal static class TrainPlanInput
{
    internal static bool TryPlan(TrainId train, bool loop, IReadOnlyList<PlanRowInput> rows,
        out CoreCommand.ReplaceRunPlan? command, out string? error)
    {
        command = null;
        error = "列车和节点 ID 必须非零，计划至少有一项，停站秒数必须为可换算为模拟步数的非负整数。";
        if (train.Value == 0 || rows.Count == 0) return false;
        var stops = ImmutableArray.CreateBuilder<PlanStop>();
        foreach (var row in rows)
        {
            if (row.Node.Value == 0) return false;
            if (!row.Station)
            {
                if (row.GoodsRules is { Count: > 0 })
                { error = "请先删除货物规则，再将停靠站改为途经点。"; return false; }
                stops.Add(new PlanStop.Waypoint(row.Node));
                continue;
            }
            ulong steps;
            if (row.UnchangedDwellSteps is { } unchanged) steps = unchanged;
            else
            {
                if (!ulong.TryParse(row.DwellSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return false;
                try { steps = checked(seconds * (ulong)SimulationTime.StepsPerSecond); }
                catch (OverflowException) { return false; }
            }
            var rules = ImmutableArray.CreateBuilder<AutomaticGoodsRule>();
            foreach (var rule in row.GoodsRules ?? [])
            {
                error = "货物规则须选择货物和本地库存；目标持有量、逐件限价须为非负整数，空限价表示不限价。";
                if (rule.Good is null || rule.Inventory is not { Value: > 0 } inventory ||
                    !long.TryParse(rule.TargetQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out var target) || target < 0)
                    return false;
                Money? price = null;
                if (rule.Action is TradeAction.Buy or TradeAction.Sell && rule.UnitPrice.Length > 0)
                {
                    if (!long.TryParse(rule.UnitPrice, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
                        return false;
                    price = new(value);
                }
                AutomaticGoodsAction? action = rule.Action switch
                {
                    TradeAction.Buy => new AutomaticGoodsAction.Buy(price),
                    TradeAction.Sell => new AutomaticGoodsAction.Sell(price),
                    TradeAction.Load => new AutomaticGoodsAction.Load(),
                    TradeAction.Unload => new AutomaticGoodsAction.Unload(),
                    _ => null
                };
                if (action is null) return false;
                rules.Add(new(action, rule.Good, inventory, target));
            }
            stops.Add(new PlanStop.Station(row.Node, steps, rules.ToImmutable()));
        }
        command = new(train, new(loop, stops.ToImmutable()));
        error = null;
        return true;
    }
}

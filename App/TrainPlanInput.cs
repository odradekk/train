using System.Collections.Immutable;
using System.Globalization;
using Train.Core.Contracts;

namespace Train.App;

internal sealed record PlanRowInput(NodeId Node, bool Station, string DwellSeconds, ulong? UnchangedDwellSteps = null,
    IReadOnlyList<GoodsRuleInput>? GoodsRules = null);
internal sealed record GoodsRuleInput(TradeAction Action, GoodId? Good, InventoryId? Inventory,
    string TargetQuantity, string UnitPrice);

internal static partial class TrainPlanInput
{
    internal static bool TryPlan(TrainId train, bool loop, IReadOnlyList<PlanRowInput> rows,
        out CoreCommand.ReplaceRunPlan? command, out string? error)
    {
        var problems = new List<PlanIssue>();
        command = Parse(train, loop, rows, problems);
        error = problems.FirstOrDefault()?.Message;
        return command is not null;
    }

    private static CoreCommand.ReplaceRunPlan? Parse(TrainId train, bool loop, IReadOnlyList<PlanRowInput> rows,
        List<PlanIssue> problems)
    {
        if (train.Value == 0) problems.Add(new(null, null, "列车已不存在，请重新打开运行计划。"));
        if (rows.Count == 0) problems.Add(new(null, null, "请在地图上选择地点，或搜索地名添加第一站。"));
        var stops = ImmutableArray.CreateBuilder<PlanStop>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Node.Value == 0) problems.Add(new(index, null, "地点已不存在，请删除这一站后重新添加。"));
            if (!row.Station)
            {
                if (row.GoodsRules is { Count: > 0 })
                    problems.Add(new(index, null, "请先删除交易规则，再将停靠站改为途经点。"));
                stops.Add(new PlanStop.Waypoint(row.Node));
                continue;
            }
            ulong steps = 0;
            if (row.UnchangedDwellSteps is { } unchanged) steps = unchanged;
            else
            {
                if (ulong.TryParse(row.DwellSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    && seconds <= ulong.MaxValue / (ulong)SimulationTime.StepsPerSecond)
                    steps = seconds * (ulong)SimulationTime.StepsPerSecond;
                else problems.Add(new(index, null, "停留时间须为范围内的非负整数秒。"));
            }
            var rules = ImmutableArray.CreateBuilder<AutomaticGoodsRule>();
            var inputs = row.GoodsRules ?? [];
            for (var ruleIndex = 0; ruleIndex < inputs.Count; ruleIndex++)
            {
                var rule = inputs[ruleIndex];
                var before = problems.Count;
                void Problem(string message) => problems.Add(new(index, ruleIndex, message));
                if (rule.Good is null) Problem("请选择货物。");
                if (rule.Inventory is not { Value: > 0 }) Problem("请选择本地库存。");
                if (!long.TryParse(rule.TargetQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out var target) || target < 0)
                    Problem("目标持有量须为范围内的非负整数。");
                Money? price = null;
                if (rule.Action is TradeAction.Buy or TradeAction.Sell && rule.UnitPrice.Length > 0)
                {
                    if (!long.TryParse(rule.UnitPrice, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
                        Problem("单价须为范围内的非负整数，留空表示不限价。");
                    else price = new(value);
                }
                AutomaticGoodsAction? action = rule.Action switch
                {
                    TradeAction.Buy => new AutomaticGoodsAction.Buy(price),
                    TradeAction.Sell => new AutomaticGoodsAction.Sell(price),
                    TradeAction.Load => new AutomaticGoodsAction.Load(),
                    TradeAction.Unload => new AutomaticGoodsAction.Unload(),
                    _ => null
                };
                if (action is null) Problem("请选择本站可用的交易动作。");
                if (problems.Count == before) rules.Add(new(action!, rule.Good!, rule.Inventory!.Value, target));
            }
            stops.Add(new PlanStop.Station(row.Node, steps, rules.ToImmutable()));
        }
        return problems.Count == 0 ? new(train, new(loop, stops.ToImmutable())) : null;
    }
}

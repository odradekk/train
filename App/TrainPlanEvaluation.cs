using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

internal sealed record PlanIssue(int? Stop, int? Rule, string Message);
internal sealed record PlanLeg(NodeId From, NodeId To, ImmutableArray<EdgeId>? Edges);
internal sealed record PlanEvaluation(CoreCommand.ReplaceRunPlan? Command,
    IReadOnlyList<PlanIssue> Problems, IReadOnlyList<PlanLeg> Legs);

internal static partial class TrainPlanInput
{
    /// <summary>同时生成草稿的问题、预览路径和可保存命令；有问题时命令为空。</summary>
    internal static PlanEvaluation Evaluate(TrainId train, bool loop, IReadOnlyList<PlanRowInput> rows,
        ICoreReadView view, IContentView content)
    {
        var problems = new List<PlanIssue>();
        var command = Parse(train, loop, rows, problems);
        var legs = new List<PlanLeg>();
        if (loop && rows.Count > 0 && rows.Select(row => row.Node).Distinct().Count() < 2)
            problems.Add(new(null, null, "循环运行至少需要两个不同的地点。"));
        var choices = new Dictionary<NodeId, IReadOnlyList<(TradeAction Action, InventoryId Inventory)>>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var node = view.World.Nodes.FirstOrDefault(item => item.Id == row.Node);
            if (node is null) problems.Add(new(index, null, "地点已不存在，请删除这一站后重新添加。"));
            else if (row.Station && StationProblem(node) is { } reason) problems.Add(new(index, null, reason));
            var goods = new HashSet<GoodId>();
            var rules = row.GoodsRules ?? [];
            for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                var rule = rules[ruleIndex];
                if (rule.Good is { } good)
                {
                    if (content.FindGood(good) is not ContentLookup<GoodPresentation>.Found { Value.Missing: false })
                        problems.Add(new(index, ruleIndex, "货物内容已缺失，请重新选择货物。"));
                    if (!goods.Add(good)) problems.Add(new(index, ruleIndex, "同一停靠站不能重复设置同一种货物，请合并或删除重复规则。"));
                }
                if (!choices.TryGetValue(row.Node, out var local)) choices.Add(row.Node, local = Choices(view, row.Node));
                if (!local.Any(item => item.Action == rule.Action))
                    problems.Add(new(index, ruleIndex, "这个地点不能执行此动作，请选择本站可用的动作。"));
                else if (rule.Inventory is { } inventory && !local.Contains((rule.Action, inventory)))
                    problems.Add(new(index, ruleIndex, "所选库存已失效或不能执行此动作，请重新选择本站库存。"));
            }
            if (index + 1 < rows.Count) AddLeg(index, row.Node, rows[index + 1].Node);
            else if (loop && rows.Count > 1) AddLeg(index, row.Node, rows[0].Node);
        }
        return new(problems.Count == 0 ? command : null, problems, legs);

        void AddLeg(int index, NodeId from, NodeId to)
        {
            var edges = TrainPlanRoute.Find(view.World, from, to);
            legs.Add(new(from, to, edges));
            if (edges is null) problems.Add(new(index, null, "到下一站没有建成的线路，请调整停靠顺序或先建设、修复线路。"));
        }
    }

    /// <summary>本站允许的动作与本地库存组合；已过期的租用仓库只允许装载。</summary>
    internal static IReadOnlyList<(TradeAction Action, InventoryId Inventory)> Choices(ICoreReadView view, NodeId node)
    {
        var choices = new List<(TradeAction, InventoryId)>();
        if (view.World.Nodes.FirstOrDefault(item => item.Id == node)?.Occupant is not NodeOccupant.Settlement occupant ||
            view.FindSettlement(occupant.Id) is not Lookup<SettlementView>.Found { Value: var settlement } ||
            settlement.Relation == SettlementRelation.Hostile) return choices;
        // 与 Core/Inventory/InventoryCommands.cs 的访问规则一致；过期租约允许取出，不允许存入。
        if (view.FindInventory(settlement.Inventory) is Lookup<InventoryView>.Found)
        {
            var actions = settlement.Relation == SettlementRelation.PlayerOwned
                ? new[] { TradeAction.Load, TradeAction.Unload } : [TradeAction.Buy, TradeAction.Sell];
            foreach (var action in actions) choices.Add((action, settlement.Inventory));
        }
        foreach (var id in settlement.Leases)
            if (view.FindLease(id) is Lookup<LeaseView>.Found { Value: var lease } && lease.Settlement == settlement.Id &&
                view.FindInventory(lease.Inventory) is Lookup<InventoryView>.Found)
            {
                choices.Add((TradeAction.Load, lease.Inventory));
                if (view.Position.CompletedSteps < lease.ExpiresAtStep) choices.Add((TradeAction.Unload, lease.Inventory));
            }
        return choices;
    }

    internal static string? StationProblem(NodeMapItem? node) => node?.Occupant switch
    {
        NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile } => null,
        NodeOccupant.Settlement => "敌对聚居地不能停靠，请改为途经点。",
        NodeOccupant.Lair => "生物聚落不能停靠，请改为途经点。",
        NodeOccupant.Empty => "空节点不能停靠，请改为途经点。",
        _ => "地点已不存在，请删除这一站后重新添加。"
    };
}

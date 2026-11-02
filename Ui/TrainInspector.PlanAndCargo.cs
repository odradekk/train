using System.Text;
using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class TrainInspector
{
    private Label _planText = null!, _capacity = null!, _emptyCargo = null!;
    private readonly Dictionary<GoodId, Label> _goods = [];
    private InventoryView? _inventory;

    private void BuildReadOnlyPages()
    {
        _planText = Text(_planPage, "PlanText", "");
        _capacity = Text(_cargoPage, "Capacity", "");
        _emptyCargo = Text(_cargoPage, "EmptyCargo", "车上没有货物。");
    }

    private void RefreshReadOnlyPages()
    {
        var text = new StringBuilder();
        if (_train.Plan is not { } plan) text.Append("没有运行计划。请点底部的「编辑计划」。");
        else
        {
            text.AppendLine(plan.Loop ? "循环运行" : "单次运行");
            for (var index = 0; index < plan.Stops.Length; index++)
            {
                text.AppendLine();
                switch (plan.Stops[index])
                {
                    case PlanStop.Waypoint waypoint:
                        text.AppendLine($"{index + 1}. {Place(waypoint.Node)} · 途经点");
                        break;
                    case PlanStop.Station station:
                        text.AppendLine($"{index + 1}. {Place(station.Node)} · 停留 {station.DwellSteps / (decimal)SimulationTime.StepsPerSecond:0.###} 秒");
                        foreach (var rule in station.GoodsRules) text.AppendLine(RuleSentence(rule));
                        if (station.GoodsRules.IsEmpty) text.AppendLine("没有交易规则。");
                        break;
                }
            }
        }
        _planText.Text = text.ToString().TrimEnd().ReplaceLineEndings("\n");
        _inventory = (_view.FindInventory(_train.Inventory) as Lookup<InventoryView>.Found)?.Value;
        _capacity.Text = _inventory is { } inventory ? $"车上货物 {_inventory.Used} / {_inventory.Capacity}\n货物质量 {inventory.GoodsMassTons:0.###} 吨" : "列车库存已不存在。";
        var stacks = _inventory?.Stacks ?? [];
        _emptyCargo.Visible = stacks.IsEmpty;
        foreach (var good in _goods.Keys.Where(good => !stacks.Any(stack => stack.Good == good)).ToArray())
        { EconomyPanelControls.RemoveControl((Control)_goods[good].GetParent()); _goods.Remove(good); }
        foreach (var stack in stacks)
        {
            if (!_goods.TryGetValue(stack.Good, out var row))
            {
                var box = new HBoxContainer(); _cargoPage.AddChild(box);
                _goods.Add(stack.Good, row = Text(box, "GoodName", ""));
            }
            row.Text = $"{GoodName(stack.Good)} × {stack.Quantity}" + (stack.DefinitionMissing ? "（内容缺失）" : "");
        }
    }

    private string RuleSentence(AutomaticGoodsRule rule)
    {
        var store = _view.FindInventory(rule.LocalInventory) is Lookup<InventoryView>.Found { Value.Owner: InventoryOwner.Lease }
            ? "租用仓库" : "库存";
        var good = GoodName(rule.Good);
        return rule.Action switch
        {
            AutomaticGoodsAction.Buy buy => $"买入{good}，直到车上有 {rule.TargetQuantity}" +
                (buy.MaximumUnitPrice is { } maximum ? $"；单价高于 {maximum.Value} 不买" : ""),
            AutomaticGoodsAction.Sell sell => $"卖出{good}，直到车上剩 {rule.TargetQuantity}" +
                (sell.MinimumUnitPrice is { } minimum ? $"；单价低于 {minimum.Value} 不卖" : ""),
            AutomaticGoodsAction.Load => $"从{store}装载{good}，直到车上有 {rule.TargetQuantity}",
            AutomaticGoodsAction.Unload => $"卸载{good}到{store}，直到车上剩 {rule.TargetQuantity}",
            _ => "交易动作已不可用。"
        };
    }
}

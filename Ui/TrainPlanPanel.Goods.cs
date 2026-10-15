using System.Globalization;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainPlanPanel
{
    private sealed record RuleRow(VBoxContainer Box, OptionButton Action, OptionButton Good,
        List<GoodId?> Goods, OptionButton Inventory, List<InventoryId?> Inventories, LineEdit Target, LineEdit Price);

    private void AddRule(Row stop, AutomaticGoodsRule? value)
    {
        var box = new VBoxContainer { Name = "Rule", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        stop.Rules.AddChild(box);
        var first = new HBoxContainer(); box.AddChild(first);
        var good = new OptionButton { Name = "Good", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        List<GoodId?> goods = [null]; good.AddItem("请选择货物");
        foreach (var id in _content.Core.Goods.Concat(_content.Core.DesignGoods).Select(item => item.Good)
            .Concat(value is null ? [] : new[] { value.Good }).Distinct())
        {
            goods.Add(id);
            var missing = _content.FindGood(id) is not ContentLookup<GoodPresentation>.Found { Value.Missing: false };
            good.AddItem(WorldPanels.GoodName(id, _content) + (missing ? "（缺失，保留身份）" : ""));
        }
        good.Select(goods.IndexOf(value?.Good)); first.AddChild(good);
        var action = new OptionButton { Name = "Action" };
        foreach (var (kind, title) in new[] { (TradeAction.Buy, "买入"), (TradeAction.Sell, "卖出"),
            (TradeAction.Load, "装载"), (TradeAction.Unload, "卸载") }) action.AddItem(title, (int)kind);
        action.Select(value?.Action switch
        { AutomaticGoodsAction.Sell => 1, AutomaticGoodsAction.Load => 2, AutomaticGoodsAction.Unload => 3, _ => 0 });
        first.AddChild(action);
        var second = new HBoxContainer(); box.AddChild(second);
        var inventory = new OptionButton { Name = "Inventory", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        List<InventoryId?> inventories = [];
        if (value is not null) { inventories.Add(value.LocalInventory); inventory.AddItem(""); inventory.Select(0); }
        second.AddChild(inventory);
        var target = new LineEdit { Name = "TargetQuantity", Text = (value?.TargetQuantity ?? 0).ToString(CultureInfo.InvariantCulture),
            PlaceholderText = "目标持有量", CustomMinimumSize = new(170, 0), TooltipText = "列车目标持有量（非负整数）" };
        second.AddChild(target);
        var price = new LineEdit { Name = "UnitPrice", Text = (value?.Action switch
            { AutomaticGoodsAction.Buy buy => buy.MaximumUnitPrice, AutomaticGoodsAction.Sell sell => sell.MinimumUnitPrice, _ => null })?.Value.ToString(CultureInfo.InvariantCulture) ?? "",
            CustomMinimumSize = new(220, 0) };
        second.AddChild(price);
        var row = new RuleRow(box, action, good, goods, inventory, inventories, target, price);
        void PriceInput()
        {
            price.Visible = action.Selected < 2;
            price.PlaceholderText = action.Selected == 0 ? "最高单价（空为不限价）" : "最低单价（空为不限价）";
            price.TooltipText = price.PlaceholderText;
        }
        action.ItemSelected += _ => PriceInput(); PriceInput();
        var buttons = new HBoxContainer(); box.AddChild(buttons);
        buttons.AddChild(UiStyle.Button("RuleUp", "规则上移", () => MoveRule(stop, row, -1)));
        buttons.AddChild(UiStyle.Button("RuleDown", "规则下移", () => MoveRule(stop, row, 1)));
        buttons.AddChild(UiStyle.Button("RemoveRule", "删除规则", () =>
        {
            stop.Goods.Remove(row); stop.Rules.RemoveChild(box); box.QueueFree(); UpdateStopKind(stop);
        }));
        stop.Goods.Add(row); RefreshInventories(stop, row); UpdateStopKind(stop);
    }

    private void RefreshInventories(Row stop, RuleRow rule)
    {
        var selected = rule.Inventory.Selected >= 0 ? rule.Inventories[rule.Inventory.Selected] : null;
        var inventories = new List<InventoryId>();
        if (stop.Node.Selected >= 0 && stop.Node.Selected < _nodes.Length &&
            _view.World.Nodes.FirstOrDefault(node => node.Id == _nodes[stop.Node.Selected])?.Occupant is NodeOccupant.Settlement occupant &&
            _view.FindSettlement(occupant.Id) is Lookup<SettlementView>.Found settlement)
        {
            inventories.Add(settlement.Value.Inventory);
            foreach (var id in settlement.Value.Leases)
                if (_view.FindLease(id) is Lookup<LeaseView>.Found lease) inventories.Add(lease.Value.Inventory);
        }
        var visible = inventories.Where(id => _view.FindInventory(id) is Lookup<InventoryView>.Found).ToArray();
        EconomyPanels.SyncChoices(rule.Inventory, rule.Inventories, visible, selected, id =>
            _view.FindInventory(id) is Lookup<InventoryView>.Found { Value.Owner: InventoryOwner.Lease lease }
                ? $"租用库存 {id.Value} · 租约 {lease.Id.Value}" : $"聚居地库存 {id.Value}", "请选择本地库存");
        if (selected is { } retained && !visible.Contains(retained))
            rule.Inventory.SetItemText(rule.Inventories.IndexOf(retained),
                _view.FindInventory(retained) is Lookup<InventoryView>.Found
                    ? $"非本站库存 {retained.Value}（保留）" : $"已不存在：库存 {retained.Value}（保留）");
    }

    private static void UpdateStopKind(Row row)
    {
        row.Kind.SetItemDisabled(1, row.Goods.Count > 0);
        row.Seconds.Editable = row.Kind.Selected == 0;
        row.Rules.Visible = row.AddRule.Visible = row.Kind.Selected == 0;
    }

    private static void MoveRule(Row stop, RuleRow rule, int offset)
    {
        var index = stop.Goods.IndexOf(rule); var next = index + offset;
        if (next < 0 || next >= stop.Goods.Count) return;
        stop.Goods.RemoveAt(index); stop.Goods.Insert(next, rule); stop.Rules.MoveChild(rule.Box, next);
    }
}

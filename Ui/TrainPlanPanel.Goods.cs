using System.Globalization;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainPlanPanel
{
    private sealed record RuleRow(VBoxContainer Box, OptionButton Action, OptionButton Good,
        List<GoodId?> Goods, OptionButton Inventory, List<InventoryId?> Inventories, LineEdit Target, LineEdit Price,
        VBoxContainer PriceColumn, Label PriceLabel);

    private void AddRule(Row stop)
    {
        BindRule(CreateRule(stop), null);
        RefreshInventories([stop]);
        UpdateStopKind(stop);
    }

    private RuleRow CreateRule(Row stop)
    {
        var box = new VBoxContainer { Name = "Rule", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        stop.Rules.AddChild(box);
        var first = new HBoxContainer(); box.AddChild(first);
        var good = new OptionButton { Name = "Good", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        first.AddChild(good);
        var action = new OptionButton { Name = "Action" };
        foreach (var (kind, title) in new[] { (TradeAction.Buy, "买入"), (TradeAction.Sell, "卖出"),
            (TradeAction.Load, "装载"), (TradeAction.Unload, "卸载") }) action.AddItem(title, (int)kind);
        first.AddChild(action);
        var second = new HBoxContainer(); box.AddChild(second);
        var inventory = new OptionButton { Name = "Inventory", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkEnd };
        second.AddChild(inventory);
        var targetColumn = new VBoxContainer(); second.AddChild(targetColumn);
        targetColumn.AddChild(new Label { Name = "TargetQuantityLabel", Text = "目标持有量" });
        var target = new LineEdit { Name = "TargetQuantity",
            PlaceholderText = "目标持有量", CustomMinimumSize = new(170, 0), TooltipText = "列车目标持有量（非负整数）" };
        targetColumn.AddChild(target);
        var priceColumn = new VBoxContainer(); second.AddChild(priceColumn);
        var priceLabel = new Label { Name = "UnitPriceLabel" }; priceColumn.AddChild(priceLabel);
        var price = new LineEdit { Name = "UnitPrice",
            PlaceholderText = "空为不限价", CustomMinimumSize = new(220, 0) };
        priceColumn.AddChild(price);
        var row = new RuleRow(box, action, good, [], inventory, [], target, price, priceColumn, priceLabel);
        action.ItemSelected += _ => UpdatePriceInput(row);
        var buttons = new HBoxContainer(); box.AddChild(buttons);
        buttons.AddChild(UiStyle.Button("RuleUp", "规则上移", () => MoveRule(stop, row, -1)));
        buttons.AddChild(UiStyle.Button("RuleDown", "规则下移", () => MoveRule(stop, row, 1)));
        buttons.AddChild(UiStyle.Button("RemoveRule", "删除规则", () =>
        {
            stop.Goods.Remove(row); stop.Rules.RemoveChild(box); box.QueueFree(); UpdateStopKind(stop);
        }));
        stop.Goods.Add(row);
        return row;
    }

    private void BindRule(RuleRow row, AutomaticGoodsRule? value)
    {
        row.Goods.Clear(); row.Good.Clear();
        row.Goods.Add(null); row.Good.AddItem("请选择货物");
        foreach (var id in _content.Core.Goods.Concat(_content.Core.DesignGoods).Select(item => item.Good)
            .Concat(value is null ? [] : new[] { value.Good }).Distinct())
        {
            row.Goods.Add(id);
            var missing = _content.FindGood(id) is not ContentLookup<GoodPresentation>.Found { Value.Missing: false };
            row.Good.AddItem(WorldPanels.GoodName(id, _content) + (missing ? "（缺失，保留身份）" : ""));
        }
        row.Good.Select(row.Goods.IndexOf(value?.Good));
        row.Action.Select(value?.Action switch
        { AutomaticGoodsAction.Sell => 1, AutomaticGoodsAction.Load => 2, AutomaticGoodsAction.Unload => 3, _ => 0 });
        row.Inventories.Clear(); row.Inventory.Clear();
        if (value is not null) { row.Inventories.Add(value.LocalInventory); row.Inventory.AddItem(""); row.Inventory.Select(0); }
        row.Target.Text = (value?.TargetQuantity ?? 0).ToString(CultureInfo.InvariantCulture);
        row.Price.Text = (value?.Action switch
            { AutomaticGoodsAction.Buy buy => buy.MaximumUnitPrice, AutomaticGoodsAction.Sell sell => sell.MinimumUnitPrice, _ => null })?.Value.ToString(CultureInfo.InvariantCulture) ?? "";
        UpdatePriceInput(row);
    }

    private static void UpdatePriceInput(RuleRow row)
    {
        row.PriceColumn.Visible = row.Action.Selected < 2;
        row.PriceLabel.Text = row.Action.Selected == 0 ? "最高单价" : "最低单价";
        row.Price.TooltipText = row.PriceLabel.Text + "（空为不限价）";
    }

    private void RefreshInventories(IEnumerable<Row> stops)
    {
        Dictionary<NodeId, InventoryId[]> candidates = [];
        Dictionary<InventoryId, Lookup<InventoryView>> inventories = [];
        Lookup<InventoryView> FindInventory(InventoryId id)
        {
            if (!inventories.TryGetValue(id, out var found)) inventories.Add(id, found = _view.FindInventory(id));
            return found;
        }
        foreach (var stop in stops)
        {
            if (stop.Goods.Count == 0) continue;
            var node = stop.Node.Selected >= 0 && stop.Node.Selected < _nodes.Length ? _nodes[stop.Node.Selected] : default;
            if (!candidates.TryGetValue(node, out var visible))
            {
                var ids = new List<InventoryId>();
                if (_view.World.Nodes.FirstOrDefault(item => item.Id == node)?.Occupant is NodeOccupant.Settlement occupant &&
                    _view.FindSettlement(occupant.Id) is Lookup<SettlementView>.Found settlement)
                {
                    ids.Add(settlement.Value.Inventory);
                    foreach (var id in settlement.Value.Leases)
                        if (_view.FindLease(id) is Lookup<LeaseView>.Found lease) ids.Add(lease.Value.Inventory);
                }
                candidates.Add(node, visible = ids.Where(id => FindInventory(id) is Lookup<InventoryView>.Found).ToArray());
            }
            foreach (var rule in stop.Goods)
            {
                var selected = rule.Inventory.Selected >= 0 ? rule.Inventories[rule.Inventory.Selected] : null;
                EconomyPanels.SyncChoices(rule.Inventory, rule.Inventories, visible, selected, id =>
                    FindInventory(id) is Lookup<InventoryView>.Found { Value.Owner: InventoryOwner.Lease lease }
                        ? $"租用库存 {id.Value} · 租约 {lease.Id.Value}" : $"聚居地库存 {id.Value}", "请选择本地库存");
                if (selected is { } retained && !visible.Contains(retained))
                    rule.Inventory.SetItemText(rule.Inventories.IndexOf(retained), FindInventory(retained) is Lookup<InventoryView>.Found
                        ? $"非本站库存 {retained.Value}（保留）" : $"已不存在：库存 {retained.Value}（保留）");
            }
        }
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

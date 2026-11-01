using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Godot.Control;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class GoodsRows
{
    private readonly GoodsIconCache _icons;
    internal GoodsRows(GoodsIconCache icons) => _icons = icons;

    internal sealed record GoodsRow(HBoxContainer Control, GoodIcon Icon, Label Name);
    internal sealed record StockRows(Label Access, Label Capacity, Label Empty)
    {
        internal readonly Dictionary<GoodId, GoodsRow> Goods = [];
    }

    internal static StockRows BuildStock(Node parent) => new(Text(parent, "Access", ""), Text(parent, "Capacity", ""), Text(parent, "Empty", ""));

    internal void RenderInventory(VBoxContainer parent, StockRows rows, InventoryView? inventory, IContentView content, SettlementView? settlement)
    {
        rows.Access.Visible = rows.Capacity.Visible = inventory is not null;
        rows.Empty.Text = inventory is null ? "已不存在：所选库存" : "库存为空";
        rows.Empty.Visible = inventory is null || inventory.Stacks.IsEmpty;
        if (inventory is not null)
        {
            rows.Access.Text = inventory.Access switch
            { InventoryAccess.ReadWrite => "可存取", InventoryAccess.WithdrawOnly => "只取不存", _ => "已冻结：禁止存取" };
            rows.Access.AddThemeColorOverride("font_color", inventory.Access switch
            { InventoryAccess.ReadWrite => UiStyle.Success, InventoryAccess.WithdrawOnly => UiStyle.Warning, _ => UiStyle.Error });
            rows.Capacity.Text = $"容量 {inventory.Capacity} · 已用 {inventory.Used} · 预留 {inventory.Reserved} · 可用 {inventory.Available}\n货物质量 {inventory.GoodsMassTons:0.###} t" +
                (inventory.Owner is InventoryOwner.Settlement ? "\n" + ReservedStockHint : "");
        }
        var stacks = inventory?.Stacks ?? [];
        foreach (var id in rows.Goods.Keys.Where(id => !stacks.Any(stack => stack.Good == id)).ToArray())
        { RemoveControl(rows.Goods[id].Control); rows.Goods.Remove(id); }
        foreach (var stack in stacks)
        {
            if (!rows.Goods.TryGetValue(stack.Good, out var row)) rows.Goods.Add(stack.Good, row = CreateGoodRow(parent, stack.Good));
            UpdateGoodRow(row, stack.Good, stack.Quantity, content, stack.DefinitionMissing || inventory!.Access == InventoryAccess.Frozen);
            if (inventory.Owner is InventoryOwner.Settlement &&
                settlement?.Market.Goods.FirstOrDefault(item => item.Good == stack.Good) is { RestockTarget: > 0 } market)
                row.Name.Text += $" · 补货目标 {market.RestockTarget}";
        }
    }

    internal GoodsRow CreateGoodRow(Node parent, GoodId good, int size = 32)
    {
        var row = new HBoxContainer { Name = "GoodRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(row);
        var icon = _icons.Create(good, null, size); row.AddChild(icon);
        return new(row, icon, Text(row, "GoodName", ""));
    }

    internal void UpdateGoodRow(GoodsRow row, GoodId good, long? quantity, IContentView content, bool unavailable = false, bool selected = false)
    {
        row.Icon.Quantity = quantity; row.Icon.Unavailable = unavailable; row.Icon.Selected = selected;
        row.Name.Text = WorldPanels.GoodName(good, content) + (quantity is { } value ? $" × {value}" : "");
    }

}

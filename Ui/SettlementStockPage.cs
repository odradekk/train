using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class SettlementStockPage
{
    private readonly GoodsRows _goods;
    private VBoxContainer _stock = null!;
    private GoodsRows.StockRows _rows = null!;

    internal SettlementStockPage(GoodsRows goods) => _goods = goods;

    internal void Build(VBoxContainer body)
    {
        _stock = body;
        _rows = GoodsRows.BuildStock(body);
    }

    internal void Render(InventoryView? inventory, IContentView content, SettlementView? settlement)
        => _goods.RenderInventory(_stock, _rows, inventory, content, settlement);

    internal void Close() { if (_rows is not null) _rows.Goods.Clear(); }
}

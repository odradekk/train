using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class TrainInventoryPanel
{
    private readonly HostCoordinator _host;
    private readonly GoodsRows _goods;
    private PanelContainer? _panel;
    private PanelTarget? _target;
    private CoreRevision? _revision;
    private Label _state = null!;
    private VBoxContainer _stock = null!;
    private GoodsRows.StockRows _rows = null!;

    internal TrainInventoryPanel(HostCoordinator host, GoodsRows goods) { _host = host; _goods = goods; }
    internal void Open(PanelContainer panel, VBoxContainer body, PanelTarget target)
    {
        RemoveWindow(_panel);
        _target = target; _revision = null; _panel = panel;
        _state = Text(body, "State", "");
        _stock = List(body, "Stacks", 420); _rows = GoodsRows.BuildStock(_stock);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content, SettlementView? settlement)
    {
        if (_panel?.Visible != true) return;
        if (_target is null || _target.Binding != binding || !_host.PanelGenerationCurrent(_target.Binding, _target.Generation))
            Invalidate(_stock, _state);
        else if (_revision != view.Revision)
        {
            _revision = view.Revision;
            var trainId = ((WorldObjectId.Train)_target.Object).Id;
            if (view.FindTrain(trainId) is not Lookup<TrainView>.Found train)
            { Invalidate(_stock, _state); return; }
            if (view.FindInventory(train.Value.Inventory) is not Lookup<InventoryView>.Found found)
            { Invalidate(_stock, _state); return; }
            _state.Text = ""; _goods.RenderInventory(_stock, _rows, found.Value, content, settlement);
        }
    }

    internal void InvalidateRevision() => _revision = null;
    internal void Close()
    {
        RemoveWindow(_panel); _panel = null; _target = null;
        if (_rows is not null) _rows.Goods.Clear();
    }
}

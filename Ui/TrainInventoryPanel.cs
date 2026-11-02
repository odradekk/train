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
    private readonly Control _parent;
    private readonly HostCoordinator _host;
    private readonly Action<HostBinding, ICoreReadView, IContentView> _refresh;
    private readonly GoodsRows _goods;
    private PanelContainer? _panel;
    private PanelTarget? _target;
    private CoreRevision? _revision;
    private Label _state = null!;
    private VBoxContainer _stock = null!;
    private GoodsRows.StockRows _rows = null!;
    internal event Action<WorldObjectId, Control>? Opened;

    internal TrainInventoryPanel(Control parent, HostCoordinator host, GoodsRows goods,
        Action<HostBinding, ICoreReadView, IContentView> refresh)
    { _parent = parent; _host = host; _goods = goods; _refresh = refresh; }

    internal void Open(PanelTarget target, TrainId id)
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content ||
            !_host.PanelGenerationCurrent(target.Binding, target.Generation)) return;
        Close();
        _target = target; _revision = null;
        _panel = UiStyle.Panel(_parent, "TrainInventory", $"列车 {id.Value} 库存", new(1160, 70), 420,
            () => _panel!.Hide(), out var body);
        _state = Text(body, "State", "");
        _stock = List(body, "Stacks", 420); _rows = GoodsRows.BuildStock(_stock);
        _refresh(status.Binding, status.View, content);
        Opened?.Invoke(target.Object, _panel);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content, SettlementView? settlement)
    {
        if (_panel?.Visible != true) return;
        if (_target is not { } target || !Current(_host, binding, target.Binding, target.Generation))
            Invalidate(_stock, _state);
        else if (_revision != view.Revision)
        {
            _revision = view.Revision;
            var trainId = ((WorldObjectId.Train)target.Object).Id;
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

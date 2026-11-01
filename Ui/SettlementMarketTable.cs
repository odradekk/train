using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

/// <summary>
/// 聚居地的库存、市场与行内装卸交易。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed partial class SettlementMarketTable(Func<bool> canAct, GoodsRows goods,
    Func<PanelTarget, CoreCommand, SubmissionResult> submit)
{
    private const int AmountWidth = 52;
    private const int RestockTargetWidth = 80;
    private const int MinimumRowHeight = 48;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private PanelTarget _target = null!;
    private IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private VBoxContainer _rows = null!, _editor = null!;
    private Label _problems = null!, _empty = null!, _description = null!, _result = null!, _cargo = null!, _leaseLabel = null!;
    private Label _storage = null!, _secondHeading = null!, _thirdHeading = null!;
    private OptionButton _action = null!, _trainChoice = null!, _leaseChoice = null!;
    private LineEdit _quantity = null!;
    private Button _confirm = null!, _maximum = null!;
    private readonly Dictionary<GoodId, MarketRow> _items = [];
    private readonly List<TrainId?> _trains = [];
    private readonly List<LeaseId?> _leases = [];
    private readonly List<TradeAction?> _actions = [];
    private TrainId? _train;
    private LeaseId? _lease;
    private GoodId? _good;
    private TradeQuote? _quote, _pressedQuote;
    private sealed record MarketRow(VBoxContainer Container, Button Select, GoodsRows.GoodsRow Good, Label Stock, Label Buy, Label Sell);

    internal void Bind(ICoreReadView view, IContentView content, SettlementView settlement,
        IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> locals, PanelTarget target)
    { _view = view; _content = content; _settlement = settlement; _locals = locals; _target = target; }

    internal void Build(VBoxContainer body)
    {
        var header = new HBoxContainer(); body.AddChild(header);
        Text(header, "GoodsHeading", "货物");
        AmountCell(header, "库存");
        _secondHeading = AmountCell(header, "可买"); _thirdHeading = AmountCell(header, "可卖");
        _storage = Text(body, "StockStorage", "");
        _problems = Text(body, "MarketProblems", "");
        _empty = Text(body, "Empty", "没有可显示的货物。");
        _rows = new VBoxContainer { Name = "Goods" }; body.AddChild(_rows);
        _editor = new VBoxContainer { Name = "Trade", Visible = false }; body.AddChild(_editor);
        Text(_editor, "ActionLabel", "动作");
        _action = new OptionButton { Name = "Action" }; _editor.AddChild(_action);
        Text(_editor, "TrainLabel", "在站列车");
        _trainChoice = new OptionButton { Name = "Train" }; _editor.AddChild(_trainChoice);
        _leaseLabel = Text(_editor, "LeaseLabel", "租用仓库");
        _leaseChoice = new OptionButton { Name = "TradeLease" }; _editor.AddChild(_leaseChoice);
        _cargo = Text(_editor, "TrainCargo", "");
        var quantity = new HBoxContainer(); _editor.AddChild(quantity);
        Text(quantity, "QuantityLabel", "数量");
        _quantity = IntegerInput(quantity, "Quantity", "正整数", "10");
        _quantity.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _maximum = UiStyle.Button("Maximum", "最大", Maximum); quantity.AddChild(_maximum);
        _description = Text(_editor, "Quote", "");
        _confirm = UiStyle.Button("ConfirmTrade", "买入", Confirm, true); _editor.AddChild(_confirm);
        _result = Text(_editor, "TradeResult", "");
        _quantity.TextChanged += _ => Changed();
        _action.ItemSelected += _ => Changed();
        _trainChoice.ItemSelected += index => { _train = _trains[(int)index]; Changed(); };
        _leaseChoice.ItemSelected += index => { _lease = _leases[(int)index]; Changed(); };
        // 按下到释放期间即使核心修订变化，也保留玩家按下时看到的价格条件。
        _confirm.ButtonDown += () => _pressedQuote = _quote;
        _confirm.ButtonUp += () => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_confirm) && _quote?.Revision != _view.Revision)
            { RequestQuote(); UpdateActions(); }
        }).CallDeferred();
    }

    internal void Refresh()
    {
        _secondHeading.Text = Owned ? "补货目标" : "可买"; _thirdHeading.Visible = !Owned;
        _secondHeading.CustomMinimumSize = new(Owned ? RestockTargetWidth : AmountWidth, 0);
        _storage.Visible = Owned;
        _storage.Text = Owned && Local is { } stock
            ? $"仓储 已用 {stock.Used}/{stock.Capacity} · 预留 {stock.Reserved} · 可用 {stock.Available}" : "";
        var trains = _view.World.Trains.Where(item => item.Position is TrainPosition.Node node && node.Id == _settlement.Node).ToArray();
        if (_train is { } chosen && trains.All(item => item.Id != chosen)) _train = null;
        if (_train is null && trains.Length == 1) _train = trains[0].Id;
        EconomyPanels.SyncChoices(_trainChoice, _trains, trains.Select(item => item.Id), _train,
            id => Owned ? $"第{Array.FindIndex(trains, item => item.Id == id) + 1}列车" : $"列车 {id.Value}", "请选择在站列车");
        var leases = _locals.Where(item => item.Lease is not null).Select(item => item.Lease!).ToArray();
        if (_lease is { } selected && leases.All(item => item.Id != selected)) _lease = null;
        if (_lease is null && leases.Length == 1) _lease = leases[0].Id;
        EconomyPanels.SyncChoices(_leaseChoice, _leases, leases.Select(item => item.Id), _lease,
            id => $"用量 {_locals.Single(item => item.Lease?.Id == id).Inventory.Used}/{leases.Single(item => item.Id == id).Capacity} · {UiInput.FormatTime(leases.Single(item => item.Id == id).ExpiresAtStep)}到期", "请选择租用仓库");
        var actions = Owned ? new[] { TradeAction.Load, TradeAction.Unload }
            : leases.Length == 0 ? new[] { TradeAction.Buy, TradeAction.Sell }
            : new[] { TradeAction.Buy, TradeAction.Sell, TradeAction.Load, TradeAction.Unload };
        var action = _action.Selected >= 0 ? _actions[_action.Selected] : actions[0];
        if (action is null || !actions.Contains(action.Value)) action = actions[0];
        EconomyPanels.SyncChoices(_action, _actions, actions, action, ActionName);
        _problems.Text = Describe(_settlement.Market.Rule.Problems); _problems.Visible = _problems.Text.Length > 0;
        var allGoods = _settlement.Market.Goods.Select(item => item.Good)
            .Concat(_locals.Where(item => Owned ? item.Inventory.Id == _settlement.Inventory : item.Lease is not null)
                .SelectMany(item => item.Inventory.Stacks).Select(item => item.Good))
            .Concat(trains.Select(item => TrainInventory(item.Id)).OfType<InventoryView>().SelectMany(item => item.Stacks).Select(item => item.Good))
            .Concat(_good is { } selectedGood ? [selectedGood] : []).Distinct().ToArray();
        _empty.Visible = allGoods.Length == 0;
        foreach (var good in _items.Keys.Except(allGoods).ToArray()) { RemoveControl(_items[good].Container); _items.Remove(good); }
        foreach (var good in allGoods)
        {
            if (!_items.TryGetValue(good, out var row))
            {
                var container = new VBoxContainer(); _rows.AddChild(container);
                var select = UiStyle.Button("SelectGood", "", () => Select(good)); container.AddChild(select);
                select.CustomMinimumSize = new(0, MinimumRowHeight);
                select.TooltipText = $"展开{SettlementOverviewPage.GoodName(good, _content)}的交易";
                var cells = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; select.AddChild(cells);
                cells.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); cells.OffsetLeft = 8; cells.OffsetRight = -8;
                cells.MinimumSizeChanged += () => select.CustomMinimumSize = new(0, Math.Max(MinimumRowHeight, cells.GetCombinedMinimumSize().Y));
                var display = goods.CreateGoodRow(cells, good); display.Control.MouseFilter = Control.MouseFilterEnum.Ignore;
                display.Icon.MouseFilter = Control.MouseFilterEnum.Ignore;
                display.Control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row = new(container, select, display, AmountCell(cells, ""), AmountCell(cells, ""), AmountCell(cells, ""));
                _items.Add(good, row);
            }
            var item = _settlement.Market.Goods.FirstOrDefault(item => item.Good == good);
            goods.UpdateGoodRow(row.Good, good, null, _content, !Owned && item is { Blockers.IsEmpty: false }, _good == good && _editor.Visible);
            row.Good.Name.Text = SettlementOverviewPage.GoodName(good, _content);
            row.Stock.Text = Owned ? (Local?.Stacks.FirstOrDefault(stack => stack.Good == good)?.Quantity ?? 0).ToString() : item?.Stock.ToString() ?? "—";
            row.Buy.Text = Owned ? (item?.RestockTarget ?? 0).ToString() : item?.SellableQuantity.ToString() ?? "—";
            row.Buy.CustomMinimumSize = _secondHeading.CustomMinimumSize;
            row.Sell.Text = item?.ReceivableQuantity.ToString() ?? "—";
            row.Sell.Visible = !Owned;
        }
        if (!_confirm.IsPressed()) RequestQuote();
        UpdateActions();
    }

    private void Select(GoodId good)
    {
        if (!canAct()) return;
        if (_good == good && _editor.Visible) { _editor.Hide(); Refresh(); return; }
        OpenGood(good);
        Changed();
    }

    internal void OpenGood(GoodId good)
    {
        if (!canAct()) return;
        var changed = _good != good;
        _good = good; Refresh();
        _editor.Reparent(_items[good].Container, false); _editor.Show();
        if (changed) Changed();
        Refresh();
        var select = _items[good].Select;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(select)) return;
            for (var parent = select.GetParent(); parent is not null; parent = parent.GetParent())
                if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(select);
            select.GrabFocus();
        }).CallDeferred();
    }

    private void Changed()
    { _result.Text = ""; RequestQuote(); UpdateActions(); }

    private static Label AmountCell(Node parent, string value)
    {
        var label = Text(parent, "Amount", value); label.CustomMinimumSize = new(AmountWidth, 0);
        label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd; label.HorizontalAlignment = HorizontalAlignment.Right;
        return label;
    }

    private InventoryView? TrainInventory(TrainId id) => _view.FindTrain(id) is Lookup<TrainView>.Found train &&
        _view.FindInventory(train.Value.Inventory) is Lookup<InventoryView>.Found inventory ? inventory.Value : null;
    private TradeAction Action => _action.Selected >= 0 ? _actions[_action.Selected]!.Value : TradeAction.Buy;
    private bool Transfer => Action is TradeAction.Load or TradeAction.Unload;
    private bool Owned => _settlement.Relation == SettlementRelation.PlayerOwned;
    private InventoryView? Local => Transfer && !Owned ? _locals.FirstOrDefault(item => item.Lease?.Id == _lease && item.Lease is not null).Inventory
        : _locals.FirstOrDefault(item => item.Inventory.Id == _settlement.Inventory).Inventory;
    private string ActionName(TradeAction action) => action switch
    { TradeAction.Buy => "买入", TradeAction.Sell => "卖出", TradeAction.Load => Owned ? "装上列车" : "从仓库装车", _ => Owned ? "卸到库存" : "卸到仓库" };
}

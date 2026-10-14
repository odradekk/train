using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private Action<string> _diagnostic = null!;
    private GoodsIconCache _icons = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private PanelContainer? _settlementPanel;
    private PanelTarget? _settlementTarget;
    private SettlementId _settlementId;
    private SettlementView? _settlement;
    private CoreRevision? _settlementRevision;
    private EconomicQueries _queries = null!;
    private Label _state = null!;
    private Label _wallet = null!;
    private OptionButton _localChoice = null!;
    private VBoxContainer _stock = null!;
    private VBoxContainer _settlementBody = null!;
    private InventoryId? _localId;
    private readonly List<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private PanelContainer? _trainPanel;
    private PanelTarget? _trainTarget;
    private CoreRevision? _trainRevision;
    private EconomicQueries _trainQueries = null!;
    private Label _trainState = null!;
    private VBoxContainer _trainStock = null!;

    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, Action<string> diagnostic, GoodsIconCache icons)
    { _host = host; _submit = submit; _diagnostic = diagnostic; _icons = icons; }

    public override void _Ready()
    { SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore; }

    internal void OpenSettlement(PanelTarget source, SettlementId id)
    {
        if (!ReadCurrent() || source.Binding != _binding || source.Generation != _content.Generation) return;
        RemoveWindow(_settlementPanel);
        _settlementTarget = new(_binding, _content.Generation, new WorldObjectId.Settlement(id));
        _settlementId = id; _settlement = null; _settlementRevision = null; _localId = null;
        _queries = new(_diagnostic);
        _settlementPanel = UiStyle.Panel(this, "Settlement", $"聚居地 {id.Value}", new(420, 70), 720,
            () => _settlementPanel!.Hide(), out _settlementBody);
        _state = Text(_settlementBody, "State", "");
        _wallet = Text(_settlementBody, "Wallet", "");
        _localChoice = new OptionButton { Name = "LocalInventory" };
        _settlementBody.AddChild(_localChoice);
        _localChoice.ItemSelected += index =>
        {
            if (index < 0 || index >= _locals.Count) return;
            _localId = _locals[(int)index].Inventory.Id;
            RenderLocal(); ClearTradeQuote();
            RenderMarket();
        };
        var tabs = new TabContainer { Name = "Pages", CustomMinimumSize = new(680, 570) };
        _settlementBody.AddChild(tabs);
        _stock = Page(tabs, "库存");
        BuildTrade(Page(tabs, "市场"));
        BuildVehicles(Page(tabs, "现车"));
        BuildLeases(Page(tabs, "仓库租用"));
        Refresh(_binding, _view, _content);
    }

    internal void OpenTrainInventory(PanelTarget target, TrainId id)
    {
        if (!ReadCurrent() || target.Binding != _binding || target.Generation != _content.Generation) return;
        RemoveWindow(_trainPanel);
        _trainTarget = target; _trainRevision = null; _trainQueries = new(_diagnostic);
        _trainPanel = UiStyle.Panel(this, "TrainInventory", $"列车 {id.Value} 库存", new(1160, 70), 420,
            () => _trainPanel!.Hide(), out var body);
        _trainState = Text(body, "State", "");
        _trainStock = List(body, "Stacks", 420);
        Refresh(_binding, _view, _content);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _binding = binding; _view = view; _content = content;
        if (_settlementPanel?.Visible == true)
        {
            if (!Current(_settlementTarget)) Invalidate(_settlementBody, _state);
            else if (_queries.MissingEntry is { } entry) Unavailable(_settlementBody, _state, entry);
            else
            {
                if (_settlementRevision != view.Revision)
                {
                    _settlementRevision = view.Revision;
                    ReadSettlement();
                }
                if (_queries.MissingEntry is { } missing) Unavailable(_settlementBody, _state, missing);
                else if (_settlement is not null) UpdateActions();
            }
        }
        if (_trainPanel?.Visible == true)
        {
            if (!Current(_trainTarget)) Invalidate(_trainStock, _trainState);
            else if (_trainQueries.MissingEntry is { } entry) Unavailable(_trainStock, _trainState, entry);
            else if (_trainRevision != view.Revision)
            {
                _trainRevision = view.Revision;
                var trainId = ((WorldObjectId.Train)_trainTarget!.Object).Id;
                if (_view.FindTrain(trainId) is not Lookup<TrainView>.Found train)
                { Invalidate(_trainStock, _trainState); return; }
                if (!_trainQueries.TryRead(nameof(ICoreReadView.FindInventory), () => _view.FindInventory(train.Value.Inventory), out var inventory))
                { Unavailable(_trainStock, _trainState, _trainQueries.MissingEntry!); return; }
                if (inventory is not Lookup<InventoryView>.Found found)
                { Invalidate(_trainStock, _trainState); return; }
                _trainState.Text = ""; RenderInventory(_trainStock, found.Value);
            }
        }
    }

    private void ReadSettlement()
    {
        if (!_queries.TryRead(nameof(ICoreReadView.FindSettlement), () => _view.FindSettlement(_settlementId), out var settlement)) return;
        if (settlement is not Lookup<SettlementView>.Found found)
        { _settlement = null; Invalidate(_settlementBody, _state); return; }
        _settlement = found.Value;
        if (!_queries.TryRead(nameof(ICoreReadView.Economy), () => _view.Economy, out var economy)) return;
        _wallet.Text = $"资金：{economy.Wallet.Value}";
        _locals.Clear();
        if (!AddInventory(_settlement.Inventory, null)) return;
        foreach (var leaseId in _settlement.Leases)
        {
            if (!_queries.TryRead(nameof(ICoreReadView.FindLease), () => _view.FindLease(leaseId), out var lookup)) return;
            if (lookup is Lookup<LeaseView>.Found lease && !AddInventory(lease.Value.Inventory, lease.Value)) return;
        }
        _localId ??= _settlement.Inventory;
        _localChoice.Clear();
        foreach (var local in _locals)
            _localChoice.AddItem(local.Lease is { } lease ? $"租用库存 {local.Inventory.Id.Value} · 租约 {lease.Id.Value}"
                : $"聚居地库存 {local.Inventory.Id.Value}");
        var index = _locals.FindIndex(local => local.Inventory.Id == _localId);
        if (index < 0) { _localChoice.AddItem("已不存在：所选库存"); index = _locals.Count; }
        _localChoice.Select(index);
        _state.Text = _host.IsBusy ? "当前操作尚未结束" : "";
        RenderLocal(); RefreshTrade(); RefreshVehicles(); RefreshLeases();
    }

    private bool AddInventory(InventoryId id, LeaseView? lease)
    {
        if (!_queries.TryRead(nameof(ICoreReadView.FindInventory), () => _view.FindInventory(id), out var lookup)) return false;
        if (lookup is Lookup<InventoryView>.Found found) _locals.Add((found.Value, lease));
        return true;
    }

    private InventoryView? Local => _locals.FirstOrDefault(local => local.Inventory.Id == _localId).Inventory;
    private LeaseView? LocalLease => _locals.FirstOrDefault(local => local.Inventory.Id == _localId).Lease;
    private bool CanAct => Current(_settlementTarget) && _settlement is not null && _queries.MissingEntry is null &&
        !_host.IsBusy && _view.Fault is null;

    private void RenderLocal()
    {
        if (Local is not { } inventory) { Clear(_stock); Text(_stock, "Missing", "已不存在：所选库存"); return; }
        RenderInventory(_stock, inventory);
    }

    private void RenderInventory(VBoxContainer parent, InventoryView inventory)
    {
        Clear(parent);
        var access = inventory.Access switch
        { InventoryAccess.ReadWrite => "可存取", InventoryAccess.WithdrawOnly => "只取不存", _ => "已冻结：禁止存取" };
        var status = Text(parent, "Access", access);
        status.AddThemeColorOverride("font_color", inventory.Access switch
        { InventoryAccess.ReadWrite => UiStyle.Success, InventoryAccess.WithdrawOnly => UiStyle.Warning, _ => UiStyle.Error });
        Text(parent, "Capacity", $"容量 {inventory.Capacity} · 已用 {inventory.Used} · 预留 {inventory.Reserved} · 可用 {inventory.Available}\n货物质量 {inventory.GoodsMassTons:0.###} t");
        if (inventory.Stacks.IsEmpty) Text(parent, "Empty", "库存为空");
        foreach (var stack in inventory.Stacks)
            GoodRow(parent, stack.Good, stack.Quantity, stack.DefinitionMissing || inventory.Access == InventoryAccess.Frozen);
    }

    private HBoxContainer GoodRow(VBoxContainer parent, GoodId good, long? quantity, bool unavailable = false,
        bool selected = false, int size = 32)
    {
        var row = new HBoxContainer { Name = "GoodRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(row);
        var icon = _icons.Create(good, quantity, size, unavailable);
        icon.Selected = selected;
        row.AddChild(icon);
        Text(row, "GoodName", GoodName(good) + (quantity is { } value ? $" × {value}" : ""));
        return row;
    }

    private string GoodName(GoodId good) => _content.FindGood(good) is ContentLookup<GoodPresentation>.Found found
        ? found.Value.Display.Text.Name : good switch
        { GoodId.Voxel voxel => voxel.Definition.Value + "（缺失）", GoodId.Composite composite => composite.Definition.Value + "（缺失）",
            GoodId.Assembly assembly => assembly.Composition.Value + "（缺失）", _ => "缺失货物" };

    private void Submit(PanelTarget target, CoreCommand command)
    {
        if (!CanAct) return;
        _submit(target, command);
        if (ReadCurrent())
        {
            _settlementRevision = null; _trainRevision = null;
            Refresh(_binding, _view, _content);
        }
    }

    private bool ReadCurrent()
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content) return false;
        _binding = status.Binding; _view = status.View; _content = content; return true;
    }

    private bool Current(PanelTarget? target) => target is not null && target.Binding == _binding && target.Generation == _content.Generation;

    private static void Invalidate(Control body, Label state)
    { state.Text = "已不存在：对象或所属对局已失效，请重新打开。"; state.AddThemeColorOverride("font_color", UiStyle.Error); Disable(body); }

    private static void Unavailable(Control body, Label state, string entry)
    { state.Text = $"核心尚未提供：{entry}"; state.AddThemeColorOverride("font_color", UiStyle.Warning); Disable(body); }

    private static void Disable(Control body)
    {
        foreach (var button in body.FindChildren("*", "BaseButton", true, false).OfType<BaseButton>()) button.Disabled = true;
        foreach (var edit in body.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()) edit.Editable = false;
    }

    private void UpdateActions()
    {
        _localChoice.Disabled = !CanAct;
        UpdateTradeActions(); UpdateVehicleActions(); UpdateLeaseActions();
        _state.Text = _view.Fault is { } fault ? CommandDiagnostics.Describe(fault) : _host.IsBusy ? "当前操作尚未结束" : "";
    }

    private static Label Text(Node parent, string name, string value)
    {
        var label = UiStyle.Label(value); label.Name = name;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AddThemeFontOverride("font", UiStyle.NumericFont());
        parent.AddChild(label); return label;
    }

    private static VBoxContainer Page(TabContainer tabs, string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        var body = new VBoxContainer { Name = "Body", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body); return body;
    }

    private static VBoxContainer List(Node parent, string name, int height)
    {
        var scroll = new ScrollContainer { Name = name + "Scroll", CustomMinimumSize = new(0, height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll);
        var rows = new VBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(rows); return rows;
    }

    private static void Clear(Node node)
    { foreach (var child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); } }

    private static void RemoveWindow(PanelContainer? panel)
    { if (panel is null) return; panel.GetParent().RemoveChild(panel); panel.QueueFree(); }

    private static string Problems(ImmutableArray<CoreProblem> problems) => string.Join("\n", problems.Select(CommandDiagnostics.Describe));

    private static LineEdit IntegerInput(Node parent, string name, string placeholder, string initial = "")
    {
        var edit = new LineEdit { Name = name, Text = initial, PlaceholderText = placeholder };
        edit.AddThemeFontOverride("font", UiStyle.NumericFont()); parent.AddChild(edit); return edit;
    }
}

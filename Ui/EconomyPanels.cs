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
    private Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> _submitTarget = null!;
    private GoodsIconCache _icons = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private PanelContainer? _settlementPanel;
    private PanelTarget? _settlementTarget;
    private SettlementId _settlementId;
    private SettlementView? _settlement;
    private CoreRevision? _settlementRevision;
    private Label _state = null!;
    private Label _wallet = null!;
    private OptionButton _localChoice = null!;
    private readonly List<InventoryId?> _localOptions = [];
    private VBoxContainer _stock = null!;
    private StockRows _stockRows = null!;
    private VBoxContainer _settlementBody = null!;
    private InventoryId? _localId;
    private readonly List<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private PanelContainer? _trainPanel;
    private PanelTarget? _trainTarget;
    private CoreRevision? _trainRevision;
    private Label _trainState = null!;
    private VBoxContainer _trainStock = null!;
    private StockRows _trainRows = null!;

    private sealed record GoodsRow(HBoxContainer Control, GoodIcon Icon, Label Name);
    private sealed record StockRows(Label Access, Label Capacity, Label Empty)
    {
        internal readonly Dictionary<GoodId, GoodsRow> Goods = [];
    }

    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, GoodsIconCache icons,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> submitTarget)
    { _host = host; _submit = submit; _icons = icons; _submitTarget = submitTarget; }

    public override void _Ready()
    { SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore; }

    internal void OpenSettlement(PanelTarget source, SettlementId id)
    {
        if (!ReadCurrent() || !_host.PanelGenerationCurrent(source.Binding, source.Generation)) return;
        Show();
        RemoveWindow(_settlementPanel);
        _settlementTarget = new(_binding, _content.Generation, new WorldObjectId.Settlement(id));
        _settlementId = id; _settlement = null; _settlementRevision = null; _localId = null; _localOptions.Clear();
        _settlementPanel = UiStyle.Panel(this, "Settlement", $"聚居地 {id.Value}", new(420, 70), 720,
            () => _settlementPanel!.Hide(), out _settlementBody);
        _state = Text(_settlementBody, "State", "");
        _wallet = Text(_settlementBody, "Wallet", "");
        _localChoice = new OptionButton { Name = "LocalInventory" };
        _settlementBody.AddChild(_localChoice);
        _localChoice.ItemSelected += index =>
        {
            _localId = _localOptions[(int)index];
            RenderLocal(); ClearTradeQuote(); RenderMarket();
        };
        var tabs = new TabContainer { Name = "Pages", CustomMinimumSize = new(680, 570) };
        _settlementBody.AddChild(tabs);
        _stock = Page(tabs, "库存"); _stockRows = BuildStock(_stock);
        BuildTrade(Page(tabs, "市场"));
        BuildVehicles(Page(tabs, "现车"));
        BuildLeases(Page(tabs, "仓库租用"));
        BuildProduction(Page(tabs, "生产")); BuildPopulation(Page(tabs, "人口"));
        BuildServices(Page(tabs, "车辆服务"));
        tabs.TabChanged += _ => RefreshServices();
        Refresh(_binding, _view, _content);
    }

    internal void OpenTrainInventory(PanelTarget target, TrainId id)
    {
        if (!ReadCurrent() || !_host.PanelGenerationCurrent(target.Binding, target.Generation)) return;
        RemoveWindow(_trainPanel);
        _trainTarget = target; _trainRevision = null;
        _trainPanel = UiStyle.Panel(this, "TrainInventory", $"列车 {id.Value} 库存", new(1160, 70), 420,
            () => _trainPanel!.Hide(), out var body);
        _trainState = Text(body, "State", "");
        _trainStock = List(body, "Stacks", 420); _trainRows = BuildStock(_trainStock);
        Refresh(_binding, _view, _content);
    }

    internal void Close()
    {
        CloseServices();
        RemoveWindow(_settlementPanel); RemoveWindow(_trainPanel); RemoveWindow(_reportsPanel);
        _reportsPanel = null;
        _settlementPanel = _trainPanel = null; _settlementTarget = _trainTarget = null;
        _settlement = null; _locals.Clear(); _localOptions.Clear(); _tradeTrains.Clear(); _leaseIds.Clear();
        _marketItems.Clear(); _vehicleItems.Clear(); _capacityItems.Clear(); _leaseItems.Clear(); _facilityRows.Clear();
        _tradeQuote = null; _vehicleQuote = null; _leaseQuote = null;
        if (_stockRows is not null) _stockRows.Goods.Clear();
        if (_trainRows is not null) _trainRows.Goods.Clear();
        _view = null!; _content = null!;
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _binding = binding; _view = view; _content = content;
        RefreshReports();
        if (_settlementPanel?.Visible == true)
        {
            if (!Current(_settlementTarget)) Invalidate(_settlementBody, _state);
            else
            {
                if (_settlementRevision != view.Revision)
                { _settlementRevision = view.Revision; ReadSettlement(); }
                if (_settlement is not null) UpdateActions();
                if (_settlement is not null) RefreshServices();
            }
        }
        if (_trainPanel?.Visible == true)
        {
            if (!Current(_trainTarget)) Invalidate(_trainStock, _trainState);
            else if (_trainRevision != view.Revision)
            {
                _trainRevision = view.Revision;
                var trainId = ((WorldObjectId.Train)_trainTarget!.Object).Id;
                if (_view.FindTrain(trainId) is not Lookup<TrainView>.Found train)
                { Invalidate(_trainStock, _trainState); return; }
                if (_view.FindInventory(train.Value.Inventory) is not Lookup<InventoryView>.Found found)
                { Invalidate(_trainStock, _trainState); return; }
                _trainState.Text = ""; RenderInventory(_trainStock, _trainRows, found.Value);
            }
        }
    }

    private void ReadSettlement()
    {
        if (_view.FindSettlement(_settlementId) is not Lookup<SettlementView>.Found found)
        { _settlement = null; Invalidate(_settlementBody, _state); return; }
        _settlement = found.Value;
        _wallet.Text = $"资金：{_view.Economy.Wallet.Value}";
        _locals.Clear();
        AddInventory(_settlement.Inventory, null);
        foreach (var leaseId in _settlement.Leases)
        {
            if (_view.FindLease(leaseId) is Lookup<LeaseView>.Found lease) AddInventory(lease.Value.Inventory, lease.Value);
        }
        _localId ??= _settlement.Inventory;
        SyncChoices(_localChoice, _localOptions, _locals.Select(local => local.Inventory.Id), _localId,
            id => _locals.First(local => local.Inventory.Id == id).Lease is { } lease
                ? $"租用库存 {id.Value} · 租约 {lease.Id.Value}" : $"聚居地库存 {id.Value}");
        RenderLocal(); RefreshTrade(); RefreshVehicles(); RefreshLeases();
        RefreshProduction(); RefreshPopulation();
    }

    private void AddInventory(InventoryId id, LeaseView? lease)
    {
        if (_view.FindInventory(id) is Lookup<InventoryView>.Found found) _locals.Add((found.Value, lease));
    }

    private InventoryView? Local => _locals.FirstOrDefault(local => local.Inventory.Id == _localId).Inventory;
    private bool CanAct => Current(_settlementTarget) && _settlement is not null &&
        !_host.IsBusy && _view.Fault is null;

    private void RenderLocal() => RenderInventory(_stock, _stockRows, Local);
    private static StockRows BuildStock(Node parent) => new(Text(parent, "Access", ""), Text(parent, "Capacity", ""), Text(parent, "Empty", ""));

    private void RenderInventory(VBoxContainer parent, StockRows rows, InventoryView? inventory)
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
            rows.Capacity.Text = $"容量 {inventory.Capacity} · 已用 {inventory.Used} · 预留 {inventory.Reserved} · 可用 {inventory.Available}\n货物质量 {inventory.GoodsMassTons:0.###} t";
        }
        var stacks = inventory?.Stacks ?? [];
        foreach (var id in rows.Goods.Keys.Where(id => !stacks.Any(stack => stack.Good == id)).ToArray())
        { RemoveControl(rows.Goods[id].Control); rows.Goods.Remove(id); }
        foreach (var stack in stacks)
        {
            if (!rows.Goods.TryGetValue(stack.Good, out var row)) rows.Goods.Add(stack.Good, row = CreateGoodRow(parent, stack.Good));
            UpdateGoodRow(row, stack.Good, stack.Quantity, stack.DefinitionMissing || inventory!.Access == InventoryAccess.Frozen);
        }
    }

    private GoodsRow CreateGoodRow(Node parent, GoodId good, int size = 32)
    {
        var row = new HBoxContainer { Name = "GoodRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(row);
        var icon = _icons.Create(good, null, size); row.AddChild(icon);
        return new(row, icon, Text(row, "GoodName", ""));
    }

    private void UpdateGoodRow(GoodsRow row, GoodId good, long? quantity, bool unavailable = false, bool selected = false)
    {
        row.Icon.Quantity = quantity; row.Icon.Unavailable = unavailable; row.Icon.Selected = selected;
        row.Name.Text = WorldPanels.GoodName(good, _content) + (quantity is { } value ? $" × {value}" : "");
    }

    private void Submit(PanelTarget target, CoreCommand command, Action requote)
    {
        if (!CanAct) return;
        _submit(target, command);
        if (!ReadCurrent()) return;
        _settlementRevision = null; _trainRevision = null;
        Refresh(_binding, _view, _content);
        Quote(requote);
    }

    private bool ReadCurrent()
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content) return false;
        _binding = status.Binding; _view = status.View; _content = content; return true;
    }

    private bool Current(PanelTarget? target) => target is not null && target.Binding == _binding &&
        _host.PanelGenerationCurrent(target.Binding, target.Generation);

    private static void Invalidate(Control body, Label state)
    { state.Text = "已不存在：对象或所属对局已失效，请重新打开。"; state.AddThemeColorOverride("font_color", UiStyle.Error); Disable(body); }
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

    private void Quote(Action request)
    {
        if (!CanAct) return;
        request(); Refresh(_binding, _view, _content);
    }

    private T? ReadQuote<T>(CoreResult<T> result, Label description) where T : class
    {
        if (result is CoreResult<T>.Success success) return success.Value;
        description.Text = result switch
        {
            CoreResult<T>.Failure failure => "报价被拒绝：\n" + Problems(failure.Problems),
            CoreResult<T>.Faulted fault => CommandDiagnostics.Describe(fault.Fault),
            _ => throw new InvalidOperationException("未知报价结果。")
        };
        description.AddThemeColorOverride("font_color", UiStyle.Error); return null;
    }

    private void ShowQuote(Label label, CoreRevision revision, string details, ImmutableArray<CoreProblem> blockers,
        ImmutableArray<CoreProblem> warnings = default, string? reason = null)
    {
        label.Text = "报价不锁定\n" + details +
            (revision == _view.Revision ? "" : "\n报价后状态已变化，确认时由核心按当前状态判断。") +
            (blockers.IsEmpty ? "" : "\n受阻：\n" + Problems(blockers)) +
            (warnings.IsDefaultOrEmpty ? "" : "\n短缺风险：\n" + Problems(warnings)) +
            (reason is null ? "" : "\n" + reason);
        label.AddThemeColorOverride("font_color", !blockers.IsEmpty ? UiStyle.Error :
            !warnings.IsDefaultOrEmpty || reason is not null ? UiStyle.Warning : UiStyle.Foreground);
    }

    private static void ResetQuote<T>(ref T? quote, Label label, string reason = "输入已改变，请获取报价。") where T : class
    { quote = null; label.Text = "报价不锁定。" + reason; label.AddThemeColorOverride("font_color", UiStyle.Foreground); }
    private static void MissingSelection<T>(ref T? quote, Label label, string selection) where T : class =>
        ResetQuote(ref quote, label, $"已不存在：所选{selection}，请重新选择。");

    internal static void SyncChoices<T>(OptionButton choice, List<T?> options, IEnumerable<T> values, T? selected,
        Func<T, string> title, string? placeholder = null) where T : struct
    {
        var ids = values.Distinct().ToArray();
        var next = ids.Select(id => (T?)id).ToList();
        if (placeholder is not null) next.Insert(0, null);
        if (selected is not null && !next.Contains(selected)) next.Add(selected);
        for (var index = options.Count - 1; index >= 0; index--)
            if (!next.Contains(options[index])) { choice.RemoveItem(index); options.RemoveAt(index); }
        foreach (var id in next)
            if (!options.Contains(id)) { options.Add(id); choice.AddItem(""); }
        for (var index = 0; index < options.Count; index++)
        {
            var text = options[index] is not { } id ? placeholder! : ids.Contains(id) ? title(id) : "已不存在：所选项";
            if (choice.GetItemText(index) != text) choice.SetItemText(index, text);
        }
        var selectedIndex = options.IndexOf(selected);
        if (choice.Selected != selectedIndex) choice.Select(selectedIndex);
    }

    private static Label Text(Node parent, string name, string value)
    {
        var label = UiStyle.Label(value); label.Name = name;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart; label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AddThemeFontOverride("font", UiStyle.NumericFont()); parent.AddChild(label); return label;
    }
    private static VBoxContainer Page(TabContainer tabs, string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; tabs.AddChild(scroll);
        var body = new VBoxContainer { Name = "Body", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body); return body;
    }
    private static VBoxContainer List(Node parent, string name, int height)
    {
        var scroll = new ScrollContainer { Name = name + "Scroll", CustomMinimumSize = new(0, height), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll); var rows = new VBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(rows); return rows;
    }
    private static void Clear(Node node)
    { foreach (var child in node.GetChildren()) RemoveControl(child); }
    private static void RemoveControl(Node node) { node.GetParent().RemoveChild(node); node.QueueFree(); }
    private static void RemoveWindow(PanelContainer? panel) { if (panel is not null) RemoveControl(panel); }
    private static string Problems(ImmutableArray<CoreProblem> problems) => string.Join("\n", problems.Select(CommandDiagnostics.Describe));
    private static LineEdit IntegerInput(Node parent, string name, string placeholder, string initial = "")
    { var edit = new LineEdit { Name = name, Text = initial, PlaceholderText = placeholder }; edit.AddThemeFontOverride("font", UiStyle.NumericFont()); parent.AddChild(edit); return edit; }
}

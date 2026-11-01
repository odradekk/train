using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed class SettlementPanel
{
    private readonly Control _parent;
    private readonly HostCoordinator _host;
    private readonly Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit;
    private readonly Action<HostBinding, ICoreReadView, IContentView> _refresh;
    private readonly Action _invalidateTrainInventory;
    private readonly SettlementStockPage _stock;
    private readonly SettlementMarketPage _market;
    private readonly SettlementVehiclesPage _vehicles;
    private readonly SettlementLeasesPage _leases;
    private readonly SettlementProductionPage _production;
    private readonly SettlementPopulationPage _population;
    private readonly SettlementPlotsPage _plots;
    private readonly SettlementServicesPage _services;
    private SettlementOverviewPage? _overview;
    private bool _neutral;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private PanelTarget? _target;
    private SettlementId _settlementId;
    private SettlementView? _settlement;
    private CoreRevision? _revision;
    private InventoryId? _localId;
    private readonly List<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private PanelContainer? _panel;
    private VBoxContainer _body = null!;
    private Label _state = null!;
    private Label _wallet = null!;
    private Button _combat = null!;
    private OptionButton _localChoice = null!;
    private readonly List<InventoryId?> _localOptions = [];

    internal SettlementView? Settlement => _settlement;
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<InstanceId>? ServiceDesignRequested;
    internal event Action<SettlementId>? SettlementCombatRequested;
    internal event Action<WorldObjectId, Control>? Opened;

    internal SettlementPanel(Control parent, HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> submitTarget,
        GoodsRows goods, Action<HostBinding, ICoreReadView, IContentView> refresh, Action invalidateTrainInventory)
    {
        _parent = parent; _host = host; _submit = submit; _refresh = refresh; _invalidateTrainInventory = invalidateTrainInventory;
        _stock = new(goods);
        _market = new(() => CanAct, goods, Quote, Submit);
        _vehicles = new(() => CanAct, Quote, Submit, (target, node, view, content) => FormationRequested?.Invoke(target, node, view, content));
        _leases = new(() => CanAct, Quote, Submit);
        _production = new(() => CanAct, goods, submit, RefreshAfterSubmission);
        _population = new(); _plots = new();
        _services = new(host, () => CanAct, ReadCurrent, submit, submitTarget, RefreshAfterSubmission);
        _services.FormationRequested += (target, node, view, content) => FormationRequested?.Invoke(target, node, view, content);
        _services.ServiceDesignRequested += instance => ServiceDesignRequested?.Invoke(instance);
    }

    internal void Open(PanelTarget source, SettlementId id)
    {
        if (!ReadCurrent() || !_host.PanelGenerationCurrent(source.Binding, source.Generation)) return;
        _parent.Show();
        RemoveWindow(_panel);
        _target = new(_binding, _content.Generation, new WorldObjectId.Settlement(id));
        _settlementId = id; _settlement = null; _revision = null; _localId = null; _localOptions.Clear();
        _neutral = _view.FindSettlement(id) is Lookup<SettlementView>.Found { Value.Relation: SettlementRelation.NonHostile };
        BindPages();
        var title = _neutral ? _content.FindSettlementLayout(id) is ContentLookup<SettlementLayoutProjection>.Found layout
            ? layout.Value.Display.Name : "非敌对聚居地" : $"聚居地 {id.Value}";
        _panel = UiStyle.Panel(_parent, "Settlement", title, new(408, 68), 720,
            () => _panel!.Hide(), out _body, fill: true);
        _state = Text(_body, "State", ""); _wallet = Text(_body, "Wallet", "");
        if (!_neutral)
        {
            _combat = UiStyle.Button("OpenSettlementCombat", "战斗…", () =>
            {
                if (CanAct) SettlementCombatRequested?.Invoke(_settlementId);
            });
            _body.AddChild(_combat);
            _localChoice = new OptionButton { Name = "LocalInventory" };
            _body.AddChild(_localChoice);
            _localChoice.ItemSelected += index =>
            {
                _localId = _localOptions[(int)index]; BindPages();
                _stock.Render(Local, _content, _settlement); _market.ClearTradeQuote(); _market.RenderMarket();
            };
        }
        var tabs = new TabContainer { Name = "Pages", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 160) };
        _body.AddChild(tabs);
        _overview = null;
        if (_neutral)
        {
            _overview = new(title => tabs.CurrentTab = Enumerable.Range(0, tabs.GetTabCount()).Single(index => tabs.GetTabTitle(index) == title));
            _overview.Build(Page(tabs, "概况"));
        }
        else _stock.Build(Page(tabs, "库存"));
        _market.BuildTrade(Page(tabs, "市场"));
        _vehicles.BuildVehicles(Page(tabs, "现车"));
        _leases.BuildLeases(Page(tabs, "仓库租用"));
        if (!_neutral)
        {
            _production.BuildProduction(Page(tabs, "生产")); _population.BuildPopulation(Page(tabs, "人口"));
            _plots.BuildPlots(Page(tabs, "地块与建筑"));
        }
        _services.BuildServices(Page(tabs, "建造与工地"));
        tabs.TabChanged += _ => _services.RefreshServices();
        _refresh(_binding, _view, _content);
        Opened?.Invoke(new WorldObjectId.Settlement(id), _panel);
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        Bind(binding, view, content);
        if (_panel?.Visible != true) return;
        if (_target is not { } target || !Current(_host, binding, target.Binding, target.Generation)) Invalidate(_body, _state);
        else
        {
            if (_revision != view.Revision) { _revision = view.Revision; ReadSettlement(); }
            if (_settlement is not null) UpdateActions();
            if (_settlement is not null) _services.RefreshServices();
        }
    }

    private void Bind(HostBinding binding, ICoreReadView view, IContentView content)
    { _binding = binding; _view = view; _content = content; BindPages(); }

    private void BindPages()
    {
        _market.Bind(_view, _content, _settlement!, Local, _target!);
        _vehicles.Bind(_binding, _view, _content, _settlement!, _target!);
        _leases.Bind(_view, _content, _settlement!, _locals, _target!);
        _production.Bind(_view, _content, _settlement!, _target!);
        _population.Bind(_content, _settlement!); _plots.Bind(_view, _content, _settlement!);
        _services.Bind(_binding, _view, _content, _target, _settlementId, _settlement, _locals);
    }

    private void ReadSettlement()
    {
        if (_view.FindSettlement(_settlementId) is not Lookup<SettlementView>.Found found)
        { _settlement = null; BindPages(); Invalidate(_body, _state); return; }
        _settlement = found.Value;
        _wallet.Text = $"资金：{_view.Economy.Wallet.Value}";
        _locals.Clear();
        AddInventory(_settlement.Inventory, null);
        foreach (var leaseId in _settlement.Leases)
        {
            if (_view.FindLease(leaseId) is Lookup<LeaseView>.Found lease) AddInventory(lease.Value.Inventory, lease.Value);
        }
        _localId ??= _settlement.Inventory;
        BindPages();
        if (!_neutral) EconomyPanels.SyncChoices(_localChoice, _localOptions, _locals.Select(item => item.Inventory.Id), _localId,
            id => _locals.First(item => item.Inventory.Id == id).Lease is { } lease
                ? $"租用库存 {id.Value} · 租约 {lease.Id.Value}" : $"聚居地库存 {id.Value}");
        if (!_neutral)
        {
            _stock.Render(Local, _content, _settlement);
            _production.RefreshProduction(); _population.RefreshPopulation(); _plots.RefreshPlots();
        }
        _overview?.Refresh(_view, _content, _settlement, _locals);
        _market.RefreshTrade(); _vehicles.RefreshVehicles(); _leases.RefreshLeases();
    }

    private void AddInventory(InventoryId id, LeaseView? lease)
    {
        if (_view.FindInventory(id) is Lookup<InventoryView>.Found found) _locals.Add((found.Value, lease));
    }

    private InventoryView? Local => _locals.FirstOrDefault(item => item.Inventory.Id == _localId).Inventory;
    private bool CanAct => _target is { } target && Current(_host, _binding, target.Binding, target.Generation) &&
        _settlement is not null && !_host.IsBusy && _view.Fault is null;

    private void UpdateActions()
    {
        if (!_neutral) _localChoice.Disabled = _combat.Disabled = !CanAct;
        _market.UpdateTradeActions(); _vehicles.UpdateVehicleActions(); _leases.UpdateLeaseActions();
        _state.Text = _view.Fault is { } fault ? CommandDiagnostics.Describe(fault) : _host.IsBusy ? "当前操作尚未结束" : "";
    }

    private void Submit(PanelTarget target, CoreCommand command, Action requote)
    {
        if (!CanAct) return;
        _submit(target, command);
        if (!RefreshAfterSubmission(_invalidateTrainInventory)) return;
        Quote(requote);
    }

    private bool RefreshAfterSubmission(Action? beforeRefresh)
    {
        if (!ReadCurrent()) return false;
        beforeRefresh?.Invoke();
        _revision = null;
        _refresh(_binding, _view, _content);
        return true;
    }

    private bool ReadCurrent()
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content) return false;
        Bind(status.Binding, status.View, content); return true;
    }

    private void Quote(Action request)
    {
        if (!CanAct) return;
        request(); _refresh(_binding, _view, _content);
    }

    internal void Observe(HostBinding binding, ICoreReadView view, ImmutableArray<EventEnvelope> events)
        => _services.Observe(binding, view, events);

    internal void OpenVehicleServices(InstanceId instance)
    {
        if (instance is not InstanceId.Vehicle vehicle || !ReadCurrent() ||
            _view.FindInstance(instance) is not Lookup<InstanceView>.Found { Value.Location: WorldLocation.Node node } ||
            _view.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is not NodeOccupant.Settlement settlement) return;
        var reused = IsCurrent(settlement.Id);
        if (reused) { _parent.Show(); _panel!.Show(); UiStyle.BringToFront(_panel); }
        else Open(new(_binding, _content.Generation, new WorldObjectId.Settlement(settlement.Id)), settlement.Id);
        _services.SelectVehicle(vehicle);
        var tabs = _body.GetNode<TabContainer>("Pages");
        tabs.CurrentTab = Enumerable.Range(0, tabs.GetTabCount()).Single(index => tabs.GetTabTitle(index) == "建造与工地");
        _services.ServiceChanged();
        if (reused) Opened?.Invoke(new WorldObjectId.Settlement(settlement.Id), _panel!);
    }

    private bool IsCurrent(SettlementId requested) => _panel is not null && GodotObject.IsInstanceValid(_panel) &&
        _settlementId == requested && _target is { } target && Current(_host, _binding, target.Binding, target.Generation);

    internal void ReleaseResources() => _services.CloseServices();

    internal void Close()
    {
        ReleaseResources();
        RemoveWindow(_panel); _panel = null;
        _target = null; _settlement = null; _locals.Clear(); _localOptions.Clear();
        _stock.Close(); _market.Close(); _vehicles.Close(); _leases.Close();
        _production.Close(); _population.Close(); _plots.Close();
        _view = null!; _content = null!;
        BindPages();
    }
}

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
    private readonly SettlementVehiclesPage _vehicles;
    private readonly SettlementLeasesPage _leases;
    private readonly SettlementServicesPage _services;
    private readonly GoodsRows _goods;
    private readonly Func<PanelTarget?, CoreCommand, SubmissionResult> _submitInline;
    private SettlementMarketTable? _marketTable;
    private SettlementOverviewPage? _overview;
    private SettlementBuildingsPage? _buildings;
    private bool _neutral, _owned;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private PanelTarget? _target;
    private SettlementId _settlementId;
    private SettlementView? _settlement;
    private CoreRevision? _revision;
    private readonly List<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private PanelContainer? _panel;
    private VBoxContainer _body = null!;
    private Label _state = null!;
    private Label _wallet = null!;
    private Button _combat = null!;

    internal SettlementView? Settlement => _settlement;
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<InstanceId>? ServiceDesignRequested;
    internal event Action<SettlementId>? SettlementCombatRequested;
    internal event Action<WorldObjectId, Control>? Opened;

    internal SettlementPanel(Control parent, HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> submitTarget,
        GoodsRows goods, Action<HostBinding, ICoreReadView, IContentView> refresh, Action invalidateTrainInventory,
        Func<PanelTarget?, CoreCommand, SubmissionResult> submitInline)
    {
        _parent = parent; _host = host; _submit = submit; _refresh = refresh; _invalidateTrainInventory = invalidateTrainInventory;
        _goods = goods; _submitInline = submitInline;
        _vehicles = new(() => CanAct, Quote, Submit, (target, node, view, content) => FormationRequested?.Invoke(target, node, view, content));
        _leases = new(() => CanAct, Quote, Submit);
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
        _settlementId = id; _settlement = null; _revision = null;
        var relation = (_view.FindSettlement(id) as Lookup<SettlementView>.Found)?.Value.Relation;
        _neutral = relation == SettlementRelation.NonHostile; _owned = relation == SettlementRelation.PlayerOwned;
        BindPages();
        var title = _content.FindSettlementLayout(id) is ContentLookup<SettlementLayoutProjection>.Found layout
            ? layout.Value.Display.Name : _owned ? "自有聚居地" : _neutral ? "非敌对聚居地" : "敌对聚居地";
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
        }
        var tabs = new TabContainer { Name = "Pages", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 160) };
        _body.AddChild(tabs);
        _overview = null; _buildings = null; _marketTable = null;
        void OpenPage(string title) => tabs.CurrentTab = Enumerable.Range(0, tabs.GetTabCount()).Single(index => tabs.GetTabTitle(index) == title);
        if (_neutral || _owned)
        {
            _overview = new(OpenPage, () => CanAct, command => SubmitInline(_target!, command),
                good => { OpenPage("库存"); _marketTable!.OpenGood(good); },
                building => { OpenPage("建筑"); _buildings!.OpenBuilding(building); });
            _overview.Build(Page(tabs, "概况"), _owned);
            _marketTable = new(() => CanAct, _goods, SubmitInline);
            _marketTable.Build(Page(tabs, _owned ? "库存" : "市场"));
        }
        if (_owned)
        {
            _buildings = new(() => CanAct, command => SubmitInline(_target!, command));
            _buildings.Build(Page(tabs, "建筑"));
        }
        _vehicles.BuildVehicles(Page(tabs, "现车"));
        if (!_owned) _leases.BuildLeases(Page(tabs, "仓库租用"));
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
        _marketTable?.Bind(_view, _content, _settlement!, _locals, _target!);
        _vehicles.Bind(_binding, _view, _content, _settlement!, _target!);
        _leases.Bind(_view, _content, _settlement!, _locals, _target!);
        _services.Bind(_binding, _view, _content, _target, _settlementId, _settlement, _locals);
    }

    private void ReadSettlement()
    {
        if (_view.FindSettlement(_settlementId) is not Lookup<SettlementView>.Found found)
        { _settlement = null; BindPages(); Invalidate(_body, _state); return; }
        if (_neutral != (found.Value.Relation == SettlementRelation.NonHostile) || _owned != (found.Value.Relation == SettlementRelation.PlayerOwned))
        { _services.CloseServices(); Open(_target!, _settlementId); return; }
        _settlement = found.Value;
        _wallet.Text = $"资金：{_view.Economy.Wallet.Value}";
        _locals.Clear();
        AddInventory(_settlement.Inventory, null);
        foreach (var leaseId in _settlement.Leases)
        {
            if (_view.FindLease(leaseId) is Lookup<LeaseView>.Found lease) AddInventory(lease.Value.Inventory, lease.Value);
        }
        BindPages();
        _overview?.Refresh(_view, _content, _settlement, _locals);
        _marketTable?.Refresh(); _buildings?.Refresh(_view, _content, _settlement);
        _vehicles.RefreshVehicles(); if (!_owned) _leases.RefreshLeases();
    }

    private void AddInventory(InventoryId id, LeaseView? lease)
    {
        if (_view.FindInventory(id) is Lookup<InventoryView>.Found found) _locals.Add((found.Value, lease));
    }

    private bool CanAct => _target is { } target && Current(_host, _binding, target.Binding, target.Generation) &&
        _settlement is not null && !_host.IsBusy && _view.Fault is null;

    private void UpdateActions()
    {
        if (!_neutral) _combat.Disabled = !CanAct;
        _overview?.UpdateActions();
        _marketTable?.UpdateActions(); _buildings?.UpdateActions();
        _vehicles.UpdateVehicleActions(); if (!_owned) _leases.UpdateLeaseActions();
        _state.Text = _view.Fault is { } fault ? CommandDiagnostics.Describe(fault) : _host.IsBusy ? "当前操作尚未结束" : "";
    }

    private void Submit(PanelTarget target, CoreCommand command, Action requote)
    {
        if (!CanAct) return;
        _submit(target, command);
        if (!RefreshAfterSubmission(_invalidateTrainInventory)) return;
        Quote(requote);
    }

    private SubmissionResult SubmitInline(PanelTarget target, CoreCommand command)
    {
        var result = _submitInline(target, command);
        RefreshAfterSubmission(_invalidateTrainInventory);
        return result;
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
        _marketTable = null; _overview = null; _buildings = null;
        _target = null; _settlement = null; _locals.Clear();
        _vehicles.Close(); _leases.Close();
        _view = null!; _content = null!;
        BindPages();
    }
}

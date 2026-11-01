using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal partial class EconomyPanels : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private PanelTarget? _settlementTarget;
    private SettlementId _settlementId;
    private SettlementView? _settlement;
    private CoreRevision? _settlementRevision;
    private InventoryId? _localId;
    private readonly List<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private SettlementPanel _settlementWindow = null!;
    private TrainInventoryPanel _trainInventory = null!;
    private EconomyReportsPanel _reports = null!;
    private SettlementStockPage _stock = null!;
    private SettlementMarketPage _market = null!;
    private SettlementVehiclesPage _vehicles = null!;
    private SettlementLeasesPage _leases = null!;
    private SettlementProductionPage _production = null!;
    private SettlementPopulationPage _population = null!;
    private SettlementPlotsPage _plots = null!;
    private SettlementServicesPage _services = null!;

    internal const string ReservedStockHint = "聚居地库存在补货目标以内的部分留给本地，自动装载和买入只取超出部分。";
    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;
    internal event Action<InstanceId>? ServiceDesignRequested;

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, GoodsIconCache icons,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> submitTarget)
    {
        _host = host; _submit = submit;
        var goods = new GoodsRows(icons);
        _stock = new(goods);
        _market = new(() => CanAct, goods, Quote, Submit);
        _vehicles = new(() => CanAct, Quote, Submit, (target, node, view, content) => FormationRequested?.Invoke(target, node, view, content));
        _leases = new(() => CanAct, Quote, Submit);
        _production = new(() => CanAct, goods, SubmitProductionPlan);
        _population = new(); _plots = new();
        _services = new(host, ReadCurrent, submit, submitTarget, RefreshSettlement);
        _services.FormationRequested += (target, node, view, content) => FormationRequested?.Invoke(target, node, view, content);
        _services.ServiceDesignRequested += instance => ServiceDesignRequested?.Invoke(instance);
        _settlementWindow = new(_stock, _market, _vehicles, _leases, _production, _population, _plots, _services,
            id => { _localId = id; BindPages(); return Local; });
        _trainInventory = new(host, goods); _reports = new(host);
    }

    public override void _Ready()
    { SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore; }

    internal void OpenSettlement(PanelTarget source, SettlementId id)
    {
        if (!ReadCurrent() || !_host.PanelGenerationCurrent(source.Binding, source.Generation)) return;
        Show();
        _settlementWindow.RemoveWindow();
        _settlementTarget = new(_binding, _content.Generation, new WorldObjectId.Settlement(id));
        _settlementId = id; _settlement = null; _settlementRevision = null; _localId = null;
        BindPages();
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, "Settlement", $"聚居地 {id.Value}", new(408, 68), 720,
            () => panel.Hide(), out var body, fill: true);
        _settlementWindow.Open(panel, body);
        Refresh(_binding, _view, _content);
    }

    internal void OpenTrainInventory(PanelTarget target, TrainId id)
    {
        if (!ReadCurrent() || !_host.PanelGenerationCurrent(target.Binding, target.Generation)) return;
        _trainInventory.Close();
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, "TrainInventory", $"列车 {id.Value} 库存", new(1160, 70), 420,
            () => panel.Hide(), out var body);
        _trainInventory.Open(panel, body, target);
        Refresh(_binding, _view, _content);
    }

    internal void OpenReports()
    {
        if (!ReadCurrent()) return;
        _reports.Close();
        PanelContainer panel = null!;
        panel = UiStyle.Panel(this, "EconomyReports", "经营报告", new(420, 70), 720,
            () => panel.Hide(), out var body);
        _reports.Open(panel, body, _binding, _content.Generation);
        _reports.Refresh(_binding, _view, _content);
    }

    internal void Close()
    {
        _services.CloseServices();
        _settlementWindow.Close(); _trainInventory.Close(); _reports.Close();
        _settlementTarget = null; _settlement = null; _locals.Clear();
        _market.Close(); _vehicles.Close(); _leases.Close(); _production.Close();
        _view = null!; _content = null!;
        BindPages();
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        _binding = binding; _view = view; _content = content;
        BindPages();
        _reports.Refresh(binding, view, content);
        if (_settlementWindow.Visible)
        {
            if (!Current(_settlementTarget)) _settlementWindow.Invalidate();
            else
            {
                if (_settlementRevision != view.Revision)
                { _settlementRevision = view.Revision; ReadSettlement(); }
                if (_settlement is not null) _settlementWindow.UpdateActions(view, _host.IsBusy, CanAct);
                if (_settlement is not null) _services.RefreshServices();
            }
        }
        _trainInventory.Refresh(binding, view, content, _settlement);
    }

    private void ReadSettlement()
    {
        if (_view.FindSettlement(_settlementId) is not Lookup<SettlementView>.Found found)
        { _settlement = null; BindPages(); _settlementWindow.Invalidate(); return; }
        _settlement = found.Value;
        _locals.Clear();
        AddInventory(_settlement.Inventory, null);
        foreach (var leaseId in _settlement.Leases)
        {
            if (_view.FindLease(leaseId) is Lookup<LeaseView>.Found lease) AddInventory(lease.Value.Inventory, lease.Value);
        }
        _localId ??= _settlement.Inventory;
        BindPages();
        _settlementWindow.ReadSettlement(_view, _content, _settlement, _locals, _localId, Local);
    }

    private void AddInventory(InventoryId id, LeaseView? lease)
    {
        if (_view.FindInventory(id) is Lookup<InventoryView>.Found found) _locals.Add((found.Value, lease));
    }

    private InventoryView? Local => _locals.FirstOrDefault(local => local.Inventory.Id == _localId).Inventory;
    private bool CanAct => Current(_settlementTarget) && _settlement is not null &&
        !_host.IsBusy && _view.Fault is null;

    private void BindPages()
    {
        _market.Bind(_view, _content, _settlement!, Local, _settlementTarget!);
        _vehicles.Bind(_binding, _view, _content, _settlement!, _settlementTarget!);
        _leases.Bind(_view, _content, _settlement!, _locals, _settlementTarget!);
        _production.Bind(_view, _content, _settlement!, _settlementTarget!);
        _population.Bind(_content, _settlement!); _plots.Bind(_view, _content, _settlement!);
        _services.Bind(_binding, _view, _content, _settlementTarget, _settlementId, _settlement, _locals);
        _settlementWindow.Bind(_content, _settlement);
    }

    private void Submit(PanelTarget target, CoreCommand command, Action requote)
    {
        if (!CanAct) return;
        _submit(target, command);
        if (!ReadCurrent()) return;
        _settlementRevision = null; _trainInventory.InvalidateRevision();
        Refresh(_binding, _view, _content);
        Quote(requote);
    }

    private void SubmitProductionPlan(PanelTarget? target, CoreCommand command, Action clearEdits)
    {
        var accepted = _submit(target, command);
        if (accepted is not null && ReadCurrent())
        {
            clearEdits();
            RefreshSettlement();
        }
    }

    private void RefreshSettlement()
    { _settlementRevision = null; Refresh(_binding, _view, _content); }

    private bool ReadCurrent()
    {
        if (_host.GetStatus() is not { } status || _host.Content is not { } content) return false;
        _binding = status.Binding; _view = status.View; _content = content; BindPages(); return true;
    }

    private bool Current(PanelTarget? target) => target is not null && target.Binding == _binding &&
        _host.PanelGenerationCurrent(target.Binding, target.Generation);

    private void Quote(Action request)
    {
        if (!CanAct) return;
        request(); Refresh(_binding, _view, _content);
    }

    internal void Observe(HostBinding binding, ICoreReadView view, ImmutableArray<EventEnvelope> events)
        => _services.Observe(binding, view, events);

    internal void OpenVehicleServices(InstanceId instance)
    {
        if (instance is not InstanceId.Vehicle vehicle || !ReadCurrent() ||
            _view.FindInstance(instance) is not Lookup<InstanceView>.Found { Value.Location: WorldLocation.Node node } ||
            _view.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is not NodeOccupant.Settlement settlement) return;
        if (_settlementWindow.IsCurrent(_settlementId, settlement.Id) &&
            _settlementTarget is { } target && _host.PanelGenerationCurrent(target.Binding, target.Generation))
        { Show(); _settlementWindow.Show(); }
        else OpenSettlement(new(_binding, _content.Generation, new WorldObjectId.Settlement(settlement.Id)), settlement.Id);
        _services.SelectVehicle(vehicle);
        _settlementWindow.SelectServices();
        _services.ServiceChanged();
    }

    public override void _ExitTree() => _services.CloseServices();

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

    internal static string SizeName(SizeClass size) => size switch
    { SizeClass.Small => "小型", SizeClass.Medium => "中型", SizeClass.Large => "大型", SizeClass.ExtraLarge => "超大型", SizeClass.Titan => "泰坦", _ => size.ToString() };

}

using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal partial class SettlementServicesPage
{
    private readonly HostCoordinator _host;
    private readonly Func<bool> _canAct;
    private readonly Func<bool> _readCurrent;
    private readonly Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit;
    private readonly Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> _submitTarget;
    private readonly Func<Action?, bool> _refreshAfterSubmission;
    private HostBinding _binding;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private PanelTarget? _settlementTarget;
    private SettlementId _settlementId;
    private SettlementView? _settlement;
    private IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> _locals = [];

    internal event Action<PanelTarget, NodeId, ICoreReadView, IContentView>? FormationRequested;

    internal SettlementServicesPage(HostCoordinator host, Func<bool> canAct, Func<bool> readCurrent,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit,
        Func<PanelTarget?, CoreCommand, IContentCandidate?, CommandDecision.Accepted?> submitTarget,
        Func<Action?, bool> refreshAfterSubmission)
    {
        _host = host; _canAct = canAct; _readCurrent = readCurrent; _submit = submit;
        _submitTarget = submitTarget; _refreshAfterSubmission = refreshAfterSubmission;
    }

    internal void Bind(HostBinding binding, ICoreReadView view, IContentView content,
        PanelTarget? target, SettlementId id, SettlementView? settlement,
        IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> locals)
    {
        _binding = binding; _view = view; _content = content; _settlementTarget = target;
        _settlementId = id; _settlement = settlement; _locals = locals;
    }

    private VBoxContainer _services = null!;
    private OptionButton _serviceOperation = null!, _serviceFacility = null!, _serviceBlueprint = null!, _serviceVehicle = null!, _serviceSource = null!;
    private readonly List<FacilityKey> _serviceFacilities = [];
    private readonly List<BlueprintAddress> _serviceBlueprints = [];
    private readonly List<WorkTarget?> _serviceTargets = [];
    private readonly record struct WorkTarget(InstanceId Instance);
    private OptionButton _servicePlot = null!;
    private readonly List<PlotId?> _servicePlots = [];
    private PlotId? _serviceSelectedPlot;
    private readonly List<InventoryId?> _serviceSourceOptions = [];
    private readonly List<InventoryId> _serviceSources = [];
    private ItemList _serviceSourceOrder = null!;
    private FacilityKey? _serviceSelectedFacility;
    private BlueprintAddress? _serviceSelectedBlueprint;
    private WorkTarget? _serviceSelectedTarget;
    private IContentCandidate? _serviceCandidate;
    private DesignInput? _serviceDesign;
    private string? _serviceDesignProblem;
    private ICoreReadView? _serviceShownView;
    private IContentView? _serviceShownContent;
    private JobQuote? _serviceQuote;
    private Label _serviceDescription = null!, _serviceResult = null!;
    private Button _serviceConfirm = null!;
    private VBoxContainer _serviceJobs = null!;
    private readonly Dictionary<JobId, Label> _serviceJobRows = [];
    private CoreEvent.ConstructionCompleted? _serviceCompletion;
    private HostBinding? _serviceObservedBinding;
    private bool _serviceDirty;

    internal void BuildServices(VBoxContainer body)
    {
        CloseServices();
        _services = body; _serviceFacilities.Clear(); _serviceBlueprints.Clear(); _serviceTargets.Clear(); _servicePlots.Clear();
        _serviceSourceOptions.Clear(); _serviceSources.Clear(); _serviceJobRows.Clear(); _servicePauseButtons.Clear();
        _serviceSelectedFacility = null; _serviceSelectedBlueprint = null; _serviceSelectedTarget = null; _serviceSelectedPlot = null;
        _serviceOperation = new() { Name = "ServiceOperation" };
        var ownedSettlement = _view.FindSettlement(_settlementId) is Lookup<SettlementView>.Found { Value.Relation: SettlementRelation.PlayerOwned };
        _serviceOperation.AddItem(ownedSettlement ? "制造" : "制造车辆");
        _serviceOperation.AddItem(ownedSettlement ? "修理" : "修理车辆");
        _serviceOperation.AddItem(ownedSettlement ? "改装" : "改装车辆");
        if (ownedSettlement) _serviceOperation.AddItem("拆解");
        body.AddChild(_serviceOperation);
        _serviceFacility = ServiceChoice(body, "ServiceFacility");
        _serviceBlueprint = ServiceChoice(body, "ServiceBlueprint");
        _serviceVehicle = ServiceChoice(body, "ServiceVehicle");
        _serviceOperation.ItemSelected += _ => { ClearServiceDesign(); ServiceChanged(); };
        _serviceFacility.ItemSelected += index => { _serviceSelectedFacility = index < 0 ? null : _serviceFacilities[(int)index]; ServiceChanged(); };
        _serviceBlueprint.ItemSelected += index =>
        {
            _serviceSelectedBlueprint = index < 0 ? null : _serviceBlueprints[(int)index];
            ClearServiceDesign(); ServiceChanged();
        };
        _serviceVehicle.ItemSelected += index => { _serviceSelectedTarget = _serviceTargets[(int)index]; ClearServiceDesign(); ServiceChanged(); };
        _servicePlot = ServiceChoice(body, "ServicePlot");
        _servicePlot.ItemSelected += index =>
        {
            _serviceSelectedPlot = index < 0 || index >= _servicePlots.Count ? null : _servicePlots[(int)index];
            ServiceChanged();
        };
        BuildServiceTargets(body);
        Text(body, "SourceInstructions", "材料来源按下列顺序扣取；委托不足部分由 NPC 补购。来源需明确添加，可留空。");
        _serviceSource = ServiceChoice(body, "ServiceSource");
        var sourceActions = new HFlowContainer(); body.AddChild(sourceActions);
        sourceActions.AddChild(UiStyle.Button("AddServiceSource", "添加来源", () =>
        {
            if (_serviceSource.Selected >= 0 && _serviceSourceOptions[_serviceSource.Selected] is { } id && !_serviceSources.Contains(id))
            { _serviceSources.Add(id); ServiceChanged(); }
        }));
        sourceActions.AddChild(UiStyle.Button("MoveServiceSourceUp", "上移", () => MoveServiceSource(-1)));
        sourceActions.AddChild(UiStyle.Button("MoveServiceSourceDown", "下移", () => MoveServiceSource(1)));
        sourceActions.AddChild(UiStyle.Button("RemoveServiceSource", "移除来源", () =>
        {
            if (_serviceSourceOrder.GetSelectedItems().FirstOrDefault(-1) is var index && index >= 0)
            { _serviceSources.RemoveAt(index); ServiceChanged(); }
        }));
        _serviceSourceOrder = new() { Name = "ServiceSourceOrder", CustomMinimumSize = new(0, 100) }; body.AddChild(_serviceSourceOrder);
        _serviceDescription = Text(body, "ServiceQuote", "请选择执行建筑和目标。");
        var footer = Footer(body);
        _serviceConfirm = UiStyle.Button("ConfirmService", "确认服务", ConfirmService, true); footer.AddChild(_serviceConfirm);
        footer.AddChild(UiStyle.Button("RefreshServiceQuote", "重新获取报价", () =>
        {
            if (!_readCurrent() || !_canAct()) return;
            _serviceDirty = true; RefreshServices();
            PrepareServiceDesign(); RequestServiceQuote();
        }));
        _serviceResult = Text(body, "ServiceResult", "完工以核心事件为准；任务从队列消失不代表完工。");
        body.AddChild(UiStyle.Button("FormServiceVehicle", "编组…", () =>
        {
            if (_canAct()) FormationRequested?.Invoke(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement!.Node)), _settlement.Node, _view, _content);
        }));
        Text(body, "ServiceJobsTitle", "施工任务 · NPC 委托与自有任务");
        _serviceJobs = new() { Name = "ServiceJobs" }; body.AddChild(_serviceJobs);
        _serviceDirty = true;
    }

    private static OptionButton ServiceChoice(Node parent, string name)
    { var choice = new OptionButton { Name = name }; parent.AddChild(choice); return choice; }

    internal void CloseServices()
    {
        ClearServiceDesign();
        _serviceShownView = null; _serviceShownContent = null; _serviceQuote = null;
        _serviceCompletion = null; _services = null!;
    }

    internal void ServiceChanged()
    {
        ResetQuote(ref _serviceQuote, _serviceDescription);
        _serviceConfirm.Disabled = true;
        _serviceDirty = true; RefreshServices();
    }

    private void MoveServiceSource(int offset)
    {
        var index = _serviceSourceOrder.GetSelectedItems().FirstOrDefault(-1);
        var next = index + offset;
        if (index < 0 || next < 0 || next >= _serviceSources.Count) return;
        (_serviceSources[index], _serviceSources[next]) = (_serviceSources[next], _serviceSources[index]);
        ServiceChanged(); _serviceSourceOrder.Select(next);
    }

    internal void RefreshServices()
    {
        if (_services is null || !_services.IsVisibleInTree() || _settlement is null || _settlementTarget is null ||
            !Current(_host, _binding, _settlementTarget.Binding, _settlementTarget.Generation)) return;
        _serviceConfirm.Disabled = !_canAct() || _serviceQuote is not { Blockers.IsEmpty: true } ||
            (_settlement.Relation != SettlementRelation.PlayerOwned && _serviceQuote.TotalPayment is null);
        if (_host.IsBusy) return;
        if (!_serviceDirty && ReferenceEquals(_serviceShownView, _view) && ReferenceEquals(_serviceShownContent, _content)) return;
        var refreshTargets = _serviceDirty || !ReferenceEquals(_serviceShownContent, _content);
        _serviceShownView = _view; _serviceShownContent = _content; _serviceDirty = false;
        var facilities = _settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
            .Where(building => IsServiceBuilding(building.Id))
            .Select(building => new FacilityKey(building.Id, CapabilityKind.Construction)).ToArray();
        SyncServiceChoice(_serviceFacility, _serviceFacilities, facilities, _serviceSelectedFacility,
            ServiceFacilityTitle, "请选择执行建筑");
        var playerOwned = _settlement.Relation == SettlementRelation.PlayerOwned;
        if (refreshTargets)
        {
            var blueprints = _content.Blueprints.Where(item => item.Design is not null)
                .Select(item => (Summary: item, Found: _content.FindBlueprint(item.Address)))
                .Where(item => item.Found is ContentLookup<BlueprintRecord>.Found)
                .Select(item => (item.Summary, Record: ((ContentLookup<BlueprintRecord>.Found)item.Found).Value))
                .Where(item => item.Record.Definition.Specification is DesignSpecification.Vehicle
                    || item.Record.Definition.Specification is DesignSpecification.Building && playerOwned)
                .OrderBy(item => item.Record.Definition.Specification is DesignSpecification.Building)
                .Select(item => item.Summary).ToArray();
            SyncServiceChoice(_serviceBlueprint, _serviceBlueprints, blueprints.Select(item => item.Address), _serviceSelectedBlueprint,
                id => $"{ServiceLibrary(id.Library)} · {blueprints.First(item => item.Address == id).Display.Name}", "请选择制造蓝图");
        }
        var trains = _view.World.Trains.Select(item => _view.FindTrain(item.Id)).OfType<Lookup<TrainView>.Found>()
            .Select(item => item.Value).Where(train => train.Position is TrainPosition.Node node && node.Id == _settlement.Node).ToArray();
        var vehicles = _settlement.ParkedVehicles.Concat(trains.Where(train => train.Activity == TrainActivity.Stopped && train.SpeedMetersPerSecond == 0)
            .SelectMany(train => train.Vehicles)).Distinct().Where(id => _view.FindInstance(new InstanceId.Vehicle(id)) is
                Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned }).ToArray();
        IEnumerable<WorkTarget> targets = vehicles.Select(id => new WorkTarget(new InstanceId.Vehicle(id)));
        if (playerOwned)
            targets = targets.Concat(_settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
                .Select(building => new WorkTarget(new InstanceId.Building(building.Id))));
        SyncChoices(_serviceVehicle, _serviceTargets, targets, _serviceSelectedTarget, ServiceTargetTitle,
            playerOwned ? "请选择车辆或建筑" : "请选择车辆");
        if (refreshTargets) RefreshServiceTargets();
        var sources = trains.Select(train => train.Inventory).Concat(_locals.Where(local => local.Lease is not null || _settlement.Relation == SettlementRelation.PlayerOwned)
            .Select(local => local.Inventory.Id)).Distinct().ToArray();
        var selectedSource = _serviceSource.Selected >= 0 ? _serviceSourceOptions[_serviceSource.Selected] : null;
        SyncChoices(_serviceSource, _serviceSourceOptions, sources, selectedSource, ServiceSourceTitle, "请选择材料来源");
        var sourceSelection = _serviceSourceOrder.GetSelectedItems().FirstOrDefault(-1);
        for (var index = _serviceSourceOrder.ItemCount - 1; index >= _serviceSources.Count; index--) _serviceSourceOrder.RemoveItem(index);
        for (var index = 0; index < _serviceSources.Count; index++)
        {
            var id = _serviceSources[index];
            var text = $"{index + 1}. {ServiceSourceTitle(id)}" + (sources.Contains(id) ? "" : " · 已不存在：所选来源或已不可访问");
            if (index == _serviceSourceOrder.ItemCount) _serviceSourceOrder.AddItem(text);
            else if (_serviceSourceOrder.GetItemText(index) != text) _serviceSourceOrder.SetItemText(index, text);
        }
        if (sourceSelection >= 0 && sourceSelection < _serviceSources.Count) _serviceSourceOrder.Select(sourceSelection);
        UpdateServicePlot();
        RefreshServiceJobs();
        ValidateServiceSelection();
    }

    private static void SyncServiceChoice<T>(OptionButton choice, List<T> options, IEnumerable<T> values,
        T? selected, Func<T, string> title, string placeholder) where T : class
    {
        var current = values.Distinct().ToArray(); var next = current.ToList();
        if (selected is not null && !next.Contains(selected)) next.Add(selected);
        for (var index = options.Count - 1; index >= 0; index--)
            if (!next.Contains(options[index])) { choice.RemoveItem(index); options.RemoveAt(index); }
        foreach (var id in next) if (!options.Contains(id)) { options.Add(id); choice.AddItem(""); }
        for (var index = 0; index < options.Count; index++)
        {
            var text = current.Contains(options[index]) ? title(options[index]) : $"已不存在：{options[index]}";
            if (choice.GetItemText(index) != text) choice.SetItemText(index, text);
        }
        var selectedIndex = selected is null ? -1 : options.IndexOf(selected);
        if (choice.Selected != selectedIndex) choice.Select(selectedIndex);
        if (selected is null) choice.Text = placeholder;
    }

}

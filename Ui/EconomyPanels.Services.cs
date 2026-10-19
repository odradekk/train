using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
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

    private void BuildServices(VBoxContainer body)
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
            if (!ReadCurrent() || !CanAct) return;
            _serviceDirty = true; RefreshServices();
            PrepareServiceDesign(); RequestServiceQuote();
        }));
        _serviceResult = Text(body, "ServiceResult", "完工以核心事件为准；任务从队列消失不代表完工。");
        body.AddChild(UiStyle.Button("FormServiceVehicle", "编组…", () =>
        {
            if (CanAct) FormationRequested?.Invoke(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement!.Node)), _settlement.Node, _view, _content);
        }));
        Text(body, "ServiceJobsTitle", "施工任务 · NPC 委托与自有任务");
        _serviceJobs = new() { Name = "ServiceJobs" }; body.AddChild(_serviceJobs);
        _serviceDirty = true;
    }

    private static OptionButton ServiceChoice(Node parent, string name)
    { var choice = new OptionButton { Name = name }; parent.AddChild(choice); return choice; }

    public override void _ExitTree() => CloseServices();

    private void CloseServices()
    {
        ClearServiceDesign();
        _serviceShownView = null; _serviceShownContent = null; _serviceQuote = null;
        _serviceCompletion = null; _services = null!;
    }

    private void ServiceChanged()
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

    private void RefreshServices()
    {
        if (_services is null || !_services.IsVisibleInTree() || _settlement is null || !Current(_settlementTarget)) return;
        _serviceConfirm.Disabled = !CanAct || _serviceQuote is not { Blockers.IsEmpty: true } ||
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

    private void RequestServiceQuote()
    {
        _serviceQuote = null; _serviceConfirm.Disabled = true;
        if (_serviceOperation.Selected == 2 && _serviceDifference?.CanApplyImmediately == true)
        { _serviceDescription.Text = "构成未变，请使用单独的「立即应用」；不创建改装任务。"; return; }
        if (_serviceSelectedFacility is not { } facility) { _serviceDescription.Text = "请选择执行建筑；资格由核心报价判断。"; return; }
        if (!IsCurrentServiceBuilding(facility)) { _serviceDescription.Text = $"已不存在：执行建筑 {facility.Building.Value}"; return; }
        JobRequest request;
        if (_serviceOperation.Selected == 0)
        {
            if (_serviceSelectedBlueprint is not { } address) { _serviceDescription.Text = "请选择制造蓝图。"; return; }
            if (_content.FindBlueprint(address) is not ContentLookup<BlueprintRecord>.Found blueprint) { _serviceDescription.Text = $"已不存在：蓝图 {address}"; return; }
            var building = blueprint.Value.Definition.Specification is DesignSpecification.Building;
            if (building)
            {
                if (_settlement!.Relation != SettlementRelation.PlayerOwned) { _serviceDescription.Text = "建筑蓝图只在自有聚居地列出。"; return; }
                if (_serviceSelectedPlot is not { } plot) { _serviceDescription.Text = "请选择空地块。"; return; }
                if (!IsEmptyServicePlot(plot)) { _serviceDescription.Text = "已不存在：所选空地块。"; return; }
            }
            if (_serviceDesign is null || _serviceCandidate is null)
            { _serviceDescription.Text = _serviceDesignProblem ?? "目标设计尚未准备，请重新获取报价。"; return; }
            if (!ReferenceEquals(_serviceCandidate.Basis, _content))
            { _serviceDescription.Text = "目标蓝图的内容版本已失效，请重新获取报价。"; return; }
            request = building
                ? new JobRequest.BuildBuilding(facility, _serviceSelectedPlot!.Value, _serviceDesign, [.. _serviceSources], 0)
                : new JobRequest.BuildVehicle(facility, _serviceDesign, [.. _serviceSources], 0);
        }
        else
        {
            if (_serviceSelectedTarget is not { } target) { _serviceDescription.Text = _settlement!.Relation == SettlementRelation.PlayerOwned ? "请选择车辆或建筑。" : "请选择车辆。"; return; }
            if (!IsLocalServiceTarget(target)) { _serviceDescription.Text = target.Instance is InstanceId.Vehicle vehicle ? $"已不存在：所选本地玩家车辆 {vehicle.Id.Value}" : "已不存在：所选建筑。"; return; }
            if (_serviceOperation.Selected == 2 && (_serviceDifference is null || _serviceCandidate is null))
            { _serviceDescription.Text = _serviceDesignProblem ?? "请先选择已保存的目标并重新获取报价。"; return; }
            request = _serviceOperation.Selected switch
            {
                2 => new JobRequest.Refit(facility, target.Instance, _serviceDifference!.Core, [.. _serviceSources], 0),
                3 => new JobRequest.Dismantle(facility, target.Instance, 0),
                _ => new JobRequest.Repair(facility, target.Instance, [.. _serviceSources], 0)
            };
        }
        var funding = _settlement!.Relation == SettlementRelation.PlayerOwned ? JobFunding.OwnInventory : JobFunding.NpcCommission;
        _serviceQuote = ReadQuote(_view.GetJobQuote(request, funding), _serviceDescription);
        if (_serviceQuote is not { } quote) return;
        ShowQuote(_serviceDescription, quote.Revision,
            $"{(funding == JobFunding.OwnInventory ? "自有设施" : "NPC 委托")} · 报价修订 {quote.Revision.Value} · 总工作量 {quote.TotalWork}\n需求：{ServiceGoods(quote.RequiredGoods)}\n来源扣料：\n" +
            string.Join("\n", quote.SourceConsumption.Select(source => $"库存 {source.Inventory.Value}：{ServiceGoods(source.Goods)}")) +
            $"\nNPC 补购：{ServiceGoods(quote.NpcPurchases)}\n材料款：{Amount(quote.MaterialPayment)} · 工钱：{Amount(quote.ServicePayment)} · 总额：{Amount(quote.TotalPayment)}", quote.Blockers,
            reason: funding == JobFunding.NpcCommission && quote.TotalPayment is null ? "总付款未提供，不能确认。" : null);
        _serviceConfirm.Disabled = !CanAct || !quote.Blockers.IsEmpty || (funding == JobFunding.NpcCommission && quote.TotalPayment is null);
    }

    private string ServiceGoods(ImmutableArray<GoodsQuantity> goods) => goods.IsEmpty ? "无" :
        string.Join("、", goods.Select(item => $"{WorldPanels.GoodName(item.Good, _content)} × {item.Quantity}"));

    private static string ServiceTargetTitle(WorkTarget target) => target.Instance switch
    {
        InstanceId.Vehicle vehicle => $"车辆 {vehicle.Id.Value}",
        InstanceId.Building building => $"建筑 {building.Id.Value}",
        _ => target.Instance.ToString()!
    };

    private string ServiceFacilityTitle(FacilityKey key)
    {
        var design = _view.FindInstance(new InstanceId.Building(key.Building)) is Lookup<InstanceView>.Found found
            ? _content.FindDesign(found.Value.Design) : null;
        return design is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
            ? $"{complete.Definition.Display.Name} · 建筑 {key.Building.Value}" : $"执行建筑 {key.Building.Value}";
    }

    private string ServiceSourceTitle(InventoryId id) => _view.FindInventory(id) is Lookup<InventoryView>.Found found
        ? found.Value.Owner switch
        {
            InventoryOwner.Train train => $"列车 {train.Id.Value} · 库存 {id.Value}",
            InventoryOwner.Lease lease => $"租库 {lease.Id.Value} · 库存 {id.Value}",
            _ => $"聚居地库存 {id.Value}"
        } : $"库存 {id.Value}";

    private void ConfirmService()
    {
        if (_serviceConfirm.Disabled || !CanAct || _serviceQuote is not { } quote) return;
        CoreCommand command = quote.Funding == JobFunding.OwnInventory ? new CoreCommand.StartOwnedJob(quote.Request)
            : new CoreCommand.CommissionVehicleWork(quote.Request, quote.TotalPayment!.Value);
        _serviceCompletion = null;
        var accepted = _submitTarget(_settlementTarget, command,
            quote.Request is JobRequest.BuildVehicle or JobRequest.BuildBuilding or JobRequest.Refit ? _serviceCandidate : null);
        ResetQuote(ref _serviceQuote, _serviceDescription, "本次报价已使用，请重新获取报价。");
        _serviceConfirm.Disabled = true;
        _serviceResult.Text = accepted is null ? "提交未受理或被拒绝，请查看错误；不会自动重报或重试。" : "任务已受理，等待核心完工事件。";
        if (accepted is not null) ClearServiceDesign();
        RefreshAfterService();
    }

    private void RefreshAfterService()
    {
        if (!ReadCurrent()) return;
        _settlementRevision = null; Refresh(_binding, _view, _content);
    }

    private void UpdateServicePlot()
    {
        if (!GodotObject.IsInstanceValid(_servicePlot) || _settlement is null) return;
        var building = _settlement.Relation == SettlementRelation.PlayerOwned && SelectedBuildingBlueprint();
        if (!building) _serviceSelectedPlot = null;
        var plots = building
            ? _settlement.Plots.Where(plot => plot.Occupant is PlotOccupant.Empty).Select(plot => plot.Id)
            : [];
        SyncChoices(_servicePlot, _servicePlots, plots, _serviceSelectedPlot, PlotTitle, "请选择空地块");
        _servicePlot.Visible = building;
    }

    private bool SelectedBuildingBlueprint() =>
        _serviceOperation.Selected == 0 && _serviceSelectedBlueprint is { } address &&
        _content.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found { Value.Definition.Specification: DesignSpecification.Building };

    private bool IsEmptyServicePlot(PlotId id) =>
        _settlement!.Plots.Any(plot => plot.Id == id && plot.Occupant is PlotOccupant.Empty);

    private string PlotTitle(PlotId id)
    {
        var plot = _settlement!.Plots.FirstOrDefault(item => item.Id == id);
        return plot is null ? $"地块 {id.Value}" : $"空地块 {id.Value} · {PlotSizeName(plot.Size)}";
    }

    internal static string PlotSizeName(PlotSize size) => size switch
    {
        PlotSize.Small => "小", PlotSize.Medium => "中", PlotSize.Large => "大", PlotSize.Giant => "巨型",
        PlotSize.SmallStation => "小站点", PlotSize.LargeStation => "大站点", _ => size.ToString()
    };

    private static string ServiceLibrary(BlueprintLibrary value) => value switch
    { BlueprintLibrary.Packages => "内容包", BlueprintLibrary.Local => "本局库", BlueprintLibrary.User => "用户库", _ => value.ToString() };
    private static string ServiceKind(ConstructionKind value) => value switch
    { ConstructionKind.Construction => "制造", ConstructionKind.Repair => "修理", ConstructionKind.Refit => "改装", ConstructionKind.Deconstruction => "拆解", _ => value.ToString() };
    private static string ServiceState(ConstructionState value) => value switch
    { ConstructionState.Queued => "排队中", ConstructionState.Working => "进行中", ConstructionState.ResourceBlocked => "资源受阻", ConstructionState.Paused => "已暂停", ConstructionState.Failed => "已失败", ConstructionState.Completed => "已完成", _ => value.ToString() };
    private static string ServiceLimit(WorkLimit value) => value switch
    { WorkLimit.Capability => "设施能力", WorkLimit.Power => "电力", WorkLimit.Labor => "劳动力", _ => value.ToString() };
}

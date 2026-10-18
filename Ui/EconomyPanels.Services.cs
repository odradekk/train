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
    private readonly List<VehicleId?> _serviceVehicles = [];
    private readonly List<InventoryId?> _serviceSourceOptions = [];
    private readonly List<InventoryId> _serviceSources = [];
    private ItemList _serviceSourceOrder = null!;
    private FacilityKey? _serviceSelectedFacility;
    private BlueprintAddress? _serviceSelectedBlueprint;
    private VehicleId? _serviceSelectedVehicle;
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
    private bool _serviceSubmitting;
    private bool _serviceQuoteRejected;

    private void BuildServices(VBoxContainer body)
    {
        CloseServices();
        _services = body; _serviceFacilities.Clear(); _serviceBlueprints.Clear(); _serviceVehicles.Clear();
        _serviceSourceOptions.Clear(); _serviceSources.Clear(); _serviceJobRows.Clear();
        _serviceSelectedFacility = null; _serviceSelectedBlueprint = null; _serviceSelectedVehicle = null;
        _serviceOperation = new() { Name = "ServiceOperation" };
        _serviceOperation.AddItem("制造车辆"); _serviceOperation.AddItem("修理车辆"); body.AddChild(_serviceOperation);
        _serviceFacility = ServiceChoice(body, "ServiceFacility");
        _serviceBlueprint = ServiceChoice(body, "ServiceBlueprint");
        _serviceVehicle = ServiceChoice(body, "ServiceVehicle");
        _serviceOperation.ItemSelected += _ => ServiceChanged();
        _serviceFacility.ItemSelected += index => { _serviceSelectedFacility = index < 0 ? null : _serviceFacilities[(int)index]; ServiceChanged(); };
        _serviceBlueprint.ItemSelected += index =>
        {
            _serviceSelectedBlueprint = index < 0 ? null : _serviceBlueprints[(int)index];
            PrepareServiceDesign(); ServiceChanged();
        };
        _serviceVehicle.ItemSelected += index => { _serviceSelectedVehicle = _serviceVehicles[(int)index]; ServiceChanged(); };
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
        _serviceConfirm = UiStyle.Button("ConfirmService", "确认服务", ConfirmService, true); body.AddChild(_serviceConfirm);
        body.AddChild(UiStyle.Button("RefreshServiceQuote", "重新获取报价", () =>
        {
            if (_serviceOperation.Selected == 0 && _serviceCandidate is null) PrepareServiceDesign();
            ServiceChanged();
        }));
        _serviceResult = Text(body, "ServiceResult", "完工以核心事件为准；任务从队列消失不代表完工。");
        body.AddChild(UiStyle.Button("FormServiceVehicle", "编组…", () =>
        {
            if (CanAct) FormationRequested?.Invoke(new(_binding, _content.Generation, new WorldObjectId.Node(_settlement!.Node)), _settlement.Node, _view, _content);
        }));
        Text(body, "ServiceJobsTitle", "NPC 委托队列");
        _serviceJobs = new() { Name = "ServiceJobs" }; body.AddChild(_serviceJobs);
        _serviceDirty = true;
    }

    private static OptionButton ServiceChoice(Node parent, string name)
    { var choice = new OptionButton { Name = name }; parent.AddChild(choice); return choice; }

    public override void _ExitTree() => CloseServices();

    private void CloseServices()
    {
        _serviceCandidate?.Dispose(); _serviceCandidate = null; _serviceDesign = null;
        _serviceDesignProblem = null;
        _serviceShownView = null; _serviceShownContent = null; _serviceQuote = null;
        _serviceCompletion = null; _services = null!;
        _serviceQuoteRejected = false;
    }

    private void ServiceChanged()
    { _serviceDirty = true; _serviceQuoteRejected = false; RefreshServices(); }

    private void MoveServiceSource(int offset)
    {
        var index = _serviceSourceOrder.GetSelectedItems().FirstOrDefault(-1);
        var next = index + offset;
        if (index < 0 || next < 0 || next >= _serviceSources.Count) return;
        (_serviceSources[index], _serviceSources[next]) = (_serviceSources[next], _serviceSources[index]);
        ServiceChanged(); _serviceSourceOrder.Select(next);
    }

    private void PrepareServiceDesign()
    {
        _serviceCandidate?.Dispose(); _serviceCandidate = null; _serviceDesign = null;
        _serviceDesignProblem = null;
        if (_serviceSelectedBlueprint is not { } address || _content.FindBlueprint(address) is not ContentLookup<BlueprintRecord>.Found blueprint) return;
        var result = _host.ContentFactory.PrepareEdit(_content, new ContentEdit.ConstructionTarget(blueprint.Value.Definition));
        if (result is ContentResult<IContentCandidate>.Success success)
        {
            _serviceCandidate = success.Value;
            _serviceDesign = (success.Value.Edit as PreparedContentEdit.ConstructionTarget)?.Design;
        }
        else _serviceDesignProblem = result switch
        {
            ContentResult<IContentCandidate>.Failure failure => string.Join("\n", failure.Diagnostics.Select(Train.App.Editing.EditorService.Describe)),
            ContentResult<IContentCandidate>.Faulted fault => $"{fault.Code}：{fault.Message}",
            _ => "目标设计不可用。"
        };
    }

    private void RefreshServices()
    {
        if (_services is null || !_services.IsVisibleInTree() || _settlement is null || !Current(_settlementTarget)) return;
        _serviceConfirm.Disabled = !CanAct || _serviceQuoteRejected || _serviceQuote is not { Blockers.IsEmpty: true } ||
            (_settlement.Relation != SettlementRelation.PlayerOwned && _serviceQuote.TotalPayment is null);
        if (_host.IsBusy) return;
        if (!_serviceDirty && ReferenceEquals(_serviceShownView, _view) && ReferenceEquals(_serviceShownContent, _content)) return;
        _serviceShownView = _view; _serviceShownContent = _content; _serviceDirty = false;
        var facilities = _settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
            .Select(building => new FacilityKey(building.Id, CapabilityKind.Construction)).ToArray();
        SyncServiceChoice(_serviceFacility, _serviceFacilities, facilities, _serviceSelectedFacility,
            ServiceFacilityTitle, "请选择执行建筑");
        var blueprints = _content.Blueprints.Where(item => item.Design is not null).Where(item =>
            _content.FindBlueprint(item.Address) is ContentLookup<BlueprintRecord>.Found { Value.Definition.Specification: DesignSpecification.Vehicle }).ToArray();
        SyncServiceChoice(_serviceBlueprint, _serviceBlueprints, blueprints.Select(item => item.Address), _serviceSelectedBlueprint,
            id => $"{ServiceLibrary(id.Library)} · {blueprints.First(item => item.Address == id).Display.Name}", "请选择制造蓝图");
        var trains = _view.World.Trains.Select(item => _view.FindTrain(item.Id)).OfType<Lookup<TrainView>.Found>()
            .Select(item => item.Value).Where(train => train.Position is TrainPosition.Node node && node.Id == _settlement.Node).ToArray();
        var vehicles = _settlement.ParkedVehicles.Concat(trains.Where(train => train.Activity == TrainActivity.Stopped && train.SpeedMetersPerSecond == 0)
            .SelectMany(train => train.Vehicles)).Distinct().Where(id => _view.FindInstance(new InstanceId.Vehicle(id)) is
                Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned }).ToArray();
        SyncChoices(_serviceVehicle, _serviceVehicles, vehicles, _serviceSelectedVehicle, id => $"修理车辆 {id.Value}", "请选择修理车辆");
        _serviceBlueprint.Visible = _serviceOperation.Selected == 0; _serviceVehicle.Visible = _serviceOperation.Selected == 1;
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
            if (index == _serviceSourceOrder.ItemCount) _serviceSourceOrder.AddItem(text); else _serviceSourceOrder.SetItemText(index, text);
        }
        if (sourceSelection >= 0 && sourceSelection < _serviceSources.Count) _serviceSourceOrder.Select(sourceSelection);
        RefreshServiceJobs();
        if (!_serviceSubmitting && !_serviceQuoteRejected) RequestServiceQuote(facilities, vehicles, blueprints.Select(item => item.Address).ToArray());
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
            choice.SetItemText(index, current.Contains(options[index]) ? title(options[index]) : $"已不存在：{options[index]}");
        choice.Select(selected is null ? -1 : options.IndexOf(selected));
        if (selected is null) choice.Text = placeholder;
    }

    private void RequestServiceQuote(FacilityKey[] facilities, VehicleId[] vehicles, BlueprintAddress[] blueprints)
    {
        _serviceQuote = null; _serviceConfirm.Disabled = true;
        if (_serviceSelectedFacility is not { } facility) { _serviceDescription.Text = "请选择执行建筑；资格由核心报价判断。"; return; }
        if (!facilities.Contains(facility)) { _serviceDescription.Text = $"已不存在：执行建筑 {facility.Building.Value}"; return; }
        JobRequest request;
        if (_serviceOperation.Selected == 0)
        {
            if (_serviceSelectedBlueprint is not { } address) { _serviceDescription.Text = "请选择制造蓝图。"; return; }
            if (!blueprints.Contains(address)) { _serviceDescription.Text = $"已不存在：蓝图 {address}"; return; }
            if (_serviceDesign is null || _serviceCandidate is null)
            { _serviceDescription.Text = _serviceDesignProblem ?? "目标设计尚未准备，请重新获取报价。"; return; }
            if (!ReferenceEquals(_serviceCandidate.Basis, _content))
            { _serviceDescription.Text = "目标蓝图的内容版本已失效，请重新选择蓝图。"; return; }
            request = new JobRequest.BuildVehicle(facility, _serviceDesign, [.. _serviceSources], 0);
        }
        else
        {
            if (_serviceSelectedVehicle is not { } id) { _serviceDescription.Text = "请选择修理车辆。"; return; }
            if (!vehicles.Contains(id)) { _serviceDescription.Text = $"已不存在：所选本地玩家车辆 {id.Value}"; return; }
            request = new JobRequest.Repair(facility, new InstanceId.Vehicle(id), [.. _serviceSources], 0);
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
        _serviceSubmitting = true;
        try
        {
            var accepted = _submitTarget(_settlementTarget, command, quote.Request is JobRequest.BuildVehicle ? _serviceCandidate : null);
            _serviceQuote = null; _serviceConfirm.Disabled = true;
            _serviceQuoteRejected = true;
            _serviceResult.Text = accepted is null ? "提交未受理或被拒绝，请查看错误；不会自动重报或重试。" : "任务已受理，等待核心完工事件。";
            if (accepted is not null && quote.Request is JobRequest.BuildVehicle)
            { _serviceCandidate?.Dispose(); _serviceCandidate = null; _serviceDesign = null; }
            if (ReadCurrent()) { _settlementRevision = null; Refresh(_binding, _view, _content); }
        }
        finally { _serviceSubmitting = false; }
    }

    internal void Observe(HostBinding binding, ICoreReadView view, ImmutableArray<EventEnvelope> events)
    {
        if (_serviceObservedBinding != binding)
        { _serviceObservedBinding = binding; _serviceCompletion = null; }
        if (_services is null || _settlement is null || _settlementTarget?.Binding != binding) return;
        foreach (var envelope in events)
        {
            if (envelope.Event is not CoreEvent.ConstructionCompleted completed) continue;
            var createdHere = completed.CreatedInstance is { } instance && view.FindInstance(instance) is
                Lookup<InstanceView>.Found { Value.Location: WorldLocation.Node node } && node.Id == _settlement.Node;
            if (createdHere || _settlement.CommissionJobs.Contains(completed.Job))
            { _serviceCompletion = completed; _serviceDirty = true; }
        }
    }

    private void RefreshServiceJobs()
    {
        var ids = _settlement!.CommissionJobs.OrderBy(id => id.Value).ToArray();
        foreach (var id in _serviceJobRows.Keys.Except(ids).ToArray()) { RemoveControl(_serviceJobRows[id]); _serviceJobRows.Remove(id); }
        foreach (var id in ids)
        {
            if (!_serviceJobRows.TryGetValue(id, out var label)) _serviceJobRows.Add(id, label = Text(_serviceJobs, $"ServiceJob_{id.Value}", ""));
            label.Text = $"任务 {id.Value}：";
            if (_view.FindJob(id) is Lookup<JobView>.Found { Value: JobView.Construction job })
            {
                var value = job.Value;
                label.Text += $"建筑 {value.Facility.Building.Value} · {ServiceKind(value.Kind)} · {ServiceState(value.State)}\n工作 {value.ProgressWork:0.###}/{value.FrozenTotalWork} · {value.Progress:P1} · 实际 {value.ActualWorkPerSecond:0.###} 工作/秒\n限制：{string.Join("、", value.Limits.Select(ServiceLimit))}\n投入：{ServiceGoods(value.InvestedGoods)}\n" + Problems(value.Blockers);
            }
            else label.Text += "已不在当前任务查询中；未收到完工事件。";
        }
        if (_serviceCompletion is { } completed)
        {
            _serviceResult.Text = $"核心已确认任务 {completed.Job.Value} 完工 · {ServiceKind(completed.Kind)}";
            if (completed.CreatedInstance is InstanceId.Vehicle vehicle)
                _serviceResult.Text += _view.FindInstance(vehicle) is Lookup<InstanceView>.Found
                    ? $" · 新车 {vehicle.Id.Value}，可编组" : $" · 新车 {vehicle.Id.Value} 已不存在";
            _serviceResult.Text += $"\n返还：{ServiceGoods(completed.ReturnedGoods)}";
        }
    }

    private static string ServiceLibrary(BlueprintLibrary value) => value switch
    { BlueprintLibrary.Packages => "内容包", BlueprintLibrary.Local => "本局库", BlueprintLibrary.User => "用户库", _ => value.ToString() };
    private static string ServiceKind(ConstructionKind value) => value switch
    { ConstructionKind.Construction => "制造", ConstructionKind.Repair => "修理", ConstructionKind.Refit => "改装", ConstructionKind.Deconstruction => "拆解", _ => value.ToString() };
    private static string ServiceState(ConstructionState value) => value switch
    { ConstructionState.Queued => "排队中", ConstructionState.Working => "进行中", ConstructionState.ResourceBlocked => "资源受阻", ConstructionState.Paused => "已暂停", ConstructionState.Failed => "已失败", ConstructionState.Completed => "已完成", _ => value.ToString() };
    private static string ServiceLimit(WorkLimit value) => value switch
    { WorkLimit.Capability => "设施能力", WorkLimit.Power => "电力", WorkLimit.Labor => "劳动力", _ => value.ToString() };
}

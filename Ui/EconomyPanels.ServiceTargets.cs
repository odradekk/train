using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private OptionButton _serviceTargetKind = null!, _serviceDraft = null!;
    private readonly List<DraftId?> _serviceDrafts = [];
    private DraftId? _serviceSelectedDraft;
    private DesignChangeEvaluation? _serviceDifference;
    private Label _serviceDifferenceText = null!;
    private Button _serviceApply = null!, _serviceEdit = null!;
    internal event Action<InstanceId>? ServiceDesignRequested;

    private void BuildServiceTargets(VBoxContainer body)
    {
        _serviceSelectedDraft = null; _serviceDrafts.Clear();
        _serviceTargetKind = ServiceChoice(body, "ServiceTargetKind");
        _serviceTargetKind.AddItem("蓝图"); _serviceTargetKind.AddItem("该实例已保存的改装草稿");
        _serviceTargetKind.ItemSelected += _ => { ClearServiceDesign(); ServiceChanged(); };
        _serviceDraft = ServiceChoice(body, "ServiceDraft");
        _serviceDraft.ItemSelected += index =>
        { _serviceSelectedDraft = _serviceDrafts[(int)index]; ClearServiceDesign(); ServiceChanged(); };
        _serviceEdit = UiStyle.Button("EditServiceVehicle", "编辑改装草稿…", () =>
        {
            if (CanAct && _serviceSelectedVehicle is { } id && IsLocalServiceVehicle(id))
                ServiceDesignRequested?.Invoke(new InstanceId.Vehicle(id));
        });
        body.AddChild(_serviceEdit);
        _serviceDifferenceText = Text(body, "ServiceDifference", "保存草稿后回到此页获取报价；编辑器不提交车辆操作。");
        _serviceApply = UiStyle.Button("ApplyServiceDesign", "立即应用", ApplyServiceDesign, true);
        body.AddChild(_serviceApply);
    }

    private void ClearServiceDesign()
    {
        _serviceCandidate?.Dispose(); _serviceCandidate = null; _serviceDesign = null;
        _serviceDifference = null; _serviceDesignProblem = null;
        if (GodotObject.IsInstanceValid(_serviceApply)) { _serviceApply.Disabled = true; _serviceApply.Hide(); }
    }

    private void RefreshServiceTargets()
    {
        var refit = _serviceOperation.Selected == 2;
        _serviceTargetKind.Visible = _serviceDraft.Visible = _serviceEdit.Visible = refit;
        _serviceDifferenceText.Visible = refit || _serviceOperation.Selected == 3;
        _serviceDraft.Visible = refit && _serviceTargetKind.Selected == 1;
        _serviceBlueprint.Visible = _serviceOperation.Selected == 0 || refit && _serviceTargetKind.Selected == 0;
        _serviceVehicle.Visible = _serviceOperation.Selected != 0;
        _serviceApply.Visible = refit && _serviceDifference?.CanApplyImmediately == true;
        _serviceEdit.Disabled = !CanAct || _serviceSelectedVehicle is not { } id || !IsLocalServiceVehicle(id);
        var drafts = _content.LocalDrafts.Where(draft => draft.Target is DraftTarget.Instance
            { Id: InstanceId.Vehicle vehicle } && vehicle.Id == _serviceSelectedVehicle && draft.Payload is DraftPayload.Design).ToArray();
        SyncChoices(_serviceDraft, _serviceDrafts, drafts.Select(draft => draft.Id), _serviceSelectedDraft,
            id => drafts.First(draft => draft.Id == id).Payload is DraftPayload.Design design
                ? $"已保存草稿 · {design.Value.Display.Name}" : id.Value.ToString(), "请选择已保存草稿");
        if (_serviceDifference is null) _serviceDifferenceText.Text = _serviceDesignProblem ?? "选择蓝图或该实例已保存的草稿后，获取报价与设计差额。";
        if (_serviceOperation.Selected == 3)
            _serviceDifferenceText.Text = "拆解要求当地停放车辆；返还由核心按完工时损伤结算，进入自有聚居地库存。开工后可暂停或恢复。";
    }

    private void PrepareServiceDesign()
    {
        ClearServiceDesign();
        if (_serviceOperation.Selected is not (0 or 2)) return;
        if (_serviceOperation.Selected == 2 && _serviceSelectedVehicle is null)
        { _serviceDesignProblem = "请选择车辆。"; RefreshServiceTargets(); return; }
        DesignDraft? draft = null;
        if (_serviceOperation.Selected == 2 && _serviceTargetKind.Selected == 1)
            draft = _content.LocalDrafts.FirstOrDefault(saved => saved.Id == _serviceSelectedDraft &&
                saved.Target == new DraftTarget.Instance(new InstanceId.Vehicle(_serviceSelectedVehicle!.Value)))?.Payload
                is DraftPayload.Design design ? design.Value : null;
        else if (_serviceSelectedBlueprint is { } address && _content.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found blueprint)
            draft = blueprint.Value.Definition;
        if (draft is null) { _serviceDesignProblem = "请选择现有蓝图或该实例已保存的改装草稿。"; RefreshServiceTargets(); return; }
        ContentEdit edit;
        if (_serviceOperation.Selected == 0) edit = new ContentEdit.ConstructionTarget(draft);
        else if (_serviceSelectedVehicle is { } id && _view.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found instance)
            edit = new ContentEdit.InstanceTarget(instance.Value.Design, draft);
        else { _serviceDesignProblem = "已不存在：所选车辆。"; RefreshServiceTargets(); return; }
        var result = _host.ContentFactory.PrepareEdit(_content, edit);
        if (result is ContentResult<IContentCandidate>.Success success)
        {
            _serviceCandidate = success.Value;
            _serviceDesign = (success.Value.Edit as PreparedContentEdit.ConstructionTarget)?.Design;
            _serviceDifference = (success.Value.Edit as PreparedContentEdit.InstanceTarget)?.Difference;
        }
        else _serviceDesignProblem = result switch
        {
            ContentResult<IContentCandidate>.Failure failure => string.Join("\n", failure.Diagnostics.Select(Train.App.Editing.EditorService.Describe)),
            ContentResult<IContentCandidate>.Faulted fault => $"{fault.Code}：{fault.Message}",
            _ => "目标设计不可用。"
        };
        RefreshServiceTargets();
        if (_serviceDifference is { } difference)
        {
            _serviceDifferenceText.Text = $"新增货物：{ServiceGoods(difference.Core.AddedGoods)}\n拆下项：" +
                (difference.Core.RemovedItems.IsEmpty ? "无" : string.Join("、", difference.Core.RemovedItems.Select(item => WorldPanels.GoodName(item.Good, _content)))) +
                $"\n改动单位：{difference.Core.ChangedItemCount}\n返还由核心按完工时损伤结算；委托返还进入当地玩家租用库存。\n" +
                (difference.CanApplyImmediately ? "构成未变，可立即应用；不建任务、不消耗材料。" : "构成改变，须正式改装。") +
                string.Join("\n", difference.Diagnostics.Select(Train.App.Editing.EditorService.Describe));
            _serviceApply.Disabled = !CanAct || !difference.CanApplyImmediately;
        }
    }

    private bool IsServiceBuilding(BuildingId id) =>
        _view.FindInstance(new InstanceId.Building(id)) is Lookup<InstanceView>.Found instance &&
        _content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete { Core: { } input } } &&
        input.Design.Parts.Any(part => part.Capabilities.Any(capability => capability is PartCapability.Construction));

    private bool IsCurrentServiceBuilding(FacilityKey key) => _settlement!.Plots.Any(plot =>
        plot.Occupant is PlotOccupant.Building building && building.Id == key.Building) && IsServiceBuilding(key.Building);

    private bool IsLocalServiceVehicle(VehicleId id) =>
        _view.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found
        { Value.Ownership: SettlementRelation.PlayerOwned, Value.Location: WorldLocation.Node node } && node.Id == _settlement!.Node;

    private void ValidateServiceSelection()
    {
        string? reason = null;
        if (_serviceSelectedFacility is { } facility && !IsCurrentServiceBuilding(facility)) reason = "已不存在：执行建筑，请重新选择。";
        if (_serviceOperation.Selected != 0 && _serviceSelectedVehicle is { } id && !IsLocalServiceVehicle(id)) reason = "已不存在：所选本地玩家车辆，请重新选择。";
        if (_serviceCandidate is { } candidate && !ReferenceEquals(candidate.Basis, _content)) reason = "目标内容版本已失效，请重新获取报价。";
        if (_serviceDifference is { } difference && _serviceSelectedVehicle is { } vehicle &&
            _view.FindInstance(new InstanceId.Vehicle(vehicle)) is Lookup<InstanceView>.Found found && found.Value.Design != difference.Core.Before)
            reason = "车辆设计已变化，请重新获取报价。";
        if (reason is not null)
        {
            ResetQuote(ref _serviceQuote, _serviceDescription, reason); ClearServiceDesign();
            _serviceConfirm.Disabled = true;
        }
        if (_serviceApply is not null) _serviceApply.Disabled = !CanAct || _serviceDifference?.CanApplyImmediately != true;
    }

    private void ApplyServiceDesign()
    {
        if (!CanAct || _serviceApply.Disabled || _serviceDifference is not { CanApplyImmediately: true } difference ||
            _serviceSelectedVehicle is not { } id) return;
        _serviceCompletion = null;
        _serviceResult.Text = "等待核心设计变化事件。";
        var accepted = _submitTarget(_settlementTarget, new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(id), difference.Core), _serviceCandidate);
        ResetQuote(ref _serviceQuote, _serviceDescription, "请重新获取报价与设计差额。");
        ClearServiceDesign();
        if (accepted is null) _serviceResult.Text = "立即应用未受理或被拒绝；已保存草稿保留，请查看错误。";
        RefreshAfterService(accepted is not null);
    }

    internal void OpenVehicleServices(InstanceId instance)
    {
        if (instance is not InstanceId.Vehicle vehicle || !ReadCurrent() ||
            _view.FindInstance(instance) is not Lookup<InstanceView>.Found { Value.Location: WorldLocation.Node node } ||
            _view.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is not NodeOccupant.Settlement settlement) return;
        OpenSettlement(new(_binding, _content.Generation, new WorldObjectId.Settlement(settlement.Id)), settlement.Id);
        _serviceSelectedVehicle = vehicle.Id; _serviceOperation.Select(2);
        if (_content.LocalDrafts.Any(saved => saved.Target == new DraftTarget.Instance(instance)))
        {
            _serviceTargetKind.Select(1);
            _serviceSelectedDraft = _content.LocalDrafts.First(saved => saved.Target == new DraftTarget.Instance(instance)).Id;
        }
        _settlementBody.GetNode<TabContainer>("Pages").CurrentTab = 6; ServiceChanged();
    }
}

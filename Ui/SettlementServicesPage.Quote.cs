using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal partial class SettlementServicesPage
{
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
        _serviceQuote = ReadQuote(_view.GetJobQuote(request, funding), _serviceDescription, _content);
        if (_serviceQuote is not { } quote) return;
        var reasons = new[]
        {
            FacilityBlockReason(facility, _serviceOperation.Selected == 0 ? null : _serviceSelectedTarget?.Instance, quote.Blockers),
            funding == JobFunding.NpcCommission && quote.TotalPayment is null ? "总付款未提供，不能确认。" : null
        }.OfType<string>().ToArray();
        ShowQuote(_view, _content, _serviceDescription, quote.Revision,
            $"{(funding == JobFunding.OwnInventory ? "自有设施" : "NPC 委托")} · 报价修订 {quote.Revision.Value} · 总工作量 {quote.TotalWork}\n需求：{ServiceGoods(quote.RequiredGoods)}\n来源扣料：\n" +
            string.Join("\n", quote.SourceConsumption.Select(source => $"库存 {source.Inventory.Value}：{ServiceGoods(source.Goods)}")) +
            $"\nNPC 补购：{ServiceGoods(quote.NpcPurchases)}\n材料款：{Amount(quote.MaterialPayment)} · 工钱：{Amount(quote.ServicePayment)} · 总额：{Amount(quote.TotalPayment)}", quote.Blockers,
            reason: reasons.Length == 0 ? null : string.Join("\n", reasons));
        _serviceConfirm.Disabled = !CanAct || !quote.Blockers.IsEmpty || (funding == JobFunding.NpcCommission && quote.TotalPayment is null);
    }

    // 执行建筑正被施工锁定和施工上限不够，核心报同一个问题；这里按读视图区分，并写出实际档位。
    private string? FacilityBlockReason(FacilityKey facility, InstanceId? target, ImmutableArray<CoreProblem> blockers)
    {
        if (!blockers.Any(problem => problem is { Code: "conditions_not_met", Field: "Facility" }) ||
            _view.FindInstance(new InstanceId.Building(facility.Building)) is not Lookup<InstanceView>.Found found)
            return null;
        if ((found.Value.Lock & (InstanceLock.UnderRefit | InstanceLock.UnderRepair | InstanceLock.UnderDeconstruction)) != 0)
            return "执行建筑自身正在改装、修理或拆解，完成前不能执行其他施工。";
        var limits = _content.FindDesign(found.Value.Design) is ContentLookup<DesignEvaluation>.Found
            { Value: DesignEvaluation.Complete { Core: { } core } }
            ? core.Design.Parts.Where(part => !part.Missing).SelectMany(part => part.Capabilities)
                .OfType<PartCapability.Construction>().ToArray() : [];
        if (limits.Length == 0) return "执行建筑没有可用的施工能力。";
        var details = target is not null && _view.FindInstance(target) is Lookup<InstanceView>.Found item ? item.Value.Details : null;
        return details switch
        {
            InstanceDetails.Building building =>
                $"施工上限不足：执行建筑最高施工{PlotSizeName(limits.Max(item => item.MaximumBuildingPlotSize))}地块的建筑，" +
                $"目标建筑为{PlotSizeName(building.Size)}" + building.Size switch
                {
                    PlotSize.SmallStation => "，按大地块计。",
                    PlotSize.LargeStation => "，按巨型地块计。",
                    _ => "。"
                },
            InstanceDetails.Vehicle vehicle => limits.Max(item => item.MaximumVehicleSize) is { } maximum
                ? $"施工上限不足：执行建筑最高施工{SizeName(maximum)}车辆，目标车辆为{SizeName(vehicle.Size)}。"
                : "执行建筑不能施工车辆。",
            _ => "执行建筑的施工上限低于目标设计的档位。"
        };
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
        _refreshSettlement();
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

}

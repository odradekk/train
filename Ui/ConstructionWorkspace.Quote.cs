using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal partial class ConstructionWorkspace
{
    private void Evaluate()
    {
        _quote = null; _confirm.Disabled = true; _reason.Text = ""; _summary.Text = ""; Clear(_materials);
        _materials.GetParent<Control>().Hide();
        if (_instance is { } instance && (_view.FindInstance(instance) is not Lookup<InstanceView>.Found found ||
            found.Value.Location != new WorldLocation.Node(_settlement.Node) || found.Value.Ownership != SettlementRelation.PlayerOwned))
        { _reason.Text = "所选实例已不存在或已离开，请重新打开。"; return; }
        var funding = _settlement.Relation == SettlementRelation.PlayerOwned ? JobFunding.OwnInventory : JobFunding.NpcCommission;
        ImmutableArray<InventoryId> sources = funding == JobFunding.OwnInventory
            ? [_settlement.Inventory] : [.. _sourceOrder.Where(_enabledSources.Contains)];
        if (!(_instance is not null && _repairSelected) && !PrepareDesign()) return;
        if (_difference?.CanApplyImmediately == true)
        {
            _sources.Hide(); _summary.Text = "构成未变，不消耗材料，不创建施工任务。";
            UpdateConfirm(); return;
        }
        if (_instance is { } current && _difference is { } difference &&
            _view.FindInstance(current) is Lookup<InstanceView>.Found original && original.Value.Design != difference.Core.Before)
        { _reason.Text = "实例设计已变化，请重新编辑并保存改装草稿。"; return; }
        _quote = FindQuote(facility => _instance is { } target
            ? _repairSelected ? new JobRequest.Repair(facility, target, sources, 0)
                : new JobRequest.Refit(facility, target, _difference!.Core, sources, 0)
            : _plot is { } plot ? new JobRequest.BuildBuilding(facility, plot, _design!, sources, 0)
                : new JobRequest.BuildVehicle(facility, _design!, sources, 0), out var error);
        if (_quote is not { } quote) { _reason.Text = error ?? "无法获取报价。"; return; }
        _sources.Visible = funding == JobFunding.NpcCommission;
        ShowMaterials(quote);
        _reason.Text = Describe(quote.Blockers);
        if (!quote.Shortages.IsEmpty)
            _reason.Text += "\n" + string.Join("、", quote.Shortages.Select(item => $"{WorldPanels.GoodName(item.Good, _content)}还缺 {item.Quantity}")) +
                (funding == JobFunding.OwnInventory ? "。请运到本聚居地库存，或换一个设计。" : "。当地市场的货不足，请用列车带来或补充租用仓库。 ");
        if (funding == JobFunding.NpcCommission && quote.TotalPayment is null && quote.Blockers.IsEmpty)
            _reason.Text = "总付款暂不可用，不能提交。";
        var duration = quote.CapabilityWorkPerSecond > 0 ? quote.TotalWork / quote.CapabilityWorkPerSecond : (double?)null;
        var time = duration is { } seconds ? (seconds < 60 ? "工期：约 " : "预计完成：") + ServiceDuration(_view, seconds)
            : "当前没有可用施工能力。";
        var plotName = _settlement.Plots.FirstOrDefault(item => item.Id == _plot);
        var destination = _instance is not null ? "修理或改装完成后，实例留在原处。"
            : _plot is not null ? $"完成后：建筑出现在{PlotSizeName(plotName!.Size)}地块。" : $"完成后：车辆停在{PlaceName()}。";
        _summary.Text = $"材料款：{Amount(quote.MaterialPayment)} · 工钱：{Amount(quote.ServicePayment)}\n合计：{Amount(quote.TotalPayment)}\n{time}\n" +
            (funding == JobFunding.NpcCommission ? "排队、电力或劳动力不足会延长工期。\n" : "电力或劳动力不足会延长工期。\n") +
            destination + (_difference is null ? "" : _instance is InstanceId.Vehicle
                ? "\n改装期间列车不能运行；返还材料按完工时损伤结算。"
                : "\n改装期间建筑不能工作；返还材料按完工时损伤结算。");
        UpdateConfirm();
    }

    private bool PrepareDesign()
    {
        if (_candidate is not null && ReferenceEquals(_candidate.Basis, _content)) return true;
        ClearDesign();
        DesignDraft? definition = _instance is { } instance
            ? _content.LocalDrafts.FirstOrDefault(item => item.Id == _draft && item.Target == new DraftTarget.Instance(instance))?.Payload
                is DraftPayload.Design draft ? draft.Value : null
            : _blueprint is { } address && _content.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found blueprint
                ? blueprint.Value.Definition : null;
        if (definition is null) { _reason.Text = _instance is null ? "请从左边选择蓝图。" : "请选择已保存的改装草稿。"; return false; }
        ContentEdit edit;
        if (_instance is { } target && _view.FindInstance(target) is Lookup<InstanceView>.Found original)
            edit = new ContentEdit.InstanceTarget(original.Value.Design, definition);
        else edit = new ContentEdit.ConstructionTarget(definition);
        if (_host.ContentFactory.PrepareEdit(_content, edit) is not ContentResult<IContentCandidate>.Success prepared)
        { _reason.Text = "所选设计无法用于施工，请检查设计并重新保存。"; return false; }
        _candidate = prepared.Value;
        _design = (_candidate.Edit as PreparedContentEdit.ConstructionTarget)?.Design;
        _difference = (_candidate.Edit as PreparedContentEdit.InstanceTarget)?.Difference;
        return true;
    }

    private JobQuote? FindQuote(Func<FacilityKey, JobRequest> request, out string? error)
    {
        JobQuote? first = null;
        error = null;
        var funding = _settlement.Relation == SettlementRelation.PlayerOwned ? JobFunding.OwnInventory : JobFunding.NpcCommission;
        foreach (var building in _settlement.Plots.Select(item => item.Occupant).OfType<PlotOccupant.Building>())
        {
            if (!IsConstructionFacility(_view, _content, building.Id)) continue;
            switch (_view.GetJobQuote(request(new(building.Id, CapabilityKind.Construction)), funding))
            {
                case CoreResult<JobQuote>.Success { Value: var quote }:
                    first ??= quote;
                    if (quote.Blockers.IsEmpty) return quote;
                    break;
                case CoreResult<JobQuote>.Failure failure: error ??= Describe(failure.Problems); break;
                case CoreResult<JobQuote>.Faulted: error = "核心模拟发生故障，无法获取报价。"; return null;
            }
        }
        error ??= "这里没有可用的施工设施。";
        return first;
    }

    private void ShowMaterials(JobQuote quote)
    {
        _materials.GetParent<Control>().Visible = !quote.RequiredGoods.IsEmpty;
        foreach (var title in new[] { "材料", "需要", "来源", "当地购买", "费用" }) Text(_materials, "Heading", title);
        for (var index = 0; index < quote.RequiredGoods.Length; index++)
        {
            var required = quote.RequiredGoods[index];
            var allocations = quote.SourceConsumption.SelectMany(source => source.Goods.Where(item => item.Good == required.Good)
                .Select(item => $"{SourceName(source.Inventory)} × {item.Quantity}"));
            var purchase = quote.NpcPurchases.FirstOrDefault(item => item.Good == required.Good);
            var shortage = quote.Shortages.FirstOrDefault(item => item.Good == required.Good)?.Quantity ?? 0;
            var origin = string.Join("\n", allocations);
            if (shortage > 0) origin += $"\n还缺 {shortage}";
            Text(_materials, $"MaterialName_{index}", WorldPanels.GoodName(required.Good, _content));
            Text(_materials, $"MaterialNeed_{index}", required.Quantity.ToString());
            Text(_materials, $"MaterialSources_{index}", origin.Length == 0 ? "无" : origin).CustomMinimumSize = new(140, 0);
            Text(_materials, $"MaterialPurchase_{index}", (purchase?.Quantity ?? 0).ToString());
            Text(_materials, $"MaterialPayment_{index}", (purchase?.Payment.Value ?? 0).ToString());
        }
    }

    private void UpdateConfirm()
    {
        _edit.Disabled = !CanAct;
        _consequence.Visible = _difference?.CanApplyImmediately != true;
        if (_difference?.CanApplyImmediately == true)
        { _confirm.Text = "立即应用"; _confirm.Disabled = _submitted || !CanAct || _candidate is null; return; }
        var action = _instance is null ? _plot is not null ? "开工" : _settlement.Relation == SettlementRelation.PlayerOwned ? "制造车辆" : "委托制造"
            : _repairSelected ? "修理" : "改装";
        _confirm.Text = _quote?.TotalPayment is { } payment ? $"{action} · {payment.Value}" : action;
        _confirm.Disabled = _submitted || !CanAct || _quote is not { Blockers.IsEmpty: true } ||
            _quote.Funding == JobFunding.NpcCommission && _quote.TotalPayment is null;
    }

    private void Confirm()
    {
        if (_confirm.Disabled || !CanAct || _target is null) return;
        var status = _host.GetStatus()!;
        if (!ReferenceEquals(status.View, _view) || !ReferenceEquals(_host.Content, _content))
        { Refresh(status.Binding, status.View, _host.Content!); return; }
        CoreCommand command;
        if (_instance is { } instance && _difference?.CanApplyImmediately == true)
            command = new CoreCommand.ApplyInstanceDesign(instance, _difference.Core);
        else if (_quote is { } quote) command = quote.Funding == JobFunding.OwnInventory
            ? new CoreCommand.StartOwnedJob(quote.Request) : new CoreCommand.CommissionVehicleWork(quote.Request, quote.TotalPayment!.Value);
        else return;
        _submitted = true; _confirm.Disabled = true; _refreshing = true;
        SubmissionResult result;
        try { result = _submit(_target, command, _candidate); }
        finally { _refreshing = false; }
        ClearDesign();
        if (_host.GetStatus() is { } after && _host.Content is { } content)
        { _shownView = _view = after.View; _shownContent = _content = content; }
        _result.Text = result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }
            ? command is CoreCommand.ApplyInstanceDesign ? "改装方案已应用。" : "施工已开始，可在车辆页或建筑页查看进度。" : "";
        _reason.Text = result switch
        {
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } => "",
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Describe(rejected.Problems),
            SubmissionResult.NotAccepted rejected => Describe([rejected.Reason]),
            _ => "核心模拟发生故障，无法完成操作。"
        };
    }
}

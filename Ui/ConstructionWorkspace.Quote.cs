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
        _quote = null;
        var (reason, summary) = EvaluateDetails();
        _reason.Text = reason; _summary.Text = summary;
        if (_quote is { } quote) ShowMaterials(quote);
        else _materials.GetParent<Control>().Hide();
        UpdateConfirm();
    }

    private (string Reason, string Summary) EvaluateDetails()
    {
        if (_instance is { } instance && (_view.FindInstance(instance) is not Lookup<InstanceView>.Found found ||
            found.Value.Location != new WorldLocation.Node(_settlement.Node) || found.Value.Ownership != SettlementRelation.PlayerOwned))
            return ("所选实例已不存在或已离开，请重新打开。", "");
        var funding = _settlement.Relation == SettlementRelation.PlayerOwned ? JobFunding.OwnInventory : JobFunding.NpcCommission;
        ImmutableArray<InventoryId> sources = funding == JobFunding.OwnInventory
            ? [_settlement.Inventory] : [.. _sourceOrder.Where(_enabledSources.Contains)];
        if (!(_instance is not null && _repairSelected) && !PrepareDesign(out var designError)) return (designError, "");
        if (_difference?.CanApplyImmediately == true)
        {
            _sources.Hide();
            return ("", "构成未变，不消耗材料，不创建施工任务。");
        }
        _quote = FindQuote(facility => _instance is { } target
            ? _repairSelected ? new JobRequest.Repair(facility, target, sources, 0)
                : new JobRequest.Refit(facility, target, _difference!.Core, sources, 0)
            : _plot is { } plot ? new JobRequest.BuildBuilding(facility, plot, _design!, sources, 0)
                : new JobRequest.BuildVehicle(facility, _design!, sources, 0), out var error);
        if (_quote is not { } quote) return (error ?? "无法获取报价。", "");
        _sources.Visible = funding == JobFunding.NpcCommission;
        var reason = quote.Blockers.IsEmpty ? "" : error ?? Describe(quote.Blockers);
        if (!quote.Shortages.IsEmpty)
            reason += "\n" + string.Join("、", quote.Shortages.Select(item => $"{WorldPanels.GoodName(item.Good, _content)}还缺 {item.Quantity}")) +
                (funding == JobFunding.OwnInventory ? "。请运到本聚居地库存，或换一个设计。" : "。当地市场的货不足，请用列车带来或补充租用仓库。");
        if (funding == JobFunding.NpcCommission && quote.TotalPayment is null && quote.Blockers.IsEmpty)
            reason = "总付款暂不可用，不能提交。";
        var duration = quote.CapabilityWorkPerSecond > 0 ? quote.TotalWork / quote.CapabilityWorkPerSecond : (double?)null;
        var time = duration is { } seconds ? (seconds < 60 ? "工期：约 " : "预计完成：") + ServiceDuration(_view, seconds)
            : "当前没有可用施工能力。";
        var plotName = _settlement.Plots.FirstOrDefault(item => item.Id == _plot);
        var destination = _instance is not null ? "修理或改装完成后，实例留在原处。"
            : _plot is not null ? $"完成后：建筑出现在{PlotSizeName(plotName!.Size)}地块。" : $"完成后：车辆停在{PlaceName()}。";
        var summary = $"材料款：{Amount(quote.MaterialPayment)} · 工钱：{Amount(quote.ServicePayment)}\n合计：{Amount(quote.TotalPayment)}\n{time}\n" +
            (funding == JobFunding.NpcCommission ? "排队、电力或劳动力不足会延长工期。\n" : "电力或劳动力不足会延长工期。\n") +
            destination + (_difference is null ? "" : _instance is InstanceId.Vehicle
                ? "\n改装期间列车不能运行；返还材料按完工时损伤结算。"
                : "\n改装期间建筑不能工作；返还材料按完工时损伤结算。");
        return (reason, summary);
    }

    private bool PrepareDesign(out string error)
    {
        error = "";
        if (_candidate is not null && ReferenceEquals(_candidate.Basis, _content)) return true;
        ClearDesign();
        DesignDraft? definition = _instance is { } instance
            ? _content.LocalDrafts.FirstOrDefault(item => item.Id == _draft && item.Target == new DraftTarget.Instance(instance))?.Payload
                is DraftPayload.Design draft ? draft.Value : null
            : _blueprint is { } address && _content.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found blueprint
                ? blueprint.Value.Definition : null;
        if (definition is null) { error = _instance is null ? "请从左边选择蓝图。" : "请选择已保存的改装草稿。"; return false; }
        ContentEdit edit;
        if (_instance is { } target && _view.FindInstance(target) is Lookup<InstanceView>.Found original)
            edit = new ContentEdit.InstanceTarget(original.Value.Design, definition);
        else edit = new ContentEdit.ConstructionTarget(definition);
        var result = _host.ContentFactory.PrepareEdit(_content, edit);
        if (result is not ContentResult<IContentCandidate>.Success prepared)
        {
            error = result switch
            {
                ContentResult<IContentCandidate>.Failure failure => string.Join("\n", failure.Diagnostics.Select(item => item.Message)),
                _ => "所选设计无法用于施工，请检查设计并重新保存。"
            };
            return false;
        }
        _candidate = prepared.Value;
        _design = (_candidate.Edit as PreparedContentEdit.ConstructionTarget)?.Design;
        _difference = (_candidate.Edit as PreparedContentEdit.InstanceTarget)?.Difference;
        return true;
    }

    private JobQuote? FindQuote(Func<FacilityKey, JobRequest> request, out string? error)
        => FindConstructionQuote(_view, _content,
            _settlement.Plots.Select(item => item.Occupant).OfType<PlotOccupant.Building>()
                .Where(item => IsConstructionFacility(_view, _content, item.Id)).Select(item => item.Id),
            _settlement.Relation == SettlementRelation.PlayerOwned ? JobFunding.OwnInventory : JobFunding.NpcCommission,
            request, out error);

    private void ShowMaterials(JobQuote quote)
    {
        _materials.GetParent<Control>().Visible = !quote.RequiredGoods.IsEmpty;
        var npc = quote.Funding == JobFunding.NpcCommission;
        var headings = npc ? new[] { "材料", "需要", "来源", "当地购买", "费用" } : new[] { "材料", "需要", "来源" };
        var labels = _materials.GetChildren().Cast<Label>().ToArray();
        if (_materials.Columns != headings.Length || labels.Length != headings.Length * (quote.RequiredGoods.Length + 1))
        {
            Clear(_materials); _materials.Columns = headings.Length;
            _materials.CustomMinimumSize = new(npc ? 480 : 320, 0);
            foreach (var title in headings) Text(_materials, "Heading", title);
            for (var index = 0; index < quote.RequiredGoods.Length; index++)
                foreach (var column in (npc ? new[] { "Name", "Need", "Sources", "Purchase", "Payment" } : new[] { "Name", "Need", "Sources" }))
                {
                    var label = Text(_materials, $"Material{column}_{index}", "");
                    if (column == "Sources") label.CustomMinimumSize = new(140, 0);
                }
            labels = _materials.GetChildren().Cast<Label>().ToArray();
        }
        for (var index = 0; index < quote.RequiredGoods.Length; index++)
        {
            var required = quote.RequiredGoods[index];
            var allocations = quote.SourceConsumption.SelectMany(source => source.Goods.Where(item => item.Good == required.Good)
                .Select(item => $"{SourceName(source.Inventory)} × {item.Quantity}"));
            var purchase = quote.NpcPurchases.FirstOrDefault(item => item.Good == required.Good);
            var shortage = quote.Shortages.FirstOrDefault(item => item.Good == required.Good)?.Quantity ?? 0;
            var origin = string.Join("\n", allocations);
            if (shortage > 0) origin += $"\n还缺 {shortage}";
            var offset = headings.Length * (index + 1);
            labels[offset].Text = WorldPanels.GoodName(required.Good, _content);
            labels[offset + 1].Text = required.Quantity.ToString();
            labels[offset + 2].Text = origin.Length == 0 ? "无" : origin;
            if (npc)
            {
                labels[offset + 3].Text = (purchase?.Quantity ?? 0).ToString();
                labels[offset + 4].Text = (purchase?.Payment.Value ?? 0).ToString();
            }
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
        {
            Refresh(status.Binding, status.View, _host.Content!);
            if (_confirm.Disabled || !CanAct) return;
        }
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

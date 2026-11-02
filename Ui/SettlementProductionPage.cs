using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.GoodsRows;

namespace Train.Ui;

internal sealed class SettlementProductionPage
{
    private PanelTarget _settlementTarget = null!;
    private readonly Func<bool> _canAct;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private readonly GoodsRows _goods;
    private readonly Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit;
    private readonly Func<Action?, bool> _refreshAfterSubmission;

    internal SettlementProductionPage(Func<bool> canAct, GoodsRows goods,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, Func<Action?, bool> refreshAfterSubmission)
    { _canAct = canAct; _goods = goods; _submit = submit; _refreshAfterSubmission = refreshAfterSubmission; }

    internal void Bind(ICoreReadView view, IContentView content, SettlementView settlement, PanelTarget target)
    { _view = view; _content = content; _settlement = settlement; _settlementTarget = target; }

    private VBoxContainer _production = null!;
    private Label _power = null!;
    private Label _labor = null!;
    private Label _noFacilities = null!;
    private bool _syncingPlan;
    private readonly Dictionary<FacilityKey, FacilityRow> _facilityRows = [];
    private sealed record ProductionGoodsRows(VBoxContainer Control)
    {
        internal readonly Dictionary<GoodId, GoodsRow> Goods = [];
    }
    private sealed record FacilityRow(VBoxContainer Control, Label State, Label Blockers, Label Limits,
        Label CurrentSpecification, Label CurrentOutputs, ProductionGoodsRows Outputs, Label Batch,
        ProductionGoodsRows FrozenOutputs, Label OutputBuffer, ProductionGoodsRows Buffer, Label PlanMessage,
        OptionButton ProcessChoice, List<ProcessRef?> Processes, LineEdit Priority, Button Apply, Button Stop)
    {
        internal bool ProcessEdited;
        internal ProcessRef? EditedProcess;
        internal string? EditedPriority;
    }

    internal void BuildProduction(VBoxContainer body)
    {
        _production = body; _facilityRows.Clear();
        _power = Text(body, "Power", ""); _labor = Text(body, "Labor", "");
        _noFacilities = Text(body, "Empty", "没有生产设施。");
    }

    internal void RefreshProduction()
    {
        var settlement = _settlement!;
        var power = settlement.Power;
        _power.Text = $"电力：当前发电能力 {power.GenerationCapacity:0.###} · 燃料能量余量 {power.FuelEnergyRemaining:0.###}\n上一步需求 {power.Demand:0.###} · 实际供电 {power.ActualSupply:0.###}";
        var population = settlement.Population;
        _labor.Text = $"劳动力：当前可用 {population.AvailableLabor:0.###} · 上一步实际占用 {population.AllocatedLabor:0.###} · 当前闲置 {population.IdleLabor:0.###}\n当前能力变化后，上一步占用可能暂时超过当前可用量。";
        _noFacilities.Visible = settlement.Facilities.IsEmpty;
        foreach (var key in _facilityRows.Keys.Where(key => !settlement.Facilities.Any(facility => facility.Key == key)).ToArray())
        { RemoveControl(_facilityRows[key].Control); _facilityRows.Remove(key); }
        foreach (var facility in settlement.Facilities)
        {
            if (!_facilityRows.TryGetValue(facility.Key, out var row))
                _facilityRows.Add(facility.Key, row = BuildFacilityRow(facility.Key));
            var state = facility.State switch
            {
                ProductionState.Disabled => "已停用", ProductionState.Running => "生产中",
                ProductionState.WaitingForInputs => "等待原料", ProductionState.OutputBlocked => "产出受阻",
                ProductionState.ZeroCapability => "有效能力为零", ProductionState.ConditionsNotMet => "条件未满足",
                ProductionState.Locked => "已锁定", ProductionState.ProcessMissing => "流程缺失", _ => facility.State.ToString()
            };
            row.State.Text = $"建筑 {facility.Key.Building.Value} · {(facility.Key.Capability == CapabilityKind.Extraction ? "开采" : "加工／回收")} · {state}\n计划{(facility.Enabled ? "启用" : "停用")} · 优先级 {facility.Priority}\n当前能力 {facility.CapabilityWorkPerSecond:0.###} 工作/秒 · 上一步实际 {facility.ActualWorkPerSecond:0.###} 工作/秒";
            row.Blockers.Visible = !facility.Blockers.IsEmpty;
            row.Blockers.Text = "受阻：\n" + Problems(facility.Blockers, _content);
            row.Limits.Visible = !facility.Limits.IsEmpty;
            row.Limits.Text = "上一步限制：" + string.Join("、", facility.Limits.Select(limit => limit switch
            { WorkLimit.Capability => "设施能力", WorkLimit.Power => "电力", WorkLimit.Labor => "劳动力", _ => limit.ToString() }));
            var process = _content.Core.Processes.FirstOrDefault(candidate => (facility.Process, candidate.Details) switch
            {
                (ProcessRef.Production p, ProcessDetails.Production d) => p.Definition == d.Id,
                (ProcessRef.Production p, ProcessDetails.Extraction d) => p.Definition == d.Id,
                (ProcessRef.Recovery p, ProcessDetails.Recovery d) => p.Definition == d.Id,
                _ => false
            });
            row.CurrentSpecification.Text = facility.Process is null ? "当前计划：未选择流程" : $"当前定义：{ProcessName(facility.Process)}";
            if (process is not null)
                row.CurrentSpecification.Text += $" · 修订 {ShortRevision(process.Revision)}\n工作量 {process.WorkRequired} · 每工作电力 {process.PowerPerWork:0.###} · 劳动 {process.LaborPerWork:0.###}";
            ImmutableArray<GoodsQuantity> outputs = process?.Details switch
            {
                ProcessDetails.Production p => p.Outputs, ProcessDetails.Recovery p => p.Outputs,
                ProcessDetails.Extraction p => [p.Extracts], _ => []
            };
            row.CurrentOutputs.Visible = process is not null;
            RefreshProductionGoods(row.Outputs, outputs);
            row.Batch.Text = facility.Batch is { } batch
                ? $"进行中批次：{ProcessName(batch.Process)} · 启动修订 {ShortRevision(batch.StartedFromRevision)}\n冻结工作量 {batch.FrozenWorkRequired} · 已完成 {batch.ProgressWork:0.###} · 进度 {batch.Progress:P1}\n{(batch.CurrentSpecificationDiffers ? "当前规格与批次冻结规格不同" : "当前规格与批次冻结规格一致")}\n本批次按冻结规格完成，下一批使用当前定义。\n冻结产出："
                : "没有进行中批次。";
            RefreshProductionGoods(row.FrozenOutputs, facility.Batch?.FrozenOutputs ?? []);
            row.OutputBuffer.Text = facility.OutputBuffer.IsEmpty ? "输出缓冲：空" : "输出缓冲（尚未入库）：";
            RefreshProductionGoods(row.Buffer, facility.OutputBuffer);
            SyncPlan(row, facility);
        }
    }

    private FacilityRow BuildFacilityRow(FacilityKey key)
    {
        var box = new VBoxContainer { Name = "Facility" }; _production.AddChild(box);
        var state = Text(box, "State", ""); var blockers = Text(box, "Blockers", "");
        var limits = Text(box, "Limits", ""); var current = Text(box, "CurrentSpecification", "");
        var currentOutputs = Text(box, "CurrentOutputs", "当前定义产出：");
        var outputs = new VBoxContainer { Name = "Outputs" }; box.AddChild(outputs);
        var batch = Text(box, "Batch", "");
        var frozen = new VBoxContainer { Name = "FrozenOutputs" }; box.AddChild(frozen);
        var outputBuffer = Text(box, "OutputBuffer", "");
        var buffer = new VBoxContainer { Name = "Buffer" }; box.AddChild(buffer);
        var message = Text(box, "PlanMessage", "");
        var processes = new List<ProcessRef?>();
        var choice = new OptionButton { Name = $"PlanProcess_{key.Building.Value}" };
        box.AddChild(choice);
        choice.ItemSelected += _ =>
        {
            if (_syncingPlan || !_facilityRows.TryGetValue(key, out var row) || choice.Selected < 0 || choice.Selected >= row.Processes.Count) return;
            row.ProcessEdited = true;
            row.EditedProcess = row.Processes[choice.Selected];
            row.Apply.Disabled = !CanEditPlan() || choice.Selected <= 0 || row.EditedProcess is null;
        };
        Text(box, "PlanPriorityHint", "优先级（整数，越小越先）");
        var priority = new LineEdit { Name = $"PlanPriority_{key.Building.Value}" }; box.AddChild(priority);
        priority.TextChanged += text =>
        {
            if (!_syncingPlan && _facilityRows.TryGetValue(key, out var row)) row.EditedPriority = text;
        };
        var apply = UiStyle.Button($"ApplyPlan_{key.Building.Value}", "应用计划", () => ApplyProductionPlan(key, true), true);
        var stop = UiStyle.Button($"StopPlan_{key.Building.Value}", "停用", () => ApplyProductionPlan(key, false), true);
        box.AddChild(apply); box.AddChild(stop);
        return new(box, state, blockers, limits, current, currentOutputs, new(outputs), batch, new(frozen), outputBuffer, new(buffer),
            message, choice, processes, priority, apply, stop);
    }

    private void SyncPlan(FacilityRow row, FacilityView facility)
    {
        _syncingPlan = true;
        try
        {
            var next = new List<ProcessRef?> { null };
            next.AddRange(CandidateProcesses(facility.Key));
            while (row.ProcessChoice.ItemCount > next.Count) row.ProcessChoice.RemoveItem(row.ProcessChoice.ItemCount - 1);
            while (row.Processes.Count > next.Count) row.Processes.RemoveAt(row.Processes.Count - 1);
            for (var index = 0; index < next.Count; index++)
            {
                var text = next[index] is { } process ? ProcessName(process) : "选择流程";
                if (index == row.ProcessChoice.ItemCount)
                {
                    row.Processes.Add(next[index]);
                    row.ProcessChoice.AddItem(text);
                }
                else
                {
                    row.Processes[index] = next[index];
                    if (row.ProcessChoice.GetItemText(index) != text) row.ProcessChoice.SetItemText(index, text);
                }
            }
            var selected = row.ProcessEdited
                ? row.EditedProcess is null ? 0 : next.FindIndex(item => item == row.EditedProcess)
                : facility.Process is { } current ? next.FindIndex(item => item == current) : 0;
            if (selected < 0) selected = 0;
            if (row.ProcessChoice.Selected != selected) row.ProcessChoice.Select(selected);
            var priority = row.EditedPriority ?? facility.Priority.ToString(CultureInfo.InvariantCulture);
            if (!row.Priority.HasFocus() && row.Priority.Text != priority) row.Priority.Text = priority;
            var editable = CanEditPlan();
            row.ProcessChoice.Disabled = !editable;
            row.Priority.Editable = editable;
            row.Apply.Disabled = !editable || row.ProcessChoice.Selected <= 0;
            row.Stop.Disabled = !editable || facility.Process is null || !facility.Enabled;
            row.PlanMessage.Text = "";
        }
        finally { _syncingPlan = false; }
    }

    private ProcessRef[] CandidateProcesses(FacilityKey key)
    {
        var tags = FacilityTags(key);
        return _content.Core.Processes.Where(process => CapabilityMatches(key.Capability, process.Details) &&
                (tags is null || !tags.Value.IsEmpty && tags.Value.Contains(process.RequiredFacilityTag)))
            .Select(ProcessReference).OfType<ProcessRef>().ToArray();
    }

    private static bool CapabilityMatches(CapabilityKind capability, ProcessDetails details) => capability switch
    {
        CapabilityKind.Extraction => details is ProcessDetails.Extraction,
        CapabilityKind.Production => details is ProcessDetails.Production or ProcessDetails.Recovery,
        _ => false
    };

    private static ProcessRef? ProcessReference(ProcessProjection process) => process.Details switch
    {
        ProcessDetails.Production production => new ProcessRef.Production(production.Id),
        ProcessDetails.Extraction extraction => new ProcessRef.Production(extraction.Id),
        ProcessDetails.Recovery recovery => new ProcessRef.Recovery(recovery.Id),
        _ => null
    };

    private ImmutableArray<FacilityTag>? FacilityTags(FacilityKey key)
    {
        if (_view.FindInstance(new InstanceId.Building(key.Building)) is not Lookup<InstanceView>.Found instance) return null;
        if (_content.FindDesign(instance.Value.Design) is not ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete { Core: { } input } })
            return null;
        return input.Design.Kind is DesignKind.Building building ? building.FacilityTags : null;
    }

    private bool CanEditPlan() => _canAct() && _settlement?.Relation == SettlementRelation.PlayerOwned;

    private void ApplyProductionPlan(FacilityKey key, bool enabled)
    {
        if (!CanEditPlan() || !_facilityRows.TryGetValue(key, out var row)) return;
        var facility = _settlement!.Facilities.FirstOrDefault(item => item.Key == key);
        ProcessRef? process = enabled
            ? row.ProcessEdited ? row.EditedProcess : row.Processes.ElementAtOrDefault(row.ProcessChoice.Selected)
            : facility?.Process;
        if (facility is null || process is null || enabled && row.Apply.Disabled || !enabled && row.Stop.Disabled) return;
        if (!int.TryParse(row.EditedPriority ?? row.Priority.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
        { row.PlanMessage.Text = "优先级须为整数。"; return; }
        var accepted = _submit(_settlementTarget, new CoreCommand.SetProductionPlan(key, process, enabled, priority));
        if (accepted is not null) _refreshAfterSubmission(() =>
        {
            row.ProcessEdited = false; row.EditedProcess = null; row.EditedPriority = null;
        });
    }

    private void RefreshProductionGoods(ProductionGoodsRows rows, ImmutableArray<GoodsQuantity> goods)
    {
        foreach (var id in rows.Goods.Keys.Where(id => !goods.Any(good => good.Good == id)).ToArray())
        { RemoveControl(rows.Goods[id].Control); rows.Goods.Remove(id); }
        foreach (var good in goods)
        {
            if (!rows.Goods.TryGetValue(good.Good, out var row))
                rows.Goods.Add(good.Good, row = _goods.CreateGoodRow(rows.Control, good.Good));
            _goods.UpdateGoodRow(row, good.Good, good.Quantity, _content);
        }
        rows.Control.Visible = !goods.IsEmpty;
    }

    private string ProcessName(ProcessRef process)
    {
        if (_content.FindDefinitionDisplay(new CoreDefinitionReference.Process(process)) is ContentLookup<ContentDisplay>.Found found)
            return found.Value.Text.Name;
        var definition = process switch
        { ProcessRef.Production p => p.Definition.Value, ProcessRef.Recovery p => p.Definition.Value, _ => process.ToString()! };
        return $"流程定义缺失（定义标识：{definition}）";
    }

    private static string ShortRevision(SemanticRevision revision) => revision.Value[..Math.Min(8, revision.Value.Length)];
    internal void Close() { _facilityRows.Clear(); }
}

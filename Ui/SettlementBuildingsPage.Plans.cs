using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed partial class SettlementBuildingsPage
{
    private bool _syncing;
    private sealed record PlanRow(VBoxContainer Root, Label State, Label Blockers, Label Current,
        Label Batch, VBoxContainer FrozenOutputs, Label FrozenGoods, Label Buffer,
        OptionButton Process, List<ProcessRef?> Processes, CheckButton Enabled, Button[] Priority, Label Message)
    {
        internal CoreCommand.SetProductionPlan? Pending;
    }

    private PlanRow BuildPlan(VBoxContainer parent, FacilityKey key)
    {
        var root = new VBoxContainer { Name = "Facility" }; parent.AddChild(root);
        Text(root, "Capability", key.Capability == CapabilityKind.Extraction ? "开采" : "生产与回收");
        var state = Text(root, "State", ""); var blockers = Text(root, "Blockers", "");
        var current = Text(root, "CurrentSpecification", "");
        var batch = Text(root, "Batch", "");
        var outputs = new VBoxContainer { Name = "FrozenOutputs" }; root.AddChild(outputs);
        var frozen = Text(outputs, "Goods", ""); var buffer = Text(root, "Buffer", "");
        Text(root, "ProcessLabel", "生产流程");
        var process = new OptionButton { Name = "Process", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        root.AddChild(process);
        var processes = new List<ProcessRef?>();
        process.ItemSelected += index =>
        {
            if (!_syncing && index >= 0 && index < processes.Count && processes[(int)index] is { } selected) ChangePlan(key, process: selected);
        };
        var enabled = new CheckButton { Name = "Enabled", Text = "启用" }; root.AddChild(enabled);
        enabled.Toggled += value => { if (!_syncing) ChangePlan(key, enabled: value); };
        Text(root, "PriorityLabel", "优先级");
        var priorities = new HBoxContainer(); root.AddChild(priorities);
        var group = new ButtonGroup();
        var buttons = new List<Button>();
        foreach (var (name, title, value) in new[] { ("PriorityLow", "低", 1), ("PriorityMiddle", "中", 0), ("PriorityHigh", "高", -1) })
        {
            var button = UiStyle.Button(name, title, () => { if (!_syncing) ChangePlan(key, priority: value); });
            button.ToggleMode = true; button.ButtonGroup = group; button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            priorities.AddChild(button); buttons.Add(button);
        }
        var message = Text(root, "PlanMessage", "");
        return new(root, state, blockers, current, batch, outputs, frozen, buffer, process, processes, enabled, [.. buttons], message);
    }

    private void RefreshPlan(PlanRow row, FacilityView facility)
    {
        _syncing = true;
        try
        {
            row.State.Text = Status(facility);
            var remedy = facility.State switch
            {
                ProductionState.WaitingForInputs => facility.Key.Capability == CapabilityKind.Extraction
                    ? "检查节点资源是否足够；资源耗尽时改用其他流程。" : "向库存补充流程所需原料。",
                ProductionState.OutputBlocked => "把库存里的货物装上列车，腾出仓储空间。",
                ProductionState.ZeroCapability => "修理受损设备，或改装建筑以提供所需能力。",
                ProductionState.ConditionsNotMet => "选择适合这座建筑和当地地形的流程。",
                ProductionState.Locked => "等待建筑的施工或交战结束。",
                ProductionState.ProcessMissing => "恢复缺失的流程内容，或选择其他流程。",
                _ => ""
            };
            var limits = facility.Limits.Where(limit => limit is WorkLimit.Power or WorkLimit.Labor).ToArray();
            if (limits.Contains(WorkLimit.Power)) remedy += "\n电力不足，请补充燃料或增加发电能力。";
            if (limits.Contains(WorkLimit.Labor)) remedy += "\n劳动力不足，可提高优先级或停用其他建筑。";
            row.Blockers.Text = Describe(facility.Blockers) + (remedy.Length == 0 ? "" : "\n" + remedy);
            row.Blockers.Visible = row.Blockers.Text.Trim().Length > 0;
            row.Current.Text = facility.Process is null ? "当前计划：未选择流程" : $"当前流程：{ProcessName(facility.Process)}";
            row.Batch.Text = facility.Batch is { } batch
                ? $"本批流程：{ProcessName(batch.Process)} · 进度 {batch.Progress:P0}\n本批按开始时的投入与产出完成，下一批使用当前流程。" : "没有进行中的批次。";
            row.FrozenOutputs.Visible = facility.Batch is { FrozenOutputs.IsEmpty: false };
            row.FrozenGoods.Text = "本批产出：" + Goods(facility.Batch?.FrozenOutputs ?? []);
            row.Buffer.Text = facility.OutputBuffer.IsEmpty ? "" : "等待入库：" + Goods(facility.OutputBuffer);
            row.Buffer.Visible = !facility.OutputBuffer.IsEmpty;
            var selected = row.Pending?.Process ?? facility.Process;
            var next = new List<ProcessRef?> { null };
            next.AddRange(CandidateProcesses(facility.Key));
            var availableCount = next.Count;
            if (selected is not null && !next.Contains(selected)) next.Add(selected);
            while (row.Process.ItemCount > next.Count) row.Process.RemoveItem(row.Process.ItemCount - 1);
            row.Processes.Clear(); row.Processes.AddRange(next);
            for (var index = 0; index < next.Count; index++)
            {
                var text = next[index] is { } reference ? ProcessDescription(reference) : "选择流程";
                if (index == row.Process.ItemCount) row.Process.AddItem(text);
                else if (row.Process.GetItemText(index) != text) row.Process.SetItemText(index, text);
                row.Process.SetItemDisabled(index, index == 0 || index >= availableCount);
            }
            var choice = next.FindIndex(item => item == selected);
            if (row.Process.Selected != choice) row.Process.Select(choice);
            row.Process.TooltipText = selected is null ? "先选择生产流程" : ProcessDescription(selected);
            row.Enabled.SetPressedNoSignal(row.Pending?.Enabled ?? facility.Enabled);
            var priority = Math.Sign(row.Pending?.Priority ?? facility.Priority);
            row.Priority[0].SetPressedNoSignal(priority > 0);
            row.Priority[1].SetPressedNoSignal(priority == 0);
            row.Priority[2].SetPressedNoSignal(priority < 0);
            if (selected is null && row.Message.Text.Length == 0) row.Message.Text = "先选择生产流程，再调整启用状态和优先级。";
        }
        finally { _syncing = false; }
    }

    private void ChangePlan(FacilityKey key, ProcessRef? process = null, bool? enabled = null, int? priority = null)
    {
        if (!canAct() || !_plans.TryGetValue(key, out var row) ||
            _settlement.Facilities.FirstOrDefault(item => item.Key == key) is not { } facility) return;
        var selected = process ?? row.Pending?.Process ?? facility.Process;
        if (selected is null) return;
        var command = new CoreCommand.SetProductionPlan(key, selected,
            enabled ?? row.Pending?.Enabled ?? (facility.Process is null || facility.Enabled), priority ?? row.Pending?.Priority ?? facility.Priority);
        row.Pending = command;
        var result = submit(command);
        if (result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }) row.Pending = null;
        row.Message.Text = Result(result, "生产设置已更新。");
        if (_settlement.Facilities.FirstOrDefault(item => item.Key == key) is { } current) RefreshPlan(row, current);
        UpdateActions();
    }

    private IEnumerable<ProcessRef> CandidateProcesses(FacilityKey key)
    {
        if (_view.FindInstance(new InstanceId.Building(key.Building)) is not Lookup<InstanceView>.Found instance ||
            _content.FindDesign(instance.Value.Design) is not ContentLookup<DesignEvaluation>.Found
                { Value: DesignEvaluation.Complete { Core.Design.Kind: DesignKind.Building building } }) return [];
        return _content.Core.Processes.Where(process => building.FacilityTags.Contains(process.RequiredFacilityTag) &&
            (key.Capability == CapabilityKind.Extraction ? process.Details is ProcessDetails.Extraction : process.Details is ProcessDetails.Production or ProcessDetails.Recovery))
            .Select(process => Reference(process.Details));
    }

    private static ProcessRef Reference(ProcessDetails details) => details switch
    {
        ProcessDetails.Production production => new ProcessRef.Production(production.Id),
        ProcessDetails.Extraction extraction => new ProcessRef.Production(extraction.Id),
        ProcessDetails.Recovery recovery => new ProcessRef.Recovery(recovery.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(details))
    };

    private ProcessProjection? Process(ProcessRef reference) => _content.Core.Processes.FirstOrDefault(process => Reference(process.Details) == reference);
    private string ProcessName(ProcessRef reference) => Process(reference) is null ? "流程内容缺失" :
        _content.FindDefinitionDisplay(new CoreDefinitionReference.Process(reference)) is ContentLookup<ContentDisplay>.Found found
            ? found.Value.Text.Name : "流程名称缺失";
    private string ProcessDescription(ProcessRef reference)
    {
        var process = Process(reference);
        if (process is null) return "流程内容缺失";
        var inputs = process.Details switch { ProcessDetails.Production p => p.Inputs, ProcessDetails.Recovery p => p.Inputs, _ => [] };
        var outputs = process.Details switch { ProcessDetails.Production p => p.Outputs, ProcessDetails.Recovery p => p.Outputs,
            ProcessDetails.Extraction p => [p.Extracts], _ => [] };
        var consumption = process.Details is ProcessDetails.Extraction extraction ? "节点资源 " + Goods([extraction.Extracts]) : Goods(inputs);
        return $"{ProcessName(reference)} · 产出 {Goods(outputs)} · 消耗 {consumption}";
    }
    private string Goods(ImmutableArray<GoodsQuantity> goods) => goods.IsEmpty ? "无" :
        string.Join("、", goods.Select(item => $"{SettlementOverviewPage.GoodName(item.Good, _content)} × {item.Quantity}"));
    private string Describe(ImmutableArray<CoreProblem> problems) => string.Join("\n", problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, _content)));
    private string Result(SubmissionResult result, string success) => result switch
    {
        SubmissionResult.NotAccepted rejected => Describe([rejected.Reason]),
        SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Describe(rejected.Problems),
        SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } => success,
        _ => "操作失败，请查看错误报告。"
    };
}

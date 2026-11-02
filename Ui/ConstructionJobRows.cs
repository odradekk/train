using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal sealed class ConstructionJobRows(Func<bool> canAct, Func<CoreCommand, SubmissionResult> submit)
{
    private VBoxContainer _body = null!;
    private Label _empty = null!;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private readonly Dictionary<JobId, Row> _rows = [];
    private sealed record Row(VBoxContainer Root, Label Description, Label Result)
    {
        internal Button? Pause { get; set; }
    }

    internal void Build(VBoxContainer body)
    {
        _body = new() { Name = "ConstructionJobs" }; body.AddChild(_body);
        _empty = Text(_body, "NoConstructionJobs", "没有进行中的施工任务。");
    }

    internal void Refresh(ICoreReadView view, IContentView content, SettlementView settlement, bool vehicles)
    {
        _view = view; _content = content; _settlement = settlement;
        var jobs = settlement.OwnedJobs.Concat(settlement.CommissionJobs).Distinct().Select(view.FindJob)
            .OfType<Lookup<JobView>.Found>().Select(item => item.Value).OfType<JobView.Construction>()
            .Select(item => item.Value).Where(job => vehicles
                ? job.Target is ConstructionTarget.NewVehicle or ConstructionTarget.Existing { Instance: InstanceId.Vehicle }
                : job.Target is ConstructionTarget.NewBuilding or ConstructionTarget.Existing { Instance: InstanceId.Building }).ToArray();
        foreach (var id in _rows.Keys.Except(jobs.Select(job => job.Id)).ToArray())
        { RemoveControl(_rows[id].Root); _rows.Remove(id); }
        _empty.Visible = jobs.Length == 0;
        foreach (var job in jobs)
        {
            if (!_rows.TryGetValue(job.Id, out var row))
            {
                var id = job.Id;
                var root = new VBoxContainer { Name = $"ConstructionJob_{id.Value}" }; _body.AddChild(root);
                var description = Text(root, "JobDescription", "");
                row = new(root, description, Text(root, "JobResult", "")); _rows.Add(id, row);
            }
            var summary = vehicles ? DescribeVehicleJob(view, content, job)
                : $"正在{(job.Kind == ConstructionKind.Construction ? "建造" : ServiceKind(job.Kind))}{TargetName(job.Target)} · 进度 {job.Progress:P0} · {ServiceRemainingTime(job, view)}";
            row.Description.Text = summary + $"\n状态：{ServiceState(job.State)}\n投入：{Goods(job.InvestedGoods)}\n{Reservations(job.Id)}" +
                (job.Blockers.IsEmpty ? "" : "\n受阻：" + Describe(job.Blockers));
            if (settlement.OwnedJobs.Contains(job.Id) && job.Kind == ConstructionKind.Deconstruction)
            {
                if (row.Pause is null)
                {
                    var id = job.Id;
                    row.Pause = UiStyle.Button($"PauseConstructionJob_{id.Value}", "", () => TogglePause(id));
                    row.Root.AddChild(row.Pause);
                }
                row.Pause.Text = job.State == ConstructionState.Paused ? "恢复拆解" : "暂停拆解";
                row.Pause.Disabled = !canAct();
            }
            else if (row.Pause is not null) { RemoveControl(row.Pause); row.Pause = null; }
        }
    }

    private void TogglePause(JobId id)
    {
        if (!canAct() || !_settlement.OwnedJobs.Contains(id) || !_rows.TryGetValue(id, out var row) ||
            _view.FindJob(id) is not Lookup<JobView>.Found
            { Value: JobView.Construction { Value.Kind: ConstructionKind.Deconstruction } found }) return;
        var result = submit(new CoreCommand.SetDeconstructionPaused(id, found.Value.State != ConstructionState.Paused));
        row.Result.Text = result switch
        {
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } => "拆解暂停状态已更新。",
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Describe(rejected.Problems),
            SubmissionResult.NotAccepted rejected => Describe([rejected.Reason]),
            _ => "操作失败，请查看错误报告。"
        };
    }

    private string TargetName(ConstructionTarget target)
    {
        DesignHash? design = target switch
        {
            ConstructionTarget.NewBuilding newBuilding => newBuilding.Design,
            ConstructionTarget.Existing existing => existing.TargetDesign,
            _ => null
        };
        var name = design is { } hash && _content.FindDesign(hash) is ContentLookup<DesignEvaluation>.Found
            { Value: DesignEvaluation.Complete complete } ? complete.Definition.Display.Name : "建筑内容缺失";
        return target is ConstructionTarget.NewBuilding building
            ? name + (_settlement.Plots.FirstOrDefault(plot => plot.Id == building.Plot) is { } plot
                ? $"（{PlotSizeName(plot.Size)}地块）" : "（地块已不存在）") : name;
    }

    private string Reservations(JobId job)
    {
        var inventories = new List<(InventoryId Id, string Name)> { (_settlement.Inventory, "聚居地库存") };
        var leaseNumber = 0;
        foreach (var id in _settlement.Leases)
            if (_view.FindLease(id) is Lookup<LeaseView>.Found lease)
                inventories.Add((lease.Value.Inventory, $"租用仓库 {++leaseNumber}（容量 {lease.Value.Capacity}）"));
        foreach (var summary in _view.World.Trains)
            if (_view.FindTrain(summary.Id) is Lookup<TrainView>.Found train &&
                train.Value.Position is TrainPosition.Node node && node.Id == _settlement.Node)
                inventories.Add((train.Value.Inventory, $"列车 {train.Value.Id.Value} 货舱"));
        var lines = new List<string>();
        foreach (var inventory in inventories.DistinctBy(item => item.Id))
            if (_view.FindInventory(inventory.Id) is Lookup<InventoryView>.Found found)
                foreach (var reservation in found.Value.Reservations.Where(item => item.Job == job))
                    lines.Add($"{inventory.Name} · 容量 {reservation.Capacity} · 预计返还 {Goods(reservation.ExpectedReturns)}");
        return lines.Count == 0 ? "返还库存预留：无" : "返还库存预留：" + string.Join("；", lines);
    }

    private string Goods(ImmutableArray<GoodsQuantity> goods) => goods.IsEmpty ? "无" :
        string.Join("、", goods.Select(item => $"{SettlementOverviewPage.GoodName(item.Good, _content)} × {item.Quantity}"));
    private string Describe(ImmutableArray<CoreProblem> problems) =>
        string.Join("\n", problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, _content)));
}

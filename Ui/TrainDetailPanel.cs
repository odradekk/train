using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class TrainDetailPanel
{
    private readonly TrainId _id;
    private readonly PanelTarget _target;
    private readonly Func<bool> _available;
    private readonly Action<PanelTarget, TrainId> _inventory;
    private readonly Action<TrainId> _combat;
    private readonly Action<PanelTarget, TrainId> _plan;
    private readonly Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit;
    private readonly Action<PanelTarget, TrainId> _disband;
    private readonly Action<InstanceId> _refit;
    private readonly Action<InstanceId> _service;
    internal DetailPanelControls Controls { get; }

    internal TrainDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target, TrainId id,
        Func<bool> available,
        Action<PanelTarget, TrainId> inventory,
        Action<TrainId> combat,
        Action<PanelTarget, TrainId> plan,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit,
        Action<PanelTarget, TrainId> disband,
        Action<InstanceId> refit,
        Action<InstanceId> service)
    {
        _id = id;
        _target = target;
        _available = available;
        _inventory = inventory;
        _combat = combat;
        _plan = plan;
        _submit = submit;
        _disband = disband;
        _refit = refit;
        _service = service;
        Controls = new(panel, body, target, Describe, Actions);
    }

    private List<DetailAction> Actions(ICoreReadView view)
    {
        var actions = new List<DetailAction>();
        actions.Add(new("Inventory", "列车库存…", false, () =>
        { if (_available()) _inventory(_target, _id); }));
        actions.Add(new("OpenCombat", "战斗…", false, () =>
        { if (_available()) _combat(_id); }));
        actions.Add(new("EditPlan", "编辑计划", true, () =>
        {
            if (_available()) _plan(_target, _id);
        }));
        actions.Add(new("Start", "启动", true, () => _submit(_target, new CoreCommand.StartTrain(_id))));
        actions.Add(new("Stop", "停车", false, () => _submit(_target, new CoreCommand.StopTrain(_id))));
        actions.Add(new("Disband", "解编…", false, () => _disband(_target, _id)));

        if (view.FindTrain(_id) is Lookup<TrainView>.Found trainForRefit)
            foreach (var vehicle in trainForRefit.Value.Vehicles)
            {
                var id = vehicle;
                actions.Add(new("RefitVehicle", $"改装车辆 {id.Value}…", false, () => { if (_available()) _refit(new InstanceId.Vehicle(id)); }));
                if (trainForRefit.Value.Position is TrainPosition.Node)
                    actions.Add(new("VehicleServices", $"车辆 {id.Value} 服务…", false, () => { if (_available()) _service(new InstanceId.Vehicle(id)); }));
            }
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var text = new StringBuilder();
        if (view.FindTrain(_id) is Lookup<TrainView>.Found foundTrain)
        {
            var value = foundTrain.Value;
            text.AppendLine($"状态：{WorldPanels.Activity(value.Activity)}\n位置：{PositionText(value.Position, view, content)}\n速度：{value.SpeedMetersPerSecond:0.00} m/s\n编组：{string.Join(" → ", value.Vehicles.Select(id => $"车辆 {id.Value}"))}\n总长：{value.LengthMeters:0.##} m\n总质量：{value.MassTons:0.##} t\n牵引：{value.TractionKilonewtons:0.##} kN\n加速度：{value.Acceleration:0.##} m/s²\n");
            foreach (var problem in value.Blockers) text.AppendLine(CommandDiagnostics.Describe(problem, content));
            text.AppendLine("\n运行计划");
            if (value.Plan is { } plan)
            {
                text.AppendLine(plan.Loop ? "循环" : "单次");
                foreach (var stop in plan.Stops) text.AppendLine(stop switch
                {
                    PlanStop.Station station => $"{WorldPanels.NodeName(station.Node, view, content)} · 停靠 {station.DwellSteps / (decimal)SimulationTime.StepsPerSecond:0.###} 秒",
                    PlanStop.Waypoint waypoint => $"{WorldPanels.NodeName(waypoint.Node, view, content)} · 途经",
                    _ => ""
                });
            }
            else text.AppendLine("未设置计划");
            text.AppendLine("\n当前运行累计");
            text.AppendLine(value.CurrentRun is { } run ? EconomyPanels.RunText(run, content) : "尚未开始或已结束，无当前运行累计。");
            text.AppendLine("\n最近运行报告");
            foreach (var report in value.RecentReports) text.AppendLine(EconomyPanels.ReportText(report, content));
            if (value.RecentReports.IsEmpty) text.AppendLine("暂无已结束的运行报告。");
        }
        else return "已不存在";
        return text.ToString().ReplaceLineEndings("\n");
    }

    private static string PositionText(TrainPosition position, ICoreReadView view, IContentView content) => position switch
    { TrainPosition.Node node => WorldPanels.NodeName(node.Id, view, content), TrainPosition.Edge edge => $"线路 {edge.Id.Value} · {edge.DistanceMeters:0.##} m", _ => "" };
}

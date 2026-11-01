using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class TrainDetailPanel
{
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

    internal TrainDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target,
        Func<bool> available,
        Action<PanelTarget, TrainId> inventory,
        Action<TrainId> combat,
        Action<PanelTarget, TrainId> plan,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit,
        Action<PanelTarget, TrainId> disband,
        Action<InstanceId> refit,
        Action<InstanceId> service)
    {
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

    private List<(string Name, string Text, bool Primary, Action Press)> Actions(ICoreReadView view)
    {
        var bound = _target;
        var target = bound.Object;
        var actions = new List<(string Name, string Text, bool Primary, Action Press)>();
        if (target is WorldObjectId.Train train)
        {
            actions.Add(("Inventory", "列车库存…", false, () =>
            { if (_available()) _inventory(bound, train.Id); }));
            actions.Add(("OpenCombat", "战斗…", false, () =>
            { if (_available()) _combat(train.Id); }));
            actions.Add(("EditPlan", "编辑计划", true, () =>
            {
                if (_available()) _plan(bound, train.Id);
            }));
            actions.Add(("Start", "启动", true, () => _submit(bound, new CoreCommand.StartTrain(train.Id))));
            actions.Add(("Stop", "停车", false, () => _submit(bound, new CoreCommand.StopTrain(train.Id))));
            actions.Add(("Disband", "解编…", false, () => _disband(bound, train.Id)));
        }
        if (target is WorldObjectId.Train refitTrain && view.FindTrain(refitTrain.Id) is Lookup<TrainView>.Found trainForRefit)
            foreach (var vehicle in trainForRefit.Value.Vehicles)
            {
                var id = vehicle;
                actions.Add(("RefitVehicle", $"改装车辆 {id.Value}…", false, () => { if (_available()) _refit(new InstanceId.Vehicle(id)); }));
                if (trainForRefit.Value.Position is TrainPosition.Node)
                    actions.Add(("VehicleServices", $"车辆 {id.Value} 服务…", false, () => { if (_available()) _service(new InstanceId.Vehicle(id)); }));
            }
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var target = _target.Object;
        var text = new StringBuilder();
        if (target is WorldObjectId.Train train && view.FindTrain(train.Id) is Lookup<TrainView>.Found foundTrain)
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

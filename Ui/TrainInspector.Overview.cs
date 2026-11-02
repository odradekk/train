using System.Text;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class TrainInspector
{
    private void RefreshOverview()
    {
        var text = new StringBuilder();
        text.AppendLine($"状态：{WorldPanels.Activity(_train.Activity)}");
        if (_train.Position is TrainPosition.Edge at && _view.FindEdge(at.Id) is Lookup<EdgeView>.Found edge)
        {
            var map = edge.Value.Map;
            var destination = at.Direction == TravelDirection.Forward ? map.End : map.Start;
            var travelled = at.Direction == TravelDirection.Forward ? at.DistanceMeters : map.LengthMeters - at.DistanceMeters;
            text.AppendLine($"前往 {Place(destination)} · {(map.LengthMeters > 0 ? travelled / map.LengthMeters : 0):0%}");
            text.AppendLine($"经由 {Place(map.Start)}—{Place(map.End)}");
        }
        else if (_train.Position is TrainPosition.Node node)
            text.AppendLine($"位置：{Place(node.Id)}");
        text.AppendLine($"速度：{_train.SpeedMetersPerSecond:0.##} 米/秒");
        foreach (var problem in _train.Blockers) text.AppendLine(ProblemText(problem));
        text.AppendLine("\n运行计划");
        if (_train.Plan is not { } plan) text.AppendLine("这列车没有运行计划，写好计划后才能发车。");
        else
        {
            text.AppendLine($"{(plan.Loop ? "循环" : "单次")} · {plan.Stops.Length} 站");
            text.AppendLine(string.Join(" → ", plan.Stops.Select(stop => stop switch
            {
                PlanStop.Station station => Place(station.Node),
                PlanStop.Waypoint waypoint => $"{Place(waypoint.Node)}（途经）",
                _ => "地点已不存在"
            })));
            if (_train.NextStopIndex is { } next && next < plan.Stops.Length)
                text.AppendLine("下一站：" + Place(plan.Stops[next] is PlanStop.Station station ? station.Node : ((PlanStop.Waypoint)plan.Stops[next]).Node));
            if (_train.RemainingDwellSteps > 0)
                text.AppendLine($"还需停留 {_train.RemainingDwellSteps / (decimal)SimulationTime.StepsPerSecond:0.###} 秒");
        }
        text.AppendLine("\n当前运行累计");
        text.AppendLine(_train.CurrentRun is { } run ? EconomyPanels.RunText(run, _content) : "尚未开始或已结束，无当前运行累计。");
        text.AppendLine("\n上一次运行报告");
        var report = _train.RecentReports.OrderBy(value => value.EndedAt.CompletedSteps).ThenBy(value => value.EndedAt.Sequence).LastOrDefault();
        _openReport.Visible = report is not null;
        if (report is null) text.AppendLine("暂无已结束的运行报告。");
        else
        {
            text.AppendLine(UiInput.FormatTime(report.EndedAt.CompletedSteps) + " · " + EconomyPanels.EndReasonText(report.Reason));
            text.AppendLine(EconomyPanels.RunText(report.Totals, _content));
        }
        _description.Text = text.ToString().TrimEnd().ReplaceLineEndings("\n");
        _openCombat.Visible = _view.World.Combats.Any(item => _view.FindCombat(item.Id) is Lookup<CombatView>.Found combat &&
            combat.Value.Participants.Any(participant => participant.Unit is CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle } &&
                _train.Vehicles.Contains(vehicle.Id)));
    }

    private string Place(NodeId node) => WorldPanels.NodeName(node, _view, _content);
    private string GoodName(GoodId good) => _content.FindGood(good) is ContentLookup<GoodPresentation>.Found { Value.Missing: false } found
        ? found.Value.Display.Text.Name : "货物内容缺失";
    private string ProblemText(CoreProblem problem) => CommandDiagnostics.Describe(problem with { Subject = null }, _content);
}

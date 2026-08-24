using Godot;
using TrainMvp.App;
using TrainMvp.Domain;

namespace TrainMvp.Report;

public partial class ReportPanel : Control
{
    private GameSession _session = null!;
    private VBoxContainer _body = null!;

    public event Action? ReturnToPlanningRequested;

    public void Bind(GameSession session)
    {
        _session = session;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var scroll = new ScrollContainer();
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scroll);
        _body = new VBoxContainer();
        _body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _body.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_body);
    }

    public void ShowCurrentReport()
    {
        foreach (var child in _body.GetChildren())
        {
            _body.RemoveChild(child);
            child.Free();
        }

        var run = _session.CurrentRun ?? throw new InvalidOperationException("No report.");
        var report = run.Report;
        var scenario = _session.Scenario;

        _body.AddChild(UiTheme.MakeLabel("运行报告", 22, true));
        _body.AddChild(UiTheme.MakeLabel($"圈利润 {report.Profit}    资金 {report.CashBefore} → {report.CashAfter}    运行圈 {report.LoopTicks} 刻"));
        if (report.RepositionTicks > 0)
        {
            var path = string.Join(" → ", report.RepositionPath.Select(id => scenario.StationName(id)));
            _body.AddChild(UiTheme.MakeLabel(
                $"调度空驶：{scenario.StationName(report.RepositionFromId ?? "")} → {scenario.StationName(report.RepositionToId ?? "")}，路径 {path}，调度 {report.RepositionTicks} 刻（不计圈利润）。"));
        }
        _body.AddChild(UiTheme.MakeLabel(
            $"圈末 粮{report.CargoAfter[GoodIds.Grain]} 煤{report.CargoAfter[GoodIds.Coal]} 茶{report.CargoAfter[GoodIds.Tea]}    货舱 {report.CargoUsedAfter}/{report.Capacity}"));

        if (_session.PreviousReport is { } previous)
        {
            _body.AddChild(UiTheme.MakeLabel(
                $"与上一圈对比：利润 {previous.Profit} → {report.Profit}，卖出量 {previous.SoldQuantity} → {report.SoldQuantity}，压舱 {previous.CargoUsedAfter} → {report.CargoUsedAfter}"));
        }

        var hintCard = UiTheme.MakeCard();
        var hintStyle = new StyleBoxFlat
        {
            BgColor = report.NextHint.Contains("未成交", StringComparison.Ordinal) ? UiTheme.Warn : UiTheme.Ok,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        hintCard.AddThemeStyleboxOverride("panel", hintStyle);
        hintCard.AddChild(UiTheme.MakeLabel($"下一步：{report.NextHint}", 16, true));
        _body.AddChild(hintCard);

        _body.AddChild(UiTheme.MakeLabel("每站规则结果", 18, true));
        foreach (var stop in report.Stops)
        {
            var title = stop.Traded ? $"{scenario.StationName(stop.StationId)}（交易）" : $"{scenario.StationName(stop.StationId)}（返回起点，不再交易）";
            _body.AddChild(UiTheme.MakeLabel(title, 16, true));
            if (stop.Rules.Count == 0)
            {
                _body.AddChild(UiTheme.MakeLabel("无交易。"));
                continue;
            }

            foreach (var rule in stop.Rules)
            {
                var line =
                    $"{RunReport.FormatAction(rule.Action)}{scenario.GoodName(rule.GoodId)}  报价 {rule.QuotedPrice}  门槛 {rule.Threshold}  期望 {rule.ExpectedQuantity}  实际 {rule.ActualQuantity}  {RunReport.FormatOutcome(rule.Outcome)}";
                if (rule.Cause != RuleFailureCause.None)
                {
                    line += $"  主因：{RunReport.FormatCause(rule.Cause)}";
                }

                _body.AddChild(UiTheme.MakeLabel(line));
            }
        }

        var back = UiTheme.MakeButton("返回规划");
        back.Pressed += () => ReturnToPlanningRequested?.Invoke();
        _body.AddChild(back);
    }
}

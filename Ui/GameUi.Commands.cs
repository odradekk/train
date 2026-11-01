using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command)
        => Submit(target, command, null);

    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command, IContentCandidate? candidate)
        => (SubmitResult(target, command, candidate, true) as SubmissionResult.Completed)?.Receipt.Decision as CommandDecision.Accepted;

    private SubmissionResult SubmitInline(PanelTarget? target, CoreCommand command)
        => SubmitResult(target, command, null, false);

    private SubmissionResult SubmitResult(PanelTarget? target, CoreCommand command, IContentCandidate? candidate, bool reportError)
    {
        var status = _host?.GetStatus();
        if (status is null || _host!.Content is not { } content || target is null ||
            !target.IsAvailable(status.Binding, status.View, _host.PanelGenerationCurrent(target.Binding, target.Generation)) || _host.IsBusy)
        {
            if (reportError) ReportUiError("当前操作不可提交：对象已失效或宿主正在操作。");
            return new SubmissionResult.NotAccepted(new("object_missing", "Target", null, []));
        }
        SubmissionResult result;
        try { result = _host.Submit(target.Binding, command, CommandSource.UserInterface, candidate); }
        catch (NotImplementedException)
        {
            _logger.Warning("ui", "command_not_implemented", command.GetType().Name);
            if (reportError) ShowError("该操作尚未实现。");
            return new SubmissionResult.NotAccepted(new("command_not_implemented", "Command", null, []));
        }
        if (CommandDiagnostics.Report(_logger, result, content) is { } problem && reportError) ShowError(problem);
        return result;
    }

    private void SetSpeed(SimulationSpeed? speed)
    {
        _notificationPause = null;
        if (_host is null) return;
        if (speed is null) { Report(_host.SetPaused(true)); return; }
        var problem = _host.SetSpeed(speed.Value);
        if (problem is not null) { Report(problem); return; }
        Report(_host.SetPaused(false));
    }

    // 玩家和通知暂停由暂停按钮表示；这里列出其他仍然生效的来源，避免倍速按钮按下却不推进时看不出原因。
    private static string PauseText(HostStatus status)
    {
        if (!status.IsPaused) return "运行中";
        var reasons = new[] { (status.FocusPaused, "失焦"), (status.EditingPaused, "编辑"),
            (status.OperationLocked, "处理中"), (status.IsFrozen, "故障") }.Where(item => item.Item1).Select(item => item.Item2);
        return reasons.Any() ? $"已暂停（{string.Join("、", reasons)}）" : "已暂停";
    }

    private void Report(CoreProblem? problem)
    {
        if (problem is null) return;
        ShowError(CommandDiagnostics.LogProblem(_logger, "ui", "simulation_control_denied", problem, _content));
    }
}

using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.App.Authoring;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    private Label _error = null!;
    private long? _missingErrorOperation;
    private PanelContainer _errorPanel = null!;
    private NotificationPolicy _policy = null!;
    private NotificationPanel? _notices;

    private void OnEditorChanged()
    {
        if (_editor is { IsOpen: false, HasError: true, Message: { Length: > 0 } message }) ShowError(message);
    }

    private void Report(CoreProblem? problem)
    {
        if (problem is null) return;
        ShowError(CommandDiagnostics.LogProblem(_logger, "ui", "simulation_control_denied", problem, _content));
    }

    private void ReportUiError(string message)
    {
        _logger.Warning("ui", "ui_operation_invalid", message);
        ShowError(message);
    }

    private void OpenNotice(NotificationNotice notice)
    {
        if (_host?.GetStatus() is not { } status) { ReportUiError("已不存在"); return; }
        var view = status.View;
        switch (notice.Subject)
        {
            case WorldObjectId.Settlement settlement:
                var occupied = view.World.Nodes.FirstOrDefault(item =>
                    item.Occupant is NodeOccupant.Settlement node && node.Id == settlement.Id);
                if (occupied is null) { ReportUiError("已不存在"); return; }
                OpenLocation(new WorldLocation.Node(occupied.Id));
                return;
            case WorldObjectId.Instance instance:
                if (view.FindInstance(instance.Id) is not Lookup<InstanceView>.Found found ||
                    !LocationExists(view, found.Value.Location))
                { ReportUiError("已不存在"); return; }
                OpenLocation(found.Value.Location);
                return;
            case WorldObjectId.Train train:
                if (!view.World.Trains.Any(item => item.Id == train.Id)) { ReportUiError("已不存在"); return; }
                OpenScene(new WorldObjectId.Train(train.Id));
                return;
            case WorldObjectId.Combat combat:
                var listed = view.World.Combats.FirstOrDefault(item => item.Id == combat.Id);
                if (listed is null) { ReportUiError("已不存在"); return; }
                OpenLocation(listed.Location);
                return;
            case { } subject when !SubjectExists(view, subject):
                ReportUiError("已不存在");
                return;
        }
        if (notice.Location is { } location) OpenLocation(location);
        else ReportUiError("已不存在");
    }

    private static bool SubjectExists(ICoreReadView view, WorldObjectId subject) => subject switch
    {
        WorldObjectId.Combat combat => view.World.Combats.Any(item => item.Id == combat.Id),
        WorldObjectId.Train train => view.World.Trains.Any(item => item.Id == train.Id),
        WorldObjectId.Node node => view.World.Nodes.Any(item => item.Id == node.Id),
        WorldObjectId.Edge edge => view.World.Edges.Any(item => item.Id == edge.Id),
        WorldObjectId.Settlement settlement => view.World.Nodes.Any(item =>
            item.Occupant is NodeOccupant.Settlement occupied && occupied.Id == settlement.Id),
        WorldObjectId.Lair lair => view.World.Nodes.Any(item =>
            item.Occupant is NodeOccupant.Lair occupied && occupied.Id == lair.Id),
        WorldObjectId.Instance instance => view.FindInstance(instance.Id) is Lookup<InstanceView>.Found,
        _ => false
    };

    private void OnOperationEnded(HostOperationResult result)
    {
        if (result.Kind == OperationKind.Restore && result.Succeeded)
        {
            _menu.CloseSaves();
            if (!result.Problems.IsEmpty)
                ShowMessage("读档完成，内容已适配", string.Join("\n", result.Problems.Select(AdaptationLine)), UiStyle.Info);
        }
        if (!result.Succeeded && result.Status is not (HostOperationStatus.Cancelled or HostOperationStatus.Discarded))
        {
            var message = string.Join("\n", result.Problems.Select(problem => CommandDiagnostics.Describe(problem, _content))
                .Concat(result.Diagnostics.Select(problem => problem.Message)));
            var operation = result.Kind switch
            { OperationKind.NewGame => "新游戏", OperationKind.Restore => "读档", OperationKind.Reload => _host?.Content is null ? "启动内容加载" : "内容重载", _ => "宿主操作" };
            if (result.Status == HostOperationStatus.Faulted)
                message = result.Fault is { } fault ? CommandDiagnostics.Describe(fault) : $"{operation}发生故障，详情见日志。";
            else if (message.Length == 0) message = result.Status == HostOperationStatus.NotAccepted
                ? $"{operation}当前无法执行，请稍后重试。" : $"{operation}准备失败，请检查内容或输入。";
            ShowError(message);
            if (_newOperation == result.Operation) _newGameError!.Text = message;
        }
        if (_newOperation != result.Operation) return;
        _newOperation = 0;
        _confirmMissing!.Hide();
        _createNewGame!.Disabled = false;
        if (result.Succeeded || result.Status == HostOperationStatus.Cancelled)
            _newGame!.Hide();
    }

    internal void ShowError(string message) => ShowMessage("操作未完成", message, UiStyle.Error);

    private static string AdaptationLine(CoreProblem problem) => problem.Code == "content_changed"
        ? "存档保存后安装的内容有变化，已按当前内容调整存档状态。"
        : "部分状态已按当前安装的内容调整。";

    private void ShowMessage(string title, string message, Color color)
    {
        _missingErrorOperation = null;
        ((Label)_errorPanel.FindChild("Title", true, false)).Text = title;
        _error.Text = message;
        _error.AddThemeColorOverride("font_color", color);
        _errorPanel.Show(); _errorPanel.MoveToFront();
        _errorPanel.Position = new(Math.Max(16, _root.Size.X - _errorPanel.Size.X - 24), 68);
    }
    private void SetSpeed(SimulationSpeed? speed)
    {
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
}

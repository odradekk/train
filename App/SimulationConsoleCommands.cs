
using System.Globalization;
using Train.App.Logging;
using Train.Core.Contracts;
using Train.App.Development;

namespace Train.App;

internal sealed class SimulationConsoleCommands
{
    internal const string Category = "simulation";
    internal const string StatusEventId = "simulation_status";
    internal const string PausedEventId = "simulation_paused";
    internal const string ResumedEventId = "simulation_resumed";
    internal const string SpeedChangedEventId = "simulation_speed_changed";
    internal const string SteppedEventId = "simulation_stepped";
    internal const string CheckpointEventId = "simulation_checkpoint";
    internal const string UsageEventId = "simulation_usage";
    internal const string SessionUnavailableEventId = "simulation_session_unavailable";
    internal const string RequiresPauseEventId = "simulation_requires_pause";
    internal const string FrozenEventId = "simulation_frozen";
    internal const string BusyEventId = "simulation_busy";
    internal const string CheckpointFailedEventId = "simulation_checkpoint_failed";

    private const string Usage =
        "用法: sim [status|pause|resume|speed 1|2|4|8|step [N]|checkpoint]";

    private readonly HostCoordinator _host;
    private readonly TrainLogger _logger;
    private readonly DeveloperTools _development;

    internal SimulationConsoleCommands(HostCoordinator host, TrainLogger logger, DeveloperTools development)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(development);
        _host = host;
        _logger = logger;
        _development = development;
    }

    internal void Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 ||
            arguments.Count == 1 && arguments[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ReportStatus();
            return;
        }

        var command = arguments[0].ToLowerInvariant();
        switch (command)
        {
            case "pause" when arguments.Count == 1:
                SetPaused(true);
                return;
            case "resume" when arguments.Count == 1:
                SetPaused(false);
                return;
            case "speed" when arguments.Count == 2 && TryParseSpeed(arguments[1], out var speed):
                SetSpeed(speed);
                return;
            case "step" when TryParseStepCount(arguments, out var steps):
                Step(steps);
                return;
            case "checkpoint" when arguments.Count == 1:
                ReportCheckpoint();
                return;
            default:
                Log(LogLevel.Warning, UsageEventId, Usage);
                return;
        }
    }

    private void ReportStatus()
    {
        var status = _host.GetStatus();
        if (status is null)
        {
            SessionUnavailable();
            return;
        }

        var state = status.View;
        var fault = state.Fault is null
            ? "无"
            : $"{state.Fault.Code}/{state.Fault.Operation}/{state.Fault.Field}";
        Log(LogLevel.Info, StatusEventId,
            $"模拟位置={state.Position.CompletedSteps}:{state.Position.Sequence}，模拟秒={Number(status.SimulationSeconds)}，" +
            $"暂停={status.IsPaused}，设定倍速={(int)status.Speed}，实际倍速={Number(status.ActualSpeed)}，" +
            $"玩家暂停={status.PlayerPaused}，失焦暂停={status.FocusPaused}，技术操作锁={status.OperationLocked}，" +
            $"帧余量秒={Number(status.Remainder)}，故障={fault}。");
    }

    private void SetPaused(bool paused)
    {
        if (_host.SetPaused(paused) is { } problem)
        {
            ReportProblem(problem);
            return;
        }

        Log(LogLevel.Info, paused ? PausedEventId : ResumedEventId,
            paused ? "玩家暂停已启用。" : _host.GetStatus()!.IsPaused
                ? "玩家暂停已解除；其他暂停来源仍有效，模拟保持暂停。"
                : "玩家暂停已解除，模拟已继续运行。");
    }

    private void SetSpeed(SimulationSpeed speed)
    {
        if (_host.SetSpeed(speed) is { } problem)
        {
            ReportProblem(problem);
            return;
        }

        Log(LogLevel.Info, SpeedChangedEventId,
            $"模拟倍速已设为 {(int)speed}。",
            new Dictionary<string, object?> { ["speed"] = (int)speed });
    }

    private void Step(ulong steps)
    {
        var result = _host.Step(steps);
        if (result.Problem is { } problem)
        {
            ReportProblem(problem, $"请求推进 {steps} 步，实际推进 {result.Completed} 步。");
            return;
        }
        var position = _host.GetStatus()!.View.Position;
        Log(LogLevel.Info, SteppedEventId,
            $"请求推进 {steps} 步，实际推进 {result.Completed} 步，当前位置={position.CompletedSteps}:{position.Sequence}。",
            new Dictionary<string, object?>
            {
                ["requested_steps"] = steps, ["steps_advanced"] = result.Completed,
                ["completed_steps"] = position.CompletedSteps
            });
    }

    private void ReportCheckpoint()
    {
        if (_host.GetStatus() is null)
        {
            SessionUnavailable();
            return;
        }
        if (_development.CaptureCheckpoint("控制台检查点") is { } failure)
            Log(LogLevel.Warning, CheckpointFailedEventId, failure.Message);
        else ReportCheckpointValue(_development.Checkpoints[^1].Checkpoint);
    }

    private void ReportCheckpointValue(CoreCheckpoint checkpoint) => Log(LogLevel.Info, CheckpointEventId,
        $"模拟检查点位置={checkpoint.Position.CompletedSteps}:{checkpoint.Position.Sequence}，SHA-256={checkpoint.Sha256}。",
        new Dictionary<string, object?>
        {
            ["completed_steps"] = checkpoint.Position.CompletedSteps,
            ["sequence"] = checkpoint.Position.Sequence, ["sha256"] = checkpoint.Sha256
        });

    private void SessionUnavailable() =>
        Log(LogLevel.Warning, SessionUnavailableEventId, "没有活动模拟会话。");

    private void ReportProblem(CoreProblem problem, string prefix = "")
    {
        var eventId = problem.Code switch
        {
            "simulation_session_unavailable" => SessionUnavailableEventId,
            "simulation_requires_pause" => RequiresPauseEventId,
            "busy" => BusyEventId,
            "frozen" => FrozenEventId,
            _ => CheckpointFailedEventId
        };
        var fault = problem.Code == "frozen" ? _host.GetStatus()?.View.Fault : null;
        var detail = fault is null ? $"{problem.Code}/{problem.Field}" :
            $"{fault.Code}/{fault.Operation}/{fault.Field}";
        Log(LogLevel.Warning, eventId, $"{prefix}模拟操作未完成：{detail}。",
            new Dictionary<string, object?>
            {
                ["problem_code"] = problem.Code, ["problem_field"] = problem.Field,
                ["fault_code"] = fault?.Code, ["operation"] = fault?.Operation, ["field"] = fault?.Field
            });
    }

    private void Log(LogLevel level, string eventId, string message,
        IReadOnlyDictionary<string, object?>? attributes = null) =>
        _logger.Log(level, Category, eventId, message, attributes);
    private static bool TryParseSpeed(string text, out SimulationSpeed speed)
    {
        speed = text switch
        {
            "1" => SimulationSpeed.One,
            "2" => SimulationSpeed.Two,
            "4" => SimulationSpeed.Four,
            "8" => SimulationSpeed.Eight,
            _ => default
        };
        return text is "1" or "2" or "4" or "8";
    }

    private static bool TryParseStepCount(IReadOnlyList<string> arguments, out ulong steps)
    {
        steps = 1;
        if (arguments.Count == 1)
            return true;
        return arguments.Count == 2 &&
            ulong.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out steps) &&
            steps > 0;
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

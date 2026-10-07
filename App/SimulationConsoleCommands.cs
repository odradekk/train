// Train.App 在 Godot 主线程执行模拟调试命令，并通过共享日志返回结果。
// 查询与操作都作用于已挂接会话，无会话时只报告状态，不建立模拟世界。

using System.Globalization;
using Train.Core.Logging;
using Train.Core.Simulation;

namespace Train.App;

/// <summary>解析 sim 子命令，并调用应用宿主的模拟查询与控制入口。</summary>
/// <remarks>实例由 Debug 宿主创建，Execute 必须在 Godot 主线程同步调用；本类型不创建模拟会话。</remarks>
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

    private const string Usage =
        "用法: sim [status|pause|resume|speed 1|2|4|8|step [N]|checkpoint]";

    private readonly App _app;
    private readonly TrainLogger _logger;

    /// <summary>绑定应用宿主与其共享日志。</summary>
    internal SimulationConsoleCommands(App app, TrainLogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);
        _app = app;
        _logger = logger;
    }

    /// <summary>执行 sim 后的参数；参数形状无效时不查询或修改会话。</summary>
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
                Resume();
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
                _logger.Warning(Category, UsageEventId, Usage);
                return;
        }
    }

    /// <summary>从同一完整边界输出核心位置、宿主时钟观察值及冻结原因。</summary>
    private void ReportStatus()
    {
        var status = _app.GetSimulationStatus();
        if (status is null)
        {
            SessionUnavailable();
            return;
        }

        var state = status.State;
        var fault = state.Fault is null
            ? "无"
            : $"{state.Fault.Code}/{state.Fault.Phase}";
        _logger.Info(Category, StatusEventId,
            $"模拟位置={state.CompletedSteps}:{state.BoundarySequence}，模拟秒={Number(status.SimulationSeconds)}，" +
            $"暂停={status.IsPaused}，设定倍速={(int)status.Speed}，实际倍速={Number(status.ActualSpeed)}，" +
            $"待处理步数={status.PendingSteps}，帧余量秒={Number(status.Remainder)}，故障={fault}。");
    }

    private void SetPaused(bool paused)
    {
        if (!_app.SetSimulationPaused(paused))
        {
            SessionUnavailable();
            return;
        }

        _logger.Info(Category, paused ? PausedEventId : ResumedEventId,
            paused ? "模拟已暂停。" : "模拟已继续运行。");
    }

    /// <summary>检查活动会话尚未冻结后取消宿主暂停。</summary>
    private void Resume()
    {
        var status = _app.GetSimulationStatus();
        if (status is null)
        {
            SessionUnavailable();
            return;
        }
        if (status.State.Fault is not null)
        {
            Frozen(status.State.Fault, "模拟已冻结，不能继续运行");
            return;
        }

        SetPaused(false);
    }

    private void SetSpeed(SimulationSpeed speed)
    {
        if (!_app.SetSimulationSpeed(speed))
        {
            SessionUnavailable();
            return;
        }

        _logger.Info(Category, SpeedChangedEventId,
            $"模拟倍速已设为 {(int)speed}。",
            new Dictionary<string, object?> { ["speed"] = (int)speed });
    }

    /// <summary>验证活动会话已经暂停且未冻结，再同步推进并报告实际完成步数。</summary>
    private void Step(ulong steps)
    {
        var status = _app.GetSimulationStatus();
        if (status is null)
        {
            SessionUnavailable();
            return;
        }
        if (status.State.Fault is not null)
        {
            Frozen(status.State.Fault, "模拟已冻结，不能手动推进");
            return;
        }
        if (!status.IsPaused)
        {
            _logger.Warning(Category, RequiresPauseEventId, "手动推进前必须先暂停模拟。");
            return;
        }

        var result = _app.StepSimulation(steps);
        if (result is null)
        {
            SessionUnavailable();
            return;
        }
        if (result.StateAfter.Fault is { } fault)
        {
            Frozen(fault, $"模拟推进 {result.StepsAdvanced} 步后冻结");
            return;
        }

        _logger.Info(Category, SteppedEventId,
            $"模拟已手动推进 {result.StepsAdvanced} 步，当前位置={result.StateAfter.CompletedSteps}:{result.StateAfter.BoundarySequence}。",
            new Dictionary<string, object?>
            {
                ["requested_steps"] = steps,
                ["steps_advanced"] = result.StepsAdvanced,
                ["completed_steps"] = result.StateAfter.CompletedSteps
            });
    }

    private void ReportCheckpoint()
    {
        var checkpoint = _app.CreateSimulationCheckpoint();
        if (checkpoint is null)
        {
            SessionUnavailable();
            return;
        }

        _logger.Info(Category, CheckpointEventId,
            $"模拟检查点位置={checkpoint.Position.CompletedSteps}:{checkpoint.Position.Sequence}，SHA-256={checkpoint.Sha256}。",
            new Dictionary<string, object?>
            {
                ["completed_steps"] = checkpoint.Position.CompletedSteps,
                ["sequence"] = checkpoint.Position.Sequence,
                ["sha256"] = checkpoint.Sha256
            });
    }

    private void SessionUnavailable() =>
        _logger.Warning(Category, SessionUnavailableEventId, "没有活动模拟会话。");

    private void Frozen(SimulationFault fault, string message) =>
        _logger.Warning(Category, FrozenEventId, $"{message}：{fault.Code}/{fault.Phase}。",
            new Dictionary<string, object?>
            {
                ["fault_code"] = fault.Code,
                ["fault_phase"] = fault.Phase.ToString()
            });

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

    /// <summary>将省略的步数解析为一步，或接受唯一的正 UInt64 十进制参数。</summary>
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

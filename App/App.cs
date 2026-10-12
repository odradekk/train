
using Godot;
using Train.App.Checks;
using Train.App.Console;
using Train.Content;
using Train.App.Logging;
using Train.Core.Simulation;
using Train.Ui;

namespace Train.App;

public partial class App : Node
{
    public const string ReloadContentAction = "reload_content";

    private const double SimulationFrameBudgetSeconds = 0.005;

    internal const string SimulationFaultEventId = "simulation_fault";

    private TrainLogger? _logger;
    private ContentReloader? _reloader;
    private CheckArguments? _check;
    private LoggingCheck? _loggingCheck;
    private ContentCheck? _contentCheck;
    private SimulationCheck? _simulationCheck;
    private SimulationDriver? _simulation;
    private int _mainThread;
    private bool _insideSimulation;
    private long _attachedAt;
    private bool _firstSimulationFrame;
    private ContentReloadResult? _lastReload;
    private bool _checkFinished;
    private bool _quitRequested;

    public event Action<AdvanceResult>? SimulationAdvanced;

    public event Action<SubmissionResult.Completed>? SimulationCommandCompleted;

    public override void _Ready()
    {
        _mainThread = System.Environment.CurrentManagedThreadId;
        GetTree().AutoAcceptQuit = false;
        GetNode<MainMenu>("MainMenu").QuitRequested += RequestQuit;
        try
        {
            _check = CheckArguments.Parse(OS.GetCmdlineUserArgs());
            if (_check?.Name == "simulation-host" && !OS.IsDebugBuild())
                throw new InvalidOperationException("simulation-host 检查需要开发构建（Debug），以验证 sim 调试命令。");
            var userData = _check?.UserDataDirectory ?? OS.GetUserDataDir();
            var buffer = new RingBufferLogSink();
            _logger = TrainLogger.StartSession(Path.Combine(userData, "logs"),
                OS.IsDebugBuild() ? LogLevel.Debug : LogLevel.Info, new GodotLogOutput(), buffer,
                readEnvironmentVariable: _check is null);
            IContentSource source = new GodotContentSource(_logger, userData,
                createUserDirectories: _check is null);
            if (_check?.Name == "content")
            {
                _contentCheck = new ContentCheck(_logger, userData, _check.ContentRoot!);
                source = _contentCheck;
            }
            _reloader = new ContentReloader(source, _logger);
            var console = new GameConsole();
            Action<IReadOnlyList<string>>? simulationCommands = null;
            if (OS.IsDebugBuild())
                simulationCommands = new SimulationConsoleCommands(this, _logger).Execute;
            console.Initialize(buffer, new ConsoleCommandDispatcher(
                _logger, buffer.Clear, RequestQuit, ReloadContent, OS.IsDebugBuild(), simulationCommands));
            AddChild(console);
            ReloadContent();
            if (_check?.Name == "logging")
                _loggingCheck = new LoggingCheck(_logger);
            if (_check?.Name == "simulation-host")
                _simulationCheck = new SimulationCheck(this, console, _logger, buffer);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"APP_CHECK_FAILED {_check?.Name ?? "startup"}: {exception.Message}");
            _checkFinished = true;
            GetTree().Quit(1);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            RequestQuit();
    }

    public void RequestQuit()
    {
        _quitRequested = true;
        GetTree().Quit();
    }

    private void ReloadContent() => _lastReload = _reloader!.Reload();

    public void AttachSession(SimulationSession session, SimulationStateCodec states)
    {
        EnsureSimulationMutation();
        _simulation = new SimulationDriver(session, states);
        _attachedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _firstSimulationFrame = true;
    }

    public void DetachSession()
    {
        EnsureSimulationMutation();
        _simulation = null;
        _firstSimulationFrame = false;
    }

    public SimulationStatus? GetSimulationStatus()
    {
        EnsureSimulationThread();
        return _simulation?.GetStatus();
    }

    public bool SetSimulationPaused(bool paused)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return false;
        _simulation.SetPaused(paused);
        return true;
    }

    public bool SetSimulationSpeed(SimulationSpeed speed)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return false;
        _simulation.SetSpeed(speed);
        return true;
    }

    public AdvanceResult? StepSimulation(ulong steps)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return null;
        var result = RunSimulation(() => _simulation.Step(steps));
        ReportSimulationFault(result.Diagnostic);
        SimulationAdvanced?.Invoke(result);
        return result;
    }

    public SubmissionResult SubmitCommand(CoreCommand command, CommandSource source)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return new SubmissionResult.NotAccepted("simulation_session_unavailable");
        var result = RunSimulation(() => _simulation.SubmitCommand(command, source));
        if (result is SubmissionResult.Completed completed)
        {
            ReportSimulationFault(completed.Diagnostic);
            SimulationCommandCompleted?.Invoke(completed);
        }
        return result;
    }

    public AuthoritativeCheckpoint? CreateSimulationCheckpoint()
    {
        EnsureSimulationThread();
        return _simulation?.CreateCheckpoint();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (OS.IsDebugBuild() && _reloader is not null &&
            @event.IsActionPressed(ReloadContentAction) && !@event.IsEcho())
        {
            ReloadContent();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_checkFinished)
            return;
        try
        {
            if (_simulation is not null)
            {
                // 挂接可能发生在较长的生成帧内；第一帧只累计会话实际存在的时间。
                var simulationDelta = delta;
                if (_firstSimulationFrame)
                {
                    simulationDelta = Math.Min(delta, System.Diagnostics.Stopwatch.GetElapsedTime(_attachedAt).TotalSeconds);
                    _firstSimulationFrame = false;
                }
                var result = RunSimulation(() => _simulation.AdvanceFrame(simulationDelta, SimulationFrameBudgetSeconds));
                ReportSimulationFault(result.Diagnostic);
                SimulationAdvanced?.Invoke(result);
            }
            var drained = _logger?.DrainGodotQueue() ?? 0;
            if (_check is null)
                return;
            bool? passed = _check.Name switch
            {
                "logging" => _loggingCheck!.ObserveDrain(drained),
                "content" => _contentCheck!.Advance(_reloader!, _lastReload!, ReloadContent),
                "content-export" => StartupChecks.ContentExport(_reloader!, _lastReload!, _logger!),
                "mods-export" => StartupChecks.ModsExport(_reloader!, _lastReload!, _check.UserDataDirectory),
                "main-menu" => StartupChecks.MainMenu(GetNode<MainMenu>("MainMenu"), () => _quitRequested),
                "simulation-host" => _simulationCheck!.ObserveFrame(delta),
                _ => false
            };
            if (passed is null)
                return;
            _checkFinished = true;
            GD.Print(passed.Value ? $"APP_CHECK_PASSED {_check.Name}" : $"APP_CHECK_FAILED {_check.Name}");
            GetTree().Quit(passed.Value ? 0 : 1);
        }
        catch (Exception exception)
        {
            if (_check is null)
                throw;
            _checkFinished = true;
            GD.PrintErr($"APP_CHECK_FAILED {_check.Name}: {exception.Message}");
            GetTree().Quit(1);
        }
    }

    public override void _ExitTree()
    {
        _simulation = null;
        _loggingCheck?.Join();
        _logger?.Dispose();
        _logger = null;
        _reloader = null;
        if (_loggingCheck is not null)
            GD.Print("SMOKE_LOGGER_DISPOSED");
    }

    private T RunSimulation<T>(Func<T> operation)
    {
        EnsureSimulationMutation();
        _insideSimulation = true;
        try { return operation(); }
        finally { _insideSimulation = false; }
    }

    private void ReportSimulationFault(SimulationFaultDiagnostic? diagnostic)
    {
        if (diagnostic is null)
            return;
        var fault = diagnostic.Fault;
        var context = fault.Context;
        _logger!.Error(SimulationConsoleCommands.Category, SimulationFaultEventId,
            "核心模拟发生内部故障，已冻结在最后完整边界。",
            new Dictionary<string, object?>
            {
                ["fault_code"] = fault.Code,
                ["fault_phase"] = fault.Phase.ToString(),
                ["operation"] = context?.Operation.ToString(),
                ["attempted_transaction_id"] = context?.TransactionId,
                ["attempted_completed_steps"] = context?.Position.CompletedSteps,
                ["attempted_sequence"] = context?.Position.Sequence,
                ["attempted_command_id"] = context?.CommandId,
                ["entity_id"] = fault.EntityId
            }, diagnostic.Exception);
    }

    private void EnsureSimulationThread()
    {
        if (_mainThread == 0 || System.Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("App 模拟服务只能由 Godot 主线程调用。");
    }

    private void EnsureSimulationMutation()
    {
        EnsureSimulationThread();
        if (_insideSimulation)
            throw new InvalidOperationException("模拟计算期间不能重入 App 修改入口。");
    }
}

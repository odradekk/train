
using Godot;
using Train.App.Console;
using Train.Content;
using Train.Content.Voxels;
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
    private SimulationDriver? _simulation;
    private int _mainThread;
    private bool _insideSimulation;
    private long _attachedAt;
    private bool _firstSimulationFrame;
    private GameUi? _ui;

    internal TrainLogger Logger => _logger!;
    internal ContentSnapshot? CurrentContent => _reloader?.Current;
    internal Action QuitAction { get; set; }
    internal Func<TrainLogger, HostCoordinator>? CreateHost { get; set; }
    internal HostCoordinator? Host { get; private set; }

    public App() => QuitAction = () => GetTree().Quit();

    public event Action<AdvanceResult>? SimulationAdvanced;

    public event Action<SubmissionResult.Completed>? SimulationCommandCompleted;

    public override void _Ready()
    {
        _mainThread = System.Environment.CurrentManagedThreadId;
        GetTree().AutoAcceptQuit = false;
        GetNode<MainMenu>("MainMenu").QuitRequested += RequestQuit;
        try
        {
            var userData = OS.GetUserDataDir();
            var buffer = new RingBufferLogSink();
            _logger = TrainLogger.StartSession(Path.Combine(userData, "logs"),
                OS.IsDebugBuild() ? LogLevel.Debug : LogLevel.Info, new GodotLogOutput(), buffer);
            if (CreateHost is not null)
                Host = CreateHost(_logger);
            else
            {
                var source = new GodotContentSource(_logger, userData);
                _reloader = new ContentReloader(source, _logger);
            }
            _ui = new GameUi { Name = "GameUi" };
            _ui.Initialize(GetNode<MainMenu>("MainMenu"), Host, _logger);
            _ui.NewGameRequested += NewGame;
            AddChild(_ui);
            var console = new GameConsole();
            Action<IReadOnlyList<string>>? simulationCommands = null;
            Action<IReadOnlyList<string>>? trainCommands = null;
            if (OS.IsDebugBuild())
                simulationCommands = Host is null ? new LegacySimulationConsoleCommands(this, _logger).Execute
                    : new SimulationConsoleCommands(Host, _logger).Execute;
            if (OS.IsDebugBuild() && Host is not null)
                trainCommands = new TrainConsoleCommands(Host, _logger, _ui.ShowError).Execute;
            console.Initialize(buffer, new ConsoleCommandDispatcher(
                _logger, buffer.Clear, RequestQuit, () => ReloadContent(Train.Core.Contracts.CommandSource.Console), OS.IsDebugBuild(), simulationCommands, trainCommands));
            AddChild(console);
            if (Host is not null)
            {
                Host.SetFocused(GetWindow().HasFocus());
                if (!Host.IsBusy) Host.BeginReload(Train.Core.Contracts.CommandSource.UserInterface);
                return;
            }
            var loaded = _reloader!.Reload();
            if (OS.GetCmdlineUserArgs().Contains("--smoke", StringComparer.Ordinal))
            {
                var grain = loaded.Snapshot?.Voxels.GetValueOrDefault(new VoxelDefinitionId("core", "grain"));
                var passed = loaded.IsCommitted && grain is not null && grain.PresentationImages.Count > 0 &&
                    grain.PresentationImages.Values.All(image => image.Width > 0 && image.Height > 0);
                GetTree().Quit(passed ? 0 : 1);
            }
        }
        catch (Exception exception)
        {
            GD.PrintErr($"Application startup failed: {exception}");
            GetTree().Quit(1);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            RequestQuit();
        else if (what == NotificationApplicationFocusIn)
            Host?.SetFocused(true);
        else if (what == NotificationApplicationFocusOut)
            Host?.SetFocused(false);
    }

    public void RequestQuit()
    {
        QuitAction();
    }

    private void ReloadContent(Train.Core.Contracts.CommandSource source)
    {
        if (Host is null) _reloader!.Reload();
        else if (Host.BeginReload(source).Problem is { Code: "busy" } problem)
            _logger!.Warning("content", "content_reload_failed", problem.Code,
                new Dictionary<string, object?> { ["code"] = problem.Code, ["field"] = problem.Field });
    }

    private void NewGame(Train.Content.Contracts.GenerationProfileId profile, ulong seed)
    {
        var version = ProjectSettings.GetSetting("application/config/version").AsString();
        var runtime = new Train.Core.Contracts.RuntimeIdentity(version, version, OS.GetName(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        _ui!.NewGameStarted(Host!.BeginNewGame(profile, seed, "stage1-world-v1",
            new(runtime, OS.IsDebugBuild(), 1)));
    }

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

    public bool SetSimulationSpeed(Train.Core.Simulation.SimulationSpeed speed)
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
        if (OS.IsDebugBuild() && (_reloader is not null || Host is not null) &&
            @event.IsActionPressed(ReloadContentAction) && !@event.IsEcho())
        {
            ReloadContent(Train.Core.Contracts.CommandSource.UserInterface);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        Host?.ProcessFrame(delta);
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
        _logger?.DrainGodotQueue();
    }

    public override void _ExitTree()
    {
        Host?.Dispose();
        Host = null;
        _simulation = null;
        _logger?.Dispose();
        _logger = null;
        _reloader = null;
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

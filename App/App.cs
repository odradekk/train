// Train.App 管理 Godot 应用服务和主线程上的模拟会话接入。
// 主菜单与控制台共用宿主回调；只有挂接会话后才按帧驱动完整模拟步。

using Godot;
using Train.App.Checks;
using Train.Core.Console;
using Train.Core.Content;
using Train.Core.Logging;
using Train.Core.Simulation;
using Train.Ui;

namespace Train.App;

/// <summary>在 Godot 主线程管理应用服务，驱动已挂接模拟会话并连接主菜单和控制台。</summary>
/// <remarks>拥有日志会话；退出场景树时取出待输出事件并释放持久日志。</remarks>
/// <example>正常启动显示只有“退出”的主菜单；传入 --check=main-menu 会检查该入口后退出。</example>
public partial class App : Node
{
    /// <summary>Debug 构建中的内容重载动作，与项目输入映射共用此名称。</summary>
    public const string ReloadContentAction = "reload_content";

    /// <summary>#221 结对确认的暂定每帧模拟工作预算，单位秒；#223 可按真实世界测量调整。</summary>
    /// <remarks>5 ms 只限制在完整步之间继续工作，不改变模拟步长，不丢弃剩余步骤。</remarks>
    private const double SimulationFrameBudgetSeconds = 0.005;

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

    /// <summary>在完整边界报告每帧或手动推进结果；事件保持核心确定的顺序，也可能没有完成步。</summary>
    /// <remarks>回调在核心计算结束后由主线程调用，消费者不得把事件作为未提交状态。</remarks>
    public event Action<AdvanceResult>? SimulationAdvanced;

    /// <summary>在完整边界报告执行、预期拒绝或故障的命令结果，保留原始输入来源。</summary>
    public event Action<SubmissionResult.Completed>? SimulationCommandCompleted;

    /// <summary>创建内容与日志服务，把退出、清屏和重载回调交给界面。</summary>
    public override void _Ready()
    {
        _mainThread = System.Environment.CurrentManagedThreadId;
        GetTree().AutoAcceptQuit = false;
        GetNode<MainMenu>("MainMenu").QuitRequested += RequestQuit;
        try
        {
            _check = CheckArguments.Parse(OS.GetCmdlineUserArgs());
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

    /// <summary>处理窗口关闭请求，使窗口、按钮和控制台使用同一退出路径。</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            RequestQuit();
    }

    /// <summary>请求退出应用，日志会话由场景树退出回调释放。</summary>
    public void RequestQuit()
    {
        _quitRequested = true;
        GetTree().Quit();
    }

    /// <summary>重新扫描内容来源，并保存本次发布结果供检查观察。</summary>
    private void ReloadContent() => _lastReload = _reloader!.Reload();

    /// <summary>在主线程整体挂接已经创建或恢复成功的会话，建立新的主机调度和测量。</summary>
    /// <param name="session">已验证的完整会话，已有状态和命令日志不会重建。</param>
    /// <param name="states">与该会话匹配的领域状态格式。</param>
    /// <remarks>默认未暂停、1×；创建新驱动成功后才替换当前绑定。世界生成和玩家存档流程由对应工单负责。</remarks>
    public void AttachSession(SimulationSession session, SimulationStateCodec states)
    {
        EnsureSimulationMutation();
        _simulation = new SimulationDriver(session, states);
        _attachedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _firstSimulationFrame = true;
    }

    /// <summary>移除当前会话及其主机余量，不修改曾返回的完整状态快照。</summary>
    public void DetachSession()
    {
        EnsureSimulationMutation();
        _simulation = null;
        _firstSimulationFrame = false;
    }

    /// <summary>查询活动会话的完整状态、模拟时间和主机观察值；无会话时返回 null。</summary>
    /// <returns>当前绑定的只读状态；没有绑定时为 null。</returns>
    public SimulationStatus? GetSimulationStatus()
    {
        EnsureSimulationThread();
        return _simulation?.GetStatus();
    }

    /// <summary>设置活动会话的主机暂停状态；无会话时返回 false，不建立模拟状态。</summary>
    /// <param name="paused">true 停止自动推进，期间仍可提交命令。</param>
    /// <returns>存在活动会话且已设置时为 true；无会话时为 false。</returns>
    public bool SetSimulationPaused(bool paused)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return false;
        _simulation.SetPaused(paused);
        return true;
    }

    /// <summary>设置活动会话的目标倍速；无会话时返回 false。</summary>
    /// <param name="speed">1/2/4/8 档位，不改变权威状态或步长。</param>
    /// <returns>存在活动会话且已设置时为 true；无会话时为 false。</returns>
    public bool SetSimulationSpeed(SimulationSpeed speed)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return false;
        _simulation.SetSpeed(speed);
        return true;
    }

    /// <summary>在主机暂停时同步推进指定完整步数并报告已提交结果；无会话时返回 null。</summary>
    /// <param name="steps">请求步数，零不产生事务。</param>
    /// <returns>同一会话实际完成的步数、最后完整状态与事件；无会话时为 null。</returns>
    /// <exception cref="InvalidOperationException">活动会话未暂停。</exception>
    public AdvanceResult? StepSimulation(ulong steps)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return null;
        var result = RunSimulation(() => _simulation.Step(steps));
        SimulationAdvanced?.Invoke(result);
        return result;
    }

    /// <summary>经活动会话的统一入口提交核心命令，成功发布完整结果后通知观察者。</summary>
    /// <param name="command">已解析的领域命令。</param>
    /// <param name="source">UI、控制台或自动化来源。</param>
    /// <returns>核心原始提交结果；无会话时拒收且不分配身份。</returns>
    public SubmissionResult SubmitCommand(CoreCommand command, CommandSource source)
    {
        EnsureSimulationMutation();
        if (_simulation is null)
            return new SubmissionResult.NotAccepted("simulation_session_unavailable");
        var result = RunSimulation(() => _simulation.SubmitCommand(command, source));
        if (result is SubmissionResult.Completed completed)
            SimulationCommandCompleted?.Invoke(completed);
        return result;
    }

    /// <summary>从活动会话的完整边界创建权威检查点；无会话时返回 null。</summary>
    /// <returns>同一边界的规范权威文档、位置和摘要；无会话时为 null。</returns>
    public AuthoritativeCheckpoint? CreateSimulationCheckpoint()
    {
        EnsureSimulationThread();
        return _simulation?.CreateCheckpoint();
    }

    /// <summary>在 Debug 构建中响应内容重载快捷键，并阻止事件继续传递。</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (OS.IsDebugBuild() && _reloader is not null &&
            @event.IsActionPressed(ReloadContentAction) && !@event.IsEcho())
        {
            ReloadContent();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>在主线程按帧驱动活动会话，输出工作线程日志，再观察已选中的启动检查。</summary>
    /// <param name="delta">引擎真实帧间隔，单位秒；无会话时不累计模拟时间。</param>
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

    /// <summary>等待检查自己的工作线程结束，释放日志并输出日志检查的释放标记。</summary>
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

    /// <summary>在计算期间保护会话绑定，所有观察事件在该保护结束后通知。</summary>
    private T RunSimulation<T>(Func<T> operation)
    {
        EnsureSimulationMutation();
        _insideSimulation = true;
        try { return operation(); }
        finally { _insideSimulation = false; }
    }

    /// <summary>要求模拟服务从该 App 的 Godot 主线程访问。</summary>
    private void EnsureSimulationThread()
    {
        if (_mainThread == 0 || System.Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("App 模拟服务只能由 Godot 主线程调用。");
    }

    /// <summary>禁止计算阶段更换会话或重入宿主修改，完整状态查询仍可进行。</summary>
    private void EnsureSimulationMutation()
    {
        EnsureSimulationThread();
        if (_insideSimulation)
            throw new InvalidOperationException("模拟计算期间不能重入 App 修改入口。");
    }
}

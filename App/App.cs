// Train.App 管理 Godot 应用的启动、内容服务与日志生命周期。
// 主菜单和控制台共用宿主回调，检查入口不创建旧玩法会话。

using Godot;
using Train.App.Checks;
using Train.Content.Godot;
using Train.Core.Console;
using Train.Core.Content;
using Train.Core.Logging;
using Train.Ui;

namespace Train.App;

/// <summary>在 Godot 主线程启动内容与诊断服务，并连接主菜单和控制台。</summary>
/// <remarks>拥有日志会话；退出场景树时取出待输出事件并释放持久日志。</remarks>
/// <example>正常启动显示只有“退出”的主菜单；传入 --check=main-menu 会检查该入口后退出。</example>
public partial class App : Node
{
    /// <summary>Debug 构建中的内容重载动作，与项目输入映射共用此名称。</summary>
    public const string ReloadContentAction = "reload_content";

    private TrainLogger? _logger;
    private ContentReloader? _reloader;
    private CheckArguments? _check;
    private LoggingCheck? _loggingCheck;
    private ContentCheck? _contentCheck;
    private ContentReloadResult? _lastReload;
    private bool _checkFinished;
    private bool _quitRequested;

    /// <summary>创建内容与日志服务，把退出、清屏和重载回调交给界面。</summary>
    public override void _Ready()
    {
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
            console.Initialize(buffer, new ConsoleCommandDispatcher(
                _logger, buffer.Clear, RequestQuit, ReloadContent, OS.IsDebugBuild()));
            AddChild(console);
            ReloadContent();
            if (_check?.Name == "logging")
                _loggingCheck = new LoggingCheck(_logger);
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

    /// <summary>在主线程输出工作线程日志，并推进已选中的启动检查。</summary>
    /// <param name="delta">引擎帧间隔，单位为秒；此宿主尚不推进核心模拟。</param>
    public override void _Process(double delta)
    {
        var drained = _logger?.DrainGodotQueue() ?? 0;
        if (_check is null || _checkFinished)
            return;
        try
        {
            bool? passed = _check.Name switch
            {
                "logging" => _loggingCheck!.ObserveDrain(drained),
                "content" => _contentCheck!.Advance(_reloader!, _lastReload!, ReloadContent),
                "content-export" => StartupChecks.ContentExport(_reloader!, _lastReload!, _logger!),
                "mods-export" => StartupChecks.ModsExport(_reloader!, _lastReload!, _check.UserDataDirectory),
                "main-menu" => StartupChecks.MainMenu(GetNode<MainMenu>("MainMenu"), () => _quitRequested),
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
            _checkFinished = true;
            GD.PrintErr($"APP_CHECK_FAILED {_check.Name}: {exception.Message}");
            GetTree().Quit(1);
        }
    }

    /// <summary>等待检查自己的工作线程结束，释放日志并输出日志检查的释放标记。</summary>
    public override void _ExitTree()
    {
        _loggingCheck?.Join();
        _logger?.Dispose();
        _logger = null;
        _reloader = null;
        if (_loggingCheck is not null)
            GD.Print("SMOKE_LOGGER_DISPOSED");
    }
}

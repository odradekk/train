using Godot;
using Train.App.Assets;
using Train.App.Composition;
using Train.App.Console;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.App;

public partial class App : Node
{
    public const string ReloadContentAction = "reload_content";

    private TrainLogger? _logger;
    private GodotMainThreadDispatcher? _dispatcher;
    private bool _smoke;

    internal TrainLogger Logger => _logger!;
    internal Action QuitAction { get; set; }
    internal Func<TrainLogger, HostCoordinator>? CreateHost { get; set; }
    internal Func<Train.Scenes.Contracts.IScenesFactory>? CreateScenes { get; set; }
    internal HostCoordinator? Host { get; private set; }
    internal WorldSceneController? WorldScenes { get; private set; }
    internal SaveService? Saves { get; private set; }

    public App() => QuitAction = () => GetTree().Quit();

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;
        GetNode<MainMenu>("MainMenu").QuitRequested += RequestQuit;
        try
        {
            var userData = OS.GetUserDataDir();
            var buffer = new RingBufferLogSink();
            _logger = TrainLogger.StartSession(Path.Combine(userData, "logs"),
                OS.IsDebugBuild() ? LogLevel.Debug : LogLevel.Info, new GodotLogOutput(), buffer);
            _dispatcher = new();
            Host = CreateHost?.Invoke(_logger) ?? HostComposition.CreateHost(_logger, userData, _dispatcher);
            Saves = new SaveService(Host, new FileSaveStore(userData));
            GetNode<MainMenu>("MainMenu").ConfigureSaves(Saves);
            WorldScenes = new(Host, CreateScenes?.Invoke() ?? HostComposition.CreateScenes(), _logger);
            AddChild(WorldScenes.View);
            var console = new GameConsole();
            Action<IReadOnlyList<string>>? simulationCommands = OS.IsDebugBuild()
                ? new SimulationConsoleCommands(Host, _logger).Execute : null;
            console.Initialize(buffer, new ConsoleCommandDispatcher(
                _logger, buffer.Clear, RequestQuit, () => ReloadContent(CommandSource.Console), OS.IsDebugBuild(), simulationCommands));
            AddChild(console);
            _smoke = OS.GetCmdlineUserArgs().Contains("--smoke", StringComparer.Ordinal);
            Host.SetFocused(GetWindow().HasFocus());
            if (!Host.IsBusy) Host.BeginReload(CommandSource.UserInterface);
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
        if (Saves is null) QuitAction();
        else GetNode<MainMenu>("MainMenu").ShowSaveFailure(Saves.RequestLeave(QuitAction));
    }

    private void ReloadContent(CommandSource source)
    {
        if (Host!.BeginReload(source).Problem is { Code: "busy" } problem)
            _logger!.Warning("content", "content_reload_failed", problem.Code,
                new Dictionary<string, object?> { ["code"] = problem.Code, ["field"] = problem.Field });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (OS.IsDebugBuild() && Host is not null &&
            @event.IsActionPressed(ReloadContentAction) && !@event.IsEcho())
        {
            ReloadContent(CommandSource.UserInterface);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        _dispatcher?.Pump();
        Saves?.Process();
        Host?.ProcessFrame(delta);
        // headless 的虚拟窗口始终报告最小化；它仍需执行资源和显示边界检查。
        WorldScenes?.ProcessFrame(delta, DisplayServer.GetName() != "headless" && GetWindow().Mode == Window.ModeEnum.Minimized);
        _logger?.DrainGodotQueue();
        if (_smoke && Host is { IsBusy: false }) CompleteSmoke();
    }

    private void CompleteSmoke()
    {
        _smoke = false;
        var content = Host!.Content;
        var display = content?.FindVoxelDisplay(new("core:staple-food"));
        var passed = display is ContentLookup<VoxelDisplayProjection>.Found food &&
            content!.FindAsset(food.Value.Icon) is ContentLookup<ContentAsset>.Found
                { Value.Metadata: AssetMetadata.Png { Width: > 0, Height: > 0 } } &&
            content.FindGood(new GoodId.Voxel(new("core:staple-food"))) is
                ContentLookup<GoodPresentation>.Found;
        _logger!.Info("application", "application_smoke_summary", "生产装配冒烟结束。",
            new Dictionary<string, object?>
            {
                ["passed"] = passed,
                ["packages"] = content?.Packages.Where(package => package.State == PackageState.Available)
                    .Select(package => package.Metadata!.Id.Value).ToArray() ?? [],
                ["voxels"] = content?.Voxels.Select(voxel => voxel.Definition.Value).ToArray() ?? []
            });
        GetTree().Quit(passed ? 0 : 1);
    }

    public override void _ExitTree()
    {
        Saves?.Dispose();
        Saves = null;
        WorldScenes?.Dispose();
        WorldScenes = null;
        Host?.Dispose();
        Host = null;
        _dispatcher?.Dispose();
        _dispatcher = null;
        _logger?.Dispose();
        _logger = null;
    }
}

using Godot;
using Train.App.Assets;
using Train.App.Composition;
using Train.App.Console;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.App.Recording;
using Train.App.Development;
using Train.App.Measurement;
using Train.App.Editing;
using Train.Ui;

namespace Train.App;

public partial class App : Node
{
    public const string ReloadContentAction = "reload_content";
    internal const string ExampleSavePath = "res://examples/basic-through/save.json";

    private TrainLogger? _logger;
    private GodotMainThreadDispatcher? _dispatcher;
    private bool _smoke;
    private MeasurementRun? _measurement;
    private Guid? _smokeExampleId;

    internal TrainLogger Logger => _logger!;
    internal Action QuitAction { get; set; }
    internal Func<TrainLogger, HostCoordinator>? CreateHost { get; set; }
    internal Func<Train.Scenes.Contracts.IScenesFactory>? CreateScenes { get; set; }
    internal HostCoordinator? Host { get; private set; }
    internal WorldSceneController? WorldScenes { get; private set; }
    internal SaveService? Saves { get; private set; }
    internal RecordingService? Recording { get; private set; }
    internal DeveloperTools? Development { get; private set; }
    internal EditorService? Editor { get; private set; }

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
            GetNode<MainMenu>("MainMenu").ExampleRequested += () => StartExample();
            Recording = new RecordingService(Host, new FileSaveStore(userData));
            var scenesFactory = CreateScenes?.Invoke() ?? HostComposition.CreateScenes();
            Editor = new(Host, scenesFactory, new FileSaveStore(userData), _logger);
            Saves.CloseEditor = Editor.RequestClose;
            WorldScenes = new(Host, scenesFactory, _logger);
            Editor.Changed += () =>
            {
                if (Editor.IsOpen && WorldScenes.View.Visible) WorldScenes.ReturnToMap();
            };
            AddChild(WorldScenes.View);
            var icons = new GoodsIconCache { Name = "GoodsIcons" };
            icons.Initialize(scenesFactory, _dispatcher);
            icons.Diagnostic += message => _logger.Warning("ui", "goods_icon_unavailable", message);
            AddChild(icons);
            var ui = new GameUi { Name = "GameUi" };
            ui.Initialize(GetNode<MainMenu>("MainMenu"), Host, _logger, WorldScenes, Recording, icons, Editor);
            AddChild(ui);
            var console = new GameConsole();
            Action<IReadOnlyList<string>>? simulationCommands = null;
            Action<IReadOnlyList<string>>? trainCommands = null;
            Action<IReadOnlyList<string>>? lightingCommands = null;
            Action<IReadOnlyList<string>>? developmentCommands = null;
            if (OS.IsDebugBuild())
            {
                Development = new(Host, Saves);
                var inspector = new DeveloperInspector();
                AddChild(inspector);
                inspector.Configure(Development, WorldScenes);
                developmentCommands = inspector.Execute;
                simulationCommands = new SimulationConsoleCommands(Host, _logger, Development).Execute;
                trainCommands = new TrainConsoleCommands(Host, _logger, ui.ShowError).Execute;
                lightingCommands = new LightingConsoleCommands(_logger, () => WorldScenes?.View.Lighting,
                    mode => WorldScenes!.View.SetLighting(mode)).Execute;
            }
            console.Initialize(buffer, new ConsoleCommandDispatcher(
                _logger, buffer.Clear, RequestQuit, () => ReloadContent(CommandSource.Console), OS.IsDebugBuild(), simulationCommands,
                train: trainCommands, lighting: lightingCommands, development: developmentCommands));
            AddChild(console);
            _smoke = OS.GetCmdlineUserArgs().Contains("--smoke", StringComparer.Ordinal);
            Host.SetFocused(GetWindow().HasFocus());
            var arguments = OS.GetCmdlineUserArgs();
            if (arguments.Any(argument => argument.StartsWith("--measure=", StringComparison.Ordinal)))
            {
                _measurement = new(this, arguments);
                _measurement.Start();
            }
            else if (!Host.IsBusy) Host.BeginReload(CommandSource.UserInterface);
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
        if (Saves is null) FinishQuit();
        else GetNode<MainMenu>("MainMenu").ShowSaveFailure(Saves.RequestLeave(FinishQuit));
    }

    internal SaveFailure? StartExample(string path = ExampleSavePath)
    {
        SaveFailure? failure;
        try
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file is null) failure = new("example-read-failed", $"无法读取示例存档：{Godot.FileAccess.GetOpenError()}。");
            else
            {
                var document = SaveContainer.Decode(file.GetAsText());
                if (_smoke) _smokeExampleId = document.Header.Id;
                failure = Saves!.StartExample(document);
            }
        }
        catch (SaveFormatException error)
        { failure = new(error.Code, $"无法读取示例存档：{error.Message}"); }
        GetNode<MainMenu>("MainMenu").ShowSaveFailure(failure);
        return failure;
    }

    private void FinishQuit()
    {
        if (Recording is null) QuitAction();
        else
        {
            if (Host!.LockLeaving() is { } problem)
            { GetNode<MainMenu>("MainMenu").ShowSaveFailure(new(problem.Code, "正在处理其他操作。")); return; }
            Recording.Close(() => { Host.UnlockLeaving(); QuitAction(); });
        }
    }

    private void ReloadContent(CommandSource source)
    {
        void Reload()
        {
            if (Host!.BeginReload(source).Problem is { } problem)
                _logger!.Warning("content", "content_reload_failed", CommandDiagnostics.Describe(problem),
                    new Dictionary<string, object?> { ["code"] = problem.Code, ["field"] = problem.Field });
        }
        if (Editor is { IsOpen: true }) Editor.RequestClose(Reload);
        else Reload();
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
        Editor?.Process();
        _measurement?.OnSimulationFrame(Host!.SimulationFrameSeconds);
        Development?.Process();
        Recording?.Process();
        // headless 的虚拟窗口始终报告最小化；它仍需执行资源和显示边界检查。
        WorldScenes?.ProcessFrame(delta, DisplayServer.GetName() != "headless" && GetWindow().Mode == Window.ModeEnum.Minimized);
        _logger?.DrainGodotQueue();
        if (_smoke && Host is { IsBusy: false } && Saves is { IsBusy: false })
        {
            if (_smokeExampleId is not null || StartExample() is not null) CompleteSmoke();
        }
    }

    private void CompleteSmoke()
    {
        _smoke = false;
        var content = Host!.Content;
        var display = content?.FindVoxelDisplay(new("core:staple-food"));
        var status = Host.GetStatus();
        var save = Saves!.Status;
        var exampleVisible = GetNode<MainMenu>("MainMenu").GetNode<Button>("%Example").Visible;
        var restored = status is { IsReplay: false } && save is not null;
        var passed = display is ContentLookup<VoxelDisplayProjection>.Found food &&
            content!.FindAsset(food.Value.Icon) is ContentLookup<ContentAsset>.Found
                { Value.Metadata: AssetMetadata.Png { Width: > 0, Height: > 0 } } &&
            content.FindGood(new GoodId.Voxel(new("core:staple-food"))) is
                ContentLookup<GoodPresentation>.Found && restored && _smokeExampleId is not null &&
            save!.Identity.Id != _smokeExampleId && status!.View.Position == new SimulationPosition(200, 0) &&
            status.IsPaused && save.Unsaved && !exampleVisible;
        _logger!.Info("application", "application_smoke_summary", "生产装配冒烟结束。",
            new Dictionary<string, object?>
            {
                ["passed"] = passed,
                ["example_source_id"] = _smokeExampleId?.ToString(),
                ["save_id"] = save?.Identity.Id.ToString(),
                ["restored"] = restored,
                ["completed_steps"] = status?.View.Position.CompletedSteps,
                ["sequence"] = status?.View.Position.Sequence,
                ["paused"] = status?.IsPaused == true,
                ["unsaved"] = save?.Unsaved == true,
                ["example_button_visible"] = exampleVisible,
                ["packages"] = content?.Packages.Where(package => package.State == PackageState.Available)
                    .Select(package => package.Metadata!.Id.Value).ToArray() ?? [],
                ["voxels"] = content?.Voxels.Select(voxel => voxel.Definition.Value).ToArray() ?? []
            });
        GetTree().Quit(passed ? 0 : 1);
    }

    public override void _ExitTree()
    {
        _measurement?.Dispose();
        _measurement = null;
        Development?.Dispose();
        Development = null;
        Recording?.Dispose();
        Recording = null;
        Editor?.Dispose();
        Editor = null;
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

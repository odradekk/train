using Godot;
using Train.Content;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train;

public partial class Main : Node
{
    public const string ReloadContentAction = "reload_content";

    public TrainLogger? Logger => _logger;

    private const string LoggingSmokeEnvironmentVariable = "TRAIN_LOG_SMOKE";
    private const string LoggingSmokeWorkerEventId = "smoke_worker_event";
    private const string LoggingSmokeProcessMarker = "SMOKE_PROCESS_DRAINED";
    private const string LoggingSmokeDisposedMarker = "SMOKE_LOGGER_DISPOSED";
    private const string ContentSmokeEnvironmentVariable = "TRAIN_CONTENT_SMOKE";
    private const string ContentSmokeAssertDomainEmptyEnvironmentVariable = "CONTENT_SMOKE_ASSERT_DOMAIN_EMPTY";
    private const string ContentExportSmokeEnvironmentVariable = "TRAIN_CONTENT_EXPORT_SMOKE";
    private const string ModsExportSmokeEnvironmentVariable = "TRAIN_MODS_EXPORT_SMOKE";
    private const string ModsSmokeUserDataEnvironmentVariable = "TRAIN_MODS_SMOKE_USER_DATA";
    private const string SmokeVoxelsPath = "res://builtin/core/resources/smoke-temp.voxels.json";

    private TrainLogger? _logger;
    private ContentReloader? _reloader;
    private bool _smoke;
    private bool _contentSmoke;
    private bool _contentExportSmoke;
    private bool _modsExportSmoke;
    private string? _modsSmokeUserData;
    private bool _ownsModsSmokeUserData;
    private volatile bool _smokeWorkerLogged;
    private bool _smokeDrainedFromProcess;
    private bool _smokeQuitRequested;
    private bool _contentSmokeFinished;
    private ContentSmokePhase _contentSmokePhase;
    private long _contentSmokeVersionBeforeInput;
    private int _contentSmokeInputFrames;

    private enum ContentSmokePhase
    {
        Idle,
        WriteAndFire,
        AwaitInputReload,
        Remove,
        Done
    }

    public override void _Ready()
    {
        var modsUserData = IsModsExportSmokeEnabled() ? ResolveModsSmokeUserData() : null;
        var logDirectory = Path.Combine(modsUserData ?? OS.GetUserDataDir(), "logs");
        var buildDefault = OS.IsDebugBuild() || Engine.IsEditorHint()
            ? LogLevel.Debug
            : LogLevel.Info;
        var logBuffer = new RingBufferLogSink();
        _logger = TrainLogger.StartSession(logDirectory, buildDefault, new GodotLogOutput(), logBuffer);
        _reloader = new ContentReloader(new GodotContentSource(_logger, modsUserData), _logger);
        var console = new GameConsole();
        console.Initialize(_logger, logBuffer, _reloader);
        AddChild(console);
        var loaded = _reloader.Reload();
        ReportDomainEmptyAssertion();
        if (IsLoggingSmokeEnabled())
            StartLoggingSmoke();
        if (IsContentSmokeEnabled())
            StartContentSmoke(loaded);
        if (IsContentExportSmokeEnabled())
            ReportExportSmoke(loaded);
        if (IsModsExportSmokeEnabled())
            ReportModsExportSmoke(loaded);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!OS.IsDebugBuild() || _reloader is null)
            return;
        if (@event.IsActionPressed(ReloadContentAction) && !@event.IsEcho())
        {
            _reloader.Reload();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        var drained = _logger?.DrainGodotQueue() ?? 0;
        if (_smoke && drained > 0)
            _smokeDrainedFromProcess = true;
        if (_smoke && _smokeWorkerLogged && !_smokeQuitRequested)
        {
            if (_smokeDrainedFromProcess)
                GD.Print(LoggingSmokeProcessMarker);
            _smokeQuitRequested = true;
            GetTree().Quit();
        }

        if (_contentSmoke && !_contentSmokeFinished)
            AdvanceContentSmoke();

        if ((_contentExportSmoke || _modsExportSmoke) && !_contentSmokeFinished)
        {
            _contentSmokeFinished = true;
            GetTree().Quit();
        }
    }

    public override void _ExitTree()
    {
        TryDeleteSmokeVoxels();
        if (_ownsModsSmokeUserData && _modsSmokeUserData is not null)
        {
            try
            {
                if (Directory.Exists(_modsSmokeUserData))
                    Directory.Delete(_modsSmokeUserData, true);
            }
            catch
            {
                // Isolated smoke user-data cleanup must not throw.
            }
        }

        _reloader = null;
        _logger?.Dispose();
        _logger = null;
        if (_smoke)
            GD.Print(LoggingSmokeDisposedMarker);
    }

    private static bool IsLoggingSmokeEnabled() =>
        string.Equals(System.Environment.GetEnvironmentVariable(LoggingSmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private static bool IsContentSmokeEnabled() =>
        string.Equals(System.Environment.GetEnvironmentVariable(ContentSmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private static bool IsContentExportSmokeEnabled() =>
        string.Equals(System.Environment.GetEnvironmentVariable(ContentExportSmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private static bool IsModsExportSmokeEnabled() =>
        string.Equals(System.Environment.GetEnvironmentVariable(ModsExportSmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private void StartLoggingSmoke()
    {
        _smoke = true;
        var logger = _logger!;
        new Thread(() =>
        {
            logger.Info("test", LoggingSmokeWorkerEventId, "queued from background");
            _smokeWorkerLogged = true;
        }).Start();
    }

    private void StartContentSmoke(ContentReloadResult loaded)
    {
        _contentSmoke = true;
        _contentSmokePhase = ContentSmokePhase.WriteAndFire;
        ReportCatalogMarkers(loaded, "CONTENT");
        GD.Print(InputMap.HasAction(ReloadContentAction)
            ? "CONTENT_RELOAD_ACTION_PRESENT"
            : "CONTENT_RELOAD_ACTION_MISSING");
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore.voxels.json"))
            GD.Print("CONTENT_VFS_JSON_READABLE");
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore/icon.png"))
            GD.Print("CONTENT_VFS_PNG_READABLE");
    }

    private void AdvanceContentSmoke()
    {
        if (_reloader is null)
            return;

        switch (_contentSmokePhase)
        {
            case ContentSmokePhase.WriteAndFire:
                _contentSmokeVersionBeforeInput = _reloader.Catalog.Current?.Generation ?? 0;
                WriteSmokeVoxels();
                if (OS.IsDebugBuild() && InputMap.HasAction(ReloadContentAction))
                {
                    var pressed = new InputEventAction
                    {
                        Action = ReloadContentAction,
                        Pressed = true
                    };
                    Input.ParseInputEvent(pressed);
                    Input.FlushBufferedEvents();
                }

                _contentSmokeInputFrames = 0;
                _contentSmokePhase = ContentSmokePhase.AwaitInputReload;
                return;
            case ContentSmokePhase.AwaitInputReload:
                _contentSmokeInputFrames++;
                var current = _reloader.Catalog.Current;
                var inputReloaded = current is not null &&
                    current.Generation > _contentSmokeVersionBeforeInput &&
                    current.TryGet(new VoxelDefinitionId("core", "smoke-ore"), out _);
                if (inputReloaded)
                {
                    GD.Print("CONTENT_SMOKE_INPUT_RELOADED");
                    _contentSmokePhase = ContentSmokePhase.Remove;
                    return;
                }

                if (_contentSmokeInputFrames >= 8)
                    _contentSmokePhase = ContentSmokePhase.Remove;
                return;
            case ContentSmokePhase.Remove:
                TryDeleteSmokeVoxels();
                var afterRemove = _reloader.Reload();
                if (afterRemove.Removed.Contains(new VoxelDefinitionId("core", "smoke-ore")))
                    GD.Print("CONTENT_SMOKE_REMOVED");
                _contentSmokePhase = ContentSmokePhase.Done;
                _contentSmokeFinished = true;
                GetTree().Quit();
                return;
        }
    }

    private string ResolveModsSmokeUserData()
    {
        var configured = System.Environment.GetEnvironmentVariable(ModsSmokeUserDataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _modsSmokeUserData = configured;
            return configured;
        }

        var isolated = Path.Combine(Path.GetTempPath(), "train-mods-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        _modsSmokeUserData = isolated;
        _ownsModsSmokeUserData = true;
        return isolated;
    }

    private void ReportModsExportSmoke(ContentReloadResult loaded)
    {
        _modsExportSmoke = true;
        ReportCatalogMarkers(loaded, "MODS_EXPORT");
        try
        {
            if (_modsSmokeUserData is null)
            {
                GD.Print("MODS_EXPORT_ROOT_UNAVAILABLE");
                return;
            }

            var modsPhysical = Path.Combine(_modsSmokeUserData, "mods");
            if (!Directory.Exists(modsPhysical))
            {
                GD.Print("MODS_EXPORT_ROOT_UNAVAILABLE");
                return;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(modsPhysical).Any())
                    GD.Print("MODS_EXPORT_ROOT_EMPTY");
                else
                    GD.Print("MODS_EXPORT_ROOT_PRESENT");
            }
            catch
            {
                GD.Print("MODS_EXPORT_ROOT_UNAVAILABLE");
            }
        }
        catch
        {
            GD.Print("MODS_EXPORT_ROOT_UNAVAILABLE");
        }
    }

    private void ReportExportSmoke(ContentReloadResult loaded)
    {
        _contentExportSmoke = true;
        ReportCatalogMarkers(loaded, "CONTENT_EXPORT");
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore.voxels.json"))
            GD.Print("CONTENT_EXPORT_JSON_READABLE");
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore/icon.png") &&
            Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore/top.png") &&
            Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore/side.png") &&
            Godot.FileAccess.FileExists("res://builtin/core/resources/voxels/iron-ore/bottom.png"))
            GD.Print("CONTENT_EXPORT_PNG_READABLE");
        if (_logger?.JsonlPath is not null && File.Exists(_logger.JsonlPath))
            GD.Print("CONTENT_EXPORT_JSONL_CREATED");
    }

    private void ReportCatalogMarkers(ContentReloadResult loaded, string prefix)
    {
        if (loaded.IsCommitted &&
            _reloader?.Catalog.Current?.TryGet(new VoxelDefinitionId("core", "iron-ore"), out var entry) == true)
        {
            GD.Print($"{prefix}_IRON_ORE_LOADED");
            if (entry.Definition.Name.Length > 0 && entry.Definition.Presentation.IconPath.Length > 0)
                GD.Print($"{prefix}_PNG_DECODED");
        }
        else if (_reloader?.InitializationFailed == true)
            GD.Print($"{prefix}_INIT_FAILED");

        if (loaded.IsCommitted)
            GD.Print($"{prefix}_INIT_OK");
    }

    private void ReportDomainEmptyAssertion()
    {
        if (!string.Equals(
                System.Environment.GetEnvironmentVariable(ContentSmokeAssertDomainEmptyEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
            return;

        var catalogEmpty = _reloader?.Catalog.Current is null || _reloader.Catalog.Current.Entries.Count == 0;
        var registryEmpty = _reloader?.Settlements.Count == 0;
        GD.Print(catalogEmpty && registryEmpty
            ? "CONTENT_SMOKE_DOMAIN_EMPTY"
            : "CONTENT_SMOKE_DOMAIN_NOT_EMPTY");
    }

    private static void WriteSmokeVoxels()
    {
        using var file = Godot.FileAccess.Open(SmokeVoxelsPath, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
            return;
        file.StoreString(
            """
            {
              "schema_version": 1,
              "voxels": [
                {
                  "id": "smoke-ore",
                  "name": "冒烟矿",
                  "description": "开发冒烟测试用的临时体素种类。",
                  "value": 1,
                  "mass": 1,
                  "max_durability": 100,
                  "tags": ["natural_resource"],
                  "behaviors": [],
                  "allowed_orientations": "all_24",
                  "presentation": {
                    "kind": "cube",
                    "icon": "voxels/iron-ore/icon.png",
                    "voxel_top": "voxels/iron-ore/top.png",
                    "voxel_side": "voxels/iron-ore/side.png",
                    "voxel_bottom": "voxels/iron-ore/bottom.png"
                  },
                  "collision": { "kind": "full_box" }
                }
              ]
            }
            """);
    }

    private static void TryDeleteSmokeVoxels()
    {
        if (Godot.FileAccess.FileExists(SmokeVoxelsPath))
            DirAccess.RemoveAbsolute(SmokeVoxelsPath);
    }
}

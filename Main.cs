using Godot;
using Train.Content;
using Train.Domain.Goods;
using Train.Logging;

namespace Train;

public partial class Main : Node
{
    public const string ReloadContentAction = "reload_content";

    private const string LoggingSmokeEnvironmentVariable = "TRAIN_LOG_SMOKE";
    private const string LoggingSmokeWorkerEventId = "smoke_worker_event";
    private const string LoggingSmokeProcessMarker = "SMOKE_PROCESS_DRAINED";
    private const string LoggingSmokeDisposedMarker = "SMOKE_LOGGER_DISPOSED";
    private const string ContentSmokeEnvironmentVariable = "TRAIN_CONTENT_SMOKE";
    private const string ContentExportSmokeEnvironmentVariable = "TRAIN_CONTENT_EXPORT_SMOKE";
    private const string ModsExportSmokeEnvironmentVariable = "TRAIN_MODS_EXPORT_SMOKE";
    private const string ModsSmokeUserDataEnvironmentVariable = "TRAIN_MODS_SMOKE_USER_DATA";
    private const string SmokeGoodsPath = "res://builtin/core/resources/smoke-temp.goods.json";

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
        _logger = TrainLogger.StartSession(logDirectory, buildDefault, new GodotLogOutput());
        _reloader = new ContentReloader(new GodotContentSource(_logger, modsUserData), _logger);
        var loaded = _reloader.Reload();
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
        TryDeleteSmokeGoods();
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
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore.goods.json"))
            GD.Print("CONTENT_VFS_JSON_READABLE");
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore/icon.png"))
            GD.Print("CONTENT_VFS_PNG_READABLE");
    }

    private void AdvanceContentSmoke()
    {
        if (_reloader is null)
            return;

        switch (_contentSmokePhase)
        {
            case ContentSmokePhase.WriteAndFire:
                _contentSmokeVersionBeforeInput = _reloader.Catalog.Current?.Version ?? 0;
                WriteSmokeGoods();
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
                    current.Version > _contentSmokeVersionBeforeInput &&
                    current.TryGet(new GoodId("core", "smoke-ore"), out _);
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
                TryDeleteSmokeGoods();
                var afterRemove = _reloader.Reload();
                if (afterRemove.Removed.Contains(new GoodId("core", "smoke-ore")))
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
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore.goods.json"))
            GD.Print("CONTENT_EXPORT_JSON_READABLE");
        if (Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore/icon.png") &&
            Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore/top.png") &&
            Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore/side.png") &&
            Godot.FileAccess.FileExists("res://builtin/core/resources/natural/iron-ore/bottom.png"))
            GD.Print("CONTENT_EXPORT_PNG_READABLE");
        if (_logger?.JsonlPath is not null && File.Exists(_logger.JsonlPath))
            GD.Print("CONTENT_EXPORT_JSONL_CREATED");
    }

    private void ReportCatalogMarkers(ContentReloadResult loaded, string prefix)
    {
        if (loaded.IsCommitted &&
            _reloader?.Catalog.Current?.TryGet(new GoodId("core", "iron-ore"), out var entry) == true)
        {
            GD.Print($"{prefix}_IRON_ORE_LOADED");
            if (entry.Definition.Name.Length > 0 && entry.Definition.VoxelVisual.IconPath.Length > 0)
                GD.Print($"{prefix}_PNG_DECODED");
        }
        else if (_reloader?.InitializationFailed == true)
            GD.Print($"{prefix}_INIT_FAILED");

        if (loaded.IsCommitted)
            GD.Print($"{prefix}_INIT_OK");
    }

    private static void WriteSmokeGoods()
    {
        using var file = Godot.FileAccess.Open(SmokeGoodsPath, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
            return;
        file.StoreString(
            """
            {
              "schema_version": 1,
              "goods": [
                {
                  "id": "smoke-ore",
                  "category": "natural_resource",
                  "name": "冒烟矿",
                  "description": "开发冒烟测试用的临时货物种类。",
                  "base_monetary_value": 1,
                  "visual": {
                    "icon": "natural/iron-ore/icon.png",
                    "voxel_top": "natural/iron-ore/top.png",
                    "voxel_side": "natural/iron-ore/side.png",
                    "voxel_bottom": "natural/iron-ore/bottom.png"
                  }
                }
              ]
            }
            """);
    }

    private static void TryDeleteSmokeGoods()
    {
        if (Godot.FileAccess.FileExists(SmokeGoodsPath))
            DirAccess.RemoveAbsolute(SmokeGoodsPath);
    }
}

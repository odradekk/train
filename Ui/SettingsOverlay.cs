using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
using Train.App.Saves;

namespace Train.Ui;

// Decision note: 2026-10-31-menus-saves-and-settings
public partial class SettingsOverlay : Control
{
    private const string Path = "user://settings.cfg";
    private static readonly int[] AutoSaveMinutes = [0, 5, 10, 15, 30, 60, 120];
    private HostCoordinator? _host;
    private Control _menu = null!;
    private NotificationPolicy _policy = null!;
    private RecordingService? _recording;
    private IssueReportPanel? _issueReport;
    internal void OpenIssueReport() => _issueReport?.Open();
    private SaveService? _saves;
    private Action<bool>? _setHighQuality;
    private Action<bool>? _setAlwaysShowSceneStatus;
    private TrainLogger _logger = null!;
    private Action<string> _showError = null!;
    private Window _window = null!;
    private int _mode;
    private float _scale = 1;
    private int _fps;
    private bool _focusPause;
    private bool _cameraShake = true;
    private bool _alwaysShowSceneStatus;
    private bool _highQuality;
    private int _autoSaveMinutes = SaveService.DefaultAutoSaveMinutes;
    private WorldCamera? _camera;
    private NotificationAction[] _actions = Defaults();

    internal void Initialize(HostCoordinator? host, Control menu, TrainLogger logger, Action<string> showError,
        NotificationPolicy policy, RecordingService? recording = null, WorldCamera? camera = null,
        SaveService? saves = null, Action<bool>? setHighQuality = null, Action<bool>? setAlwaysShowSceneStatus = null)
    {
        _host = host; _menu = menu; _logger = logger; _showError = showError; _policy = policy; _recording = recording; _camera = camera;
        _saves = saves; _setHighQuality = setHighQuality; _setAlwaysShowSceneStatus = setAlwaysShowSceneStatus;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        MouseForcePassScrollEvents = false;
        _window = GetWindow();
        _fps = Engine.MaxFps;
        using (var config = new ConfigFile())
        {
            var result = config.Load(Path);
            if (result == Error.Ok)
            {
                var mode = config.GetValue("display", "mode", 0);
                var configScale = config.GetValue("display", "scale", 1.0);
                var configFps = config.GetValue("display", "fps", _fps);
                var focusPause = config.GetValue("game", "focus_pause", false);
                var cameraShake = config.GetValue("game", "camera_shake", true);
                var alwaysShowSceneStatus = config.GetValue("game", "always_show_scene_status", false);
                var configQuality = config.GetValue("display", "quality", 0);
                var configAutoSave = config.GetValue("game", "autosave_minutes", SaveService.DefaultAutoSaveMinutes);
                var displayValid = mode.VariantType == Variant.Type.Int && mode.AsInt64() is >= 0 and <= 2 &&
                    (configScale.VariantType is Variant.Type.Int or Variant.Type.Float) &&
                    double.IsFinite(configScale.AsDouble()) && configScale.AsDouble() is >= 0.75 and <= 2 &&
                    configFps.VariantType == Variant.Type.Int && configFps.AsInt64() is >= 0 and <= 1000 &&
                    focusPause.VariantType == Variant.Type.Bool && cameraShake.VariantType == Variant.Type.Bool &&
                    alwaysShowSceneStatus.VariantType == Variant.Type.Bool &&
                    configQuality.VariantType == Variant.Type.Int && configQuality.AsInt64() is 0 or 1 &&
                    configAutoSave.VariantType == Variant.Type.Int && AutoSaveMinutes.Any(minutes => minutes == configAutoSave.AsInt64());
                if (!displayValid || !TryReadNotifications(config, out var actions))
                {
                    Report("设置文件包含无效值，使用当前工程默认设置。", Error.InvalidData);
                    _mode = 0; _scale = 1; _fps = Engine.MaxFps; _focusPause = false; _cameraShake = true;
                    _alwaysShowSceneStatus = false; _highQuality = false; _autoSaveMinutes = SaveService.DefaultAutoSaveMinutes; _actions = Defaults();
                }
                else
                {
                    _mode = mode.AsInt32(); _scale = (float)configScale.AsDouble();
                    _fps = configFps.AsInt32(); _focusPause = focusPause.AsBool();
                    _cameraShake = cameraShake.AsBool(); _alwaysShowSceneStatus = alwaysShowSceneStatus.AsBool();
                    _highQuality = configQuality.AsInt32() == 1;
                    _autoSaveMinutes = configAutoSave.AsInt32(); _actions = actions;
                }
            }
            else if (result != Error.FileNotFound) Report("无法读取设置文件，使用当前工程默认设置。", result);
        }
        Apply();
        BuildLayout();
        BuildDisplay(Page("Display", "显示"));
        BuildGame(Page("Game", "游戏"));
        BuildNotifications(Page("Notifications", "通知"));
        BuildKeys(Page("Keys", "按键"));
        BuildAbout(Page("About", "关于"));
        VisibilityChanged += VisibilityUpdated;
        SelectPage(0);
        Hide();
    }

    private void Apply()
    {
        SetWindowMode(_pendingMode ?? _mode);
        _window.ContentScaleFactor = _scale;
        Engine.MaxFps = _fps;
        if (_host is not null) _host.AutoPauseOnFocusLoss = _focusPause;
        if (_camera is not null) _camera.ShakeEnabled = _cameraShake;
        _setAlwaysShowSceneStatus?.Invoke(_alwaysShowSceneStatus);
        _setHighQuality?.Invoke(_highQuality);
        // 重设间隔会重新开始计时，所以只在间隔变化时调用。
        TimeSpan? autoSave = _autoSaveMinutes == 0 ? null : TimeSpan.FromMinutes(_autoSaveMinutes);
        if (_saves is not null && _saves.AutoSaveInterval != autoSave) _saves.SetAutoSaveInterval(autoSave);
        foreach (var category in Enum.GetValues<NotificationCategory>())
            _policy.SetAction(category, _actions[(int)category]);
    }

    private void Save()
    {
        Apply();
        using var config = new ConfigFile();
        config.SetValue("display", "mode", _mode);
        config.SetValue("display", "scale", _scale);
        config.SetValue("display", "fps", _fps);
        config.SetValue("display", "quality", _highQuality ? 1 : 0);
        config.SetValue("game", "focus_pause", _focusPause);
        config.SetValue("game", "camera_shake", _cameraShake);
        config.SetValue("game", "always_show_scene_status", _alwaysShowSceneStatus);
        config.SetValue("game", "autosave_minutes", _autoSaveMinutes);
        foreach (var category in Enum.GetValues<NotificationCategory>())
            config.SetValue("notifications", NotificationPolicy.Key(category), (int)_actions[(int)category]);
        if (config.Save(Path) is var error && error != Error.Ok)
            Report("设置已生效，但未能保存到磁盘。", error);
    }

    private void Report(string message, Error error)
    {
        _logger.Error("settings", "settings_io_failed", message,
            new Dictionary<string, object?> { ["path"] = Path, ["error"] = error.ToString() });
        _showError(message);
    }

    private static NotificationAction[] Defaults() =>
        Enum.GetValues<NotificationCategory>().Select(NotificationPolicy.DefaultAction).ToArray();

    private static bool TryReadNotifications(ConfigFile config, out NotificationAction[] actions)
    {
        actions = Defaults();
        foreach (var category in Enum.GetValues<NotificationCategory>())
        {
            var key = NotificationPolicy.Key(category);
            if (!config.HasSectionKey("notifications", key)) continue;
            var value = config.GetValue("notifications", key);
            if (value.VariantType != Variant.Type.Int || value.AsInt64() is < 0 or > 2) return false;
            actions[(int)category] = (NotificationAction)value.AsInt32();
        }
        return true;
    }

    private static OptionButton Options(string name, string[] values)
    {
        var options = new OptionButton { Name = name, CustomMinimumSize = new(280, 38) };
        foreach (var value in values) options.AddItem(value);
        return options;
    }

    private static void AddRow(VBoxContainer body, string title, Control input)
    {
        var row = new HBoxContainer();
        var label = UiStyle.Label(title);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label); row.AddChild(input); body.AddChild(row);
    }
}

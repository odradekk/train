using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
using Train.App.Saves;

namespace Train.Ui;

public partial class SettingsPanel : Control
{
    private const string Path = "user://settings.cfg";
    private static readonly int[] AutoSaveMinutes = [0, 5, 10, 15, 30, 60, 120];
    private HostCoordinator? _host;
    private NotificationPolicy _policy = null!;
    private RecordingService? _recording;
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

    internal void Initialize(HostCoordinator? host, TrainLogger logger, Action<string> showError,
        NotificationPolicy policy, RecordingService? recording = null, WorldCamera? camera = null,
        SaveService? saves = null, Action<bool>? setHighQuality = null, Action<bool>? setAlwaysShowSceneStatus = null)
    {
        _host = host; _logger = logger; _showError = showError; _policy = policy; _recording = recording; _camera = camera;
        _saves = saves; _setHighQuality = setHighQuality; _setAlwaysShowSceneStatus = setAlwaysShowSceneStatus;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
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
        UiStyle.Panel(this, "Settings", "设置", new(650, 200), 576, Hide, out var body, centered: true);
        var modes = Options("WindowMode", ["窗口化", "无边框全屏", "独占全屏"]);
        modes.Select(_mode);
        AddRow(body, "窗口模式", modes);
        modes.ItemSelected += index => { _mode = (int)index; Save(); };
        var quality = Options("Quality", ["中", "高"]);
        quality.Select(_highQuality ? 1 : 0);
        AddRow(body, "画质", quality);
        quality.ItemSelected += index => { _highQuality = index == 1; Save(); };
        var scale = new SpinBox { Name = "UiScale", MinValue = 0.75, MaxValue = 2, Step = 0.25,
            Value = _scale, Suffix = "×", CustomMinimumSize = new(280, 38) };
        AddRow(body, "界面缩放", scale);
        scale.ValueChanged += value => { _scale = (float)value; Save(); };
        var fps = new SpinBox { Name = "FrameLimit", MinValue = 0, MaxValue = 1000, Step = 1,
            Value = _fps, Suffix = "FPS", CustomMinimumSize = new(280, 38) };
        AddRow(body, "渲染帧率上限", fps);
        body.AddChild(UiStyle.Label("0 表示不限；实际帧率仍受垂直同步与硬件限制。"));
        fps.ValueChanged += value => { _fps = (int)value; Save(); };
        var focus = new CheckButton { Name = "FocusPause", Text = "失焦时自动暂停", ButtonPressed = _focusPause };
        focus.Toggled += value => { _focusPause = value; Save(); };
        body.AddChild(focus);
        var shake = new CheckButton { Name = "CameraShake", Text = "镜头抖动", ButtonPressed = _cameraShake };
        shake.Toggled += value => { _cameraShake = value; Save(); };
        body.AddChild(shake);
        var alwaysStatus = new CheckButton { Name = "AlwaysShowSceneStatus", Text = "一直显示血条和状态图标",
            ButtonPressed = _alwaysShowSceneStatus };
        alwaysStatus.Toggled += value => { _alwaysShowSceneStatus = value; Save(); };
        body.AddChild(alwaysStatus);
        if (_saves is not null)
        {
            var autoSave = Options("AutoSave", AutoSaveMinutes.Select(minutes => minutes == 0 ? "关闭" : minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟").ToArray());
            autoSave.Select(Array.IndexOf(AutoSaveMinutes, _autoSaveMinutes));
            AddRow(body, "自动存档", autoSave);
            autoSave.ItemSelected += index => { _autoSaveMinutes = AutoSaveMinutes[index]; Save(); };
        }
        body.AddChild(UiStyle.Label("通知", true));
        foreach (var category in Enum.GetValues<NotificationCategory>())
        {
            var options = Options("Notify_" + NotificationPolicy.Key(category), ["不响应", "降到 1×", "暂停"]);
            options.Select((int)_actions[(int)category]);
            AddRow(body, NotificationPolicy.Label(category), options);
            var selected = category;
            options.ItemSelected += index =>
            {
                _actions[(int)selected] = (NotificationAction)(int)index;
                Save();
            };
        }
        body.AddChild(UiStyle.Label("更改立即生效，并自动保存。等级过滤只保留在本次运行。"));
        if (_recording is not null)
        {
            var report = new IssueReportPanel { Name = "IssueReportPanel" };
            AddChild(report);
            report.Configure(_recording);
            body.AddChild(UiStyle.Button("ReportIssue", "报告问题", report.Open));
        }
        body.AddChild(UiStyle.Button("Back", "返回", Hide));
        Hide();
    }

    private void Apply()
    {
        _window.Mode = _mode switch { 1 => Window.ModeEnum.Fullscreen, 2 => Window.ModeEnum.ExclusiveFullscreen,
            _ => Window.ModeEnum.Windowed };
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

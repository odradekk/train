using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Recording;

namespace Train.Ui;

public partial class SettingsPanel : Control
{
    private const string Path = "user://settings.cfg";
    private HostCoordinator? _host;
    private RecordingService? _recording;
    private TrainLogger _logger = null!;
    private Action<string> _showError = null!;
    private Window _window = null!;
    private int _mode;
    private float _scale = 1;
    private int _fps;
    private bool _focusPause;

    internal void Initialize(HostCoordinator? host, TrainLogger logger, Action<string> showError,
        RecordingService? recording = null)
    { _host = host; _logger = logger; _showError = showError; _recording = recording; }

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
                if (mode.VariantType != Variant.Type.Int || mode.AsInt64() is < 0 or > 2 ||
                    configScale.VariantType is not (Variant.Type.Int or Variant.Type.Float) ||
                    !double.IsFinite(configScale.AsDouble()) || configScale.AsDouble() is < 0.75 or > 2 ||
                    configFps.VariantType != Variant.Type.Int || configFps.AsInt64() is < 0 or > 1000 ||
                    focusPause.VariantType != Variant.Type.Bool)
                {
                    Report("设置文件包含无效值，使用当前工程默认设置。", Error.InvalidData);
                    _mode = 0; _scale = 1; _fps = Engine.MaxFps; _focusPause = false;
                }
                else
                {
                    _mode = mode.AsInt32(); _scale = (float)configScale.AsDouble();
                    _fps = configFps.AsInt32(); _focusPause = focusPause.AsBool();
                }
            }
            else if (result != Error.FileNotFound) Report("无法读取设置文件，使用当前工程默认设置。", result);
        }
        Apply();
        var panel = UiStyle.Panel(this, "Settings", "设置", new(650, 200), 576, Hide, out var body);
        var modes = Options("WindowMode", ["窗口化", "无边框全屏", "独占全屏"]);
        modes.Select(_mode);
        AddRow(body, "窗口模式", modes);
        modes.ItemSelected += index => { _mode = (int)index; Save(); };
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
        body.AddChild(UiStyle.Label("更改立即生效，并自动保存。"));
        if (_recording is not null)
        {
            var report = new IssueReportPanel { Name = "IssueReportPanel" };
            AddChild(report);
            report.Configure(_recording);
            body.AddChild(UiStyle.Button("ReportIssue", "报告问题", report.Open));
        }
        body.AddChild(UiStyle.Button("Back", "返回", Hide));
        panel.Resized += () => panel.Position = (Size - panel.Size) / 2;
        Resized += () => panel.Position = (Size - panel.Size) / 2;
        Hide();
    }

    private void Apply()
    {
        _window.Mode = _mode switch { 1 => Window.ModeEnum.Fullscreen, 2 => Window.ModeEnum.ExclusiveFullscreen,
            _ => Window.ModeEnum.Windowed };
        _window.ContentScaleFactor = _scale;
        Engine.MaxFps = _fps;
        if (_host is not null) _host.AutoPauseOnFocusLoss = _focusPause;
    }

    private void Save()
    {
        Apply();
        using var config = new ConfigFile();
        config.SetValue("display", "mode", _mode);
        config.SetValue("display", "scale", _scale);
        config.SetValue("display", "fps", _fps);
        config.SetValue("game", "focus_pause", _focusPause);
        if (config.Save(Path) is var error && error != Error.Ok)
            Report("设置已生效，但未能保存到磁盘。", error);
    }

    private void Report(string message, Error error)
    {
        _logger.Error("settings", "settings_io_failed", message,
            new Dictionary<string, object?> { ["path"] = Path, ["error"] = error.ToString() });
        _showError(message);
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

using Godot;
using Train.Content.Contracts;

namespace Train.Ui;

public partial class SettingsOverlay
{
    private readonly Label _packages = new() { Name = "Packages", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };

    private void BuildDisplay(VBoxContainer body)
    {
        var modes = Options("WindowMode", ["窗口化", "无边框全屏", "独占全屏"]);
        _modeOptions = modes;
        modes.Select(_mode);
        AddRow(body, "窗口模式", modes);
        modes.ItemSelected += index => PreviewMode((int)index);
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
    }

    private void BuildGame(VBoxContainer body)
    {
        var focus = new CheckButton { Name = "FocusPause", Text = "失焦时自动暂停", ButtonPressed = _focusPause };
        focus.Toggled += value => { _focusPause = value; Save(); };
        body.AddChild(focus);
        var shake = new CheckButton { Name = "CameraShake", Text = "镜头抖动", ButtonPressed = _cameraShake };
        shake.Toggled += value => { _cameraShake = value; Save(); };
        body.AddChild(shake);
        var alwaysStatus = new CheckButton { Name = "AlwaysShowSceneStatus", Text = "一直显示耐久条和状态图标",
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
    }

    private void BuildAbout(VBoxContainer body)
    {
        var version = UiStyle.Label(ProjectSettings.GetSetting("application/config/version").AsString());
        version.Name = "Version";
        AddRow(body, "版本", version);
        AddRow(body, "内容包", _packages);
        RefreshPackages();
        if (_recording is not null)
        {
            var report = _issueReport = new IssueReportPanel { Name = "IssueReportPanel" };
            AddChild(report);
            report.Configure(_recording);
            body.AddChild(UiStyle.Button("ReportIssue", "报告问题", report.Open));
        }
    }

    private void RefreshPackages()
    {
        _packages.Text = _host?.Content is { } content
            ? string.Join("、", content.Packages.Where(package => package.State == PackageState.Available)
                .Select(package => $"{package.Metadata!.Name} {package.Metadata.Version}"))
            : _host?.IsBusy == true ? "正在加载内容包…" : "没有可用内容包";
    }

    private static void BuildKeys(VBoxContainer body)
    {
        var table = new GridContainer { Columns = 2 };
        table.AddThemeConstantOverride("h_separation", 32);
        table.AddThemeConstantOverride("v_separation", 16);
        body.AddChild(table);
        table.AddChild(UiStyle.Label("操作", true));
        table.AddChild(UiStyle.Label("按键", true));
        foreach (var (action, label) in new (string, string)[]
        {
            ("editor_undo", "撤销（设计编辑器）"), ("editor_redo", "重做（设计编辑器）"),
            ("editor_save", "保存草稿（设计编辑器）"), ("editor_close", "取消选点／退出（设计编辑器）"),
            ("reload_content", "重载内容（开发构建）")
        })
        {
            if (action == "reload_content" && !OS.IsDebugBuild()) continue;
            var keys = InputMap.ActionGetEvents(action).OfType<InputEventKey>()
                .Select(key => OS.GetKeycodeString(key.GetKeycodeWithModifiers()));
            KeyRow(action, label, string.Join(" / ", keys));
        }
        KeyRow("close", "关闭当前界面／返回", "Esc");
        KeyRow("console", "打开／关闭控制台", "` / ~");
        KeyRow("console_submit", "执行命令（控制台）", "Enter");
        KeyRow("save_name", "确认名称（存档）", "Enter");

        void KeyRow(string name, string action, string binding)
        {
            var label = UiStyle.Label(action);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            table.AddChild(label);
            var keys = UiStyle.Label(binding);
            keys.Name = "Key_" + name;
            table.AddChild(keys);
        }
    }
}

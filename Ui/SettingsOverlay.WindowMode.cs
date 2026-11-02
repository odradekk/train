using Godot;

namespace Train.Ui;

public partial class SettingsOverlay
{
    private readonly Control _modePrompt = new() { Name = "ModePrompt", Visible = false, MouseForcePassScrollEvents = false };
    private readonly PanelContainer _modeDialog = new();
    private readonly Label _modeCountdown = UiStyle.Label("");
    private OptionButton _modeOptions = null!;
    private int? _pendingMode;
    private double _modeRemaining;
    private Button _restoreMode = null!;

    private void BuildModePrompt()
    {
        _modePrompt.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_modePrompt);
        var shade = new ColorRect { Color = new(0, 0, 0, .6f) };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _modePrompt.AddChild(shade);
        _modePrompt.AddChild(_modeDialog);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
        _modeDialog.AddChild(margin);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 16);
        margin.AddChild(layout);
        layout.AddChild(UiStyle.Label("保留新的窗口模式？", true));
        _modeCountdown.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        layout.AddChild(_modeCountdown);
        var actions = new HFlowContainer();
        layout.AddChild(actions);
        actions.AddChild(UiStyle.Button("KeepMode", "保留", () =>
        {
            if (_pendingMode is not { } mode) return;
            _mode = mode;
            CloseModePrompt();
            Save();
        }, true));
        _restoreMode = UiStyle.Button("RestoreMode", "恢复", RestoreMode);
        actions.AddChild(_restoreMode);
    }

    private void PreviewMode(int mode)
    {
        if (mode == _mode) return;
        _pendingMode = mode;
        _modeRemaining = 10;
        SetWindowMode(mode);
        _panel.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
        _modePrompt.Show(); _modePrompt.MoveToFront();
        UpdateModeCountdown();
        ArrangeModePrompt();
        _restoreMode.GrabFocus();
    }

    public override void _Process(double delta)
    {
        if (_packages.IsVisibleInTree()) RefreshPackages();
        if (_pendingMode is null) return;
        _modeRemaining -= delta;
        if (_modeRemaining <= 0) RestoreMode();
        else UpdateModeCountdown();
    }

    private void UpdateModeCountdown() =>
        _modeCountdown.Text = $"画面显示不正常时无需操作，{Math.Ceiling(_modeRemaining):0} 秒后自动恢复原来的窗口模式。";

    private void RestoreMode()
    {
        if (_pendingMode is null) return;
        SetWindowMode(_mode);
        _modeOptions.Select(_mode);
        CloseModePrompt();
    }

    private void CloseModePrompt()
    {
        _pendingMode = null;
        _modePrompt.Hide();
        _panel.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Inherited;
        if (_modeOptions.IsInsideTree() && IsVisibleInTree()) _modeOptions.GrabFocus();
    }

    private void SetWindowMode(int mode) => _window.Mode = mode switch
    {
        1 => Window.ModeEnum.Fullscreen, 2 => Window.ModeEnum.ExclusiveFullscreen, _ => Window.ModeEnum.Windowed
    };

    private void ArrangeModePrompt()
    {
        _modeDialog.Size = new(Math.Min(560, Math.Max(0, Size.X - 32)), 180);
        _modeDialog.Position = (Size - _modeDialog.Size) / 2;
    }
}

using Godot;
using Train.App;

namespace Train.Ui;

public partial class SettingsOverlay
{
    private readonly PanelContainer _panel = new() { Name = "Settings" };
    private readonly VBoxContainer _navigation = new() { Name = "Navigation", CustomMinimumSize = new(130, 0) };
    private readonly Control _pageHost = new() { Name = "Pages", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly List<ScrollContainer> _pages = [];
    private readonly List<Button> _pageButtons = [];
    private readonly ButtonGroup _pageGroup = new();
    private readonly List<(Control Control, FocusBehaviorRecursiveEnum Previous)> _backgroundFocus = [];
    private Control? _previousFocus;

    private void BuildLayout()
    {
        var shade = new ColorRect { Color = new(0, 0, 0, .6f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        AddChild(_panel);
        var margin = new MarginContainer { Name = "Margin" };
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
        _panel.AddChild(margin);
        var layout = new VBoxContainer { Name = "Layout" };
        layout.AddThemeConstantOverride("separation", 16);
        margin.AddChild(layout);
        var heading = new HBoxContainer();
        var title = UiStyle.Label("设置", true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(title);
        heading.AddChild(UiStyle.Label("改动立即生效，并自动保存。"));
        heading.AddChild(UiStyle.Button("Close", "关闭（Esc）", Hide));
        layout.AddChild(heading);
        var body = new HBoxContainer { Name = "Body", SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 24);
        layout.AddChild(body);
        body.AddChild(_navigation);
        body.AddChild(_pageHost);
        BuildModePrompt();
        Resized += Arrange;
        Arrange();
    }

    private VBoxContainer Page(string name, string label)
    {
        var index = _pages.Count;
        var button = UiStyle.Button(name, label, () => SelectPage(index));
        button.ToggleMode = true;
        button.ButtonGroup = _pageGroup;
        _navigation.AddChild(button);
        _pageButtons.Add(button);
        var page = new ScrollContainer { Name = name + "Page", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _pageHost.AddChild(page);
        _pages.Add(page);
        var content = new VBoxContainer { Name = "Content", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 10);
        page.AddChild(content);
        return content;
    }

    private void SelectPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visible = i == index;
            _pageButtons[i].SetPressedNoSignal(i == index);
        }
        if (_packages.IsVisibleInTree()) RefreshPackages();
    }

    internal void Open()
    {
        if (!Visible) _previousFocus = GetViewport().GuiGetFocusOwner();
        Show(); MoveToFront();
        _pageButtons.First(button => button.ButtonPressed).GrabFocus();
    }

    private void VisibilityUpdated()
    {
        _host?.SetOverlayPaused(OverlayPauseSource.Settings, Visible);
        if (Visible)
        {
            foreach (var control in GetParent().GetChildren().OfType<Control>().Append(_menu))
            {
                if (control == this || control is SavePanel) continue;
                _backgroundFocus.Add((control, control.FocusBehaviorRecursive));
                control.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
            }
        }
        else { RestoreMode(); RestoreFocus(); }
    }

    private void RestoreFocus()
    {
        foreach (var (control, previous) in _backgroundFocus)
            if (GodotObject.IsInstanceValid(control)) control.FocusBehaviorRecursive = previous;
        _backgroundFocus.Clear();
        if (GodotObject.IsInstanceValid(_previousFocus) && _previousFocus!.IsInsideTree() && _previousFocus.IsVisibleInTree()) _previousFocus.GrabFocus();
        _previousFocus = null;
    }

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && HandleEscape())
            GetViewport().SetInputAsHandled();
    }

    internal bool HandleEscape()
    {
        if (!IsVisibleInTree() || _issueReport?.Visible == true) return false;
        if (_pendingMode is not null) RestoreMode();
        else Hide();
        return true;
    }

    private void Arrange()
    {
        _panel.Size = new(Math.Min(1100, Math.Max(0, Size.X - 32)), Math.Min(960, Math.Max(0, Size.Y - 32)));
        _panel.Position = (Size - _panel.Size) / 2;
        ArrangeModePrompt();
    }

    public override void _ExitTree()
    {
        _host?.SetOverlayPaused(OverlayPauseSource.Settings, false);
        RestoreMode();
        RestoreFocus();
    }
}

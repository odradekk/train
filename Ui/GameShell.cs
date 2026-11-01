using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 承载游戏画面的固定区域；业务内容负责自己的绑定、刷新和命令。
/// Decision note: 2026-10-31-docked-game-shell
/// </summary>
public partial class GameShell : Control
{
    private readonly Control _listHost = Host("ListRegion");
    private readonly Control _inspectorHost = Host("InspectorRegion");
    private readonly Control _workspaceHost = Host("WorkspaceRegion");
    private readonly ColorRect _mask = new() { Name = "WorkspaceMask", Color = new(0, 0, 0, .45f),
        MouseFilter = MouseFilterEnum.Stop, MouseForcePassScrollEvents = false, Visible = false };
    private readonly PanelContainer _navigation = new() { Name = "Navigation", MouseFilter = MouseFilterEnum.Stop };
    private readonly Dictionary<Control, Action> _watchers = [];
    private float _topInset, _bottomInset;

    internal WorldObjectId? SelectedObject { get; private set; }
    internal Control? ListContent { get; private set; }
    internal Control? InspectorContent { get; private set; }
    internal Control? WorkspaceContent { get; private set; }
    internal VBoxContainer Navigation { get; } = new() { Name = "Items" };
    internal Rect2 SceneArea { get; private set; }
    internal event Action? LayoutChanged;
    internal Func<bool>? CloseOverlay { get; set; }
    internal Func<bool>? SceneVisible { get; set; }
    internal Action? OpenMenu { get; set; }

    internal float TopInset
    {
        get => _topInset;
        set { if (_topInset == value) return; _topInset = value; Arrange(); }
    }
    internal float BottomInset
    {
        get => _bottomInset;
        set { if (_bottomInset == value) return; _bottomInset = value; Arrange(); }
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(Navigation);
        _navigation.AddChild(scroll);
        AddChild(_navigation);
        AddChild(_listHost);
        AddChild(_inspectorHost);
        _mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_mask);
        _mask.AddChild(_workspaceHost);
        Resized += Arrange;
        Arrange();
    }

    public override void _Input(InputEvent input)
    {
        if (!IsVisibleInTree() || input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        // 子窗口和弹出菜单接收自己的输入；这里不能顺手再关掉它们下面的一层。
        if (VisibleWindow(GetTree().Root)) return;
        if (CloseOverlay?.Invoke() == true || CloseTop()) GetViewport().SetInputAsHandled();
        else if (SceneVisible?.Invoke() != true && OpenMenu is { } open)
        { open(); GetViewport().SetInputAsHandled(); }
    }

    private static bool VisibleWindow(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Window { Visible: true }) return true;
            if (child is not Node3D && VisibleWindow(child)) return true;
        }
        return false;
    }

    // 内容是调用者复用的有限实例；关闭只隐藏，随外壳退出场景树时释放。
    internal void ShowList(Control? content)
    {
        var previous = ListContent;
        ListContent = content;
        Mount(previous, content, _listHost);
    }

    internal void Inspect(WorldObjectId? selected, Control? content)
    {
        if ((selected is null) != (content is null)) throw new ArgumentException("选中对象和检查器内容必须一起设置或清空。");
        var previous = InspectorContent;
        SelectedObject = selected;
        InspectorContent = content;
        Mount(previous, content, _inspectorHost);
    }

    internal void ShowWorkspace(Control? content)
    {
        var previous = WorkspaceContent;
        WorkspaceContent = content;
        Mount(previous, content, _workspaceHost);
        _mask.Visible = content is not null;
        if (content is not null) _mask.MoveToFront();
    }

    internal bool CloseTop()
    {
        if (WorkspaceContent is not null) ShowWorkspace(null);
        else if (InspectorContent is not null) Inspect(null, null);
        else if (ListContent is not null) ShowList(null);
        else return false;
        return true;
    }

    private void Mount(Control? previous, Control? content, Control host)
    {
        if (previous is not null && GodotObject.IsInstanceValid(previous))
        {
            if (_watchers.Remove(previous, out var watch))
            { previous.VisibilityChanged -= watch; previous.TreeExited -= watch; }
            if (previous != content) previous.Hide();
        }
        if (content is not null)
        {
            if (content.GetParent() is null) host.AddChild(content);
            else if (content.GetParent() != host) content.Reparent(host);
            content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            content.Show();
            void Closed()
            {
                // 隐藏祖先（设计编辑器等）时保留内容自身的开关状态。
                if (!IsInsideTree() || content.Visible && content.IsInsideTree()) return;
                if (ListContent == content) ShowList(null);
                if (InspectorContent == content) Inspect(null, null);
                if (WorkspaceContent == content) ShowWorkspace(null);
            }
            _watchers[content] = Closed;
            content.VisibilityChanged += Closed;
            content.TreeExited += Closed;
        }
        Arrange();
    }

    private void Arrange()
    {
        if (!IsNodeReady() || !IsInsideTree()) return;
        var navigationWidth = _navigation.GetCombinedMinimumSize().X;
        var height = Math.Max(0, Size.Y - TopInset - BottomInset);
        // 中间至少容得下现有的 280 宽地图图层面板和 240 宽节点小地图。
        var width = Math.Max(0, Size.X - navigationWidth - 24 - 280);
        var inspectorWidth = InspectorContent is null ? 0 : Math.Min(440, ListContent is null ? width : width * .62f);
        var listWidth = ListContent is null ? 0 : Math.Min(340, width - inspectorWidth);
        _navigation.Position = new(0, TopInset);
        _navigation.Size = new(navigationWidth, height);
        _listHost.Position = new(navigationWidth, TopInset);
        _listHost.Size = new(listWidth, height);
        _inspectorHost.Position = new(Size.X - inspectorWidth - 8, TopInset + 8);
        _inspectorHost.Size = new(inspectorWidth, Math.Max(0, height - 16));
        _workspaceHost.Position = new(24, TopInset + 16);
        _workspaceHost.Size = new(Math.Max(0, Size.X - 48), Math.Max(0, height - 32));
        SceneArea = new(new(navigationWidth + listWidth + 8, TopInset + 8),
            new(Math.Max(0, Size.X - navigationWidth - listWidth - inspectorWidth - 24), Math.Max(0, height - 16)));
        LayoutChanged?.Invoke();
    }

    internal static Rect2? DockArea(Control panel)
    {
        for (Node? ancestor = panel.GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
            if (ancestor is GameShell shell)
                foreach (var host in new[] { shell._listHost, shell._inspectorHost, shell._workspaceHost })
                    if (host.IsAncestorOf(panel)) return host.GetGlobalRect();
        return null;
    }

    private static Control Host(string name) => new() { Name = name, MouseFilter = MouseFilterEnum.Ignore };
}

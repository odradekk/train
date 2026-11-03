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
    private readonly Control _inspectorBody = Host("InspectorBody");
    private readonly PanelContainer _objectNavigation = new() { Name = "ObjectNavigation", Visible = false };
    private Button _locate = null!, _enter = null!;
    private Label _navigationReason = null!;
    private readonly Control _workspaceHost = Host("WorkspaceRegion");
    private readonly ColorRect _mask = new() { Name = "WorkspaceMask", Color = new(0, 0, 0, .45f),
        MouseFilter = MouseFilterEnum.Stop, MouseForcePassScrollEvents = false, Visible = false };
    private readonly PanelContainer _navigation = new() { Name = "Navigation", MouseFilter = MouseFilterEnum.Stop };
    private readonly Dictionary<Control, Action> _watchers = [];
    private readonly ConfirmationDialog _switchConfirmation = new() { Name = "WorkspaceSwitchConfirmation",
        Title = "放弃未保存的编辑？", DialogAutowrap = true, OkButtonText = "放弃编辑并切换", CancelButtonText = "取消" };
    private Action? _pendingWorkspace;
    private float _topInset, _bottomInset;

    internal WorldObjectId? SelectedObject { get; private set; }
    internal Control? ListContent { get; private set; }
    internal Control? InspectorContent { get; private set; }
    internal Control? WorkspaceContent { get; private set; }
    internal VBoxContainer Navigation { get; } = new() { Name = "Items" };
    internal Rect2 SceneArea { get; private set; }
    internal event Action? LayoutChanged;
    internal event Action<WorldObjectId>? LocateRequested;
    internal event Action<WorldObjectId>? SceneRequested;
    internal Func<bool>? CloseOverlay { get; set; }
    internal Func<bool>? SceneVisible { get; set; }
    internal Action? OpenMenu { get; set; }
    internal Func<string?>? UnsavedWorkspace { get; set; }

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
        _inspectorHost.AddChild(_inspectorBody);
        _inspectorHost.AddChild(_objectNavigation);
        var navigationBody = new VBoxContainer();
        var actions = new HBoxContainer();
        _locate = UiStyle.Button("LocateObject", "定位", () =>
        { if (SelectedObject is { } id) LocateRequested?.Invoke(id); });
        _locate.TooltipText = "在宏观地图上定位";
        _enter = UiStyle.Button("EnterScene", "进入场景", () =>
        { if (SelectedObject is { } id) SceneRequested?.Invoke(id); });
        actions.AddChild(_locate); actions.AddChild(_enter);
        navigationBody.AddChild(actions);
        _navigationReason = UiStyle.Label("");
        _navigationReason.Name = "NavigationReason";
        _navigationReason.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        navigationBody.AddChild(_navigationReason);
        _objectNavigation.AddChild(navigationBody);
        _objectNavigation.Resized += Arrange;
        AddChild(_mask);
        _mask.AddChild(_workspaceHost);
        AddChild(_switchConfirmation);
        _switchConfirmation.Canceled += () => _pendingWorkspace = null;
        _switchConfirmation.Confirmed += () =>
        {
            var open = _pendingWorkspace;
            _pendingWorkspace = null;
            open?.Invoke();
        };
        Resized += Arrange;
        Arrange();
    }

    public override void _Input(InputEvent input)
    {
        if (!IsVisibleInTree() || input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        if (CloseOverlay?.Invoke() == true || CloseTop()) GetViewport().SetInputAsHandled();
        else if (SceneVisible?.Invoke() != true && OpenMenu is { } open)
        { open(); GetViewport().SetInputAsHandled(); }
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
        if (selected is null) _objectNavigation.Hide();
        Mount(previous, content, _inspectorBody);
    }

    internal void SetObjectNavigation(bool visible, string? locateReason, string? sceneReason)
    {
        var reason = string.Join("\n", new[]
        {
            locateReason is null ? null : "定位：" + locateReason,
            sceneReason is null ? null : "进入场景：" + sceneReason
        }.OfType<string>());
        if (_objectNavigation.Visible == visible && _locate.Disabled == (locateReason is not null) &&
            _enter.Disabled == (sceneReason is not null) && _navigationReason.Text == reason) return;
        _objectNavigation.Visible = visible;
        _locate.Disabled = locateReason is not null;
        _enter.Disabled = sceneReason is not null;
        _navigationReason.Text = reason;
        _navigationReason.Visible = _navigationReason.Text.Length > 0;
        Arrange();
    }

    internal void RequestWorkspace(Action open)
    {
        if (UnsavedWorkspace?.Invoke() is not { } workspace) { open(); return; }
        _pendingWorkspace = open;
        _switchConfirmation.DialogText = $"切换工作区会丢掉{workspace}里未保存的编辑。是否放弃编辑并切换？";
        _switchConfirmation.PopupCentered();
        _switchConfirmation.GetCancelButton().GrabFocus();
    }

    internal void ShowWorkspace(Control? content)
    {
        _pendingWorkspace = null;
        _switchConfirmation.Hide();
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
        // 中间保留地图工具、图层弹窗和节点小地图的操作空间。
        var width = Math.Max(0, Size.X - navigationWidth - 24 - 280);
        var inspectorWidth = InspectorContent is null ? 0 : Math.Min(440, ListContent is null ? width : width * .62f);
        var listWidth = ListContent is null ? 0 : Math.Min(340, width - inspectorWidth);
        _navigation.Position = new(0, TopInset);
        _navigation.Size = new(navigationWidth, height);
        _listHost.Position = new(navigationWidth, TopInset);
        _listHost.Size = new(listWidth, height);
        _inspectorHost.Position = new(Size.X - inspectorWidth - 8, TopInset + 8);
        _inspectorHost.Size = new(inspectorWidth, Math.Max(0, height - 16));
        _inspectorHost.Visible = InspectorContent is not null;
        _objectNavigation.Size = new(inspectorWidth, _objectNavigation.GetCombinedMinimumSize().Y);
        var navigationHeight = _objectNavigation.Visible ? _objectNavigation.Size.Y + 8 : 0;
        _inspectorBody.Position = new(0, navigationHeight);
        _inspectorBody.Size = new(inspectorWidth, Math.Max(0, _inspectorHost.Size.Y - navigationHeight));
        SceneArea = new(new(navigationWidth + listWidth + 8, TopInset + 8),
            new(Math.Max(0, Size.X - navigationWidth - listWidth - inspectorWidth - 24), Math.Max(0, height - 16)));
        _mask.Position = SceneArea.Position;
        _mask.Size = SceneArea.Size;
        _workspaceHost.Position = new(16, 16);
        _workspaceHost.Size = new(Math.Max(0, SceneArea.Size.X - 32), Math.Max(0, SceneArea.Size.Y - 32));
        LayoutChanged?.Invoke();
    }

    internal static Rect2? DockArea(Control panel)
    {
        for (Node? ancestor = panel.GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
            if (ancestor is GameShell shell)
                foreach (var host in new[] { shell._listHost, shell._inspectorBody, shell._workspaceHost })
                    if (host.IsAncestorOf(panel)) return host.GetGlobalRect();
        return null;
    }

    private static Control Host(string name) => new() { Name = name, MouseFilter = MouseFilterEnum.Ignore };
}

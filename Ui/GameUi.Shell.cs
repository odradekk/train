using Godot;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    private GameShell _shell = null!;
    private Label _wallet = null!;
    private Label _breadcrumb = null!;
    private Button _fleetNavigation = null!, _edgeNavigation = null!, _noticeButton = null!;
    private MenuButton _gameMenu = null!;
    private readonly List<Action> _menuActions = [];
    private long? _lastWallet;
    private long _walletChange;
    private double _walletChangeRemaining;
    private string? _notificationPause;
    private WorldListKind? _listKind;
    private int _authoringMenuIndex = -1;
    internal Action? OpenDeveloperTools { get; set; }

    private void ConfigureShell()
    {
        _shell.CloseOverlay = CloseOverlay;
        _shell.SceneVisible = () => _scenes?.View.Visible == true;
        _shell.OpenMenu = () => _gameMenu.ShowPopup();
        _formation!.Opened += () => _shell.ShowWorkspace(_formation);
        _plan!.Opened += () => _shell.ShowWorkspace(_plan);
        _economy!.InspectorRequested += SelectObject;
        _economy.WorkspaceRequested += _shell.ShowWorkspace;
        _panels!.SelectionRequested += id => SelectObject(id);
        _panels.ListOpened += _shell.ShowList;
        _panels.InspectorOpened += SelectObject;
        _library!.Opened += LibraryOpened;

        _fleetNavigation = Navigation("Fleet", "▰", "车队", () => ToggleList(WorldListKind.Fleet));
        _edgeNavigation = Navigation("Edges", "╱", "线路", () => ToggleList(WorldListKind.Edges));
        Navigation("Nodes", "⌂", "聚居地", () => ToggleList(WorldListKind.Nodes));
        Navigation("EconomyReports", "▤", "报告", _economy.OpenReports);
        Navigation("BlueprintLibrary", "◇", "设计", _library.Open);
        Navigation("Combats", "⚔", "战斗", () => ToggleList(WorldListKind.Combats));

        // ReturnToMap 保持直接位于 Bar/TopBar 下，场景测量通过这个现有入口返回。
        for (var index = 0; index < _speeds.Count; index++) _bar.MoveChild(_speeds[index].Button, index);
        _bar.MoveChild(_clock, _speeds.Count);
        _breadcrumb = UiStyle.Label("");
        _breadcrumb.Name = "Breadcrumb";
        _breadcrumb.HorizontalAlignment = HorizontalAlignment.Center;
        _breadcrumb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _breadcrumb.ClipText = true;
        _breadcrumb.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _bar.AddChild(_breadcrumb);
        _wallet = UiStyle.Label(""); _wallet.Name = "Wallet";
        _bar.AddChild(_wallet);
        _noticeButton = UiStyle.Button("Notices", "通知", () => _notices!.ShowLog());
        _bar.AddChild(_noticeButton);
        _gameMenu = new MenuButton { Name = "GameMenu", Text = "菜单", CustomMinimumSize = new(0, 34), FocusMode = Control.FocusModeEnum.All };
        _bar.AddChild(_gameMenu);
        _gameMenu.GetPopup().IdPressed += id => _menuActions[(int)id]();
        MenuAction("保存", () => _menu.ShowSaveFailure(_saves?.Save()));
        MenuAction("存档与读档", _menu.OpenSaves);
        MenuAction("设置", _settings.Show);
        MenuAction("报告问题", _settings.OpenIssueReport);
        MenuAction("返回主菜单", () => _menu.ShowSaveFailure(_saves?.ReturnToMenu()));
        MenuAction("退出游戏", () => GetParent<Train.App.App>().RequestQuit());
        if (OS.IsDebugBuild())
        {
            MenuAction("开发工具", () => OpenDeveloperTools?.Invoke());
            if (_authoringPanel is not null) _authoringMenuIndex = MenuAction("创作模式", _authoringPanel.Open);
        }
        _shell.LayoutChanged += () => PanelAreaChanged?.Invoke();
    }

    private Button Navigation(string name, string symbol, string label, Action open)
    {
        var button = UiStyle.Button(name, symbol + "\n" + label, open);
        button.CustomMinimumSize = new(64, 58);
        button.TooltipText = label;
        button.ClipText = true;
        _shell.Navigation.AddChild(button);
        return button;
    }

    private int MenuAction(string label, Action action)
    {
        var index = _menuActions.Count;
        _gameMenu.GetPopup().AddItem(label, index);
        _menuActions.Add(action);
        return index;
    }

    private void ToggleList(WorldListKind kind)
    {
        if (_listKind == kind && _shell.ListContent is not null) _shell.ShowList(null);
        else { _listKind = kind; _panels!.OpenList(kind); }
    }

    private void LibraryOpened()
    {
        if (_editor.IsOpen || _authoring?.Form is not null)
        {
            // 编辑器保存蓝图后的库独立显示；隐藏世界外壳不会把它一起藏起来。
            _shell.ShowWorkspace(null);
            if (_library!.GetParent() != _root) _library.Reparent(_root);
            _library.Show(); _library.MoveToFront();
        }
        else _shell.ShowWorkspace(_library);
    }

    private bool CloseOverlay()
    {
        if (_errorPanel.Visible) { _errorPanel.Hide(); return true; }
        if (_notices?.CloseLog() == true) return true;
        if (_settings.Visible) { _settings.Hide(); return true; }
        if (_newGame?.Visible == true) { CancelNewGame(); return true; }
        return false;
    }

    public override void _Input(InputEvent input)
    {
        if (_editing && _library?.Visible == true && _library.GetParent() == _root &&
            input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        { _library.Hide(); GetViewport().SetInputAsHandled(); }
    }

    private void UpdateShell(HostStatus status, double delta)
    {
        var wallet = status.View.Economy.Wallet.Value;
        if (_lastWallet is { } previous && previous != wallet)
        { _walletChange = wallet - previous; _walletChangeRemaining = 3; }
        _lastWallet = wallet;
        _walletChangeRemaining = Math.Max(0, _walletChangeRemaining - delta);
        _wallet.Text = $"资金 {wallet}" + (_walletChangeRemaining > 0 ? $"（{_walletChange:+0;-0}）" : "");
        if (!status.PlayerPaused) _notificationPause = null;
        _clock.Text = UiInput.FormatTime(status.View.Position.CompletedSteps) + "  " +
            (_notificationPause is { } reason ? $"已暂停（{reason}）" : PauseText(status));
        _clock.TooltipText = $"实际 {status.ActualSpeed:0.00}×";
        _breadcrumb.Text = _scenes?.View.Visible == true && _scenes.ViewedLocation(status.View) is { } location && _content is not null
            ? "宏观地图 › " + WorldPanels.LocationText(location, status.View, _content) : "";
        _breadcrumb.TooltipText = _breadcrumb.Text;
        _noticeButton.Text = _notices?.UnreadCount > 0 ? $"通知 {_notices.UnreadCount}" : "通知";
        if (_authoringMenuIndex >= 0)
        {
            _gameMenu.GetPopup().SetItemDisabled(_authoringMenuIndex, !_authoring!.CanUse || _authoring.IsBusy || _host!.IsBusy);
            _gameMenu.GetPopup().SetItemTooltip(_authoringMenuIndex, "制作内容；需要可编辑的对局且当前没有其他操作。");
        }
        PositionOverlays();
    }

    private void RefreshNavigation(ICoreReadView view)
    {
        var needsAttention = view.World.Trains.Count(item => item.Activity == TrainActivity.NoRoute ||
            view.FindTrain(item.Id) is Lookup<TrainView>.Found { Value.Plan: null });
        var breaks = view.World.Edges.Sum(item => item.Breaks.Length);
        _fleetNavigation.Text = "▰\n车队" + (needsAttention == 0 ? "" : $" {needsAttention}");
        _fleetNavigation.TooltipText = $"没有运行计划或受阻：{needsAttention} 列";
        _edgeNavigation.Text = "╱\n线路" + (breaks == 0 ? "" : $" {breaks}");
        _edgeNavigation.TooltipText = $"断点：{breaks} 处";
    }

    private void PositionOverlays()
    {
        if (_shell is null || _map is null || _minimap is null || _status is null) return;
        var area = _shell.SceneArea;
        _map.OverlayArea = area;
        _map.PositionLayerPanel(area.Position.Y);
        _minimap.Position = new(area.End.X - _minimap.Size.X, area.Position.Y);
        _status.ClipContents = true;
        _status.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _status.Position = area.Position;
        _status.Size = area.Size;
        if (_notices is not null) _notices.ToastArea = new(_world.GlobalPosition + area.Position, area.Size);
    }
}

using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.App.Authoring;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

public partial class GameUi : CanvasLayer
{
    private EditorService _editor = null!;
    private DesignEditorPanel? _editorPanel;
    private BlueprintLibraryPanel? _library;
    private AuthoringService? _authoring;
    private AuthoringPanel? _authoringPanel;
    private TemplateSceneController? _templates;
    private Button? _authoringButton;
    private bool _editing;
    private bool _restoreEconomy;
    private bool _restoreFormation;
    private bool _restorePlan;
    private bool _restoreCombat;
    private HostCoordinator? _host;
    private RecordingService? _recording;
    private SaveService? _saves;
    private TrainLogger _logger = null!;
    private MainMenu _menu = null!;
    private Control _root = null!;
    private Control _world = null!;
    private SettingsPanel _settings = null!;
    private Label _clock = null!;
    private MacroMap _map = null!;
    private SceneStatusOverlay _status = null!;
    private NodeMinimap _minimap = null!;
    private WorldPanels? _panels;
    private TrainFormationPanel? _formation;
    private TrainPlanPanel? _plan;
    private EconomyPanels? _economy;
    private CombatPanel? _combat;
    private readonly BattleReportLog _reports = new();
    private SettlementFoundingPanel? _founding;
    private RailwayWorkPanel? _railway;
    private GoodsIconCache _icons = null!;
    private HFlowContainer _bar = null!;
    private readonly List<(SimulationSpeed? Speed, Button Button)> _speeds = [];
    private ICoreReadView? _view;
    private IContentView? _content;
    private HostBinding _binding;

    internal ICoreReadView? View => _view;
    internal string ErrorText => _error.Text;
    internal Rect2 PanelArea { get; private set; }
    internal Control World => _world;
    internal event Action? PanelAreaChanged;

    private void UpdatePanelArea()
    {
        var area = _root.GetGlobalRect().Grow(-8);
        if (_world.IsVisibleInTree())
        {
            var top = _bar.GetParent<Control>().GetGlobalRect().End.Y + 8;
            area = new(new(area.Position.X, top), new(area.Size.X, Math.Max(0, area.End.Y - top)));
        }
        if (PanelArea == area) return;
        PanelArea = area;
        _menu.FitSaves(area);
        PanelAreaChanged?.Invoke();
    }

    internal void SetInspectionLighting(InspectionLighting mode)
    {
        _scenes?.View.SetLighting(mode);
        _templates?.View.SetLighting(mode);
        _editorPanel?.Scene.View.SetLighting(mode);
    }

    private void SetHighQuality(bool high)
    {
        _scenes?.View.SetHighQuality(high);
        _templates?.View.SetHighQuality(high);
        _editorPanel?.Scene.View.SetHighQuality(high);
    }

    internal void Initialize(MainMenu menu, HostCoordinator? host, TrainLogger logger,
        WorldSceneController? scenes, RecordingService? recording, GoodsIconCache icons, EditorService editor,
        AuthoringService? authoring, TemplateSceneController? templates, NotificationPolicy policy, SaveService? saves)
    { _menu = menu; _host = host; _logger = logger; _scenes = scenes; _recording = recording; _icons = icons; _editor = editor; _authoring = authoring; _templates = templates; _policy = policy; _saves = saves; }

    public override void _Ready()
    {
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _world = new Control { Name = "World", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _world.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_world);
        _map = new MacroMap { Name = "MacroMap" };
        _map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _world.AddChild(_map);
        _status = new SceneStatusOverlay { Name = "SceneStatusOverlay", MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _world.AddChild(_status);
        _map.ObjectClicked += OpenScene;
        _map.CombatClicked += OpenCombat;
        _map.DisplayFailed += message =>
        { _logger.Warning("ui", "map_display_failed", message); ShowError(message); };
        _minimap = new();
        _world.AddChild(_minimap);
        var top = new PanelContainer { Name = "Bar", ThemeTypeVariation = "TopBar", MouseFilter = Control.MouseFilterEnum.Stop,
            MouseForcePassScrollEvents = false };
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        _world.AddChild(top);
        void PositionOverlays()
        {
            _minimap.Position = new(Math.Max(8, _world.Size.X - _minimap.Size.X - 12), top.Position.Y + top.Size.Y + 8);
            _map.PositionLayerPanel(top.Position.Y + top.Size.Y + 8);
        }
        top.Resized += PositionOverlays;
        _world.Resized += PositionOverlays;
        _bar = new HFlowContainer { Name = "TopBar" };
        _bar.AddThemeConstantOverride("h_separation", 12);
        top.AddChild(_bar);
        top.Resized += UpdatePanelArea;
        _root.Resized += UpdatePanelArea;
        _world.VisibilityChanged += UpdatePanelArea;
        _clock = UiStyle.Label("");
        _clock.Name = "Clock";
        _clock.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _bar.AddChild(_clock);
        if (_scenes is not null)
        {
            _returnToMap = UiStyle.Button("ReturnToMap", "返回地图", _scenes.ReturnToMap);
            _returnToMap.Hide();
            _bar.AddChild(_returnToMap);
            _scenes.ReturnedToMap += OnReturnedToMap;
            _scenes.Selected += OnSelected;
        }
        var speeds = new ButtonGroup();
        foreach (var speed in new SimulationSpeed?[] { null, SimulationSpeed.One, SimulationSpeed.Two, SimulationSpeed.Four, SimulationSpeed.Eight })
        {
            var button = UiStyle.Button(speed is null ? "Pause" : $"Speed{(int)speed.Value}",
                speed is null ? "暂停" : $"{(int)speed.Value}×", () => SetSpeed(speed));
            button.ToggleMode = true;
            button.ButtonGroup = speeds;
            button.ThemeTypeVariation = "Speed";
            _speeds.Add((speed, button));
            _bar.AddChild(button);
        }
        if (_host is not null)
        {
            _panels = new WorldPanels { Name = "WorldPanels" };
            _panels.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_panels);
            _bar.AddChild(UiStyle.Button("Nodes", "节点", () => _panels.OpenList(WorldListKind.Nodes)));
            _bar.AddChild(UiStyle.Button("Edges", "线路", () => _panels.OpenList(WorldListKind.Edges)));
            _bar.AddChild(UiStyle.Button("Fleet", "车队", () => _panels.OpenList(WorldListKind.Fleet)));
            _bar.AddChild(UiStyle.Button("Saves", "存档", _menu.OpenSaves));
            _formation = new TrainFormationPanel();
            _formation.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_formation);
            _plan = new TrainPlanPanel();
            _plan.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_plan);
            _panels.FormationRequested += _formation.Open;
            _panels.PlanRequested += _plan.Open;
            _economy = new EconomyPanels { Name = "EconomyPanels" };
            _economy.Initialize(_host, Submit, _icons, Submit);
            _world.AddChild(_economy);
            _bar.AddChild(UiStyle.Button("EconomyReports", "经营报告", _economy.OpenReports));
            _panels.SettlementRequested += _economy.OpenSettlement;
            _founding = new SettlementFoundingPanel { Name = "SettlementFounding" };
            _founding.Initialize(_host, Submit);
            _world.AddChild(_founding);
            _panels.FoundingRequested += (target, node) => _founding.Open(target, node);
            _founding.SettlementRequested += id =>
            {
                if (_host.GetStatus() is not { } status || _host.Content is not { } content) return;
                _economy.OpenSettlement(new(status.Binding, content.Generation, new WorldObjectId.Settlement(id)), id);
            };
            _railway = new RailwayWorkPanel { Name = "RailwayWork" };
            _railway.Initialize(_host, Submit);
            _world.AddChild(_railway);
            _panels.RailwayWorkRequested += (target, edge) => _railway.Open(target, edge);
            _panels.InventoryRequested += _economy.OpenTrainInventory;
            _economy.FormationRequested += _formation.Open;
            _combat = new CombatPanel { Name = "CombatPanel" };
            _combat.Initialize(Submit, ReportUiError);
            _world.AddChild(_combat);
            // 提示放在地图层之后的 Root 中：业务面板置前时不会盖住它，之后加入 Root 的设置、错误等界面仍在它之上。
            _notices = new NotificationPanel { Name = "NotificationPanel", ToastParent = _root };
            _notices.Chosen += OpenNotice;
            _world.AddChild(_notices);
            _bar.AddChild(UiStyle.Button("Combats", "战斗", () => _combat.Open()));
            _bar.AddChild(UiStyle.Button("Notices", "通知", () => _notices.ShowLog()));
            _panels.TrainCombatRequested += _combat.OpenTrain;
            _panels.SettlementCombatRequested += _combat.OpenSettlement;
            _formation.TrainCreated += train =>
            {
                if (_host.GetStatus() is not { } status || _host.Content is not { } content) return;
                _panels.Refresh(status.Binding, status.View, content);
                _panels.OpenObject(new WorldObjectId.Train(train));
            };
        }
        _errorPanel = UiStyle.Panel(_root, "ErrorPanel", "操作未完成", new(1250, 65), 540,
            () => _errorPanel.Hide(), out var errorBody);
        _errorPanel.Hide();
        _error = UiStyle.Label("");
        _error.Name = "Error";
        _error.CustomMinimumSize = new(500, 0);
        _error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        errorBody.AddChild(_error);
        _library = new BlueprintLibraryPanel { Name = "BlueprintLibrary" }; _library.Initialize(_editor); _root.AddChild(_library);
        _editorPanel = new DesignEditorPanel { Name = "DesignEditor" }; _editorPanel.Initialize(_editor); _editorPanel.LibraryRequested += _library.Open; _root.AddChild(_editorPanel);
        // 设置在加入场景树时立即应用画质，所以编辑器的场景视图要先创建。
        _settings = new SettingsPanel { Name = "SettingsPanel" };
        _settings.Initialize(_host, _logger, ShowError, _policy, _recording, _scenes?.View.CameraRig, _saves, SetHighQuality,
            show => _status.Always = show);
        _root.AddChild(_settings);
        _bar.AddChild(UiStyle.Button("Settings", "设置", _settings.Show));
        _bar.AddChild(UiStyle.Button("NewDesign", "新建设计…", _library.NewDraft));
        _bar.AddChild(UiStyle.Button("BlueprintLibrary", "蓝图库…", _library.Open));
        if (_authoring is not null)
        {
            _authoringPanel = new AuthoringPanel { Name = "Authoring" };
            _authoringPanel.Initialize(_authoring, _editor); _root.AddChild(_authoringPanel);
            if (_templates is not null) _templates.PlotSelected += _authoringPanel.SelectPlot;
            _authoringButton = UiStyle.Button("OpenAuthoring", "创作…", _authoringPanel.Open);
            _bar.AddChild(_authoringButton);
        }
        if (_panels is not null) _panels.RefitRequested += _editor.OpenInstance;
        if (_economy is not null)
        {
            _panels!.VehicleServiceRequested += _economy.OpenVehicleServices;
            _economy.ServiceDesignRequested += _editor.OpenInstance;
            _editorPanel.ServiceRequested += _economy.OpenVehicleServices;
        }
        _editor.Changed += OnEditorChanged;
        _menu.SetHostAvailable(_host is not null);
        _menu.NewGameRequested += OpenNewGame;
        _menu.SettingsRequested += _settings.Show;
        if (_host is not null)
        {
            _host.BoundaryCompleted += OnBoundary;
            _host.OperationEnded += OnOperationEnded;
            if (_host.GetStatus() is { } status && _host.Content is { } content)
                OnBoundary(status.Binding, status.View, content, []);
        }
        UpdatePanelArea();
    }

    public override void _Process(double delta)
    {
        var showingScene = _scenes?.View.Visible == true;
        var status = _host?.GetStatus();
        _status.Scenes = _scenes;
        _status.Core = status?.View;
        _status.Selected = _panels?.OpenInstances() ?? [];
        var editing = _editor.IsOpen || _authoring?.Form is not null;
        if (editing != _editing)
        {
            if (editing)
            {
                _restoreEconomy = _economy?.Visible == true;
                _restoreFormation = _formation?.Visible == true;
                _restorePlan = _plan?.Visible == true;
                _restoreCombat = _combat?.Visible == true;
            }
            else
            {
                if (_restoreEconomy && _economy is { Visible: false }) _economy.Show();
                if (_restoreFormation && _formation is { Visible: false }) _formation.Show();
                if (_restorePlan && _plan is { Visible: false }) _plan.Show();
                if (_restoreCombat && _combat is { Visible: false }) _combat.Show();
            }
            _editing = editing;
        }
        if (editing) _minimap.Clear();
        else _minimap.Refresh(_scenes, status?.View, status?.Binding ?? default, delta);
        if (_authoringButton is not null)
        {
            _authoringButton.Visible = _authoring!.CanUse;
            _authoringButton.Disabled = _authoring.IsBusy || _host!.IsBusy;
        }
        _map.Visible = status is not null && !showingScene && !editing;
        _map.RefreshPlatformQueues(delta);
        if (_panels is not null) _panels.Visible = !editing;
        if (_formation is not null) _formation.Visible = !editing && _formation.Visible;
        if (_plan is not null) _plan.Visible = !editing && _plan.Visible;
        if (_economy is not null) _economy.Visible = !editing && _economy.Visible;
        if (_combat is not null) _combat.Visible = !editing && _combat.Visible;
        if (_notices is not null) _notices.Visible = status is not null && !editing;
        if (_founding is not null) _founding.Visible = !editing;
        if (_railway is not null) _railway.Visible = !editing;
        if (editing) _editorPanel?.MoveToFront();
        _returnToMap?.SetVisible(showingScene);
        if (_newOperation != 0 && _host?.MissingConfirmation is { } missing &&
            missing.Operation == _newOperation && _reportedMissing != missing.Operation)
        {
            _reportedMissing = missing.Operation;
            var details = missing.Content.RuntimeMissing.Select(item => $"{item.Reference} · {item.Field}")
                .Concat(missing.Core.Select(item => $"{item.Reference} · {item.FieldPath}"));
            var message = "等待缺失确认，可确认继续或取消创建。\n" + string.Join("\n", details);
            _newGameError!.Text = message;
            _confirmMissing!.Show();
            _logger.Warning("ui", "new_game_missing_confirmation", message);
            ShowError(message);
            _missingErrorOperation = missing.Operation;
        }
        if (status is not null)
        {
            _clock.Text = $"{UiInput.FormatTime(status.View.Position.CompletedSteps)}   {PauseText(status)}   " +
                $"实际 {status.ActualSpeed:0.00}×   资金 {status.View.Economy.Wallet.Value}";
            foreach (var (speed, button) in _speeds)
            {
                button.Disabled = _host!.IsBusy || editing;
                button.SetPressedNoSignal(speed == (status.PlayerPaused ? null : (SimulationSpeed?)status.Speed));
            }
            if (_view is not null && _content is not null)
            {
                _panels!.Refresh(_binding, _view, _content);
                _formation!.Refresh(_binding, _view, _content);
                _plan!.Refresh(_binding, _view, _content);
                _economy!.Refresh(_binding, _view, _content);
                _combat!.Refresh(_binding, _view, _content, _host!.IsBusy, _reports);
                _founding?.Refresh(_binding, _view, _content);
                _railway?.Refresh(_binding, _view, _content);
            }
        }
        else if (_view is not null)
        {
            CloseSessionPanels();
            _icons.Bind(default, null);
            _view = null; _content = null; _binding = default;
            _world.Hide(); _errorPanel.Hide(); _settings.Hide(); _missingErrorOperation = null;
            _menu.Show();
        }
        _menu.SetGameStartEnabled(_host?.SessionOptions is not null && !_host.IsBusy && _host.Content is not null);
    }

    private void CloseSessionPanels()
    {
        _panels!.Invalidate();
        _formation!.Refresh(default, _view!, _content!);
        _plan!.Refresh(default, _view!, _content!);
        _economy!.Close();
        _founding?.Close();
        _railway?.Close();
        _formation.Hide(); _plan.Hide(); _combat?.Close(); _notices?.Hide();
        _reports.Clear();
        _notices?.Clear();
    }

    private void OnBoundary(HostBinding binding, ICoreReadView view, IContentView content,
        ImmutableArray<EventEnvelope> events)
    {
        if (_view is not null && _binding != binding) CloseSessionPanels();
        _economy?.Observe(binding, view, events);
        if (_notices is not null && _binding.Session != binding.Session) _notices.Begin(binding.Session);
        if (_notices is not null)
        {
            var previous = _binding == binding ? _view : null;
            foreach (var notice in events.Select(envelope => _policy.Classify(envelope.Event, previous, view))
                         .OfType<NotificationNotice>().Distinct())
                _notices.Add(notice, view, content);
        }
        _reports.Observe(binding, events);
        if (view.Fault is { } fault && (_binding != binding || _view?.Fault != fault))
            ShowError(CommandDiagnostics.DescribeFrozen(fault));
        _map.SetView(view, content, _binding != binding);
        _icons.Bind(binding, content);
        // 边界内 IsBusy 为 true；面板在 _Process 消费此视图，避免禁用按钮打断当前点击。
        _binding = binding; _view = view; _content = content;
        _menu.Hide(); _world.Show();
    }

    public override void _ExitTree()
    {
        _editor.Changed -= OnEditorChanged;
        if (_scenes is not null)
        { _scenes.ReturnedToMap -= OnReturnedToMap; _scenes.Selected -= OnSelected; }
        if (_host is not null)
        { _host.BoundaryCompleted -= OnBoundary; _host.OperationEnded -= OnOperationEnded; }
        _menu.NewGameRequested -= OpenNewGame;
        _menu.SettingsRequested -= _settings.Show;
    }
}

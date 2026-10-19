using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.App.Authoring;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
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
    private HostCoordinator? _host;
    private WorldSceneController? _scenes;
    private RecordingService? _recording;
    private TrainLogger _logger = null!;
    private MainMenu _menu = null!;
    private Control _root = null!;
    private Control _world = null!;
    private SettingsPanel _settings = null!;
    private Label _error = null!;
    private long? _missingErrorOperation;
    private PanelContainer _errorPanel = null!;
    private Button? _createNewGame;
    private Button? _confirmMissing;
    private Button? _returnToMap;
    private Label _clock = null!;
    private MacroMap _map = null!;
    private NodeMinimap _minimap = null!;
    private WorldPanels? _panels;
    private TrainFormationPanel? _formation;
    private TrainPlanPanel? _plan;
    private EconomyPanels? _economy;
    private NotificationPolicy _policy = null!;
    private NotificationPanel? _notices;
    private CombatPanel? _combat;
    private readonly BattleReportLog _reports = new();
    private SettlementFoundingPanel? _founding;
    private RailwayWorkPanel? _railway;
    private GoodsIconCache _icons = null!;
    private HFlowContainer _bar = null!;
    private readonly List<(SimulationSpeed? Speed, Button Button)> _speeds = [];
    private PanelContainer? _newGame;
    private Label? _newGameError;
    private long _newOperation;
    private long _reportedMissing;
    private ICoreReadView? _view;
    private IContentView? _content;
    private HostBinding _binding;

    internal ICoreReadView? View => _view;
    internal string ErrorText => _error.Text;
    internal Rect2 PanelArea { get; private set; }
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

    internal void Initialize(MainMenu menu, HostCoordinator? host, TrainLogger logger,
        WorldSceneController? scenes, RecordingService? recording, GoodsIconCache icons, EditorService editor,
        AuthoringService? authoring, TemplateSceneController? templates, NotificationPolicy policy)
    { _menu = menu; _host = host; _logger = logger; _scenes = scenes; _recording = recording; _icons = icons; _editor = editor; _authoring = authoring; _templates = templates; _policy = policy; }

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
            _notices = new NotificationPanel { Name = "NotificationPanel" };
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
        _error.AddThemeColorOverride("font_color", UiStyle.Error);
        errorBody.AddChild(_error);
        _settings = new SettingsPanel { Name = "SettingsPanel" };
        _settings.Initialize(_host, _logger, ShowError, _policy, _recording, _scenes?.View.CameraRig);
        _root.AddChild(_settings);
        _bar.AddChild(UiStyle.Button("Settings", "设置", _settings.Show));
        _library = new BlueprintLibraryPanel { Name = "BlueprintLibrary" }; _library.Initialize(_editor); _root.AddChild(_library);
        _editorPanel = new DesignEditorPanel { Name = "DesignEditor" }; _editorPanel.Initialize(_editor); _editorPanel.LibraryRequested += _library.Open; _root.AddChild(_editorPanel);
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
        var editing = _editor.IsOpen || _authoring?.Form is not null;
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
            _clock.Text = $"{UiInput.FormatTime(status.View.Position.CompletedSteps)}   {(status.IsPaused ? "已暂停" : "运行中")}   实际 {status.ActualSpeed:0.00}×";
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
            _panels!.Invalidate();
            _formation!.Refresh(default, _view, _content!);
            _plan!.Refresh(default, _view, _content!);
            _economy!.Close();
            _founding?.Close();
            _railway?.Close();
            _icons.Bind(default, null);
            _formation.Hide(); _plan.Hide(); _combat?.Hide(); _notices?.Hide();
            _reports.Clear();
            _notices?.Clear();
            _view = null; _content = null; _binding = default;
            _world.Hide(); _errorPanel.Hide(); _settings.Hide(); _missingErrorOperation = null;
            _menu.Show();
        }
        _menu.SetGameStartEnabled(_host?.SessionOptions is not null && !_host.IsBusy && _host.Content is not null);
    }

    private void OnEditorChanged()
    {
        if (_editor is { IsOpen: false, HasError: true, Message: { Length: > 0 } message }) ShowError(message);
    }

    private void Report(CoreProblem? problem)
    {
        if (problem is null) return;
        var message = CommandDiagnostics.Describe(problem);
        _logger.Warning("ui", "simulation_control_denied", message);
        ShowError(message);
    }

    private void SetSpeed(SimulationSpeed? speed)
    {
        if (_host is null) return;
        if (speed is null) { Report(_host.SetPaused(true)); return; }
        var problem = _host.SetSpeed(speed.Value);
        if (problem is not null) { Report(problem); return; }
        Report(_host.SetPaused(false));
    }

    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command)
        => Submit(target, command, null);

    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command, IContentCandidate? candidate)
    {
        var status = _host?.GetStatus();
        if (status is null || _host!.Content is not { } content || target is null ||
            !target.IsAvailable(status.Binding, status.View, _host.PanelGenerationCurrent(target.Binding, target.Generation)) || _host.IsBusy)
        {
            ReportUiError("当前操作不可提交：对象已失效或宿主正在操作。");
            return null;
        }
        SubmissionResult result;
        try { result = _host.Submit(target.Binding, command, CommandSource.UserInterface, candidate); }
        catch (NotImplementedException)
        {
            _logger.Warning("ui", "command_not_implemented", command.GetType().Name);
            ShowError("该操作尚未实现。");
            return null;
        }
        if (CommandDiagnostics.Report(_logger, result) is { } problem) ShowError(problem);
        return (result as SubmissionResult.Completed)?.Receipt.Decision as CommandDecision.Accepted;
    }

    private void ReportUiError(string message)
    {
        _logger.Warning("ui", "ui_operation_invalid", message);
        ShowError(message);
    }

    private void OnBoundary(HostBinding binding, ICoreReadView view, IContentView content,
        ImmutableArray<EventEnvelope> events)
    {
        _economy?.Observe(binding, view, events);
        if (_notices is not null && _binding.Session != binding.Session) _notices.Begin(binding.Session);
        if (_notices is not null)
        {
            var previous = _binding == binding ? _view : null;
            foreach (var envelope in events)
                if (_policy.Classify(envelope.Event, previous, view) is { } notice)
                    _notices.Add(notice, view, content);
        }
        _reports.Observe(binding, events);
        if (view.Fault is { } fault && (_binding != binding || _view?.Fault != fault))
            ShowError(CommandDiagnostics.Describe(fault));
        _map.SetView(view, content, _binding != binding);
        _icons.Bind(binding, content);
        // 边界内 IsBusy 为 true；面板在 _Process 消费此视图，避免禁用按钮打断当前点击。
        _binding = binding; _view = view; _content = content;
        _menu.Hide(); _world.Show();
    }

    private void OpenScene(WorldObjectId id)
    {
        if (_scenes is null || _content is null || _host?.GetStatus() is not { } status || _host.Content is null ||
            !new PanelTarget(_binding, _content.Generation, id).IsAvailable(status.Binding, status.View,
                _host.PanelGenerationCurrent(_binding, _content.Generation)))
            return;
        WorldSceneTarget? target = id switch
        {
            WorldObjectId.Node node => new WorldSceneTarget.Location(new WorldLocation.Node(node.Id)),
            WorldObjectId.Edge edge when status.View.FindEdge(edge.Id) is Lookup<EdgeView>.Found found =>
                new WorldSceneTarget.Location(new WorldLocation.Edge(edge.Id, found.Value.Map.LengthMeters / 2)),
            WorldObjectId.Train train when status.View.FindTrain(train.Id) is Lookup<TrainView>.Found found &&
                !found.Value.Vehicles.IsEmpty => new WorldSceneTarget.Follow(train.Id, found.Value.Vehicles[0]),
            _ => null
        };
        if (target is null) { ReportUiError("对象没有可观察的场景目标。"); return; }
        _scenes.Open(target);
        var openDetail = id is WorldObjectId.Edge || id is WorldObjectId.Node clicked &&
            status.View.World.Nodes.FirstOrDefault(item => item.Id == clicked.Id)?.Occupant is NodeOccupant.Empty;
        if (openDetail)
        {
            _panels?.Refresh(_binding, status.View, _content!);
            _panels?.OpenObject(id);
        }
    }

    private void OpenCombat(CombatId id)
    {
        if (_host?.GetStatus() is not { } status) return;
        var item = status.View.World.Combats.FirstOrDefault(combat => combat.Id == id);
        if (item is null) { ReportUiError("已不存在"); return; }
        OpenLocation(item.Location);
    }

    private void OpenNotice(NotificationNotice notice)
    {
        if (_host?.GetStatus() is not { } status) { ReportUiError("已不存在"); return; }
        var view = status.View;
        switch (notice.Subject)
        {
            case WorldObjectId.Settlement settlement:
                var occupied = view.World.Nodes.FirstOrDefault(item =>
                    item.Occupant is NodeOccupant.Settlement node && node.Id == settlement.Id);
                if (occupied is null) { ReportUiError("已不存在"); return; }
                OpenLocation(new WorldLocation.Node(occupied.Id));
                return;
            case WorldObjectId.Instance instance:
                if (view.FindInstance(instance.Id) is not Lookup<InstanceView>.Found found ||
                    !LocationExists(view, found.Value.Location))
                { ReportUiError("已不存在"); return; }
                OpenLocation(found.Value.Location);
                return;
            case WorldObjectId.Train train:
                if (!view.World.Trains.Any(item => item.Id == train.Id)) { ReportUiError("已不存在"); return; }
                OpenScene(new WorldObjectId.Train(train.Id));
                return;
            case WorldObjectId.Combat combat:
                var listed = view.World.Combats.FirstOrDefault(item => item.Id == combat.Id);
                if (listed is null) { ReportUiError("已不存在"); return; }
                OpenLocation(listed.Location);
                return;
            case { } subject when !SubjectExists(view, subject):
                ReportUiError("已不存在");
                return;
        }
        if (notice.Location is { } location) OpenLocation(location);
        else ReportUiError("已不存在");
    }

    private void OpenLocation(WorldLocation location)
    {
        if (_scenes is null || _host?.GetStatus() is not { } status || !LocationExists(status.View, location))
        { ReportUiError("已不存在"); return; }
        _scenes.Open(new WorldSceneTarget.Location(location));
    }

    private static bool LocationExists(ICoreReadView view, WorldLocation location) => location switch
    {
        WorldLocation.Node node => view.World.Nodes.Any(item => item.Id == node.Id),
        WorldLocation.Edge edge => view.World.Edges.Any(item => item.Id == edge.Id),
        _ => false
    };

    private static bool SubjectExists(ICoreReadView view, WorldObjectId subject) => subject switch
    {
        WorldObjectId.Combat combat => view.World.Combats.Any(item => item.Id == combat.Id),
        WorldObjectId.Train train => view.World.Trains.Any(item => item.Id == train.Id),
        WorldObjectId.Node node => view.World.Nodes.Any(item => item.Id == node.Id),
        WorldObjectId.Edge edge => view.World.Edges.Any(item => item.Id == edge.Id),
        WorldObjectId.Settlement settlement => view.World.Nodes.Any(item =>
            item.Occupant is NodeOccupant.Settlement occupied && occupied.Id == settlement.Id),
        WorldObjectId.Lair lair => view.World.Nodes.Any(item =>
            item.Occupant is NodeOccupant.Lair occupied && occupied.Id == lair.Id),
        WorldObjectId.Instance instance => view.FindInstance(instance.Id) is Lookup<InstanceView>.Found,
        _ => false
    };

    private void OnReturnedToMap(SceneProblem? problem)
    {
        if (problem is not null) ShowError($"{problem.Code}：{problem.Detail}");
    }

    private void OnSelected(PickSubject subject)
    {
        if (_scenes?.View.Visible != true || _host?.GetStatus() is not { } status || _host.Content is not { } content)
            return;
        WorldObjectId? id = subject switch
        {
            PickSubject.WorldObject item => item.Object,
            PickSubject.Terrain { Location: WorldLocation.Node node } => new WorldObjectId.Node(node.Id),
            PickSubject.Terrain { Location: WorldLocation.Edge edge } => new WorldObjectId.Edge(edge.Id),
            _ => null
        };
        if (id is null || _content is null ||
            !new PanelTarget(_binding, _content.Generation, id).IsAvailable(status.Binding, status.View,
                _host.PanelGenerationCurrent(_binding, _content.Generation)))
            return;
        _panels!.Refresh(status.Binding, status.View, content);
        _panels.OpenObject(id);
    }

    private void OpenNewGame()
    {
        if (_host?.Content is not { } content || _host.SessionOptions is null) return;
        if (_newGame is not null)
        {
            _root.RemoveChild(_newGame); _newGame.QueueFree();
        }
        _newGame = UiStyle.Panel(_root, "NewGame", "新游戏", new(720, 260), 480,
            CancelNewGame, out var body, centered: true);
        body.AddChild(UiStyle.Label("世界种子", true));
        var seed = new LineEdit { Name = "Seed", Text = "0", CustomMinimumSize = new(430, 38) };
        body.AddChild(seed);
        body.AddChild(UiStyle.Label("生成配置", true));
        var profiles = content.GenerationProfiles;
        var profile = new OptionButton { Name = "Profile", CustomMinimumSize = new(430, 38) };
        foreach (var item in profiles) profile.AddItem(item.Display.Name);
        body.AddChild(profile);
        body.AddChild(UiStyle.Label("创建成功后显示暂停的宏观地图。"));
        _newGameError = UiStyle.Label(profiles.IsEmpty ? "没有可用的生成配置。" : "");
        _newGameError.Name = "Problem";
        _newGameError.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _newGameError.AddThemeColorOverride("font_color", UiStyle.Error);
        body.AddChild(_newGameError);
        _confirmMissing = UiStyle.Button("ConfirmMissing", "确认继续", () =>
        {
            var operation = _newOperation;
            if (operation == 0 || _host!.MissingConfirmation?.Operation != operation) return;
            if (_host.ConfirmMissing(operation))
            {
                if (_missingErrorOperation == operation) { _errorPanel.Hide(); _missingErrorOperation = null; }
                return;
            }
            const string message = "缺失确认已失效，请重新创建对局。";
            _logger.Warning("ui", "new_game_confirmation_expired", message);
            ShowError(message);
        }, true);
        _confirmMissing.Hide();
        body.AddChild(_confirmMissing);
        body.AddChild(UiStyle.Button("Cancel", "取消", CancelNewGame));
        var create = _createNewGame = UiStyle.Button("Create", "创建对局", () =>
        {
            if (!ulong.TryParse(seed.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            { _newGameError.Text = "种子必须是 0 至 18446744073709551615 的整数。"; return; }
            if (profile.Selected < 0) return;
            void Start()
            {
            var result = _host.BeginNewGame(profiles[profile.Selected].Id, value, "stage1-world-v1", _host.SessionOptions);
            if (result.Problem is { } problem)
            {
                var message = CommandDiagnostics.Describe(problem);
                _logger.Warning("application", "host_new_game_denied", message);
                ShowError(message);
                _newGameError.Text = message;
                return;
            }
            _newOperation = result.Operation;
            _confirmMissing!.Hide();
            _newGameError.Text = "正在创建对局，可取消。";
            _createNewGame!.Disabled = true;
            }
            if (_editor.RequestClose(Start) is { } failure) ShowError(failure.Message);
        }, true);
        create.Disabled = profiles.IsEmpty;
        body.AddChild(create);
    }

    private void CancelNewGame()
    {
        if (_newOperation != 0) _host!.Cancel(_newOperation);
        _newOperation = 0;
        _newGame?.Hide();
    }

    private void OnOperationEnded(HostOperationResult result)
    {
        if (result.Kind == OperationKind.Restore && result.Succeeded)
            _menu.CloseSaves();
        if (!result.Succeeded && result.Status is not (HostOperationStatus.Cancelled or HostOperationStatus.Discarded))
        {
            var message = string.Join("；", result.Problems.Select(CommandDiagnostics.Describe)
                .Concat(result.Diagnostics.Select(problem => problem.Message)));
            var operation = result.Kind switch
            { OperationKind.NewGame => "新游戏", OperationKind.Restore => "读档", OperationKind.Reload => "内容重载", _ => "宿主操作" };
            if (result.Status == HostOperationStatus.Faulted)
                message = result.Fault is { } fault ? CommandDiagnostics.Describe(fault) : $"{operation}发生故障：{result.FaultCode}。";
            else if (message.Length == 0) message = result.Status == HostOperationStatus.NotAccepted
                ? $"{operation}当前无法执行，请稍后重试。" : $"{operation}准备失败，请检查内容或输入。";
            ShowError(message);
            if (_newOperation == result.Operation) _newGameError!.Text = message;
        }
        if (_newOperation != result.Operation) return;
        _newOperation = 0;
        _confirmMissing!.Hide();
        _createNewGame!.Disabled = false;
        if (result.Succeeded || result.Status == HostOperationStatus.Cancelled)
            _newGame!.Hide();
    }

    internal void ShowError(string message)
    {
        _missingErrorOperation = null;
        _error.Text = message; _errorPanel.Show(); _errorPanel.MoveToFront();
        _errorPanel.Position = new(Math.Max(16, _root.Size.X - _errorPanel.Size.X - 24), 68);
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

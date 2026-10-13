using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class GameUi : CanvasLayer
{
    private HostCoordinator? _host;
    private TrainLogger _logger = null!;
    private MainMenu _menu = null!;
    private Control _root = null!;
    private Control _world = null!;
    private SettingsPanel _settings = null!;
    private Label _error = null!;
    private PanelContainer _errorPanel = null!;
    private Button? _createNewGame;
    private Button? _confirmMissing;
    private Label _clock = null!;
    private MacroMap _map = null!;
    private WorldPanels? _panels;
    private TrainFormationPanel? _formation;
    private TrainPlanPanel? _plan;
    private HFlowContainer _bar = null!;
    private readonly Dictionary<int, Button> _speeds = [];
    private HostBinding _displayedBinding;
    private ICoreReadView? _displayedView;
    private IContentView? _displayedContent;
    private PanelContainer? _newGame;
    private Label? _newGameError;
    private long _newOperation;
    private long _reportedMissing;
    private ICoreReadView? _view;
    private IContentView? _content;
    private HostBinding _binding;

    internal event Action<GenerationProfileId, ulong>? NewGameRequested;
    internal ICoreReadView? View => _view;
    internal string ErrorText => _error.Text;

    internal void Initialize(MainMenu menu, HostCoordinator? host, TrainLogger logger)
    { _menu = menu; _host = host; _logger = logger; }

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
        _map.DisplayFailed += message =>
        { _logger.Warning("ui", "map_display_failed", message); ShowError(message); };
        var top = new PanelContainer { Name = "Bar", ThemeTypeVariation = "TopBar", MouseFilter = Control.MouseFilterEnum.Stop,
            MouseForcePassScrollEvents = false };
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        _world.AddChild(top);
        _bar = new HFlowContainer { Name = "TopBar" };
        _bar.AddThemeConstantOverride("h_separation", 12);
        top.AddChild(_bar);
        _clock = UiStyle.Label("");
        _clock.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _bar.AddChild(_clock);
        var speeds = new ButtonGroup();
        foreach (var speed in new[] { 0, 1, 2, 4, 8 })
        {
            var button = UiStyle.Button(speed == 0 ? "Pause" : $"Speed{speed}",
                speed == 0 ? "暂停" : $"{speed}×", () => SetSpeed(speed));
            button.ToggleMode = true;
            button.ButtonGroup = speeds;
            button.ThemeTypeVariation = "Speed";
            _speeds.Add(speed, button);
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
            _formation = new TrainFormationPanel();
            _formation.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_formation);
            _plan = new TrainPlanPanel();
            _plan.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_plan);
            _panels.FormationRequested += _formation.Open;
            _panels.PlanRequested += _plan.Open;
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
        _settings.Initialize(_host, _logger, ShowError);
        _root.AddChild(_settings);
        _bar.AddChild(UiStyle.Button("Settings", "设置", _settings.Show));
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
    }

    public override void _Process(double delta)
    {
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
        }
        if (_host?.GetStatus() is { } status)
        {
            _clock.Text = $"{UiInput.FormatTime(status.View.Position.CompletedSteps)}   {(status.IsPaused ? "已暂停" : "运行中")}   实际 {status.ActualSpeed:0.00}×";
            foreach (var (speed, button) in _speeds)
            {
                button.Disabled = _host.IsBusy;
                button.SetPressedNoSignal(speed == (status.PlayerPaused ? 0 : (int)status.Speed));
            }
            if (_view is not null && _content is not null)
            {
                if (!ReferenceEquals(_displayedView, _view) || !ReferenceEquals(_displayedContent, _content))
                {
                    _map.SetView(_view, _content, _displayedBinding != _binding);
                    _displayedBinding = _binding; _displayedView = _view; _displayedContent = _content;
                }
                _panels!.Refresh(_binding, _view, _content);
                _formation!.Refresh(_binding, _view, _content);
                _plan!.Refresh(_binding, _view, _content);
            }
        }
        _menu.GetNode<Button>("%NewGame").Disabled = _host is null || _host.IsBusy || _host.Content is null;
    }

    private void Report(CoreProblem? problem)
    {
        if (problem is null) return;
        var message = CommandDiagnostics.Describe(problem);
        _logger.Warning("ui", "simulation_control_denied", message);
        ShowError(message);
    }

    private void SetSpeed(int speed)
    {
        if (_host is null) return;
        if (speed == 0) { Report(_host.SetPaused(true)); return; }
        var problem = _host.SetSpeed((SimulationSpeed)speed);
        if (problem is not null) { Report(problem); return; }
        Report(_host.SetPaused(false));
    }

    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command)
    {
        var status = _host?.GetStatus();
        if (status is null || _host!.Content is not { } content || target is null ||
            !target.IsAvailable(status.Binding, content.Generation, status.View) || _host.IsBusy)
        {
            ReportUiError("当前操作不可提交：对象已失效或宿主正在操作。");
            return null;
        }
        var result = _host.Submit(target.Binding, command, CommandSource.UserInterface);
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
        if (view.Fault is { } fault && (_binding != binding || _view?.Fault != fault))
            ShowError(CommandDiagnostics.Describe(fault));
        _binding = binding; _view = view; _content = content;
        _menu.Hide(); _world.Show();
    }

    private void OpenNewGame()
    {
        if (_host?.Content is not { } content) return;
        if (_newGame is not null)
        {
            _root.RemoveChild(_newGame); _newGame.QueueFree();
        }
        _newGame = UiStyle.Panel(_root, "NewGame", "新游戏", new(720, 260), 480,
            CancelNewGame, out var body);
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
            var missingMessage = _newGameError!.Text;
            if (_host.ConfirmMissing(operation))
            {
                if (_error.Text == missingMessage) _errorPanel.Hide();
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
            NewGameRequested?.Invoke(profiles[profile.Selected].Id, value);
        }, true);
        create.Disabled = profiles.IsEmpty;
        body.AddChild(create);
        _newGame.Resized += () => _newGame.Position = (_root.Size - _newGame.Size) / 2;
    }

    internal void NewGameStarted((long Operation, CoreProblem? Problem) result)
    {
        if (result.Problem is { } problem)
        {
            var message = CommandDiagnostics.Describe(problem);
            _logger.Warning("application", "host_new_game_denied", message);
            ShowError(message);
            _newGameError!.Text = message;
            return;
        }
        _newOperation = result.Operation;
        _confirmMissing!.Hide();
        _newGameError!.Text = "正在创建对局，可取消。";
        _createNewGame!.Disabled = true;
    }

    private void CancelNewGame()
    {
        if (_newOperation != 0) _host!.Cancel(_newOperation);
        _newOperation = 0;
        _newGame?.Hide();
    }

    private void OnOperationEnded(HostOperationResult result)
    {
        if (result.Status is not (HostOperationStatus.Committed or HostOperationStatus.CommittedWithWarnings or
            HostOperationStatus.Cancelled or HostOperationStatus.Discarded))
        {
            var message = string.Join("；", result.Problems.Select(CommandDiagnostics.Describe)
                .Concat(result.Diagnostics.Select(problem => problem.Message)));
            var operation = result.Kind switch
            { OperationKind.NewGame => "新游戏", OperationKind.Restore => "读档", OperationKind.Reload => "内容重载", _ => "宿主操作" };
            if (result.Status == HostOperationStatus.Faulted)
                message = result.Fault is { } fault ? CommandDiagnostics.Describe(fault) : $"{operation}发生故障：{result.FaultCode}。";
            else if (message.Length == 0) message = $"{operation}准备失败：{result.Status}。";
            ShowError(message);
            if (_newOperation == result.Operation) _newGameError!.Text = message;
        }
        if (_newOperation != result.Operation) return;
        _newOperation = 0;
        _confirmMissing!.Hide();
        _createNewGame!.Disabled = false;
        if (result.Status is HostOperationStatus.Committed or HostOperationStatus.CommittedWithWarnings or HostOperationStatus.Cancelled)
            _newGame!.Hide();
    }

    internal void ShowError(string message)
    {
        _error.Text = message; _errorPanel.Show(); _errorPanel.MoveToFront();
        _errorPanel.Position = new(Math.Max(16, _root.Size.X - _errorPanel.Size.X - 24), 68);
    }

    public override void _ExitTree()
    {
        if (_host is not null)
        { _host.BoundaryCompleted -= OnBoundary; _host.OperationEnded -= OnOperationEnded; }
        _menu.NewGameRequested -= OpenNewGame;
        _menu.SettingsRequested -= _settings.Show;
    }
}

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
    private bool _editing;
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
            _shell.TopInset = top;
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

    public override void _Ready() => BuildUi();

    public override void _Process(double delta)
    {
        var showingScene = _scenes?.View.Visible == true;
        var status = _host?.GetStatus();
        _status.Scenes = _scenes;
        _status.Core = status?.View;
        _status.Selected = _shell.SelectedObject is WorldObjectId.Instance selected ? [selected.Id] : [];
        var editing = _editor.IsOpen || _authoring?.Form is not null;
        _editing = editing;
        _world.Visible = status is not null && !editing;
        if (editing) _minimap.Clear();
        else _minimap.Refresh(_scenes, status?.View, status?.Binding ?? default, delta);
        _map.Visible = status is not null && !showingScene && !editing;
        _map.RefreshPlatformQueues(delta);
        if (editing)
        {
            _editorPanel?.MoveToFront();
            if (_library?.Visible == true && _library.GetParent() == _root) _library.MoveToFront();
        }
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
            UpdateShell(status, delta);
            foreach (var (speed, button) in _speeds)
            {
                button.Disabled = _host!.IsBusy || editing;
                button.SetPressedNoSignal(speed == (status.PlayerPaused ? null : (SimulationSpeed?)status.Speed));
            }
            if (_view is not null && _content is not null)
            {
                RefreshSelection();
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
        _shell.ShowWorkspace(null); _shell.Inspect(null, null); _shell.ShowList(null);
        _lastWallet = null; _notificationPause = null;
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
            {
                _notices.Add(notice, view, content);
                if (_policy.Action(notice.Category) == NotificationAction.Pause)
                    _notificationPause = NotificationPolicy.Label(notice.Category);
            }
        }
        _reports.Observe(binding, events);
        if (view.Fault is { } fault && (_binding != binding || _view?.Fault != fault))
            ShowError(CommandDiagnostics.DescribeFrozen(fault));
        _map.SetView(view, content, _binding != binding);
        _icons.Bind(binding, content);
        // 边界内 IsBusy 为 true；面板在 _Process 消费此视图，避免禁用按钮打断当前点击。
        _binding = binding; _view = view; _content = content;
        RefreshNavigation(view);
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

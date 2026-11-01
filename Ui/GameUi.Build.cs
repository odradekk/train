using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    private void BuildUi()
    {
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _world = _shell = new GameShell { Name = "World", Visible = false };
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
        _shell.LayoutChanged += PositionOverlays;
        _minimap.Resized += PositionOverlays;
        _bar = new HFlowContainer { Name = "TopBar" };
        _bar.AddThemeConstantOverride("h_separation", 12);
        top.AddChild(_bar);
        top.Resized += UpdatePanelArea;
        _root.Resized += UpdatePanelArea;
        _world.VisibilityChanged += UpdatePanelArea;
        _clock = UiStyle.Label("");
        _clock.Name = "Clock";
        _clock.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
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
            _formation = new TrainFormationPanel();
            _formation.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_formation);
            _plan = new TrainPlanPanel();
            _plan.Initialize(_host, Submit, ReportUiError);
            _world.AddChild(_plan);
            _panels.FormationRequested += _formation.Open;
            _panels.PlanRequested += _plan.Open;
            _economy = new EconomyPanels { Name = "EconomyPanels" };
            _economy.Initialize(_host, Submit, _icons, Submit, SubmitInline);
            _world.AddChild(_economy);
            _founding = new SettlementFoundingPanel { Name = "SettlementFounding" };
            _founding.Initialize(_host, Submit);
            _world.AddChild(_founding);
            _panels.FoundingRequested += (target, node) => { _founding.Open(target, node); _shell.ShowWorkspace(_founding.Content); };
            _founding.SettlementRequested += id =>
            {
                if (_host.GetStatus() is not { } status || _host.Content is not { } content) return;
                _economy.OpenSettlement(new(status.Binding, content.Generation, new WorldObjectId.Settlement(id)), id);
            };
            _railway = new RailwayWorkPanel { Name = "RailwayWork" };
            _railway.Initialize(_host, Submit);
            _world.AddChild(_railway);
            _panels.RailwayWorkRequested += (target, edge) => { _railway.Open(target, edge); _shell.ShowWorkspace(_railway.Content); };
            _panels.InventoryRequested += _economy.OpenTrainInventory;
            _economy.FormationRequested += _formation.Open;
            _combat = new CombatPanel { Name = "CombatPanel" };
            _combat.Initialize(Submit, ReportUiError);
            _world.AddChild(_combat);
            _economy.SettlementCombatRequested += settlement =>
            { _combat.OpenSettlement(settlement); SelectObject(new WorldObjectId.Settlement(settlement), _combat); };
            // 提示放在地图层之后的 Root 中：业务面板置前时不会盖住它，之后加入 Root 的设置、错误等界面仍在它之上。
            _notices = new NotificationPanel { Name = "NotificationPanel", ToastParent = _root };
            _notices.Chosen += OpenNotice;
            _world.AddChild(_notices);
            _panels.TrainCombatRequested += train => { _combat.OpenTrain(train); SelectObject(new WorldObjectId.Train(train), _combat); };
            _formation.TrainCreated += train =>
            {
                if (_host.GetStatus() is not { } status || _host.Content is not { } content) return;
                _panels.Refresh(status.Binding, status.View, content);
                SelectObject(new WorldObjectId.Train(train));
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
        if (_authoring is not null)
        {
            _authoringPanel = new AuthoringPanel { Name = "Authoring" };
            _authoringPanel.Initialize(_authoring, _editor); _root.AddChild(_authoringPanel);
            if (_templates is not null) _templates.PlotSelected += _authoringPanel.SelectPlot;
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
        ConfigureShell();
        if (_host is not null)
        {
            _host.BoundaryCompleted += OnBoundary;
            _host.OperationEnded += OnOperationEnded;
            if (_host.GetStatus() is { } status && _host.Content is { } content)
                OnBoundary(status.Binding, status.View, content, []);
        }
        _world.MoveChild(_map, 0);
        UpdatePanelArea();
    }

}

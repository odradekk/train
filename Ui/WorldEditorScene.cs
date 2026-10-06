using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Globalization;
using Godot;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

public partial class WorldEditorScene : Node3D
{
    public const string SmokeEnvironmentVariable = "TRAIN_WORLD_EDITOR_SMOKE";
    public const string SaveIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_SAVE_ID";
    public const string CaptureEnvironmentVariable = "TRAIN_WORLD_EDITOR_CAPTURE";
    public const string CaptureStepsEnvironmentVariable = "TRAIN_WORLD_EDITOR_CAPTURE_STEPS";
    public const string AuthorityEnvironmentVariable = "TRAIN_AUTHORITY_CHECKPOINT";
    public const string CrossProcessEnvironmentVariable = "TRAIN_WORLD_EDITOR_CROSS_PROCESS";

    internal static readonly WorldVoxelCoord EmptyProbe = new(100, 10, 15);
    internal static readonly ObservationCameraPose OverviewPose = new(new ObservationVector(76, 16, -8), new ObservationVector(76, 2, 16));

    private readonly VoxelWorldMeshPresenter _meshes = new();
    private readonly VoxelWorldCollisionPresenter _colliders = new();
    private readonly CompositeVisualPresenter _composites = new();
    private readonly TrackRendererPresenter _tracks = new();
    private VoxelDevelopmentSession? _session;
    private Camera3D _camera = null!;
    private Label _source = null!;
    private Label _state = null!;
    private Label _result = null!;
    private SpinBox _stepCount = null!;
    private SpinBox[] _worldCell = null!;
    private SpinBox[] _localCell = null!;
    private SpinBox _editAmount = null!;
    private SpinBox _editDelay = null!;
    private Label _edits = null!;
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private ConfirmationDialog _historyPrompt = null!;
    private WorldEditorOperation? _pendingHistoryOperation;
    private Label _historyState = null!;
    private bool _dirty = true;
    private WorldEditorSmoke? _smoke;
    private WorldEditorObservationSmoke? _observationSmoke;
    private WorldEditorSwitchSmoke? _switchSmoke;
    private UnsavedWorldChangePrompt? _worldChangePrompt;
    private LineEdit? _loadId;
    private WorldEditorCapture? _capture;
    private AuthorityCheckpointSmoke? _authority;
    private WorldEditorCrossProcessSmoke? _crossProcess;
    private BlankWorldSmoke? _blankWorld;
    private WorldEditorObservationView? _observation;
    private LineEdit _saveName = null!;
    private WorldEditorWorkspaceSmoke? _workspaceSmoke;
    private ConfirmationDialog _saveAsPrompt = null!;
    private LineEdit _saveAsName = null!;
    private FileDialog _exportFile = null!;
    private Control _worldInput = null!;
    private VBoxContainer _inspector = null!;
    private VBoxContainer _tools = null!;
    private WorldEditorVoxelToolView? _cellTool;

    internal VoxelDevelopmentSession? Session => _session;
    internal WorldEditorObservationView? Observation => _observation;
    internal Camera3D Camera => _camera;
    internal VoxelWorldCollisionPresenter Colliders => _colliders;
    internal VoxelWorldMeshPresenter Meshes => _meshes;
    internal CompositeVisualPresenter Composites => _composites;
    internal TrackRendererPresenter Tracks => _tracks;
    internal LineEdit SaveNameInput => _saveName;
    internal WorldEditorVoxelToolView? CellTool => _cellTool;

    public override void _Ready()
    {
        var main = GetNode<Main>("/root/Main");
        AddEnvironment();
        _camera = new Camera3D();
        AddChild(_camera);
        CreateHud();

        var status = EstablishOrResume(main, out var session);
        if (session is null)
        {
            _source.Text = $"世界编辑器无法建立：{main.WorldEditorConfigurationId} · {status}";
            GD.PrintErr($"WORLD_EDITOR_ESTABLISH_FAILED config={main.WorldEditorConfigurationId} status={status}");
            if (WorldEditorWorkspaceSmoke.Requested())
                Callable.From(() => WorldEditorWorkspaceSmoke.FromFailedEditor(this, status)).CallDeferred();
            if (IsSmoke() || IsAuthority() || IsCrossProcess() || BlankWorldSmoke.Requested())
                CallDeferred(nameof(QuitFailedEstablish));
            return;
        }

        _session = session;
        _session.Changed += MarkDirty;
        _session.WorldChangeChanged += PresentWorldChange;
        main.SetVoxelDevelopmentSession(_session);
        _saveName.PlaceholderText = _session.SaveId is null ? "新世界" : _session.DisplayName;
        _meshes.Rebuild(this, _session.Identity);
        _colliders.Rebuild(this, _session.Identity);
        _observation = new WorldEditorObservationView();
        AddChild(_observation);
        _observation.Initialize(_session, _camera, _colliders, () => main.ConsoleOpen, InitialPose(_session), _worldInput, _inspector);
        main.SetWorldEditorObserver(_observation.Observer);
        _cellTool = new WorldEditorVoxelToolView();
        AddChild(_cellTool);
        _cellTool.Initialize(_session, _tools, _inspector);
        _observation.CellPointer = _cellTool.HandlePointer;
        _observation.PointerInputInterrupted = _cellTool.InterruptPointer;
        _observation.DraftGuard = _cellTool.RequestChange;
        _worldInput.GuiInput += HandleHistoryInput;
        Refresh();
        ApplyMotionPresentation(0);

        if (WorldEditorWorkspaceSmoke.Requested())
            _workspaceSmoke = new WorldEditorWorkspaceSmoke(this);
        else if (BlankWorldSmoke.Requested())
            _blankWorld = new BlankWorldSmoke(this);
        else if (IsSmoke() && WorldEditorSwitchSmoke.Requested())
            _switchSmoke = new WorldEditorSwitchSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
        else if (IsSmoke() && WorldEditorObservationSmoke.Requested())
            _observationSmoke = new WorldEditorObservationSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
        else if (IsSmoke())
            _smoke = new WorldEditorSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
        else if (System.Environment.GetEnvironmentVariable(CaptureEnvironmentVariable) is { Length: > 0 } capturePath)
            _capture = new WorldEditorCapture(this, capturePath, System.Environment.GetEnvironmentVariable(CaptureStepsEnvironmentVariable));
        else if (IsCrossProcess())
            _crossProcess = new WorldEditorCrossProcessSmoke(this);
        else if (IsAuthority())
            _authority = new AuthorityCheckpointSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
    }

    private ObservationCameraPose InitialPose(VoxelDevelopmentSession session)
    {
        if (session.WorldSource?.Kind == WorldSource.FixedConfigurationKind)
            return OverviewPose;

        var bounds = session.Identity.Bounds;
        var center = new Vector3(bounds.SizeX, bounds.SizeY, bounds.SizeZ) * 0.5f;
        var viewport = GetViewport().GetVisibleRect().Size;
        var halfAngle = Math.Atan(Math.Tan(Mathf.DegToRad(_camera.Fov) * 0.5) * Math.Min(1, viewport.X / viewport.Y));
        // Fit the sphere enclosing the world, with margin, using the narrower field of view.
        var distance = (float)(center.Length() / Math.Sin(halfAngle) * 1.1);
        var position = center + new Vector3(0, 1, -1).Normalized() * distance;
        return new(WorldEditorObservationView.From(position), WorldEditorObservationView.From(center));
    }

    public override void _Process(double delta)
    {
        if (_session is null)
            return;
        if (_dirty)
            Refresh();
        var steps = _session.CollectHostSteps(delta);
        if (steps > 0)
            _session.AdvanceSimulation(steps);

        ApplyMotionPresentation(delta);
        _smoke?.Advance();
        _observationSmoke?.Advance();
        _switchSmoke?.Advance();
        _capture?.Advance();
        _authority?.Advance();
        _crossProcess?.Advance();
        _blankWorld?.Advance();
        _workspaceSmoke?.Advance();
    }

    public override void _ExitTree()
    {
        if (_session is not null)
        {
            _session.Changed -= MarkDirty;
            _session.WorldChangeChanged -= PresentWorldChange;
            _session.Detach();
        }

        if (GetNodeOrNull<Main>("/root/Main") is { } main)
        {
            main.SetVoxelDevelopmentSession(null);
            main.SetWorldEditorObserver(null);
        }
    }

    internal string ApplyOperation(WorldEditorOperation operation, bool confirmEndHistory = false)
    {
        if (_session is null)
            return "NoSession";
        var status = operation.Apply(_session, confirmEndHistory);
        if (status == nameof(WorldEditRefusal.HistoryConfirmationRequired))
        {
            _pendingHistoryOperation = operation;
            _historyPrompt.PopupCentered(new Vector2I(460, 160));
        }
        _result.Text = $"最近操作：{operation.Describe()} → {status}";
        Refresh();
        return status;
    }

    internal void Press(string button) => _buttons[button].EmitSignal(BaseButton.SignalName.Pressed);
    internal Button ActionButton(string action) => _buttons[action];
    internal ConfirmationDialog HistoryPrompt => _historyPrompt;

    internal string ApplyHistory(bool redo)
    {
        if (_session is null || _cellTool is null)
            return "NoSession";
        var status = _cellTool.State.HasDraft
            ? ((redo ? _cellTool.State.RedoDraft() : _cellTool.State.UndoDraft()) ? "DraftApplied" : "NoDraftHistory")
            : (redo ? _session.Redo() : _session.Undo()).Status;
        _result.Text = $"最近操作：{(redo ? "重做" : "撤销")} → {status}";
        Refresh();
        return status;
    }

    private void HandleHistoryInput(InputEvent input)
    {
        if (_observation?.WorldHasInput != true || input is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true } key)
            return;
        if (key.Keycode == Key.Z)
            ApplyHistory(key.ShiftPressed);
        else if (key.Keycode == Key.Y)
            ApplyHistory(redo: true);
        else
            return;
        GetViewport().SetInputAsHandled();
    }

    internal void SetSaveName(string name) => _saveName.Text = name;

    internal bool SaveAsPromptVisible => _saveAsPrompt.Visible;
    internal void SetSaveAsName(string name) => _saveAsName.Text = name;
    internal void PressSaveAsConfirm() => _saveAsPrompt.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
    internal void PressSaveAsCancel() => _saveAsPrompt.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);

    internal bool PromptVisible => _worldChangePrompt?.Visible == true;
    internal UnsavedWorldChangePrompt? WorldChangePrompt => _worldChangePrompt;
    internal Button DraftLeaveDiscardButton => _worldChangePrompt!.DraftDiscardButton;

    internal void PressPrompt(string name) => _worldChangePrompt!.Press(name);

    internal void SetLoadId(string id)
    {
        if (_loadId is not null)
            _loadId.Text = id;
    }

    internal void SetStepCount(ulong steps) => _stepCount.Value = steps;

    internal void Refresh()
    {
        if (_session is null)
            return;
        var catalog = GetNode<Main>("/root/Main").Reloader?.Catalog.Current;
        _meshes.Refresh(_session, catalog);
        _colliders.Refresh(_session, catalog);
        _composites.Refresh(this, _session, catalog);
        _tracks.Refresh(this, _session);
        _dirty = false;
        _source.Text = SourceText(_session.WorldSource);
    }

    private WorldEditorEstablishStatus EstablishOrResume(Main main, out VoxelDevelopmentSession? session)
    {
        session = main.TakeWorldEditorCandidate();
        if (session is not null)
            return WorldEditorEstablishStatus.Established;
        var reloader = main.Reloader ?? throw new InvalidOperationException("Content reloader is unavailable.");
        var store = new FileWorldSaveStore(main.IsolatedUserDataDirectory);
        var rawSaveId = System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable);
        if (!(IsSmoke() || IsAuthority() || IsCrossProcess()) || !Guid.TryParse(rawSaveId, out var saveId))
            return WorldEditorSession.TryEstablish(main.WorldEditorConfigurationId, reloader.Catalog, reloader.Composites, store, out session);

        var configuration = WorldEditorConfigurations.Find(main.WorldEditorConfigurationId);
        if (configuration is null)
            return WorldEditorEstablishStatus.UnknownConfiguration;
        var loaded = new VoxelDevelopmentSession(reloader.Catalog, configuration.BaseWorld, store, reloader.Composites);
        loaded.MarkWorldEditorHost();
        var result = loaded.Load(saveId);
        if (result.Succeeded && !loaded.SimulationPaused)
            loaded.PauseSimulation();
        if (!result.Succeeded)
        {
            loaded.Detach();
            GD.PrintErr($"WORLD_EDITOR_SMOKE_FAILED load status={result.Status} stage={result.Diagnostic?.Stage ?? "none"} " +
                        $"subject={result.Diagnostic?.Subject ?? "none"} relationship={result.Diagnostic?.Relationship ?? "none"}");
            return WorldEditorEstablishStatus.InitialStateRejected;
        }

        session = loaded;
        return WorldEditorEstablishStatus.Established;
    }

    private void MarkDirty() => _dirty = true;

    private void QuitFailedEstablish() => GetTree().Quit(1);

    private void ApplyMotionPresentation(double delta)
    {
        if (_session is null)
            return;
        var sample = _session.QueryDisplayPoses();
        _composites.ApplyDisplayPoses(sample);
        _observation?.Present(delta, sample);
        _cellTool?.Present();
        PresentHistory();
        _state.Text = StateText();
        _edits.Text = EditsText();
    }

    private void PresentHistory()
    {
        if (_session is null || _cellTool is null)
            return;
        var tool = _cellTool.State;
        _buttons["undo"].Disabled = tool.HasDraft ? !tool.CanUndoDraft : !_session.CanUndo;
        _buttons["redo"].Disabled = tool.HasDraft ? !tool.CanRedoDraft : !_session.CanRedo;
        _historyState.Text = tool.HasDraft ? "草稿撤销 / 重做 · 提交或放弃后可操作世界历史"
            : $"世界历史：可撤销 {_session.UndoCount} · 可重做 {_session.RedoCount} · 上限 {VoxelDevelopmentSession.VoxelHistoryCapacity}";
        if (_session.HistoryEvictionCount > 0)
            _historyState.Text += $"\n世界撤销历史已淘汰 {_session.HistoryEvictionCount} 条最旧记录。";
        if (tool.HistoryEvictionCount > 0)
            _historyState.Text += $"\n草稿撤销历史已淘汰 {tool.HistoryEvictionCount} 条最旧记录。";
    }

    private string StateText()
    {
        var session = _session!;
        var assemblies = session.QueryTrainAssemblies();
        var motion = assemblies.Count == 0
            ? "无编组"
            : string.Join(" · ", assemblies.Select(item =>
                $"编组{item.Id.Value} 前 {item.Vehicles[0].FrontContact.X.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"速 {item.Speed.ToString("0.###", CultureInfo.InvariantCulture)} {AlongRailDisplay.MotionStatusText(item, session.SimulationPaused)}"));
        var rails = session.QueryRails();
        var collision = session.QueryCollision(StraightRailFixture.LeftRailSample).Status;
        var boundary = session.HasUnsavedChanges ? "未保存" : "已保存";
        var title = session.SaveId is null ? "未保存的新世界" : session.DisplayName;
        _buttons["reload_saved"].Disabled = !session.CanReloadSaved || session.IsChangingWorld;
        return $"{title} · 步 {session.SimulationStep} · {(session.IsChangingWorld ? "正在切换（模拟与修改暂挂）" : session.SimulationPaused ? "已暂停" : "运行中")} · {boundary} · 车辆 {session.QueryComposites().Count} · {motion}\n" +
               $"线路段 {rails.Segments.Count} 枕木 {rails.SleeperCount} · 世界体素 {session.World.VoxelCount} · 轨道采样碰撞 {collision}";
    }

    private static string SourceText(WorldSource? source) =>
        source is null
            ? "世界编辑器 · 当前世界没有固定配置来源"
            : source.Kind == WorldSource.BlankKind
            ? $"空白世界 v{source.ConfigurationVersion} · 范围 {source.Parameter("base.bounds")} · 种子 {source.Parameter("base.seed")} · 基底 {source.BaseFingerprint[..8]}"
            : $"世界编辑器 · {source.ConfigurationId} v{source.ConfigurationVersion} · {source.Kind} · 参数 {source.EffectiveParameters.Count} · 内容依赖 {source.ContentDependencies.Count}" +
              (source.BaseFingerprint.Length == 0 ? "" : $" · 基底 {source.BaseFingerprint[..8]}");

    private void AddEnvironment()
    {
        AddChild(new DirectionalLight3D
        {
            Position = new Vector3(8, 20, 8),
            RotationDegrees = new Vector3(-50, -25, 0),
            LightEnergy = 1.4f,
            ShadowEnabled = true
        });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.43f, 0.45f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = 0.7f
            }
        });
    }

    private void CreateHud()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var workspace = new VBoxContainer { OffsetLeft = 12, OffsetTop = 12, OffsetRight = -12, OffsetBottom = -12 };
        workspace.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(workspace);
        var panel = new PanelContainer();
        StylePanel(panel);
        workspace.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        _source = new Label();
        _state = new Label();
        _result = new Label { Text = "最近操作：无（建立后默认暂停）" };
        box.AddChild(_source);
        box.AddChild(_state);
        _source.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _state.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 6);
        box.AddChild(controls);
        AddButton(controls, "run", "运行", () => ApplyOperation(WorldEditorOperation.Resume()));
        AddButton(controls, "pause", "暂停", () => ApplyOperation(WorldEditorOperation.Pause()));
        AddButton(controls, "step", "单步", () => ApplyOperation(WorldEditorOperation.Advance(1)));
        _stepCount = new SpinBox { MinValue = 1, MaxValue = 100000, Value = 60, Rounded = true };
        controls.AddChild(_stepCount);
        AddButton(controls, "step_n", "推进 N 步", () => ApplyOperation(WorldEditorOperation.Advance((ulong)_stepCount.Value)));
        var history = new HBoxContainer();
        box.AddChild(history);
        AddButton(history, "undo", "撤销 Ctrl+Z", () => ApplyHistory(redo: false));
        AddButton(history, "redo", "重做 Ctrl+Shift+Z", () => ApplyHistory(redo: true));
        _historyState = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_historyState);
        _historyPrompt = new ConfirmationDialog
        {
            Title = "此操作会结束撤销历史",
            DialogText = "这项修改不能撤销。成功后将结束撤销历史，是否继续？",
            OkButtonText = "继续修改", CancelButtonText = "取消"
        };
        AddChild(_historyPrompt);
        _historyPrompt.Confirmed += () =>
        {
            var operation = _pendingHistoryOperation;
            _pendingHistoryOperation = null;
            if (operation is not null)
                ApplyOperation(operation, confirmEndHistory: true);
        };
        _historyPrompt.Canceled += () => _pendingHistoryOperation = null;
        var persistence = new HFlowContainer();
        persistence.AddThemeConstantOverride("separation", 6);
        box.AddChild(persistence);
        _saveName = new LineEdit { PlaceholderText = "存档名称", MaxLength = WorldSaveDocument.DisplayNameMaxLength, CustomMinimumSize = new Vector2(150, 0) };
        persistence.AddChild(_saveName);
        AddButton(persistence, "save", "保存", () => SaveWorld());
        AddButton(persistence, "save_as", "另存为", BeginSaveAs);
        AddButton(persistence, "export", "导出 JSON", BeginExport);
        AddButton(persistence, "reload_saved", "重新加载已保存版本", () => RequestWorldChange(new WorldChangeRequest(WorldChangeKind.ReloadSaved, null)));
        AddButton(persistence, "workspace", "返回存档工作区", () => GetNode<Main>("/root/Main").EnterWorldEditorWorkspace());
        AddButton(persistence, "menu", "返回主菜单", () => GetNode<Main>("/root/Main").ReturnToMainMenu());
        if (Main.IsFixedWorldEditorAutomationEnabled())
        {
            AddButton(persistence, "reset_world", "重新建立当前配置", () => RequestWorldChange(new WorldChangeRequest(WorldChangeKind.Reset, null)));
            AddButton(persistence, "switch_world", "切换到另一配置", () => RequestWorldChange(new WorldChangeRequest(
                WorldChangeKind.SwitchConfiguration,
                null,
                OtherConfigurationId())));
            _loadId = new LineEdit { PlaceholderText = "存档 UUID", CustomMinimumSize = new Vector2(280, 0) };
            persistence.AddChild(_loadId);
            AddButton(persistence, "load_world", "读取存档", LoadFromField);
        }
        _worldChangePrompt = new UnsavedWorldChangePrompt();
        _worldChangePrompt.Chosen += ConfirmWorldChange;
        _worldChangePrompt.DraftDiscard += () => _cellTool?.State.Discard();
        AddChild(_worldChangePrompt);
        _saveAsPrompt = new ConfirmationDialog { Title = "另存为新存档", OkButtonText = "另存为", CancelButtonText = "取消" };
        _saveAsName = new LineEdit { MaxLength = WorldSaveDocument.DisplayNameMaxLength, PlaceholderText = "新存档名称（留空沿用当前名称）" };
        _saveAsPrompt.AddChild(_saveAsName);
        _saveAsPrompt.Confirmed += CompleteSaveAs;
        _saveAsPrompt.Canceled += _saveAsPrompt.Hide;
        AddChild(_saveAsPrompt);
        _exportFile = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.json ; JSON 存档" }, ModeOverridesTitle = false,
            Title = "导出已提交世界（不包含临时草稿）"
        };
        // An existing selected file has passed FileDialog's overwrite warning. Later arrivals must not be replaced.
        _exportFile.FileSelected += path => ExportWorld(path, overwrite: File.Exists(path));
        AddChild(_exportFile);

        var areas = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        workspace.AddChild(areas);
        var tools = AddSidebar(areas, 310);
        _tools = tools;
        tools.AddChild(new Label { Text = "工具与内容库" });
        var help = new Label { Text = "以下开发操作使用精确参数；不随选择改变目标。", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        tools.AddChild(help);
        _worldInput = new Control { FocusMode = Control.FocusModeEnum.All, MouseFilter = Control.MouseFilterEnum.Stop,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        areas.AddChild(_worldInput);
        _inspector = AddSidebar(areas, 340);

        tools.AddChild(new Label { Text = "世界坐标 X / Y / Z" });
        var world = new HBoxContainer();
        tools.AddChild(world);
        var probe = WorldEditorSequences.EditProbe;
        _worldCell = new[] { probe.X, probe.Y, probe.Z }.Select(value => AddSpin(world, -100000, 100000, value)).ToArray();
        var parameters = new HBoxContainer();
        tools.AddChild(parameters);
        parameters.AddChild(new Label { Text = "伤害量" });
        _editAmount = AddSpin(parameters, 1, 100000, 25);
        parameters.AddChild(new Label { Text = "延迟步" });
        _editDelay = AddSpin(parameters, 0, 100000, 0);
        var voxels = AddToolGroup(tools, "世界体素");
        AddButton(voxels, "edit_add", "放置矿石", () => ApplyEdit("edit_add"));
        AddButton(voxels, "edit_remove", "移除体素", () => ApplyEdit("edit_remove"));
        AddButton(voxels, "edit_damage", "损伤体素", () => ApplyEdit("edit_damage"));
        var rails = AddToolGroup(tools, "铁路");
        AddButton(rails, "edit_repair", "修复轨道", () => ApplyEdit("edit_repair"));
        AddButton(rails, "edit_construct", "施工直轨", () => ApplyEdit("edit_construct"));
        var vehicles = AddToolGroup(tools, "车辆与编组");
        AddButton(vehicles, "edit_manufacture", "在此制造空壳", () => ApplyEdit("edit_manufacture"));
        AddButton(vehicles, "edit_assemble", "组装列车", () => ApplyEdit("edit_assemble"));
        AddButton(vehicles, "start", "启动列车", () => ApplyEdit("start"));
        AddButton(vehicles, "stop", "停车", () => ApplyEdit("stop"));

        var vehicle = new HBoxContainer();
        vehicle.AddThemeConstantOverride("separation", 6);
        tools.AddChild(new Label { Text = "最新车辆局部坐标 X / Y / Z" });
        tools.AddChild(vehicle);
        var local = HollowShellRefit.FirstAdd;
        _localCell = new[] { local.X, local.Y, local.Z }.Select(value => AddSpin(vehicle, 0, 255, value)).ToArray();
        var refit = AddToolGroup(tools, "车辆零件开发操作（最新车辆）");
        AddButton(refit, "vehicle_damage", "损伤零件", () => ApplyEdit("vehicle_damage"));
        AddButton(refit, "refit_add", "加装", () => ApplyEdit("refit_add"));
        AddButton(refit, "refit_remove", "拆除", () => ApplyEdit("refit_remove"));
        AddButton(refit, "refit_move", "移到 +X", () => ApplyEdit("refit_move"));
        AddButton(refit, "refit_rotate", "旋转", () => ApplyEdit("refit_rotate"));
        AddButton(refit, "refit_replace", "替换", () => ApplyEdit("refit_replace"));
        AddButton(refit, "refit_batch", "批量加装两格", () => ApplyEdit("refit_batch"));
        var results = new VBoxContainer { Visible = false };
        AddButton(box, "results", "展开 / 收起操作结果", () => results.Visible = !results.Visible);
        _result.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _edits = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        results.AddChild(_result);
        results.AddChild(_edits);
        box.AddChild(results);
    }

    private static VBoxContainer AddSidebar(Container parent, float width)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 0) };
        StylePanel(panel);
        parent.AddChild(panel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(box);
        return box;
    }

    private static void StylePanel(PanelContainer panel) => panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
    {
        BgColor = new Color(0.14f, 0.15f, 0.17f),
        ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8
    });

    private static HFlowContainer AddToolGroup(Container parent, string title)
    {
        parent.AddChild(new Label { Text = title });
        var group = new HFlowContainer();
        parent.AddChild(group);
        return group;
    }

    private static SpinBox AddSpin(Container parent, double min, double max, double value)
    {
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value, Rounded = true, CustomMinimumSize = new Vector2(72, 0) };
        parent.AddChild(spin);
        return spin;
    }

    private void ApplyEdit(string button) => ApplyOperation(EditOperation(button));

    internal void SetEditInputs(WorldVoxelCoord cell, LocalVoxelCoord local, int amount, ulong delay)
    {
        _worldCell[0].Value = cell.X;
        _worldCell[1].Value = cell.Y;
        _worldCell[2].Value = cell.Z;
        _localCell[0].Value = local.X;
        _localCell[1].Value = local.Y;
        _localCell[2].Value = local.Z;
        _editAmount.Value = amount;
        _editDelay.Value = delay;
    }

    /// <summary>The formal operation a panel button submits for the current inputs; a non-zero delay queues it.</summary>
    internal WorldEditorOperation EditOperation(string button)
    {
        var cell = new WorldVoxelCoord((int)_worldCell[0].Value, (int)_worldCell[1].Value, (int)_worldCell[2].Value);
        var local = new LocalVoxelCoord((int)_localCell[0].Value, (int)_localCell[1].Value, (int)_localCell[2].Value);
        var amount = (int)_editAmount.Value;
        ulong? at = _editDelay.Value > 0 && _session is not null ? _session.SimulationStep + (ulong)_editDelay.Value : null;
        var marker = HollowShellRefit.MarkerId;
        return button switch
        {
            "start" => WorldEditorOperation.Start(atStep: at),
            "stop" => WorldEditorOperation.Stop(at),
            "edit_repair" => WorldEditorOperation.Submit(new RailRepairEdit(new[] { cell }), at),
            "edit_construct" => WorldEditorOperation.Submit(new RailConstructEdit(StraightRailFixture.CreateFixturePlan()), at),
            "edit_assemble" => WorldEditorOperation.Submit(TrainAssembleEdit.StaticFixture, at),
            "edit_add" => WorldEditorOperation.Submit(new VoxelAddEdit(cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity), at),
            "edit_remove" => WorldEditorOperation.Submit(new VoxelRemoveEdit(cell), at),
            "edit_damage" => WorldEditorOperation.Submit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(cell, amount) }), at),
            "edit_manufacture" => WorldEditorOperation.Submit(
                new VehicleManufactureEdit(HollowShellFixture.DefinitionId, CompositeTransform.Translation(cell.X, cell.Y, cell.Z)), at),
            "vehicle_damage" => WorldEditorOperation.DamageLatest(local, amount, at),
            "refit_add" => WorldEditorOperation.RefitLatest(WorldEditorRefit.Add(local, marker), at),
            "refit_remove" => WorldEditorOperation.RefitLatest(WorldEditorRefit.Remove(local), at),
            "refit_move" => WorldEditorOperation.RefitLatest(WorldEditorRefit.Move(local, new LocalVoxelCoord(local.X + 1, local.Y, local.Z)), at),
            "refit_rotate" => WorldEditorOperation.RefitLatest(WorldEditorRefit.Rotate(local, HollowShellRefit.MarkerOrientation), at),
            "refit_replace" => WorldEditorOperation.RefitLatest(WorldEditorRefit.Replace(local, HollowShellRefit.ReplacementId), at),
            "refit_batch" => WorldEditorOperation.RefitLatest(new[]
            {
                WorldEditorRefit.Add(local, marker),
                WorldEditorRefit.Add(new LocalVoxelCoord(local.X + 2, local.Y, local.Z), marker)
            }, at),
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, null)
        };
    }

    private string EditsText()
    {
        var session = _session!;
        var log = session.QueryEditLog();
        var last = log.Count == 0 ? "无" : WorldEditorCheck.DescribeRecord(log[^1]);
        var fault = session.Fault is { } value
            ? $" · 故障 {(value.EditId is { } id ? $"修改{id}" : "运动")}@{value.Position} {value.ExceptionType}"
            : "";
        return $"修改 边界 {session.EditBoundary} · 下一个 {session.NextEditId} · 待执行 {session.QueryPendingEdits().Count} · 最近 {last}{fault}";
    }

    private void AddButton(Container parent, string id, string text, Action action)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += action;
        parent.AddChild(button);
        _buttons[id] = button;
    }

    internal WorldSaveResult SaveWorld()
    {
        var result = _session?.Save(RequestedSaveName())
                     ?? new WorldSaveResult(WorldSaveStatus.Unavailable);
        _result.Text = $"最近操作：保存 → {result.Status} {result.SaveId}" + DraftSaveNotice();
        UpdateSavedName(result);
        return result;
    }

    private void BeginSaveAs()
    {
        if (_session is null || _session.IsChangingWorld)
            return;
        _saveAsName.Text = RequestedSaveName() ?? _session!.DisplayName;
        _saveAsPrompt.PopupCentered(new Vector2I(430, 130));
        _saveAsName.GrabFocus();
        _saveAsName.SelectAll();
    }

    private void BeginExport()
    {
        if (_session is null || _session.IsChangingWorld)
            return;
        _exportFile.CurrentFile = _session.DisplayName + ".json";
        _exportFile.PopupCentered(new Vector2I(820, 560));
    }

    internal WorldSaveResult ExportWorld(string path, bool overwrite)
    {
        var main = GetNode<Main>("/root/Main");
        var reloader = main.Reloader!;
        var workspace = new WorldEditorWorkspace(reloader.Catalog, reloader.Composites,
            new FileWorldSaveStore(main.IsolatedUserDataDirectory));
        var result = _session is null ? new WorldSaveResult(WorldSaveStatus.Unavailable) : workspace.Export(_session, path, overwrite);
        _result.Text = result.Succeeded ? $"已导出已提交世界：{path}（不包含临时草稿）。"
            : $"无法导出：{WorldEditorWorkspaceScene.Describe(result)}";
        return result;
    }

    private void CompleteSaveAs()
    {
        _saveAsPrompt.Hide();
        var name = _saveAsName.Text.Length > 0 ? _saveAsName.Text : null;
        var result = _session?.SaveAs(name) ?? new WorldSaveResult(WorldSaveStatus.Unavailable);
        _result.Text = $"最近操作：另存为 → {result.Status} {result.SaveId}" + DraftSaveNotice();
        UpdateSavedName(result);
    }

    private void UpdateSavedName(WorldSaveResult result)
    {
        if (!result.Succeeded)
            return;
        _saveName.Text = "";
        _saveName.PlaceholderText = _session!.DisplayName;
        _state.Text = StateText();
    }

    private string DraftSaveNotice() => _session?.HasEditorDraft == true ? " · 体素草稿未保存，仍保留。" : "";

    private string? RequestedSaveName() =>
        _saveName.Text.Length > 0 ? _saveName.Text : _session?.SaveId is null ? "新世界" : null;

    private string OtherConfigurationId() =>
        _session?.WorldSource?.ConfigurationId == WorldEditorConfigurations.StraightRailEmptyId
            ? WorldEditorConfigurations.StraightRailTrainId
            : WorldEditorConfigurations.StraightRailEmptyId;

    private void LoadFromField()
    {
        if (_loadId is null || !Guid.TryParse(_loadId.Text.Trim(), out var saveId))
        {
            _result.Text = "最近操作：读取需要有效的存档 UUID。";
            return;
        }

        RequestWorldChange(new WorldChangeRequest(WorldChangeKind.Load, null, SaveId: saveId));
    }

    internal WorldChangeResult RequestWorldChange(WorldChangeRequest request)
    {
        if (_session is null)
        {
            if (!request.LeavesScene)
                return new WorldChangeResult(WorldChangeStatus.CandidateFailed);
            var error = GetNode<Main>("/root/Main").CompleteWorldDeparture(request.Kind);
            return new WorldChangeResult(error == Error.Ok ? WorldChangeStatus.Completed : WorldChangeStatus.CandidateFailed);
        }
        request = request with { SaveDisplayName = request.SaveDisplayName ?? RequestedSaveName() };
        _saveAsPrompt.Hide();
        var result = _session.BeginWorldChange(request);
        if (result.Status is not (WorldChangeStatus.ChoiceRequired or WorldChangeStatus.Switching or WorldChangeStatus.Busy))
            PresentWorldChange(request, result);
        return result;
    }

    private void ConfirmWorldChange(WorldChangeChoice choice)
    {
        _session?.ChooseWorldChange(choice);
    }

    private void PresentWorldChange(WorldChangeRequest request, WorldChangeResult result)
    {
        if (result.Status == WorldChangeStatus.ChoiceRequired)
        {
            var canSave = _session?.Fault is null;
            _worldChangePrompt?.Open(request.Kind, canSave, _session?.HasEditorDraft == true, _session?.HasUnsavedChanges == true);
            _result.Text = canSave ? "最近操作：有未保存变化，请选择保存后继续、放弃或取消。"
                : "最近操作：模拟发生故障，无法保存当前世界。可以放弃更改或取消。";
            _state.Text = StateText();
            return;
        }
        if (result.Status == WorldChangeStatus.Switching)
        {
            _worldChangePrompt?.ShowProgress(result.SaveStatus is null ? "正在切换，模拟与修改已暂挂。" : "保存完成，正在准备目标世界。");
            _state.Text = StateText();
            return;
        }
        _worldChangePrompt?.Close();
        if (result.Status == WorldChangeStatus.Completed && request.LeavesScene)
        {
            var error = GetNode<Main>("/root/Main").CompleteWorldDeparture(request.Kind);
            if (error != Error.Ok)
                _result.Text = $"最近操作：离开失败 {error}，当前世界保留。";
            return;
        }
        if (request.Kind == WorldChangeKind.LeaveForScene)
            GetNode<Main>("/root/Main").CancelSceneDeparture();
        _result.Text = result.Status switch
        {
            WorldChangeStatus.Completed => CompletedText(result),
            WorldChangeStatus.Cancelled => "最近操作：已取消，当前世界未改变。",
            WorldChangeStatus.SaveFailed => $"最近操作：保存失败 {result.SaveStatus}，切换已取消。{DiagnosticText(result.Diagnostic)}",
            WorldChangeStatus.CandidateFailed => $"最近操作：未能替换世界 {result.CandidateStatus}。{DiagnosticText(result.Diagnostic)}",
            _ => $"最近操作：{result.Status}。"
        };
        _dirty = true;
    }

    private string CompletedText(WorldChangeResult result)
    {
        var source = _session?.WorldSource?.ConfigurationId ?? "无来源";
        return $"最近操作：世界已替换为 {source}，暂停。{AdaptationText(result.Adaptations)}";
    }

    private static string DiagnosticText(WorldSaveDiagnostic? diagnostic) =>
        diagnostic is null
            ? ""
            : $"阶段 {diagnostic.Stage}，对象 {diagnostic.Subject}，关系 {diagnostic.Relationship}。";

    private static string AdaptationText(IReadOnlyList<WorldSaveAdaptation>? adaptations) =>
        adaptations is not { Count: > 0 }
            ? "无适配。"
            : "适配 " + string.Join("；", adaptations.Select(item => $"{item.Subject}/{item.Relationship}")) + "。";

    private static bool IsSmoke() =>
        string.Equals(System.Environment.GetEnvironmentVariable(SmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private static bool IsAuthority() =>
        string.Equals(System.Environment.GetEnvironmentVariable(AuthorityEnvironmentVariable), "1", StringComparison.Ordinal);

    private static bool IsCrossProcess() =>
        string.Equals(System.Environment.GetEnvironmentVariable(CrossProcessEnvironmentVariable), "1", StringComparison.Ordinal);
}

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
    private bool _dirty = true;
    private WorldEditorSmoke? _smoke;
    private WorldEditorObservationSmoke? _observationSmoke;
    private WorldEditorSwitchSmoke? _switchSmoke;
    private UnsavedWorldChangePrompt? _worldChangePrompt;
    private WorldChangeRequest? _pendingWorldChange;
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

    internal VoxelDevelopmentSession? Session => _session;
    internal WorldEditorObservationView? Observation => _observation;
    internal Camera3D Camera => _camera;
    internal VoxelWorldCollisionPresenter Colliders => _colliders;
    internal VoxelWorldMeshPresenter Meshes => _meshes;
    internal CompositeVisualPresenter Composites => _composites;
    internal TrackRendererPresenter Tracks => _tracks;

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
            if (IsSmoke() || IsAuthority() || IsCrossProcess() || BlankWorldSmoke.Requested())
                CallDeferred(nameof(QuitFailedEstablish));
            return;
        }

        _session = session;
        _session.Changed += MarkDirty;
        _session.WorldReplaced += DismissStaleWorldChange;
        main.SetVoxelDevelopmentSession(_session);
        _saveName.PlaceholderText = _session.SaveId is null ? "新世界" : _session.DisplayName;
        _meshes.Rebuild(this, _session.Identity);
        _colliders.Rebuild(this, _session.Identity);
        _observation = new WorldEditorObservationView();
        AddChild(_observation);
        _observation.Initialize(_session, _camera, _colliders, () => main.ConsoleOpen, OverviewPose);
        main.SetWorldEditorObserver(_observation.Observer);
        Refresh();

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
            _session.WorldReplaced -= DismissStaleWorldChange;
            _session.Detach();
        }

        if (GetNodeOrNull<Main>("/root/Main") is { } main)
        {
            main.SetVoxelDevelopmentSession(null);
            main.SetWorldEditorObserver(null);
        }
    }

    internal string ApplyOperation(WorldEditorOperation operation)
    {
        if (_session is null)
            return "NoSession";
        var status = operation.Apply(_session);
        _result.Text = $"最近操作：{operation.Describe()} → {status}";
        Refresh();
        return status;
    }

    internal void Press(string button) => _buttons[button].EmitSignal(BaseButton.SignalName.Pressed);

    internal void SetSaveName(string name) => _saveName.Text = name;

    internal bool SaveAsPromptVisible => _saveAsPrompt.Visible;
    internal void SetSaveAsName(string name) => _saveAsName.Text = name;
    internal void PressSaveAsConfirm() => _saveAsPrompt.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
    internal void PressSaveAsCancel() => _saveAsPrompt.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);

    internal bool PromptVisible => _worldChangePrompt?.Visible == true;

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
        ApplyMotionPresentation();
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

    private void DismissStaleWorldChange(WorldReplacement _)
    {
        _pendingWorldChange = null;
        _worldChangePrompt?.Close();
    }

    private void ApplyMotionPresentation(double delta = 0)
    {
        if (_session is null)
            return;
        var sample = _session.QueryDisplayPoses();
        _composites.ApplyDisplayPoses(sample);
        _observation?.Present(delta, sample);
        _state.Text = StateText();
        _edits.Text = EditsText();
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
        var boundary = session.HasUnsavedChanges ? "未保存" : "已对齐保存点";
        var title = session.SaveId is null ? "未保存的新世界" : session.DisplayName;
        return $"{title} · 步 {session.SimulationStep} · {(session.SimulationPaused ? "已暂停" : "运行中")} · {boundary} · 车辆 {session.QueryComposites().Count} · {motion}\n" +
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
        var panel = new PanelContainer { Position = new Vector2(12, 12) };
        layer.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        _source = new Label();
        _state = new Label();
        _result = new Label { Text = "最近操作：无（建立后默认暂停）" };
        box.AddChild(_source);
        box.AddChild(_state);
        box.AddChild(_result);

        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 6);
        box.AddChild(controls);
        AddButton(controls, "run", "运行", () => ApplyOperation(WorldEditorOperation.Resume()));
        AddButton(controls, "pause", "暂停", () => ApplyOperation(WorldEditorOperation.Pause()));
        AddButton(controls, "step", "单步", () => ApplyOperation(WorldEditorOperation.Advance(1)));
        _stepCount = new SpinBox { MinValue = 1, MaxValue = 100000, Value = 60, Rounded = true };
        controls.AddChild(_stepCount);
        AddButton(controls, "step_n", "推进 N 步", () => ApplyOperation(WorldEditorOperation.Advance((ulong)_stepCount.Value)));
        AddButton(controls, "start", "启动列车", () => ApplyEdit("start"));
        AddButton(controls, "stop", "停车", () => ApplyEdit("stop"));
        var persistence = new HBoxContainer();
        persistence.AddThemeConstantOverride("separation", 6);
        box.AddChild(persistence);
        _saveName = new LineEdit { PlaceholderText = "存档名称", MaxLength = WorldSaveDocument.DisplayNameMaxLength, CustomMinimumSize = new Vector2(150, 0) };
        persistence.AddChild(_saveName);
        AddButton(persistence, "save", "保存", () => SaveWorld());
        AddButton(persistence, "save_as", "另存为", BeginSaveAs);
        AddButton(persistence, "workspace", "返回存档工作区", () => BeginWorldChange(new WorldChangeRequest(WorldChangeKind.ReturnToWorkspace, null)));
        if (Main.IsFixedWorldEditorAutomationEnabled())
        {
            AddButton(persistence, "reset_world", "重置", () => BeginWorldChange(new WorldChangeRequest(WorldChangeKind.Reset, null)));
            AddButton(persistence, "switch_world", "切换到另一配置", () => BeginWorldChange(new WorldChangeRequest(
                WorldChangeKind.SwitchConfiguration,
                null,
                OtherConfigurationId())));
            _loadId = new LineEdit { PlaceholderText = "存档 UUID", CustomMinimumSize = new Vector2(280, 0) };
            persistence.AddChild(_loadId);
            AddButton(persistence, "load_world", "读取存档", LoadFromField);
        }
        _worldChangePrompt = new UnsavedWorldChangePrompt();
        _worldChangePrompt.Chosen += ConfirmWorldChange;
        AddChild(_worldChangePrompt);
        _saveAsPrompt = new ConfirmationDialog { Title = "另存为新存档", OkButtonText = "另存为", CancelButtonText = "取消" };
        _saveAsName = new LineEdit { MaxLength = WorldSaveDocument.DisplayNameMaxLength, PlaceholderText = "新存档名称（留空沿用当前名称）" };
        _saveAsPrompt.AddChild(_saveAsName);
        _saveAsPrompt.Confirmed += () => CompleteSaveAs(WorldChangeChoice.SaveThenSwitch);
        _saveAsPrompt.Canceled += () => CompleteSaveAs(WorldChangeChoice.Cancel);
        AddChild(_saveAsPrompt);

        var world = new HBoxContainer();
        world.AddThemeConstantOverride("separation", 6);
        box.AddChild(world);
        world.AddChild(new Label { Text = "世界格" });
        var probe = WorldEditorSequences.EditProbe;
        _worldCell = new[] { probe.X, probe.Y, probe.Z }.Select(value => AddSpin(world, -100000, 100000, value)).ToArray();
        world.AddChild(new Label { Text = "量" });
        _editAmount = AddSpin(world, 1, 100000, 25);
        world.AddChild(new Label { Text = "延迟步" });
        _editDelay = AddSpin(world, 0, 100000, 0);
        AddButton(world, "edit_add", "放置矿石", () => ApplyEdit("edit_add"));
        AddButton(world, "edit_remove", "移除体素", () => ApplyEdit("edit_remove"));
        AddButton(world, "edit_damage", "损伤体素", () => ApplyEdit("edit_damage"));
        AddButton(world, "edit_manufacture", "在此制造空壳", () => ApplyEdit("edit_manufacture"));
        AddButton(world, "edit_repair", "修复轨道", () => ApplyEdit("edit_repair"));
        AddButton(world, "edit_construct", "施工直轨", () => ApplyEdit("edit_construct"));
        AddButton(world, "edit_assemble", "组装列车", () => ApplyEdit("edit_assemble"));

        var vehicle = new HBoxContainer();
        vehicle.AddThemeConstantOverride("separation", 6);
        box.AddChild(vehicle);
        vehicle.AddChild(new Label { Text = "最新车辆局部格" });
        var local = HollowShellRefit.FirstAdd;
        _localCell = new[] { local.X, local.Y, local.Z }.Select(value => AddSpin(vehicle, 0, 255, value)).ToArray();
        AddButton(vehicle, "vehicle_damage", "损伤零件", () => ApplyEdit("vehicle_damage"));
        AddButton(vehicle, "refit_add", "加装", () => ApplyEdit("refit_add"));
        AddButton(vehicle, "refit_remove", "拆除", () => ApplyEdit("refit_remove"));
        AddButton(vehicle, "refit_move", "移到 +X", () => ApplyEdit("refit_move"));
        AddButton(vehicle, "refit_rotate", "旋转", () => ApplyEdit("refit_rotate"));
        AddButton(vehicle, "refit_replace", "替换", () => ApplyEdit("refit_replace"));
        AddButton(vehicle, "refit_batch", "批量加装两格", () => ApplyEdit("refit_batch"));
        _edits = new Label();
        box.AddChild(_edits);
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
        _result.Text = $"最近操作：保存 → {result.Status} {result.SaveId}";
        Refresh();
        return result;
    }

    private void BeginSaveAs()
    {
        _saveAsName.Text = RequestedSaveName() ?? _session?.DisplayName ?? "新世界";
        _saveAsPrompt.PopupCentered(new Vector2I(430, 130));
        _saveAsName.GrabFocus();
        _saveAsName.SelectAll();
    }

    private void CompleteSaveAs(WorldChangeChoice choice)
    {
        _saveAsPrompt.Hide();
        var name = _saveAsName.Text.Length > 0 ? _saveAsName.Text : null;
        var result = _session?.TryChangeWorld(new(WorldChangeKind.SaveAs, choice, SaveDisplayName: name));
        _result.Text = choice == WorldChangeChoice.Cancel
            ? "最近操作：另存为已取消"
            : $"最近操作：另存为 → {result?.SaveStatus ?? WorldSaveStatus.Unavailable} {_session?.SaveId}";
        if (result?.Status == WorldChangeStatus.Completed)
        {
            _saveName.Text = "";
            _saveName.PlaceholderText = _session!.DisplayName;
        }
        Refresh();
    }

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

        BeginWorldChange(new WorldChangeRequest(WorldChangeKind.Load, null, SaveId: saveId));
    }

    private void BeginWorldChange(WorldChangeRequest request)
    {
        if (_session is null)
            return;
        if (request.Kind == WorldChangeKind.ReturnToWorkspace)
            request = request with { SaveDisplayName = RequestedSaveName() };
        PresentWorldChange(_session.TryChangeWorld(request), request);
    }

    private void ConfirmWorldChange(WorldChangeChoice choice)
    {
        if (_session is null || _pendingWorldChange is not { } pending)
            return;
        _worldChangePrompt?.Close();
        _pendingWorldChange = null;
        PresentWorldChange(_session.TryChangeWorld(pending with { Choice = choice }), pending);
    }

    private void PresentWorldChange(WorldChangeResult result, WorldChangeRequest? request)
    {
        if (result.Status == WorldChangeStatus.ChoiceRequired && request is not null)
        {
            _pendingWorldChange = request;
            _worldChangePrompt?.Open(request.Kind switch
            {
                WorldChangeKind.SwitchConfiguration => "可以保存后切换、放弃当前更改，或取消。",
                WorldChangeKind.Load => "可以保存后读取、放弃当前更改，或取消。",
                WorldChangeKind.ReturnToWorkspace => "可以保存后返回存档工作区、放弃当前更改，或取消。",
                _ => "可以保存后重置、放弃当前更改，或取消。"
            });
            _result.Text = "最近操作：有未保存变化，请选择保存后继续、放弃或取消。";
            return;
        }

        _worldChangePrompt?.Close();
        _pendingWorldChange = null;
        if (result.Status == WorldChangeStatus.Completed && request?.Kind == WorldChangeKind.ReturnToWorkspace)
        {
            GetNode<Main>("/root/Main").EnterWorldEditorWorkspace();
            return;
        }
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

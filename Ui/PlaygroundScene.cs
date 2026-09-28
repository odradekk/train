using System.Globalization;
using Godot;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

public partial class PlaygroundScene : Node3D
{
    public const string SmokeEnvironmentVariable = "TRAIN_PLAYGROUND_SMOKE";
    public const string SaveIdEnvironmentVariable = "TRAIN_PLAYGROUND_SAVE_ID";
    public const string CaptureEnvironmentVariable = "TRAIN_PLAYGROUND_CAPTURE";
    public const string CaptureStepsEnvironmentVariable = "TRAIN_PLAYGROUND_CAPTURE_STEPS";
    public const string AuthorityEnvironmentVariable = "TRAIN_AUTHORITY_CHECKPOINT";

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
    private PlaygroundSmoke? _smoke;
    private PlaygroundObservationSmoke? _observationSmoke;
    private PlaygroundCapture? _capture;
    private AuthorityCheckpointSmoke? _authority;
    private PlaygroundObservationView? _observation;

    internal VoxelDevelopmentSession? Session => _session;
    internal PlaygroundObservationView? Observation => _observation;
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
            _source.Text = $"Playground 无法建立：{main.PlaygroundConfigurationId} · {status}";
            GD.PrintErr($"PLAYGROUND_ESTABLISH_FAILED config={main.PlaygroundConfigurationId} status={status}");
            if (IsSmoke() || IsAuthority())
                GetTree().Quit(1);
            return;
        }

        _session = session;
        _session.Changed += MarkDirty;
        main.SetVoxelDevelopmentSession(_session);
        _meshes.Rebuild(this, _session.Identity);
        _colliders.Rebuild(this, _session.Identity);
        _observation = new PlaygroundObservationView();
        AddChild(_observation);
        _observation.Initialize(_session, _camera, _colliders, () => main.ConsoleOpen, OverviewPose);
        main.SetPlaygroundObserver(_observation.Observer);
        Refresh();

        if (IsSmoke() && PlaygroundObservationSmoke.Requested())
            _observationSmoke = new PlaygroundObservationSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
        else if (IsSmoke())
            _smoke = new PlaygroundSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
        else if (System.Environment.GetEnvironmentVariable(CaptureEnvironmentVariable) is { Length: > 0 } capturePath)
            _capture = new PlaygroundCapture(this, capturePath, System.Environment.GetEnvironmentVariable(CaptureStepsEnvironmentVariable));
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
        _capture?.Advance();
        _authority?.Advance();
    }

    public override void _ExitTree()
    {
        if (_session is not null)
        {
            _session.Changed -= MarkDirty;
            _session.Detach();
        }

        if (GetNodeOrNull<Main>("/root/Main") is { } main)
            main.SetVoxelDevelopmentSession(null);
    }

    internal string ApplyOperation(PlaygroundOperation operation)
    {
        if (_session is null)
            return "NoSession";
        var status = operation.Apply(_session);
        _result.Text = $"最近操作：{operation.Describe()} → {status}";
        Refresh();
        return status;
    }

    internal void Press(string button) => _buttons[button].EmitSignal(BaseButton.SignalName.Pressed);

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

    private PlaygroundEstablishStatus EstablishOrResume(Main main, out VoxelDevelopmentSession? session)
    {
        session = null;
        var reloader = main.Reloader ?? throw new InvalidOperationException("Content reloader is unavailable.");
        var store = new FileWorldSaveStore(main.IsolatedUserDataDirectory);
        var rawSaveId = System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable);
        if (!(IsSmoke() || IsAuthority()) || !Guid.TryParse(rawSaveId, out var saveId))
            return PlaygroundSession.TryEstablish(main.PlaygroundConfigurationId, reloader.Catalog, reloader.Composites, store, out session);

        var configuration = PlaygroundConfigurations.Find(main.PlaygroundConfigurationId);
        if (configuration is null)
            return PlaygroundEstablishStatus.UnknownConfiguration;
        var loaded = new VoxelDevelopmentSession(reloader.Catalog, configuration.BaseWorld, store, reloader.Composites);
        var result = loaded.Load(saveId);
        if (!result.Succeeded)
        {
            loaded.Detach();
            GD.PrintErr($"PLAYGROUND_SMOKE_FAILED load status={result.Status}");
            return PlaygroundEstablishStatus.InitialStateRejected;
        }

        session = loaded;
        return PlaygroundEstablishStatus.Established;
    }

    private void MarkDirty() => _dirty = true;

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
        return $"步 {session.SimulationStep} · {(session.SimulationPaused ? "已暂停" : "运行中")} · 车辆 {session.QueryComposites().Count} · {motion}\n" +
               $"线路段 {rails.Segments.Count} 枕木 {rails.SleeperCount} · 世界体素 {session.World.VoxelCount} · 轨道采样碰撞 {collision}";
    }

    private static string SourceText(WorldSource? source) =>
        source is null
            ? "Playground · 当前世界没有固定配置来源"
            : $"Playground · {source.ConfigurationId} v{source.ConfigurationVersion} · {source.Kind} · 参数 {source.EffectiveParameters.Count} · 内容依赖 {source.ContentDependencies.Count}";

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
        AddButton(controls, "run", "运行", () => ApplyOperation(PlaygroundOperation.Resume()));
        AddButton(controls, "pause", "暂停", () => ApplyOperation(PlaygroundOperation.Pause()));
        AddButton(controls, "step", "单步", () => ApplyOperation(PlaygroundOperation.Advance(1)));
        _stepCount = new SpinBox { MinValue = 1, MaxValue = 100000, Value = 60, Rounded = true };
        controls.AddChild(_stepCount);
        AddButton(controls, "step_n", "推进 N 步", () => ApplyOperation(PlaygroundOperation.Advance((ulong)_stepCount.Value)));
        AddButton(controls, "start", "启动列车", () => ApplyEdit("start"));
        AddButton(controls, "stop", "停车", () => ApplyEdit("stop"));
        AddButton(controls, "save", "保存", () => SaveWorld());

        var world = new HBoxContainer();
        world.AddThemeConstantOverride("separation", 6);
        box.AddChild(world);
        world.AddChild(new Label { Text = "世界格" });
        var probe = PlaygroundSequences.EditProbe;
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
    internal PlaygroundOperation EditOperation(string button)
    {
        var cell = new WorldVoxelCoord((int)_worldCell[0].Value, (int)_worldCell[1].Value, (int)_worldCell[2].Value);
        var local = new LocalVoxelCoord((int)_localCell[0].Value, (int)_localCell[1].Value, (int)_localCell[2].Value);
        var amount = (int)_editAmount.Value;
        ulong? at = _editDelay.Value > 0 && _session is not null ? _session.SimulationStep + (ulong)_editDelay.Value : null;
        var marker = HollowShellRefit.MarkerId;
        return button switch
        {
            "start" => PlaygroundOperation.Start(atStep: at),
            "stop" => PlaygroundOperation.Stop(at),
            "edit_repair" => PlaygroundOperation.Submit(new RailRepairEdit(new[] { cell }), at),
            "edit_construct" => PlaygroundOperation.Submit(new RailConstructEdit(StraightRailFixture.CreateFixturePlan()), at),
            "edit_assemble" => PlaygroundOperation.Submit(TrainAssembleEdit.StaticFixture, at),
            "edit_add" => PlaygroundOperation.Submit(new VoxelAddEdit(cell, PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity), at),
            "edit_remove" => PlaygroundOperation.Submit(new VoxelRemoveEdit(cell), at),
            "edit_damage" => PlaygroundOperation.Submit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(cell, amount) }), at),
            "edit_manufacture" => PlaygroundOperation.Submit(
                new VehicleManufactureEdit(HollowShellFixture.DefinitionId, CompositeTransform.Translation(cell.X, cell.Y, cell.Z)), at),
            "vehicle_damage" => PlaygroundOperation.DamageLatest(local, amount, at),
            "refit_add" => PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(local, marker), at),
            "refit_remove" => PlaygroundOperation.RefitLatest(PlaygroundRefit.Remove(local), at),
            "refit_move" => PlaygroundOperation.RefitLatest(PlaygroundRefit.Move(local, new LocalVoxelCoord(local.X + 1, local.Y, local.Z)), at),
            "refit_rotate" => PlaygroundOperation.RefitLatest(PlaygroundRefit.Rotate(local, HollowShellRefit.MarkerOrientation), at),
            "refit_replace" => PlaygroundOperation.RefitLatest(PlaygroundRefit.Replace(local, HollowShellRefit.ReplacementId), at),
            "refit_batch" => PlaygroundOperation.RefitLatest(new[]
            {
                PlaygroundRefit.Add(local, marker),
                PlaygroundRefit.Add(new LocalVoxelCoord(local.X + 2, local.Y, local.Z), marker)
            }, at),
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, null)
        };
    }

    private string EditsText()
    {
        var session = _session!;
        var log = session.QueryEditLog();
        var last = log.Count == 0 ? "无" : PlaygroundCheck.DescribeRecord(log[^1]);
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
        var result = _session?.Save("Playground " + (_session.WorldSource?.ConfigurationId ?? "world"))
                     ?? new WorldSaveResult(WorldSaveStatus.Unavailable);
        _result.Text = $"最近操作：保存 → {result.Status} {result.SaveId}";
        return result;
    }

    private static bool IsSmoke() =>
        string.Equals(System.Environment.GetEnvironmentVariable(SmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private static bool IsAuthority() =>
        string.Equals(System.Environment.GetEnvironmentVariable(AuthorityEnvironmentVariable), "1", StringComparison.Ordinal);
}

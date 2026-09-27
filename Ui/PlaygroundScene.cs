using System.Globalization;
using Godot;
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

    internal static readonly WorldVoxelCoord EmptyProbe = new(100, 10, 15);

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
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private bool _dirty = true;
    private PlaygroundSmoke? _smoke;
    private PlaygroundCapture? _capture;

    internal VoxelDevelopmentSession? Session => _session;
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
            if (IsSmoke())
                GetTree().Quit(1);
            return;
        }

        _session = session;
        _session.Changed += MarkDirty;
        main.SetVoxelDevelopmentSession(_session);
        _meshes.Rebuild(this, _session.Identity);
        _colliders.Rebuild(this, _session.Identity);
        PlaceOverviewCamera();
        Refresh();

        if (IsSmoke())
            _smoke = new PlaygroundSmoke(this, System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable));
        else if (System.Environment.GetEnvironmentVariable(CaptureEnvironmentVariable) is { Length: > 0 } capturePath)
            _capture = new PlaygroundCapture(this, capturePath, System.Environment.GetEnvironmentVariable(CaptureStepsEnvironmentVariable));
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

        ApplyMotionPresentation();
        _smoke?.Advance();
        _capture?.Advance();
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
        if (!IsSmoke() || !Guid.TryParse(rawSaveId, out var saveId))
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

    private void ApplyMotionPresentation()
    {
        if (_session is null)
            return;
        _composites.ApplyDisplayPoses(_session.QueryDisplayPoses());
        ApplyFollowCamera();
        _state.Text = StateText();
    }

    private void ApplyFollowCamera()
    {
        var follow = _session!.QueryFollowedCamera();
        if (follow is null || !_camera.IsInsideTree())
            return;
        var definition = _session.QueryComposite(follow.VehicleId)?.DefinitionId ?? StaticTrainFixture.CarriageId;
        var frame = AlongRailDisplay.FrameFollowCamera(follow, definition);
        var look = new Vector3((float)frame.LookX, (float)frame.LookY, (float)frame.LookZ);
        _camera.Position = new Vector3((float)frame.PositionX, (float)frame.PositionY, (float)frame.PositionZ);
        if ((_camera.Position - look).LengthSquared() > 1e-8f)
            _camera.LookAt(look, Vector3.Up);
    }

    private void PlaceOverviewCamera()
    {
        _camera.Position = new Vector3(76, 16, -8);
        _camera.LookAt(new Vector3(76, 2, 16), Vector3.Up);
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
        AddButton(controls, "start", "启动列车", () => ApplyOperation(PlaygroundOperation.Start()));
        AddButton(controls, "stop", "停车", () => ApplyOperation(PlaygroundOperation.Stop()));
        AddButton(controls, "save", "保存", () => SaveWorld());
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
}

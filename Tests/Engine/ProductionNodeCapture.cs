using System.Text.Json;
using System.Reflection;
using Godot;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Engine.Tests;

// 生产 App 的 Debug 取证入口；固定 Demo 路线复用已有控制台解析与提交。
public partial class ProductionNodeCapture : Node
{
    private Train.App.App _app = null!;
    private StreamWriter _frames = null!;
    private string _directory = "";
    private readonly SpinBox _train = new() { MinValue = 1, MaxValue = 1000000, Value = 1, CustomMinimumSize = new(120, 36) };
    private readonly Label _status = new() { Position = new(16, 50), MouseFilter = Control.MouseFilterEnum.Ignore };
    private WorldObjectId? _subject;
    private ulong _draw;
    private string? _lastPhase;
    private bool _worldRecorded;
    private FieldInfo[] _presentFields = [];

    public static void Start(Node root) => root.AddChild(new ProductionNodeCapture { Name = "ProductionNodeCapture" });

    public override void _Ready()
    {
        _directory = ProjectSettings.GlobalizePath($"res://.godot/validation/production-node-capture-{DateTime.UtcNow:yyyyMMddTHHmmssfff}");
        Directory.CreateDirectory(_directory);
        _frames = new(Path.Combine(_directory, "frames.jsonl")) { AutoFlush = true };
        _app = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
        AddChild(_app);
        var diagnostics = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Train.Scenes")
            .GetType("Train.Scenes.NodePresentDiagnostics", throwOnError: true)!;
        _presentFields = new[] { "Calls", "Ticks", "LastTicks", "LastNode" }.Select(name =>
            diagnostics.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        var layer = new CanvasLayer { Layer = 120 };
        AddChild(layer);
        var bar = new HBoxContainer { Position = new(16, 8) };
        layer.AddChild(bar);
        bar.AddChild(new Label { Text = "生产逐绘制帧取证 · 列车" });
        bar.AddChild(_train);
        var follow = new Button { Text = "跟随并记录", Name = "CaptureFollow" };
        bar.AddChild(follow);
        follow.Pressed += () =>
        {
            if (_app.Host?.GetStatus()?.View.FindTrain(new((ulong)_train.Value)) is not Lookup<TrainView>.Found train || train.Value.Vehicles.IsEmpty) return;
            _subject = new WorldObjectId.Instance(new InstanceId.Vehicle(train.Value.Vehicles[0]));
            _app.WorldScenes!.Open(new WorldSceneTarget.Follow(train.Value.Id, train.Value.Vehicles[0]));
            _lastPhase = null;
        };
        var route = new Button { Text = "Demo 停靠 / 途经计划", Name = "CaptureRoute" };
        bar.AddChild(route);
        layer.AddChild(_status);
        route.Pressed += () =>
        {
            var commands = new Train.App.TrainConsoleCommands(_app.Host!, _app.Logger, error => GD.PrintErr(error));
            var id = ((ulong)_train.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            commands.Execute(["plan", id, "once", "station:13:3", "waypoint:8", "station:5:3"]);
            commands.Execute(["start", id]);
        };
        RenderingServer.FramePostDraw += Capture;
        GD.Print("PRODUCTION NODE CAPTURE: " + _directory);
    }

    private void Capture()
    {
        var status = _app.Host?.GetStatus();
        var world = _app.WorldScenes;
        var display = world?.Display;
        var frame = display?.DisplayedFrame;
        var train = status?.View.FindTrain(new((ulong)_train.Value)) is Lookup<TrainView>.Found found ? found.Value : null;
        var pose = frame is not null && _subject is not null && frame.FindPose(_subject) is Lookup<ScenePose>.Found positioned ? positioned.Value : null;
        var node = _subject is null ? null : display?.Objects.Values.FirstOrDefault(item => item.Source.Subject == _subject && item.Source.Parent is null)?.Root;
        var camera = world?.View.CameraRig.Camera;
        var phase = train?.Position switch
        {
            TrainPosition.Node n => $"node-{n.Id.Value}-{n.Phase}-platform-{n.PlatformNumber}",
            TrainPosition.Edge e => $"edge-{e.Id.Value}",
            _ => "no-train"
        };
        string? screenshot = null;
        if (pose is not null && phase != _lastPhase)
        {
            screenshot = $"{_draw:D8}-{phase}.png";
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.Combine(_directory, screenshot)) != Error.Ok) throw new IOException("逐帧取证截图保存失败。");
            _lastPhase = phase;
        }
        if (!_worldRecorded && status is not null)
        {
            System.IO.File.WriteAllText(Path.Combine(_directory, "world.json"), JsonSerializer.Serialize(new
            {
                seed = status.View.World.WorldSeed, inputIdentity = status.View.InputIdentity, nodes = status.View.World.Nodes, edges = status.View.World.Edges,
                trains = status.View.World.Trains.Select(t => status.View.FindTrain(t.Id) is Lookup<TrainView>.Found f ? new { f.Value.Id, f.Value.Vehicles, f.Value.LengthMeters, position = f.Value.Position.ToString(), f.Value.Activity } : null),
                railwayRule = _app.Host!.Content!.FindRailwayAppearance(status.View.World.RailwayRule) is Train.Content.Contracts.ContentLookup<Train.Content.Contracts.RailwayAppearanceProjection>.Found rule ? rule.Value : null
            }));
            _worldRecorded = true;
        }
        var plan = train?.Plan?.Stops.Select(stop => stop switch
        {
            PlanStop.Station station => $"station:{station.Node.Value}:steps={station.DwellSteps}",
            PlanStop.Waypoint waypoint => $"waypoint:{waypoint.Node.Value}",
            _ => throw new InvalidOperationException("未知计划项。")
        }).ToArray();
        _status.Text = $"{phase} · {train?.Activity} · {train?.Position}\n实际计划：{string.Join(", ", plan ?? [])}；next={train?.NextStopIndex} dwell={train?.RemainingDwellSteps}";
        _frames.WriteLine(JsonSerializer.Serialize(new
        {
            draw = ++_draw, usec = Time.GetTicksUsec(), engineDraw = Godot.Engine.GetFramesDrawn(),
            stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
            nodePresent = _presentFields.Select(field => field.GetValue(null)).ToArray(),
            prepareCalls = world?.DiagnosticPrepareCalls, prepareTicks = world?.DiagnosticPrepareTicks,
            prepareLastTicks = world?.DiagnosticPrepareLastTicks,
            train = (ulong)_train.Value, subject = _subject?.ToString(), phase, corePosition = status?.View.Position,
            trainPosition = train?.Position.ToString(), plan, planLoop = train?.Plan?.Loop, nextStop = train?.NextStopIndex, dwellSteps = train?.RemainingDwellSteps, activity = train?.Activity.ToString(), speed = train?.SpeedMetersPerSecond,
            paused = status?.IsPaused, remainder = status?.Remainder, interpolation = status is null ? (double?)null : Math.Clamp(status.Remainder / SimulationTime.StepSeconds, 0, 1), frame = frame?.Update.Frame,
            origin = frame?.Origin, readiness = frame?.Readiness.ToString(), loading = world?.View.Loading, preparing = display?.IsPreparing,
            simulationSeconds = frame?.SimulationSeconds,
            findPosePosition = pose is null ? null : new[] { pose.Position.X, pose.Position.Y, pose.Position.Z },
            findPoseRotation = pose is null ? null : new[] { pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W },
            vehiclePosition = node is null ? null : V(node.GlobalPosition), vehicleRotation = node is null ? null : Q(node.GlobalBasis.GetRotationQuaternion()),
            cameraPosition = camera is null ? null : V(camera.GlobalPosition), cameraRotation = camera is null ? null : Q(camera.GlobalBasis.GetRotationQuaternion()),
            focus = world is null ? null : V(world.View.CameraRig.Focus), screenshot
        }));
    }

    private static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
    private static float[] Q(Quaternion q) => [q.X, q.Y, q.Z, q.W];

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= Capture;
        _frames?.Dispose();
    }
}

using System.Text.Json;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Ui;

namespace Train.Engine.Tests;

public partial class NodeTransitionPreview : Node
{
    private readonly WorldSceneView _view = new();
    private readonly Label _status = new() { Position = new(16, 16) };
    private SceneDisplay? _display;
    private GodotMainThreadDispatcher? _dispatcher;
    private AssetResourceCache? _cache;
    private IWorldSceneSession? _session;
    private bool _running;
    private readonly Dictionary<string, (Vector3 Position, Quaternion Rotation)> _keyframes = new();
    private readonly string _directory = ProjectSettings.GlobalizePath($"res://.godot/validation/node-transition-preview-{DateTime.UtcNow:yyyyMMddTHHmmssfff}");

    public static void Start(Node root) => root.AddChild(new NodeTransitionPreview { Name = "NodeTransitionPreview" });

    public override async void _Ready()
    {
        AddChild(_view);
        var layer = new CanvasLayer();
        AddChild(layer);
        layer.AddChild(_status);
        var run = new Button { Text = "运行 / 重跑节点帧序列", Name = "RunNodeTransitions", Position = new(16, 100) };
        layer.AddChild(run);
        run.Pressed += async () => { if (!_running) await Run(); };
        await Run();
    }

    private async Task Run()
    {
        _running = true;
        _keyframes.Clear();
        try
        {
            Directory.CreateDirectory(_directory);
            System.IO.File.WriteAllText(Path.Combine(_directory, "frames.jsonl"), "");
            System.IO.File.WriteAllText(Path.Combine(_directory, "loading.jsonl"), "");
            if (_session is not null) await _session.DisposeAsync();
            _display?.Dispose();
            _cache?.Dispose();
            _dispatcher?.Dispose();
            var content = NodeTransitionFixture.Content();
            _dispatcher = new();
            _cache = new(_dispatcher);
            _display = new(_cache, content);
            _view.AddChild(_display.Root);
            var first = NodeTransitionFixture.Snapshot(content, Position(NodeTravelPhase.Approaching, 0), 0);
            _session = ((SceneResult<IWorldSceneSession>.Success)Train.Scenes.ScenesFactory.Create().OpenWorld(new(Guid.NewGuid()), first)).Value;
            _session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            ulong step = 0;
            async Task Sample(string name, TrainPosition position, TrainOrderFacing facing = TrainOrderFacing.FirstEndLeading, string? boundaryFrom = null)
            {
                var snapshot = NodeTransitionFixture.Snapshot(content, position, ++step, facing);
                var observed = _session.Observe(snapshot, []);
                if (observed is not SceneResult<SimulationPosition>.Success) throw new InvalidOperationException(observed.ToString());
                foreach (var alpha in new[] { 0d, .25, .5, .75, 1d }) await Capture(name, snapshot, alpha, boundaryFrom);
            }
            await Sample("00-edge-before-entry", new TrainPosition.Edge(new(2), 2, TravelDirection.Forward, 799.99));
            await Sample("00-node-boundary", Position(NodeTravelPhase.Approaching, 0), boundaryFrom: "00-edge-before-entry");
            await Sample("01-entry", Position(NodeTravelPhase.Approaching, 40));
            await Sample("02-W-waiting", Position(NodeTravelPhase.Waiting, 80, null));
            await Sample("03-W-platform-granted", Position(NodeTravelPhase.Approaching, 80));
            await Sample("04-approach-S", Position(NodeTravelPhase.Approaching, 110));
            await Sample("05-S-docked", Position(NodeTravelPhase.Docked, 128));
            await Sample("06-departure", Position(NodeTravelPhase.Departing, 150));
            await Sample("07-T-departure-start", Position(NodeTravelPhase.Departing, 176));
            await Sample("07-exit-190", Position(NodeTravelPhase.Departing, 190));
            await Sample("07-before-L-255.99", Position(NodeTravelPhase.Departing, 255.99));
            await Sample("07-L-256", Position(NodeTravelPhase.Departing, 256));
            await Sample("07-edge-after-exit", new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 0), boundaryFrom: "07-L-256");
            await Sample("07-track2-departure", Position(NodeTravelPhase.Departing, 150, nextTrack: 2));
            await Sample("07-track2-T", Position(NodeTravelPhase.Departing, 176, nextTrack: 2));
            await Sample("07-track2-node-L", Position(NodeTravelPhase.Departing, 256, nextTrack: 2));
            await Sample("07-track2-edge-after-exit", new TrainPosition.Edge(new(1), 2, TravelDirection.Forward, 0), boundaryFrom: "07-track2-node-L");
            await Sample("08-through-entry", Position(NodeTravelPhase.Approaching, 40, null));
            await Sample("09-through-departure", Position(NodeTravelPhase.Departing, 150, null));
            await Sample("10-return-S", Position(NodeTravelPhase.Docked, 128, 1, 2));
            await Sample("11-return-departure", Position(NodeTravelPhase.Departing, 150, 1, 2), TrainOrderFacing.LastEndLeading);
            await Sample("11-return-T-176", Position(NodeTravelPhase.Departing, 176, 1, 2), TrainOrderFacing.LastEndLeading);
            await Sample("11-return-190", Position(NodeTravelPhase.Departing, 190, 1, 2), TrainOrderFacing.LastEndLeading);
            await Sample("11-return-L-256", Position(NodeTravelPhase.Departing, 256, 1, 2), TrainOrderFacing.LastEndLeading);
            await Sample("11-return-edge-track1", new TrainPosition.Edge(new(2), 1, TravelDirection.Reverse, 800), TrainOrderFacing.LastEndLeading, "11-return-L-256");
            await Sample("12-same-side-S", Position(NodeTravelPhase.Docked, 128, 1, 3));
            await Sample("13-same-side-departure", Position(NodeTravelPhase.Departing, 150, 1, 3));
            await Sample("13-same-side-T-176", Position(NodeTravelPhase.Departing, 176, 1, 3));
            await Sample("13-same-side-190", Position(NodeTravelPhase.Departing, 190, 1, 3));
            // This fixture's connecting S-bends keep the Z tangent's sign; the half-turn changes it.
            var low = 176d;
            var high = 256d;
            for (var i = 0; i < 30; i++)
            {
                var distance = (low + high) / 2;
                var snapshot = NodeTransitionFixture.Snapshot(content, Position(NodeTravelPhase.Departing, distance, 1, 3), ++step);
                if (_session.Observe(snapshot, []) is not SceneResult<SimulationPosition>.Success)
                    throw new InvalidOperationException("回转线取样输入被拒绝。");
                var frame = ((SceneResult<ISceneFrame>.Success)_session.Present(new(1, null, SceneDebugLayer.None), _display.DisplayedFrame)).Value;
                var pose = ((Lookup<ScenePose>.Found)frame.FindPose(NodeTransitionFixture.Subject)).Value;
                var forward = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, pose.Rotation);
                if (forward.Z > 0) low = distance;
                else high = distance;
            }
            await Sample("13-same-side-turn-midpoint", Position(NodeTravelPhase.Departing, (low + high) / 2, 1, 3));
            await Sample("13-same-side-216", Position(NodeTravelPhase.Departing, 216, 1, 3));
            await Sample("13-same-side-240", Position(NodeTravelPhase.Departing, 240, 1, 3));
            await Sample("13-same-side-L-256", Position(NodeTravelPhase.Departing, 256, 1, 3));
            await Sample("13-same-side-edge", new TrainPosition.Edge(new(3), 1, TravelDirection.Reverse, 800), boundaryFrom: "13-same-side-L-256");
            _status.Text += "\n完成；最后一帧保留。证据位于 " + _directory;
            GD.Print("NODE FRAMES COMPLETE: " + _directory);
        }
        catch (Exception exception)
        {
            _status.Text = "节点取证失败：" + exception.Message;
            GD.PrintErr(exception);
        }
        finally { _running = false; }
    }

    private static TrainPosition Position(NodeTravelPhase phase, double distance, int? platform = 1, ulong next = 1, int nextTrack = 1) =>
        new TrainPosition.Node(new(1), new(2), 2, new(next), phase == NodeTravelPhase.Departing ? nextTrack : null, phase, distance, platform);

    private async Task Capture(string name, SceneWorldSnapshot snapshot, double alpha, string? boundaryFrom)
    {
        var scenario = name.Contains("return") ? "return-track2-to-track1" : name.Contains("same-side") ? "same-side-turn" : name.Contains("track2") ? "entry2-exit2" : "entry2-exit1";
        var attempts = 0;
        ISceneFrame frame;
        while (true)
        {
            frame = ((SceneResult<ISceneFrame>.Success)_session!.Present(new(alpha, null, SceneDebugLayer.None), _display!.DisplayedFrame)).Value;
            _view.SetLoading(frame.Readiness == SceneReadiness.Loading);
            using var candidate = _display.Prepare(frame);
            if (candidate is not null)
            {
                candidate.Commit();
                frame = candidate.Frame;
                _view.ShowFrame(frame, NodeTransitionFixture.Subject);
                if (frame.FindPose(NodeTransitionFixture.Subject) is Lookup<ScenePose>.Found && frame.Readiness == SceneReadiness.Ready) break;
            }
            _status.Text = $"{name} alpha={alpha} readiness={frame.Readiness} loading={_view.Loading}\n受控 Core 公开快照；真实 Scenes / Godot 节点和相机；不是生产 Core 时序或性能证据。";
            System.IO.File.AppendAllText(Path.Combine(_directory, "loading.jsonl"), JsonSerializer.Serialize(new { name, scenario, enteredTrack = (snapshot.Core.World.Trains[0].Position as TrainPosition.Node)?.EnteredTrack, nextTrack = (snapshot.Core.World.Trains[0].Position as TrainPosition.Node)?.NextTrack, origin = new[] { frame.Origin.X, frame.Origin.Y, frame.Origin.Z }, alpha, attempt = attempts, readiness = frame.Readiness.ToString(), loading = _view.Loading }) + System.Environment.NewLine);
            if (++attempts > 3600) throw new InvalidOperationException("场景 3600 帧内未就绪。");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var pose = ((Lookup<ScenePose>.Found)frame.FindPose(NodeTransitionFixture.Subject)).Value;
        var item = _display.Objects.Values.Single(item => item.Source.Subject == NodeTransitionFixture.Subject && item.Source.Parent is null);
        var root = item.Root;
        var expected = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        var rotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
        Assertions.Require(root.GlobalPosition.DistanceTo(expected) < .003f && root.GlobalBasis.GetRotationQuaternion().AngleTo(rotation) < .003f,
            $"真实 Godot 车辆节点姿态必须与 FindPose 一致：node={root.GlobalTransform}, expected={pose}, frame={frame.Update.Frame}。");
        float? boundaryDisplacement = null, boundaryDirectionAngle = null, boundaryHeightDifference = null;
        if (alpha == 1)
        {
            if (boundaryFrom is not null)
            {
                // SwitchOrigin preserves display coordinates across edge/node spaces.
                var previous = _keyframes[boundaryFrom];
                boundaryDisplacement = root.GlobalPosition.DistanceTo(previous.Position);
                boundaryDirectionAngle = root.GlobalBasis.GetRotationQuaternion().AngleTo(previous.Rotation);
                boundaryHeightDifference = Mathf.Abs(root.GlobalPosition.Y - previous.Position.Y);
                Assertions.Require(boundaryDisplacement < .05f && boundaryDirectionAngle < .003f && boundaryHeightDifference < .003f,
                    $"边界连续性失败：{boundaryFrom} → {name}, displacement={boundaryDisplacement}, angle={boundaryDirectionAngle}, height={boundaryHeightDifference}, origin={frame.Origin}。");
            }
            _keyframes[name] = (root.GlobalPosition, root.GlobalBasis.GetRotationQuaternion());
        }
        // Inputs are separate keyframes; let the normal follow camera catch up after their deliberate jumps.
        for (var i = 0; i < 60; i++)
        {
            _view.CameraRig.Update(1d / 60);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _status.Text = $"{name} alpha={alpha} readiness={frame.Readiness} loading={_view.Loading}\n受控 Core 公开快照；真实 Scenes / Godot 节点和相机；不是生产 Core 时序或性能证据。\nNode3D == FindPose：通过";
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var filename = $"{name}-{alpha:0.00}";
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.Combine(_directory, filename + ".png")) != Error.Ok) throw new IOException("截图保存失败。");
        static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
        static float[] Q(Quaternion q) => [q.X, q.Y, q.Z, q.W];
        var nodePosition = snapshot.Core.World.Trains[0].Position as TrainPosition.Node;
        var row = new { name, scenario, enteredTrack = nodePosition?.EnteredTrack, nextTrack = nodePosition?.NextTrack, origin = new[] { frame.Origin.X, frame.Origin.Y, frame.Origin.Z }, boundaryFrom, boundaryDisplacement, boundaryDirectionAngle, boundaryHeightDifference, alpha, cameraSettlingFrames = 60, corePosition = snapshot.Core.Position.ToString(), trainPosition = snapshot.Core.World.Trains[0].Position.ToString(),
            evidence = "controlled public Core snapshot; real ScenesFactory + SceneDisplay + WorldCamera; no production Core advancement or performance evidence",
            frame.SimulationSeconds, readiness = frame.Readiness.ToString(), loading = _view.Loading, attempts,
            displayedPosition = V(root.GlobalPosition), displayedRotation = Q(root.GlobalBasis.GetRotationQuaternion()),
            findPosePosition = new[] { pose.Position.X, pose.Position.Y, pose.Position.Z }, findPoseRotation = new[] { pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W },
            cameraFocus = V(_view.CameraRig.Focus), cameraPosition = V(_view.CameraRig.Camera.GlobalPosition), cameraRotation = Q(_view.CameraRig.Camera.GlobalBasis.GetRotationQuaternion()), screenshot = filename + ".png" };
        System.IO.File.AppendAllText(Path.Combine(_directory, "frames.jsonl"), JsonSerializer.Serialize(row) + System.Environment.NewLine);
    }

    public override void _ExitTree()
    {
        _display?.Dispose();
        _cache?.Dispose();
        _dispatcher?.Dispose();
        if (_session is not null) _ = _session.DisposeAsync();
    }
}

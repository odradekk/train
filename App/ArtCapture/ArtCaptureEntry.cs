using System.Diagnostics;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using Train.App.Assets;
using Train.App.Composition;
using Train.App.Logging;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;

namespace Train.App.ArtCapture;

public partial class ArtCaptureEntry : Node
{
    private ArtCaptureRun? _run;

    public override async void _Ready()
    {
        // 官方导出模板在进入场景前拒绝 --scene；此检查防备允许路径覆盖的自编译模板。
        if (!OS.IsDebugBuild()) { GD.PrintErr("art_capture_failed: 仅 Debug 可用。"); GetTree().Quit(1); return; }
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _run = new(this);
            await _run.CaptureAsync(CaptureInput.Parse(OS.GetCmdlineUserArgs()));
            GD.Print("art_capture_completed");
            GetTree().Quit();
        }
        catch (Exception exception) { GD.PrintErr($"art_capture_failed: {exception}"); GetTree().Quit(1); }
    }

    public override void _ExitTree() => _run?.Dispose();
}

internal sealed class ArtCaptureRun(Node owner) : IDisposable
{
    private GodotMainThreadDispatcher? _dispatcher;
    private TrainLogger? _logger;
    internal HostCoordinator? Host { get; private set; }
    internal WorldSceneController? World { get; private set; }
    internal Func<TrainLogger, string, GodotMainThreadDispatcher, HostCoordinator> CreateHost { get; set; } = HostComposition.CreateHost;
    internal Func<IScenesFactory> CreateScenes { get; set; } = HostComposition.CreateScenes;
    private string? _sceneFailure;
    private string? _saveHash;
    private string? _output;
    private WorldObjectId? _subject;
    private readonly List<CaptureImage> _images = [];
    private System.Numerics.Vector3 _initialFront;
    private bool _turned;

    internal async Task CaptureAsync(CaptureInput input)
    {
        await RestoreAsync(input);
        if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("截图需要真实渲染窗口，不能使用 --headless。");
        Directory.CreateDirectory(_output!);
        var scene = input.Location is WorldLocation.Edge edge ? $"edge-{edge.Id.Value}" : $"node-{((WorldLocation.Node)input.Location).Id.Value}";
        foreach (var lighting in new[] { InspectionLighting.Daylight, InspectionLighting.Overcast, InspectionLighting.Night })
        {
            World!.View.SetLighting(lighting);
            foreach (var distance in new[] { "near", "follow", "far" })
            {
                PlaceCamera(distance, 0);
                await SaveImageAsync($"{scene}_{distance}_{lighting.ToString().ToLowerInvariant()}_0000.png");
            }
        }
        World!.View.SetLighting(InspectionLighting.Daylight);
        for (var frame = 0; frame < 60; frame++)
        {
            await AdvanceTurnFrameAsync();
            PlaceCamera("follow", 0);
            await SaveImageAsync($"{scene}_turn_daylight_{frame:D4}.png");
        }
        RequireTurn();
        for (var frame = 0; frame < 60; frame++)
        {
            PlaceCamera("follow", Mathf.Tau * frame / 60);
            await SaveImageAsync($"{scene}_orbit_daylight_{frame:D4}.png");
        }
        var commit = typeof(ArtCaptureEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[^1];
        File.WriteAllText(Path.Combine(_output!, "manifest.json"), JsonSerializer.Serialize(new
        {
            commit, inputSaveHash = _saveHash, scene, train = input.Train.Value,
            viewportSize = new[] { owner.GetViewport().GetTexture().GetWidth(), owner.GetViewport().GetTexture().GetHeight() },
            images = _images
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    }

    internal async Task RestoreAsync(CaptureInput input)
    {
        var bytes = File.ReadAllBytes(input.SavePath);
        var document = SaveContainer.Decode(Encoding.UTF8.GetString(bytes));
        _saveHash = Convert.ToHexString(SHA256.HashData(bytes));
        _output = ProjectSettings.GlobalizePath($"res://.godot/validation/{input.Name}");
        if (File.Exists(_output) || Directory.Exists(_output) && Directory.EnumerateFileSystemEntries(_output).Any())
            throw new IOException("输出目录已存在且非空。");
        _dispatcher = new();
        _logger = TrainLogger.StartSession(ProjectSettings.GlobalizePath("res://.godot/art-capture/logs"),
            LogLevel.Info, new GodotLogOutput());
        Host = CreateHost(_logger, OS.GetUserDataDir(), _dispatcher);
        var restored = Host.BeginRestore(document.CorePayload, document.ContentPayload, Host.SessionOptions!);
        if (restored.Problem is { } problem) throw new InvalidDataException($"恢复失败：{problem.Code}");
        await WaitAsync(() => !Host.IsBusy, () => { _dispatcher.Pump(); Host.ProcessFrame(0); });
        var status = Host.GetStatus() ?? throw new InvalidDataException("存档恢复失败。");
        if (Host.MissingConfirmation is not null) throw new InvalidDataException("存档依赖缺失。");
        var train = input.Validate(status.View);
        _subject = new WorldObjectId.Instance(new InstanceId.Vehicle(train.Vehicles[0]));
        Host.SetPaused(true);
        Host.AutoPauseOnFocusLoss = false;
        World = new(Host, CreateScenes(), _logger);
        World.ReturnedToMap += problem => _sceneFailure = problem?.Code ?? "场景已关闭";
        owner.AddChild(World.View);
        World.Open(new WorldSceneTarget.Follow(input.Train, train.Vehicles[0]));
        await DisplayAsync();
        _initialFront = Front();
    }

    private async Task WaitAsync(Func<bool> ready, Action pump)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(120))
        {
            pump();
            if (_sceneFailure is not null) throw new InvalidDataException($"场景失败：{_sceneFailure}");
            if (Host?.MissingConfirmation is not null) throw new InvalidDataException("存档依赖缺失。");
            if (ready()) return;
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException("资源等待超过 120 秒。");
    }

    internal Task DisplayAsync() => WaitAsync(
        () => World!.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready } frame &&
            frame.FindPose(_subject!) is Lookup<ScenePose>.Found,
        () => World!.ProcessFrame(0, false));

    internal async Task AdvanceTurnFrameAsync()
    {
        var step = Host!.Step(2);
        if (step.Problem is not null || step.Completed != 2)
            throw new InvalidOperationException($"运动推进失败：{step.Problem?.Code ?? "未完成两个模拟步"}。");
        await DisplayAsync();
        _turned |= System.Numerics.Vector3.DistanceSquared(_initialFront, Front()) > 1e-10f;
    }

    internal void RequireTurn()
    {
        if (!_turned) throw new InvalidDataException("未过弯：60 帧中跟随车辆的朝向没有变化。");
    }

    private System.Numerics.Vector3 Front()
    {
        var pose = ((Lookup<ScenePose>.Found)World!.Display!.DisplayedFrame!.FindPose(_subject!)).Value;
        return System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, pose.Rotation);
    }

    private void PlaceCamera(string distance, float orbit)
    {
        var display = World!.Display!;
        var vehicle = display.Objects.Values.Single(item => item.Source.Subject == _subject && item.Source.Parent is null);
        Aabb? bounds = null;
        void Include(Node node)
        {
            if (node is MeshInstance3D { Mesh: not null } mesh)
            {
                var box = (vehicle.Root.GlobalTransform.AffineInverse() * mesh.GlobalTransform) * mesh.Mesh.GetAabb();
                bounds = bounds is { } previous ? previous.Merge(box) : box;
            }
            foreach (var child in node.GetChildren()) Include(child);
        }
        Include(vehicle.Root);
        var box = bounds ?? throw new InvalidDataException("跟随车辆没有可显示的网格。");
        var focus = vehicle.Root.GlobalTransform * box.GetCenter();
        var front = vehicle.Root.GlobalBasis * Vector3.Back;
        var yaw = Mathf.Atan2(front.X, front.Z) + orbit + (distance == "near" ? Mathf.Atan2(box.Size.X, box.Size.Z) : 0);
        var pitch = distance == "near" ? 0.2f : 0.55f;
        var range = distance == "far" ? WorldCamera.MaximumDistance : WorldCamera.DefaultDistance;
        if (distance == "near")
        {
            var direction = vehicle.Root.GlobalBasis.Inverse() * new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch),
                Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            var half = box.Size / 2;
            var low = 0f;
            var high = box.Size.Length() + 6;
            // 求相机到包围盒的最短距离为 6 格，而非到车辆中心的距离。
            for (var i = 0; i < 24; i++)
            {
                var middle = (low + high) / 2;
                var outside = (direction * middle).Abs() - half;
                outside = new(Mathf.Max(outside.X, 0), Mathf.Max(outside.Y, 0), Mathf.Max(outside.Z, 0));
                if (outside.LengthSquared() < 36) low = middle; else high = middle;
            }
            range = (low + high) / 2;
        }
        World.View.CameraRig.PlaceAbsolute(focus, range, yaw, pitch);
    }

    private async Task SaveImageAsync(string name)
    {
        await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = owner.GetViewport().GetTexture().GetImage();
        if (image is null || image.IsEmpty()) throw new InvalidDataException("视口没有渲染图像。");
        var error = image.SavePng(Path.Combine(_output!, name));
        if (error != Error.Ok) throw new IOException($"截图写盘失败：{error}。");
        var camera = World!.View.CameraRig.Camera.Transform;
        var focus = World.View.CameraRig.Focus;
        _images.Add(new(name, Host!.GetStatus()!.View.Position, World.Display!.DisplayedFrame!.SimulationSeconds,
            World.View.Lighting.ToString().ToLowerInvariant(),
            [camera.Basis.X.X, camera.Basis.X.Y, camera.Basis.X.Z, camera.Basis.Y.X, camera.Basis.Y.Y, camera.Basis.Y.Z,
                camera.Basis.Z.X, camera.Basis.Z.Y, camera.Basis.Z.Z, camera.Origin.X, camera.Origin.Y, camera.Origin.Z],
            [focus.X, focus.Y, focus.Z]));
    }

    public void Dispose()
    {
        World?.Dispose();
        Host?.Dispose();
        _dispatcher?.Dispose();
        _logger?.Dispose();
        World = null; Host = null; _dispatcher = null; _logger = null;
    }
}

internal sealed record CaptureImage(string File, SimulationPosition Position, double DisplaySeconds,
    string Lighting, float[] CameraTransform, float[] Focus);

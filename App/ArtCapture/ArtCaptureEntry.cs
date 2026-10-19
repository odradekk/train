using System.Diagnostics;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using Train.App.Assets;
using Train.App.Composition;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Saves;
using Train.Content.Contracts;
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
    internal Func<TrainLogger, string, GodotMainThreadDispatcher, NotificationPolicy, HostCoordinator> CreateHost { get; set; } = HostComposition.CreateHost;
    internal Func<IScenesFactory> CreateScenes { get; set; } = HostComposition.CreateScenes;
    private string? _sceneFailure;
    private string? _saveHash;
    private string? _output;
    private WorldObjectId[] _subjects = [];
    private CaptureInput? _input;
    private Aabb? _subjectBounds;
    private ScenePoint _subjectOrigin;
    private readonly List<CaptureImage> _images = [];
    private System.Numerics.Vector3 _initialFront;
    private bool _turned;

    internal async Task CaptureAsync(CaptureInput input)
    {
        await RestoreAsync(input);
        if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("截图需要真实渲染窗口，不能使用 --headless。");
        Directory.CreateDirectory(_output!);
        var scene = input.Location is WorldLocation.Edge edge ? $"edge-{edge.Id.Value}" : $"node-{((WorldLocation.Node)input.Location).Id.Value}";
        var farDistance = input.Location is WorldLocation.Node ? WorldCamera.NodeMaximumDistance : WorldCamera.MaximumDistance;
        if (input.Train.Value == 0) await PrepareSubjectCameraAsync(farDistance);
        foreach (var lighting in new[] { InspectionLighting.Daylight, InspectionLighting.Overcast, InspectionLighting.Night })
        {
            World!.View.SetLighting(lighting);
            foreach (var distance in new[] { "near", "follow", "far" })
            {
                PlaceCamera(distance, 0, farDistance);
                if (input.Train.Value == 0) await DisplayAsync(includeCamera: true);
                await SaveImageAsync($"{scene}_{distance}_{lighting.ToString().ToLowerInvariant()}_0000.png");
            }
        }
        World!.View.SetLighting(InspectionLighting.Daylight);
        for (var frame = 0; frame < 60; frame++)
        {
            await AdvanceTurnFrameAsync();
            PlaceCamera("follow", 0, farDistance);
            if (input.Train.Value == 0) await DisplayAsync(allowMissing: true, includeCamera: true);
            await SaveImageAsync($"{scene}_{(input.Train.Value != 0 ? "turn" : "motion")}_daylight_{frame:D4}.png");
        }
        if (input.Train.Value != 0) RequireTurn();
        else if (SubjectBounds() is { } bounds)
        {
            _subjectBounds = bounds;
            _subjectOrigin = World.Display!.PoseFrame!.Origin;
        }
        for (var frame = 0; frame < 60; frame++)
        {
            PlaceCamera("follow", Mathf.Tau * frame / 60, farDistance);
            if (input.Train.Value == 0) await DisplayAsync(allowMissing: true, includeCamera: true);
            await SaveImageAsync($"{scene}_orbit_daylight_{frame:D4}.png");
        }
        var commit = typeof(ArtCaptureEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[^1];
        File.WriteAllText(Path.Combine(_output!, "manifest.json"), JsonSerializer.Serialize(new
        {
            commit, inputSaveHash = _saveHash, scene, train = input.Train.Value == 0 ? (ulong?)null : input.Train.Value,
            lair = input.Lair?.Value, group = input.Group?.Value,
            motionSteps = input.MotionSteps,
            viewportSize = new[] { owner.GetViewport().GetTexture().GetWidth(), owner.GetViewport().GetTexture().GetHeight() },
            images = _images
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
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
        Host = CreateHost(_logger, OS.GetUserDataDir(), _dispatcher, new NotificationPolicy());
        var restored = Host.BeginRestore(document.CorePayload, document.ContentPayload, Host.SessionOptions!);
        if (restored.Problem is { } problem) throw new InvalidDataException($"恢复失败：{problem.Code}");
        await WaitAsync(() => !Host.IsBusy, () => { _dispatcher.Pump(); Host.ProcessFrame(0); });
        var status = Host.GetStatus() ?? throw new InvalidDataException("存档恢复失败。");
        if (Host.MissingConfirmation is not null) throw new InvalidDataException("存档依赖缺失。");
        _input = input;
        _subjects = input.Validate(status.View);
        Host.SetPaused(true);
        Host.AutoPauseOnFocusLoss = false;
        World = new(Host, CreateScenes(), _logger);
        World.ReturnedToMap += problem => _sceneFailure = problem?.Code ?? "场景已关闭";
        owner.AddChild(World.View);
        World.Open(input.Train.Value != 0
            ? new WorldSceneTarget.Follow(input.Train, ((InstanceId.Vehicle)((WorldObjectId.Instance)_subjects[0]).Id).Id)
            : new WorldSceneTarget.Location(input.Location));
        await DisplayAsync();
        if (input.Train.Value != 0) _initialFront = Front();
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

    internal Task DisplayAsync(bool allowMissing = false, bool includeCamera = false, SceneFrameStamp? after = null) => WaitAsync(
        () => World!.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready } frame &&
            (after is null || frame.Update.Frame != after) &&
            (allowMissing || _subjects.Any(subject => frame.FindPose(subject) is Lookup<ScenePose>.Found)) &&
            // 普通插值最多落后一步；旧 Ready 网格仍在显示时不能继续推进下一张生物截图。
            (_input!.Train.Value != 0 || frame.SimulationSeconds + 1e-9 >=
                Math.Max(0, (double)Host!.GetStatus()!.View.Position.CompletedSteps - 1) * SimulationTime.StepSeconds) &&
            (!includeCamera || _input!.Train.Value != 0 || CaptureAreaReady(frame)),
        () => World!.ProcessFrame(0, false));

    internal async Task PrepareSubjectCameraAsync(float farDistance)
    {
        PlaceCamera("follow", 0, farDistance);
        await DisplayAsync(includeCamera: true);
        _subjectBounds = null;
    }

    private bool CaptureAreaReady(ISceneFrame frame)
    {
        if (frame.Update.Environment.FoggedRegions.IsEmpty) return true;
        var area = SubjectBounds();
        var position = World!.View.CameraRig.Camera.GlobalPosition;
        area = area is { } bounds ? bounds.Expand(position) : new Aabb(position, Vector3.Zero);
        if (area is not { } box) return true;
        var origin = World.Display!.PoseFrame!.Origin;
        box.Position += new Vector3((float)(origin.X - frame.Origin.X),
            (float)(origin.Y - frame.Origin.Y), (float)(origin.Z - frame.Origin.Z));
        // Ready 允许地形仍在生成；只等待会遮住本次对象和相机之间区域的灰色生成遮罩。
        return !frame.Update.Environment.FoggedRegions.Any(fog =>
            box.Position.X < fog.Maximum.X && box.End.X > fog.Minimum.X &&
            box.Position.Z < fog.Maximum.Z && box.End.Z > fog.Minimum.Z);
    }

    internal async Task AdvanceTurnFrameAsync()
    {
        var steps = _input!.MotionSteps;
        var previousFrame = World!.Display!.DisplayedFrame!.Update.Frame;
        var step = Host!.Step(steps);
        if (step.Problem is not null || step.Completed != steps)
            throw new InvalidOperationException($"运动推进失败：{step.Problem?.Code ?? $"未完成 {steps} 个模拟步"}。");
        if (_input!.Group is { } group)
            _subjects = Host.GetStatus()!.View.FindCreatureGroup(group) is Lookup<CreatureGroupView>.Found found
                ? found.Value.Creatures.Select(creature => (WorldObjectId)new WorldObjectId.Creature(creature.Id)).ToArray() : [];
        await DisplayAsync(allowMissing: _input!.Train.Value == 0,
            after: _input.Train.Value != 0 ? null : previousFrame);
        if (_input.Train.Value != 0) _turned |= System.Numerics.Vector3.DistanceSquared(_initialFront, Front()) > 1e-10f;
    }

    internal void RequireTurn()
    {
        if (!_turned) throw new InvalidDataException("未过弯：60 帧中跟随车辆的朝向没有变化。");
    }

    private System.Numerics.Vector3 Front()
    {
        var pose = ((Lookup<ScenePose>.Found)World!.Display!.DisplayedFrame!.FindPose(_subjects[0])).Value;
        return System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, pose.Rotation);
    }

    internal void PlaceCamera(string distance, float orbit, float farDistance)
    {
        if (_input!.Train.Value == 0)
        {
            if (_subjectBounds is null)
            {
                _subjectBounds = SubjectBounds() ?? throw new InvalidDataException("检查对象没有可显示的网格。");
                _subjectOrigin = World!.Display!.PoseFrame!.Origin;
            }
            var subjectBox = _subjectBounds.Value;
            var origin = World!.Display!.PoseFrame!.Origin;
            subjectBox.Position += new Vector3((float)(_subjectOrigin.X - origin.X),
                (float)(_subjectOrigin.Y - origin.Y), (float)(_subjectOrigin.Z - origin.Z));
            var subjectPitch = distance == "near" ? 0.2f : 0.55f;
            var subjectYaw = 0.6f + orbit;
            var subjectRange = distance == "far" ? farDistance : WorldCamera.DefaultDistance;
            if (distance == "near") subjectRange = NearDistance(subjectBox.Size, new(
                Mathf.Sin(subjectYaw) * Mathf.Cos(subjectPitch), Mathf.Sin(subjectPitch), Mathf.Cos(subjectYaw) * Mathf.Cos(subjectPitch)));
            World!.View.CameraRig.PlaceAbsolute(subjectBox.GetCenter(), subjectRange, subjectYaw, subjectPitch);
            return;
        }
        var display = World!.Display!;
        var vehicle = display.Objects.Values.Single(item => item.Source.Subject == _subjects[0] && item.Source.Parent is null);
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
        var range = distance == "far" ? farDistance : WorldCamera.DefaultDistance;
        if (distance == "near")
        {
            var direction = vehicle.Root.GlobalBasis.Inverse() * new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch),
                Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            range = NearDistance(box.Size, direction);
        }
        World.View.CameraRig.PlaceAbsolute(focus, range, yaw, pitch);
    }

    private static float NearDistance(Vector3 size, Vector3 direction)
    {
        var half = size / 2;
        var low = 0f;
        var high = size.Length() + 6;
        // 相机距包围盒表面 6 格（约 3 米），而非距对象中心。
        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) / 2;
            var outside = (direction * middle).Abs() - half;
            outside = new(Mathf.Max(outside.X, 0), Mathf.Max(outside.Y, 0), Mathf.Max(outside.Z, 0));
            if (outside.LengthSquared() < 36) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    private Aabb? SubjectBounds()
    {
        var display = World!.Display!;
        if (_input!.Lair is { } lair)
        {
            if (Host!.GetStatus()!.View.FindLair(lair) is not Lookup<LairView>.Found found ||
                Host.Content!.FindLair(found.Value.Type) is not ContentLookup<LairAppearanceProjection>.Found appearance ||
                appearance.Value.SceneModel.Scene.OccupiedBounds is not { } bounds) return null;
            var root = display.Objects.Values.SingleOrDefault(item => item.Source.Subject == _subjects[0]);
            return root is null ? null : root.Root.GlobalTransform * new Aabb(
                new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
                new(bounds.Maximum.X - bounds.Minimum.X + 1, bounds.Maximum.Y - bounds.Minimum.Y + 1, bounds.Maximum.Z - bounds.Minimum.Z + 1));
        }
        Aabb? result = null;
        void Include(Node node)
        {
            if (node is MeshInstance3D { Mesh: not null } mesh)
            {
                var box = mesh.GlobalTransform * mesh.Mesh.GetAabb();
                result = result is { } previous ? previous.Merge(box) : box;
            }
            foreach (var child in node.GetChildren()) Include(child);
        }
        foreach (var item in display.Objects.Values.Where(item => item.Source.Parent is null && _subjects.Contains(item.Source.Subject)))
            Include(item.Root);
        return result;
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
        var display = World.Display!;
        _images.Add(new(name, Host!.GetStatus()!.View.Position, World.Display!.DisplayedFrame!.SimulationSeconds,
            World.View.Lighting.ToString().ToLowerInvariant(),
            Matrix(camera),
            [focus.X, focus.Y, focus.Z], _input!.Train.Value != 0 ? null :
                _subjects.Where(subject => World.Display.DisplayedFrame.FindPose(subject) is Lookup<ScenePose>.Found)
                    .Select(subject => subject.ToString()!).ToArray(),
            World.Display.DisplayedFrame.Update.Effects.Select(effect => effect.Kind.ToString()).Distinct().ToArray(),
            World.Display.Root.GetNodeOrNull("CombatEffects")?.GetChildren()
                .ToDictionary(child => child.Name.ToString(), child => child.GetChildCount()),
            _input.Train.Value != 0 ? null : display.DisplayedFrame.Update.Frame,
            _input.Train.Value != 0 ? null : display.PoseFrame?.SimulationSeconds,
            _input.Train.Value != 0 ? null : display.Objects.Values
                .Where(item => item.Source.Parent is null && _subjects.Contains(item.Source.Subject))
                .ToDictionary(item => item.Source.Subject!.ToString()!, item => Matrix(item.Root.GlobalTransform))));
    }

    private static float[] Matrix(Transform3D value) =>
        [value.Basis.X.X, value.Basis.X.Y, value.Basis.X.Z, value.Basis.Y.X, value.Basis.Y.Y, value.Basis.Y.Z,
            value.Basis.Z.X, value.Basis.Z.Y, value.Basis.Z.Z, value.Origin.X, value.Origin.Y, value.Origin.Z];

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
    string Lighting, float[] CameraTransform, float[] Focus, string[]? VisibleSubjects = null,
    string[]? EffectKinds = null, Dictionary<string, int>? VisibleEffects = null,
    SceneFrameStamp? DisplayFrame = null, double? PoseSeconds = null, Dictionary<string, float[]>? SubjectTransforms = null);

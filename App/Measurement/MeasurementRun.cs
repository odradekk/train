using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun : IDisposable
{
    private readonly App _app;
    private readonly string _scenario;
    private readonly string _input;
    private readonly string _output;
    private readonly Dictionary<string, object?> _identity;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly List<MemoryMeasurement> _memory = [];
    private readonly List<MeasuredOperation> _operations = [];
    private readonly List<SceneObservation> _scenes = [];
    private readonly double _created = Seconds();
    private FrameMeasurements? _samples;
    private MeasurementTiming? _timing;
    private double _sampleStarted;
    private double _lastDraw;
    private ulong _lastDrawSteps;
    private double _simulationWork;
    private double? _firstDisplay;
    private double? _readyDisplay;
    private ScenePoint? _origin;
    private SceneReadiness? _readiness;
    private WorldObjectId? _subject;
    private System.Numerics.Vector3? _position;
    private int _movingFrames;
    private Exception? _drawFailure;
    private bool _disposed;
    private TrainId? _measuredTrain;
    private readonly TrainId _train;
    private readonly NodeId _node;
    private int SampleSeconds => _sceneDuration > 0 ? _sceneDuration : 30;
    private static readonly Vector2I WindowSize = new(1920, 1080);
    private static readonly string[] SoftwareAdapters = ["llvmpipe", "lavapipe", "softpipe", "swiftshader"];

    internal MeasurementRun(App app, IReadOnlyList<string> arguments, StreamingRecorder? streaming = null)
    {
        _app = app;
        _streamingRecorder = streaming;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments.Where(value => value.StartsWith("--measure=", StringComparison.Ordinal)))
            values.Add("scenario", argument["--measure=".Length..].ToLowerInvariant());
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith("--measure=", StringComparison.Ordinal)) continue;
            if (arguments[i] is "--measure-streaming" or "--measure-node-ready-hold") { values.Add(arguments[i], "true"); continue; }
            if (arguments[i] is not ("--measure-input" or "--measure-output" or "--measure-identity" or "--measure-duration" or "--measure-reference" or
                    "--measure-train" or "--measure-node" or "--measure-route-camera") ||
                ++i >= arguments.Count) throw new ArgumentException("测量需要情景、输入存档、输出报告和构建身份路径。");
            values.Add(arguments[i - 1], arguments[i]);
        }
        _scenario = values["scenario"];
        if (_scenario is not ("map-1x" or "map-8x" or "follow-1x" or "follow-8x" or "node-1x" or "node-8x" or
            "sdfgi-off" or "sdfgi-on" or "taa-off" or "taa-on" or "scene-node" or "scene-node-first" or
            "scene-edge" or "scene-follow" or "scene-follow-8x" or "scene-memory" or "scene-soak" or "scene-capture" or "scene-map" or "scene-reference" or "scene-editor" or
            "scene-route-1x" or "scene-route-2x" or "scene-route-4x" or "scene-route-8x" or
            "scene-route-capture-1x" or "scene-route-capture-8x"))
            throw new ArgumentException("未知测量情景。");
        _streamingCamera = values.GetValueOrDefault("--measure-route-camera", "default");
        ValidateCameraOptions(_scenario, _streamingCamera, values.ContainsKey("--measure-route-camera"), _streamingRecorder is not null);
        _nodeReadyHold = values.ContainsKey("--measure-node-ready-hold");
        if (_nodeReadyHold && _scenario is not ("scene-node-first" or "scene-reference"))
            throw new ArgumentException("节点 Ready 保持仅支持 scene-node-first 和 scene-reference。");
        if (values.TryGetValue("--measure-duration", out var duration))
        {
            if (!int.TryParse(duration, out _sceneDuration) || _sceneDuration < 0 ||
                _scenario is not ("scene-memory" or "scene-soak") && _sceneDuration > 120)
                throw new ArgumentException("测量时长必须是非负整数秒；除 scene-memory 和 scene-soak 外只能缩短 120 秒采样。");
        }
        if ((values.ContainsKey("--measure-train") || values.ContainsKey("--measure-node")) &&
            !_scenario.StartsWith("scene-", StringComparison.Ordinal))
            throw new ArgumentException("--measure-train 和 --measure-node 只用于 scene-* 情景。");
        _train = new(TargetId(values, "--measure-train", 1));
        _node = new(TargetId(values, "--measure-node", 2));
        _input = Path.GetFullPath(values["--measure-input"]);
        _sceneReferencePath = values.TryGetValue("--measure-reference", out var reference) ? Path.GetFullPath(reference) : null;
        _output = Path.GetFullPath(values["--measure-output"]);
        if (File.Exists(_output)) throw new IOException("测量报告已存在。");
        _identity = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(values["--measure-identity"]))
            ?? throw new InvalidDataException("缺少构建身份。");
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        RenderingServer.FramePostDraw += OnDraw;
    }

    private static ulong TargetId(Dictionary<string, string> values, string name, ulong fallback)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        return ulong.TryParse(text, out var id) && id > 0 ? id : throw new ArgumentException(name + " 必须是正整数编号。");
    }

    internal void Start() => _ = Run();
    internal void OnSimulationFrame(double seconds) => _simulationWork += seconds;

    private async Task Run()
    {
        string? failure = null;
        try
        {
            if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("测量需要真实绘制窗口。");
            var adapter = RenderingServer.GetVideoAdapterName();
            if (RenderingServer.GetVideoAdapterType() == RenderingDevice.DeviceType.Cpu ||
                SoftwareAdapters.Any(name => adapter.Contains(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"测量需要硬件渲染，当前适配器为 {adapter}。");
            if (_scenario.StartsWith("scene-", StringComparison.Ordinal))
            {
                await RunSceneBudget();
            }
            else
            {
                await Timed("first-load", async () =>
                {
                    using var stream = File.OpenRead(_input);
                    var header = SaveContainer.ReadHeader(stream);
                    if (_app.Saves!.Load(new(_input, header, false)) is { } problem)
                        throw new InvalidDataException(problem.Code + ": " + problem.Message);
                    await Wait(() => !_app.Saves.HasPendingWork && !_app.Host!.IsBusy);
                    if (_app.Host!.GetStatus() is null) throw new InvalidDataException(_app.Saves.LastMessage);
                });
                var host = _app.Host!;
                var view = host.GetStatus()!.View;
                var train = view.World.Trains.OrderBy(item => item.Id.Value)
                    .Select(item => ((Lookup<TrainView>.Found)view.FindTrain(item.Id)).Value)
                    .FirstOrDefault(item => !item.Vehicles.IsEmpty)
                    ?? throw new InvalidDataException("存档中没有非空编组列车可供测量。");
                _measuredTrain = train.Id;
                _subject = new WorldObjectId.Instance(new InstanceId.Vehicle(train.Vehicles[0]));
                var submitted = host.Submit(host.GetStatus()!.Binding, new CoreCommand.StartTrain(train.Id), CommandSource.AutomationTest);
                if (submitted is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted })
                    throw new InvalidDataException("无法启动测量列车：" + submitted);
                host.AutoPauseOnFocusLoss = false;
                var speed = _scenario.EndsWith("-1x", StringComparison.Ordinal) ? SimulationSpeed.One : SimulationSpeed.Eight;
                if (host.SetSpeed(speed) is not null) throw new InvalidOperationException("无法设置测量倍速。");
                _app.WorldScenes!.View.SetMeasurementEffects(_scenario == "sdfgi-on", _scenario == "taa-on");

                if (!_scenario.StartsWith("map-", StringComparison.Ordinal))
                {
                    var requested = Seconds();
                    await SceneDisplayTime("scene-first-display", requested, async () =>
                    {
                        _app.WorldScenes.Open(_scenario.StartsWith("node-", StringComparison.Ordinal)
                            ? new WorldSceneTarget.Location(new WorldLocation.Node(view.World.Nodes[0].Id))
                            : new WorldSceneTarget.Follow(train.Id, train.Vehicles[0]));
                        await Wait(() => _firstDisplay is not null);
                        return _firstDisplay!.Value;
                    });
                    await SceneDisplayTime("scene-ready", requested, async () =>
                    {
                        await Wait(() => _readyDisplay is not null);
                        return _readyDisplay!.Value;
                    });
                }
                await Timed("environment-identity", () => { ReadIdentity(train.Id, speed); return Task.CompletedTask; });
                if (host.SetPaused(false) is not null) throw new InvalidOperationException("无法开始实时模拟。");
                var warming = Seconds();
                await Wait(() => Seconds() - warming >= 5 && _lastDraw > 0);
                _sampleStarted = _lastDraw;
                _samples = new(_lastDraw, _lastDrawSteps);
                _simulationWork = 0;
                await Wait(() => _timing is not null, SampleSeconds + 30);
                // Process 读取可能枚举系统进程；只在最后一个计时帧之后采集。
                _process.Refresh();
                var video = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
                _memory.Add(new(Seconds() - _sampleStarted, _process.WorkingSet64, _process.PrivateMemorySize64,
                    _process.PeakWorkingSet64,
                    OS.IsDebugBuild() ? Performance.GetMonitor(Performance.Monitor.MemoryStatic) : null,
                    video > 0 ? video : null));
                _identity["memory_scope"] = "one reading after frame sampling; working set and private bytes are current; peak working set covers process lifetime";
                _identity["scene_observations"] = _scenes;
                _identity["moving_sample_frames"] = _movingFrames;
                if ((_scenario.StartsWith("taa-", StringComparison.Ordinal) || _scenario.StartsWith("follow-", StringComparison.Ordinal)) && _movingFrames == 0)
                    throw new InvalidDataException("采样期间没有观察到列车移动。");
                if (_scenario.StartsWith("sdfgi-", StringComparison.Ordinal) && !_scenes.Any(scene => scene.Sampled && scene.OriginChanged))
                    throw new InvalidDataException("采样期间没有观察到浮动原点变化。");
                if (_scenario.StartsWith("sdfgi-", StringComparison.Ordinal) || _scenario.StartsWith("taa-", StringComparison.Ordinal))
                    await CaptureVisuals();
                host.SetPaused(true);
                await Timed("save", async () =>
                {
                    var target = Path.Combine(Path.GetDirectoryName(_output)!, "measured.save.json");
                    var position = host.GetStatus()!.View.Position;
                    if (_app.Saves!.Export(target) is { } problem) throw new IOException(problem.Code + ": " + problem.Message);
                    await Wait(() => !_app.Saves.HasPendingWork);
                    using var stream = File.OpenRead(target);
                    var header = SaveContainer.ReadHeader(stream);
                    if (header.Position != new SavePosition(position.CompletedSteps, position.Sequence))
                        throw new InvalidDataException("保存位置与捕获边界不同。");
                });
                var closed = false;
                _app.Recording!.Close(() => closed = true);
                await Wait(() => closed);
            }
        }
        catch (Exception error)
        {
            if (_disposed) return;
            failure = error.ToString();
            GD.PrintErr(failure);
        }
        try
        {
            File.WriteAllText(_output, MeasurementReport.Serialize(_scenario, _identity, _timing, _memory, _operations, failure));
            GD.Print($"MEASUREMENT_REPORT={_output} outcome={(failure is null ? "completed" : "failed")}");
        }
        catch (Exception error) { failure = error.ToString(); GD.PrintErr(failure); }
        _app.GetTree().Quit(failure is null ? 0 : 1);
    }

    private void ReadIdentity(TrainId train, SimulationSpeed speed)
    {
        var host = _app.Host!;
        var assembly = typeof(App).Assembly;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        if (_identity["assembly"] is not JsonElement supplied ||
            !string.Equals(supplied.GetProperty("sha256").GetString(), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("构建身份与当前加载的 Train.dll 不同。");
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var commit = build[(build.LastIndexOf('+') + 1)..];
        if (_identity["build_commit"] is not JsonElement expectedCommit ||
            !string.Equals(commit, expectedCommit.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("构建提交与当前加载程序集不同。");
        _identity["game_build"] = build;
        _identity["build_commit"] = commit;
        _identity["build_configuration"] = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        if ((string)_identity["build_configuration"]! != "ExportRelease")
            throw new InvalidDataException("测量需要 ExportRelease 构建。");
        _identity["runtime"] = host.Runtime;
        _identity["godot"] = Engine.GetVersionInfo()["string"].AsString();
        _identity["dotnet"] = RuntimeInformation.FrameworkDescription;
        _identity["os"] = RuntimeInformation.OSDescription;
        _identity["cpu"] = OS.GetProcessorName();
        _identity["cpu_count"] = System.Environment.ProcessorCount;
        _identity["gpu"] = RenderingServer.GetVideoAdapterName();
        _identity["gpu_vendor"] = RenderingServer.GetVideoAdapterVendor();
        _identity["gpu_api"] = RenderingServer.GetVideoAdapterApiVersion();
        _identity["gpu_driver"] = OS.GetVideoAdapterDriverInfo();
        _identity["gpu_type"] = RenderingServer.GetVideoAdapterType().ToString();
        _identity["display_server"] = DisplayServer.GetName();
        _identity["refresh_rate"] = DisplayServer.ScreenGetRefreshRate();
        _identity["renderer"] = RenderingServer.GetCurrentRenderingMethod();
        _identity["rendering_driver"] = RenderingServer.GetCurrentRenderingDriverName();
        _identity["window_size"] = new[] { _app.GetWindow().Size.X, _app.GetWindow().Size.Y };
        var size = _app.GetViewport().GetTexture().GetSize();
        _identity["viewport_size"] = new[] { size.X, size.Y };
        _identity["vsync"] = DisplayServer.WindowGetVsyncMode().ToString();
        _identity["maximum_fps"] = Engine.MaxFps;
        _identity["msaa"] = _app.GetViewport().Msaa3D.ToString();
        _identity["sdfgi_enabled"] = _app.WorldScenes!.View.SdfgiEnabled;
        _identity["taa_enabled"] = _app.GetViewport().UseTaa;
        _identity["world_seed"] = host.GetStatus()!.View.World.WorldSeed;
        _identity["core_input_id"] = host.GetStatus()!.View.InputIdentity;
        _identity["input_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_input)));
        _identity["train"] = train.Value;
        _identity["requested_speed"] = (int)speed;
        _identity["recording_enabled"] = _app.Recording is not null;
        _identity["retained_recording_starts"] = host.SessionOptions!.RetainedRecordingStarts;
        _identity["auto_save_seconds"] = _app.Saves!.AutoSaveInterval?.TotalSeconds;
        _identity["warmup_seconds"] = 5;
        _identity["sample_seconds"] = SampleSeconds;
        _identity["speed_window_method"] = "连续目标秒后的首个绘制帧作为窗口边界；窗口共享边界帧，不插值；末尾不足目标秒部分仅计入整段";
        _identity["percentile_method"] = "nearest-rank";
        _identity["simulation_work_scope"] = "frame-driver advancement, including boundary consumers; excludes candidate preparation and render submission";
        _identity["cache_scope"] = OperatingSystem.IsLinux()
            ? "fresh process and user data; the isolated XDG_CACHE_HOME gives each run an empty Mesa shader cache; OS file cache is not cleared"
            : "fresh process and user data; OS file and driver shader caches are not cleared";
    }

    private void OnDraw()
    {
        if (_disposed || _app.Host!.GetStatus() is not { } status) return;
        var window = _app.GetWindow().Size;
        var viewport = _app.GetViewport().GetTexture().GetSize();
        if (window != WindowSize || viewport != (Vector2)WindowSize)
            _drawFailure ??= new InvalidOperationException($"测量窗口与视口必须保持 1920×1080，实际为 {window}／{viewport}。");
        if (_scenario.StartsWith("scene-", StringComparison.Ordinal))
        {
            RecordSceneResourceFrame();
            OnSceneDraw();
            return;
        }
        try
        {
            var now = Seconds();
            if (status.IsFrozen) throw new InvalidOperationException("测量期间核心冻结。");
            if (_app.GetWindow().Mode == Window.ModeEnum.Minimized) throw new InvalidOperationException("测量窗口被最小化。");
            if (_app.WorldScenes!.Display?.DisplayedFrame is { } frame)
            {
                _firstDisplay ??= now;
                if (frame.Readiness == SceneReadiness.Ready) _readyDisplay ??= now;
                var shifted = _origin is not null && _origin != frame.Origin;
                if (_origin != frame.Origin || _readiness != frame.Readiness)
                    _scenes.Add(new(now - _created, status.View.Position, frame.Origin, frame.Readiness, shifted, _samples is not null));
                if (frame.FindPose(_subject!) is Lookup<ScenePose>.Found pose)
                {
                    if (_samples is not null && (_position != pose.Value.Position || shifted)) _movingFrames++;
                    _position = pose.Value.Position;
                }
                _origin = frame.Origin;
                _readiness = frame.Readiness;
            }
            if (_samples is not null)
            {
                FollowFrame? follow = null;
                if (_scenario.StartsWith("follow-", StringComparison.Ordinal) && _measuredTrain is { } trainId)
                {
                    TrainPosition? position = null;
                    foreach (var train in status.View.World.Trains)
                        if (train.Id == trainId) { position = train.Position; break; }
                    var (kind, id) = position switch
                    {
                        TrainPosition.Edge edge => ("Edge", edge.Id.Value),
                        TrainPosition.Node node => ("Node", node.Id.Value),
                        _ => throw new InvalidOperationException("未知列车位置。")
                    };
                    follow = new(trainId.Value, kind, id, _app.WorldScenes!.Display?.DisplayedFrame?.Update.Frame.Space.Value);
                }
                _samples.AddFrame(now, status.View.Position.CompletedSteps, _simulationWork, status.Speed, status.IsPaused, follow);
                if (now - _sampleStarted >= SampleSeconds) { _timing = _samples.Summarize(includeSpeedWindows: true); _samples = null; }
            }
            _lastDraw = now;
            _lastDrawSteps = status.View.Position.CompletedSteps;
            _simulationWork = 0;
        }
        catch (Exception error) { _drawFailure = error; }
    }

    private async Task CaptureVisuals()
    {
        if (_scenario.StartsWith("sdfgi-", StringComparison.Ordinal))
        {
            var previous = _origin;
            await Wait(() => _origin != previous);
        }
        var images = new List<string>();
        var directory = Path.GetDirectoryName(_output)!;
        for (var i = 0; i < 12; i++)
        {
            await _app.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var name = $"visual-{i:D2}.png";
            using var image = _app.GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.Combine(directory, name)) != Error.Ok) throw new IOException("不能保存实际窗口截图。");
            images.Add(name);
        }
        _identity["visual_frames"] = images;
        _identity["visual_scope"] = "consecutive drawn frames after sampling; screenshot readback and PNG encoding excluded from timing";
    }

    private async Task Timed(string name, Func<Task> operation)
    {
        var start = Seconds();
        try { await operation(); _operations.Add(new(name, start - _created, Seconds() - start, true)); }
        catch (Exception error)
        { _operations.Add(new(name, start - _created, Seconds() - start, false, error.Message)); throw; }
    }

    private async Task SceneDisplayTime(string name, double requested, Func<Task<double>> display)
    {
        try { _operations.Add(new(name, requested - _created, await display() - requested, true)); }
        catch (Exception error)
        { _operations.Add(new(name, requested - _created, Seconds() - requested, false, error.Message)); throw; }
    }

    private async Task Wait(Func<bool> ready, double timeoutSeconds = 120)
    {
        var started = Seconds();
        while (!ready())
        {
            if (_disposed) throw new OperationCanceledException();
            if (_drawFailure is not null) throw _drawFailure;
            if (_app.Host!.MissingConfirmation is not null) throw new InvalidDataException("测量存档依赖缺失。");
            if (Seconds() - started > timeoutSeconds) throw new TimeoutException($"测量阶段等待超过 {timeoutSeconds} 秒。");
            await _app.ToSignal(_app.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (_drawFailure is not null) throw _drawFailure;
    }

    private static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public void Dispose()
    {
        _disposed = true;
        StopEditorMeasurement();
        RenderingServer.FramePostDraw -= OnDraw;
        _process.Dispose();
    }

    private sealed record SceneObservation(double ElapsedSeconds, SimulationPosition Position, ScenePoint Origin,
        SceneReadiness Readiness, bool OriginChanged, bool Sampled);
}

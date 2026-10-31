using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Train.App.Notifications;
using Train.Core.Contracts;

namespace Train.App.Measurement;

/// <summary>游戏内测量只保留 scene-* 场景。
/// Decision note: 2026-10-26-performance-measurement-system
/// </summary>
internal sealed partial class MeasurementRun : IDisposable
{
    private readonly App _app;
    private readonly string _scenario;
    private readonly string _input;
    private readonly string _output;
    private readonly Dictionary<string, object?> _identity;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly ArrayPoolDiagnosis? _arrayPoolDiagnosis;
    private MeasurementTiming? _timing;
    private double _simulationWork;
    private WorldObjectId? _subject;
    private Exception? _drawFailure;
    private bool _disposed;
    private readonly TrainId _train;
    private readonly NodeId _node;
    private int SampleSeconds => _sceneDuration > 0 ? _sceneDuration : 30;
    private static readonly Vector2I WindowSize = new(1920, 1080);
    private static readonly string[] SoftwareAdapters = ["llvmpipe", "lavapipe", "softpipe", "swiftshader"];
    private static readonly JsonSerializerOptions SceneJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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
        if (_scenario is not ("scene-node" or "scene-node-first" or
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
        _train = new(TargetId(values, "--measure-train", 1));
        _node = new(TargetId(values, "--measure-node", 2));
        _input = Path.GetFullPath(values["--measure-input"]);
        _sceneReferencePath = values.TryGetValue("--measure-reference", out var reference) ? Path.GetFullPath(reference) : null;
        _output = Path.GetFullPath(values["--measure-output"]);
        if (File.Exists(_output)) throw new IOException("测量报告已存在。");
        _identity = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(values["--measure-identity"]))
            ?? throw new InvalidDataException("缺少构建身份。");
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        _arrayPoolDiagnosis = ArrayPoolDiagnosis.StartIfEnabled();
        RenderingServer.FramePostDraw += OnDraw;
    }

    private static ulong TargetId(Dictionary<string, string> values, string name, ulong fallback)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        return ulong.TryParse(text, out var id) && id > 0 ? id : throw new ArgumentException(name + " 必须是正整数编号。");
    }

    internal void Start() => _ = Run();
    internal void OnSimulationFrame(double seconds) => _simulationWork += seconds;

    internal static Dictionary<string, string> ConfigureEncounterAction(string scenario, NotificationPolicy policy)
    {
        var disabled = scenario is "scene-route-8x" or "scene-follow-8x" or "scene-soak";
        if (disabled) policy.SetAction(NotificationCategory.Encounter, NotificationAction.None);
        var action = new Dictionary<string, string>
        {
            ["value"] = policy.Action(NotificationCategory.Encounter).ToString(),
            ["default_value"] = NotificationPolicy.DefaultAction(NotificationCategory.Encounter).ToString()
        };
        if (disabled)
            action["reason"] = "设计 00 允许性能测试关闭遇敌自动降速并记录；否则新战斗会把 8× 采样降到 1×";
        return action;
    }

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
            await RunSceneBudget();
        }
        catch (Exception error)
        {
            if (_disposed) return;
            failure = error.ToString();
            GD.PrintErr(failure);
        }
        try
        {
            var evidence = failure is null && (MeasurementEvidence.UsesSidecars(_scenario) || MeasurementEvidence.UsesPreparation(_scenario))
                ? MeasurementEvidence.Read(_scenario, Path.GetDirectoryName(_output)!) : null;
            File.WriteAllText(_output, MeasurementReport.Serialize(_scenario, _identity, _timing, failure, evidence));
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
        _identity["percentile_method"] = "nearest-rank";
        _identity["simulation_work_scope"] = "frame-driver advancement, including boundary consumers; excludes candidate preparation and render submission";
        _identity["cache_scope"] = OperatingSystem.IsLinux()
            ? "fresh process and user data; the isolated XDG_CACHE_HOME gives each run an empty Mesa shader cache; OS file cache is not cleared"
            : "fresh process and user data; OS file and driver shader caches are not cleared";
    }

    private void OnDraw()
    {
        if (_disposed || _app.Host!.GetStatus() is null) return;
        ObserveSceneResources();
        var window = _app.GetWindow().Size;
        var viewport = _app.GetViewport().GetTexture().GetSize();
        if (window != WindowSize || viewport != (Vector2)WindowSize)
            _drawFailure ??= new InvalidOperationException($"测量窗口与视口必须保持 1920×1080，实际为 {window}／{viewport}。");
        RecordSceneResourceFrame();
        OnSceneDraw();
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
        _arrayPoolDiagnosis?.Dispose();
        StopEditorMeasurement();
        ObserveSceneResources(null);
        RenderingServer.FramePostDraw -= OnDraw;
        _process.Dispose();
    }
}

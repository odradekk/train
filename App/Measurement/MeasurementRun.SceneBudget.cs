using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Godot;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private int _sceneDuration;
    private string _scenePhase = "startup";
    private string? _sceneOperation;
    private double _sceneClick, _sceneVisible, _sceneReady, _sceneOperable;
    private double _sceneLastDraw, _sceneLastProcessRead;
    private long _scenePrivate, _sceneWorking, _scenePeakPrivate, _scenePeakWorking;
    private bool _sceneNearAvailable, _sceneDragIssued;
    private Vector2 _sceneBeforeDrag;
    private double _sceneDragAt;
    private long _sceneDrawCount, _sceneDragDraw;
    private object? _sceneProbeDetail, _sceneFirstNearDetail;
    private ProbeState? _sceneLoggedProbeState;
    private readonly List<object> _sceneProbeEvents = [];
    private readonly List<SceneProbeCost> _sceneProbeCosts = [];
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool,
            QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
    private sealed record SceneProbeCost(string? Operation, long Draw, double Started, double Ended,
        double FrameFogSeconds, double PointSeconds, double BusinessSeconds, double LoggingSeconds, double OtherSeconds,
        int FogRegionCount);
    private readonly List<double> _sceneOpeningFrames = [];
    private readonly Queue<SceneSample> _sceneRecent = new();
    private readonly Dictionary<string, object?> _sceneEvidence = [];
    private StreamWriter? _sceneTrace, _sceneOperationTrace, _sceneCollectionTrace, _sceneCostTrace;
    private FrameMeasurements? _sceneTiming;
    private double _sceneLoadButton, _sceneLoadCompleted;
    private readonly HashSet<string> _sceneOpenedTargets = new(StringComparer.Ordinal);
    private int _sceneCompletedOperations;
    private double _sceneLastLoadSample;
    private bool _sceneCapturedPartial;
    private object? _scenePresented;
    private SceneFrameStamp? _scenePresentedStamp;
    private static readonly JsonSerializerOptions SceneJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record SceneSample(double At, string Phase, double FrameSeconds, ulong Steps,
        string Speed, bool Paused, string? Request, string? Space, string? Readiness,
        bool? NearAvailable, bool DragIssued, float OrbitYawRadians, float OrbitPitchRadians, bool LoadingLabel, bool Overlay,
        string[] BlockingControls, bool ModalWindow, int Picks,
        ProbeState? Probe, object? Presented, long Draw, long PrivateBytes, long WorkingSetBytes,
        long PeakPrivateBytes, long PeakWorkingSetBytes, ulong BufferBytes, ulong TextureBytes, ulong VideoBytes);

    private async Task RunSceneBudget()
    {
        var directory = Path.GetDirectoryName(_output)!;
        using var frames = new StreamWriter(Path.Combine(directory, "scene-frames.jsonl"));
        using var operations = new StreamWriter(Path.Combine(directory, "scene-operations.jsonl"));
        using var collections = new StreamWriter(Path.Combine(directory, "scene-collections.jsonl"));
        using var costs = new StreamWriter(Path.Combine(directory, "scene-probe-costs.jsonl"));
        _sceneTrace = frames; _sceneOperationTrace = operations; _sceneCollectionTrace = collections;
        _sceneCostTrace = costs;
        _app.Saves!.Changed += OnSceneSaveChanged;
        _app.WorldScenes!.FramePresented += OnScenePresented;
        _identity["scene_budget"] = _sceneEvidence;
        _sceneEvidence["frames"] = "scene-frames.jsonl";
        _sceneEvidence["operations"] = "scene-operations.jsonl";
        _sceneEvidence["collections"] = "scene-collections.jsonl";
        _sceneEvidence["probe_costs"] = "scene-probe-costs.jsonl";
        _sceneEvidence["clock"] = "Stopwatch.GetTimestamp / Stopwatch.Frequency; absolute monotonic seconds";
        _sceneEvidence["scope"] = "current implemented stage load; not the complete standard mixed load";
        _sceneEvidence["capture_only"] = _scenario == "scene-capture";
        _sceneEvidence["reference_only"] = _scenario == "scene-reference";
        _sceneEvidence["unobservable_cache_state"] = new[] { "prebuild_tasks", "node_cache_entries" };
        LoadSceneReference();
        try
        {
            var host = _app.Host!;
            host.AutoPauseOnFocusLoss = false;
            await LoadSceneInput();
            _sceneEvidence["initial_load"] = new { button = _sceneLoadButton, completed = _sceneLoadCompleted, map_drawn = _sceneLastDraw };
            _sceneEvidence["initial_load_counts"] = SceneLoadCounts();
            // First clicks happen before the steady-map hold. They never wait for a prebuild.
            if (IsRouteMeasurement)
            {
                await RunFollowRoute();
            }
            else if (_scenario == "scene-reference")
            {
                await OpenSceneMeasured("reference-node", new WorldObjectId.Node(_node));
                await ReturnSceneMap();
                await LoadSceneInput();
                await OpenSceneMeasured("reference-edge", new WorldObjectId.Edge(new(11)));
                await ReturnSceneMap();
                await LoadSceneInput();
                await OpenSceneMeasured("reference-follow", new WorldObjectId.Train(_train));
                WriteSceneReference();
            }
            else if (_scenario is "scene-node" or "scene-node-first" or "scene-capture")
            {
                await OpenSceneMeasured("map-node", new WorldObjectId.Node(_node));
                if (_scenario == "scene-node")
                {
                    await ReturnSceneMap();
                    await OpenSceneMeasured("node-reopen", new WorldObjectId.Node(_node));
                    await OpenSceneMeasured("node-to-edge", new WorldObjectId.Edge(new(11)));
                    await OpenSceneMeasured("edge-to-node", new WorldObjectId.Node(_node));
                    await ReturnSceneMap();
                    await LoadSceneInput();
                    await OpenSceneMeasured("reload-node", new WorldObjectId.Node(_node), _sceneLoadButton);
                }
            }
            else if (_scenario == "scene-edge")
            {
                await OpenSceneMeasured("map-edge", new WorldObjectId.Edge(new(11)));
                await ReturnSceneMap();
                await OpenSceneMeasured("edge-reopen", new WorldObjectId.Edge(new(11)));
                await ReturnSceneMap();
                await LoadSceneInput();
                await OpenSceneMeasured("reload-edge", new WorldObjectId.Edge(new(11)), _sceneLoadButton);
            }
            else if (_scenario is "scene-follow" or "scene-follow-8x")
            {
                await OpenSceneMeasured("map-follow", new WorldObjectId.Train(_train));
                if (_scenario == "scene-follow-8x")
                {
                    host.SetSpeed(SimulationSpeed.Eight);
                    host.SetPaused(false);
                    _scenePhase = "follow-8x-warmup";
                    var warming = Seconds();
                    await Wait(() => Seconds() - warming >= 5);
                    _scenePhase = "follow-8x-sample";
                    _sceneTiming = new(_sceneLastDraw, host.GetStatus()!.View.Position.CompletedSteps);
                    _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "sample-load", edge = "start", load = SceneLoadCounts() }, SceneJson));
                    var started = Seconds();
                    await Wait(() => Seconds() - started >= (_sceneDuration > 0 ? _sceneDuration : 120));
                    _timing = _sceneTiming.Summarize();
                    _sceneTiming = null;
                    _sceneOperationTrace.WriteLine(JsonSerializer.Serialize(new { kind = "sample-load", edge = "end", load = SceneLoadCounts() }, SceneJson));
                }
            }
            else if (_scenario == "scene-editor")
            {
                await RunEditorMeasurement();
            }
            else if (_scenario == "scene-memory")
            {
                await HoldSceneMap("initial-map");
                CollectSceneHeap(0, "initial-map");
                var started = Seconds();
                var cycle = 0;
                do
                {
                    await OpenSceneMeasured("memory-node", new WorldObjectId.Node(_node));
                    await ReturnSceneMap();
                    await HoldSceneMap("retired-map");
                    cycle++;
                    _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new
                    { kind = "natural-map", cycle, at = Seconds(), memory = SceneSteady(), load = SceneLoadCounts() }, SceneJson));
                    if (cycle % 10 == 0) CollectSceneHeap(cycle, "retired-map");
                } while (Seconds() - started < (_sceneDuration > 0 ? _sceneDuration : 7200));
                CollectSceneHeap(cycle, "final-map");
                _sceneEvidence["cycles"] = cycle;
                _sceneEvidence["memory_loop_seconds"] = Seconds() - started;
            }
            ReadIdentity(_train, IsRouteMeasurement ? RouteSpeed :
                _scenario == "scene-follow-8x" ? SimulationSpeed.Eight : SimulationSpeed.One);
            _identity["node"] = _node.Value;
            _identity["warmup_seconds"] = _scenario == "scene-follow-8x" ? 5 : 0;
            _identity["sample_seconds"] = IsRouteMeasurement ? _timing!.ElapsedSeconds :
                _scenario == "scene-follow-8x" ? (_sceneDuration > 0 ? _sceneDuration : 120) :
                _scenario == "scene-editor" ? SampleSeconds : 0;
            if (_app.WorldScenes!.View.Visible) await ReturnSceneMap();
            await HoldSceneMap("final-map");
            _sceneEvidence["final_map"] = SceneSteady();
            _sceneEvidence["final_load_counts"] = SceneLoadCounts();
            _sceneEvidence["completed_operations"] = _sceneCompletedOperations;
            frames.Flush(); operations.Flush(); collections.Flush();
        }
        finally
        {
            FlushSceneProbeEvents();
            FlushSceneProbeCosts();
            _app.Saves!.Changed -= OnSceneSaveChanged;
            _app.WorldScenes!.FramePresented -= OnScenePresented;
            _sceneTrace = _sceneOperationTrace = _sceneCollectionTrace = _sceneCostTrace = null;
            ResetSceneProbe();
        }
    }

    private async Task LoadSceneInput()
    {
        _scenePhase = "load";
        var menu = _app.GetNode<MainMenu>("MainMenu");
        using var input = File.OpenRead(_input);
        var header = SaveContainer.ReadHeader(input);
        var store = new FileSaveStore(OS.GetUserDataDir());
        Directory.CreateDirectory(store.DirectoryFor(header.Id));
        var copy = store.PathFor(header.Id);
        if (!File.Exists(copy)) File.Copy(_input, copy);
        menu.OpenSaves();
        var panel = menu.GetChildren().OfType<SavePanel>().Single();
        var list = SceneUiDescendants(panel).OfType<ItemList>().Single();
        var entries = panel.Entries;
        var index = entries.Select((entry, index) => (entry, index)).Single(pair => pair.entry.Path == copy).index;
        list.Select(index);
        var button = SceneUiDescendants(panel).OfType<Button>().Single(item => item.Text == "读取选中存档");
        _sceneLoadButton = Seconds();
        button.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() =>
        {
            if (SceneUiDescendants(panel).OfType<ConfirmationDialog>()
                    .FirstOrDefault(dialog => dialog.Title == "未保存的更改") is { Visible: true } leave &&
                _app.Saves!.Leaving is { Waiting: false })
                leave.EmitSignal(AcceptDialog.SignalName.CustomAction, "discard");
            return !_app.Saves!.IsBusy && _app.Host!.GetStatus() is not null;
        });
        _sceneLoadCompleted = Seconds();
        panel.EmitSignal(Window.SignalName.CloseRequested);
        if (_app.Host!.SetSpeed(SimulationSpeed.One) is not null || _app.Host.SetPaused(false) is not null)
            throw new InvalidOperationException("不能启动测量负载。");
        var previousDraw = _sceneDrawCount;
        await Wait(() => _sceneDrawCount > previousDraw &&
            _app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").IsVisibleInTree() && !_app.WorldScenes!.View.Visible &&
            !panel.Visible && !_app.Saves!.IsBusy);
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "load-map", button = _sceneLoadButton,
            completed = _sceneLoadCompleted, map_drawn = _sceneLastDraw }, SceneJson));
        if (_app.Host!.GetStatus()!.IsReplay) throw new InvalidDataException("测量输入不能是重放。");
    }

    private async Task OpenSceneMeasured(string name, WorldObjectId target, double? loadButton = null)
    {
        ResetSceneProbe();
        _scenePhase = name;
        _sceneOperation = name;
        _sceneVisible = _sceneReady = _sceneOperable = 0;
        _sceneNearAvailable = _sceneDragIssued = false;
        _sceneCapturedPartial = false;
        _sceneProbeDetail = _sceneFirstNearDetail = null;
        _sceneLoggedProbeState = null;
        _sceneProbeEvents.Clear();
        _sceneOpeningFrames.Clear();
        var before = SceneCacheState();
        var loadAtClick = SceneLoadCounts();
        _sceneTarget = target;
        var peakBefore = new { private_bytes = _scenePeakPrivate, working_set_bytes = _scenePeakWorking };
        if (target is WorldObjectId.Node node && _app.Host!.GetStatus()!.View.FindNode(node.Id) is not Lookup<NodeView>.Found)
            throw new InvalidDataException($"测量存档中没有节点 {node.Id.Value}。");
        // 与 GameUi.OpenScene 相同，跟随列车的首车。
        var follow = target is WorldObjectId.Train ? new WorldObjectId.Instance(new InstanceId.Vehicle(MeasuredTrain().Vehicles[0])) : null;
        _subject = follow;
        _sceneClick = Seconds();
        if (_scenario == "scene-reference") _app.Host!.SetPaused(true);
        _app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").RaiseObjectClicked(target);
        _sceneOpenedTargets.Add(target.ToString()!);
        var referenceResumed = false;
        await Wait(() =>
        {
            if (_scenario == "scene-reference" && _sceneReady > 0 && !referenceResumed)
            {
                _app.Host!.SetPaused(false);
                referenceResumed = true;
            }
            if (_sceneNearAvailable && !_sceneDragIssued)
            {
                var camera = _app.WorldScenes!.View.CameraRig;
                _sceneBeforeDrag = new(camera.OrbitYawRadians, camera.OrbitPitchRadians);
                _sceneDragAt = Seconds();
                _sceneDragDraw = _sceneDrawCount;
                var point = _app.GetViewport().GetVisibleRect().Size / 2;
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = point });
                Input.ParseInputEvent(new InputEventMouseMotion { Position = point + new Vector2(8, 0), Relative = new(8, 0), ButtonMask = MouseButtonMask.Right });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = point + new Vector2(8, 0) });
                _sceneDragIssued = true;
            }
            // The observation window ends after the same Ready hold used for memory.
            // Missing interaction remains a null observation, never a successful signal.
            return _sceneReady > 0 && _sceneLastDraw >= _sceneReady + (_scenario == "scene-reference" ? 10 : 5);
        });
        var end = Seconds();
        if (_app.WorldScenes!.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready } readyFrame)
            RecordReadyGeometry(readyFrame, _sceneLastDraw);
        _sceneOperation = null;
        // Validate the immutable Ready snapshot after the observation, without charging far-content evaluation to UI latency.
        var validationStarted = Seconds();
        if (_scenario != "scene-reference") ValidateReadyReference();
        var validationSeconds = Seconds() - validationStarted;
        FlushSceneProbeEvents();
        _sceneCompletedOperations++;
        var record = new
        {
            kind = "open", name, target = target.ToString(), click = _sceneClick,
            first_visible = _sceneVisible > 0 ? (double?)_sceneVisible : null,
            first_operable = _sceneOperable > 0 ? (double?)_sceneOperable : null,
            ready = _sceneReady, observation_ended = end,
            click_to_operable_seconds = _sceneOperable > 0 ? (double?)(_sceneOperable - _sceneClick) : null,
            operable_to_ready_seconds = _sceneOperable > 0 ? (double?)Math.Max(0, _sceneReady - _sceneOperable) : null,
            load_button = loadButton,
            load_button_to_operable_seconds = loadButton is { } start && _sceneOperable > 0 ? (double?)(_sceneOperable - start) : null,
            opening_frames = _sceneOpeningFrames.Count > 0 ? FrameMeasurements.Distribution(_sceneOpeningFrames) : null,
            probe_cost_seconds = new
            {
                click_to_operable = _sceneOperable > 0 ? ProbeCosts(_sceneClick, _sceneOperable) : null,
                operable_to_ready = _sceneOperable > 0 ? ProbeCosts(_sceneOperable, _sceneReady) : null
            },
            first_near = _sceneFirstNearDetail, last_probe = _sceneProbeDetail, cache_at_click = before, load_at_click = loadAtClick,
            drag = new { issued_at = _sceneDragIssued ? (double?)_sceneDragAt : null,
                issued_draw = _sceneDragIssued ? (long?)_sceneDragDraw : null,
                before = new { yaw_radians = _sceneBeforeDrag.X, pitch_radians = _sceneBeforeDrag.Y },
                after = new { yaw_radians = _app.WorldScenes.View.CameraRig.OrbitYawRadians,
                    pitch_radians = _app.WorldScenes.View.CameraRig.OrbitPitchRadians } },
            peak_watermark_before = peakBefore, steady = SceneSteady(), load = SceneLoadCounts(),
            reference_validation = _probeReferenceValidation,
            reference_validation_seconds = validationSeconds,
            reference_snapshot = _probeReadySnapshot is { } snapshot ? new { at = snapshot.At,
                frame = snapshot.Frame.ToString(), position = snapshot.Position } : null,
            sample_valid = _probeReferenceValidation?.Valid == true,
            partial_capture_observed = _sceneCapturedPartial
        };
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(record, SceneJson));
        _sceneOperationTrace.Flush();
        FlushSceneProbeCosts();
        if (_scenario == "scene-capture") await CaptureSceneImage(name + "-ready");
        ResetSceneProbe();
    }

    private TrainView MeasuredTrain() =>
        _app.Host!.GetStatus()!.View.FindTrain(_train) is Lookup<TrainView>.Found { Value: { Vehicles.IsEmpty: false } train }
            ? train : throw new InvalidDataException($"测量存档中没有非空编组的列车 {_train.Value}。");

    private void FlushSceneProbeEvents()
    {
        foreach (var state in _sceneProbeEvents) _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(state, SceneJson));
        _sceneProbeEvents.Clear();
    }

    private void FlushSceneProbeCosts()
    {
        foreach (var cost in _sceneProbeCosts) _sceneCostTrace!.WriteLine(JsonSerializer.Serialize(cost, SceneJson));
        _sceneCostTrace?.Flush();
        _sceneProbeCosts.Clear();
    }

    private object ProbeCosts(double start, double end)
    {
        SceneProbeCost[] costs = end <= start ? [] :
            _sceneProbeCosts.Where(cost => cost.Started < end && cost.Ended > start).ToArray();
        object? Distribution(Func<SceneProbeCost, double> value) => costs.Length == 0 ? null : FrameMeasurements.Distribution(costs.Select(value));
        return new { frames = costs.Length,
            frame_fog = Distribution(cost => cost.FrameFogSeconds), points = Distribution(cost => cost.PointSeconds),
            business = Distribution(cost => cost.BusinessSeconds), logging = Distribution(cost => cost.LoggingSeconds),
            other = Distribution(cost => cost.OtherSeconds), total = Distribution(cost => cost.Ended - cost.Started),
            contribution_upper_bound_seconds = FrameMeasurements.SumOverlap(_sceneProbeCosts.Select(cost => (cost.Started, cost.Ended)), start, end) };
    }

    private async Task ReturnSceneMap()
    {
        _scenePhase = "return-map";
        var before = _sceneDrawCount;
        _app.GetNode<Button>("GameUi/Root/World/Bar/TopBar/ReturnToMap").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() => !_app.WorldScenes!.View.Visible && _sceneDrawCount > before);
    }

    private async Task HoldSceneMap(string phase)
    {
        _scenePhase = phase;
        var started = _sceneLastDraw;
        if (_timing is null) _sceneTiming = new(started, _app.Host!.GetStatus()!.View.Position.CompletedSteps);
        await Wait(() => _sceneLastDraw >= started + 5);
        if (_sceneTiming is not null)
        { _timing = _sceneTiming.Summarize(); _sceneTiming = null; }
    }

    private void OnSceneDraw()
    {
        if (IsRouteMeasurement && _routeSampling) { OnRouteDraw(); return; }
        var observerStarted = Seconds();
        var operation = _sceneOperation;
        var loggingSeconds = 0d;
        _probeFogSeconds = _probePointSeconds = _probeBusinessSeconds = 0;
        _probeFogRegionCount = 0;
        try
        {
            var host = _app.Host!;
            if (host.GetStatus() is not { } status || _sceneTrace is null) return;
            if (status.IsFrozen) throw new InvalidOperationException("测量期间核心冻结。");
            if (_app.GetWindow().Mode == Window.ModeEnum.Minimized) throw new InvalidOperationException("测量窗口被最小化。");
            var now = Seconds();
            var interval = _sceneLastDraw == 0 ? 0 : now - _sceneLastDraw;
            _sceneDrawCount++;
            var frameLookupStarted = Seconds();
            var frame = _app.WorldScenes!.View.Visible ? _app.WorldScenes.Display?.DisplayedFrame : null;
            if (frame is not null && frame.Update.Frame.Request !=
                _app.WorldScenes.CurrentRequest) frame = null;
            var interaction = _app.WorldScenes.InteractionFrame;
            if (interaction is not null && interaction.Update.Frame.Request != _app.WorldScenes.CurrentRequest) interaction = null;
            _probeFogSeconds += Seconds() - frameLookupStarted;
            var checkedNear = false;
            var picksBefore = _scenePickCount;
            _sceneNearAvailable = false;
            _probeState = new(_sceneOperable > 0 ? "not_checked_operable" : "no_frame", null, 0, 0, 0, 0, 0, 0, false);
            if (_sceneOperation is not null && (_sceneReady == 0 || _sceneOperable == 0 || now <= Math.Max(_sceneReady, _sceneOperable)))
                _sceneOpeningFrames.Add(interval);
            if (frame is not null && _sceneOperation is not null)
            {
                if (_sceneVisible == 0 && frame.Update.Upserts.Any(item => !item.Visuals.IsEmpty)) _sceneVisible = now;
                if (frame.Readiness == SceneReadiness.Ready && _sceneReady == 0)
                {
                    _sceneReady = now;
                }
            }
            if (interaction is not null && _sceneOperation is not null && _sceneOperable > 0)
                CaptureReadyReference(interaction, _subject);
            if (interaction is not null && _sceneOperation is not null && (_sceneOperable == 0 || _scenario == "scene-reference"))
            {
                checkedNear = true;
                _sceneNearAvailable = CheckNearArea(interaction, _subject, out var detail);
                _sceneProbeDetail = detail;
                if (_sceneNearAvailable) _sceneFirstNearDetail ??= detail;
                if (_sceneDragIssued && _sceneNearAvailable && _sceneDrawCount > _sceneDragDraw &&
                    new Vector2(_app.WorldScenes.View.CameraRig.OrbitYawRadians,
                        _app.WorldScenes.View.CameraRig.OrbitPitchRadians) != _sceneBeforeDrag && _sceneOperable == 0)
                    _sceneOperable = Seconds();
            }
            if (now - _sceneLastProcessRead >= .2)
            {
                if (!GetProcessMemoryInfo(_process.Handle, out var memory, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _scenePrivate = (long)memory.PrivateUsage; _sceneWorking = (long)memory.WorkingSet;
                _scenePeakPrivate = (long)memory.PeakPagefileUsage; _scenePeakWorking = (long)memory.PeakWorkingSet;
                _sceneLastProcessRead = now;
            }
            var view = _app.WorldScenes.View;
            var blocking = SceneBlockingControls();
            var modal = SceneUiDescendants(_app).OfType<Window>()
                .Any(window => window != _app.GetWindow() && window.Visible && window.Exclusive);
            var bufferBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed);
            var textureBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
            var videoBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);
            var sample = new SceneSample(now, _scenePhase, interval, status.View.Position.CompletedSteps,
                status.Speed.ToString(), status.IsPaused, frame?.Update.Frame.Request.ToString(), frame?.Update.Frame.Space.ToString(),
                frame?.Readiness.ToString(), checkedNear ? _sceneNearAvailable : null, _sceneDragIssued,
                view.CameraRig.OrbitYawRadians, view.CameraRig.OrbitPitchRadians, view.Loading && view.Visible,
                view.LoadingOverlayVisible && view.Visible, blocking, modal,
                _scenePickCount - picksBefore,
                _sceneOperation is null ? null : _probeState, _scenePresented, _sceneDrawCount,
                _scenePrivate, _sceneWorking, _scenePeakPrivate, _scenePeakWorking,
                bufferBytes, textureBytes, videoBytes);
            _sceneRecent.Enqueue(sample);
            while (_sceneRecent.TryPeek(out var old) && old.At < now - 6) _sceneRecent.Dequeue();
            var loggingStarted = Seconds();
            _sceneTrace.WriteLine(JsonSerializer.Serialize(sample, SceneJson));
            if (_sceneOperation is not null && _sceneLoggedProbeState != _probeState)
            {
                _sceneProbeEvents.Add(new { kind = "probe-state", operation = _sceneOperation,
                    draw = _sceneDrawCount, at = now, state = _probeState,
                    detail = checkedNear ? _sceneProbeDetail : null });
                _sceneLoggedProbeState = _probeState;
            }
            loggingSeconds += Seconds() - loggingStarted;
            if (now - _sceneLastLoadSample >= 1 && (_sceneOperation is null || _scenario == "scene-reference"))
            {
                var loadRecord = new { kind = "active-load", phase = _scenePhase, load = SceneLoadCounts() };
                loggingStarted = Seconds();
                _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(loadRecord, SceneJson));
                loggingSeconds += Seconds() - loggingStarted;
                _sceneLastLoadSample = now;
            }
            if (_scenario == "scene-capture" && frame is not null && !_sceneCapturedPartial && frame.Readiness == SceneReadiness.Partial)
            {
                _sceneCapturedPartial = true;
                using var image = _app.GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(Path.GetDirectoryName(_output)!, "partial.png")) != Error.Ok)
                    throw new IOException("部分就绪截图保存失败。");
                _sceneEvidence["fog_samples"] = frame.Update.Environment.FoggedRegions.Take(3).Select(bounds =>
                {
                    var point = (bounds.Minimum + bounds.Maximum) / 2;
                    var pick = frame.Pick(new(new(point.X, 4096, point.Z), -System.Numerics.Vector3.UnitY, 8192));
                    return new { x = point.X, z = point.Z, pick = pick.GetType().Name };
                }).ToArray();
            }
            _sceneTiming?.AddFrame(now, status.View.Position.CompletedSteps, _simulationWork, status.Speed, status.IsPaused);
            _simulationWork = 0;
            _sceneLastDraw = now;
        }
        catch (Exception error) { _drawFailure = error; }
        finally
        {
            if (_sceneCostTrace is not null)
            {
                var ended = Seconds();
                _sceneProbeCosts.Add(new(operation, _sceneDrawCount, observerStarted, ended,
                    _probeFogSeconds, _probePointSeconds, _probeBusinessSeconds, loggingSeconds,
                    Math.Max(0, ended - observerStarted - _probeFogSeconds - _probePointSeconds - _probeBusinessSeconds - loggingSeconds),
                    _probeFogRegionCount));
            }
        }
    }

    private object SceneSteady()
    {
        var samples = _sceneRecent.Where(sample => sample.At >= _sceneLastDraw - 1).ToArray();
        double? Median(Func<SceneSample, double> value) => samples.Length == 0 ? null :
            FrameMeasurements.Distribution(samples.Select(value)).P50;
        return new { at = _sceneLastDraw, samples = samples.Length, private_bytes = Median(item => item.PrivateBytes),
            working_set_bytes = Median(item => item.WorkingSetBytes), buffer_bytes = Median(item => item.BufferBytes),
            texture_bytes = Median(item => item.TextureBytes), video_bytes = Median(item => item.VideoBytes),
            peak_private_bytes = _scenePeakPrivate, peak_working_set_bytes = _scenePeakWorking };
    }

    private void CollectSceneHeap(int cycle, string phase)
    {
        ResetSceneProbe();
        _sceneProbeDetail = _sceneFirstNearDetail = null;
        _sceneRecent.Clear();
        var natural = new { private_bytes = _scenePrivate, working_set_bytes = _sceneWorking };
        var started = Seconds();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var heap = GC.GetGCMemoryInfo();
        _sceneCollectionTrace!.WriteLine(JsonSerializer.Serialize(new { cycle, phase, at = Seconds(),
            seconds = Seconds() - started, natural, managed_live_bytes = GC.GetTotalMemory(false),
            heap_bytes = heap.HeapSizeBytes, fragmented_bytes = heap.FragmentedBytes,
            committed_bytes = heap.TotalCommittedBytes }, SceneJson));
        _sceneCollectionTrace.Flush();
    }

    private static string Location(WorldLocation location) => location switch
    {
        WorldLocation.Node node => "node:" + node.Id.Value,
        WorldLocation.Edge edge => "edge:" + edge.Id.Value,
        _ => throw new InvalidOperationException("未知地点。")
    };

    private object SceneLoadCounts()
    {
        var core = _app.Host!.GetStatus()!.View;
        var trains = core.World.Trains.Select(item => ((Lookup<TrainView>.Found)core.FindTrain(item.Id)).Value).ToArray();
        var settlements = core.World.Nodes.Where(item => item.Occupant is NodeOccupant.Settlement)
            .Select(item => ((NodeOccupant.Settlement)item.Occupant!).Id)
            .Select(id => ((Lookup<SettlementView>.Found)core.FindSettlement(id)).Value).ToArray();
        return new { at = Seconds(), position = core.Position, nodes = core.World.Nodes.Length, edges = core.World.Edges.Length,
            trains = trains.Length, train_vehicles = trains.Sum(train => train.Vehicles.Length),
            train_states = trains.Select(train => new { id = train.Id.Value, vehicles = train.Vehicles.Length,
                position = train.Position switch
                {
                    TrainPosition.Edge edge => "edge:" + edge.Id.Value,
                    TrainPosition.Node node => "node:" + node.Id.Value,
                    _ => throw new InvalidOperationException("未知列车位置。")
                },
                activity = train.Activity.ToString(),
                speed = train.SpeedMetersPerSecond, has_plan = train.Plan is not null }),
            combats = core.World.Combats.Select(item => new { id = item.Id.Value, location = Location(item.Location),
                participants = core.FindCombat(item.Id) is Lookup<CombatView>.Found combat ? combat.Value.Participants.Length : 0 }).ToArray(),
            creature_groups = core.World.CreatureGroups.Select(item => core.FindCreatureGroup(item.Id) is Lookup<CreatureGroupView>.Found group
                ? new { id = item.Id.Value, location = Location(group.Value.Location), activity = group.Value.Activity.ToString(),
                    creatures = group.Value.Creatures.Length, alive = group.Value.Creatures.Count(creature => creature.Health > 0) }
                : null).ToArray(),
            creatures = core.World.CreatureGroups.Sum(item => core.FindCreatureGroup(item.Id) is Lookup<CreatureGroupView>.Found group
                ? group.Value.Creatures.Length : 0),
            settlements = settlements.Length, buildings = settlements.Sum(settlement => settlement.Plots.Count(plot => plot.Occupant is PlotOccupant.Building)),
            parked_vehicles = settlements.Sum(settlement => settlement.ParkedVehicles.Length),
            active_facilities = settlements.Sum(settlement => settlement.Facilities.Count(facility => facility.ActualWorkPerSecond > 0)),
            facilities = settlements.SelectMany(settlement => settlement.Facilities)
                .Select(facility => new { key = facility.Key.ToString(), enabled = facility.Enabled,
                    state = facility.State.ToString(), actual_work_per_second = facility.ActualWorkPerSecond }).ToArray() };
    }

    private object SceneCacheState()
    {
        var controller = _app.WorldScenes!;
        var frame = controller.Display?.DisplayedFrame;
        return new { at = Seconds(), since_load_completed_seconds = Seconds() - _sceneLoadCompleted,
            previously_opened_targets = _sceneOpenedTargets.ToArray(),
            displayed = frame?.Update.Frame.ToString(), readiness = frame?.Readiness.ToString(),
            diagnostics = frame?.Diagnostics.Select(item => item.Code).ToArray() };
    }

    private string[] SceneBlockingControls()
    {
        var size = _app.GetViewport().GetVisibleRect().Size;
        return SceneUiDescendants(_app).OfType<Control>().Where(control => control.IsVisibleInTree() &&
            control.MouseFilter != Control.MouseFilterEnum.Ignore && control.GetGlobalRect().Size.X >= size.X &&
            control.GetGlobalRect().Size.Y >= size.Y && control is not MacroMap).Select(control => control.GetPath().ToString()).ToArray();
    }

    private async Task CaptureSceneImage(string name)
    {
        await _app.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = _app.GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.Combine(Path.GetDirectoryName(_output)!, name + ".png")) != Error.Ok)
            throw new IOException("测量截图保存失败。");
    }

    private void OnSceneSaveChanged()
    {
        if (_sceneOperationTrace is null) return;
        _sceneOperationTrace.WriteLine(JsonSerializer.Serialize(new { kind = "save-state", at = Seconds(),
            busy = _app.Saves!.IsBusy, pending_writes = _app.Saves.PendingWrites
                .Select(write => new { write.Kind, write.Path }).ToArray() }, SceneJson));
    }

    private void OnScenePresented(ISceneFrame frame)
    {
        if (IsRouteMeasurement && _routeSampling) { RecordRoutePresented(frame); return; }
        _scenePresentedStamp = frame.Update.Frame;
        var followPose = _subject is not null && frame.FindPose(_subject) is Lookup<ScenePose>.Found pose
            ? pose.Value.ToString() : null;
        _scenePresented = new { at = Seconds(), stamp = frame.Update.Frame.ToString(), readiness = frame.Readiness.ToString(),
            origin = frame.Origin, simulation_seconds = frame.SimulationSeconds, follow_pose = followPose };
    }

    private void RecordReadyGeometry(ISceneFrame frame, double at)
    {
        var started = Seconds();
        var objects = _app.WorldScenes!.Display!.Objects.Values.Select(item => item.Source).ToArray();
        var meshes = objects.Where(item => item.Parent is null && item.Subject is null)
            .SelectMany(item => item.Visuals.OfType<SceneVisual.Geometry>()).Select(item => item.Mesh).ToArray();
        var terrain = meshes.Where(mesh => mesh.Material is not SceneMaterial.BuiltIn).ToArray();
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "ready-geometry", operation = _sceneOperation, at, first_ready = _sceneReady, frame = frame.Update.Frame.ToString(),
            label = "替代读数，不是 N／C／F",
            scope = "drawn Ready at observation end; counted outside the timing interval; Parent=null, Subject=null, non-BuiltIn Geometry; includes authored ground; per published object, not whole node or unique GPU allocations",
            terrain_meshes = terrain.Length, terrain_vertices = terrain.Sum(mesh => (long)mesh.Vertices.Length),
            terrain_triangles = terrain.Sum(mesh => (long)mesh.TriangleIndices.Length / 3),
            built_in = meshes.Where(mesh => mesh.Material is SceneMaterial.BuiltIn)
                .GroupBy(mesh => ((SceneMaterial.BuiltIn)mesh.Material).Kind).Select(group => new
                { material = group.Key.ToString(), meshes = group.Count(), vertices = group.Sum(mesh => (long)mesh.Vertices.Length),
                    triangles = group.Sum(mesh => (long)mesh.TriangleIndices.Length / 3) }).ToArray(),
            followed_train = _sceneTarget is WorldObjectId.Train ? TrainGeometry(objects) : null,
            render_instances = RenderInstances(_app.WorldScenes.Display.Root),
            count_seconds = Seconds() - started
        }, SceneJson));
    }

    // 证明大设计实际显示为完整车体而非空壳：按成员统计已发布车体及其子对象的几何量。
    private object TrainGeometry(SceneObject[] objects)
    {
        var children = objects.Where(item => item.Parent is not null).ToLookup(item => item.Parent!.Value);
        IEnumerable<SceneMesh> Meshes(SceneObject item) => item.Visuals.OfType<SceneVisual.Geometry>().Select(visual => visual.Mesh)
            .Concat(children[item.Id].SelectMany(Meshes));
        var members = MeasuredTrain().Vehicles.Select(vehicle =>
        {
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(vehicle));
            var body = objects.FirstOrDefault(item => item.Parent is null && item.Subject == subject);
            var meshes = body is null ? [] : Meshes(body).ToArray();
            return new { vehicle = vehicle.Value, displayed = body is not null, meshes = meshes.Length,
                vertices = meshes.Sum(mesh => (long)mesh.Vertices.Length), triangles = meshes.Sum(mesh => (long)mesh.TriangleIndices.Length / 3) };
        }).ToArray();
        return new { train = _train.Value, members = members.Length, displayed_bodies = members.Count(item => item.displayed),
            vertices = members.Sum(item => item.vertices), triangles = members.Sum(item => item.triangles), per_member = members };
    }

    // Godot 为每个带实例变量的几何实例在全局着色器缓冲区预留 16 个 vec4，默认缓冲区可容纳 4096 个这样的实例。
    private static object RenderInstances(Node root)
    {
        static IEnumerable<GeometryInstance3D> Geometry(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is GeometryInstance3D geometry) yield return geometry;
                foreach (var descendant in Geometry(child)) yield return descendant;
            }
        }
        static bool Surface(GeometryInstance3D item) =>
            (item.MaterialOverride is ShaderMaterial { Shader: { } shader } && shader.Code.Contains("scene_surface.gdshaderinc")) ||
            item is MeshInstance3D mesh && Enumerable.Range(0, mesh.GetSurfaceOverrideMaterialCount()).Any(index =>
                mesh.GetSurfaceOverrideMaterial(index) is ShaderMaterial { Shader: { } shader } && shader.Code.Contains("scene_surface.gdshaderinc"));
        var instances = Geometry(root).ToArray();
        return new
        {
            geometry_instances = instances.Length,
            surface_shader_instances = instances.Count(Surface),
            instance_uniform_instances = instances.Count(item => RenderingServer.InstanceGeometryGetShaderParameterList(item.GetInstance()).Count > 0),
            instance_uniform_capacity = (int)ProjectSettings.GetSetting("rendering/limits/global_shader_variables/buffer_size", 65536) / 16
        };
    }

    private static IEnumerable<Node> SceneUiDescendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D) continue;
            yield return child;
            foreach (var descendant in SceneUiDescendants(child)) yield return descendant;
        }
    }
}

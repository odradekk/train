using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using NVector = System.Numerics.Vector3;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private bool IsRouteMeasurement => _scenario.StartsWith("scene-route-", StringComparison.Ordinal);
    private SimulationSpeed RouteSpeed => _scenario switch
    {
        "scene-route-2x" => SimulationSpeed.Two,
        "scene-route-4x" => SimulationSpeed.Four,
        _ when _scenario.EndsWith("8x", StringComparison.Ordinal) => SimulationSpeed.Eight,
        _ => SimulationSpeed.One
    };
    private readonly List<RouteFrame> _routeSamples = [];
    private readonly List<RouteRangeChange> _routeRangeChanges = [];
    private ISceneFrame? _routeSceneFrame;
    private readonly RouteRangeRecorder _routeBuiltRecorder = new(), _routeShownRecorder = new();
    private RouteRangeRecorder.Snapshot? _routeDisplayedRanges;
    private ImmutableHashSet<ulong> _routePreviousPresentedChanges = [];
    // null 表示缺少连续 D 增量，下一条记录必须比较真实完整快照。
    private HashSet<ulong>? _routeBuiltChanges, _routeShownChanges;
    private readonly Dictionary<VehicleId, double?> _routeLengths = [];
    private long? _routeBuilt, _routeShown;
    private (SceneFrameStamp Frame, SceneSpaceId Coordinates, ScenePoint Translation)? _routeShownIdentity;
    private RouteTransition? _routeTransition;
    private TrainPosition? _routePreviousPosition;
    private long _routeStateId, _routeFrames;
    private long _routeDecisionEndDraw;
    private TimeSpan? _routeOperableGcPause;
    private int[]? _routeOperableGcCollections;
    private double _routeStarted, _routeLastCapture, _routePresentedCost;
    private SceneSpaceId? _routeLastSpace;
    private int _routeCaptureBurst;
    private bool _routeSampling;

    private async Task RunFollowRoute()
    {
        var directory = Path.GetDirectoryName(_output)!;
        var traceWritten = false;
        void WriteTrace()
        {
            traceWritten = true;
            using (var frames = new StreamWriter(Path.Combine(directory, "route-frames.jsonl")))
                foreach (var sample in _routeSamples) frames.WriteLine(JsonSerializer.Serialize(sample, SceneJson));
            using (var ranges = new StreamWriter(Path.Combine(directory, "route-ranges.jsonl")))
                foreach (var change in _routeRangeChanges) ranges.WriteLine(JsonSerializer.Serialize(change, SceneJson));
        }
        try
        {
            var host = _app.Host!;
            var speed = RouteSpeed;
            var train = MeasuredTrain();
            // 与 GameUi.OpenScene 相同。
            var followed = FollowedVehicle.Of(train, host.GetStatus()!.View, host.Content!);
            _subject = new WorldObjectId.Instance(new InstanceId.Vehicle(followed));
            _sceneTarget = new WorldObjectId.Train(train.Id);
            host.SetSpeed(speed);
            host.SetPaused(false);
            if (host.Submit(host.GetStatus()!.Binding, new CoreCommand.StartTrain(train.Id), CommandSource.AutomationTest)
                is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted })
                throw new InvalidOperationException("无法启动测量列车。");
            _app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").RaiseObjectClicked(_sceneTarget);
            _routeStarted = Seconds();
            var gcPause = GC.GetTotalPauseDuration();
            var gcCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            _sceneTiming = new(_routeStarted, host.GetStatus()!.View.Position.CompletedSteps);
            _routeSampling = true;
            EdgeId? first = null, second = null;
            NodeId? middle = null, finalNode = null;
            double? coreCompletedAt = null;
            var routeCompleted = false;
            var postrollCells = 0d;
            ScenePoint? postrollPrevious = null;
            TimeSpan? routeGcEnd = null;
            int[]? routeGcEndCollections = null;
            var transitions = new List<object>();
            string? lastPosition = null;
            while (true)
            {
                await _app.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                if (_drawFailure is not null) throw _drawFailure;
                var status = host.GetStatus()!;
                train = ((Lookup<TrainView>.Found)status.View.FindTrain(train.Id)).Value;
                var location = train.Position is TrainPosition.Edge edge ? "edge:" + edge.Id.Value : "node:" + ((TrainPosition.Node)train.Position).Id.Value;
                if (location != lastPosition)
                {
                    transitions.Add(new { location, elapsed_seconds = Seconds() - _routeStarted, steps = status.View.Position.CompletedSteps });
                    lastPosition = location;
                    GD.Print("STREAMING_ROUTE " + location + " elapsed=" + (Seconds() - _routeStarted).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (train.Position is TrainPosition.Edge at)
                {
                    first ??= at.Id;
                    if (middle is not null) second ??= at.Id;
                }
                else if (first is not null && train.Position is TrainPosition.Node node)
                {
                    if (second is not null) { coreCompletedAt ??= Seconds(); finalNode ??= node.Id; }
                    else middle ??= node.Id;
                }
                // 终点按车头判断：原路返回后跟随的首车在车尾，长编组车尾要再走一列车长才进入终点节点。
                var head = new WorldObjectId.Instance(new InstanceId.Vehicle(
                    train.OrderFacing == TrainOrderFacing.FirstEndLeading ? train.Vehicles[0] : train.Vehicles[^1]));
                if (coreCompletedAt is not null && _routeSceneFrame is { } latest &&
                    _app.WorldScenes!.Display?.DisplayedFrame is { } drawn && drawn.Update.Frame.Space == latest.Update.Frame.Space &&
                    drawn.FindPose(head) is Lookup<ScenePose>.Found finalPose &&
                    drawn.Locate(finalPose.Value.Position) is Lookup<WorldLocation>.Found { Value: WorldLocation.Node atNode } && atNode.Id == finalNode)
                {
                    if (!routeCompleted)
                    {
                        routeCompleted = true; _routeDecisionEndDraw = _routeFrames;
                        routeGcEnd = GC.GetTotalPauseDuration();
                        routeGcEndCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                    }
                    var sample = _routeSamples[^1];
                    var leading = train.OrderFacing == TrainOrderFacing.FirstEndLeading ? sample.Vehicles.FirstOrDefault() : sample.Vehicles.LastOrDefault();
                    if (leading is not null)
                    {
                        if (postrollPrevious is { } before)
                            postrollCells += Math.Sqrt(Math.Pow(leading.Position.X - before.X, 2) + Math.Pow(leading.Position.Y - before.Y, 2) + Math.Pow(leading.Position.Z - before.Z, 2));
                        postrollPrevious = leading.Position;
                        if (leading.LengthCells is { } length && postrollCells >= length / 2 + 1) break;
                    }
                }
                if (coreCompletedAt is { } ended && Seconds() - ended > 30)
                {
                    if (routeCompleted) break;
                    throw new TimeoutException("核心路线完成后30秒内未取得终点新场景显示帧。");
                }
                if (Seconds() - _routeStarted > 3300) throw new TimeoutException("整条路线在3300秒内未完成。");
                if (_sceneDuration > 0 && Seconds() - _routeStarted >= _sceneDuration) break;
            }
            _routeSampling = false;
            _timing = _sceneTiming.Summarize();
            _sceneTiming = null;
            _sceneEvidence["route_gc"] = new
            {
                pause_seconds = ((routeGcEnd ?? GC.GetTotalPauseDuration()) - gcPause).TotalSeconds,
                collections_by_generation = Enumerable.Range(0, 3).Select(generation => (routeGcEndCollections?[generation] ?? GC.CollectionCount(generation)) - gcCollections[generation]).ToArray(),
                after_first_operable_pause_seconds = _routeOperableGcPause is { } operable ? ((routeGcEnd ?? GC.GetTotalPauseDuration()) - operable).TotalSeconds : (double?)null,
                after_first_operable_collections_by_generation = _routeOperableGcCollections is { } counts ?
                    Enumerable.Range(0, 3).Select(generation => (routeGcEndCollections?[generation] ?? GC.CollectionCount(generation)) - counts[generation]).ToArray() : null,
                method = "Process-local GC.GetTotalPauseDuration delta; GC.CollectionCount deltas are collection counts, not pause-event counts. Trajectory-only postroll, offline serialization and analysis are excluded."
            };
            WriteTrace();
            var routeAnalysis = FollowRouteAnalysis.Analyze(directory);
            _sceneEvidence["route"] = new
            {
                train = train.Id.Value, vehicle = followed.Value, first_edge = first?.Value, middle_node = middle?.Value,
                second_edge = second?.Value, transitions, route_completed = routeCompleted, preview_seconds = _sceneDuration, frames = _routeFrames,
                decision_end_draw = _routeDecisionEndDraw, postroll_frames = _routeDecisionEndDraw == 0 ? 0 : _routeFrames - _routeDecisionEndDraw,
                analysis = routeAnalysis.Summary, startup = routeAnalysis.Startup,
                elapsed_seconds = _timing.ElapsedSeconds, capture_only = _scenario.Contains("capture", StringComparison.Ordinal),
                absolute_start_seconds = _routeStarted, clock_frequency = System.Diagnostics.Stopwatch.Frequency,
                endpoint = "first edge, full node traversal, full following edge; ends on next node",
                files = new[] { "route-frames.jsonl", "route-ranges.jsonl", "route-analysis.json" }
            };
        }
        finally
        {
            _routeSampling = false;
            if (!traceWritten) WriteTrace();
        }
    }

    private void RecordRoutePresented(ISceneFrame frame)
    {
        if (!_routeSampling) return;
        var started = Seconds();
        if (_routeSceneFrame is { } previous && previous.Update.Frame.Space != frame.Update.Frame.Space &&
            _app.WorldScenes!.Display?.DisplayedFrame is { } old && old.Update.Frame.Space == previous.Update.Frame.Space)
        {
            var core = _app.Host!.GetStatus()!.View;
            var current = ((Lookup<TrainView>.Found)core.FindTrain(_train)).Value.Position;
            var edge = current as TrainPosition.Edge ?? _routePreviousPosition as TrainPosition.Edge;
            var multipleTracks = edge is not null && core.FindEdge(edge.Id) is Lookup<EdgeView>.Found found && found.Value.Map.Tracks > 1;
            var candidate = _app.WorldScenes.Display.CandidateProgress(frame.Update.Frame);
            _routeTransition = new(old.Update.Frame, old.Origin, frame.Update.Frame, frame.Origin, multipleTracks,
                candidate?.Prepared, candidate?.Received);
        }
        _routeSceneFrame = frame;
        if (frame.Update.Base is { } baseline)
        {
            if (_app.WorldScenes!.Display?.DisplayedFrame?.Update.Frame != baseline)
            { _routeBuilt = null; _routeBuiltChanges = null; _routePresentedCost += Seconds() - started; return; }
        }
        ObserveRouteDisplay();
        var snapshot = _routeBuiltRecorder.CapturePresented(frame, _routeDisplayedRanges)!;
        // P₂ 以 D 为基准；P₁ 独有的覆盖必须撤销，不能把 P₂ 累积到 P₁ 上。
        var changed = _routeBuiltChanges is null || !snapshot.IsDelta ? null :
            _routeBuiltChanges.Concat(_routePreviousPresentedChanges).Concat(snapshot.Changed);
        _routeBuilt = RecordRouteRanges(_routeBuiltRecorder, snapshot, changed, frame.Update.Frame.Space, default);
        _routePreviousPresentedChanges = snapshot.Changed;
        _routeBuiltChanges = [];
        _routePresentedCost += Seconds() - started;
    }

    private void ObserveRouteDisplay()
    {
        var display = _app.WorldScenes!.Display;
        if (display?.DisplayedFrame is not { } drawn || _routeDisplayedRanges?.Frame.Update.Frame == drawn.Update.Frame) return;
        var next = _routeBuiltRecorder.Capture(drawn, _routeDisplayedRanges, () => display.Objects.Values.Select(item => item.Source));
        if (!next.IsDelta) { _routeBuiltChanges = null; _routeShownChanges = null; }
        else
        {
            _routeBuiltChanges?.UnionWith(next.Changed);
            _routeShownChanges?.UnionWith(next.Changed);
        }
        _routeDisplayedRanges = next;
    }

    private long? RecordRouteRanges(RouteRangeRecorder recorder, RouteRangeRecorder.Snapshot snapshot,
        IEnumerable<ulong>? changed, SceneSpaceId coordinates, ScenePoint translation)
    {
        if (recorder.Record(_routeStateId + 1, snapshot, changed, coordinates, translation) is { } change)
        { _routeStateId++; _routeRangeChanges.Add(change); }
        return recorder.Id;
    }

    private static ScenePoint RouteAbsolute(NVector point, ScenePoint origin) => new(origin.X + point.X, origin.Y + point.Y, origin.Z + point.Z);

    private void OnRouteDraw()
    {
        if (!_routeSampling) return;
        var started = Seconds();
        try
        {
            var status = _app.Host!.GetStatus()!;
            if (status.IsFrozen) throw new InvalidOperationException("路线测量期间核心冻结。");
            if (_app.GetWindow().Mode == Window.ModeEnum.Minimized) throw new InvalidOperationException("测量窗口被最小化。");
            var now = Seconds();
            var completed = status.View.Position.CompletedSteps;
            var frame = _routeSceneFrame; var display = _app.WorldScenes!.Display;
            var train = ((Lookup<TrainView>.Found)status.View.FindTrain(_train)).Value;
            var vehicles = new List<RouteVehicle>(); var displayedVehicles = new List<RouteVehicle>();
            if (frame is not null)
            {
                foreach (var id in train.Vehicles)
                {
                    if (!_routeLengths.TryGetValue(id, out var length))
                    {
                        var instance = ((Lookup<InstanceView>.Found)status.View.FindInstance(new InstanceId.Vehicle(id))).Value;
                        length = (_app.Host.Content!.FindDesign(instance.Design) as ContentLookup<DesignEvaluation>.Found)?.Value switch
                        {
                            DesignEvaluation.Complete { Core.Design.Kind: DesignKind.Vehicle design } => design.LengthCells,
                            DesignEvaluation.Partial { Metrics.VehicleLengthCells: ComputedValue<int>.Known known } => known.Value,
                            _ => null
                        };
                        _routeLengths.Add(id, length);
                    }
                    var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(id));
                    if (frame.FindPose(subject) is Lookup<ScenePose>.Found pose)
                    {
                        vehicles.Add(new(id.Value, RouteAbsolute(pose.Value.Position, frame.Origin), length));
                    }
                    var shown = display?.FindRoot(subject);
                    if (shown is not null && display!.PoseFrame?.Update.Frame.Space == frame.Update.Frame.Space)
                    {
                        var transform = _app.WorldScenes.View.GlobalTransform.AffineInverse() * shown.Root.GlobalTransform;
                        displayedVehicles.Add(new(id.Value, RouteAbsolute(new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z), frame.Origin),
                            length));
                    }
                }
                if (display?.DisplayedFrame is { } drawn)
                {
                    ObserveRouteDisplay();
                    var shift = new NVector(display.Root.Position.X, display.Root.Position.Y, display.Root.Position.Z);
                    var identity = (drawn.Update.Frame, frame.Update.Frame.Space, RouteAbsolute(shift, frame.Origin));
                    if (drawn.Update.Frame == frame.Update.Frame && shift == NVector.Zero)
                    { _routeShown = _routeBuilt; _routeShownIdentity = null; }
                    else if (_routeShownIdentity != identity)
                    {
                        var translation = new ScenePoint(frame.Origin.X + shift.X - drawn.Origin.X,
                            frame.Origin.Y + shift.Y - drawn.Origin.Y, frame.Origin.Z + shift.Z - drawn.Origin.Z);
                        _routeShown = RecordRouteRanges(_routeShownRecorder, _routeDisplayedRanges!, _routeShownChanges,
                            frame.Update.Frame.Space, translation);
                        _routeShownChanges = [];
                        _routeShownIdentity = identity;
                    }
                }
            }
            var newSpace = frame is not null && frame.Update.Frame.Space != _routeLastSpace;
            if (newSpace) { _routeLastSpace = frame!.Update.Frame.Space; _routeCaptureBurst = 6; }
            var operable = _app.WorldScenes.View.Interactive && !_app.WorldScenes.View.Loading;
            if (operable && _routeOperableGcPause is null)
            {
                _routeOperableGcPause = GC.GetTotalPauseDuration();
                _routeOperableGcCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            }
            var sample = new RouteFrame(++_routeFrames, now - _routeStarted, completed,
                frame?.Update.Frame, display?.DisplayedFrame?.Update.Frame,
                _routeBuilt, _routeShown, vehicles.ToArray(), displayedVehicles.ToArray(), train.OrderFacing == TrainOrderFacing.FirstEndLeading,
                JsonSerializer.SerializeToElement(train.Position, train.Position.GetType(), SceneJson), Seconds() - started + _routePresentedCost,
                train.Vehicles.Select(id => id.Value).ToArray(), train.LengthMeters,
                operable, _routeTransition, _routeDecisionEndDraw == 0, display?.DisplayedFrame?.Readiness);
            _routeSamples.Add(sample);
            _routeTransition = null;
            _routePreviousPosition = train.Position;
            _routePresentedCost = 0;
            if (_scenario.Contains("capture", StringComparison.Ordinal) && (now - _routeLastCapture >= .5 || _routeCaptureBurst > 0))
            {
                using var image = _app.GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(Path.GetDirectoryName(_output)!, $"route-{_routeFrames:D6}.png")) != Error.Ok)
                    throw new IOException("路线截图保存失败。");
                _routeLastCapture = now; _routeCaptureBurst--;
            }
            _sceneTiming!.AddFrame(now, completed, _simulationWork, status.Speed, status.IsPaused);
            _simulationWork = 0;
        }
        catch (Exception error) { _drawFailure = error; }
        finally
        {
            var ended = Seconds();
            _sceneProbeCosts.Add(new("route-observer", _routeFrames, started, ended, 0, 0, 0, 0, ended - started, 0));
        }
    }
}

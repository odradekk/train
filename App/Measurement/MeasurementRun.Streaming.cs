using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private readonly StreamingRecorder? _streamingRecorder;
    private readonly string _streamingCamera;
    private readonly List<StreamingRouteFrame> _streamingFrames = [];
    private readonly List<StreamingHostEvent> _streamingHost = [];
    private readonly List<CoverageObservation> _streamingCoverage = [];
    private readonly List<StreamingProbe> _streamingProbes = [];
    private SceneDisplay? _streamingDisplay;
    private int _streamingDisplayNumber;
    private int _streamingHostReplayed;
    private VehicleId _streamingFollowed;
    private double _streamingLastCoverage = double.NegativeInfinity;
    private SceneFrameStamp? _streamingCoverageFrame;
    private string? _streamingCoverageWindow;
    private sealed record StreamingProbe(long Draw, string Kind, double Started, double Ended)
    {
        public double Seconds => Ended - Started;
    }
    private static readonly JsonSerializerOptions StreamingJson = new(SceneJson)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private void StartStreamingRoute(VehicleId followed)
    {
        if (IsLowOrbit) BeginLowOrbit();
        if (_streamingRecorder is null) return;
        _streamingFollowed = followed;
        var camera = _app.WorldScenes!.View.CameraRig;
        camera.ShakeEnabled = false;
        if (_streamingCamera == "farthest") camera.Zoom(-100);
        _sceneEvidence["streaming"] = new
        {
            enabled = true, camera = _streamingCamera, camera_shake = false,
            window_boundary_core_meters = StreamingRouteAnalysis.WindowBoundaryCoreMeters, grid_columns = ViewCoverage.Columns, grid_rows = ViewCoverage.Rows,
            coverage_hz = ViewCoverage.FrequencyHz, ground_plane_scene_y = 0,
            attribution = "Scenes frame terrain parts classified online; no sampled points retained",
            files = new[] { "streaming-frames.jsonl", "streaming-host.jsonl", "streaming-coverage.jsonl",
                "streaming-probes.jsonl", "streaming-scenes.jsonl", "streaming-parts.jsonl", "streaming-analysis.json" }
        };
    }

    private void ObserveStreamingDisplay()
    {
        if (_streamingRecorder is null || _app.WorldScenes!.Display is not { } display || ReferenceEquals(display, _streamingDisplay)) return;
        _streamingDisplay = display;
        var number = ++_streamingDisplayNumber;
        display.Observe = value =>
        {
            if (_routeSampling || OpeningDiagnostics && _sceneTrace is not null)
            {
                var at = Seconds();
                _streamingHost.Add(new(number, at, value));
            }
        };
    }

    private void OnScenePresented(ISceneFrame frame)
    {
        ObserveSceneResources();
        if (OpeningDiagnostics) ObserveStreamingDisplay();
        if (IsRouteMeasurement && _routeSampling) { RecordRoutePresented(frame); return; }
        _scenePresentedStamp = frame.Update.Frame;
        var followPose = _subject is not null && frame.FindPose(_subject) is Lookup<ScenePose>.Found pose
            ? pose.Value.ToString() : null;
        _scenePresented = new { at = Seconds(), stamp = frame.Update.Frame.ToString(), readiness = frame.Readiness.ToString(),
            origin = frame.Origin, simulation_seconds = frame.SimulationSeconds, follow_pose = followPose };
    }

    private void RecordStreamingDraw(RouteFrame route, TrainView train, ICoreReadView core)
    {
        if (_streamingRecorder is not { } recorder) return;
        var started = Seconds();
        double length;
        if (train.Position is TrainPosition.Edge edge)
            length = ((Lookup<EdgeView>.Found)core.FindEdge(edge.Id)).Value.Map.LengthMeters;
        else if (train.Position is TrainPosition.Node node)
        {
            var view = ((Lookup<NodeView>.Found)core.FindNode(node.Id)).Value;
            length = view.ApproachLengthMeters + view.PlatformLengthMeters + view.DepartureLengthMeters;
        }
        else throw new InvalidOperationException("未知路线位置。");
        var window = StreamingRouteAnalysis.Window(train.Position, length);
        var display = _app.WorldScenes!.Display;
        var frame = display?.DisplayedFrame;
        recorder.Displayed(frame?.Update.Frame, _streamingHost.Skip(_streamingHostReplayed));
        _streamingHostReplayed = _streamingHost.Count;
        var previous = _streamingCoverageFrame;
        if (route.At - _streamingLastCoverage >= ViewCoverage.IntervalSeconds || window.Key != _streamingCoverageWindow ||
            (frame is null) != (previous is null) || frame is not null && previous is not null &&
            !StreamingRouteAnalysis.SameSpace(frame.Update.Frame, previous))
        {
            var probeStarted = Seconds();
            var sample = frame is not null ? ViewCoverage.Sample(frame, CoverageRays(display!),
                recorder.Coverage(frame.Update.Frame), recorder.Category) : null;
            _streamingCoverage.Add(new(route.At, frame?.Update.Frame, window.Key, sample, route.Draw));
            _streamingLastCoverage = route.At;
            _streamingCoverageFrame = frame?.Update.Frame;
            _streamingCoverageWindow = window.Key;
            _streamingProbes.Add(new(route.Draw, "coverage", probeStarted - _routeStarted, Seconds() - _routeStarted));
        }
        var camera = RecordCamera();
        var position = route.DisplayedVehicles.FirstOrDefault(vehicle => vehicle.Id == _streamingFollowed.Value)?.Position;
        _streamingFrames.Add(new(route.Draw, route.At, route.Steps, route.Latest, route.Displayed, position,
            route.Transition, window, camera, _streamingCoverage.Count > 0 ? _streamingCoverage[^1].Draw : null,
            recorder.MissingCounts(), recorder.PreviewCounts(), recorder.RebuildingCounts()));
        _streamingProbes.Add(new(route.Draw, "streaming-draw", started - _routeStarted, Seconds() - _routeStarted));
    }

    private IEnumerable<CoverageRay> CoverageRays(SceneDisplay display)
    {
        var camera = _app.WorldScenes!.View.CameraRig.Camera;
        var inverse = display.Root.GlobalTransform.AffineInverse();
        var size = _app.GetViewport().GetVisibleRect().Size;
        var forward = -camera.GlobalBasis.Z;
        for (var y = 0; y < ViewCoverage.Rows; y++)
        for (var x = 0; x < ViewCoverage.Columns; x++)
        {
            var screen = new Vector2((x + .5f) * size.X / ViewCoverage.Columns, (y + .5f) * size.Y / ViewCoverage.Rows);
            var worldOrigin = camera.ProjectRayOrigin(screen);
            var worldDirection = camera.ProjectRayNormal(screen).Normalized();
            var depth = (worldOrigin - camera.GlobalPosition).Dot(forward);
            var slope = worldDirection.Dot(forward);
            var origin = inverse * worldOrigin;
            var direction = (inverse.Basis * worldDirection).Normalized();
            yield return new(Point(origin), Point(direction), (camera.Near - depth) / slope, (camera.Far - depth) / slope);
        }
    }

    private static ScenePoint Point(Vector3 value) => new(value.X, value.Y, value.Z);

    private void WriteStreamingTrace(string directory)
    {
        if (_streamingRecorder is null) return;
        Write("streaming-frames.jsonl", _streamingFrames);
        Write("streaming-host.jsonl", _streamingHost.Select(value => value with { At = value.At - _routeStarted }));
        Write("streaming-coverage.jsonl", _streamingCoverage);
        Write("streaming-probes.jsonl", _streamingProbes);
        _streamingRecorder?.Drain();
        var until = _routeStarted + (_routeSamples.Count > 0 ? _routeSamples[^1].At : 0);
        var parts = _streamingRecorder?.Timings(_routeStarted, until) ?? [];
        var sceneEvents = _streamingRecorder?.Events(_routeStarted, until) ?? [];
        Write("streaming-scenes.jsonl", sceneEvents);
        Write("streaming-parts.jsonl", parts);
        object Distribution(IEnumerable<double?> values)
        {
            var known = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            var distribution = known.Length > 0 ? FrameMeasurements.Distribution(known) : null;
            return new { n = known.Length, p50 = distribution?.P50, p95 = distribution?.P95,
                p99 = distribution?.P99, maximum = distribution?.Maximum };
        }
        object StageDistributions(IEnumerable<StreamingPartTiming> values)
        {
            var group = values.ToArray();
            return new
            {
                needed_to_commit = Distribution(group.Select(part => part.NeededToCommittedSeconds)),
                needed_to_queued = Distribution(group.Select(part => part.Queued - part.Needed)),
                queued_to_started = Distribution(group.Select(part => part.QueueSeconds)),
                started_to_built = Distribution(group.Select(part => part.BuildSeconds)),
                built_to_admitted = Distribution(group.Select(part => part.BuiltToAdmittedSeconds)),
                admitted_to_commit = Distribution(group.Select(part => part.AdmittedToCommittedSeconds)),
                admitted_to_resources_prepared = Distribution(group.Select(part => part.ResourcesPrepared - part.Admitted)),
                resources_prepared_to_commit = Distribution(group.Select(part => part.Committed - part.ResourcesPrepared))
            };
        }
        object PartSummary(IEnumerable<StreamingPartTiming> values)
        {
            var group = values.ToArray();
            return new
            {
                attempts = group.Length,
                outcomes = group.GroupBy(part => part.Outcome).ToDictionary(items => items.Key, items => items.Count()),
                cancelled = group.Count(part => part.Outcome == "cancelled"),
                unfinished = group.Count(part => part.Outcome == "unfinished"),
                already_visible_before_demand = group.Count(part => part.AlreadyVisibleBeforeDemand),
                latency_seconds = StageDistributions(group)
            };
        }
        var frameParts = sceneEvents.Where(value => value.Kind == "Frame" && value.Stamp is not null)
            .GroupBy(value => value.Stamp!).ToDictionary(group => group.Key, group => group.First().Parts ?? []);
        var partWindows = new Dictionary<SceneStreamingAttemptId, string>();
        foreach (var draw in _streamingFrames)
            if (draw.Displayed is { } displayed && frameParts.TryGetValue(displayed, out var members))
                foreach (var member in members) partWindows.TryAdd(member, draw.Window.Key);
        var speeds = StreamingRouteAnalysis.Speeds(_streamingFrames);
        var end = _routeSamples.Count > 0 ? _routeSamples[^1].At : (double?)null;
        var analysis = new
        {
            already_visible_before_demand = parts.Count(part => part.AlreadyVisibleBeforeDemand),
            part_outcomes = parts.GroupBy(part => part.Outcome).ToDictionary(group => group.Key, group => group.Count()),
            part_latency_seconds = StageDistributions(parts),
            parts_by_window_and_kind = parts.GroupBy(part =>
                (Window: partWindows.GetValueOrDefault(part.Attempt, "not-observed"), Kind: part.Attempt.Part.Kind))
                .Select(group => new { window = group.Key.Window, kind = group.Key.Kind, summary = PartSummary(group) }).ToArray(),
            speeds, speed_summary = StreamingRouteAnalysis.SummarizeSpeeds(speeds),
            coverage = ViewCoverage.Analyze(_streamingCoverage, end),
            coverage_by_window = _streamingCoverage.Select(sample => sample.Window).Distinct().Select(window => new
            {
                window,
                analysis = ViewCoverage.Analyze(_streamingCoverage.Select(sample => sample.Window == window
                    ? sample : sample with { Sample = null }).ToArray(), end, window),
                missing_categories = ViewCoverage.SummarizeCategories(_streamingCoverage.Where(sample => sample.Window == window))
            }).ToArray(),
            host_cancelled_candidates = _streamingHost.Count(item => item.Value.Stage == SceneDisplayStage.HostCancelled),
            probe_seconds = _streamingProbes.Where(probe => probe.Kind == "streaming-draw").Sum(probe => probe.Seconds),
            probe_scope = "coverage is nested in streaming-draw, which is nested in route-observer; do not sum nested costs"
        };
        File.WriteAllText(Path.Combine(directory, "streaming-analysis.json"), JsonSerializer.Serialize(analysis, StreamingJson));

        void Write<T>(string name, IEnumerable<T> values)
        {
            using var writer = new StreamWriter(Path.Combine(directory, name));
            foreach (var value in values) writer.WriteLine(JsonSerializer.Serialize(value, StreamingJson));
        }
    }
}

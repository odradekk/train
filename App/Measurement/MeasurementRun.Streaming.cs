using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private readonly bool _streamingEnabled;
    private readonly string _streamingCamera;
    private readonly List<StreamingRouteFrame> _streamingFrames = [];
    private readonly List<StreamingHostEvent> _streamingHost = [];
    private readonly List<CoverageObservation> _streamingCoverage = [];
    private readonly List<StreamingProbe> _streamingProbes = [];
    private SceneDisplay? _streamingDisplay;
    private int _streamingDisplayNumber;
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
        if (!_streamingEnabled) return;
        if (_app.Host!.GetStatus()!.View.World.WorldSeed != 40)
            throw new InvalidDataException("流式测量使用 seed 40 起步贸易存档。");
        _streamingFollowed = followed;
        _app.WorldScenes!.View.CameraRig.SetMeasurementCamera(_streamingCamera == "farthest");
        _sceneEvidence["streaming"] = new
        {
            enabled = true, camera = _streamingCamera, camera_yaw_radians = 0, camera_pitch_radians = .55,
            window_boundary_core_meters = 128, grid_columns = ViewCoverage.Columns, grid_rows = ViewCoverage.Rows,
            coverage_hz = 5, ground_plane_scene_y = 0,
            attribution = "host objects only; Scenes part attribution awaits contract confirmation",
            files = new[] { "streaming-frames.jsonl", "streaming-host.jsonl", "streaming-coverage.jsonl",
                "streaming-probes.jsonl", "streaming-analysis.json" }
        };
    }

    private void ObserveStreamingDisplay()
    {
        if (!_streamingEnabled || _app.WorldScenes!.Display is not { } display || ReferenceEquals(display, _streamingDisplay)) return;
        _streamingDisplay = display;
        var number = ++_streamingDisplayNumber;
        display.Observe = value =>
        {
            if (_routeSampling) _streamingHost.Add(new(number, Seconds() - _routeStarted, value));
        };
    }

    private void RecordStreamingDraw(RouteFrame route, TrainView train, ICoreReadView core)
    {
        if (!_streamingEnabled) return;
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
        var previous = _streamingCoverageFrame;
        if (route.At - _streamingLastCoverage >= .2 || window.Key != _streamingCoverageWindow ||
            (frame is null) != (previous is null) || frame is not null && previous is not null &&
            !StreamingRouteAnalysis.SameSpace(frame.Update.Frame, previous))
        {
            var probeStarted = Seconds();
            var sample = frame is not null ? ViewCoverage.Sample(frame, CoverageRays(display!)) : null;
            _streamingCoverage.Add(new(route.At, frame?.Update.Frame, window.Key, sample, route.Draw));
            _streamingLastCoverage = route.At;
            _streamingCoverageFrame = frame?.Update.Frame;
            _streamingCoverageWindow = window.Key;
            _streamingProbes.Add(new(route.Draw, "coverage", probeStarted - _routeStarted, Seconds() - _routeStarted));
        }
        var rig = _app.WorldScenes!.View.CameraRig;
        var cameraPosition = rig.Camera.Position;
        var offset = cameraPosition - rig.Focus;
        var horizontal = Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z);
        var camera = new RouteCameraMeasurement(Point(cameraPosition), Point(rig.Focus), offset.Length(),
            Math.Atan2(offset.X, offset.Z), Math.Atan2(offset.Y, horizontal), rig.Camera.Near, rig.Camera.Far);
        var position = route.DisplayedVehicles.FirstOrDefault(vehicle => vehicle.Id == _streamingFollowed.Value)?.Position;
        _streamingFrames.Add(new(route.Draw, route.At, route.Steps, route.Latest, route.Displayed, position,
            route.Transition, window, camera, _streamingCoverage.Count > 0 ? _streamingCoverage[^1].Draw : null));
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
        if (!_streamingEnabled) return;
        Write("streaming-frames.jsonl", _streamingFrames);
        Write("streaming-host.jsonl", _streamingHost);
        Write("streaming-coverage.jsonl", _streamingCoverage);
        Write("streaming-probes.jsonl", _streamingProbes);
        var speeds = StreamingRouteAnalysis.Speeds(_streamingFrames);
        var latencies = StreamingRouteAnalysis.HostLatencies(_streamingHost);
        var windows = _streamingFrames.Where(frame => frame.Displayed is not null).GroupBy(frame => frame.Displayed!)
            .ToDictionary(group => group.Key, group => group.First().Window.Key);
        var latencyWindows = latencies.GroupBy(item => windows.GetValueOrDefault(item.Frame, "not-observed"));
        var end = _routeSamples.Count > 0 ? _routeSamples[^1].At : (double?)null;
        var analysis = new
        {
            speeds, speed_summary = StreamingRouteAnalysis.SummarizeSpeeds(speeds),
            coverage = ViewCoverage.Analyze(_streamingCoverage, end),
            coverage_by_window = _streamingCoverage.Select(sample => sample.Window).Distinct().Select(window => new
            {
                window,
                analysis = ViewCoverage.Analyze(_streamingCoverage.Select(sample => sample.Window == window
                    ? sample : sample with { Sample = null }).ToArray(), end)
            }).ToArray(),
            host_prepared_to_commit = latencies,
            host_prepared_to_commit_by_window = latencyWindows.Select(group => new
            {
                window = group.Key, objects = group.Count(),
                seconds = FrameMeasurements.Distribution(group.Select(item => item.PreparedToCommitSeconds))
            }).ToArray(),
            host_prepared_to_commit_seconds = latencies.Length > 0
                ? FrameMeasurements.Distribution(latencies.Select(item => item.PreparedToCommitSeconds)) : null,
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

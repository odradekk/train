using System.Text.Json;
using Godot;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private readonly int _nodeReadyHoldSeconds;
    private double _lowOrbitStarted, _lowOrbitLastDraw, _lowOrbitLastSample, _lowOrbitRequestedRadians;
    private StreamWriter? _lowOrbitRouteTrace;
    private double LowOrbitPeriodSeconds => IsRouteMeasurement ? 30d / (int)RouteSpeed : 30d;
    private bool IsLowOrbit => _streamingCamera is "default-low-orbit" or "farthest-low-orbit";

    internal static void ValidateCameraOptions(string scenario, string camera, bool explicitCamera, bool diagnostics)
    {
        var route = scenario.StartsWith("scene-route-", StringComparison.Ordinal);
        var orbit = camera is "default-low-orbit" or "farthest-low-orbit";
        var freeOrbit = orbit && scenario is ("scene-node" or "scene-edge");
        if (diagnostics && !route || explicitCamera && !diagnostics && !freeOrbit ||
            orbit && !route && !freeOrbit ||
            camera is not ("default" or "farthest" or "default-low-orbit" or "farthest-low-orbit"))
            throw new ArgumentException("镜头参数只用于路线诊断或节点/线路自由环绕；支持 default、farthest、default-low-orbit、farthest-low-orbit。");
    }

    internal static int ParseNodeReadyHold(string scenario, string? value)
    {
        if (value is null) return 0;
        if (!int.TryParse(value, out var seconds) || seconds != 60 ||
            scenario is not ("scene-node" or "scene-node-first" or "scene-memory" or "scene-reference"))
            throw new ArgumentException("节点 Ready 保持仅支持节点/内存/参考情景，时长必须为 60 秒。");
        return seconds;
    }

    internal static int ReadyObservationSeconds(string scenario, WorldObjectId target, NodeId node, int nodeHold, bool lowOrbit) =>
        lowOrbit ? 60 : target is WorldObjectId.Node held && held.Id == node && nodeHold > 0
            ? nodeHold : scenario == "scene-reference" ? 10 : 5;

    internal static void ConfigureLowOrbitCamera(WorldCamera rig, bool farthest)
    {
        rig.ShakeEnabled = false;
        rig.Orbit(0, -1000);
        rig.Zoom(100);
        rig.Zoom(farthest ? -100 : -MathF.Log(48f / 8f) / .15f);
    }

    private void BeginLowOrbit()
    {
        var rig = _app.WorldScenes!.View.CameraRig;
        ConfigureLowOrbitCamera(rig, _streamingCamera == "farthest-low-orbit");
        _lowOrbitStarted = _lowOrbitLastDraw = Seconds();
        _lowOrbitLastSample = double.NegativeInfinity;
        _lowOrbitRequestedRadians = 0;
        _sceneEvidence["low_orbit"] = new { camera = _streamingCamera, requested_pitch_radians = .15,
            period_seconds = LowOrbitPeriodSeconds, camera_shake = false,
            actual_camera = IsRouteMeasurement ? "route-orbit.jsonl and streaming-frames.jsonl" : "scene-operations.jsonl:low-orbit-sample",
            completion = "fixed observation window; no global terrain completion assertion" };
    }

    private void AdvanceLowOrbit(double now)
    {
        if (!IsLowOrbit || _lowOrbitStarted == 0) return;
        // Orbit accepts mouse displacement; 0.006 radians per pixel is the existing camera input scale.
        var delta = (now - _lowOrbitLastDraw) * Math.Tau / LowOrbitPeriodSeconds;
        _app.WorldScenes!.View.CameraRig.Orbit((float)(-delta / .006), 0);
        _lowOrbitRequestedRadians += delta;
        _lowOrbitLastDraw = now;
    }

    private void RecordRouteOrbit(double now)
    {
        if (_lowOrbitRouteTrace is null || now - _lowOrbitLastSample < ViewCoverage.IntervalSeconds) return;
        _lowOrbitLastSample = now;
        var rig = _app.WorldScenes!.View.CameraRig;
        var offset = rig.Camera.Position - rig.Focus;
        var status = _app.Host!.GetStatus()!;
        _lowOrbitRouteTrace.WriteLine(JsonSerializer.Serialize(new
        {
            draw = _routeFrames + 1, elapsed_seconds = now - _lowOrbitStarted,
            frame = _app.WorldScenes.Display?.DisplayedFrame?.Update.Frame,
            steps = status.View.Position.CompletedSteps, speed = status.Speed.ToString(), paused = status.IsPaused,
            requested_yaw = rig.OrbitYawRadians, requested_pitch = rig.OrbitPitchRadians,
            requested_period_seconds = LowOrbitPeriodSeconds,
            cumulative_requested_orbit_radians = _lowOrbitRequestedRadians,
            camera = new RouteCameraMeasurement(Point(rig.Camera.Position), Point(rig.Focus), offset.Length(),
                Math.Atan2(offset.X, offset.Z), Math.Atan2(offset.Y, Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z)),
                rig.Camera.Near, rig.Camera.Far)
        }, StreamingJson));
    }

    private void RecordFreeOrbit(double now)
    {
        if (now - _lowOrbitLastSample < ViewCoverage.IntervalSeconds) return;
        _lowOrbitLastSample = now;
        var display = _app.WorldScenes!.Display;
        var frame = display?.DisplayedFrame;
        var rig = _app.WorldScenes!.View.CameraRig;
        var offset = rig.Camera.Position - rig.Focus;
        var status = _app.Host!.GetStatus()!;
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "low-orbit-sample", operation = _sceneOperation, at = now,
            elapsed_seconds = now - _lowOrbitStarted, frame = frame?.Update.Frame,
            requested_period_seconds = LowOrbitPeriodSeconds,
            cumulative_requested_orbit_radians = _lowOrbitRequestedRadians, steps = status.View.Position.CompletedSteps,
            speed = status.Speed.ToString(), paused = status.IsPaused,
            camera = new RouteCameraMeasurement(Point(rig.Camera.Position), Point(rig.Focus), offset.Length(),
                Math.Atan2(offset.X, offset.Z), Math.Atan2(offset.Y, Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z)),
                rig.Camera.Near, rig.Camera.Far),
            requested_yaw = rig.OrbitYawRadians, requested_pitch = rig.OrbitPitchRadians,
            coverage = frame is not null ? ViewCoverage.Sample(frame, CoverageRays(display!)) : null
        }, SceneJson));
    }
}

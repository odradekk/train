using System.Runtime.CompilerServices;
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
    private StreamWriter? _routeTrace, _routeRanges;
    private ISceneFrame? _routeSceneFrame;
    private readonly Dictionary<SceneObjectId, SceneObject> _routeObjects = [];
    private readonly ConditionalWeakTable<SceneMesh, RouteBounds> _routeMeshBounds = new();
    private readonly Dictionary<VehicleId, double?> _routeLengths = [];
    private RouteRanges? _routeBuilt, _routeShown;
    private long _routeStateId, _routeFrames;
    private double _routeStarted, _routeLastDraw, _routeLastCapture, _routePresentedCost;
    private ulong _routeLastSteps;
    private SceneSpaceId? _routeLastSpace;
    private int _routeCaptureBurst;
    private bool _routeSampling;

    private async Task RunFollowRoute()
    {
        var directory = Path.GetDirectoryName(_output)!;
        using var frames = new StreamWriter(Path.Combine(directory, "route-frames.jsonl"));
        using var ranges = new StreamWriter(Path.Combine(directory, "route-ranges.jsonl"));
        _routeTrace = frames; _routeRanges = ranges;
        try
        {
            var host = _app.Host!;
            var speed = _scenario.EndsWith("8x", StringComparison.Ordinal) ? SimulationSpeed.Eight : SimulationSpeed.One;
            var train = ((Lookup<TrainView>.Found)host.GetStatus()!.View.FindTrain(new(1))).Value;
            if (train.Vehicles.IsEmpty) throw new InvalidDataException("路线测量需要列车1的现有编组。");
            _subject = new WorldObjectId.Instance(new InstanceId.Vehicle(train.Vehicles[0]));
            _sceneTarget = new WorldObjectId.Train(train.Id);
            host.SetSpeed(speed);
            host.SetPaused(false);
            if (host.Submit(host.GetStatus()!.Binding, new CoreCommand.StartTrain(train.Id), CommandSource.AutomationTest)
                is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted })
                throw new InvalidOperationException("无法启动测量列车。");
            _app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").RaiseObjectClicked(_sceneTarget);
            _routeStarted = _routeLastDraw = Seconds();
            _routeLastSteps = host.GetStatus()!.View.Position.CompletedSteps;
            _sceneTiming = new(_routeStarted, _routeLastSteps);
            _routeSampling = true;
            EdgeId? first = null, second = null;
            NodeId? middle = null, finalNode = null;
            double? coreCompletedAt = null;
            var routeCompleted = false;
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
                if (coreCompletedAt is not null && _routeSceneFrame is { } latest &&
                    _app.WorldScenes!.Display?.DisplayedFrame is { } drawn && drawn.Update.Frame.Space == latest.Update.Frame.Space &&
                    drawn.FindPose(_subject) is Lookup<ScenePose>.Found finalPose &&
                    drawn.Locate(finalPose.Value.Position) is Lookup<WorldLocation>.Found { Value: WorldLocation.Node atNode } && atNode.Id == finalNode)
                { routeCompleted = true; break; }
                if (coreCompletedAt is { } ended && Seconds() - ended > 30) throw new TimeoutException("核心路线完成后30秒内未取得终点新场景显示帧。");
                if (Seconds() - _routeStarted > 3300) throw new TimeoutException("整条路线在3300秒内未完成。");
                if (_sceneDuration > 0 && Seconds() - _routeStarted >= _sceneDuration) break;
            }
            _routeSampling = false;
            _timing = _sceneTiming.Summarize();
            _sceneTiming = null;
            frames.Flush(); ranges.Flush();
            _sceneEvidence["route"] = new
            {
                train = train.Id.Value, vehicle = train.Vehicles[0].Value, first_edge = first?.Value, middle_node = middle?.Value,
                second_edge = second?.Value, transitions, route_completed = routeCompleted, preview_seconds = _sceneDuration, frames = _routeFrames,
                analysis = FollowRouteAnalysis.Analyze(directory).Summary,
                elapsed_seconds = _timing.ElapsedSeconds, capture_only = _scenario.Contains("capture", StringComparison.Ordinal),
                absolute_start_seconds = _routeStarted, clock_frequency = System.Diagnostics.Stopwatch.Frequency,
                endpoint = "first edge, full node traversal, full following edge; ends on next node",
                files = new[] { "route-frames.jsonl", "route-ranges.jsonl", "route-analysis.json" }
            };
        }
        finally { _routeSampling = false; _routeTrace = _routeRanges = null; }
    }

    private void RecordRoutePresented(ISceneFrame frame)
    {
        if (!_routeSampling) return;
        var started = Seconds();
        _routeSceneFrame = frame;
        _routeObjects.Clear();
        if (frame.Update.Base is { } baseline)
        {
            if (_app.WorldScenes!.Display?.DisplayedFrame?.Update.Frame != baseline) { _routeBuilt = null; return; }
            foreach (var item in _app.WorldScenes.Display.Objects.Values)
                if (item.Source.Parent is null && item.Source.Subject is null)
                    _routeObjects.Add(item.Source.Id, item.Source with { Pose = item.Source.Pose with
                    { Position = item.Source.Pose.Position + frame.Update.ShiftExistingRoots } });
        }
        foreach (var id in frame.Update.Removed) _routeObjects.Remove(id);
        foreach (var item in frame.Update.Upserts)
            if (item.Parent is null && item.Subject is null) _routeObjects[item.Id] = item;
        _routeBuilt = RecordRouteRanges(_routeBuilt, frame, frame.Update.Frame.Space, frame.Origin, NVector.Zero, _routeObjects.Values);
        _routePresentedCost += Seconds() - started;
    }

    private RouteRanges RecordRouteRanges(RouteRanges? previous, ISceneFrame frame, SceneSpaceId coordinates,
        ScenePoint origin, NVector shift, IEnumerable<SceneObject> objects)
    {
        var terrain = new List<RouteRange>(); var rails = new List<RouteRange>();
        foreach (var item in objects)
        {
            if (item.Parent is not null || item.Subject is not null) continue;
            for (var index = 0; index < item.Visuals.Length; index++)
            {
                if (item.Visuals[index] is not SceneVisual.Geometry visual || visual.Mesh.Vertices.IsEmpty) continue;
                var rail = visual.Mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel };
                if (!rail && visual.Mesh.Material is SceneMaterial.BuiltIn) continue;
                var local = _routeMeshBounds.GetValue(visual.Mesh, mesh =>
                {
                    var min = new NVector(float.PositiveInfinity); var max = new NVector(float.NegativeInfinity);
                    foreach (var vertex in mesh.Vertices) { min = NVector.Min(min, vertex.Position); max = NVector.Max(max, vertex.Position); }
                    return new(new(min.X, min.Y, min.Z), new(max.X, max.Y, max.Z));
                });
                var minimum = new NVector(float.PositiveInfinity); var maximum = new NVector(float.NegativeInfinity);
                foreach (var x in new[] { local.Minimum.X, local.Maximum.X })
                foreach (var y in new[] { local.Minimum.Y, local.Maximum.Y })
                foreach (var z in new[] { local.Minimum.Z, local.Maximum.Z })
                {
                    var point = NVector.Transform(new((float)x, (float)y, (float)z), item.Pose.Rotation) + item.Pose.Position + shift;
                    minimum = NVector.Min(minimum, point); maximum = NVector.Max(maximum, point);
                }
                (rail ? rails : terrain).Add(new(item.Id.Value, index,
                    new(RouteAbsolute(minimum, origin), RouteAbsolute(maximum, origin))));
            }
        }
        var fog = frame.Update.Environment.FoggedRegions.Select(bounds => new RouteBounds(
            RouteAbsolute(bounds.Minimum + shift, origin), RouteAbsolute(bounds.Maximum + shift, origin))).ToArray();
        if (previous is not null && previous.Coordinates == coordinates && previous.Terrain.SequenceEqual(terrain) &&
            previous.Rails.SequenceEqual(rails) && previous.Fog.SequenceEqual(fog)) return previous;
        var state = new RouteRanges(++_routeStateId, frame.Update.Frame, coordinates, terrain.ToArray(), rails.ToArray(), fog);
        _routeRanges!.WriteLine(JsonSerializer.Serialize(state, SceneJson));
        return state;
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
            var now = Seconds(); var interval = now - _routeLastDraw;
            var completed = status.View.Position.CompletedSteps; var stepDelta = completed - _routeLastSteps;
            var frame = _routeSceneFrame; var display = _app.WorldScenes!.Display;
            var train = ((Lookup<TrainView>.Found)status.View.FindTrain(new(1))).Value;
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
                        var rotation = pose.Value.Rotation;
                        vehicles.Add(new(id.Value, RouteAbsolute(pose.Value.Position, frame.Origin), [rotation.X, rotation.Y, rotation.Z, rotation.W], length));
                    }
                    var shown = display?.Objects.Values.FirstOrDefault(item => item.Source.Parent is null && item.Source.Subject == subject);
                    if (shown is not null && display!.PoseFrame?.Update.Frame.Space == frame.Update.Frame.Space)
                    {
                        var transform = _app.WorldScenes.View.GlobalTransform.AffineInverse() * shown.Root.GlobalTransform;
                        var rotation = transform.Basis.GetRotationQuaternion();
                        displayedVehicles.Add(new(id.Value, RouteAbsolute(new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z), frame.Origin),
                            [rotation.X, rotation.Y, rotation.Z, rotation.W], length));
                    }
                }
                if (display?.DisplayedFrame is { } drawn)
                    _routeShown = drawn.Update.Frame == frame.Update.Frame && display.Root.Position == Vector3.Zero ? _routeBuilt :
                        RecordRouteRanges(_routeShown, drawn, frame.Update.Frame.Space, frame.Origin,
                            new(display.Root.Position.X, display.Root.Position.Y, display.Root.Position.Z), display.Objects.Values.Select(item => item.Source));
            }
            var newSpace = frame is not null && frame.Update.Frame.Space != _routeLastSpace;
            if (newSpace) { _routeLastSpace = frame!.Update.Frame.Space; _routeCaptureBurst = 6; }
            var sample = new RouteFrame(++_routeFrames, now - _routeStarted, interval, completed, stepDelta, status.Speed.ToString(),
                frame?.Update.Frame, display?.DisplayedFrame?.Update.Frame, display?.PoseFrame?.Update.Frame,
                _routeBuilt?.Id, _routeShown?.Id, vehicles.ToArray(), displayedVehicles.ToArray(), train.OrderFacing == TrainOrderFacing.FirstEndLeading,
                JsonSerializer.SerializeToElement(train.Position, train.Position.GetType(), SceneJson), _simulationWork, Seconds() - started + _routePresentedCost,
                train.Vehicles.Select(id => id.Value).ToArray());
            _routeTrace!.WriteLine(JsonSerializer.Serialize(sample, SceneJson));
            _routePresentedCost = 0;
            if (_scenario.Contains("capture", StringComparison.Ordinal) && (now - _routeLastCapture >= .5 || _routeCaptureBurst > 0))
            {
                using var image = _app.GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(Path.GetDirectoryName(_output)!, $"route-{_routeFrames:D6}.png")) != Error.Ok)
                    throw new IOException("路线截图保存失败。");
                _routeLastCapture = now; _routeCaptureBurst--;
            }
            _sceneTiming!.AddFrame(now, completed, _simulationWork, status.Speed, status.IsPaused);
            _simulationWork = 0; _routeLastSteps = completed; _routeLastDraw = now;
        }
        catch (Exception error) { _drawFailure = error; }
        finally
        {
            var ended = Seconds();
            _sceneProbeCosts.Add(new("route-observer", _routeFrames, started, ended, 0, 0, 0, 0, ended - started, 0));
        }
    }
}

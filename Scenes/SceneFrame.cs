using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record RailwayArcRange(double Begin, double End, double Width);
internal sealed record SceneFog(SceneBounds Bounds, RailwayArcRange? Arc = null, ScenePose? Pose = null)
{
    internal SceneBounds WorldBounds
    {
        get
        {
            if (Pose is null) return Bounds;
            var minimum = new Vector3(float.MaxValue);
            var maximum = new Vector3(float.MinValue);
            foreach (var x in new[] { Bounds.Minimum.X, Bounds.Maximum.X })
                foreach (var y in new[] { Bounds.Minimum.Y, Bounds.Maximum.Y })
                    foreach (var z in new[] { Bounds.Minimum.Z, Bounds.Maximum.Z })
                    {
                        var point = Pose.Position + Vector3.Transform(new Vector3(x, y, z), Pose.Rotation);
                        minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
                    }
            return new(minimum, maximum);
        }
    }
}

internal sealed class SceneFrame : ISceneFrame
{
    internal static (SceneWorldSnapshot? Previous, double Seconds) SampleTime(SceneWorldSnapshot snapshot,
        SceneWorldSnapshot? previous, double interpolation)
    {
        if (previous is null || previous.Core.Position.CompletedSteps + 1 != snapshot.Core.Position.CompletedSteps)
            return (null, snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds);
        return (previous, (previous.Core.Position.CompletedSteps + interpolation) * SimulationTime.StepSeconds);
    }

    private readonly ImmutableArray<PickGrid> picking;
    private readonly ImmutableDictionary<WorldObjectId, ScenePose> poses;
    private readonly RailwayPath? railway;
    private readonly ImmutableArray<RailwaySegment> segments;
    private readonly ImmutableArray<SceneFog> fog;
    private readonly NodeLayout? node;
    private readonly ImmutableArray<NodeScene.Part> nodeParts;
    internal RailwayPath? Railway => railway;
    internal NodeLayout? Node => node;
    internal ImmutableArray<SceneObject> Objects { get; }
    public ScenePoint Origin { get; }
    public ScenePanBounds? NodePanBounds => node?.PanBounds(Origin);
    public SceneReadiness Readiness { get; }
    public double SimulationSeconds { get; }
    public SceneUpdate Update { get; }
    public ImmutableArray<SceneProblem> Diagnostics { get; }
    public ImmutableArray<SceneDebugLine> DebugLines { get; }

    internal SceneFrame(SceneFrameStamp stamp, ScenePoint origin, double seconds,
        SceneReadiness readiness, ImmutableArray<SceneObject> objects, ImmutableArray<PickGrid> picking,
        ImmutableArray<SceneFog> fog, ImmutableArray<SceneProblem> diagnostics, SceneFrame? displayed = null,
        RailwayPath? railway = null, ImmutableArray<RailwaySegment> segments = default,
        NodeLayout? node = null, ImmutableArray<NodeScene.Part> nodeParts = default,
        SceneDebugLayer debugLayers = SceneDebugLayer.None, ImmutableDictionary<EdgeId, int>? nodeTracks = null,
        ScenePickRaySample? pickRay = null)
    {
        Origin = origin;
        SimulationSeconds = seconds;
        Readiness = readiness;
        Objects = objects;
        this.picking = picking;
        Diagnostics = diagnostics;
        this.railway = railway;
        this.node = node;
        this.nodeParts = nodeParts.IsDefault ? [] : nodeParts;
        this.segments = segments.IsDefault ? [] : segments;
        this.fog = fog;
        DebugLines = debugLayers == SceneDebugLayer.None ? [] :
            SceneDebugLines.Generate(debugLayers, picking, origin, railway, this.segments, node, nodeTracks, stamp, pickRay);
        var environment = new SceneEnvironment(fog.Select(item => item.WorldBounds).ToImmutableArray(), []);
        var byId = objects.ToDictionary(item => item.Id);
        var composed = new Dictionary<SceneObjectId, ScenePose>();
        var visiting = new HashSet<SceneObjectId>();
        var subjects = ImmutableDictionary.CreateBuilder<WorldObjectId, ScenePose>();
        foreach (var item in objects)
        {
            var pose = Compose(item.Id);
            if (item.Subject is not null && (item.Parent is null || !subjects.ContainsKey(item.Subject))) subjects[item.Subject] = pose;
        }
        poses = subjects.ToImmutable();
        var previous = displayed is not null && displayed.Update.Frame.Session == stamp.Session &&
            displayed.Update.Frame.Request == stamp.Request && displayed.Update.Frame.Space == stamp.Space ? displayed : null;
        var shift = previous is null ? Vector3.Zero : new Vector3(
            (float)(previous.Origin.X - origin.X), (float)(previous.Origin.Y - origin.Y), (float)(previous.Origin.Z - origin.Z));
        var oldObjects = previous?.Objects.ToDictionary(item => item.Id);
        var upserts = previous is null ? objects : objects.Where(item => !oldObjects!.TryGetValue(item.Id, out var old) ||
            item != (old.Parent is null
                ? old with { Pose = old.Pose with { Position = old.Pose.Position + shift } }
                : old)).ToImmutableArray();
        var removed = previous is null ? ImmutableArray<SceneObjectId>.Empty : previous.Objects
            .Where(item => !byId.ContainsKey(item.Id)).Select(item => item.Id).ToImmutableArray();
        Update = new(previous?.Update.Frame, stamp, shift, upserts, removed, environment, []);

        ScenePose Compose(SceneObjectId id)
        {
            if (composed.TryGetValue(id, out var pose)) return pose;
            if (!visiting.Add(id) || !byId.TryGetValue(id, out var item))
                throw new ArgumentException("Scene parents must exist and be acyclic.", nameof(objects));
            SceneGeometry.Validate(item.Pose);
            pose = item.Parent is { } parent ? SceneGeometry.Compose(Compose(parent), item.Pose) : item.Pose;
            visiting.Remove(id);
            composed.Add(id, pose);
            return pose;
        }
    }

    public ScenePick Pick(SceneRay ray)
    {
        SceneGeometry.Validate(ray);
        SceneHit? nearest = null;
        foreach (var item in picking)
        {
            var inverse = Quaternion.Conjugate(item.Pose.Rotation);
            var local = new SceneRay(Vector3.Transform(ray.Origin - item.Pose.Position, inverse),
                Vector3.Transform(ray.Direction, inverse), nearest?.DistanceCells ?? ray.MaximumDistanceCells);
            if (item.Grid.Pick(local) is not { } hit) continue;
            if (nearest is not null && hit.Distance >= nearest.DistanceCells) continue;
            nearest = new(Update.Frame, item.Object, hit.Cell.Subject!, hit.Cell.At, hit.Face,
                ray.Origin + ray.Direction * (float)hit.Distance,
                Vector3.Transform(SceneGeometry.Normal(hit.Face), item.Pose.Rotation), hit.Distance, hit.Cell.Path);
        }
        foreach (var region in fog)
        {
            var cast = region.Pose is { } pose ? new SceneRay(
                Vector3.Transform(ray.Origin - pose.Position, Quaternion.Conjugate(pose.Rotation)),
                Vector3.Transform(ray.Direction, Quaternion.Conjugate(pose.Rotation)), ray.MaximumDistanceCells) : ray;
            if (SceneGeometry.Intersect(cast, region.Bounds, out var entry, out _, out var exit) &&
                (nearest is null || entry <= nearest.DistanceCells) &&
                (region.Pose is not null || region.Arc is null || railway!.IntersectsArcRange(ray, Origin,
                    region.Arc, entry, Math.Min(exit, nearest?.DistanceCells ?? exit))))
                return new ScenePick.NotReady();
        }
        return nearest is null ? new ScenePick.Miss() : new ScenePick.Hit(nearest);
    }

    public Lookup<ScenePose> FindPose(WorldObjectId subject) => poses.TryGetValue(subject, out var pose)
        ? new Lookup<ScenePose>.Found(pose) : new Lookup<ScenePose>.Missing();
    public Lookup<WorldLocation> Locate(Vector3 point)
    {
        if (!SceneGeometry.Finite(point)) throw new ArgumentException("Point must be finite.", nameof(point));
        if (node is not null)
        {
            foreach (var part in nodeParts)
            {
                var x = Math.Floor(point.X + Origin.X - part.Origin.X);
                var z = Math.Floor(point.Z + Origin.Z - part.Origin.Z);
                if (x >= int.MinValue && x <= int.MaxValue && z >= int.MinValue && z <= int.MaxValue &&
                    part.Heights.ContainsKey(new((int)x, (int)z))) return new Lookup<WorldLocation>.Found(new WorldLocation.Node(node.Node));
            }
            return new Lookup<WorldLocation>.Missing();
        }
        if (railway is null) return new Lookup<WorldLocation>.Missing();
        var distance = railway.Locate(RailwayCoordinates.Add(Origin, point));
        var index = Math.Clamp((int)(distance / RailwayCoordinates.SegmentLengthCells), 0, railway.SegmentCount - 1);
        return segments.Any(segment => segment.Index == index)
            ? new Lookup<WorldLocation>.Found(new WorldLocation.Edge(railway.Edge.Id, distance / RailwayCoordinates.CellsPerMeter)) : new Lookup<WorldLocation>.Missing();
    }
    public Lookup<double> TerrainHeight(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentException("Coordinates must be finite.");
        var point = new ScenePoint(x + Origin.X, 0, z + Origin.Z);
        if (node is not null)
        {
            foreach (var part in nodeParts)
            {
                var localX = Math.Floor(point.X - part.Origin.X);
                var localZ = Math.Floor(point.Z - part.Origin.Z);
                if (localX >= int.MinValue && localX <= int.MaxValue && localZ >= int.MinValue && localZ <= int.MaxValue &&
                    part.Heights.TryGetValue(new((int)localX, (int)localZ), out var height)) return new Lookup<double>.Found(height - Origin.Y);
            }
            return new Lookup<double>.Missing();
        }
        foreach (var segment in segments)
        {
            var localX = Math.Floor(point.X - segment.Origin.X);
            var localZ = Math.Floor(point.Z - segment.Origin.Z);
            if (localX < int.MinValue || localX > int.MaxValue || localZ < int.MinValue || localZ > int.MaxValue) continue;
            var column = new GridColumn((int)localX, (int)localZ);
            if (segment.Heights.TryGetValue(column, out var height)) return new Lookup<double>.Found(height - Origin.Y);
        }
        return new Lookup<double>.Missing();
    }

    internal static ScenePoint? SwitchOrigin(SceneFrame previous, NodeLayout? nextNode, RailwayPath? nextRailway)
    {
        ScenePoint from, to;
        if (previous.railway is { } oldRailway && nextNode is not null &&
            (oldRailway.Start.Id == nextNode.Node || oldRailway.End.Id == nextNode.Node))
        {
            from = oldRailway.Sample(oldRailway.Start.Id == nextNode.Node ? 0 : oldRailway.LengthCells).Position;
            to = nextNode.EntryPoint(oldRailway.Edge.Id);
        }
        else if (previous.node is { } oldNode && nextRailway is not null &&
            (nextRailway.Start.Id == oldNode.Node || nextRailway.End.Id == oldNode.Node))
        {
            from = oldNode.EntryPoint(nextRailway.Edge.Id);
            to = nextRailway.Sample(nextRailway.Start.Id == oldNode.Node ? 0 : nextRailway.LengthCells).Position;
        }
        else return null;
        return new(previous.Origin.X + to.X - from.X, previous.Origin.Y + to.Y - from.Y, previous.Origin.Z + to.Z - from.Z);
    }
}

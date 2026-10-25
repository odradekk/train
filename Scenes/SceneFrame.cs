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
    private readonly NodeHeights nodeHeights;
    private readonly FarTerrainView? farTerrain;
    internal RailwayPath? Railway => railway;
    internal NodeLayout? Node => node;
    internal sealed class StaticGeometry
    {
        internal static StaticGeometry Empty { get; } = new();
        private readonly ImmutableSortedDictionary<string, SceneFrame> parts;
        private readonly ImmutableDictionary<SceneObjectId, SceneObject> objects;
        private readonly ImmutableDictionary<SceneObjectId, ScenePose> poses;
        private readonly ImmutableDictionary<WorldObjectId, ImmutableSortedDictionary<string, (ScenePose Pose, bool Root)>> subjects;
        private readonly ImmutableSortedDictionary<string, ImmutableArray<SceneProblem>> problems;
        private StaticGeometry? previous;
        private readonly ImmutableHashSet<SceneObjectId> changedIds;
        private ImmutableArray<SceneObject> allObjects;
        private ImmutableArray<SceneProblem> diagnostics;

        private StaticGeometry()
        {
            parts = ImmutableSortedDictionary.Create<string, SceneFrame>(StringComparer.Ordinal);
            objects = ImmutableDictionary<SceneObjectId, SceneObject>.Empty;
            poses = ImmutableDictionary<SceneObjectId, ScenePose>.Empty;
            subjects = ImmutableDictionary<WorldObjectId, ImmutableSortedDictionary<string, (ScenePose, bool)>>.Empty;
            problems = ImmutableSortedDictionary.Create<string, ImmutableArray<SceneProblem>>(StringComparer.Ordinal);
            changedIds = [];
        }

        private StaticGeometry(StaticGeometry previous,
            ImmutableSortedDictionary<string, SceneFrame> parts,
            ImmutableDictionary<SceneObjectId, SceneObject> objects,
            ImmutableDictionary<SceneObjectId, ScenePose> poses,
            ImmutableDictionary<WorldObjectId, ImmutableSortedDictionary<string, (ScenePose Pose, bool Root)>> subjects,
            ImmutableSortedDictionary<string, ImmutableArray<SceneProblem>> problems,
            ImmutableHashSet<SceneObjectId> changedIds)
        {
            this.previous = previous; this.parts = parts; this.objects = objects; this.poses = poses;
            this.subjects = subjects; this.problems = problems; this.changedIds = changedIds;
        }

        internal ImmutableArray<SceneObject> Objects => allObjects.IsDefault
            ? allObjects = parts.Values.SelectMany(part => part.dynamicObjects).ToImmutableArray() : allObjects;
        internal IEnumerable<PickGrid> Picking => parts.Values.SelectMany(part => part.picking);
        internal ImmutableArray<SceneProblem> Diagnostics => diagnostics.IsDefault
            ? diagnostics = problems.Values.SelectMany(items => items).ToImmutableArray() : diagnostics;
        internal bool TryGetObject(SceneObjectId id, out SceneObject value) => objects.TryGetValue(id, out value!);
        internal bool TryGetPose(SceneObjectId id, out ScenePose value) => poses.TryGetValue(id, out value!);
        internal Lookup<ScenePose> FindPose(WorldObjectId subject)
        {
            if (!subjects.TryGetValue(subject, out var entries)) return new Lookup<ScenePose>.Missing();
            var selected = entries.First().Value.Pose;
            foreach (var entry in entries.Values)
                if (entry.Root) selected = entry.Pose;
            return new Lookup<ScenePose>.Found(selected);
        }

        internal StaticGeometry Apply(IReadOnlyDictionary<string, RenderedDesign?> changes)
        {
            if (changes.Count == 0) return this;
            var nextParts = parts;
            var nextObjects = objects.ToBuilder();
            var nextPoses = poses.ToBuilder();
            var nextSubjects = subjects.ToBuilder();
            var nextProblems = problems;
            var changed = ImmutableHashSet.CreateBuilder<SceneObjectId>();
            foreach (var key in changes.Keys)
            {
                if (!parts.TryGetValue(key, out var old)) continue;
                foreach (var item in old.dynamicObjects)
                {
                    nextObjects.Remove(item.Id); nextPoses.Remove(item.Id); changed.Add(item.Id);
                }
                foreach (var subject in old.poses.Keys)
                {
                    var entries = nextSubjects[subject].Remove(key);
                    if (entries.IsEmpty) nextSubjects.Remove(subject);
                    else nextSubjects[subject] = entries;
                }
                nextParts = nextParts.Remove(key);
                nextProblems = nextProblems.Remove(key);
            }
            foreach (var (key, geometry) in changes)
            {
                if (geometry is null) continue;
                var part = new SceneFrame(new(default, default, default, 0), default, 0, SceneReadiness.Ready,
                    geometry.Objects, geometry.Picking, [], geometry.Diagnostics);
                foreach (var item in part.dynamicObjects)
                {
                    if (nextObjects.ContainsKey(item.Id))
                        throw new ArgumentException("Scene object IDs must be unique.", nameof(changes));
                    nextObjects.Add(item.Id, item); nextPoses.Add(item.Id, part.composed[item.Id]); changed.Add(item.Id);
                }
                var roots = part.dynamicObjects.Where(item => item.Parent is null && item.Subject is not null)
                    .Select(item => item.Subject!).ToHashSet();
                foreach (var (subject, pose) in part.poses)
                {
                    if (!nextSubjects.TryGetValue(subject, out var entries))
                        entries = ImmutableSortedDictionary.Create<string, (ScenePose Pose, bool Root)>(StringComparer.Ordinal);
                    nextSubjects[subject] = entries.SetItem(key, (pose, roots.Contains(subject)));
                }
                nextParts = nextParts.Add(key, part);
                if (!geometry.Diagnostics.IsEmpty) nextProblems = nextProblems.Add(key, geometry.Diagnostics);
            }
            return new(this, nextParts, nextObjects.ToImmutable(), nextPoses.ToImmutable(),
                nextSubjects.ToImmutable(), nextProblems, changed.ToImmutable());
        }

        internal void ForgetHistory() => previous = null;

        internal IEnumerable<SceneObjectId> ChangedSince(StaticGeometry? old)
        {
            if (ReferenceEquals(this, old)) return [];
            var changed = new HashSet<SceneObjectId>();
            for (StaticGeometry? current = this; current is not null; current = current.previous)
            {
                if (ReferenceEquals(current, old)) return changed;
                changed.UnionWith(current.changedIds);
            }
            return old is null ? objects.Keys : objects.Keys.Concat(old.objects.Keys).Distinct();
        }
    }

    private readonly StaticGeometry? staticGeometry;
    private readonly int staticPickOffset;
    private readonly ImmutableArray<SceneObject> dynamicObjects;
    private readonly Dictionary<SceneObjectId, SceneObject> byId;
    private readonly Dictionary<SceneObjectId, ScenePose> composed;
    private ImmutableArray<SceneObject> allObjects;
    internal ImmutableArray<SceneObject> Objects => allObjects.IsDefault
        ? allObjects = staticGeometry is null ? dynamicObjects : staticGeometry.Objects.AddRange(dynamicObjects)
        : allObjects;

    internal bool ContainsObject(SceneObjectId id) => byId.ContainsKey(id) || staticGeometry?.TryGetObject(id, out _) == true;

    internal IEnumerable<SceneObjectId> RemovedSince(SceneFrame previous)
    {
        var changed = staticGeometry is not null ? staticGeometry.ChangedSince(previous.staticGeometry) :
            previous.staticGeometry?.ChangedSince(null) ?? [];
        return previous.byId.Keys.Concat(changed).Distinct().Where(id => previous.ContainsObject(id) && !ContainsObject(id));
    }

    public ScenePoint Origin { get; }
    public ScenePanBounds? NodePanBounds => node?.PanBounds(Origin);
    public SceneNodeOverview? NodeOverview { get; }
    public SceneReadiness Readiness { get; }
    public double SimulationSeconds { get; }
    public SceneUpdate Update { get; }
    public ImmutableArray<SceneProblem> Diagnostics { get; }
    public ImmutableArray<SceneDebugLine> DebugLines { get; }

    internal SceneFrame(SceneFrameStamp stamp, ScenePoint origin, double seconds,
        SceneReadiness readiness, ImmutableArray<SceneObject> objects, ImmutableArray<PickGrid> picking,
        ImmutableArray<SceneFog> fog, ImmutableArray<SceneProblem> diagnostics, SceneFrame? displayed = null,
        RailwayPath? railway = null, ImmutableArray<RailwaySegment> segments = default,
        NodeLayout? node = null, NodeHeights? nodeHeights = null,
        SceneDebugLayer debugLayers = SceneDebugLayer.None, ImmutableDictionary<EdgeId, int>? nodeTracks = null,
        ScenePickRaySample? pickRay = null, ImmutableArray<SceneDebugLine> editLines = default,
        ImmutableArray<SceneEffectCue> effects = default, ImmutableArray<TerrainCollisionPatch> terrain = default,
        StaticGeometry? staticGeometry = null, int staticPickOffset = 0, FarTerrainView? farTerrain = null)
    {
        Origin = origin;
        this.farTerrain = farTerrain;
        SimulationSeconds = seconds;
        Readiness = readiness;
        this.staticGeometry = staticGeometry;
        this.staticPickOffset = staticPickOffset;
        dynamicObjects = objects;
        this.picking = picking;
        Diagnostics = diagnostics;
        this.railway = railway;
        this.node = node;
        NodeOverview = node?.Overview(origin);
        this.nodeHeights = nodeHeights ?? NodeHeights.Empty;
        this.segments = segments.IsDefault ? [] : segments;
        this.fog = fog;
        DebugLines = debugLayers == SceneDebugLayer.None ? [] :
            SceneDebugLines.Generate(debugLayers, Picking().ToImmutableArray(), origin, railway, this.segments, node, nodeTracks, stamp, pickRay);
        if (!editLines.IsDefaultOrEmpty) DebugLines = DebugLines.AddRange(editLines);
        var environment = new SceneEnvironment(fog.Select(item => item.WorldBounds).ToImmutableArray(),
            terrain.IsDefault ? [] : terrain);
        byId = objects.ToDictionary(item => item.Id);
        if (staticGeometry is not null && objects.Any(item => staticGeometry.TryGetObject(item.Id, out _)))
            throw new ArgumentException("Scene object IDs must be unique.", nameof(objects));
        composed = new Dictionary<SceneObjectId, ScenePose>();
        var visiting = new HashSet<SceneObjectId>();
        var subjects = ImmutableDictionary.CreateBuilder<WorldObjectId, ScenePose>();
        foreach (var item in objects)
        {
            var pose = Compose(item.Id);
            if (item.Subject is not null && (item.Parent is null || !subjects.ContainsKey(item.Subject) && staticGeometry?.FindPose(item.Subject) is not Lookup<ScenePose>.Found)) subjects[item.Subject] = pose;
        }
        poses = subjects.ToImmutable();
        var previous = displayed is not null && displayed.Update.Frame.Session == stamp.Session &&
            displayed.Update.Frame.Request == stamp.Request && displayed.Update.Frame.Space == stamp.Space ? displayed : null;
        var shift = previous is null ? Vector3.Zero : new Vector3(
            (float)(previous.Origin.X - origin.X), (float)(previous.Origin.Y - origin.Y), (float)(previous.Origin.Z - origin.Z));
        var upserts = ImmutableArray.CreateBuilder<SceneObject>();
        var removed = ImmutableArray.CreateBuilder<SceneObjectId>();
        if (previous is null) upserts.AddRange(Objects);
        else
        {
            IEnumerable<SceneObjectId> staticChanges = staticGeometry is not null
                ? staticGeometry.ChangedSince(previous.staticGeometry)
                : previous.staticGeometry?.ChangedSince(null) ?? [];
            if (shift != Vector3.Zero)
                staticChanges = (staticGeometry?.ChangedSince(null) ?? [])
                    .Concat(previous.staticGeometry?.ChangedSince(null) ?? []);
            foreach (var id in byId.Keys.Concat(previous.byId.Keys).Concat(staticChanges).Distinct())
            {
                if (!TryGetObject(id, out var item))
                {
                    if (previous.ContainsObject(id)) removed.Add(id);
                    continue;
                }
                if (!previous.TryGetObject(id, out var old) || item != (old.Parent is null && shift != Vector3.Zero
                    ? old with { Pose = old.Pose with { Position = old.Pose.Position + shift } } : old)) upserts.Add(item);
            }
        }
        Update = new(previous?.Update.Frame, stamp, shift, upserts.ToImmutable(), removed.ToImmutable(), environment, effects.IsDefault ? [] : effects);
        if (displayed?.staticGeometry is { } confirmed) confirmed.ForgetHistory();

        ScenePose Compose(SceneObjectId id)
        {
            if (composed.TryGetValue(id, out var pose)) return pose;
            if (staticGeometry is not null && staticGeometry.TryGetPose(id, out pose)) return pose;
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
        foreach (var item in Picking())
        {
            var inverse = Quaternion.Conjugate(item.Pose.Rotation);
            var local = new SceneRay(Vector3.Transform(ray.Origin - item.Pose.Position, inverse),
                Vector3.Transform(ray.Direction, inverse), nearest?.DistanceCells ?? ray.MaximumDistanceCells);
            if (item.Grid.Pick(local) is not { } hit) continue;
            if (nearest is not null && hit.Distance >= nearest.DistanceCells) continue;
            var subject = item.ResolveSubject is { } resolve ? resolve(hit.Cell.At) : hit.Cell.Subject!;
            nearest = new(Update.Frame, item.Object, subject, hit.Cell.At, hit.Face,
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
        if (farTerrain?.Intersect(ray, Origin, nearest?.DistanceCells ?? ray.MaximumDistanceCells) is { } distance &&
            (nearest is null || distance < nearest.DistanceCells)) return new ScenePick.NotReady();
        return nearest is null ? new ScenePick.Miss() : new ScenePick.Hit(nearest);
    }

    private IEnumerable<PickGrid> Picking() => staticGeometry is null ? picking :
        picking.Take(staticPickOffset).Concat(staticGeometry.Picking).Concat(picking.Skip(staticPickOffset));

    private bool TryGetObject(SceneObjectId id, out SceneObject value) => byId.TryGetValue(id, out value!) ||
        staticGeometry is not null && staticGeometry.TryGetObject(id, out value!);

    public Lookup<ScenePose> FindPose(WorldObjectId subject) => poses.TryGetValue(subject, out var pose)
        ? new Lookup<ScenePose>.Found(pose) : staticGeometry?.FindPose(subject) ?? new Lookup<ScenePose>.Missing();
    public Lookup<WorldLocation> Locate(Vector3 point)
    {
        if (!SceneGeometry.Finite(point)) throw new ArgumentException("Point must be finite.", nameof(point));
        if (node is not null)
        {
            return nodeHeights.At(point.X + Origin.X, point.Z + Origin.Z) is not null
                ? new Lookup<WorldLocation>.Found(new WorldLocation.Node(node.Node)) : new Lookup<WorldLocation>.Missing();
        }
        if (railway is null) return new Lookup<WorldLocation>.Missing();
        var distance = railway.Locate(RailwayCoordinates.Add(Origin, point));
        var index = Math.Clamp((int)(distance / RailwayCoordinates.SegmentLengthCells), 0, railway.SegmentCount - 1);
        return segments.Any(segment => segment.Index == index)
            ? new Lookup<WorldLocation>.Found(new WorldLocation.Edge(railway.Edge.Id, distance / RailwayCoordinates.CellsPerMeter)) : new Lookup<WorldLocation>.Missing();
    }
    public Lookup<double> DisplayTerrainHeight(double x, double z) =>
        TerrainHeight(x, z) is Lookup<double>.Found found ? found :
            farTerrain?.Height(new(x + Origin.X, 0, z + Origin.Z)) is { } height
                ? new Lookup<double>.Found(height - Origin.Y) : new Lookup<double>.Missing();

    public Lookup<double> TerrainHeight(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentException("Coordinates must be finite.");
        var point = new ScenePoint(x + Origin.X, 0, z + Origin.Z);
        if (node is not null)
        {
            return nodeHeights.At(point.X, point.Z) is { } height
                ? new Lookup<double>.Found(height - Origin.Y) : new Lookup<double>.Missing();
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

using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed class RouteRangeRecorder
{
    private sealed record LocalBounds(Vector3 Minimum, Vector3 Maximum);
    private sealed record CachedVisual(SceneMesh Mesh, bool Rail, RouteRange Range);
    private sealed record CachedObject(ScenePoint Position, Quaternion Rotation, Dictionary<int, CachedVisual> Visuals);
    private readonly ConditionalWeakTable<SceneMesh, LocalBounds> meshBounds = new();
    private Dictionary<ulong, CachedObject> current = [];
    private (SceneSessionId Session, SceneRequestId Request, SceneSpaceId Space, SceneSpaceId Coordinates)? identity;
    private RouteBounds[] fog = [];
    private ImmutableArray<SceneBounds> inputFog = [];
    private ScenePoint fogTranslation;
    internal long? Id { get; private set; }

    internal RouteRangeChange? Update(long nextId, ISceneFrame frame, SceneSpaceId coordinates,
        ScenePoint origin, Vector3 shift, IEnumerable<SceneObject> objects)
    {
        var stamp = frame.Update.Frame;
        var nextIdentity = (stamp.Session, stamp.Request, stamp.Space, coordinates);
        var reset = identity != nextIdentity;
        if (reset) current.Clear();
        var next = new Dictionary<ulong, CachedObject>();
        var terrain = new List<RouteRange>();
        var rails = new List<RouteRange>();
        var removed = new HashSet<RouteRangeKey>();
        foreach (var item in objects)
        {
            if (item.Parent is not null || item.Subject is not null) continue;
            var position = new ScenePoint(origin.X + ((double)item.Pose.Position.X + shift.X),
                origin.Y + ((double)item.Pose.Position.Y + shift.Y), origin.Z + ((double)item.Pose.Position.Z + shift.Z));
            current.TryGetValue(item.Id.Value, out var old);
            var samePose = old is not null && old.Position == position && old.Rotation == item.Pose.Rotation;
            if (samePose && SameGeometry(item, old!))
            {
                next.Add(item.Id.Value, old!);
                continue;
            }
            Dictionary<int, CachedVisual>? visuals = null;
            for (var index = 0; index < item.Visuals.Length; index++)
            {
                if (RecordedMesh(item.Visuals[index]) is not { } mesh) continue;
                var rail = mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel };
                var previous = old?.Visuals.GetValueOrDefault(index);
                var range = samePose && previous is not null && ReferenceEquals(previous.Mesh, mesh) ? previous.Range :
                    new RouteRange(item.Id.Value, index, Transform(meshBounds.GetValue(mesh, Bounds), position, item.Pose.Rotation));
                (visuals ??= []).Add(index, new(mesh, rail, range));
                if (previous is null || previous.Rail != rail || previous.Range != range)
                {
                    if (previous is not null && previous.Rail != rail) removed.Add(new(item.Id.Value, index));
                    (rail ? rails : terrain).Add(range);
                }
            }
            if (visuals is not null) next.Add(item.Id.Value, new(position, item.Pose.Rotation, visuals));
        }
        foreach (var (id, item) in current)
            foreach (var index in item.Visuals.Keys)
                if (!next.TryGetValue(id, out var replacement) || !replacement.Visuals.ContainsKey(index)) removed.Add(new(id, index));
        var nextInputFog = frame.Update.Environment.FoggedRegions;
        var translation = new ScenePoint(origin.X + shift.X, origin.Y + shift.Y, origin.Z + shift.Z);
        var nextFog = fog;
        if (translation != fogTranslation || !nextInputFog.AsSpan().SequenceEqual(inputFog.AsSpan()))
        {
            nextFog = new RouteBounds[nextInputFog.Length];
            for (var index = 0; index < nextFog.Length; index++)
            {
                var bounds = nextInputFog[index];
                nextFog[index] = new(Absolute(bounds.Minimum), Absolute(bounds.Maximum));
            }
        }
        ScenePoint Absolute(Vector3 point) => new(translation.X + point.X, translation.Y + point.Y, translation.Z + point.Z);
        var fogChanged = reset || !fog.AsSpan().SequenceEqual(nextFog);
        inputFog = nextInputFog;
        fogTranslation = translation;
        current = next;
        identity = nextIdentity;
        if (!reset && terrain.Count == 0 && rails.Count == 0 && removed.Count == 0 && !fogChanged) return null;
        var change = new RouteRangeChange(nextId, reset ? null : Id, stamp, coordinates,
            [.. terrain], [.. rails], [.. removed], fogChanged ? nextFog : null);
        fog = nextFog;
        Id = nextId;
        return change;
    }

    private static bool SameGeometry(SceneObject item, CachedObject previous)
    {
        var count = 0;
        for (var index = 0; index < item.Visuals.Length; index++)
        {
            if (RecordedMesh(item.Visuals[index]) is not { } mesh) continue;
            if (!previous.Visuals.TryGetValue(index, out var visual) || !ReferenceEquals(visual.Mesh, mesh)) return false;
            count++;
        }
        return count == previous.Visuals.Count;
    }

    private static SceneMesh? RecordedMesh(SceneVisual visual) => visual is SceneVisual.Geometry { Mesh: var mesh } &&
        !mesh.Vertices.IsEmpty && mesh.Material is not SceneMaterial.BuiltIn { Kind: not BuiltInMaterial.RailSteel } ? mesh : null;

    private static LocalBounds Bounds(SceneMesh mesh)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var vertex in mesh.Vertices)
        {
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }
        return new(minimum, maximum);
    }

    private static RouteBounds Transform(LocalBounds bounds, ScenePoint position, Quaternion rotation)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var index = 0; index < 8; index++)
        {
            var point = Vector3.Transform(new Vector3((index & 1) == 0 ? bounds.Minimum.X : bounds.Maximum.X,
                (index & 2) == 0 ? bounds.Minimum.Y : bounds.Maximum.Y,
                (index & 4) == 0 ? bounds.Minimum.Z : bounds.Maximum.Z), rotation);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }
        return new(new(position.X + minimum.X, position.Y + minimum.Y, position.Z + minimum.Z),
            new(position.X + maximum.X, position.Y + maximum.Y, position.Z + maximum.Z));
    }
}

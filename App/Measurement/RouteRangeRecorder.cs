using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed class RouteRangeRecorder
{
    private sealed record LocalBounds(Vector3 Minimum, Vector3 Maximum);
    internal sealed record CachedVisual(bool Rail, RouteRange Range);
    internal sealed record CachedObject(Dictionary<int, CachedVisual> Visuals);
    internal sealed record Snapshot(ISceneFrame Frame, ImmutableDictionary<ulong, CachedObject> Objects,
        ImmutableHashSet<ulong> Changed, bool IsDelta);
    private readonly ConditionalWeakTable<SceneMesh, LocalBounds> meshBounds = new();
    private (SceneSessionId Session, SceneRequestId Request, SceneSpaceId Space, SceneSpaceId Coordinates)? identity;
    private RouteBounds[] fog = [];
    private ImmutableArray<SceneBounds> inputFog = [];
    private ScenePoint fogTranslation;
    internal long? Id { get; private set; }
    private Snapshot? recorded;
    private ScenePoint recordedTranslation;

    internal Snapshot? CapturePresented(ISceneFrame frame, Snapshot? displayed) =>
        frame.Update.Base is { } baseline && displayed?.Frame.Update.Frame != baseline ? null :
        Capture(frame, displayed, () => frame.Update.Upserts);

    internal Snapshot Capture(ISceneFrame frame, Snapshot? baseline, Func<IEnumerable<SceneObject>> fullSnapshot)
    {
        var delta = baseline is not null && frame.Update.Base == baseline.Frame.Update.Frame;
        var objects = (delta ? baseline!.Objects : ImmutableDictionary<ulong, CachedObject>.Empty).ToBuilder();
        var changed = ImmutableHashSet.CreateBuilder<ulong>();
        if (delta)
        {
            var shift = frame.Update.ShiftExistingRoots;
            var translation = new ScenePoint(frame.Origin.X - baseline!.Frame.Origin.X + shift.X,
                frame.Origin.Y - baseline.Frame.Origin.Y + shift.Y, frame.Origin.Z - baseline.Frame.Origin.Z + shift.Z);
            if (translation != default)
                foreach (var (id, item) in baseline.Objects)
                {
                    objects[id] = item with
                    {
                        Visuals = item.Visuals.ToDictionary(pair => pair.Key,
                            pair => pair.Value with { Range = Translate(pair.Value.Range, translation) })
                    };
                    changed.Add(id);
                }
            foreach (var id in frame.Update.Removed) { objects.Remove(id.Value); changed.Add(id.Value); }
        }
        foreach (var item in delta ? frame.Update.Upserts : fullSnapshot())
        {
            changed.Add(item.Id.Value);
            if (item.Parent is not null || item.Subject is not null) { objects.Remove(item.Id.Value); continue; }
            var position = new ScenePoint(frame.Origin.X + item.Pose.Position.X,
                frame.Origin.Y + item.Pose.Position.Y, frame.Origin.Z + item.Pose.Position.Z);
            var visuals = new Dictionary<int, CachedVisual>();
            for (var index = 0; index < item.Visuals.Length; index++)
            {
                if (RecordedMesh(item.Visuals[index]) is not { } mesh) continue;
                visuals.Add(index, new(mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel },
                    new(item.Id.Value, index, Transform(meshBounds.GetValue(mesh, Bounds), position, item.Pose.Rotation))));
            }
            if (visuals.Count == 0) objects.Remove(item.Id.Value);
            else objects[item.Id.Value] = new(visuals);
        }
        return new(frame, objects.ToImmutable(), changed.ToImmutable(), delta);
    }

    // changed 包含上一 P 的覆盖 ID、此间 D 的变化，以及本次 P 的覆盖 ID；P 的状态始终从 D 派生。
    internal RouteRangeChange? Record(long nextId, Snapshot snapshot, IEnumerable<ulong>? changed,
        SceneSpaceId coordinates, ScenePoint translation)
    {
        var frame = snapshot.Frame;
        var stamp = frame.Update.Frame;
        var nextIdentity = (stamp.Session, stamp.Request, stamp.Space, coordinates);
        var reset = identity != nextIdentity || recorded is null;
        var ids = reset || changed is null || recordedTranslation != translation
            ? snapshot.Objects.Keys.Concat(recorded?.Objects.Keys ?? []).Distinct()
            : changed.Distinct();
        var terrain = new List<RouteRange>();
        var rails = new List<RouteRange>();
        var removed = new HashSet<RouteRangeKey>();
        foreach (var id in ids)
        {
            snapshot.Objects.TryGetValue(id, out var item);
            CachedObject? old = null;
            if (!reset) recorded!.Objects.TryGetValue(id, out old);
            if (item is not null)
                foreach (var (index, visual) in item.Visuals)
                {
                    var range = Translate(visual.Range, translation);
                    var previous = old?.Visuals.GetValueOrDefault(index);
                    if (previous is not null && previous.Rail == visual.Rail &&
                        Translate(previous.Range, recordedTranslation) == range) continue;
                    if (previous is not null && previous.Rail != visual.Rail) removed.Add(new(id, index));
                    (visual.Rail ? rails : terrain).Add(range);
                }
            if (old is not null)
                foreach (var index in old.Visuals.Keys)
                    if (item is null || !item.Visuals.ContainsKey(index)) removed.Add(new(id, index));
        }
        var nextInputFog = frame.Update.Environment.FoggedRegions;
        var fogOffset = Add(frame.Origin, translation);
        var nextFog = fog;
        if (fogOffset != fogTranslation || !nextInputFog.AsSpan().SequenceEqual(inputFog.AsSpan()))
            nextFog = nextInputFog.Select(bounds => new RouteBounds(
                new(fogOffset.X + bounds.Minimum.X, fogOffset.Y + bounds.Minimum.Y, fogOffset.Z + bounds.Minimum.Z),
                new(fogOffset.X + bounds.Maximum.X, fogOffset.Y + bounds.Maximum.Y, fogOffset.Z + bounds.Maximum.Z))).ToArray();
        var fogChanged = reset || !fog.AsSpan().SequenceEqual(nextFog);
        inputFog = nextInputFog;
        fogTranslation = fogOffset;
        recorded = snapshot;
        recordedTranslation = translation;
        identity = nextIdentity;
        if (!reset && terrain.Count == 0 && rails.Count == 0 && removed.Count == 0 && !fogChanged) return null;
        var change = new RouteRangeChange(nextId, reset ? null : Id, stamp, coordinates,
            [.. terrain], [.. rails], [.. removed], fogChanged ? nextFog : null);
        fog = nextFog;
        Id = nextId;
        return change;
    }

    private static ScenePoint Add(ScenePoint point, ScenePoint offset) => new(point.X + offset.X, point.Y + offset.Y, point.Z + offset.Z);
    private static RouteRange Translate(RouteRange range, ScenePoint translation) => translation == default ? range :
        range with { Bounds = new(Add(range.Bounds.Minimum, translation), Add(range.Bounds.Maximum, translation)) };

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

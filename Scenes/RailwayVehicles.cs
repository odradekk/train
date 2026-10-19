using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayVehicles
{
    internal static int? Length(DesignEvaluation? evaluation) => evaluation switch
    {
        DesignEvaluation.Complete { Core.Design.Kind: DesignKind.Vehicle vehicle } => vehicle.LengthCells,
        DesignEvaluation.Partial { Metrics.VehicleLengthCells: ComputedValue<int>.Known length } => length.Value,
        _ => null
    };
    internal static BuiltObject Build(IContentView content, InstanceView instance)
    {
        var built = WorldSceneSession.BuildObject(content, instance, new(Vector3.Zero, Quaternion.Identity));
        var geometry = built.Geometry.Geometry;
        var keys = built.Geometry.Keys;
        if (content.FindDesign(instance.Design) is not ContentLookup<DesignEvaluation>.Found found ||
            found.Value is DesignEvaluation.Failed || instance.Details is not InstanceDetails.Vehicle vehicle) return built;
        var projection = DesignGeometry.Projection(found.Value);
        var destroyed = built.Destroyed;
        var removedPaths = projection.NestedObjects.Where(item => item.Details is NestedObjectDetails.FunctionalComposite composite &&
            destroyed.Contains(composite.Number)).Select(item => item.NestedItemIndices).ToArray();
        var removed = StructureDamage.Assess(instance, projection).Removed;
        var rows = projection.Voxels.Where(voxel => !voxel.Missing && !destroyed.Contains(voxel.Number) &&
            !removed.Contains(new(DesignGeometry.Key(voxel.ObjectPath), voxel.LocalAt)) &&
            !removedPaths.Any(path => voxel.ObjectPath.Length >= path.Length && voxel.ObjectPath.Take(path.Length).SequenceEqual(path)) &&
            content.FindVoxelAppearance(voxel.Voxel) is ContentLookup<VoxelAppearanceProjection>.Found
            { Value.Behaviors.Wheelset: true }).Select(voxel => voxel.At.Z).Distinct().Order().ToArray();
        var (gauge, _, radius, width) = RailwayCoordinates.Wheels(vehicle.Size);
        var objects = geometry.Objects.ToBuilder();
        var next = geometry.Objects.Max(item => item.Id.Value);
        var parent = geometry.Objects.First(item => item.Parent is null).Id;
        foreach (var row in rows)
        {
            var id = new SceneObjectId(++next);
            keys = keys.Add(id, "wheelset:" + row);
            objects.Add(new(id, parent, new(new(0, (float)radius, row + 0.5f), Quaternion.Identity), null,
                [new SceneVisual.Wheelset(gauge, radius, width, 0)], [], []));
        }
        return built with { Geometry = new(geometry with { Objects = objects.ToImmutable() }, keys) };
    }
    internal static RenderedDesign Place(RenderedDesign geometry, ScenePose pose, double distanceCells)
    {
        var placed = SceneGeometry.Place(geometry, pose);
        return placed with
        {
            Objects = placed.Objects.Select(item => item with
            {
                Visuals = item.Visuals.Any(visual => visual is SceneVisual.Wheelset)
                    ? item.Visuals.Select(visual => visual is SceneVisual.Wheelset wheel
                        ? wheel with { RotationRadians = (distanceCells / wheel.RadiusCells % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) }
                        : visual).ToImmutableArray() : item.Visuals
            }).ToImmutableArray()
        };
    }
    internal static ScenePose Pose(RailwaySample sample, ScenePoint origin, bool firstEndPositive)
    {
        var forward = firstEndPositive ? sample.Forward : -sample.Forward;
        return new(RailwayCoordinates.Relative(sample.Position, origin), RailwayCoordinates.Rotation(forward));
    }
}

internal sealed class SceneVehicles : IAsyncDisposable
{
    private readonly Dictionary<InstanceId, BuiltObject> ready = [];
    private readonly Dictionary<InstanceId, InstanceView> current = [];
    private readonly HashSet<InstanceId> requested = [];
    internal sealed class Cache
    {
        private readonly object gate = new();
        private ulong owner;
        private HashSet<InstanceId>? live;
        private readonly Dictionary<string, (InstanceId Instance, BuiltObject Body)> bodies = [];
        internal ulong Open() { lock (gate) { live = null; return ++owner; } }
        internal BuiltObject? Find(string key) { lock (gate) return bodies.GetValueOrDefault(key).Body; }
        internal void Publish(ulong session, InstanceId instance, string key, BuiltObject body)
        {
            lock (gate)
            {
                if (owner != session || live is not null && !live.Contains(instance)) return;
                foreach (var old in bodies.Where(pair => pair.Value.Instance == instance && pair.Key != key).Select(pair => pair.Key).ToArray()) bodies.Remove(old);
                bodies[key] = (instance, body);
            }
        }
        internal void Retain(ulong session, HashSet<InstanceId> instances)
        {
            lock (gate)
            {
                if (session != owner) return;
                live = instances;
                foreach (var key in bodies.Where(pair => !live.Contains(pair.Value.Instance)).Select(pair => pair.Key).ToArray()) bodies.Remove(key);
            }
        }
    }
    private readonly Cache cache;
    private readonly ulong owner;
    private HashSet<InstanceId>? live;
    private readonly Func<IContentView, InstanceView, BuiltObject> build;
    private readonly Dictionary<InstanceId, Task<(string Key, BuiltObject Body)>> pending = [];
    internal SceneVehicles(Cache? cache = null, Func<IContentView, InstanceView, BuiltObject>? build = null)
    {
        this.cache = cache ?? new Cache();
        owner = this.cache.Open();
        this.build = build ?? RailwayVehicles.Build;
    }
    private readonly CancellationTokenSource cancellation = new();
    internal Task[] PendingWork => pending.Values.Cast<Task>().ToArray();
    internal bool HasPendingBuilds => pending.Values.Any(task => !task.IsCompleted) || requested.Any(id =>
        !ready.TryGetValue(id, out var body) || !WorldSceneSession.Matches(body, current[id]));

    internal void Retain(IEnumerable<InstanceId> instances)
    {
        requested.Clear();
        live = instances.ToHashSet();
        cache.Retain(owner, live);
        foreach (var id in ready.Keys.Where(id => !live.Contains(id)).ToArray()) ready.Remove(id);
        foreach (var id in current.Keys.Where(id => !live.Contains(id)).ToArray()) current.Remove(id);
        Drain();
        // In-flight work remains owned and counted until completion; removed instances cannot republish to the cache.
    }

    internal RenderedDesign? Reconcile(IContentView content, InstanceView instance, Func<string, SceneObjectId> objectId)
    {
        var body = Prepare(content, instance);
        return body is not null && WorldSceneSession.Matches(body, instance)
            ? WorldSceneSession.Attach(body.Geometry, "vehicle:" + instance.Id + ":", objectId).Geometry : null;
    }

    internal BuiltObject? Prepare(IContentView content, InstanceView instance, bool allowBuild = true)
    {
        requested.Add(instance.Id);
        current[instance.Id] = instance;
        Drain();
        if (ready.TryGetValue(instance.Id, out var body) && WorldSceneSession.Matches(body, instance))
            return body;
        if (allowBuild && !pending.ContainsKey(instance.Id) && pending.Count < RailwayCoordinates.MaxConcurrentBuilds)
            pending.Add(instance.Id, Task.Run(() =>
            {
                var key = Inputs(content, instance);
                return (key, cache.Find(key) ?? build(content, instance));
            }, cancellation.Token));
        return body;
    }

    private void Drain()
    {
        foreach (var pair in pending.Where(pair => pair.Value.IsCompleted).ToArray())
        {
            pending.Remove(pair.Key);
            var built = pair.Value.GetAwaiter().GetResult();
            if ((live is null || live.Contains(pair.Key)) && current.TryGetValue(pair.Key, out var wanted) &&
                WorldSceneSession.Matches(built.Body, wanted))
            {
                ready[pair.Key] = built.Body;
                cache.Publish(owner, pair.Key, built.Key, built.Body);
            }
        }
    }

    private static string Inputs(IContentView content, InstanceView instance)
    {
        var lookup = content.FindDesign(instance.Design);
        if (lookup is not ContentLookup<DesignEvaluation>.Found found || found.Value is DesignEvaluation.Failed)
            return RailwayDependencies.Values(new object[] { instance.Id, instance.Design, lookup });
        var scene = DesignGeometry.Projection(found.Value);
        return RailwayDependencies.Values(new object?[] { instance.Id, instance.Design,
            instance.Details is InstanceDetails.Vehicle vehicle ? (object)vehicle.Size : null,
            WorldSceneSession.Destroyed(instance).OrderBy(part => part.Value).ToArray(),
            StructureDamage.Signature.From(instance), scene,
            found.Value is DesignEvaluation.Complete complete ? complete.Diagnostics : ((DesignEvaluation.Partial)found.Value).Diagnostics,
            scene.Voxels.Select(voxel => voxel.Voxel).Distinct().OrderBy(id => id.Value, StringComparer.Ordinal)
                .Select(id => content.FindVoxelAppearance(id)).ToArray() });
    }

    internal static (SceneObject Object, SceneFog Fog) Loading(InstanceView instance, DesignEvaluation evaluation,
        ScenePose pose, Func<string, SceneObjectId> objectId) =>
        (new(objectId("vehicle:" + instance.Id + ":loading"), null, pose, new WorldObjectId.Instance(instance.Id), [], [], []),
            new(VehicleBounds(DesignGeometry.Projection(evaluation), (InstanceDetails.Vehicle)instance.Details), Pose: pose));

    internal static RenderedDesign Unplaced(IContentView content, InstanceView instance,
        ContentLookup<DesignEvaluation> design, int? length, ScenePose pose, Func<string, SceneObjectId> objectId)
    {
        if (design is not ContentLookup<DesignEvaluation>.Found found || found.Value is DesignEvaluation.Failed)
            return WorldSceneSession.Attach(WorldSceneSession.BuildObject(content, instance, pose).Geometry,
                "vehicle:" + instance.Id + ":", objectId).Geometry;
        var subject = new WorldObjectId.Instance(instance.Id);
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        if (length is not > 0)
        {
            var diagnostics = found.Value is DesignEvaluation.Complete complete ? complete.Diagnostics : ((DesignEvaluation.Partial)found.Value).Diagnostics;
            DesignGeometry.AddDiagnostics(diagnostics, problems, subject);
            var reference = new ContentReference.Design(instance.Design);
            var source = problems.LastOrDefault(problem => problem.Content == reference && problem.Package is not null);
            problems.Add(new("vehicle_length_unknown", SceneStage.Reconcile, subject, reference, source?.Package, source?.Element,
                "A vehicle design has no known length; its consist remains at the authoritative center without member geometry."));
        }
        return new([new(objectId("vehicle:" + instance.Id + ":unplaced"), null, pose, subject, [], [], [])], [], problems.ToImmutable());
    }

    internal static SceneBounds VehicleBounds(DesignSceneProjection projection, InstanceDetails.Vehicle vehicle)
    {
        var minimum = Vector3.Zero;
        var maximum = Vector3.Zero;
        if (projection.OccupiedBounds is { } bounds)
        {
            minimum = new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z);
            maximum = new(bounds.Maximum.X + 1, bounds.Maximum.Y + 1, bounds.Maximum.Z + 1);
        }
        else foreach (var voxel in projection.Voxels)
            {
                minimum = Vector3.Min(minimum, new(voxel.At.X, voxel.At.Y, voxel.At.Z));
                maximum = Vector3.Max(maximum, new(voxel.At.X + 1, voxel.At.Y + 1, voxel.At.Z + 1));
            }
        var (gauge, _, radius, width) = RailwayCoordinates.Wheels(vehicle.Size);
        var halfWidth = (float)(gauge / 2 + width);
        minimum = Vector3.Min(minimum, new(-halfWidth, 0, minimum.Z - (float)radius));
        maximum = Vector3.Max(maximum, new(halfWidth, (float)(2 * radius), maximum.Z + (float)radius));
        return new(minimum, maximum);
    }


    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try { await Task.WhenAll(pending.Values); }
        catch (Exception) { /* Closing observes owned build failures. */ }
        cancellation.Dispose();
    }
}

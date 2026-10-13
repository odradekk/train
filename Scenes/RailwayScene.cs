using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayScene : IAsyncDisposable
{
    private readonly RailwayCache cache;
    private readonly ulong owner;
    private readonly Func<string, SceneObjectId> objectId;
    private readonly Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment> build;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task<RailwayPath> pendingPath;
    private readonly Dictionary<int, RailwaySegment> segments = [];
    private readonly Dictionary<int, RenderedDesign> scenery = [];
    private sealed record SegmentWork(Task<RailwaySegment> Task, CancellationTokenSource Cancellation);
    private readonly Dictionary<int, SegmentWork> pendingSegments = [];
    private readonly List<SegmentWork> canceledSegments = [];
    private readonly Dictionary<InstanceId, (DesignHash Design, ImmutableHashSet<PartId> Destroyed, RenderedDesign Geometry)> vehicles = [];
    private readonly Dictionary<InstanceId, (DesignHash Design, ImmutableHashSet<PartId> Destroyed, Task<RailwayVehicleGeometry> Task)> pendingVehicles = [];
    private readonly List<Task> retired = [];
    private RailwayPath? path;
    private SceneProblem? failure;
    private double lastRealSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private double lastSimulationSeconds;
    private double displayRate = 1;
    internal EdgeId Edge { get; }
    internal Task[] PendingWork => (path is null && failure is null ? new Task[] { pendingPath } : [])
        .Concat(pendingSegments.Values.Select(item => item.Task)).Concat(pendingVehicles.Values.Select(item => item.Task)).ToArray();

    internal RailwayScene(SceneWorldSnapshot initial, EdgeId edge, RailwayCache cache, Func<string, SceneObjectId> objectId,
        Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null)
    {
        this.cache = cache;
        Edge = edge;
        owner = cache.OpenSession();
        this.objectId = objectId;
        this.build = build ?? RailwayTerrain.Build;
        lastSimulationSeconds = initial.Core.Position.CompletedSteps * SimulationTime.StepSeconds;
        pendingPath = Task.Run(() => { cancellation.Token.ThrowIfCancellationRequested(); return new RailwayPath(initial, edge); }, cancellation.Token);
    }

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, WorldSceneTarget target,
        SceneFrameRequest request, SceneFrameStamp stamp, SceneFrame? displayed)
    {
        var seconds = snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds;
        var adjacent = previous is not null && previous.Core.Position.CompletedSteps + 1 == snapshot.Core.Position.CompletedSteps;
        if (adjacent) seconds = (previous!.Core.Position.CompletedSteps + request.Interpolation) * SimulationTime.StepSeconds;
        if (path is null && pendingPath.IsCompleted && failure is null)
        {
            try { path = pendingPath.GetAwaiter().GetResult(); }
            catch (RailwayBuildException error) { failure = new("build_failed", SceneStage.Build, null, null, null, null, error.Message); }
        }
        if (path is null)
            return new(stamp, new(0, 0, 0), seconds, failure is null ? SceneReadiness.Loading : SceneReadiness.Failed,
                [], [], new([new SceneBounds(new(float.MinValue), new(float.MaxValue))], []), failure is null ? [] : [failure], displayed);

        var focal = target is WorldSceneTarget.Follow follow && snapshot.Core.FindTrain(follow.Train) is Lookup<TrainView>.Found train
            ? Distance(train.Value) + FollowOffset(train.Value, follow.Vehicle) :
                ((WorldSceneTarget.Location)target).Value is WorldLocation.Edge location ? location.DistanceMeters * 2 : 0;
        if (request.Camera is { } camera && target is WorldSceneTarget.Location && camera.Frame.Update.Frame.Space == stamp.Space)
            focal = path.Locate(RailwayCoordinates.Add(camera.Frame.Origin, camera.Focus));
        var focus = path.Sample(focal).Position;
        var origin = new ScenePoint(Math.Floor(focus.X / 256) * 256, Math.Floor(focus.Y / 256) * 256, Math.Floor(focus.Z / 256) * 256);
        var middle = Math.Clamp((int)(focal / RailwayCoordinates.SegmentLengthCells), 0, path.SegmentCount - 1);
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (seconds > lastSimulationSeconds && now > lastRealSeconds)
            displayRate = Math.Clamp((seconds - lastSimulationSeconds) / (now - lastRealSeconds), 1, 64);
        lastSimulationSeconds = seconds;
        lastRealSeconds = now;
        var speed = 0.0;
        var direction = 1;
        if (target is WorldSceneTarget.Follow watched && snapshot.Core.FindTrain(watched.Train) is Lookup<TrainView>.Found watchedTrain)
        {
            speed = watchedTrain.Value.SpeedMetersPerSecond;
            direction = ((TrainPosition.Edge)watchedTrain.Value.Position).Direction == TravelDirection.Forward ? 1 : -1;
        }
        var ahead = Math.Clamp((int)Math.Ceiling(Math.Abs(speed) * displayRate * 4 / RailwayCoordinates.SegmentLengthCells), 0, 4);
        var minimum = Math.Max(0, middle - 2 - (direction < 0 ? ahead : 0));
        var maximum = Math.Min(path.SegmentCount - 1, middle + 2 + (direction > 0 ? ahead : 0));
        var needed = Enumerable.Range(minimum, maximum - minimum + 1).ToHashSet();
        foreach (var index in pendingSegments.Keys.Where(index => !needed.Contains(index)).ToArray())
        {
            var work = pendingSegments[index];
            work.Cancellation.Cancel();
            canceledSegments.Add(work);
            retired.Add(work.Task);
            pendingSegments.Remove(index);
        }
        foreach (var index in segments.Keys.Where(index => !needed.Contains(index)).ToArray())
        { segments.Remove(index); scenery.Remove(index); }
        foreach (var pair in pendingSegments.ToArray())
        {
            if (!pair.Value.Task.IsCompleted) continue;
            pendingSegments.Remove(pair.Key);
            try
            {
                var built = pair.Value.Task.GetAwaiter().GetResult();
                if (needed.Contains(pair.Key))
                {
                    segments[pair.Key] = built;
                    scenery[pair.Key] = Attach(built.Geometry, "segment:" + pair.Key + ":");
                }
            }
            catch (RailwayBuildException error) { failure = new("build_failed", SceneStage.Build, new WorldObjectId.Edge(path.Edge.Id), null, null, null, error.Message); }
            finally { pair.Value.Cancellation.Dispose(); }
        }
        foreach (var index in needed.OrderBy(index => Math.Abs(index - middle)))
        {
            if (segments.ContainsKey(index) || pendingSegments.ContainsKey(index)) continue;
            if (cache.Find(snapshot, path, index) is { } cached)
            {
                segments[index] = cached;
                scenery[index] = Attach(cached.Geometry, "segment:" + index + ":");
            }
            else if (pendingSegments.Count + canceledSegments.Count(work => !work.Task.IsCompleted) < 2 && failure is null)
            {
                var input = snapshot;
                var currentPath = path;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                pendingSegments.Add(index, new(Task.Run(() => build(input, currentPath, index, stop.Token), stop.Token), stop));
            }
        }
        cache.Publish(owner, segments.Values);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var fog = ImmutableArray.CreateBuilder<SceneBounds>();
        var fogArcs = ImmutableArray.CreateBuilder<(double Begin, double End)>();
        foreach (var pair in segments.OrderBy(item => item.Key))
        {
            var pose = new ScenePose(RailwayCoordinates.Relative(pair.Value.Origin, origin), Quaternion.Identity);
            var placed = RailwayVehicles.Place(scenery[pair.Key], pose, 0);
            objects.AddRange(placed.Objects); picking.AddRange(placed.Picking); problems.AddRange(placed.Diagnostics);
        }
        foreach (var index in needed.Where(index => !segments.ContainsKey(index)))
            AddFog(index * RailwayCoordinates.SegmentLengthCells,
                Math.Min(path.LengthCells, (index + 1) * RailwayCoordinates.SegmentLengthCells));
        if (minimum > 0) AddFog(0, minimum * RailwayCoordinates.SegmentLengthCells);
        if (maximum < path.SegmentCount - 1) AddFog((maximum + 1) * RailwayCoordinates.SegmentLengthCells, path.LengthCells);
        var waitingForVehicles = PresentVehicles();
        if (failure is not null) problems.Add(failure);
        retired.RemoveAll(task => { if (!task.IsCompleted) return false; _ = task.Exception; return true; });
        canceledSegments.RemoveAll(work => { if (!work.Task.IsCompleted) return false; work.Cancellation.Dispose(); return true; });
        var ready = Enumerable.Range(Math.Max(0, middle - 2), Math.Min(path.SegmentCount - 1, middle + 2) - Math.Max(0, middle - 2) + 1)
            .All(segments.ContainsKey) && !waitingForVehicles;
        return new(stamp, origin, seconds, failure is not null ? SceneReadiness.Failed : segments.Count == 0 ? SceneReadiness.Loading :
            ready ? SceneReadiness.Ready : SceneReadiness.Partial, objects.ToImmutable(), picking.ToImmutable(),
            new(fog.ToImmutable(), []), problems.ToImmutable(), displayed, path, segments.Values.ToImmutableArray(), fogArcs.ToImmutable());

        void AddFog(double begin, double end)
        { fog.Add(Bounds(begin, end, origin)); fogArcs.Add((begin, end)); }

        double Distance(TrainView current)
        {
            var position = (TrainPosition.Edge)current.Position;
            if (adjacent && previous!.Core.World.Trains.Any(item => item.Id == current.Id) && previous.Core.FindTrain(current.Id) is Lookup<TrainView>.Found old &&
                old.Value.Position is TrainPosition.Edge before && before.Id == position.Id && before.Track == position.Track &&
                before.Direction == position.Direction && old.Value.OrderFacing == current.OrderFacing && old.Value.Vehicles.SequenceEqual(current.Vehicles))
                return (before.DistanceMeters + (position.DistanceMeters - before.DistanceMeters) * request.Interpolation) * 2;
            return position.DistanceMeters * 2;
        }

        double FollowOffset(TrainView train, VehicleId selected)
        {
            var lengths = train.Vehicles.Select(id => snapshot.Core.FindInstance(new InstanceId.Vehicle(id)) is Lookup<InstanceView>.Found instance &&
                snapshot.Content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found design
                    ? RailwayVehicles.Length(design.Value) : null).ToArray();
            if (lengths.Any(length => length is null or <= 0)) return 0;
            var index = train.Vehicles.IndexOf(selected);
            var positive = (((TrainPosition.Edge)train.Position).Direction == TravelDirection.Forward) ==
                (train.OrderFacing == TrainOrderFacing.FirstEndLeading);
            return (positive ? 1 : -1) * (lengths.Sum(length => length!.Value) / 2.0 -
                lengths.Take(index).Sum(length => length!.Value) - lengths[index]!.Value / 2.0);
        }

        bool PresentVehicles()
        {
            var live = new HashSet<InstanceId>();
            var waiting = false;
            foreach (var map in snapshot.Core.World.Trains.Where(item => item.Position is TrainPosition.Edge edge && edge.Id == path.Edge.Id))
            {
                if (snapshot.Core.FindTrain(map.Id) is not Lookup<TrainView>.Found found) continue;
                var train = found.Value;
                var position = (TrainPosition.Edge)train.Position;
                if (position.Track < 1 || position.Track > path.Edge.Tracks) continue;
                var members = train.Vehicles.Select(id => new InstanceId.Vehicle(id)).ToArray();
                var instances = members.Select(id => snapshot.Core.FindInstance(id) is Lookup<InstanceView>.Found instance ? instance.Value : null).ToArray();
                var lengths = instances.Select(instance => instance is not null && snapshot.Content.FindDesign(instance.Design) is
                    ContentLookup<DesignEvaluation>.Found evaluation ? RailwayVehicles.Length(evaluation.Value) : null).ToArray();
                var distance = Distance(train);
                var positive = (position.Direction == TravelDirection.Forward) == (train.OrderFacing == TrainOrderFacing.FirstEndLeading);
                var unknown = lengths.Any(length => length is null or <= 0);
                var cursor = unknown ? 0 : lengths.Sum(length => length!.Value) / 2.0;
                for (var member = 0; member < members.Length; member++)
                {
                    var id = members[member];
                    live.Add(id);
                    var instance = instances[member];
                    var arc = unknown ? distance : distance + (positive ? 1 : -1) * (cursor - lengths[member]!.Value / 2.0);
                    if (!unknown) cursor -= lengths[member]!.Value;
                    var sample = path.Sample(arc, RailwayCoordinates.TrackOffset(position.Track, path.Edge.Tracks));
                    var pose = RailwayVehicles.Pose(sample, origin, positive);
                    if (unknown || instance is null)
                    {
                        vehicles.Remove(id);
                        if (pendingVehicles.Remove(id, out var unused)) retired.Add(unused.Task);
                        if (instance is null)
                        {
                            problems.Add(new("target_missing", SceneStage.Reconcile, new WorldObjectId.Instance(id), null, null, null,
                                "A train member is missing from the current core view."));
                            continue;
                        }
                        var reference = new ContentReference.Design(instance.Design);
                        objects.Add(new(objectId("vehicle:" + id + ":missing"), null, pose, new WorldObjectId.Instance(id),
                            [new SceneVisual.Missing(reference, false)], [], []));
                        problems.Add(new("vehicle_length_unknown", SceneStage.Reconcile, new WorldObjectId.Instance(id), reference, null, null,
                            "A vehicle length is unknown; this train is shown as markers at its authoritative center without guessing member lengths."));
                        continue;
                    }
                    var destroyed = instance.Parts.Where(part => part.Destroyed).Select(part => part.Part).ToImmutableHashSet();
                    if (pendingVehicles.TryGetValue(id, out var pending) && pending.Task.IsCompleted)
                    {
                        pendingVehicles.Remove(id);
                        var built = pending.Task.GetAwaiter().GetResult();
                        if (pending.Design == instance.Design && pending.Destroyed.SetEquals(destroyed))
                            vehicles[id] = (instance.Design, destroyed, Attach(built.Geometry, "vehicle:" + id + ":", built.Keys));
                    }
                    if (!vehicles.TryGetValue(id, out var cached) || cached.Design != instance.Design || !cached.Destroyed.SetEquals(destroyed))
                    {
                        waiting = true;
                        if (!pendingVehicles.ContainsKey(id) && pendingVehicles.Count < 2)
                        {
                            var input = snapshot.Content;
                            pendingVehicles.Add(id, (instance.Design, destroyed, Task.Run(() =>
                            { cancellation.Token.ThrowIfCancellationRequested(); return RailwayVehicles.Build(input, instance); }, cancellation.Token)));
                        }
                        objects.Add(new(objectId("vehicle:" + id + ":loading"), null, pose, new WorldObjectId.Instance(id), [], [], []));
                        fog.Add(new(pose.Position - new Vector3(16, 2, 80), pose.Position + new Vector3(16, 40, 80)));
                        fogArcs.Add((-1, -1));
                        continue;
                    }
                    var placed = RailwayVehicles.Place(cached.Geometry, pose, arc);
                    var hidden = segments.Values.Any(segment => segment.Tunnels.Any(interval => arc >= interval.Start && arc <= interval.End));
                    objects.AddRange(hidden ? placed.Objects.Select(item => item with { Visuals = [] }) : placed.Objects);
                    if (!hidden) picking.AddRange(placed.Picking);
                    problems.AddRange(placed.Diagnostics);
                }
            }
            foreach (var id in vehicles.Keys.Where(id => !live.Contains(id)).ToArray()) vehicles.Remove(id);
            foreach (var id in pendingVehicles.Keys.Where(id => !live.Contains(id)).ToArray())
            { retired.Add(pendingVehicles[id].Task); pendingVehicles.Remove(id); }
            return waiting;
        }
    }

    private RenderedDesign Attach(RenderedDesign geometry, string prefix, ImmutableDictionary<SceneObjectId, string>? keys = null)
    {
        var ids = geometry.Objects.ToDictionary(item => item.Id,
            item => objectId(prefix + (keys is null ? item.Id.Value.ToString() : keys[item.Id])));
        return geometry with
        {
            Objects = geometry.Objects.Select(item => item with { Id = ids[item.Id], Parent = item.Parent is { } parent ? ids[parent] : null }).ToImmutableArray(),
            Picking = geometry.Picking.Select(item => item with { Object = ids[item.Object] }).ToImmutableArray()
        };
    }
    private SceneBounds Bounds(double begin, double end, ScenePoint origin)
        => path!.Bounds(begin, end, origin, Math.Max(64, path.Edge.Tracks * 17 + 40));
    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try { await Task.WhenAll(PendingWork.Concat(retired)); }
        catch (Exception) { /* Closing observes owned build failures; they cannot publish after cancellation. */ }
        foreach (var work in pendingSegments.Values.Concat(canceledSegments)) work.Cancellation.Dispose();
        cancellation.Dispose();
    }
}

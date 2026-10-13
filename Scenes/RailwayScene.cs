using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayScene : IAsyncDisposable
{
    private sealed record ConnectedSegment(RailwaySegment Source, RenderedDesign Geometry);
    private sealed record SegmentWork(Task<RailwaySegment> Task, CancellationTokenSource Cancellation);
    private sealed record MemberPlacement(InstanceId Id, InstanceView? Instance,
        ContentLookup<DesignEvaluation>? Design, int? Length, double CenterArc, double TrackArc);
    private sealed record TrainPlacement(TrainView Train, bool Positive, double Offset,
        bool LengthsKnown, bool TrackAvailable, ImmutableArray<MemberPlacement> Members);
    private sealed class FrameContents
    {
        internal readonly ImmutableArray<SceneObject>.Builder Objects = ImmutableArray.CreateBuilder<SceneObject>();
        internal readonly ImmutableArray<PickGrid>.Builder Picking = ImmutableArray.CreateBuilder<PickGrid>();
        internal readonly ImmutableArray<SceneProblem>.Builder Problems = ImmutableArray.CreateBuilder<SceneProblem>();
        internal readonly ImmutableArray<SceneFog>.Builder Fog = ImmutableArray.CreateBuilder<SceneFog>();
        internal void Add(RenderedDesign geometry, bool hidden = false)
        {
            Objects.AddRange(hidden ? geometry.Objects.Select(item => item with { Visuals = [] }) : geometry.Objects);
            if (!hidden) Picking.AddRange(geometry.Picking);
            Problems.AddRange(geometry.Diagnostics);
        }
    }

    private readonly RailwayCache cache;
    private readonly ulong owner;
    private readonly Func<string, SceneObjectId> objectId;
    private readonly Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment> build;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task<RailwayPath> pendingPath;
    private readonly Dictionary<int, ConnectedSegment> segments = [];
    private readonly Dictionary<int, SegmentWork> pendingSegments = [];
    private readonly List<SegmentWork> retiredSegments = [];
    private readonly Dictionary<InstanceId, BuiltObject> vehicles = [];
    private readonly Dictionary<InstanceId, Task<BuiltObject>> pendingVehicles = [];
    private readonly List<Task<BuiltObject>> retiredVehicles = [];
    private RailwayPath? path;
    private SceneProblem? failure;
    private double lastRealSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private double lastSimulationSeconds;
    private double displayRate = 1;
    internal EdgeId Edge { get; }
    internal Task[] PendingWork => (path is null && failure is null ? new Task[] { pendingPath } : [])
        .Concat(pendingSegments.Values.Select(item => item.Task)).Concat(pendingVehicles.Values)
        .Concat(retiredSegments.Where(item => !item.Task.IsCompleted).Select(item => item.Task))
        .Concat(retiredVehicles.Where(task => !task.IsCompleted)).ToArray();

    internal RailwayScene(SceneWorldSnapshot initial, EdgeId edge, RailwayCache cache, Func<string, SceneObjectId> objectId,
        Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null)
    {
        this.cache = cache;
        Edge = edge;
        owner = cache.OpenSession();
        this.objectId = objectId;
        this.build = build ?? RailwayTerrain.Build;
        lastSimulationSeconds = initial.Core.Position.CompletedSteps * SimulationTime.StepSeconds;
        pendingPath = Task.Run(() => new RailwayPath(initial, edge), cancellation.Token);
    }

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, WorldSceneTarget target,
        SceneFrameRequest request, SceneFrameStamp stamp, SceneFrame? displayed)
    {
        var adjacent = previous is not null && previous.Core.Position.CompletedSteps + 1 == snapshot.Core.Position.CompletedSteps;
        var seconds = (adjacent ? previous!.Core.Position.CompletedSteps + request.Interpolation :
            snapshot.Core.Position.CompletedSteps) * SimulationTime.StepSeconds;
        AcceptPath();
        if (path is null)
            return new(stamp, default, seconds, failure is null ? SceneReadiness.Loading : SceneReadiness.Failed,
                [], [], new([new SceneBounds(new(float.MinValue), new(float.MaxValue))], []), failure is null ? [] : [failure], displayed);

        var trains = PlaceTrains(snapshot, adjacent ? previous : null, request.Interpolation);
        var followed = target is WorldSceneTarget.Follow follow ? trains.First(item => item.Train.Id == follow.Train) : null;
        var focal = followed is not null ? followed.Members.First(item => item.Id == new InstanceId.Vehicle(((WorldSceneTarget.Follow)target).Vehicle)).CenterArc :
            ((WorldLocation.Edge)((WorldSceneTarget.Location)target).Value).DistanceMeters * RailwayCoordinates.CellsPerMeter;
        if (request.Camera is { } camera && target is WorldSceneTarget.Location && camera.Frame.Update.Frame.Space == stamp.Space)
            focal = path.Locate(RailwayCoordinates.Add(camera.Frame.Origin, camera.Focus));
        var focus = path.Sample(focal).Position;
        var step = RailwayCoordinates.OriginStepCells;
        var origin = new ScenePoint(Math.Floor(focus.X / step) * step, Math.Floor(focus.Y / step) * step, Math.Floor(focus.Z / step) * step);
        var middle = Math.Clamp((int)(focal / RailwayCoordinates.SegmentLengthCells), 0, path.SegmentCount - 1);
        UpdateDisplayRate(seconds);
        var (minimum, maximum) = NeededRange(middle, followed?.Train);
        ReconcileSegments(snapshot, middle, minimum, maximum);
        var frame = new FrameContents();
        RenderSegments(origin, minimum, maximum, frame);
        var waiting = RenderTrains(snapshot.Content, trains, origin, frame);
        if (failure is not null) frame.Problems.Add(failure);
        RetireCompletedWork();
        var radius = RailwayCoordinates.ViewRadiusSegments;
        var ready = Enumerable.Range(Math.Max(0, middle - radius), Math.Min(path.SegmentCount - 1, middle + radius) -
            Math.Max(0, middle - radius) + 1).All(segments.ContainsKey) && !waiting;
        return new(stamp, origin, seconds, failure is not null ? SceneReadiness.Failed : segments.Count == 0 ? SceneReadiness.Loading :
            ready ? SceneReadiness.Ready : SceneReadiness.Partial, frame.Objects.ToImmutable(), frame.Picking.ToImmutable(),
            new([], []), frame.Problems.ToImmutable(), displayed, path,
            segments.Values.Select(item => item.Source).ToImmutableArray(), frame.Fog.ToImmutable());
    }

    private void AcceptPath()
    {
        if (path is not null || failure is not null || !pendingPath.IsCompleted) return;
        try { path = pendingPath.GetAwaiter().GetResult(); }
        catch (RailwayBuildException error) { Fail(error); }
    }

    private void Fail(RailwayBuildException error) => failure =
        new("build_failed", SceneStage.Build, new WorldObjectId.Edge(Edge), null, null, null, error.Message);

    private void UpdateDisplayRate(double seconds)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (seconds > lastSimulationSeconds && now > lastRealSeconds)
            displayRate = Math.Clamp((seconds - lastSimulationSeconds) / (now - lastRealSeconds), 1, RailwayCoordinates.MaximumDisplayRate);
        lastSimulationSeconds = seconds;
        lastRealSeconds = now;
    }

    private (int Minimum, int Maximum) NeededRange(int middle, TrainView? followed)
    {
        var direction = followed?.Position is TrainPosition.Edge { Direction: TravelDirection.Reverse } ? -1 : 1;
        var ahead = Math.Clamp((int)Math.Ceiling(Math.Abs(followed?.SpeedMetersPerSecond ?? 0) *
            RailwayCoordinates.CellsPerMeter * displayRate * RailwayCoordinates.PrefetchSeconds / RailwayCoordinates.SegmentLengthCells),
            0, RailwayCoordinates.MaxPrefetchSegments);
        var radius = RailwayCoordinates.ViewRadiusSegments;
        return (Math.Max(0, middle - radius - (direction < 0 ? ahead : 0)),
            Math.Min(path!.SegmentCount - 1, middle + radius + (direction > 0 ? ahead : 0)));
    }

    private void ReconcileSegments(SceneWorldSnapshot snapshot, int middle, int minimum, int maximum)
    {
        foreach (var index in pendingSegments.Keys.Where(index => index < minimum || index > maximum).ToArray())
        {
            var work = pendingSegments[index];
            work.Cancellation.Cancel();
            retiredSegments.Add(work);
            pendingSegments.Remove(index);
        }
        foreach (var index in segments.Keys.Where(index => index < minimum || index > maximum).ToArray()) segments.Remove(index);
        foreach (var pair in pendingSegments.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            pendingSegments.Remove(pair.Key);
            try { Connect(pair.Value.Task.GetAwaiter().GetResult()); }
            catch (RailwayBuildException error) { Fail(error); }
            finally { pair.Value.Cancellation.Dispose(); }
        }
        foreach (var index in Enumerable.Range(minimum, maximum - minimum + 1).OrderBy(index => Math.Abs(index - middle)))
        {
            if (segments.ContainsKey(index) || pendingSegments.ContainsKey(index)) continue;
            if (cache.Find(snapshot, path!, index) is { } cached) Connect(cached);
            else if (failure is null && pendingSegments.Count + retiredSegments.Count(work => !work.Task.IsCompleted) < RailwayCoordinates.MaxConcurrentBuilds)
            {
                var currentPath = path!;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                pendingSegments.Add(index, new(Task.Run(() => build(snapshot, currentPath, index, stop.Token), stop.Token), stop));
            }
        }
        cache.Publish(owner, segments.Values.Select(item => item.Source));
    }

    private void Connect(RailwaySegment segment)
    {
        var keys = segment.Geometry.Objects.ToImmutableDictionary(item => item.Id, item => item.Id.Value.ToString());
        segments[segment.Index] = new(segment, WorldSceneSession.Attach(new(segment.Geometry, keys), "segment:" + segment.Index + ":", objectId));
    }

    private void RenderSegments(ScenePoint origin, int minimum, int maximum, FrameContents frame)
    {
        foreach (var item in segments.OrderBy(item => item.Key).Select(item => item.Value))
            frame.Add(BuiltGeometry.Place(item.Geometry, new(RailwayCoordinates.Relative(item.Source.Origin, origin), Quaternion.Identity)));
        foreach (var index in Enumerable.Range(minimum, maximum - minimum + 1).Where(index => !segments.ContainsKey(index)))
            AddFog(index * RailwayCoordinates.SegmentLengthCells, Math.Min(path!.LengthCells, (index + 1) * RailwayCoordinates.SegmentLengthCells), origin, frame);
        if (minimum > 0) AddFog(0, minimum * RailwayCoordinates.SegmentLengthCells, origin, frame);
        if (maximum < path!.SegmentCount - 1) AddFog((maximum + 1) * RailwayCoordinates.SegmentLengthCells, path.LengthCells, origin, frame);
    }

    private void AddFog(double begin, double end, ScenePoint origin, FrameContents frame, double? width = null)
    {
        var halfWidth = width ?? RailwayCoordinates.FogHalfWidth(path!.Edge.Tracks);
        frame.Fog.Add(new(path!.Bounds(begin, end, origin, halfWidth), new(begin, end, halfWidth)));
    }

    private ImmutableArray<TrainPlacement> PlaceTrains(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, double interpolation)
    {
        var result = ImmutableArray.CreateBuilder<TrainPlacement>();
        foreach (var map in snapshot.Core.World.Trains.Where(item => item.Position is TrainPosition.Edge edge && edge.Id == Edge))
        {
            if (snapshot.Core.FindTrain(map.Id) is not Lookup<TrainView>.Found found) continue;
            var train = found.Value;
            var position = (TrainPosition.Edge)train.Position;
            var positive = (position.Direction == TravelDirection.Forward) == (train.OrderFacing == TrainOrderFacing.FirstEndLeading);
            var available = position.Track >= 1 && position.Track <= path!.Edge.Tracks;
            var offset = available ? RailwayCoordinates.TrackOffset(position.Track, path!.Edge.Tracks) : 0;
            var distance = DisplayDistance(train, previous, interpolation);
            var center = path!.TrackArc(distance, offset);
            var members = train.Vehicles.Select(id =>
            {
                var instanceId = new InstanceId.Vehicle(id);
                var instance = snapshot.Core.FindInstance(instanceId) is Lookup<InstanceView>.Found current ? current.Value : null;
                var design = instance is null ? null : snapshot.Content.FindDesign(instance.Design);
                var length = design is ContentLookup<DesignEvaluation>.Found evaluation ? RailwayVehicles.Length(evaluation.Value) : null;
                return new MemberPlacement(instanceId, instance, design, length, distance, center);
            }).ToArray();
            var known = members.All(member => member.Length is > 0);
            if (known && available)
            {
                var cursor = members.Sum(member => member.Length!.Value) / 2.0;
                for (var index = 0; index < members.Length; index++)
                {
                    var member = members[index];
                    var arc = center + (positive ? 1 : -1) * (cursor - member.Length!.Value / 2.0);
                    members[index] = member with { TrackArc = arc, CenterArc = path.CenterArc(arc, offset) };
                    cursor -= member.Length.Value;
                }
            }
            result.Add(new(train, positive, offset, known, available, members.ToImmutableArray()));
        }
        return result.ToImmutable();
    }

    private static double DisplayDistance(TrainView train, SceneWorldSnapshot? previous, double interpolation)
    {
        var position = (TrainPosition.Edge)train.Position;
        if (previous?.Core.FindTrain(train.Id) is Lookup<TrainView>.Found old && old.Value.Position is TrainPosition.Edge before &&
            before.Id == position.Id && before.Track == position.Track && before.Direction == position.Direction &&
            old.Value.OrderFacing == train.OrderFacing && old.Value.Vehicles.SequenceEqual(train.Vehicles))
            return (before.DistanceMeters + (position.DistanceMeters - before.DistanceMeters) * interpolation) * RailwayCoordinates.CellsPerMeter;
        return position.DistanceMeters * RailwayCoordinates.CellsPerMeter;
    }

    private bool RenderTrains(IContentView content, ImmutableArray<TrainPlacement> trains, ScenePoint origin, FrameContents frame)
    {
        var live = new HashSet<InstanceId>();
        var waiting = false;
        foreach (var train in trains)
        {
            if (!train.TrackAvailable)
            {
                frame.Problems.Add(new("railway_update_pending", SceneStage.Reconcile, new WorldObjectId.Train(train.Train.Id),
                    null, null, null, "This train uses a track added after scene construction; incremental railway updates are implemented by Issue #345."));
                continue;
            }
            foreach (var member in train.Members)
            {
                live.Add(member.Id);
                var pose = RailwayVehicles.Pose(path!.Sample(member.CenterArc, train.Offset), origin, train.Positive);
                if (!train.LengthsKnown)
                {
                    ForgetVehicle(member.Id);
                    RenderUnplaced(content, member, pose, frame);
                    continue;
                }
                var geometry = ReconcileVehicle(content, member.Instance!);
                if (geometry is null)
                {
                    waiting = true;
                    frame.Objects.Add(new(objectId("vehicle:" + member.Id + ":loading"), null, pose, new WorldObjectId.Instance(member.Id), [], [], []));
                    var begin = path.CenterArc(member.TrackArc - member.Length!.Value / 2.0, train.Offset);
                    var end = path.CenterArc(member.TrackArc + member.Length.Value / 2.0, train.Offset);
                    var projection = DesignGeometry.Projection(((ContentLookup<DesignEvaluation>.Found)member.Design!).Value);
                    // Loading bodies are rigid in their display pose, including beyond the route endpoints.
                    var bounds = VehicleBounds(projection, (InstanceDetails.Vehicle)member.Instance!.Details);
                    frame.Fog.Add(new(bounds, new(begin, end,
                        Math.Abs(train.Offset) + Math.Max(Math.Abs(bounds.Minimum.X), Math.Abs(bounds.Maximum.X))), pose));
                    continue;
                }
                var placed = RailwayVehicles.Place(geometry, pose, (train.Positive ? 1 : -1) * member.TrackArc);
                var hidden = segments.Values.Any(segment => segment.Source.Tunnels.Any(interval => member.CenterArc >= interval.Start && member.CenterArc <= interval.End));
                frame.Add(placed, hidden);
            }
        }
        foreach (var id in vehicles.Keys.Concat(pendingVehicles.Keys).Where(id => !live.Contains(id)).Distinct().ToArray()) ForgetVehicle(id);
        return waiting;
    }

    private static SceneBounds VehicleBounds(DesignSceneProjection projection, InstanceDetails.Vehicle vehicle)
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
        var (gauge, radius, width) = RailwayVehicles.Wheels(vehicle.Size);
        var halfWidth = (float)(gauge / 2 + width);
        minimum = Vector3.Min(minimum, new(-halfWidth, 0, minimum.Z - (float)radius));
        maximum = Vector3.Max(maximum, new(halfWidth, (float)(2 * radius), maximum.Z + (float)radius));
        return new(minimum, maximum);
    }

    private RenderedDesign? ReconcileVehicle(IContentView content, InstanceView instance)
    {
        if (pendingVehicles.TryGetValue(instance.Id, out var pending) && pending.IsCompleted)
        {
            pendingVehicles.Remove(instance.Id);
            var built = pending.GetAwaiter().GetResult();
            if (WorldSceneSession.Matches(built, instance))
                vehicles[instance.Id] = built with
                {
                    Geometry = built.Geometry with
                    { Geometry = WorldSceneSession.Attach(built.Geometry, "vehicle:" + instance.Id + ":", objectId) }
                };
        }
        if (vehicles.TryGetValue(instance.Id, out var cached) && WorldSceneSession.Matches(cached, instance)) return cached.Geometry.Geometry;
        if (!pendingVehicles.ContainsKey(instance.Id) && pendingVehicles.Count + retiredVehicles.Count(task => !task.IsCompleted) < RailwayCoordinates.MaxConcurrentBuilds)
            pendingVehicles.Add(instance.Id, Task.Run(() => RailwayVehicles.Build(content, instance), cancellation.Token));
        return null;
    }

    private void RenderUnplaced(IContentView content, MemberPlacement member, ScenePose pose, FrameContents frame)
    {
        var subject = new WorldObjectId.Instance(member.Id);
        if (member.Instance is null)
        {
            frame.Problems.Add(new("target_missing", SceneStage.Reconcile, subject, null, null, null, "A train member is missing from the current core view."));
            return;
        }
        if (member.Design is not ContentLookup<DesignEvaluation>.Found found || found.Value is DesignEvaluation.Failed)
        {
            var built = WorldSceneSession.BuildObject(content, member.Instance, pose);
            frame.Add(WorldSceneSession.Attach(built.Geometry, "vehicle:" + member.Id + ":", objectId));
            return;
        }
        // An incomplete consist has no trustworthy member positions. Keep its valid identities at the core center.
        frame.Objects.Add(new(objectId("vehicle:" + member.Id + ":unplaced"), null, pose, subject, [], [], []));
        if (member.Length is > 0) return;
        var evaluation = found.Value;
        var diagnostics = evaluation is DesignEvaluation.Complete complete ? complete.Diagnostics : ((DesignEvaluation.Partial)evaluation).Diagnostics;
        DesignGeometry.AddDiagnostics(diagnostics, frame.Problems, subject);
        var reference = new ContentReference.Design(member.Instance.Design);
        var source = frame.Problems.LastOrDefault(problem => problem.Content == reference && problem.Package is not null);
        frame.Problems.Add(new("vehicle_length_unknown", SceneStage.Reconcile, subject, reference, source?.Package, source?.Element,
            "A vehicle design has no known length; its consist remains at the authoritative center without member geometry."));
    }

    private void ForgetVehicle(InstanceId id)
    {
        vehicles.Remove(id);
        if (pendingVehicles.Remove(id, out var task)) retiredVehicles.Add(task);
    }

    private void RetireCompletedWork()
    {
        retiredSegments.RemoveAll(work =>
        {
            if (!work.Task.IsCompleted) return false;
            _ = work.Task.Exception;
            work.Cancellation.Dispose();
            return true;
        });
        retiredVehicles.RemoveAll(task => { if (!task.IsCompleted) return false; _ = task.Exception; return true; });
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try { await Task.WhenAll(PendingWork); }
        catch (Exception) { /* Closing observes owned build failures; they cannot publish after cancellation. */ }
        foreach (var work in pendingSegments.Values.Concat(retiredSegments)) work.Cancellation.Dispose();
        cancellation.Dispose();
    }
}

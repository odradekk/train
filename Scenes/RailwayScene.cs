using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayScene : IAsyncDisposable
{
    private sealed record ConnectedSegment(RailwaySegment Source, RenderedDesign Geometry);
    private sealed record SegmentWork(Task<RailwaySegment> Task, CancellationTokenSource Cancellation);
    private sealed record MemberPlacement(InstanceId Id, InstanceView Instance,
        ContentLookup<DesignEvaluation> Design, int? Length, double CenterArc, double TrackArc);
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
    private readonly Dictionary<int, ConnectedSegment> segments = [];
    private readonly Dictionary<int, SegmentWork> pendingSegments = [];
    private readonly List<SegmentWork> retiredSegments = [];
    private readonly SceneVehicles vehicles;
    private RailwayPath? path;
    private SceneProblem? failure;
    private bool waitingSegments;
    private readonly DisplayRate displayRate;
    internal EdgeId Edge { get; }
    internal RailwayPath? Path => path;
    internal bool Failed => failure is not null;
    internal bool HasPendingBuilds => waitingSegments || PendingWork.Any(task => !task.IsCompleted) || vehicles.HasPendingBuilds;
    internal Task[] PendingWork => pendingSegments.Values.Select(item => (Task)item.Task).Concat(vehicles.PendingWork)
        .Concat(retiredSegments.Where(item => !item.Task.IsCompleted).Select(item => item.Task)).ToArray();

    internal RailwayScene(SceneWorldSnapshot initial, EdgeId edge, RailwayCache cache, Func<string, SceneObjectId> objectId, SceneVehicles vehicles,
        Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null, DisplayRate? displayRate = null)
    {
        this.cache = cache;
        this.vehicles = vehicles;
        this.displayRate = displayRate ?? new();
        Edge = edge;
        owner = cache.OpenSession(edge);
        this.objectId = objectId;
        this.build = build ?? RailwayTerrain.Build;
        try { path = new RailwayPath(initial, edge, cancellation.Token); }
        catch (RailwayBuildException error) { Fail(error); }
    }

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, WorldSceneTarget target,
        SceneFrameRequest request, SceneFrameStamp stamp, SceneFrame? displayed, ScenePoint? switchedOrigin = null)
    {
        var (start, seconds) = SceneFrame.SampleTime(snapshot, previous, request.Interpolation);
        if (path is null)
            return new(stamp, switchedOrigin ?? default, seconds, failure is null ? SceneReadiness.Loading : SceneReadiness.Failed,
                [], [], [new(new SceneBounds(new(float.MinValue), new(float.MaxValue)))], failure is null ? [] : [failure], displayed,
                debugLayers: request.DebugLayers, pickRay: request.PickRay);

        var trains = PlaceTrains(snapshot, start, request.Interpolation);
        var followed = target is WorldSceneTarget.Follow follow ? trains.First(item => item.Train.Id == follow.Train) : null;
        var focal = Focal(target, followed);
        var camera = request.Camera;
        var hasSameSpaceCamera = camera is not null && camera.Frame.Update.Frame.Space == stamp.Space;
        if (target is WorldSceneTarget.Location && hasSameSpaceCamera)
            focal = path.Locate(RailwayCoordinates.Add(camera!.Frame.Origin, camera.Focus));
        var focus = path.Sample(focal).Position;
        var origin = focus;
        if (target is not WorldSceneTarget.Location || hasSameSpaceCamera)
        {
            var step = RailwayCoordinates.OriginStepCells;
            origin = new ScenePoint(Math.Floor(focus.X / step) * step, Math.Floor(focus.Y / step) * step, Math.Floor(focus.Z / step) * step);
        }
        origin = switchedOrigin ?? origin;
        displayRate.Update(seconds);
        var middle = ScheduleSegments(snapshot, focal, followed?.Train);
        var frame = new FrameContents();
        RenderSegments(origin, frame);
        var waiting = false;
        try { waiting = RenderTrains(snapshot.Content, trains, origin, frame); }
        catch (RailwayBuildException error) { Fail(error); }
        if (failure is not null) frame.Problems.Add(failure);
        RetireCompletedWork();
        var radius = RailwayCoordinates.ViewRadiusSegments;
        var ready = Enumerable.Range(Math.Max(0, middle - radius), Math.Min(path.SegmentCount - 1, middle + radius) -
            Math.Max(0, middle - radius) + 1).All(segments.ContainsKey) && !waiting;
        return new(stamp, origin, seconds, failure is not null ? SceneReadiness.Failed : segments.Count == 0 ? SceneReadiness.Loading :
            ready ? SceneReadiness.Ready : SceneReadiness.Partial, frame.Objects.ToImmutable(), frame.Picking.ToImmutable(),
            frame.Fog.ToImmutable(), RailwayTerrain.UniqueProblems(frame.Problems), displayed, path,
            segments.Values.Select(item => item.Source).ToImmutableArray(), debugLayers: request.DebugLayers, pickRay: request.PickRay);
    }

    internal void Pump(SceneWorldSnapshot snapshot, WorldLocation.Edge location, bool allowBuild = true)
    {
        if (path is null) return;
        var focal = location.DistanceMeters * RailwayCoordinates.CellsPerMeter;
        ScheduleSegments(snapshot, focal, null, allowBuild);
        RetireCompletedWork();
    }

    private static double Focal(WorldSceneTarget target, TrainPlacement? followed) => followed is not null
        ? followed.Members.First(item => item.Id == new InstanceId.Vehicle(((WorldSceneTarget.Follow)target).Vehicle)).CenterArc
        : ((WorldLocation.Edge)((WorldSceneTarget.Location)target).Value).DistanceMeters * RailwayCoordinates.CellsPerMeter;

    private int ScheduleSegments(SceneWorldSnapshot snapshot, double focal, TrainView? followed, bool allowBuild = true)
    {
        var middle = Math.Clamp((int)(focal / RailwayCoordinates.SegmentLengthCells), 0, path!.SegmentCount - 1);
        var (minimum, maximum) = NeededRange(middle, followed);
        ReconcileSegments(snapshot, middle, minimum, maximum, followed, allowBuild);
        return middle;
    }

    private void Fail(RailwayBuildException error) => failure =
        new("build_failed", SceneStage.Build, new WorldObjectId.Edge(Edge), null, null, null, error.Message);

    private (int Minimum, int Maximum) NeededRange(int middle, TrainView? followed, int? ahead = null)
    {
        var direction = followed?.Position is TrainPosition.Edge { Direction: TravelDirection.Reverse } ? -1 : 1;
        var count = ahead ?? PrefetchSegments(followed?.SpeedMetersPerSecond ?? 0, displayRate.Value);
        var radius = RailwayCoordinates.ViewRadiusSegments;
        return (Math.Max(0, middle - radius - (direction < 0 ? count : 0)),
            Math.Min(path!.SegmentCount - 1, middle + radius + (direction > 0 ? count : 0)));
    }

    internal static int PrefetchSegments(double speed, double rate) => Math.Clamp((int)Math.Ceiling(Math.Abs(speed) *
        RailwayCoordinates.CellsPerMeter * rate * RailwayCoordinates.PrefetchSeconds / RailwayCoordinates.SegmentLengthCells),
        0, RailwayCoordinates.MaxPrefetchSegments);

    internal WorldLocation.Node? FrontNode(SceneWorldSnapshot snapshot, TrainView train)
    {
        var position = (TrainPosition.Edge)train.Position;
        var edge = ((Lookup<EdgeView>.Found)snapshot.Core.FindEdge(position.Id)).Value.Map;
        var remaining = position.Direction == TravelDirection.Forward ? edge.LengthMeters - position.DistanceMeters : position.DistanceMeters;
        return remaining * RailwayCoordinates.CellsPerMeter <= PrefetchSegments(train.SpeedMetersPerSecond, displayRate.Value) * RailwayCoordinates.SegmentLengthCells
            ? new(position.Direction == TravelDirection.Forward ? edge.End : edge.Start) : null;
    }

    private void ReconcileSegments(SceneWorldSnapshot snapshot, int middle, int minimum, int maximum, TrainView? followed, bool allowBuild)
    {
        var (retainMinimum, retainMaximum) = NeededRange(middle, followed,
            followed is null ? 0 : RailwayCoordinates.MaxPrefetchSegments);
        foreach (var index in pendingSegments.Keys.Where(index => index < retainMinimum || index > retainMaximum).ToArray())
        {
            var work = pendingSegments[index];
            work.Cancellation.Cancel();
            retiredSegments.Add(work);
            pendingSegments.Remove(index);
        }
        foreach (var index in segments.Keys.Where(index => index < retainMinimum || index > retainMaximum).ToArray()) segments.Remove(index);
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
            else if (allowBuild && failure is null && pendingSegments.Count + retiredSegments.Count(work => !work.Task.IsCompleted) < RailwayCoordinates.MaxConcurrentBuilds)
            {
                var currentPath = path!;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                pendingSegments.Add(index, new(Task.Run(() => build(snapshot, currentPath, index, stop.Token), stop.Token), stop));
            }
        }
        waitingSegments = Enumerable.Range(minimum, maximum - minimum + 1).Any(index => !segments.ContainsKey(index));
        cache.Publish(Edge, owner, segments.Values.Select(item => item.Source));
    }

    private void Connect(RailwaySegment segment)
    {
        var keys = segment.Geometry.Objects.ToImmutableDictionary(item => item.Id, item => item.Id.Value.ToString());
        segments[segment.Index] = new(segment, WorldSceneSession.Attach(new(segment.Geometry, keys), "segment:" + segment.Index + ":", objectId).Geometry);
    }

    private void RenderSegments(ScenePoint origin, FrameContents frame)
    {
        var end = 0d;
        foreach (var pair in segments.OrderBy(item => item.Key))
        {
            var start = pair.Key * RailwayCoordinates.SegmentLengthCells;
            if (start > end) AddFog(end, start, origin, frame);
            var item = pair.Value;
            frame.Add(SceneGeometry.Place(item.Geometry, new(RailwayCoordinates.Relative(item.Source.Origin, origin), Quaternion.Identity)));
            end = Math.Min(path!.LengthCells, (pair.Key + 1) * RailwayCoordinates.SegmentLengthCells);
        }
        if (end < path!.LengthCells) AddFog(end, path.LengthCells, origin, frame);
    }

    private void AddFog(double begin, double end, ScenePoint origin, FrameContents frame)
    {
        var halfWidth = RailwayCoordinates.FogHalfWidth(path!.Edge.Tracks);
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
                var instance = ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(instanceId)).Value;
                var design = snapshot.Content.FindDesign(instance.Design);
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
                var pose = RailwayVehicles.Pose(path!.SampleWithNodes(member.CenterArc, train.Offset), origin, train.Positive);
                if (!train.LengthsKnown)
                {
                    frame.Add(SceneVehicles.Unplaced(content, member.Instance, member.Design, member.Length, pose, objectId));
                    continue;
                }
                var geometry = vehicles.Reconcile(content, member.Instance, objectId);
                if (geometry is null)
                {
                    waiting = true;
                    var loading = SceneVehicles.Loading(member.Instance, ((ContentLookup<DesignEvaluation>.Found)member.Design).Value, pose, objectId);
                    frame.Objects.Add(loading.Object);
                    frame.Fog.Add(loading.Fog);
                    continue;
                }
                var placed = RailwayVehicles.Place(geometry, pose, (train.Positive ? 1 : -1) * member.TrackArc);
                var hidden = segments.Values.Any(segment => segment.Source.Tunnels.Any(interval => member.CenterArc >= interval.Start && member.CenterArc <= interval.End));
                frame.Add(placed, hidden);
            }
        }
        return waiting;
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
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try { await Task.WhenAll(pendingSegments.Values.Concat(retiredSegments).Select(work => work.Task)); }
        catch (Exception) { /* Closing observes owned build failures; they cannot publish after cancellation. */ }
        foreach (var work in pendingSegments.Values.Concat(retiredSegments)) work.Cancellation.Dispose();
        cancellation.Dispose();
    }
}

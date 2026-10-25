using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayScene : IAsyncDisposable
{
    private sealed record ConnectedSegment(RailwaySegment Source, RenderedDesign Geometry, ImmutableArray<SceneBounds> Footprint);
    private sealed record SegmentWork(Task<RailwaySegment> Task, CancellationTokenSource Cancellation, SceneStreamingAttemptId? Attempt);
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
    private readonly Func<string, SceneObjectId?> findId;
    private readonly Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment> build;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<int, ConnectedSegment> segments = [];
    private readonly HashSet<int> staleSegments = [];
    private readonly Dictionary<int, SegmentWork> pendingSegments = [];
    private readonly List<SegmentWork> retiredSegments = [];
    private readonly SceneVehicles vehicles;
    private readonly PresentationState? presentation;
    private readonly TerrainCollision.PatchCache terrainPatches = new();
    private RailwayPath? path;
    private SceneProblem? failure;
    private bool waitingSegments;
    private bool fogDirty = true;
    private FarTerrainView? fogFarView;
    private ImmutableArray<SceneFog> segmentFog = [];
    private ImmutableArray<SceneFog> translatedFog = [];
    private ScenePoint? fogOrigin;
    private readonly DisplayRate displayRate;
    private readonly FarTerrain farTerrain;
    private readonly FarTerrainWorker farBuilds;
    private double terrainViewRadiusCells;
    private ScenePoint? terrainFocus;
    private (int Minimum, int Maximum) fogRange;
    internal SceneStreamingScope? Streaming { get; }
    private RailwayStreamingCoverage? streamingCoverage;
    internal EdgeId Edge { get; }
    internal RailwayPath? Path => path;
    internal bool Failed => failure is not null;
    internal bool Ready { get; private set; }
    internal bool HasPendingBuilds => waitingSegments || PendingWork.Any(task => !task.IsCompleted) || vehicles.HasPendingBuilds;
    internal Task[] PendingWork => pendingSegments.Values.Select(item => (Task)item.Task).Concat(vehicles.PendingWork)
        .Concat(retiredSegments.Where(item => !item.Task.IsCompleted).Select(item => item.Task)).ToArray();
    internal Task[] PendingFarWork => farBuilds.PendingWork is { IsCompleted: false } work ? [work] : [];
    internal bool HasPendingCoarseTerrain => terrainViewRadiusCells > 0 && farTerrain.HasPendingCoarse;
    internal void SetTerrainViewRadius(double radius) { if (radius > 0) terrainViewRadiusCells = radius; }
    internal void SetFineTerrainBuildAllowed(bool allowed) => farTerrain.SetFineBuildAllowed(allowed);
    internal void SetFarBuildAllowed(bool allowed) => farBuilds.SetEnabled(allowed);


    internal RailwayScene(SceneWorldSnapshot initial, EdgeId edge, RailwayCache cache, Func<string, SceneObjectId> objectId, SceneVehicles vehicles, DisplayRate displayRate,
        Func<string, SceneObjectId?> findId,
        Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null,
        PresentationState? presentation = null, SceneStreamingScope? streaming = null,
        FarTerrain.Cache? farCache = null)
    {
        Streaming = streaming;
        this.cache = cache;
        this.vehicles = vehicles;
        this.displayRate = displayRate;
        farTerrain = new(farCache ?? new());
        farBuilds = new(farTerrain.Advance);
        Edge = edge;
        owner = cache.OpenSession(edge);
        this.objectId = objectId;
        this.findId = findId;
        this.presentation = presentation;
        this.build = build ?? RailwayTerrain.Build;
        try { path = new RailwayPath(initial, edge, cancellation.Token); }
        catch (RailwayBuildException error) { Fail(error); }
    }

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, WorldSceneTarget target,
        SceneFrameRequest request, SceneFrameStamp stamp, SceneFrame? displayed, ScenePoint? switchedOrigin = null)
    {
        var (start, seconds) = SceneFrame.SampleTime(snapshot, previous, request.Interpolation);
        if (path is null)
        {
            Streaming?.Frame(stamp, [], null);
            return new(stamp, switchedOrigin ?? default, seconds, failure is null ? SceneReadiness.Loading : SceneReadiness.Failed,
                [], [], [new(new SceneBounds(new(float.MinValue), new(float.MaxValue)))], failure is null ? [] : [failure], displayed,
                debugLayers: request.DebugLayers, pickRay: request.PickRay);
        }

        RefreshPath(snapshot);
        var trains = PlaceTrains(snapshot, start, request.Interpolation);
        var followed = target is WorldSceneTarget.Follow follow ? trains.First(item => item.Train.Id == follow.Train) : null;
        var focal = followed is not null
            ? followed.Members.First(item => item.Id == new InstanceId.Vehicle(((WorldSceneTarget.Follow)target).Vehicle)).CenterArc
            : ((WorldLocation.Edge)WorldSceneSession.CurrentLocation(snapshot, target)).DistanceMeters * RailwayCoordinates.CellsPerMeter;
        var camera = request.Camera;
        var hasSameSpaceCamera = camera is not null && camera.Frame.Update.Frame.Space == stamp.Space;
        if (target is not WorldSceneTarget.Follow && hasSameSpaceCamera)
            focal = path.Locate(RailwayCoordinates.Add(camera!.Frame.Origin, camera.Focus));
        var focus = path.Sample(focal).Position;
        terrainFocus = hasSameSpaceCamera ? RailwayCoordinates.Add(camera!.Frame.Origin, camera.Focus) : focus;
        var origin = focus;
        if (target is WorldSceneTarget.Follow || hasSameSpaceCamera)
        {
            var step = RailwayCoordinates.OriginStepCells;
            origin = Snap(focus);
            // 自由观察的第一帧原点就在线路采样点上，尚未落在步进格角。
            // 相机还留在该格内时保持这个原点；按投影重新取整会跳到格角，
            // 显示帧未提交时焦点已经换算，定位就离开正在显示的帧。
            if (hasSameSpaceCamera && target is not WorldSceneTarget.Follow &&
                SameCell(RailwayCoordinates.Add(camera!.Frame.Origin, camera.Focus), camera.Frame.Origin))
                origin = camera.Frame.Origin;

            ScenePoint Snap(ScenePoint point) => new(Math.Floor(point.X / step) * step,
                Math.Floor(point.Y / step) * step, Math.Floor(point.Z / step) * step);
            bool SameCell(ScenePoint left, ScenePoint right) =>
                Math.Floor(left.X / step) == Math.Floor(right.X / step) &&
                Math.Floor(left.Y / step) == Math.Floor(right.Y / step) &&
                Math.Floor(left.Z / step) == Math.Floor(right.Z / step);
        }
        origin = switchedOrigin ?? origin;
        displayRate.Update(seconds);
        var middle = ScheduleSegments(snapshot, focal, followed?.Train);
        var frame = new FrameContents();
        RenderSegments(origin, frame);
        var waiting = false;
        try { waiting = RenderTrains(snapshot, trains, origin, frame, seconds); }
        catch (RailwayBuildException error) { Fail(error); }
        var farView = farTerrain.View;
        frame.Objects.AddRange(farTerrain.Objects(origin, objectId, farView));
        if (failure is not null) frame.Problems.Add(failure);
        RetireCompletedWork();
        var radius = RailwayCoordinates.ViewRadiusSegments;
        Ready = Enumerable.Range(Math.Max(0, middle - radius), Math.Min(path.SegmentCount - 1, middle + radius) -
            Math.Max(0, middle - radius) + 1).All(segments.ContainsKey) && !waiting;
        var terrain = ImmutableArray.CreateBuilder<TerrainCollisionPatch>();
        var terrainKeys = new HashSet<string>();
        foreach (var segment in segments.Values.OrderBy(item => item.Source.Index))
        {
            var key = "terrain:" + segment.Source.Index;
            terrainKeys.Add(key);
            var patch = terrainPatches.Get(key, () => objectId(key), segment.Source.Origin, origin, segment.Source.Heights);
            if (patch is not null) terrain.Add(patch);
        }
        terrainPatches.Retain(terrainKeys);
        var effects = Effects(snapshot, frame.Objects, seconds, origin, segments.Count > 0 && failure is null);
        if (Streaming is { } streaming)
        {
            if (streamingCoverage is null || !ReferenceEquals(streamingCoverage.Path, path))
                streamingCoverage = new(path, streaming.Id);
            streaming.Frame(stamp, segments.Keys.Select(index => streaming.PartId(SceneStreamingPartKind.RailwaySegment, RailwayStreamingCoverage.SegmentKey(index))),
                streamingCoverage.At(origin));
        }
        return new(stamp, origin, seconds, failure is not null ? SceneReadiness.Failed : segments.Count == 0 ? SceneReadiness.Loading :
            Ready ? SceneReadiness.Ready : SceneReadiness.Partial, frame.Objects.ToImmutable(), frame.Picking.ToImmutable(),
            frame.Fog.ToImmutable(), RailwayTerrain.UniqueProblems(frame.Problems), displayed, path,
            segments.Values.Select(item => item.Source).ToImmutableArray(), debugLayers: request.DebugLayers, pickRay: request.PickRay,
            effects: effects, terrain: terrain.DrainToImmutable(), farTerrain: farView);
    }

    internal void Pump(SceneWorldSnapshot snapshot, WorldLocation.Edge location, bool allowBuild = true)
    {
        if (path is null) return;
        RefreshPath(snapshot);
        var focal = location.DistanceMeters * RailwayCoordinates.CellsPerMeter;
        terrainFocus = path.Sample(focal).Position;
        ScheduleSegments(snapshot, focal, null, allowBuild);
        RetireCompletedWork();
    }

    private int ScheduleSegments(SceneWorldSnapshot snapshot, double focal, TrainView? followed, bool allowBuild = true)
    {
        var middle = Math.Clamp((int)(focal / RailwayCoordinates.SegmentLengthCells), 0, path!.SegmentCount - 1);
        var (minimum, maximum) = NeededRange(middle, followed);
        ReconcileSegments(snapshot, focal, middle, minimum, maximum, followed, allowBuild);
        PumpFar(snapshot, focal, followed, allowBuild, minimum, maximum);
        return middle;
    }

    private void PumpFar(SceneWorldSnapshot snapshot, double focal, TrainView? followed,
        bool allowBuild, int minimum, int maximum)
    {
        if (terrainViewRadiusCells > 0)
        {
            farTerrain.BindRailway(snapshot, path!);
            var direction = followed?.Position is TrainPosition.Edge { Direction: TravelDirection.Reverse } ? -1 : 1;
            var ahead = Math.Clamp(focal + direction * PrefetchSegments(followed?.SpeedMetersPerSecond ?? 0,
                displayRate.Value) * RailwayCoordinates.SegmentLengthCells, 0, path!.LengthCells);
            var sweep = followed is null ? null : path.Bounds(Math.Min(focal, ahead), Math.Max(focal, ahead), default, 0);
            var focus = terrainFocus ?? path.Sample(focal).Position;
            if (farTerrain.Demand(focus, terrainViewRadiusCells,
                ahead: sweep is null ? null : [new(sweep.Minimum.X, 0, sweep.Minimum.Z), new(sweep.Maximum.X, 0, sweep.Maximum.Z)])) fogDirty = true;
        }
        var waitingL0 = Enumerable.Range(minimum, maximum - minimum + 1).Any(index =>
            (!segments.ContainsKey(index) || staleSegments.Contains(index)) && !pendingSegments.ContainsKey(index));
        var occupied = pendingSegments.Values.Count(work => !work.Task.IsCompleted) + retiredSegments.Count(work => !work.Task.IsCompleted);
        farBuilds.Pump(occupied, !waitingL0 && allowBuild && failure is null && farTerrain.HasRunnableWork);
        if (farBuilds.Failure is { } error) Fail(new RailwayBuildException(error.Message));
    }

    private void RefreshPath(SceneWorldSnapshot snapshot)
    {
        if (path is null || failure is not null) return;
        if (snapshot.Core.FindEdge(Edge) is not Lookup<EdgeView>.Found found) return;
        var map = found.Value.Map;
        if (map.Tracks == path.Edge.Tracks && map.Breaks.SequenceEqual(path.Edge.Breaks) &&
            RepairIdentity(map).SequenceEqual(RepairIdentity(path.Edge))) return;
        try { path = new RailwayPath(snapshot, Edge, cancellation.Token); }
        catch (RailwayBuildException error) { Fail(error); return; }
        staleSegments.Clear();
        foreach (var pair in segments)
            if (!pair.Value.Source.Dependencies.Matches(snapshot, path, pair.Key)) staleSegments.Add(pair.Key);
    }

    internal static ImmutableArray<(ulong Job, int Track, ulong Break)> RepairIdentity(EdgeMapItem edge) =>
        edge.Works.Where(RailwayCoordinates.IsBreakRepair)
            .OrderBy(work => work.Id.Value)
            .Select(work => (work.Id.Value, work.Track, work.RepairingBreak!.Value.Value))
            .ToImmutableArray();

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

    private void ReconcileSegments(SceneWorldSnapshot snapshot, double focal, int middle, int minimum, int maximum, TrainView? followed, bool allowBuild)
    {
        farBuilds.Pause();
        if (fogRange != (minimum, maximum)) { fogRange = (minimum, maximum); fogDirty = true; }
        Streaming?.Demand(SceneStreamingPartKind.RailwaySegment, Enumerable.Range(minimum, maximum - minimum + 1).Select(RailwayStreamingCoverage.SegmentKey));
        var (retainMinimum, retainMaximum) = NeededRange(middle, followed,
            followed is null ? 0 : RailwayCoordinates.MaxPrefetchSegments);
        foreach (var index in pendingSegments.Keys.Where(index => index < retainMinimum || index > retainMaximum).ToArray())
        {
            var work = pendingSegments[index];
            work.Cancellation.Cancel();
            if (work.Attempt is { } cancelled) Streaming?.Cancel(cancelled);
            retiredSegments.Add(work);
            pendingSegments.Remove(index);
        }
        foreach (var index in segments.Keys.Where(index => index < retainMinimum || index > retainMaximum).ToArray())
        { segments.Remove(index); fogDirty = true; }
        foreach (var pair in pendingSegments.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            pendingSegments.Remove(pair.Key);
            try
            {
                var result = pair.Value.Task.GetAwaiter().GetResult();
                if (result.Dependencies.Matches(snapshot, path!, result.Index)) Connect(result, pair.Value.Attempt);
                else if (pair.Value.Attempt is { } rejected) Streaming?.Cancel(rejected);
            }
            catch (RailwayBuildException error) { Fail(error); }
            catch (OperationCanceledException) { }
            finally { pair.Value.Cancellation.Dispose(); }
        }
        foreach (var index in Enumerable.Range(minimum, maximum - minimum + 1).OrderBy(index => Math.Abs(index - middle)))
        {
            if (segments.ContainsKey(index) && !staleSegments.Contains(index)) continue;
            if (pendingSegments.ContainsKey(index)) continue;
            if (cache.Find(snapshot, path!, index) is { } cached)
            {
                var attempt = Streaming?.Begin(SceneStreamingPartKind.RailwaySegment, RailwayStreamingCoverage.SegmentKey(index));
                if (attempt is { } hit) Streaming?.CacheHit(hit);
                Connect(cached, attempt);
            }
            else if (allowBuild && failure is null && pendingSegments.Count + retiredSegments.Count(work => !work.Task.IsCompleted) +
                farBuilds.RunningCount < RailwayCoordinates.MaxConcurrentBuilds)
            {
                var currentPath = path!;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                var attempt = Streaming?.Begin(SceneStreamingPartKind.RailwaySegment, RailwayStreamingCoverage.SegmentKey(index));
                if (attempt is { } queued) Streaming?.Queue(queued);
                pendingSegments.Add(index, new(Task.Run(() => attempt is { } captured
                    ? Streaming!.Build(captured, () => build(snapshot, currentPath, index, stop.Token))
                    : build(snapshot, currentPath, index, stop.Token), stop.Token), stop, attempt));
            }
        }
        waitingSegments = Enumerable.Range(minimum, maximum - minimum + 1).Any(index => !segments.ContainsKey(index));
        cache.Publish(Edge, owner, segments.Values.Select(item => item.Source));
    }

    private void Connect(RailwaySegment segment, SceneStreamingAttemptId? attempt)
    {
        staleSegments.Remove(segment.Index);
        var keys = segment.Geometry.Objects.ToImmutableDictionary(item => item.Id, item => item.Id.Value.ToString());
        var start = segment.Index * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(path!.LengthCells, (segment.Index + 1) * RailwayCoordinates.SegmentLengthCells);
        var footprint = (segment.Heights.Count > 0
            ? ConstructionFog.Columns(segment.Heights.Keys.Select(column => new GridColumn(
                checked(column.X + (int)segment.Origin.X), checked(column.Z + (int)segment.Origin.Z))))
            : ConstructionFog.Band(arc => path.Sample(arc), start, end,
                RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks), -128, 128))
            .AddRange(RailwayMeshes.Footprint(arc => path.Sample(arc), start, end, path.Edge.Tracks, -128, 128));
        segments[segment.Index] = new(segment, WorldSceneSession.Attach(new(segment.Geometry, keys),
            "segment:" + segment.Index + ":", objectId).Geometry, footprint);
        if (Streaming is { } streaming && attempt is { } admitted)
            streaming.Admit(admitted, segments[segment.Index].Geometry.Objects.Select(item => item.Id).ToImmutableArray());
        fogDirty = true;
    }

    private void RenderSegments(ScenePoint origin, FrameContents frame)
    {
        var farView = farTerrain.View;
        if (!ReferenceEquals(fogFarView, farView)) { fogDirty = true; fogFarView = farView; }
        foreach (var pair in segments.OrderBy(item => item.Key))
        {
            var item = pair.Value;
            frame.Add(SceneGeometry.Place(item.Geometry, new(RailwayCoordinates.Relative(item.Source.Origin, origin), Quaternion.Identity)));
        }
        if (fogDirty)
        {
            var fog = ImmutableArray.CreateBuilder<SceneFog>();
            var footprint = segments.Values.SelectMany(segment => segment.Footprint).ToArray();
            var end = fogRange.Minimum * RailwayCoordinates.SegmentLengthCells;
            var finish = Math.Min(path!.LengthCells, (fogRange.Maximum + 1) * RailwayCoordinates.SegmentLengthCells);
            foreach (var index in segments.Keys.Where(index => index >= fogRange.Minimum && index <= fogRange.Maximum).Order())
            {
                var start = index * RailwayCoordinates.SegmentLengthCells;
                if (start > end) AddFog(end, start);
                end = Math.Min(path!.LengthCells, (index + 1) * RailwayCoordinates.SegmentLengthCells);
            }
            if (end < finish) AddFog(end, finish);
            for (var index = 0; index < path.SegmentCount; index++)
            {
                if (index >= fogRange.Minimum && index <= fogRange.Maximum || segments.ContainsKey(index)) continue;
                var begin = index * RailwayCoordinates.SegmentLengthCells;
                var endOfSegment = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
                var area = path.Bounds(begin, endOfSegment, default, RailwayCoordinates.FogHalfWidth(path.Edge.Tracks));
                if (!farTerrain.IntersectsDemand(area)) continue;
                AddFog(begin, endOfSegment, allowFar: true);
            }
            segmentFog = fog.ToImmutable();
            fogDirty = false;
            fogOrigin = null;

            void AddFog(double begin, double finish, bool allowFar = false)
            {
                if (finish <= begin) return;
                var width = RailwayCoordinates.FogHalfWidth(path!.Edge.Tracks);
                var area = path.Bounds(begin, finish, default, width);
                var cutters = allowFar ? footprint.Concat(farView.Coverage(area)) : footprint;
                foreach (var bounds in ConstructionFog.Clip(allowFar
                    ? ConstructionFog.Band(arc => path.Sample(arc), begin, finish, width, area.Minimum.Y, area.Maximum.Y)
                    : [area], cutters))
                    fog.Add(new(bounds, new(begin, finish, width)));
            }
        }
        if (fogOrigin != origin)
        {
            var shift = RailwayCoordinates.Relative(default, origin);
            translatedFog = segmentFog.Select(region => region with
            { Bounds = new(region.Bounds.Minimum + shift, region.Bounds.Maximum + shift) }).ToImmutableArray();
            fogOrigin = origin;
        }
        frame.Fog.AddRange(translatedFog);
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

    private ImmutableArray<SceneEffectCue> Effects(SceneWorldSnapshot snapshot, ImmutableArray<SceneObject>.Builder objects,
        double seconds, ScenePoint origin, bool emit)
    {
        if (presentation is not { } state || path is null) return [];
        state.AddCreatures(objects, snapshot, new WorldLocation.Edge(Edge, 0), origin, path, null, seconds, objectId);
        return emit ? state.Finish(objects, snapshot, new WorldLocation.Edge(Edge, 0), origin, path) : [];
    }

    private bool RenderTrains(SceneWorldSnapshot snapshot, ImmutableArray<TrainPlacement> trains, ScenePoint origin,
        FrameContents frame, double seconds)
    {
        var waiting = false;
        foreach (var train in trains)
        {
            if (!train.TrackAvailable) continue;
            foreach (var member in train.Members)
            {
                var pose = RailwayVehicles.Pose(path!.SampleWithNodes(member.CenterArc, train.Offset,
                    ((TrainPosition.Edge)train.Train.Position).Track), origin, train.Positive);
                if (!train.LengthsKnown)
                {
                    frame.Add(SceneVehicles.Unplaced(snapshot.Content, member.Instance, member.Design, member.Length, pose, objectId));
                    continue;
                }
                var geometry = vehicles.Reconcile(snapshot.Content, member.Instance, objectId);
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
                presentation?.ApplyTurrets(frame.Objects, member.Instance, "vehicle:" + member.Instance.Id + ":", findId, seconds, snapshot, origin);
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
        var closingFar = farBuilds.DisposeAsync().AsTask();
        Streaming?.CancelPending();
        try { await Task.WhenAll(pendingSegments.Values.Concat(retiredSegments).Select(work => work.Task).Append(closingFar)); }
        catch (Exception) { /* Closing observes owned build failures; they cannot publish after cancellation. */ }
        foreach (var work in pendingSegments.Values.Concat(retiredSegments)) work.Cancellation.Dispose();
        cancellation.Dispose();
        Streaming?.Close();
    }
}

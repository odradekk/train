using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record WorldSceneObject(InstanceId Instance, ScenePose Pose);
internal sealed record SceneMissingMarker(ScenePose Pose, ContentReference Reference, SceneProblem Problem);
internal sealed record WorldSceneContent(ImmutableArray<SceneCell> Terrain,
    ImmutableArray<WorldSceneObject> Objects, ImmutableArray<SceneMesh> BuiltInMeshes,
    ImmutableArray<SceneBounds> FoggedRegions, ImmutableArray<SceneMissingMarker> MissingMarkers,
    ScenePoint Origin = default);

internal sealed class WorldSceneSession : IWorldSceneSession
{
    private readonly Func<SceneWorldSnapshot, WorldSceneTarget, CancellationToken, WorldSceneContent> generate;
    private readonly RailwayCache railwayCache;
    private readonly NodeScene.Cache nodeCache;
    private readonly SceneVehicles vehicles;
    private readonly DisplayRate displayRate;
    private readonly Dictionary<NodeId, (SceneVehicles.Cache Buildings, SceneVehicles.Cache Parked)> settlementCaches = [];
    private RailwayScene? railway;
    private NodeScene? node;
    private sealed record Candidate(WorldLocation Location, Dictionary<string, SceneObjectId> Ids, NodeScene? Node = null, RailwayScene? Railway = null);
    private Candidate? candidate;
    private WorldLocation? rejectedCandidate;
    private Task? retiringCandidate;
    // Nodes retain the handoff origin for their whole scene; railways use it only for the first frame.
    private ScenePoint? sceneOrigin;
    private readonly Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built>? nodeBuild;
    private readonly List<Task> closingScenes = [];
    private SceneWorldSnapshot? interpolationStart;
    private readonly List<(Task Task, CancellationTokenSource Cancellation)> tasks = [];
    private readonly Dictionary<InstanceId, Task<BuiltObject>> rebuilds = [];
    private Dictionary<string, SceneObjectId> ids = [];
    private readonly Dictionary<InstanceId, BuiltObject> designs = [];
    private SceneWorldSnapshot snapshot;
    private WorldSceneTarget? target;
    private Task<BuiltWorld>? pending;
    private WorldSceneContent? content;
    private RenderedDesign? scenery;
    private EventEnvelope? lastEvent;
    private SceneProblem? fault;
    private SceneRequestId request;
    private SceneSpaceId space;
    private ulong revision;
    private ulong nextObject;
    private Task? closing;
    private bool disposed;
    public SceneSessionId Id { get; } = new(Guid.NewGuid());
    public SceneBinding Binding { get; }
    internal Task? PendingBuild => pending ?? node?.BuildWork;
    internal Task[] PendingObjectBuilds => rebuilds.Values.Cast<Task>().Concat(vehicles.PendingWork).ToArray();
    internal Task[] PendingRailwayBuilds => railway?.PendingWork ?? [];
    internal Task[] PendingNodeBuilds => node?.PendingWork ?? [];
    internal Task[] PendingPrebuilds => (candidate?.Node?.PendingWork ?? []).Concat(candidate?.Railway?.PendingWork ?? [])
        .Concat(retiringCandidate is { IsCompleted: false } ? [retiringCandidate] : []).ToArray();
    internal WorldLocation? PrebuiltLocation => candidate?.Location;
    internal Func<long> Timestamp { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp;

    private sealed record BuiltWorld(WorldSceneContent Content, BuiltGeometry Scenery,
        ImmutableDictionary<InstanceId, BuiltObject> Objects);

    internal WorldSceneSession(SceneBinding binding, SceneWorldSnapshot initial,
        Func<SceneWorldSnapshot, WorldSceneTarget, CancellationToken, WorldSceneContent> generate, RailwayCache railwayCache,
        NodeScene.Cache nodeCache,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built>? nodeBuild = null, SceneVehicles.Cache? vehicleCache = null)
    {
        ValidateSnapshot(initial);
        Binding = binding;
        snapshot = initial;
        this.generate = generate;
        this.railwayCache = railwayCache;
        this.nodeCache = nodeCache;
        this.nodeBuild = nodeBuild;
        vehicles = new(vehicleCache);
        displayRate = new() { Timestamp = () => Timestamp() };
    }

    internal static void ValidateSnapshot(SceneWorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Core);
        ArgumentNullException.ThrowIfNull(snapshot.Content);
        if (snapshot.Core.InputIdentity != snapshot.Content.Core.Identity)
            throw new ArgumentException("Core and content input identities differ.", nameof(snapshot));
    }

    internal static WorldSceneContent Generate(SceneWorldSnapshot snapshot, WorldSceneTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (target is WorldSceneTarget.Combat)
            throw new NotImplementedException("Combat scene generation is implemented by Issue #353.");
        var location = CurrentLocation(snapshot, target);
        if (location is WorldLocation.Node)
            throw new ArgumentException("Node generation uses the world-session node path.");
        throw new ArgumentException("Railway generation uses the segmented world-session path.");
    }

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        if (fault is not null) return new SceneResult<SceneRequestId>.Faulted(fault);
        if (!Exists(value)) return Failure<SceneRequestId>("target_missing", SceneStage.Input);
        RetireCompletedTasks();
        CancelCandidate(); rejectedCandidate = null; sceneOrigin = null;
        foreach (var work in tasks) work.Cancellation.Cancel();
        if (railway is not null) { closingScenes.Add(railway.DisposeAsync().AsTask()); railway = null; }
        if (node is not null) { closingScenes.Add(node.DisposeAsync().AsTask()); node = null; }
        target = value;
        request = new(request.Value + 1);
        space = new(space.Value + 1);
        content = null;
        scenery = null;
        designs.Clear();
        rebuilds.Clear();
        ids.Clear();
        if (value is not WorldSceneTarget.Combat && CurrentLocation(snapshot, value) is WorldLocation.Edge edge)
        {
            railway = new(snapshot, edge.Id, railwayCache, ObjectId, vehicles: vehicles, displayRate: displayRate);
            pending = null;
            PruneCaches();
            return new SceneResult<SceneRequestId>.Success(request);
        }
        if (value is not WorldSceneTarget.Combat && CurrentLocation(snapshot, value) is WorldLocation.Node location)
        {
            node = OpenNode(location.Id, ObjectId);
            pending = null;
            PruneCaches();
            return new SceneResult<SceneRequestId>.Success(request);
        }
        var input = snapshot;
        var cancellation = new CancellationTokenSource();
        var generator = generate;
        pending = Task.Run(() => BuildWorld(input, value, generator, cancellation.Token), cancellation.Token);
        tasks.Add((pending, cancellation));
        PruneCaches();
        return new SceneResult<SceneRequestId>.Success(request);
    }

    private static WorldLocation CurrentLocation(SceneWorldSnapshot snapshot, WorldSceneTarget value) => value switch
    {
        WorldSceneTarget.Location location => location.Value,
        WorldSceneTarget.Follow follow => ((Lookup<TrainView>.Found)snapshot.Core.FindTrain(follow.Train)).Value.Position switch
        {
            TrainPosition.Node node => new WorldLocation.Node(node.Id),
            TrainPosition.Edge edge => new WorldLocation.Edge(edge.Id, edge.DistanceMeters),
            _ => throw new ArgumentException("Invalid train position.")
        },
        _ => throw new ArgumentException("Invalid location target.")
    };

    private bool Exists(WorldSceneTarget value) => value switch
    {
        WorldSceneTarget.Location location => Exists(location.Value),
        WorldSceneTarget.Follow follow => snapshot.Core.FindTrain(follow.Train) is Lookup<TrainView>.Found train &&
            train.Value.Vehicles.Contains(follow.Vehicle),
        WorldSceneTarget.Combat combat => snapshot.Core.FindCombat(combat.Id) is Lookup<CombatView>.Found,
        _ => throw new ArgumentException("Invalid target.", nameof(value))
    };
    private bool Exists(WorldLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location is WorldLocation.Edge edge)
        {
            if (!double.IsFinite(edge.DistanceMeters) || edge.DistanceMeters < 0)
                throw new ArgumentException("Invalid edge distance.", nameof(location));
            return snapshot.Core.FindEdge(edge.Id) is Lookup<EdgeView>.Found view && edge.DistanceMeters <= view.Value.Map.LengthMeters;
        }
        return location is WorldLocation.Node node
            ? snapshot.Core.FindNode(node.Id) is Lookup<NodeView>.Found
            : throw new ArgumentException("Invalid location.", nameof(location));
    }

    public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot input, ImmutableArray<EventEnvelope> events)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateSnapshot(input);
        if (input.Content.Generation != snapshot.Content.Generation || input.Core.InputIdentity != snapshot.Core.InputIdentity ||
            Compare(input.Core.Position, snapshot.Core.Position) < 0 || events.IsDefault)
            throw new ArgumentException("Observe requires matching content, nondecreasing simulation position and initialized events.");
        var previous = lastEvent;
        foreach (var item in events)
        {
            if (item is null || item.Event is null || item.Ordinal < 0 || Compare(item.Position, input.Core.Position) > 0 ||
                previous is not null && (Compare(item.Position, previous.Position) < 0 ||
                    item.Position == previous.Position && item.Ordinal <= previous.Ordinal))
                throw new ArgumentException("Events must be ordered, new and no later than the input boundary.", nameof(events));
            previous = item;
        }
        if (fault is not null) return new SceneResult<SimulationPosition>.Faulted(fault);
        if (input.Core.Position.CompletedSteps != snapshot.Core.Position.CompletedSteps)
            interpolationStart = input.Core.Position.CompletedSteps == snapshot.Core.Position.CompletedSteps + 1 ? snapshot : null;
        snapshot = input;
        lastEvent = previous;
        return new SceneResult<SimulationPosition>.Success(input.Core.Position);
    }

    public SceneResult<ISceneFrame> Present(SceneFrameRequest frameRequest, ISceneFrame? displayedFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frameRequest);
        if (!double.IsFinite(frameRequest.Interpolation) || frameRequest.Interpolation < 0 || frameRequest.Interpolation > 1 ||
            (frameRequest.DebugLayers & ~(SceneDebugLayer.Chunks | SceneDebugLayer.RailwaySegments | SceneDebugLayer.Origin |
                SceneDebugLayer.PickRay | SceneDebugLayer.Centerline)) != 0)
            throw new ArgumentException("Invalid frame request.", nameof(frameRequest));
        if (displayedFrame is not null && (displayedFrame is not SceneFrame || displayedFrame.Update.Frame.Session != Id))
            throw new ArgumentException("The displayed frame belongs to another session.", nameof(displayedFrame));
        if (frameRequest.Camera is { } camera && (!SceneGeometry.Finite(camera.Focus) || camera.Frame.Update.Frame.Session != Id))
            throw new ArgumentException("Camera must use this session's displayed space.", nameof(frameRequest));
        if (frameRequest.PickRay is { } pickRay)
        {
            if (pickRay.Frame is null || pickRay.Frame.Update.Frame.Session != Id)
                throw new ArgumentException("The pick ray belongs to another session.", nameof(frameRequest));
            SceneGeometry.Validate(pickRay.Ray);
        }
        if (fault is not null) return new SceneResult<ISceneFrame>.Faulted(fault);
        if (target is null || !Exists(target)) return Failure<ISceneFrame>("target_missing", SceneStage.Present);
        try
        {
            if (target is not WorldSceneTarget.Combat)
                vehicles.Retain(snapshot.Core.World.Trains.Select(item => snapshot.Core.FindTrain(item.Id))
                    .OfType<Lookup<TrainView>.Found>().SelectMany(item => item.Value.Vehicles).Select(id => (InstanceId)new InstanceId.Vehicle(id)));
            if (target is WorldSceneTarget.Follow)
            {
                var location = CurrentLocation(snapshot, target);
                if (railway is not null && (location is not WorldLocation.Edge edge || edge.Id != railway.Edge) ||
                    node is not null && (location is not WorldLocation.Node atNode || atNode.Id != node.Node))
                    Switch(location, displayedFrame as SceneFrame);
            }
            closingScenes.RemoveAll(task => task.IsCompleted);
            if (node is not null || railway is not null)
            {
                candidate?.Node?.SetBuildAllowed(false);
                var stamp = new SceneFrameStamp(Id, request, space, ++revision);
                var frame = node is not null
                    ? node.Present(snapshot, frameRequest, stamp, displayedFrame as SceneFrame, sceneOrigin ?? default, target, interpolationStart)
                    : railway!.Present(snapshot, interpolationStart, target, frameRequest, stamp, displayedFrame as SceneFrame, sceneOrigin);
                if (railway is not null) sceneOrigin = null;
                var liveIds = frame.Objects.Select(item => item.Id).ToHashSet();
                foreach (var key in ids.Where(item => !liveIds.Contains(item.Value)).Select(item => item.Key).ToArray()) ids.Remove(key);
                Prebuild();
                PruneCaches();
                return new SceneResult<ISceneFrame>.Success(frame);
            }
            if (content is null && pending!.IsCompleted)
            {
                var built = pending.GetAwaiter().GetResult();
                pending = null;
                content = built.Content;
                scenery = Attach(built.Scenery, "", ObjectId).Geometry;
                foreach (var item in built.Objects)
                    if (snapshot.Core.FindInstance(item.Key) is Lookup<InstanceView>.Found current && Matches(item.Value, current.Value))
                        Accept(item.Key, item.Value);
            }
            var objects = ImmutableArray.CreateBuilder<SceneObject>();
            var picking = ImmutableArray.CreateBuilder<PickGrid>();
            var problems = ImmutableArray.CreateBuilder<SceneProblem>();
            var rebuilding = false;
            if (scenery is not null)
            {
                objects.AddRange(scenery.Objects);
                picking.AddRange(scenery.Picking);
                problems.AddRange(scenery.Diagnostics);
                rebuilding = Reconcile(objects, picking, problems);
            }
            var fog = content?.FoggedRegions ?? [new SceneBounds(new(float.MinValue), new(float.MaxValue))];
            var readiness = content is null ? SceneReadiness.Loading :
                rebuilding || !fog.IsEmpty ? SceneReadiness.Partial : SceneReadiness.Ready;
            var next = new SceneFrame(new(Id, request, space, ++revision), content?.Origin ?? new(0, 0, 0),
                snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds, readiness,
                objects.ToImmutable(), picking.ToImmutable(), fog.Select(bounds => new SceneFog(bounds)).ToImmutableArray(),
                problems.ToImmutable(), displayedFrame as SceneFrame, debugLayers: frameRequest.DebugLayers, pickRay: frameRequest.PickRay);
            var live = next.Objects.Select(item => item.Id).ToHashSet();
            foreach (var key in ids.Where(item => !live.Contains(item.Value)).Select(item => item.Key).ToArray()) ids.Remove(key);
            RetireCompletedTasks();
            return new SceneResult<ISceneFrame>.Success(next);
        }
        catch (Exception error)
        {
            fault = new("scene_internal_fault", SceneStage.Build, null, null, null, null, error.ToString());
            return new SceneResult<ISceneFrame>.Faulted(fault);
        }
    }

    private void RetireCompletedTasks()
    {
        foreach (var work in tasks.Where(work => work.Task.IsCompleted).ToArray())
        {
            _ = work.Task.Exception;
            work.Cancellation.Dispose();
            tasks.Remove(work);
        }
    }

    private SceneObjectId ObjectId(string key)
    {
        return AssignId(ids, key);
    }
    private SceneObjectId AssignId(Dictionary<string, SceneObjectId> map, string key)
    {
        if (!map.TryGetValue(key, out var value)) map.Add(key, value = new(++nextObject));
        return value;
    }

    private static bool SameLocation(WorldLocation? left, WorldLocation right) => (left, right) switch
    {
        (WorldLocation.Node a, WorldLocation.Node b) => a.Id == b.Id,
        (WorldLocation.Edge a, WorldLocation.Edge b) => a.Id == b.Id,
        _ => false
    };
    private void Switch(WorldLocation location, SceneFrame? displayed)
    {
        if (railway is not null) { closingScenes.Add(railway.DisposeAsync().AsTask()); railway = null; }
        if (node is not null) { closingScenes.Add(node.DisposeAsync().AsTask()); node = null; }
        space = new(space.Value + 1);
        if (candidate is { } next && SameLocation(next.Location, location) && next.Node is not { Layout: null })
        {
            railway = next.Railway; node = next.Node; ids = next.Ids;
            candidate = null;
        }
        else
        {
            CancelCandidate(); ids = [];
            var map = ids;
            if (location is WorldLocation.Edge edge) railway = new(snapshot, edge.Id, railwayCache, key => AssignId(map, key), vehicles: vehicles, displayRate: displayRate);
            else if (location is WorldLocation.Node atNode) node = OpenNode(atNode.Id, key => AssignId(map, key));
            else throw new ArgumentException("Invalid follow location.");
        }
        sceneOrigin = displayed is null ? null : SceneFrame.SwitchOrigin(displayed, node?.Layout, railway?.Path);
    }
    private void CancelCandidate()
    {
        if (candidate is not { } old) return;
        retiringCandidate = old.Node?.DisposeAsync().AsTask() ?? old.Railway!.DisposeAsync().AsTask();
        closingScenes.Add(retiringCandidate);
        candidate = null;
    }
    private void PruneCaches()
    {
        railwayCache.Retain(railway?.Edge, candidate?.Railway?.Edge);
        foreach (var id in settlementCaches.Keys.Where(id => id != node?.Node && id != candidate?.Node?.Node).ToArray())
            settlementCaches.Remove(id);
    }
    private NodeScene OpenNode(NodeId id, Func<string, SceneObjectId> objectId, bool background = false)
    {
        if (!settlementCaches.TryGetValue(id, out var caches))
            settlementCaches.Add(id, caches = (new(), new()));
        return new(snapshot, id, nodeCache, objectId, vehicles, nodeBuild, caches.Buildings, caches.Parked, background: background, displayRate: displayRate);
    }
    private void Prebuild()
    {
        if (target is not WorldSceneTarget.Follow follow) return;
        var train = ((Lookup<TrainView>.Found)snapshot.Core.FindTrain(follow.Train)).Value;
        WorldLocation? wanted = null;
        var startCandidate = true;
        if (railway is not null && train.Position is TrainPosition.Edge at && snapshot.Core.FindEdge(at.Id) is Lookup<EdgeView>.Found current)
        {
            wanted = new WorldLocation.Node(at.Direction == TravelDirection.Forward ? current.Value.Map.End : current.Value.Map.Start);
            startCandidate = railway.FrontNode(snapshot, train) is not null;
        }
        if (node is not null && (train.Position as TrainPosition.Node)?.NextEdge is { } next && snapshot.Core.FindEdge(next) is Lookup<EdgeView>.Found edge)
            wanted = new WorldLocation.Edge(next, edge.Value.Map.Start == node.Node ? 0 : edge.Value.Map.LengthMeters);
        if (wanted is null || !SameLocation(candidate?.Location, wanted)) CancelCandidate();
        if (wanted is null) { rejectedCandidate = null; return; }
        if (startCandidate && SameLocation(rejectedCandidate, wanted)) return;
        rejectedCandidate = null;
        var allowBuild = node?.HasPendingBuilds != true && railway?.HasPendingBuilds != true;
        // Keep at most one background candidate alive, including cancellation that is still draining.
        if (allowBuild && startCandidate && candidate is null && retiringCandidate?.IsCompleted != false)
        {
            var map = new Dictionary<string, SceneObjectId>();
            candidate = wanted is WorldLocation.Node atNode
                ? new(wanted, map, Node: OpenNode(atNode.Id, key => AssignId(map, key), background: true))
                : new(wanted, map, Railway: new(snapshot, ((WorldLocation.Edge)wanted).Id, railwayCache, key => AssignId(map, key), vehicles: vehicles, displayRate: displayRate));
        }
        try
        {
            if (candidate?.Node is { } candidateNode)
            {
                var entry = candidateNode.Layout?.Entries.FirstOrDefault(item => item.Edge == railway?.Edge);
                candidateNode.Pump(snapshot, entry is null ? null : candidateNode.Layout!.TrainBoundary(entry).Position,
                    allowBuild, railway?.Edge);
            }
            if (wanted is WorldLocation.Edge atEdge) candidate?.Railway?.Pump(snapshot, atEdge, allowBuild);
            if (candidate?.Node?.Failed == true || candidate?.Railway?.Failed == true)
            { rejectedCandidate = wanted; CancelCandidate(); }
        }
        catch (Exception) { rejectedCandidate = wanted; CancelCandidate(); }
    }

    internal static BuiltGeometry BuildGeometry(Func<Func<string, SceneObjectId>, RenderedDesign> build)
    {
        var keys = ImmutableDictionary.CreateBuilder<SceneObjectId, string>();
        ulong next = 0;
        var geometry = build(key =>
        {
            var id = new SceneObjectId(++next);
            keys.Add(id, key);
            return id;
        });
        return new(geometry, keys.ToImmutable());
    }

    private static BuiltWorld BuildWorld(SceneWorldSnapshot input, WorldSceneTarget target,
        Func<SceneWorldSnapshot, WorldSceneTarget, CancellationToken, WorldSceneContent> generate,
        CancellationToken cancellation)
    {
        var content = generate(input, target, cancellation);
        var scenery = BuildGeometry(id => BuildScenery(content, id));
        var objects = ImmutableDictionary.CreateBuilder<InstanceId, BuiltObject>();
        foreach (var item in content.Objects)
        {
            cancellation.ThrowIfCancellationRequested();
            if (input.Core.FindInstance(item.Instance) is Lookup<InstanceView>.Found found)
                objects.Add(item.Instance, BuildObject(input.Content, found.Value, item.Pose));
        }
        return new(content with { Terrain = [] }, scenery, objects.ToImmutable());
    }

    internal static ImmutableHashSet<PartId> Destroyed(InstanceView instance) =>
        instance.Parts.Where(part => part.Destroyed).Select(part => part.Part).ToImmutableHashSet();

    internal static bool Matches(BuiltObject built, InstanceView current) =>
        built.Design == current.Design && built.Destroyed.SetEquals(Destroyed(current));

    internal static BuiltObject BuildObject(IContentView content, InstanceView instance, ScenePose pose)
    {
        var destroyed = Destroyed(instance);
        var geometry = BuildGeometry(id =>
        {
            var subject = new WorldObjectId.Instance(instance.Id);
            var lookup = content.FindDesign(instance.Design);
            if (lookup is ContentLookup<DesignEvaluation>.Found design && design.Value is not DesignEvaluation.Failed)
                return DesignGeometry.Build(content, design.Value, subject, pose, id, destroyed, instance.Design);
            var problems = ImmutableArray.CreateBuilder<SceneProblem>();
            if (lookup is ContentLookup<DesignEvaluation>.Missing missing)
                DesignGeometry.AddDiagnostics(missing.Diagnostics, problems, subject);
            if (lookup is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Failed failed })
                DesignGeometry.AddDiagnostics(failed.Diagnostics, problems, subject);
            var reference = new ContentReference.Design(instance.Design);
            var source = problems.LastOrDefault(problem => problem.Content == reference && problem.Package is not null);
            problems.Add(new("content_missing", SceneStage.Reconcile, subject, reference,
                source?.Package, source?.Element, "Missing object design."));
            return new([new(id(""), null, pose, subject, [new SceneVisual.Missing(reference, false)], [], [])],
                [], problems.ToImmutable());
        });
        return new(instance.Design, destroyed, geometry);
    }

    internal static BuiltGeometry Attach(BuiltGeometry built, string prefix, Func<string, SceneObjectId> objectId)
    {
        var assigned = built.Keys.ToDictionary(item => item.Key, item => objectId(prefix + item.Value));
        var geometry = built.Geometry with
        {
            Objects = built.Geometry.Objects.Select(item => item with
            {
                Id = assigned[item.Id],
                Parent = item.Parent is { } parent ? assigned[parent] : null
            }).ToImmutableArray(),
            Picking = built.Geometry.Picking.Select(item => item with { Object = assigned[item.Object] }).ToImmutableArray()
        };
        return new(geometry, built.Keys.ToImmutableDictionary(item => assigned[item.Key], item => item.Value));
    }

    private void Accept(InstanceId instance, BuiltObject built) =>
        designs[instance] = built with
        {
            Geometry = Attach(built.Geometry, "instance:" + instance + ":", ObjectId)
        };

    private static RenderedDesign BuildScenery(WorldSceneContent value, Func<string, SceneObjectId> id)
    {
        if (value.Terrain.IsDefault || value.Objects.IsDefault || value.BuiltInMeshes.IsDefault ||
            value.FoggedRegions.IsDefault || value.MissingMarkers.IsDefault ||
            !double.IsFinite(value.Origin.X) || !double.IsFinite(value.Origin.Y) || !double.IsFinite(value.Origin.Z))
            throw new ArgumentException("Invalid generated content.");
        if (value.Terrain.Any(cell => cell.Subject is null))
            throw new ArgumentException("Terrain cells must have a pick subject.", nameof(value));
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var pose = new ScenePose(Vector3.Zero, Quaternion.Identity);
        var grid = new VoxelGrid(value.Terrain);
        if (!value.Terrain.IsEmpty)
        {
            var objectId = id("terrain");
            objects.Add(new(objectId, null, pose, null, DesignGeometry.Meshes(grid, false), [], []));
            picking.Add(new(objectId, pose, grid));
            foreach (var cell in value.Terrain.Where(cell => cell.MissingReference is not null))
            {
                objects.Add(new(id("terrain-missing:" + cell.At), objectId,
                    new(new(cell.At.X, cell.At.Y, cell.At.Z), Quaternion.Identity), null,
                    [new SceneVisual.Missing(cell.MissingReference!, true)], [], []));
                problems.Add(new("content_missing", SceneStage.Build, null, cell.MissingReference, null, null, "Missing terrain voxel."));
            }
        }
        for (var i = 0; i < value.BuiltInMeshes.Length; i++)
            objects.Add(new(id("built-in:" + i), null, pose, null,
                [new SceneVisual.Geometry(value.BuiltInMeshes[i], new(false, 0, 0, false))], [], []));
        for (var i = 0; i < value.MissingMarkers.Length; i++)
        {
            var marker = value.MissingMarkers[i];
            objects.Add(new(id("marker:" + i), null, marker.Pose, marker.Problem.Subject,
                [new SceneVisual.Missing(marker.Reference, false)], [], []));
            problems.Add(marker.Problem);
        }
        return new(objects.ToImmutable(), picking.ToImmutable(), problems.ToImmutable());
    }

    private bool Reconcile(ImmutableArray<SceneObject>.Builder objects,
        ImmutableArray<PickGrid>.Builder picking, ImmutableArray<SceneProblem>.Builder problems)
    {
        var present = new HashSet<InstanceId>();
        var rebuilding = false;
        foreach (var item in content!.Objects)
        {
            if (snapshot.Core.FindInstance(item.Instance) is not Lookup<InstanceView>.Found found) continue;
            present.Add(item.Instance);
            var current = found.Value;
            if (rebuilds.TryGetValue(item.Instance, out var pending) && pending.IsCompleted)
            {
                var built = pending.GetAwaiter().GetResult();
                rebuilds.Remove(item.Instance);
                if (Matches(built, current)) Accept(item.Instance, built);
            }
            if (!designs.TryGetValue(item.Instance, out var cached) ||
                !Matches(cached, current))
            {
                rebuilding = true;
                if (!rebuilds.ContainsKey(item.Instance))
                {
                    var contentView = snapshot.Content;
                    var cancellation = new CancellationTokenSource();
                    var work = Task.Run(() => BuildObject(contentView, current, item.Pose), cancellation.Token);
                    rebuilds.Add(item.Instance, work);
                    tasks.Add((work, cancellation));
                }
            }
            if (cached is not null)
            {
                objects.AddRange(cached.Geometry.Geometry.Objects);
                picking.AddRange(cached.Geometry.Geometry.Picking);
                problems.AddRange(cached.Geometry.Geometry.Diagnostics);
            }
        }
        foreach (var id in designs.Keys.Concat(rebuilds.Keys).Where(id => !present.Contains(id)).Distinct().ToArray())
        {
            designs.Remove(id);
            rebuilds.Remove(id);
        }
        return rebuilding;
    }

    public SceneResult<PickSubject> Resolve(SceneHit hit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(hit);
        if (fault is not null) return new SceneResult<PickSubject>.Faulted(fault);
        if (hit.Frame.Session != Id || hit.Frame.Request != request) return Failure<PickSubject>("stale_request", SceneStage.Pick);
        if (hit.Frame.Space != space || hit.Frame.Revision > revision || target is null || !Exists(target))
            return Failure<PickSubject>("stale_hit", SceneStage.Pick);
        if (hit.Object is { } objectId && !ids.Values.Contains(objectId)) return Failure<PickSubject>("stale_hit", SceneStage.Pick);
        if (hit.Subject is PickSubject.Terrain terrain && Exists(terrain.Location))
            return new SceneResult<PickSubject>.Success(hit.Subject);
        if (hit.Subject is PickSubject.WorldObject { Object: WorldObjectId.Instance instance } subject &&
            snapshot.Core.FindInstance(instance.Id) is Lookup<InstanceView>.Found found && found.Value.Design == subject.Design &&
            (subject.Part is null || !found.Value.Parts.Any(part => part.Part == subject.Part && part.Destroyed)) &&
            snapshot.Content.FindDesign(found.Value.Design) is ContentLookup<DesignEvaluation>.Found design &&
            design.Value is not DesignEvaluation.Failed &&
            (subject.Part is null || DesignGeometry.Projection(design.Value).Elements.Any(element => element.Number == subject.Part)))
            return new SceneResult<PickSubject>.Success(hit.Subject);
        return Failure<PickSubject>("stale_hit", SceneStage.Pick);
    }

    private static SceneResult<T> Failure<T>(string code, SceneStage stage) where T : notnull =>
        new SceneResult<T>.Failure([new(code, stage, null, null, null, null, code)]);
    private static int Compare(SimulationPosition first, SimulationPosition second) => first.CompletedSteps != second.CompletedSteps
        ? first.CompletedSteps.CompareTo(second.CompletedSteps) : first.Sequence.CompareTo(second.Sequence);

    public ValueTask DisposeAsync()
    {
        if (closing is not null) return new(closing);
        disposed = true;
        CancelCandidate();
        if (railway is not null) { closingScenes.Add(railway.DisposeAsync().AsTask()); railway = null; }
        if (node is not null) { closingScenes.Add(node.DisposeAsync().AsTask()); node = null; }
        foreach (var work in tasks) work.Cancellation.Cancel();
        closing = Close();
        return new(closing);
    }
    private async Task Close()
    {
        try
        {
            await Task.WhenAll(tasks.Select(work => work.Task).Concat(closingScenes).Append(vehicles.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closed sessions discard canceled and failed background results.
        }
        finally
        {
            foreach (var work in tasks) work.Cancellation.Dispose();
            tasks.Clear();
            designs.Clear();
            rebuilds.Clear();
            ids.Clear();
            content = null;
            scenery = null;
            pending = null;
        }
    }
}

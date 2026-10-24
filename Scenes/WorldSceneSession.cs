using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class WorldSceneSession : IWorldSceneSession
{
    private readonly RailwayCache railwayCache;
    private readonly NodeScene.Cache nodeCache;
    private readonly SceneVehicles vehicles;
    private readonly DisplayRate displayRate;
    private readonly Dictionary<NodeId, (SceneVehicles.Cache Buildings, SceneVehicles.Cache Parked)> settlementCaches = [];
    private RailwayScene? railway;
    private NodeScene? node;
    private NodeScene? successor;
    private sealed record Candidate(WorldLocation Location, Dictionary<string, SceneObjectId> Ids, SceneSpaceId Space,
        NodeScene? Node = null, RailwayScene? Railway = null)
    {
        internal SceneFrameStamp? Prepared;
        internal SceneUpdate? Offered;
    }
    private Candidate? candidate;
    private WorldLocation? rejectedCandidate;
    private SceneProblem? candidateFailure;
    private Task? retiringCandidate;
    // Nodes retain the handoff origin for their whole scene; railways use it only for the first frame.
    private ScenePoint? sceneOrigin;
    private readonly Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built>? nodeBuild;
    private readonly List<Task> closingScenes = [];
    private SceneWorldSnapshot? interpolationStart;
    private Dictionary<string, SceneObjectId> ids = [];
    private readonly ConditionalWeakTable<Dictionary<string, SceneObjectId>, ObjectIds> objectIds = new();
    private sealed class ObjectIds
    {
        internal readonly Dictionary<SceneObjectId, string> Keys = [];
        internal readonly List<SceneObjectId> New = [];
        internal SceneFrame? Frame;
        internal ImmutableArray<TerrainCollisionPatch> Terrain;
        internal HashSet<SceneObjectId> TerrainIds = [];
    }
    private SceneWorldSnapshot snapshot;
    private WorldSceneTarget? target;
    private EventEnvelope? lastEvent;
    private readonly PresentationState presentation = new();
    private SceneProblem? fault;
    private SceneRequestId request;
    private SceneSpaceId space;
    private ulong nextSpace;
    private ulong revision;
    private ulong nextObject;
    private Task? closing;
    private bool disposed;
    public SceneSessionId Id { get; } = new(Guid.NewGuid());
    public SceneBinding Binding { get; }
    internal Task? PendingBuild => node?.BuildWork;
    internal Task[] PendingObjectBuilds => vehicles.PendingWork;
    internal Task[] PendingRailwayBuilds => railway?.PendingWork ?? [];
    internal Task[] PendingNodeBuilds => (node?.PendingWork ?? []).Concat(successor?.PendingWork ?? []).ToArray();
    internal Task[] PendingPrebuilds => (candidate?.Node?.PendingWork ?? []).Concat(candidate?.Railway?.PendingWork ?? [])
        .Concat(retiringCandidate is { IsCompleted: false } ? [retiringCandidate] : []).ToArray();
    internal WorldLocation? PrebuiltLocation => candidate?.Location;
    internal Func<long> Timestamp { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp;

    internal WorldSceneSession(SceneBinding binding, SceneWorldSnapshot initial, RailwayCache railwayCache,
        NodeScene.Cache nodeCache,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built>? nodeBuild = null, SceneVehicles.Cache? vehicleCache = null)
    {
        ValidateSnapshot(initial);
        Binding = binding;
        snapshot = initial;
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

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        if (fault is not null) return new SceneResult<SceneRequestId>.Faulted(fault);
        if (!Exists(value)) return Failure<SceneRequestId>("target_missing", SceneStage.Input);
        CancelCandidate(); rejectedCandidate = null; candidateFailure = null; sceneOrigin = null;
        if (successor is not null) { closingScenes.Add(successor.DisposeAsync().AsTask()); successor = null; }
        if (railway is not null) { closingScenes.Add(railway.DisposeAsync().AsTask()); railway = null; }
        if (node is not null) { closingScenes.Add(node.DisposeAsync().AsTask()); node = null; }
        target = value;
        request = new(request.Value + 1);
        space = new(++nextSpace);
        ids = [];
        presentation.Clear();
        switch (CurrentLocation(snapshot, value))
        {
            case WorldLocation.Edge edge:
                railway = new(snapshot, edge.Id, railwayCache, ObjectId, vehicles: vehicles, displayRate: displayRate, findId: key => Find(ids, key), presentation: presentation);
                break;
            case WorldLocation.Node location:
                node = OpenNode(location.Id, ObjectId, key => Find(ids, key));
                break;
            default:
                throw new ArgumentException("Invalid location target.");
        }
        PruneCaches();
        return new SceneResult<SceneRequestId>.Success(request);
    }

    internal static WorldLocation CurrentLocation(SceneWorldSnapshot snapshot, WorldSceneTarget value) => value switch
    {
        WorldSceneTarget.Location location => location.Value,
        WorldSceneTarget.Follow follow => ((Lookup<TrainView>.Found)snapshot.Core.FindTrain(follow.Train)).Value.Position switch
        {
            TrainPosition.Node node => new WorldLocation.Node(node.Id),
            TrainPosition.Edge edge => new WorldLocation.Edge(edge.Id, edge.DistanceMeters),
            _ => throw new ArgumentException("Invalid train position.")
        },
        WorldSceneTarget.Combat combat => ((Lookup<CombatView>.Found)snapshot.Core.FindCombat(combat.Id)).Value.Location,
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
        var before = snapshot;
        // Revision includes every completed command and step; Sequence alone misses a command followed by Advance.
        if ((node is not null || successor is not null || candidate?.Node is not null) &&
            (input.Core.Revision.Value < before.Core.Revision.Value ||
             input.Core.Revision.Value - before.Core.Revision.Value != input.Core.Position.CompletedSteps - before.Core.Position.CompletedSteps ||
             events.Any(item => item.Event is CoreEvent.ConstructionCompleted or CoreEvent.InstanceDesignChanged or
                 CoreEvent.CombatDestruction or CoreEvent.SettlementFounded)))
        {
            node?.InvalidatePlatforms();
            successor?.InvalidatePlatforms();
            candidate?.Node?.InvalidatePlatforms();
        }
        if (input.Core.Position.CompletedSteps != before.Core.Position.CompletedSteps)
            interpolationStart = input.Core.Position.CompletedSteps == before.Core.Position.CompletedSteps + 1 ? before : null;
        snapshot = input;
        lastEvent = previous;
        presentation.Note(before, input, events, OpenLocation());
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
            vehicles.Retain(snapshot.Core.World.Trains.Select(item => snapshot.Core.FindTrain(item.Id))
                .OfType<Lookup<TrainView>.Found>().SelectMany(item => item.Value.Vehicles).Select(id => (InstanceId)new InstanceId.Vehicle(id)));
            if (target is WorldSceneTarget.Follow or WorldSceneTarget.Combat)
            {
                var location = CurrentLocation(snapshot, target);
                if (railway is not null && (location is not WorldLocation.Edge edge || edge.Id != railway.Edge) ||
                    node is not null && (location is not WorldLocation.Node atNode || atNode.Id != node.Node))
                    Switch(location, displayedFrame as SceneFrame);
            }
            closingScenes.RemoveAll(task => task.IsCompleted);
            presentation.Acknowledge(displayedFrame as SceneFrame, request, space);
            candidate?.Node?.SetBuildAllowed(false);
            var retire = AdvanceSuccessor();
            var stamp = new SceneFrameStamp(Id, request, space, ++revision);
            var frame = node is not null
                ? node.Present(snapshot, frameRequest, stamp, displayedFrame as SceneFrame, sceneOrigin ?? default, target, interpolationStart)
                : railway!.Present(snapshot, interpolationStart, target, frameRequest, stamp, displayedFrame as SceneFrame, sceneOrigin);
            if (retire is not null) closingScenes.Add(retire.DisposeAsync().AsTask());
            if (railway is not null) sceneOrigin = null;
            RetainObjectIds(frame);
            Prebuild();
            PruneCaches();
            return new SceneResult<ISceneFrame>.Success(frame);
        }
        catch (Exception error)
        {
            fault = new("scene_internal_fault", SceneStage.Build, null, null, null, null, error.ToString());
            return new SceneResult<ISceneFrame>.Faulted(fault);
        }
    }

    private SceneObjectId ObjectId(string key)
    {
        return AssignId(ids, key);
    }

    public SceneResult<SceneUpdate?> PresentCandidate(SceneFrameStamp? prepared)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (prepared is not null && prepared.Session != Id)
            throw new ArgumentException("The prepared candidate belongs to another session.", nameof(prepared));
        if (fault is not null) return new SceneResult<SceneUpdate?>.Faulted(fault);
        if (candidateFailure is { } problem)
        {
            candidateFailure = null;
            return new SceneResult<SceneUpdate?>.Failure([problem]);
        }
        if (candidate is not { Node: { } building } current) return new SceneResult<SceneUpdate?>.Success(null);
        try
        {
            if (prepared is not null && prepared == current.Offered?.Frame)
            {
                current.Prepared = prepared;
                current.Offered = null;
            }
            else if (prepared != current.Prepared)
            {
                current.Prepared = null;
                current.Offered = null;
            }
            if (prepared is null && current.Offered?.Base is not null)
            {
                current.Prepared = null;
                current.Offered = null;
            }
            if (current.Offered is { } offered) return new SceneResult<SceneUpdate?>.Success(offered);
            var (upserts, removed) = building.CandidateChanges(snapshot, full: current.Prepared is null);
            var stamp = current.Prepared is { } unchanged && upserts.IsEmpty && removed.IsEmpty
                ? unchanged : new SceneFrameStamp(Id, request, current.Space, ++revision);
            current.Offered = new(current.Prepared, stamp, default, upserts, removed, new([], []), []);
            return new SceneResult<SceneUpdate?>.Success(current.Offered);
        }
        catch (Exception error)
        {
            rejectedCandidate = current.Location;
            CancelCandidate();
            return new SceneResult<SceneUpdate?>.Failure([new("build_failed", SceneStage.Build, null, null, null, null, error.ToString())]);
        }
    }
    private SceneObjectId AssignId(Dictionary<string, SceneObjectId> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            map.Add(key, value = new(++nextObject));
            var assigned = objectIds.GetValue(map, _ => new());
            assigned.Keys.Add(value, key);
            assigned.New.Add(value);
        }
        return value;
    }

    private void RetainObjectIds(SceneFrame frame)
    {
        var assigned = objectIds.GetValue(ids, _ => new());
        var terrain = frame.Update.Environment.TerrainCollision;
        HashSet<SceneObjectId>? previousTerrain = null;
        if (assigned.Terrain != terrain)
        {
            previousTerrain = assigned.TerrainIds;
            assigned.TerrainIds = terrain.Select(patch => patch.Id).ToHashSet();
            assigned.Terrain = terrain;
        }
        foreach (var id in assigned.New) RemoveIfAbsent(id);
        assigned.New.Clear();
        // The displayed base can predate objects that were produced and then removed.
        if (assigned.Frame is { } previous)
            foreach (var id in frame.RemovedSince(previous)) RemoveIfAbsent(id);
        if (previousTerrain is not null)
            foreach (var id in previousTerrain) RemoveIfAbsent(id);
        assigned.Frame = frame;

        void RemoveIfAbsent(SceneObjectId id)
        {
            if (!frame.ContainsObject(id) && !assigned.TerrainIds.Contains(id) && assigned.Keys.Remove(id, out var key))
                ids.Remove(key);
        }
    }

    private WorldLocation? OpenLocation() => railway is not null ? new WorldLocation.Edge(railway.Edge, 0) :
        node is not null ? new WorldLocation.Node(node.Node) : null;

    private static bool SameLocation(WorldLocation? left, WorldLocation right) => (left, right) switch
    {
        (WorldLocation.Node a, WorldLocation.Node b) => a.Id == b.Id,
        (WorldLocation.Edge a, WorldLocation.Edge b) => a.Id == b.Id,
        _ => false
    };
    private NodeScene? AdvanceSuccessor()
    {
        if (node is null) return null;
        if (successor is null && node.FoundedSinceOpen(snapshot))
        {
            if (candidate?.Node?.Node == node.Node) CancelCandidate();
            node.AcceptTrackChanges = false;
            successor = OpenNode(node.Node, ObjectId, key => Find(ids, key), background: true);
        }
        if (successor is null) return null;
        if (!successor.Failed)
        {
            TrainView? followed = target is WorldSceneTarget.Follow follow &&
                snapshot.Core.FindTrain(follow.Train) is Lookup<TrainView>.Found found && found.Value.Position is TrainPosition.Node
                ? found.Value : null;
            successor.Pump(snapshot, node.StreamingFocus, allowBuild: node.HasPendingBuilds != true, following: node.StreamingFollow,
                followedTrain: followed);
            if (!successor.Failed && successor.NearPrepared)
            {
                successor.MarkNearReady();
                var previous = node;
                node = successor;
                successor = null;
                return previous;
            }
        }
        if (successor.Failure is { } problem) node.Report(problem);
        return null;
    }

    private void Switch(WorldLocation location, SceneFrame? displayed)
    {
        if (successor is not null) { closingScenes.Add(successor.DisposeAsync().AsTask()); successor = null; }
        if (railway is not null) { closingScenes.Add(railway.DisposeAsync().AsTask()); railway = null; }
        if (node is not null) { closingScenes.Add(node.DisposeAsync().AsTask()); node = null; }
        presentation.Clear();
        if (candidate is { } next && SameLocation(next.Location, location) && next.Node is not { Layout: null })
        {
            railway = next.Railway; node = next.Node; ids = next.Ids; space = next.Space;
            candidate = null;
        }
        else
        {
            space = new(++nextSpace);
            CancelCandidate(); ids = [];
            var map = ids;
            if (location is WorldLocation.Edge edge) railway = new(snapshot, edge.Id, railwayCache, key => AssignId(map, key), vehicles: vehicles, displayRate: displayRate, findId: key => Find(map, key), presentation: presentation);
            else if (location is WorldLocation.Node atNode) node = OpenNode(atNode.Id, key => AssignId(map, key), key => Find(map, key));
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
    private NodeScene OpenNode(NodeId id, Func<string, SceneObjectId> objectId, Func<string, SceneObjectId?> findId, bool background = false)
    {
        if (!settlementCaches.TryGetValue(id, out var caches))
            settlementCaches.Add(id, caches = (new(), new()));
        return new(snapshot, id, nodeCache, objectId, vehicles, displayRate, findId, nodeBuild, caches.Buildings, caches.Parked, background: background, presentation: presentation);
    }

    private static SceneObjectId? Find(Dictionary<string, SceneObjectId> map, string key) =>
        map.TryGetValue(key, out var id) ? id : null;
    private void Prebuild()
    {
        if (target is not WorldSceneTarget.Follow follow) return;
        var train = ((Lookup<TrainView>.Found)snapshot.Core.FindTrain(follow.Train)).Value;
        WorldLocation? wanted = null;
        var startCandidate = true;
        if (railway is not null && train.Position is TrainPosition.Edge at && snapshot.Core.FindEdge(at.Id) is Lookup<EdgeView>.Found current)
        {
            wanted = new WorldLocation.Node(at.Direction == TravelDirection.Forward ? current.Value.Map.End : current.Value.Map.Start);
            startCandidate = railway.Ready;
        }
        if (node is not null && (train.Position as TrainPosition.Node)?.NextEdge is { } next && snapshot.Core.FindEdge(next) is Lookup<EdgeView>.Found edge)
            wanted = new WorldLocation.Edge(next, edge.Value.Map.Start == node.Node ? 0 : edge.Value.Map.LengthMeters);
        if (wanted is null || !SameLocation(candidate?.Location, wanted)) CancelCandidate();
        if (wanted is null) { rejectedCandidate = null; return; }
        if (SameLocation(rejectedCandidate, wanted)) return;
        rejectedCandidate = null;
        var allowBuild = wanted is WorldLocation.Node
            ? railway?.Ready == true || candidate?.Node is not null
            : node?.HasPendingBuilds != true && railway?.HasPendingBuilds != true;
        // Keep at most one background candidate alive, including cancellation that is still draining.
        if (allowBuild && startCandidate && candidate is null && retiringCandidate?.IsCompleted != false)
        {
            var map = new Dictionary<string, SceneObjectId>();
            candidate = wanted is WorldLocation.Node atNode
                ? new(wanted, map, new(++nextSpace), Node: OpenNode(atNode.Id, key => AssignId(map, key), key => Find(map, key), background: true))
                : new(wanted, map, new(++nextSpace), Railway: new(snapshot, ((WorldLocation.Edge)wanted).Id, railwayCache, key => AssignId(map, key), vehicles: vehicles, displayRate: displayRate, findId: key => Find(map, key), presentation: presentation));
        }
        try
        {
            if (candidate?.Node is { } candidateNode)
            {
                candidateNode.Pump(snapshot, allowBuild: allowBuild, enteringTrain: train);
            }
            if (wanted is WorldLocation.Edge atEdge) candidate?.Railway?.Pump(snapshot, atEdge, allowBuild);
            if (candidate?.Node?.Failed == true || candidate?.Railway?.Failed == true)
            {
                candidateFailure = candidate.Node?.Failure ?? new("build_failed", SceneStage.Build, null, null, null, null, "Candidate construction failed.");
                rejectedCandidate = wanted; CancelCandidate();
            }
        }
        catch (Exception error)
        {
            candidateFailure = new("build_failed", SceneStage.Build, null, null, null, null, error.ToString());
            rejectedCandidate = wanted; CancelCandidate();
        }
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

    internal static ImmutableHashSet<PartId> Destroyed(InstanceView instance) =>
        instance.Parts.Where(part => part.Destroyed).Select(part => part.Part).ToImmutableHashSet();

    internal static bool Matches(BuiltObject built, InstanceView current) =>
        built.Design == current.Design && built.Destroyed.SetEquals(Destroyed(current)) &&
        StructureDamage.Signature.Same(built.Durability, StructureDamage.Signature.From(current));

    internal static BuiltObject BuildObject(IContentView content, InstanceView instance, ScenePose pose)
    {
        var destroyed = Destroyed(instance);
        var geometry = BuildGeometry(id =>
        {
            var subject = new WorldObjectId.Instance(instance.Id);
            var lookup = content.FindDesign(instance.Design);
            if (lookup is ContentLookup<DesignEvaluation>.Found design && design.Value is not DesignEvaluation.Failed)
                return DesignGeometry.Build(content, design.Value, subject, pose, id, destroyed, instance.Design,
                    damage: StructureDamage.Assess(instance, DesignGeometry.Projection(design.Value), content));
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
        return new(instance.Design, destroyed, geometry, StructureDamage.Signature.From(instance));
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
        if (successor is not null) { closingScenes.Add(successor.DisposeAsync().AsTask()); successor = null; }
        if (railway is not null) { closingScenes.Add(railway.DisposeAsync().AsTask()); railway = null; }
        if (node is not null) { closingScenes.Add(node.DisposeAsync().AsTask()); node = null; }
        closing = Close();
        return new(closing);
    }
    private async Task Close()
    {
        try
        {
            await Task.WhenAll(closingScenes.Append(vehicles.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closed sessions discard canceled and failed background results.
        }
        finally
        {
            ids = [];
        }
    }
}

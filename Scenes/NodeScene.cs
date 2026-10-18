using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeScene : IAsyncDisposable
{
    internal sealed record FollowedPath(ScenePoint Ahead, IReadOnlyList<ScenePoint> Priority, IReadOnlyList<ScenePoint> Loading);

    internal sealed record Part(string Inputs, BuiltGeometry Geometry, ImmutableDictionary<GridColumn, double> Heights,
        ScenePoint Origin = default, SceneBounds? Bounds = null)
    {
        internal long ArrayBytes { get; } = Geometry.Geometry.Objects.Sum(item => (long)IntPtr.Size *
            (1 + item.Visuals.Length + item.Lights.Length + item.Effects.Length) + item.Visuals.OfType<SceneVisual.Geometry>()
                .Select(visual => visual.Mesh).Distinct<SceneMesh>(ReferenceEqualityComparer.Instance)
                .Sum(mesh => (long)mesh.Vertices.Length * Unsafe.SizeOf<SceneVertex>() + (long)mesh.TriangleIndices.Length * sizeof(int))) +
            Geometry.Geometry.Picking.Select(pick => pick.Grid).Distinct().Sum(grid => grid.ArrayBytes) +
            (long)Geometry.Geometry.Picking.Length * IntPtr.Size + ProblemBytes(Geometry.Geometry.Diagnostics);
    }
    internal sealed record Built(NodeLayout Layout, ImmutableDictionary<string, Part> Parts,
        ImmutableDictionary<EdgeId, RailwayTerrain.BoundaryData> Boundaries)
    {
        internal ScenePoint InitialFocus { get; } = Center(Layout.PanBounds(default));
        private static ScenePoint Center(ScenePanBounds bounds) => new((bounds.Minimum.X + bounds.Maximum.X) / 2, 0,
            (bounds.Minimum.Y + bounds.Maximum.Y) / 2);
    }
    internal sealed record Cached(string LayoutInputs, ScenePoint InitialFocus, ImmutableDictionary<string, Part> Parts,
        ImmutableDictionary<EdgeId, RailwayTerrain.BoundaryData> Boundaries)
    {
        internal static Cached From(Built built) => new(built.Layout.Inputs, built.InitialFocus, built.Parts, built.Boundaries);
    }
    internal sealed record RailSegment(string Key, Func<double, RailwaySample> Sample, double Begin, double End,
        SceneBounds Bounds, string Inputs)
    {
        private ImmutableArray<SceneBounds> footprint;
        internal ImmutableArray<SceneBounds> Footprint => footprint.IsDefault ? footprint = RailwayMeshes.Footprint(
            Sample, Begin, End, 1, Bounds.Minimum.Y, Bounds.Maximum.Y) : footprint;

        internal Part Build(Part? previous = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (previous?.Inputs == Inputs) return previous;
            var part = MeshPart(RailwayMeshes.Build(Sample, Begin, End, default)) with { Inputs = Inputs };
            token.ThrowIfCancellationRequested();
            return part;
        }
    }
    internal sealed record Preparation(SceneWorldSnapshot Snapshot, Built Global,
        TerrainAppearanceProjection? Terrain, VoxelDefinitionId BallastVoxel,
        Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> Appearances,
        Dictionary<GridColumn, ImmutableArray<SceneVoxelProjection>> ModelChunks,
        Dictionary<GridColumn, Dictionary<GridColumn, RailBed>> BedChunks,
        Dictionary<GridColumn, HashSet<GridColumn>> BoundaryChunks, string TerrainInputs,
        ImmutableDictionary<string, RailSegment> Rails,
        bool HasChunks = true)
    {
        internal string ChunkInputs(GridColumn from)
        {
            var bed = BedChunks.GetValueOrDefault(from) ?? [];
            var boundary = BoundaryChunks.GetValueOrDefault(from) ?? [];
            var model = ModelChunks.GetValueOrDefault(from, []);
            return RailwayDependencies.Values(new object?[] { TerrainInputs, Global.Layout.Template?.SceneModel.Design, from,
                model.Select(voxel => voxel.Voxel).Distinct().Select(id => Appearances[id]).ToArray(),
                bed.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z).ToArray(),
                boundary.OrderBy(item => item.X).ThenBy(item => item.Z).ToArray() });
        }

        internal Part BuildChunk(GridColumn from, Part? previous = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var inputs = ChunkInputs(from);
            return previous?.Inputs == inputs ? previous : TerrainPart(Snapshot,
                Snapshot.Core.World.Nodes.Single(node => node.Id == Global.Layout.Node), Global.Layout, Terrain, BallastVoxel, Appearances,
                BedChunks.GetValueOrDefault(from) ?? [], BoundaryChunks.GetValueOrDefault(from) ?? [],
                ModelChunks.GetValueOrDefault(from, []), from, token) with { Inputs = inputs };
        }
    }

    internal sealed class Cache
    {
        internal const long DefaultIdleBudget = 32 * 1024 * 1024;
        private readonly object gate = new();
        private readonly long idleBudget;
        private ulong next;
        private sealed record Entry(ulong Owner, Cached? Built, bool Active, ulong Left = 0, long IdleBytes = 0);
        private readonly Dictionary<NodeId, Entry> nodes = [];
        internal Cache(long idleBudget = DefaultIdleBudget) => this.idleBudget = idleBudget;
        internal long IdleBytes { get { lock (gate) return nodes.Values.Sum(entry => entry.IdleBytes); } }
        internal ulong Open(NodeId node)
        {
            lock (gate)
            {
                var old = nodes.GetValueOrDefault(node);
                nodes[node] = new(++next, old?.Built, true);
                return next;
            }
        }
        internal Cached? Read(NodeId node) { lock (gate) return nodes.GetValueOrDefault(node)?.Built; }
        internal void Publish(NodeId node, ulong owner, Built built)
        {
            lock (gate) if (nodes.TryGetValue(node, out var old) && old.Owner == owner && old.Active)
                nodes[node] = old with { Built = Cached.From(built) };
        }
        internal void Close(NodeId node, ulong owner)
        {
            lock (gate)
            {
                if (!nodes.TryGetValue(node, out var old) || old.Owner != owner || !old.Active) return;
                if (old.Built is null || old.Built.Parts.IsEmpty && old.Built.Boundaries.IsEmpty)
                { nodes.Remove(node); return; }
                nodes[node] = old with { Active = false, Left = ++next, IdleBytes = Bytes(old.Built) };
                var idle = nodes.Where(pair => !pair.Value.Active).OrderBy(pair => pair.Value.Left).ToArray();
                var bytes = idle.Sum(pair => pair.Value.IdleBytes);
                foreach (var pair in idle)
                {
                    if (bytes <= idleBudget) break;
                    var built = pair.Value.Built!;
                    var size = pair.Value.IdleBytes;
                    bytes -= size;
                    if (pair.Key != idle[^1].Key) { nodes.Remove(pair.Key); continue; }
                    var fixedBytes = FixedBytes(built);
                    if (fixedBytes > idleBudget) { nodes.Remove(pair.Key); continue; }
                    var range = new NodeRange(built.InitialFocus, 0);
                    var parts = ImmutableDictionary.CreateBuilder<string, Part>();
                    var retained = fixedBytes;
                    foreach (var part in built.Parts.OrderBy(part => part.Value.Bounds is null ? 0 : 1)
                        .ThenBy(part => part.Value.Bounds is { } area ? range.DistanceSquared(area) : 0)
                        .ThenBy(part => part.Key, StringComparer.Ordinal))
                    {
                        var partBytes = part.Value.ArrayBytes;
                        if (partBytes > idleBudget - retained) continue;
                        parts.Add(part.Key, part.Value); retained += partBytes;
                    }
                    if (parts.Count == 0 && built.Boundaries.IsEmpty) nodes.Remove(pair.Key);
                    else nodes[pair.Key] = pair.Value with { Built = built with { Parts = parts.ToImmutable() }, IdleBytes = retained };
                    bytes += retained;
                }
            }
        }
        private static long FixedBytes(Cached built) => built.Boundaries.Values.Sum(boundary =>
            boundary.Grid.ArrayBytes + boundary.Dependencies.ArrayBytes + ProblemBytes(boundary.Problems));
        private static long Bytes(Cached built) => FixedBytes(built) + built.Parts.Values.Sum(part => part.ArrayBytes);
    }

    private static long ProblemBytes(ImmutableArray<SceneProblem> problems) => (long)problems.Length * IntPtr.Size +
        problems.Sum(problem => (long)(problem.Element switch
        {
            DesignElementPath.Cell cell => cell.NestedItemIndices.Length,
            DesignElementPath.FunctionalComposite composite => composite.NestedItemIndices.Length,
            _ => 0
        }) * sizeof(int));

    private readonly Cache cache;
    private readonly ulong owner;
    private readonly Func<string, SceneObjectId> objectId;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SceneVehicles vehicles;
    private readonly SettlementObjects settlement;
    private SceneWorldSnapshot snapshot;
    private readonly Dictionary<string, BuiltGeometry> attached = [];
    private readonly Dictionary<EdgeId, RailwayPath> adjacentRailways = [];
    private sealed record PlatformMap(ImmutableArray<NodeLayout.Stop> Stops, ImmutableArray<SceneProblem> Problems);
    private readonly ConditionalWeakTable<SceneWorldSnapshot, PlatformMap> platformMaps = new();
    private readonly Task<Preparation>? pending;
    private sealed record PartWork(Task<Part> Task, CancellationTokenSource Cancellation, SceneBounds Bounds, GridColumn? Chunk);
    private sealed record CompletedPart(Part Part, SceneBounds Bounds, GridColumn? Chunk)
    {
        internal long Vertices { get; } = Part.Geometry.Geometry.Objects.Sum(item =>
            item.Visuals.OfType<SceneVisual.Geometry>().Sum(visual => (long)visual.Mesh.Vertices.Length));
        internal int Visuals { get; } = Part.Geometry.Geometry.Objects.Sum(item => item.Visuals.Length);
    }
    private readonly Dictionary<GridColumn, Part> chunks = [];
    private readonly Dictionary<string, Part> rails = [];
    private readonly HashSet<string> foggedRails = [];
    private bool railFogDirty = true;
    private ImmutableArray<SceneBounds> railFogBounds = [];
    private ImmutableArray<SceneFog> translatedRailFog = [];
    private ScenePoint? railFogOrigin;
    private readonly Dictionary<string, CompletedPart> completedParts = [];
    private readonly object workGate = new();
    private Queue<(string Key, SceneBounds Bounds, GridColumn? Chunk)> waitingParts = [];
    private readonly Dictionary<string, PartWork> pendingParts = [];
    private readonly List<PartWork> retiring = [];
    private readonly Func<Preparation, GridColumn, Part?, CancellationToken, Part> buildChunk;
    private readonly Func<Preparation, RailSegment, Part?, CancellationToken, Part> buildRail;
    private Preparation? prepared;
    private NodeRange range;
    private NodeRange prefetch;
    private FollowedPath? followedPath;
    private readonly DisplayRate displayRate;
    private bool nearCompleted;
    private bool awaitingDisplay;
    private SceneFrameStamp? admissionFrame;
    private NodeLayout? layout;
    private bool buildAllowed = true;
    private Built? built;
    private SceneProblem? failure;
    internal NodeId Node { get; }
    internal NodeLayout? Layout => layout ?? (pending?.IsCompletedSuccessfully == true ? pending.Result.Global.Layout : null);
    private RailwayPath AdjacentRailway(EdgeId edge)
    {
        if (!adjacentRailways.TryGetValue(edge, out var railway))
            adjacentRailways.Add(edge, railway = new RailwayPath(snapshot, edge));
        return railway;
    }
    internal Task? BuildWork => pending;
    internal Task[] PendingWork
    {
        get
        {
            lock (workGate) return (pending is not null && prepared is null && failure is null ? new Task[] { pending } : [])
                .Concat(pendingParts.Values.Select(work => work.Task)).Concat(retiring.Select(work => work.Task))
                .Concat(vehicles.PendingWork).Concat(settlement.PendingWork).ToArray();
        }
    }
    internal bool Failed => failure is not null;
    internal bool HasPendingBuilds
    {
        get
        {
            lock (workGate) return !Failed && prepared is null || waitingParts.Count > 0 ||
                pendingParts.Values.Concat(retiring).Any(work => !work.Task.IsCompleted) ||
                vehicles.HasPendingBuilds || settlement.HasPendingBuilds;
        }
    }
    internal void SetBuildAllowed(bool allowed) { lock (workGate) buildAllowed = allowed; }

    internal NodeScene(SceneWorldSnapshot initial, NodeId node, Cache cache, Func<string, SceneObjectId> objectId, SceneVehicles vehicles, DisplayRate displayRate,
        Func<SceneWorldSnapshot, NodeId, Cached?, CancellationToken, Built>? generate = null,
        SceneVehicles.Cache? buildingCache = null, SceneVehicles.Cache? parkedCache = null,
        Func<Preparation, GridColumn, Part?, CancellationToken, Part>? buildChunk = null,
        Func<Preparation, RailSegment, Part?, CancellationToken, Part>? buildRail = null, bool background = false)
    {
        Node = node; this.cache = cache; this.objectId = objectId; owner = cache.Open(node);
        this.vehicles = vehicles;
        this.displayRate = displayRate;
        this.buildChunk = buildChunk ?? ((data, from, old, token) => data.BuildChunk(from, old, token));
        this.buildRail = buildRail ?? ((data, segment, old, token) => segment.Build(old, token));
        snapshot = initial;
        settlement = new(objectId, buildingCache: buildingCache, parkedCache: parkedCache);
        if (!background)
        {
            try { layout = NodeLayout.Build(initial, node); }
            catch (RailwayBuildException error) { Fail(error); return; }
            InitializeRange(layout);
        }
        var previous = cache.Read(node);
        pending = Task.Run(() =>
        {
            var currentLayout = layout ?? NodeLayout.Build(initial, node, cancellation.Token);
            return generate is null ? Prepare(initial, currentLayout, previous, cancellation.Token) :
                new Preparation(initial, generate(initial, node, previous, cancellation.Token), null, default, [], [], [], [], "", [], false);
        }, cancellation.Token);
    }
    private void InitializeRange(NodeLayout value)
    {
        var bounds = value.PanBounds(default);
        range = new(new((bounds.Minimum.X + bounds.Maximum.X) / 2, 0,
            (bounds.Minimum.Y + bounds.Maximum.Y) / 2), NodeStreaming.PrefetchCells);
        prefetch = range;
    }
    internal void Pump(SceneWorldSnapshot? current = null, ScenePoint? focus = null, bool allowBuild = true, TrainView? enteringTrain = null,
        FollowedPath? following = null)
    {
        SetBuildAllowed(allowBuild);
        if (current is not null) snapshot = current;
        if (cancellation.IsCancellationRequested || failure is not null) return;
        if (enteringTrain is { Position: TrainPosition.Edge entering } && Layout is { } candidateLayout &&
            candidateLayout.TrainEntry(entering.Id) is { } entry)
        {
            focus = candidateLayout.TrainBoundary(entry).Position;
            var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(Node)).Value;
            var crossing = new TrainPosition.Node(Node, entering.Id, null, NodeTravelPhase.Approaching, 0, null);
            var path = new NodeTrainPath(candidateLayout, dimensions, crossing, candidateLayout.Bypass,
                enteringTrain.LengthMeters * RailwayCoordinates.CellsPerMeter, AdjacentRailway);
            following = FollowPath(path, enteringTrain, 0);
        }
        followedPath = following;
        if (focus is { } requestedFocus) range = new(requestedFocus, NodeStreaming.PrefetchCells);
        prefetch = new(following?.Ahead ?? range.Focus, NodeStreaming.PrefetchCells);
        var hadLayout = layout is not null;
        if (hadLayout) settlement.Pump(snapshot, layout!, range, allowBuild, prefetch);
        if (prepared is null && pending?.IsCompleted == true)
        {
            try
            {
                prepared = pending.GetAwaiter().GetResult();
                built = prepared.Global;
                if (layout is null) { layout = built.Layout; InitializeRange(layout); }
                if (focus is { } initialFocus) range = new(initialFocus, NodeStreaming.PrefetchCells);
                foreach (var part in built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal)) Attach(part.Key, part.Value);
                if (cache.Read(Node) is { } previous)
                {
                    foreach (var segment in prepared.Rails.Values.Where(segment => range.Around(NodeStreaming.RetainCells).Intersects(segment.Bounds)))
                        if (previous.Parts.TryGetValue(segment.Key, out var old) && old.Inputs == segment.Inputs)
                            completedParts.Add(segment.Key, new(old, segment.Bounds, null));
                    if (prepared.HasChunks)
                    foreach (var from in range.Around(NodeStreaming.RetainCells).Chunks(layout!))
                        if (previous.Parts.TryGetValue(ChunkKey(from), out var old))
                        {
                            // Validate the value key before retaining geometry from another binding.
                            if (old.Inputs == prepared.ChunkInputs(from))
                                completedParts.Add(ChunkKey(from), new(old, ChunkBounds(from), from));
                        }
                }
            }
            catch (RailwayBuildException error) { Fail(error); return; }
        }
        if (focus is { } point) range = new(point, NodeStreaming.PrefetchCells);
        prefetch = new(following?.Ahead ?? range.Focus, NodeStreaming.PrefetchCells);
        if (prepared is null) return;
        if (!hadLayout) settlement.Pump(snapshot, layout!, range, allowBuild, prefetch);
        lock (workGate) ReconcileParts();
        cache.Publish(Node, owner, built! with
        { Parts = prepared.Global.Parts.SetItems(chunks.Select(item => new KeyValuePair<string, Part>(ChunkKey(item.Key), item.Value))).SetItems(rails) });
    }

    private void Attach(string key, Part part) => attached[key] = WorldSceneSession.Attach(part.Geometry, "node:" + key + ":", objectId);
    internal static string ChunkKey(GridColumn from) => "terrain:" + from.X + ":" + from.Z;

    private void ReconcileParts()
    {
        var retention = range.Around(NodeStreaming.RetainCells);
        foreach (var key in completedParts.Keys.Where(key => !retention.Intersects(completedParts[key].Bounds) &&
            !prefetch.Intersects(completedParts[key].Bounds)).ToArray())
            completedParts.Remove(key);
        foreach (var key in pendingParts.Keys.Where(key => !range.Intersects(pendingParts[key].Bounds) &&
            !prefetch.Intersects(pendingParts[key].Bounds)).ToArray())
        {
            var work = pendingParts[key];
            work.Cancellation.Cancel(); retiring.Add(work); pendingParts.Remove(key);
        }
        foreach (var from in chunks.Keys.Where(from => !retention.Intersects(from) && !prefetch.Intersects(from)).ToArray())
        { chunks.Remove(from); attached.Remove(ChunkKey(from)); }
        foreach (var key in rails.Keys.Where(key => !retention.Intersects(prepared!.Rails[key].Bounds) &&
            !prefetch.Intersects(prepared.Rails[key].Bounds)).ToArray())
        { rails.Remove(key); attached.Remove(key); railFogDirty = true; }
        foreach (var key in pendingParts.Where(pair => pair.Value.Task.IsCompleted).Select(pair => pair.Key).ToArray())
        {
            var work = pendingParts[key];
            pendingParts.Remove(key);
            try
            {
                var part = work.Task.GetAwaiter().GetResult() with { Bounds = work.Bounds };
                completedParts.Add(key, new(part, work.Bounds, work.Chunk));
            }
            catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested) { }
            catch (RailwayBuildException error) { Fail(error); }
            finally { work.Cancellation.Dispose(); }
        }
        foreach (var work in retiring.Where(work => work.Task.IsCompleted).ToArray())
        {
            // Observe retired failures without allowing their results back into the live set.
            try { work.Task.GetAwaiter().GetResult(); } catch (Exception) { }
            work.Cancellation.Dispose(); retiring.Remove(work);
        }
        if (failure is not null) return;
        AdmitCompletedParts();
        var neededChunks = range.Chunks(layout!);
        if (prefetch.Focus != range.Focus) neededChunks = neededChunks.Concat(prefetch.Chunks(layout!)).Distinct();
        var terrain = prepared!.HasChunks ? neededChunks.Where(from => !chunks.ContainsKey(from) &&
                !pendingParts.ContainsKey(ChunkKey(from)) && !completedParts.ContainsKey(ChunkKey(from)))
            .Select(from => (Key: ChunkKey(from), Bounds: ChunkBounds(from), Chunk: (GridColumn?)from)) : [];
        var tracks = prepared.Rails.Values.Where(segment => !rails.ContainsKey(segment.Key) && !pendingParts.ContainsKey(segment.Key) &&
                !completedParts.ContainsKey(segment.Key) && (range.Intersects(segment.Bounds) || prefetch.Intersects(segment.Bounds)))
            .Select(segment => (segment.Key, segment.Bounds, Chunk: (GridColumn?)null));
        waitingParts = new(terrain.Concat(tracks).OrderBy(item => Priority(item.Bounds)).ThenBy(item => item.Key, StringComparer.Ordinal));
        StartParts();
    }

    private (int Ring, double Distance, double Progress) Priority(SceneBounds bounds)
    {
        var (distance, progress) = followedPath?.Priority is { Count: > 0 } path
            ? PathPriority(bounds, path) : (range.DistanceSquared(bounds), 0);
        return (!nearCompleted && !range.Around(NodeStreaming.NearCells).Intersects(bounds) ? 1 : 0,
            distance, progress);
    }

    private static (double Distance, double Progress) PathPriority(SceneBounds bounds, IReadOnlyList<ScenePoint> path)
    {
        if (path.Count == 0) return (double.PositiveInfinity, 0);
        var distance = new NodeRange(path[0], 0).DistanceSquared(bounds);
        if (distance == 0) return (0, 0);
        for (var i = 1; i < path.Count; i++)
        {
            distance = Math.Min(distance, new NodeRange(path[i], 0).DistanceSquared(bounds));
            var from = path[i - 1];
            var dx = path[i].X - from.X;
            var dz = path[i].Z - from.Z;
            double enter = 0, exit = 1;
            for (var axis = 0; axis < 2; axis++)
            {
                var start = axis == 0 ? from.X : from.Z;
                var delta = axis == 0 ? dx : dz;
                var minimum = axis == 0 ? bounds.Minimum.X : bounds.Minimum.Z;
                var maximum = axis == 0 ? bounds.Maximum.X : bounds.Maximum.Z;
                if (delta == 0)
                {
                    if (start < minimum || start > maximum) { enter = 1; exit = 0; break; }
                }
                else
                {
                    var first = (minimum - start) / delta;
                    var last = (maximum - start) / delta;
                    enter = Math.Max(enter, Math.Min(first, last));
                    exit = Math.Min(exit, Math.Max(first, last));
                }
            }
            if (enter <= exit) return (0, i - 1 + enter);
            var lengthSquared = dx * dx + dz * dz;
            for (var corner = 0; corner < 4 && lengthSquared > 0; corner++)
            {
                var x = corner % 2 == 0 ? bounds.Minimum.X : bounds.Maximum.X;
                var z = corner < 2 ? bounds.Minimum.Z : bounds.Maximum.Z;
                var along = Math.Clamp(((x - from.X) * dx + (z - from.Z) * dz) / lengthSquared, 0, 1);
                var offsetX = from.X + along * dx - x;
                var offsetZ = from.Z + along * dz - z;
                distance = Math.Min(distance, offsetX * offsetX + offsetZ * offsetZ);
            }
        }
        return (distance, 0);
    }

    private static SceneBounds ChunkBounds(GridColumn from) => new(new(from.X, -128, from.Z),
        new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize));

    private void AdmitCompletedParts()
    {
        if (nearCompleted && awaitingDisplay) return;
        var near = range.Around(NodeStreaming.NearCells);
        long vertices = 0;
        var visuals = 0;
        var count = 0;
        (int Ring, double Distance, double Progress)? firstPriority = null;
        const double pathMargin = NodeStreaming.PathSampleCells;
        foreach (var (key, ready, priority) in completedParts
            .Select(item => (item.Key, item.Value, Priority: Priority(item.Value.Bounds)))
            .OrderBy(item => item.Priority).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            if (!nearCompleted && !near.Intersects(ready.Bounds) && PathPriority(ready.Bounds, followedPath?.Loading ?? []).Distance >
                pathMargin * pathMargin) continue;
            // One indivisible part may exceed the batch target, but must never block the queue.
            // Later path crossings must not delay this atomic display target.
            if (nearCompleted && count > 0 && (vertices + ready.Vertices > NodeStreaming.AdmissionVertexLimit ||
                visuals + ready.Visuals > NodeStreaming.AdmissionVisualLimit ||
                followedPath?.Priority.Count > 0 && firstPriority is { Distance: 0 } && priority != firstPriority.Value)) break;
            if (ready.Chunk is { } from) chunks.Add(from, ready.Part);
            else { rails.Add(key, ready.Part); railFogDirty = true; }
            Attach(key, ready.Part);
            completedParts.Remove(key);
            vertices += ready.Vertices;
            visuals += ready.Visuals;
            firstPriority ??= priority;
            count++;
        }
        if (nearCompleted && count > 0) awaitingDisplay = true;
    }

    // Caller holds workGate; only Pump replaces the priority order and consumes results.
    private void StartParts()
    {
        if (!buildAllowed || cancellation.IsCancellationRequested || failure is not null || pendingParts.Values.Any(work => work.Task.IsFaulted)) return;
        while (waitingParts.Count > 0 && pendingParts.Values.Count(work => !work.Task.IsCompleted) +
            retiring.Count(work => !work.Task.IsCompleted) < RailwayCoordinates.MaxConcurrentBuilds)
        {
            var item = waitingParts.Dequeue();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            var data = prepared!;
            var task = Task.Run(() => item.Chunk is { } from ? buildChunk(data, from, null, stop.Token) :
                buildRail(data, data.Rails[item.Key], null, stop.Token), stop.Token);
            pendingParts.Add(item.Key, new(task, stop, item.Bounds, item.Chunk));
            _ = task.ContinueWith(_ => { lock (workGate) StartParts(); }, CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
    private void Fail(RailwayBuildException error)
    {
        lock (workGate) failure = new("build_failed", SceneStage.Build, new WorldObjectId.Node(Node), null, null, null, error.Message);
    }

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneFrameRequest request, SceneFrameStamp stamp,
        SceneFrame? displayed, ScenePoint origin, WorldSceneTarget? target = null, SceneWorldSnapshot? previous = null)
    {
        if (admissionFrame is { } offered && displayed?.Update.Frame is { } shown &&
            shown.Session == offered.Session && shown.Request == offered.Request && shown.Space == offered.Space &&
            shown.Revision >= offered.Revision)
        {
            // Every later frame in this scope retains the admitted batch until normal range retirement.
            awaitingDisplay = false;
            admissionFrame = null;
        }
        if (layout is null && pending?.IsCompleted == true) Pump(snapshot, allowBuild: false);
        var (start, seconds) = SceneFrame.SampleTime(snapshot, previous, request.Interpolation);
        displayRate.Update(seconds);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var fog = ImmutableArray.CreateBuilder<SceneFog>();
        var trainFog = ImmutableArray.CreateBuilder<SceneFog>();
        var following = layout is null ? null : RenderTrains(snapshot, start, request.Interpolation, origin, objects,
            picking, problems, trainFog, (target as WorldSceneTarget.Follow)?.Vehicle);
        ScenePoint? focus = null;
        if (target is WorldSceneTarget.Follow follow && objects.FirstOrDefault(item => item.Parent is null &&
            item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(follow.Vehicle))) is { } vehicle)
            focus = RailwayCoordinates.Add(origin, vehicle.Pose.Position);
        else if (target is not WorldSceneTarget.Follow && request.Camera is { } camera &&
            camera.Frame.Update.Frame.Session == stamp.Session && camera.Frame.Update.Frame.Request == stamp.Request &&
            camera.Frame.Update.Frame.Space == stamp.Space)
            focus = RailwayCoordinates.Add(camera.Frame.Origin, camera.Focus);
        Pump(snapshot, focus, following: following);
        var shift = new ScenePose(RailwayCoordinates.Relative(default, origin), Quaternion.Identity);
        foreach (var part in attached.OrderBy(item => item.Key, StringComparer.Ordinal)) Add(SceneGeometry.Place(part.Value.Geometry, shift));
        var terrainFog = ImmutableArray.CreateBuilder<SceneFog>();
        if (layout is not null && prepared?.HasChunks != false) TerrainFog(origin, terrainFog);
        fog.AddRange(terrainFog);
        fog.AddRange(RailFog(origin));
        fog.AddRange(trainFog);
        if (layout is not null)
        {
            problems.AddRange(layout.Problems);
            if (layout.Template is null && snapshot.Core.World.Nodes.Single(item => item.Id == Node).Occupant is NodeOccupant.Settlement owner)
            {
                var lookup = snapshot.Content.FindSettlementLayout(owner.Id);
                if (lookup is ContentLookup<SettlementLayoutProjection>.Found)
                    problems.Add(new("railway_update_pending", SceneStage.Reconcile, new WorldObjectId.Node(Node), null, null, null,
                        "The settlement was founded after this layout opened; railway updates are implemented by Issue #345."));
                else
                {
                    if (lookup is ContentLookup<SettlementLayoutProjection>.Missing missing) DesignGeometry.AddDiagnostics(missing.Diagnostics, problems, new WorldObjectId.Node(Node));
                    problems.Add(new("content_missing", SceneStage.Input, new WorldObjectId.Node(Node), null, null, null, "The settlement layout copy is missing."));
                }
            }
            Add(settlement.Present(snapshot, layout, origin, fog, range, chunks.Keys, prefetch));
        }
        if (failure is not null) problems.Add(failure);
        bool Complete(double radius)
        {
            if (prepared is null) return false;
            var needed = range.Around(radius);
            if (prepared.HasChunks && needed.Chunks(layout!).Any(from => !chunks.ContainsKey(from))) return false;
            if (prepared.Rails.Values.Any(segment => needed.Intersects(segment.Bounds) && !rails.ContainsKey(segment.Key))) return false;
            var relative = new NodeRange(RailwayCoordinates.Add(needed.Focus, -RailwayCoordinates.Relative(origin, default)), radius);
            return !fog.Skip(terrainFog.Count).Any(region => relative.Intersects(region.WorldBounds));
        }
        if (!nearCompleted && Complete(NodeStreaming.NearCells))
        {
            nearCompleted = true;
            awaitingDisplay = true;
        }
        if (nearCompleted && awaitingDisplay) admissionFrame ??= stamp;
        var readiness = failure is not null ? SceneReadiness.Failed : !nearCompleted ? SceneReadiness.Loading :
            Complete(NodeStreaming.ViewCells) ? SceneReadiness.Ready : SceneReadiness.Partial;
        return new(stamp, origin, seconds, readiness,
            objects.ToImmutable(), picking.ToImmutable(), fog.ToImmutable(), RailwayTerrain.UniqueProblems(problems), displayed,
            node: layout, nodeParts: built is null ? [] : built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value)
                .Concat(chunks.OrderBy(item => item.Key.Z).ThenBy(item => item.Key.X).Select(item => item.Value))
                .Concat(rails.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value)).ToImmutableArray(),
            debugLayers: request.DebugLayers, nodeTracks: layout?.EntryTracks, pickRay: request.PickRay);

        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    private ImmutableArray<SceneFog> RailFog(ScenePoint origin)
    {
        if (prepared is null) return [];
        bool NeedsFog(RailSegment segment) => !rails.ContainsKey(segment.Key) &&
            (range.Intersects(segment.Bounds) || NodeStreaming.OverlapsTerrain(segment.Bounds, chunks.Keys));
        var count = 0;
        foreach (var segment in prepared.Rails.Values)
            if (NeedsFog(segment))
            {
                count++;
                if (!foggedRails.Contains(segment.Key)) railFogDirty = true;
            }
        if (count != foggedRails.Count) railFogDirty = true;
        if (railFogDirty)
        {
            foggedRails.Clear();
            foreach (var segment in prepared.Rails.Values.Where(NeedsFog)) foggedRails.Add(segment.Key);
            railFogBounds = ConstructionFog.Clip(foggedRails.Select(key => prepared.Rails[key].Bounds),
                rails.Keys.SelectMany(key => prepared.Rails[key].Footprint));
            railFogDirty = false;
            railFogOrigin = null;
        }
        if (railFogOrigin != origin)
        {
            var shift = RailwayCoordinates.Relative(default, origin);
            translatedRailFog = railFogBounds.Select(bounds => new SceneFog(
                new(bounds.Minimum + shift, bounds.Maximum + shift))).ToImmutableArray();
            railFogOrigin = origin;
        }
        return translatedRailFog;
    }

    private void TerrainFog(ScenePoint origin, ImmutableArray<SceneFog>.Builder fog)
    {
        var minimum = layout!.Bounds.Minimum;
        var maximum = layout.Bounds.Maximum;
        if (prepared is null)
        { Add(minimum.X, maximum.X, minimum.Y, maximum.Y); return; }
        var retained = range.Around(NodeStreaming.RetainCells).Window(layout);
        var ahead = prefetch.Window(layout);
        var left = Math.Min(retained.Left, ahead.Left);
        var right = Math.Max(retained.Right, ahead.Right);
        var back = Math.Min(retained.Back, ahead.Back);
        var front = Math.Max(retained.Front, ahead.Front);
        Add(minimum.X, left, minimum.Y, maximum.Y);
        Add(right, maximum.X, minimum.Y, maximum.Y);
        Add(left, right, minimum.Y, back);
        Add(left, right, front, maximum.Y);
        for (var z = back; z < front; z += VoxelGrid.ChunkSize)
        {
            int? start = null;
            for (var x = left; x < right; x += VoxelGrid.ChunkSize)
            {
                if (!chunks.ContainsKey(new(x, z))) start ??= x;
                else if (start is { } begin) { Add(begin, x, z, z + VoxelGrid.ChunkSize); start = null; }
            }
            if (start is { } last) Add(last, right, z, z + VoxelGrid.ChunkSize);
        }

        void Add(double left, double right, double back, double front)
        {
            if (left >= right || back >= front) return;
            fog.Add(new(new(new((float)(left - origin.X), (float)(-128 - origin.Y), (float)(back - origin.Z)),
                new((float)(right - origin.X), (float)(128 - origin.Y), (float)(front - origin.Z)))));
        }
    }

    internal static Preparation Prepare(SceneWorldSnapshot snapshot, NodeLayout layout, Cached? previous = null,
        CancellationToken token = default)
    {
        var node = layout.Node;
        var map = snapshot.Core.World.Nodes.Single(item => item.Id == node);
        var terrainLookup = snapshot.Content.FindTerrain(map.Terrain.TypeId);
        var terrain = (terrainLookup as ContentLookup<TerrainAppearanceProjection>.Found)?.Value;
        var ballastVoxel = snapshot.Content.FindRailwayAppearance(snapshot.Core.World.RailwayRule) is ContentLookup<RailwayAppearanceProjection>.Found rules
            ? rules.Value.BallastVoxel : throw new RailwayBuildException("The railway appearance is missing.");
        var appearances = new Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?>();
        if (terrain is not null)
            foreach (var id in new[] { terrain.SurfaceVoxel, terrain.SubsurfaceVoxel, terrain.RockVoxel }.Distinct())
                appearances[id] = (snapshot.Content.FindVoxelAppearance(id) as ContentLookup<VoxelAppearanceProjection>.Found)?.Value;
        var model = layout.Template?.SceneModel.Scene.Voxels ?? [];
        foreach (var voxel in model.Select(v => v.Voxel).Distinct())
            appearances[voxel] = (snapshot.Content.FindVoxelAppearance(voxel) as ContentLookup<VoxelAppearanceProjection>.Found)?.Value;
        var modelChunks = model.GroupBy(v => new GridColumn((int)Math.Floor((double)v.At.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor((double)v.At.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize)).ToDictionary(g => g.Key, g => g.ToImmutableArray());
        var parts = ImmutableDictionary.CreateBuilder<string, Part>();
        var rails = ImmutableDictionary.CreateBuilder<string, RailSegment>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        var bed = new Dictionary<GridColumn, RailBed>();
        var boundaryColumns = new HashSet<GridColumn>();
        var boundaries = ImmutableDictionary.CreateBuilder<EdgeId, RailwayTerrain.BoundaryData>();
        var boundaryOwners = new Dictionary<GridColumn, (EdgeId Edge, double Distance)>();
        var terrainInputs = RailwayDependencies.Values(new object?[] { snapshot.Core.World.WorldSeed, map.Position, map.Terrain.TypeId,
            map.Terrain.Relief, TerrainInputs(snapshot.Content, map.Terrain.TypeId), NodeSettlement.Inputs(layout.Template) });
        foreach (var entry in layout.Entries)
        {
            var path = new RailwayPath(snapshot, entry.Edge, token);
            var edge = path.Edge;
            var tracks = edge.Tracks;
            var entryKey = RailwayDependencies.Values(new object[] { layout.Inputs, entry.Edge, tracks });
            var routeNumber = 0;
            foreach (var route in layout.EntryRoutes(entry, tracks))
                AddRoute("entry:" + entry.Edge.Value + ":" + routeNumber++, entryKey, route.Sample, route.Length);
            var boundary = previous?.LayoutInputs == layout.Inputs && previous.Boundaries.TryGetValue(entry.Edge, out var oldBoundary) &&
                oldBoundary.Dependencies.Matches(snapshot, path, node == path.Start.Id ? 0 : path.SegmentCount - 1)
                ? oldBoundary : RailwayTerrain.NodeBoundary(snapshot, path, node, entry, token);
            diagnostics.AddRange(boundary.Problems);
            boundaries.Add(entry.Edge, boundary);
            foreach (var column in boundary.Heights.Keys)
            {
                var global = new GridColumn((int)Math.Round(column.X + boundary.Origin.X), (int)Math.Round(column.Z + boundary.Origin.Z));
                var relative = new Vector3(global.X + .5f - (float)entry.Point.X, 0, global.Z + .5f - (float)entry.Point.Z);
                var lateral = Vector3.Dot(relative, Vector3.Cross(Vector3.UnitY, -entry.Outward));
                var distance = edge.Tracks > 0 ? Enumerable.Range(1, edge.Tracks)
                    .Min(number => Math.Abs(lateral - RailwayCoordinates.TrackOffset(number, edge.Tracks))) : Math.Abs(lateral);
                if (!boundaryOwners.TryGetValue(global, out var owner) || distance < owner.Distance ||
                    distance == owner.Distance && entry.Edge.Value < owner.Edge.Value)
                    boundaryOwners[global] = (entry.Edge, distance);
            }
        }
        foreach (var entry in layout.Entries)
        {
            var boundary = boundaries[entry.Edge];
            GridColumn Global(GridColumn column) => new((int)Math.Round(column.X + boundary.Origin.X), (int)Math.Round(column.Z + boundary.Origin.Z));
            var allowed = boundary.Heights.Keys.Where(column => boundaryOwners[Global(column)].Edge == entry.Edge).ToImmutableHashSet();
            var inputs = RailwayDependencies.Values(new object[] { layout.Inputs, boundary.Dependencies.Key,
                boundary.Heights.Keys.Where(column => !allowed.Contains(column)).OrderBy(column => column.X).ThenBy(column => column.Z).ToArray() });
            Put("boundary:" + entry.Edge.Value, inputs, () => GridPart(
                boundary.Grid.Cells.Where(cell => allowed.Contains(new(cell.At.X, cell.At.Z))).ToImmutableArray(),
                boundary.Heights.Where(item => allowed.Contains(item.Key)).ToImmutableDictionary(), [], boundary.Origin));
            foreach (var column in allowed) boundaryColumns.Add(Global(column));
        }
        var anyTracks = snapshot.Core.World.Edges.Any(edge => (edge.Start == node || edge.End == node) && edge.Tracks > 0);
        var stationKey = RailwayDependencies.Values(new object[] { layout.Inputs, anyTracks });
        var stationNumber = 0;
        foreach (var route in layout.StationRoutes(anyTracks))
            AddRoute("station-tracks:" + stationNumber++, stationKey, route.Sample, route.Length);
        var turnKey = RailwayDependencies.Values(new object[] { layout.Inputs, anyTracks,
            layout.Entries.Select(entry => layout.EntryTracks[entry.Edge]).ToArray() });
        var turnNumber = 0;
        foreach (var route in anyTracks ? layout.TurnRoutes() : [])
            AddRoute("turnaround:" + turnNumber++, turnKey, route.Sample, route.Length);
        var parkingNumber = 0;
        foreach (var line in layout.Parking)
            AddRoute("parking-tracks:" + parkingNumber++, layout.Inputs,
                arc => new(RailwayCoordinates.Add(line.Start, line.Forward * (float)arc), line.Forward,
                    Vector3.Cross(Vector3.UnitY, line.Forward)), line.Length);
        GridColumn Chunk(GridColumn at) => new((int)Math.Floor((double)at.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor((double)at.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        var bedChunks = bed.GroupBy(item => Chunk(item.Key)).ToDictionary(group => group.Key, group => group.ToDictionary());
        var boundaryChunks = boundaryColumns.GroupBy(Chunk).ToDictionary(group => group.Key, group => group.ToHashSet());
        if (bed.Count > 0)
        {
            appearances[ballastVoxel] = (snapshot.Content.FindVoxelAppearance(ballastVoxel) as ContentLookup<VoxelAppearanceProjection>.Found)?.Value;
            terrainInputs = RailwayDependencies.Values(new object?[] { terrainInputs, ballastVoxel, appearances[ballastVoxel] });
        }
        if (terrain is null)
            RailwayTerrain.ReportMissing(new ContentReference.Core(new CoreDefinitionReference.Terrain(map.Terrain.TypeId)),
                terrainLookup is ContentLookup<TerrainAppearanceProjection>.Missing missingTerrain ? missingTerrain.Diagnostics : [],
                "Node terrain appearance is missing.", diagnostics, new WorldObjectId.Node(node));
        foreach (var item in appearances.Where(item => item.Value is null))
        {
            var lookup = snapshot.Content.FindVoxelAppearance(item.Key);
            RailwayTerrain.ReportMissing(new ContentReference.Core(new CoreDefinitionReference.Voxel(item.Key)),
                lookup is ContentLookup<VoxelAppearanceProjection>.Missing missing ? missing.Diagnostics : [],
                "Node voxel appearance is missing.", diagnostics, new WorldObjectId.Node(node));
        }
        var uniqueProblems = RailwayTerrain.UniqueProblems(diagnostics);
        var problemInputs = RailwayDependencies.Values(uniqueProblems);
        Put("diagnostics", problemInputs, () => GridPart([], ImmutableDictionary<GridColumn, double>.Empty, uniqueProblems));
        return new(snapshot, new(layout, parts.ToImmutable(), boundaries.ToImmutable()), terrain, ballastVoxel,
            appearances, modelChunks, bedChunks, boundaryChunks, terrainInputs, rails.ToImmutable());

        void Put(string name, string inputs, Func<Part> create)
        {
            token.ThrowIfCancellationRequested();
            parts.Add(name, previous?.Parts.TryGetValue(name, out var old) == true && old.Inputs == inputs ? old : create() with { Inputs = inputs });
        }
        void AddRoute(string route, string inputs, Func<double, RailwaySample> sample, double length)
        {
            for (var index = 0; index * RailwayCoordinates.SegmentLengthCells < length; index++)
            {
                token.ThrowIfCancellationRequested();
                var begin = index * RailwayCoordinates.SegmentLengthCells;
                var end = Math.Min(length, begin + RailwayCoordinates.SegmentLengthCells);
                var minimum = new Vector3(float.MaxValue);
                var maximum = new Vector3(float.MinValue);
                var steps = (int)Math.Ceiling(end - begin);
                for (var step = 0; step <= steps; step++) Include(begin + (end - begin) * step / steps);
                var key = route + ":" + index;
                // One-cell arc sampling plus a bed-width margin encloses the track profile and its end caps.
                var margin = new Vector3((float)RailwayCoordinates.BedBaseHalfWidth);
                rails.Add(key, new(key, sample, begin, end, new(minimum - margin, maximum + margin),
                    RailwayDependencies.Values(new object[] { inputs, route, begin, end })));

                void Include(double arc)
                {
                    var at = sample(arc);
                    var point = RailwayCoordinates.Relative(at.Position, default);
                    minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
                }
            }
            for (var arc = 0.0; arc <= length; arc = Math.Min(length, arc + 0.5))
            {
                token.ThrowIfCancellationRequested();
                var point = sample(arc);
                var half = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;
                for (var offset = -half; offset <= half; offset += 0.5)
                {
                    var at = RailwayCoordinates.Add(point.Position, point.Right * (float)offset);
                    var column = new GridColumn((int)Math.Floor(at.X), (int)Math.Floor(at.Z));
                    var distance = Math.Abs(offset);
                    if (!bed.TryGetValue(column, out var old) || distance < old.Distance) bed[column] = new(distance, point.Position.Y);
                }
                if (arc == length) break;
            }
        }
    }

    private static object TerrainInputs(IContentView content, TerrainTypeId id)
    {
        var lookup = content.FindTerrain(id);
        if (lookup is not ContentLookup<TerrainAppearanceProjection>.Found found) return lookup;
        var terrain = found.Value;
        return new object[] { id, terrain.SurfaceVoxel, terrain.SubsurfaceVoxel, terrain.RockVoxel, terrain.LocalHeightNoise,
            new[] { terrain.SurfaceVoxel, terrain.SubsurfaceVoxel, terrain.RockVoxel }.Distinct()
                .Select(voxel => content.FindVoxelAppearance(voxel)).ToArray() };
    }

    private static Part MeshPart(ImmutableArray<SceneMesh> meshes) => new("", WorldSceneSession.BuildGeometry(id =>
        new([new(id(""), null, new(Vector3.Zero, Quaternion.Identity), null,
            meshes.Select(mesh => (SceneVisual)new SceneVisual.Geometry(mesh, new(false, 0, 0, false))).ToImmutableArray(), [], [])], [], [])),
        ImmutableDictionary<GridColumn, double>.Empty);

    internal sealed record RailBed(double Distance, double Height);
    private static Part TerrainPart(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        TerrainAppearanceProjection? terrain, VoxelDefinitionId ballastVoxel, Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> appearances,
        Dictionary<GridColumn, RailBed> bed, HashSet<GridColumn> boundaryColumns, ImmutableArray<SceneVoxelProjection> model,
        GridColumn from, CancellationToken token)
    {
        var cellCount = terrain is null ? 0 :
            (VoxelGrid.ChunkSize * VoxelGrid.ChunkSize - boundaryColumns.Count) * RailwayCoordinates.TerrainDepthCells;
        var cells = ImmutableArray.CreateBuilder<SceneCell>(cellCount);
        var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
        var subject = new PickSubject.Terrain(new WorldLocation.Node(node.Id));
        var layers = RailwayTerrain.PrepareLayers(terrain, id => appearances.GetValueOrDefault(id), subject);
        var ballast = bed.Count > 0 ? RailwayTerrain.PrepareVoxel(ballastVoxel, id => appearances.GetValueOrDefault(id), subject) : null;
        for (var z = from.Z; z < from.Z + VoxelGrid.ChunkSize; z++)
        for (var x = from.X; x < from.X + VoxelGrid.ChunkSize; x++)
        {
            token.ThrowIfCancellationRequested();
            var column = new GridColumn(x, z);
            if (boundaryColumns.Contains(column)) continue;
            var (height, distance) = Column(column);
            heights.Add(column, height + 1);
            RailwayTerrain.FillColumn(cells, column, height, layers, ballast, distance, double.IsFinite(distance));
        }
        var modelCells = ImmutableArray.CreateBuilder<SceneCell>();
        foreach (var voxel in model)
        {
            var column = new GridColumn(voxel.At.X, voxel.At.Z);
            if (bed.TryGetValue(column, out var rail) && rail.Distance < RailwayCoordinates.DecorationVoxelClearance &&
                voxel.At.Y >= Math.Floor(rail.Height) - 3 && voxel.At.Y < rail.Height + 42) continue;
            var appearance = appearances.GetValueOrDefault(voxel.Voxel);
            var missing = voxel.Missing || appearance is null ? new ContentReference.Core(new CoreDefinitionReference.Voxel(voxel.Voxel)) : null;
            modelCells.Add(new(voxel.At, new(voxel.Orientation, appearance, voxel.Shape, voxel.Paint, subject, [], missing)));
        }
        var occupied = modelCells.Select(cell => cell.At).ToHashSet();
        return GridPart(cells.Where(cell => !occupied.Contains(cell.At)).Concat(modelCells).ToImmutableArray(), heights.ToImmutable(), []);

        (int Height, double Distance) Column(GridColumn column)
        {
            var raw = RailwayTerrain.NodeGround(snapshot, node, new(column.X + 0.5, 0, column.Z + 0.5), layout.Template);
            var rail = bed.GetValueOrDefault(column);
            var distance = rail?.Distance ?? double.PositiveInfinity;
            if (rail is not null)
            {
                var blend = Math.Clamp((distance - RailwayCoordinates.BedBaseHalfWidth) / RailwayCoordinates.BedBlendWidth, 0, 1);
                raw = RailwayTerrain.BedHeight(rail.Height, distance) * (1 - blend) + raw * blend;
            }
            return ((int)Math.Floor(raw), distance);
        }
    }

    private static Part GridPart(ImmutableArray<SceneCell> cells, ImmutableDictionary<GridColumn, double> heights,
        ImmutableArray<SceneProblem> problems, ScenePoint origin = default) => new("", WorldSceneSession.BuildGeometry(id =>
        {
            var grid = new VoxelGrid(cells); var root = id("");
            var pose = new ScenePose(RailwayCoordinates.Relative(origin, default), Quaternion.Identity);
            var objects = ImmutableArray.CreateBuilder<SceneObject>();
            var visuals = DesignGeometry.Meshes(grid, false).ToBuilder();
            foreach (var reference in problems.Where(problem => problem.Content is ContentReference.Core { Definition: CoreDefinitionReference.Terrain })
                .Select(problem => problem.Content!).Distinct()) visuals.Add(new SceneVisual.Missing(reference, false));
            objects.Add(new(root, null, pose, null, visuals.ToImmutable(), [], []));
            objects.AddRange(DesignGeometry.Models(grid, root, null, cell => id(":model:" + cell.At), false));
            foreach (var cell in cells.Where(cell => cell.MissingReference is not null))
                objects.Add(new(id("missing:" + cell.At), root, new(new(cell.At.X, cell.At.Y, cell.At.Z), Quaternion.Identity), null,
                    [new SceneVisual.Missing(cell.MissingReference!, true)], [], []));
            return new(objects.ToImmutable(),
                cells.IsEmpty ? [] : [new(root, pose, grid)], problems);
        }), heights, origin);

    private TrainView[] NodeTrains(SceneWorldSnapshot snapshot) => snapshot.Core.World.Trains
        .Where(item => item.Position is TrainPosition.Node position && position.Id == Node)
        .Select(item => snapshot.Core.FindTrain(item.Id)).OfType<Lookup<TrainView>.Found>()
        .Select(item => item.Value).OrderBy(train => train.Id.Value).ToArray();

    private FollowedPath? RenderTrains(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, double interpolation,
        ScenePoint origin, ImmutableArray<SceneObject>.Builder objects,
        ImmutableArray<PickGrid>.Builder picking, ImmutableArray<SceneProblem>.Builder problems, ImmutableArray<SceneFog>.Builder fog,
        VehicleId? followedVehicle)
    {
        var trains = NodeTrains(snapshot);
        if (trains.Length == 0) return null;
        FollowedPath? following = null;
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(Node)).Value;
        var queues = QueuePositions(trains, dimensions);
        var mapping = Platforms(snapshot);
        problems.AddRange(mapping.Problems);
        var platforms = mapping.Stops;
        var oldTrains = previous is null ? [] : NodeTrains(previous);
        var oldQueues = QueuePositions(oldTrains, dimensions);
        var oldPlatforms = previous is null ? [] : Platforms(previous, cacheProjections: false).Stops;
        foreach (var train in trains)
        {
            var members = train.Vehicles.Select(id => ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(id))).Value).ToArray();
            var evaluations = members.Select(member => snapshot.Content.FindDesign(member.Design)).ToArray();
            var lengths = evaluations.Select(value => RailwayVehicles.Length((value as ContentLookup<DesignEvaluation>.Found)?.Value)).ToArray();
            var known = lengths.All(length => length is > 0);
            var total = known ? lengths.Sum(length => length!.Value) : 0;
            var station = ((TrainPosition.Node)train.Position).PlatformNumber is { } number ? number - 1 : -1;
            var position = (TrainPosition.Node)train.Position;
            var entry = layout!.TrainEntry(position.EnteredFrom);
            var positive = train.OrderFacing == TrainOrderFacing.FirstEndLeading;
            if (station >= platforms.Length)
            {
                problems.Add(new("station_platform_mismatch", SceneStage.Reconcile, new WorldObjectId.Train(train.Id),
                    null, null, null, "The assigned platform has no matching station slot."));
                continue;
            }
            var path = new NodeTrainPath(layout, dimensions, position, station >= 0 ? platforms[station] : layout.Bypass,
                train.LengthMeters * RailwayCoordinates.CellsPerMeter, AdjacentRailway);
            var queue = queues.GetValueOrDefault(train.Id);
            var distance = position.DistanceMeters;
            if (oldTrains.FirstOrDefault(item => item.Id == train.Id) is { } old &&
                old.OrderFacing == train.OrderFacing && old.Vehicles.SequenceEqual(train.Vehicles) && old.LengthMeters == train.LengthMeters &&
                oldQueues.GetValueOrDefault(train.Id) == queue && old.Position is TrainPosition.Node before &&
                before.PlatformNumber == position.PlatformNumber && station < oldPlatforms.Length)
            {
                var oldPath = new NodeTrainPath(layout, dimensions, before, station >= 0 ? oldPlatforms[station] : layout.Bypass,
                    old.LengthMeters * RailwayCoordinates.CellsPerMeter, AdjacentRailway);
                if (path.SameRoute(oldPath, before.DistanceMeters, distance))
                    distance = before.DistanceMeters + (distance - before.DistanceMeters) * interpolation;
            }
            var center = path.Arc(distance, queue);
            var available = station >= 0 || entry is null || layout.EntryTracks.GetValueOrDefault(entry.Edge) > 0;
            var changed = !available || entry is not null &&
                snapshot.Core.World.Edges.FirstOrDefault(edge => edge.Id == entry.Edge)?.Tracks != layout.EntryTracks.GetValueOrDefault(entry.Edge);
            if (changed) problems.Add(new("railway_update_pending", SceneStage.Reconcile, new WorldObjectId.Train(train.Id),
                null, null, null, "Node track counts use the opening snapshot; updates are implemented by Issue #345."));
            var consumed = 0.0;
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i];
                var arc = center + (positive ? 1 : -1) * (known ? total / 2.0 - consumed - lengths[i]!.Value / 2.0 : 0);
                consumed += lengths[i] ?? 0;
                var pose = RailwayVehicles.Pose(path.Sample(arc), origin, positive);
                if (member.Id is InstanceId.Vehicle vehicleId && vehicleId.Id == followedVehicle)
                    following = FollowPath(path, train, distance, queue);
                if (!known || !available)
                {
                    Add(SceneVehicles.Unplaced(snapshot.Content, member, evaluations[i], lengths[i], pose, objectId));
                    continue;
                }
                if (vehicles.Reconcile(snapshot.Content, member, objectId) is { } body)
                    Add(RailwayVehicles.Place(body, pose, (positive ? 1 : -1) * arc));
                else
                {
                    var loading = SceneVehicles.Loading(member, ((ContentLookup<DesignEvaluation>.Found)evaluations[i]).Value, pose, objectId);
                    objects.Add(loading.Object); fog.Add(loading.Fog);
                }
            }
        }
        return following;
        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    private FollowedPath FollowPath(NodeTrainPath path, TrainView train, double distance, double queue = 0)
    {
        var center = path.Arc(distance, queue);
        var speed = (path.Arc(distance + train.SpeedMetersPerSecond * SimulationTime.StepSeconds, queue) - center)
            / SimulationTime.StepSeconds;
        var ahead = NodeStreaming.AdvanceCells(speed, displayRate.Value);
        var halfLength = train.LengthMeters * RailwayCoordinates.CellsPerMeter / 2;
        var head = center + halfLength;
        return new(path.Sample(Math.Clamp(head + ahead, 0, path.Length)).Position,
            SamplePath(path, center - halfLength, Math.Min(head + ahead, path.Length)),
            SamplePath(path, center - halfLength, Math.Min(head + NodeStreaming.ViewCells + NodeStreaming.PathSampleCells, path.Length)));
    }

    private static ScenePoint[] SamplePath(NodeTrainPath path, double begin, double end)
    {
        var intervals = Math.Max(1, (int)Math.Ceiling((end - begin) / NodeStreaming.PathSampleCells));
        var points = new ScenePoint[intervals + 1];
        for (var i = 0; i <= intervals; i++) points[i] = path.Sample(begin + (end - begin) * i / intervals).Position;
        return points;
    }

    private PlatformMap Platforms(SceneWorldSnapshot input, bool cacheProjections = true) => platformMaps.GetValue(input, value =>
    {
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var stops = settlement.Platforms(value, layout!, problems, cacheProjections);
        return new(stops, problems.ToImmutable());
    });

    private Dictionary<TrainId, double> QueuePositions(TrainView[] trains, NodeView dimensions)
    {
        var result = new Dictionary<TrainId, double>();
        var waiting = dimensions.ApproachLengthMeters;
        var queued = trains.Where(train => train.Position is TrainPosition.Node { PlatformNumber: null } position &&
            (train.PlatformQueueEnteredStep is not null && position.DistanceMeters <= waiting ||
             train.SpeedMetersPerSecond == 0 && position.Phase == NodeTravelPhase.Waiting && position.DistanceMeters == waiting));
        foreach (var group in queued.GroupBy(train =>
            layout!.TrainEntry(((TrainPosition.Node)train.Position).EnteredFrom)?.Edge))
        {
            var ordered = group.OrderBy(train => train.PlatformQueueEnteredStep is not null)
                .ThenBy(train => train.PlatformQueueEnteredStep).ThenBy(train => train.Id.Value).ToArray();
            var consumed = 0.0;
            var headWaiting = Waiting(ordered[0]);
            foreach (var train in ordered)
            {
                var offset = (consumed + (train.LengthMeters - ordered[0].LengthMeters) / 2) * RailwayCoordinates.CellsPerMeter +
                    Waiting(train) - headWaiting;
                result.Add(train.Id, offset);
                consumed += train.LengthMeters;
            }
        }
        return result;

        double Waiting(TrainView train)
        {
            return new NodeTrainPath(layout!, dimensions, (TrainPosition.Node)train.Position, layout!.Bypass,
                train.LengthMeters * RailwayCoordinates.CellsPerMeter, AdjacentRailway).WaitingArc;
        }
    }

    public async ValueTask DisposeAsync()
    {
        PartWork[] parts;
        Task[] ownedWork;
        lock (workGate)
        {
            cancellation.Cancel();
            cache.Close(Node, owner);
            waitingParts.Clear();
            completedParts.Clear();
            parts = pendingParts.Values.Concat(retiring).ToArray();
            foreach (var work in parts) work.Cancellation.Cancel();
            ownedWork = (pending is null ? Array.Empty<Task>() : [pending]).Concat(parts.Select(item => item.Task)).ToArray();
        }
        try { await Task.WhenAll(ownedWork).ConfigureAwait(false); }
        catch (Exception) { /* Retired scene work cannot be attached or published. */ }
        lock (workGate) foreach (var work in parts) work.Cancellation.Dispose();
        await settlement.DisposeAsync();
        cancellation.Dispose();
    }
}

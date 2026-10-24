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
        ImmutableDictionary<EdgeId, RailwayTerrain.BoundaryData> Boundaries);
    internal sealed record Cached(string LayoutInputs, ImmutableDictionary<string, Part> Parts,
        ImmutableDictionary<EdgeId, RailwayTerrain.BoundaryData> Boundaries)
    {
        internal static Cached From(Built built) => new(built.Layout.Inputs, built.Parts, built.Boundaries);
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
        Dictionary<GridColumn, double> BoundarySurfaces,
        bool HasChunks = true)
    {
        internal string ChunkInputs(GridColumn from)
        {
            var bed = BedsNear(from);
            var boundary = BoundaryChunks.GetValueOrDefault(from) ?? [];
            var surfaces = SurfacesNear(from);
            var model = ModelChunks.GetValueOrDefault(from, []);
            return RailwayDependencies.Values(new object?[] { TerrainInputs, Global.Layout.Template?.SceneModel.Design, from,
                model.Select(voxel => voxel.Voxel).Distinct().Select(id => Appearances[id]).ToArray(),
                bed.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z).ToArray(),
                boundary.OrderBy(item => item.X).ThenBy(item => item.Z).ToArray(),
                surfaces.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z).ToArray() });
        }

        internal Part BuildChunk(GridColumn from, Part? previous = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var inputs = ChunkInputs(from);
            return previous?.Inputs == inputs ? previous : TerrainPart(Snapshot,
                Snapshot.Core.World.Nodes.Single(node => node.Id == Global.Layout.Node), Global.Layout, Terrain, BallastVoxel, Appearances,
                BedsNear(from), BoundaryChunks.GetValueOrDefault(from) ?? [],
                ModelChunks.GetValueOrDefault(from, []), from, SurfacesNear(from), token) with { Inputs = inputs };
        }

        private Dictionary<GridColumn, RailBed> BedsNear(GridColumn from)
        {
            var beds = new Dictionary<GridColumn, RailBed>();
            foreach (var key in ChunkNeighborhood(from))
            {
                if (!BedChunks.TryGetValue(key, out var chunk)) continue;
                foreach (var (column, rail) in chunk)
                    if (NearChunk(from, column)) beds[column] = rail;
            }
            return beds;
        }

        private Dictionary<GridColumn, double> SurfacesNear(GridColumn from)
        {
            var surfaces = new Dictionary<GridColumn, double>();
            foreach (var key in ChunkNeighborhood(from))
            {
                if (!BoundaryChunks.TryGetValue(key, out var columns)) continue;
                foreach (var column in columns)
                    if (NearChunk(from, column)) surfaces[column] = BoundarySurfaces[column];
            }
            return surfaces;
        }

        private static IEnumerable<GridColumn> ChunkNeighborhood(GridColumn from)
        {
            var size = VoxelGrid.ChunkSize;
            for (var z = from.Z - size; z <= from.Z + size; z += size)
            for (var x = from.X - size; x <= from.X + size; x += size)
                yield return new(x, z);
        }

        private static bool NearChunk(GridColumn from, GridColumn column)
        {
            var size = VoxelGrid.ChunkSize;
            return column.X >= from.X - 1 && column.X <= from.X + size &&
                column.Z >= from.Z - 1 && column.Z <= from.Z + size;
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
                    var range = new NodeRange(default, 0);
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
    private readonly Func<string, SceneObjectId?> findId;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SceneVehicles vehicles;
    private readonly SettlementObjects settlement;
    private SceneWorldSnapshot snapshot;
    private readonly Dictionary<string, BuiltGeometry> attached = [];
    private readonly HashSet<string> staticChanges = [];
    private readonly Dictionary<string, RenderedDesign> placedParts = [];
    private readonly Dictionary<string, TerrainCollisionPatch> terrainByPart = [];
    private readonly HashSet<string> candidateChanges = [];
    private readonly Dictionary<SceneObjectId, SceneObject> candidateObjects = [];
    private readonly Dictionary<string, ImmutableArray<SceneObjectId>> candidatePartObjects = [];
    private ImmutableArray<SceneObject> candidateInstances = [];
    private readonly Dictionary<EdgeId, RailwayPath> adjacentRailways = [];
    private sealed record PlatformMap(NodeLayout Layout, ImmutableArray<NodeLayout.Stop> Stops, ImmutableArray<SceneProblem> Problems);
    private readonly ConditionalWeakTable<SceneWorldSnapshot, PlatformMap> platformMaps = new();
    private readonly Task<Preparation>? pending;
    private readonly Func<SceneWorldSnapshot, NodeId, Cached?, CancellationToken, Built>? nodeBuild;
    private Task<Preparation>? refresh;
    private readonly List<SceneProblem> reported = [];
    internal bool AcceptTrackChanges { get; set; } = true;
    private sealed record PartWork(Task<Part> Task, CancellationTokenSource Cancellation, SceneBounds Bounds, GridColumn? Chunk);
    private sealed record CompletedPart(Part Part, SceneBounds Bounds, GridColumn? Chunk)
    {
        internal long Vertices { get; } = Part.Geometry.Geometry.Objects.Sum(item =>
            item.Visuals.OfType<SceneVisual.Geometry>().Sum(visual => (long)visual.Mesh.Vertices.Length));
        internal int Visuals { get; } = Part.Geometry.Geometry.Objects.Sum(item => item.Visuals.Length);
    }
    private readonly Dictionary<GridColumn, Part> chunks = [];
    private readonly HashSet<GridColumn> peripheralChunks = [];
    private readonly HashSet<string> peripheralRails = [];
    private readonly HashSet<GridColumn> unbuiltCorridorChunks = [];
    private readonly HashSet<string> corridorRails = [];
    private NodeCorridor? indexedCorridor;
    private Preparation? indexedPreparation;
    private readonly HashSet<GridColumn> terrainChunks = [];
    private readonly SortedSet<GridColumn> corridorTerrainX = new(Comparer<GridColumn>.Create((a, b) =>
        a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z)));
    private readonly SortedSet<GridColumn> corridorTerrainZ = new(Comparer<GridColumn>.Create((a, b) =>
        a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X)));
    private readonly Dictionary<int, ImmutableArray<SceneBounds>> terrainFogRows = [];
    private readonly HashSet<int> dirtyTerrainRows = [];
    private (int Left, int Right, int Back, int Front)? terrainFogWindow;
    private ScenePoint? terrainFogOrigin;
    private ImmutableArray<SceneFog> cachedTerrainFog = [];
    private readonly Dictionary<string, Part> rails = [];
    private readonly Dictionary<GridColumn, HashSet<string>> builtRailsByChunk = [];
    private readonly HashSet<string> rebuilding = [];
    private readonly HashSet<string> foggedRails = [];
    private bool railFogDirty = true;
    private NodeRange? railFogRange;
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
    private NodeCorridor? corridor;
    private bool preparingCandidate;
    private (NodeLayout Layout, EdgeId? Entry, int? Track, EdgeId? Exit, int? ExitTrack, int? Platform, bool Departing, double Length)? corridorInput;
    private ImmutableArray<NodeLayout.Stop> corridorPlatforms = [];
    private readonly HashSet<string> retainedCorridorParts = [];
    private (Preparation Prepared, NodeRange Range, NodeRange Prefetch, NodeCorridor? Corridor, bool Near)? streamingPlan;
    private IReadOnlyList<ScenePoint> plannedPath = [];
    internal ulong StreamingPlanRevision { get; private set; }

    private readonly DisplayRate displayRate;
    private bool nearCompleted;
    private bool awaitingDisplay;
    private SceneFrameStamp? admissionFrame;
    private NodeLayout? layout;
    private bool buildAllowed = true;
    private Built? built;
    private ImmutableDictionary<string, Part> cacheParts = ImmutableDictionary<string, Part>.Empty;
    private ImmutableDictionary<string, Part>? publishedParts;
    private Built? publishedBuilt;
    private ImmutableDictionary<string, Part>? presentedParts;
    private ScenePoint? staticOrigin;
    private RenderedDesign staticGeometry = new([], [], []);
    private ImmutableArray<Part> staticParts = [];
    private ImmutableArray<TerrainCollisionPatch> staticTerrain = [];
    private SceneProblem? failure;
    internal NodeId Node { get; }
    private readonly PresentationState? presentation;
    private readonly TerrainCollision.PatchCache terrainPatches = new();
    internal ScenePoint StreamingFocus => range.Focus;
    internal FollowedPath? StreamingFollow => followedPath;
    internal NodeLayout? Layout => layout ?? (pending?.IsCompletedSuccessfully == true ? pending.Result.Global.Layout : null);
    internal bool FoundedSinceOpen(SceneWorldSnapshot snapshot)
    {
        if (Layout is not { Template: null }) return false;
        var map = snapshot.Core.World.Nodes.FirstOrDefault(item => item.Id == Node);
        if (map?.Occupant is not NodeOccupant.Settlement owner) return false;
        return snapshot.Content.FindSettlementLayout(owner.Id) is ContentLookup<SettlementLayoutProjection>.Found;
    }
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
                .Concat(refresh is not null && failure is null ? [refresh] : [])
                .Concat(pendingParts.Values.Select(work => work.Task)).Concat(retiring.Select(work => work.Task))
                .Concat(vehicles.PendingWork).Concat(settlement.PendingWork).ToArray();
        }
    }
    internal bool Failed => failure is not null;
    internal bool HasPendingBuilds
    {
        get
        {
            lock (workGate) return !Failed && prepared is null || refresh is not null || waitingParts.Count > 0 ||
                pendingParts.Values.Concat(retiring).Any(work => !work.Task.IsCompleted) ||
                vehicles.HasPendingBuilds || settlement.HasPendingBuilds;
        }
    }
    internal void SetBuildAllowed(bool allowed) { lock (workGate) buildAllowed = allowed; }

    internal NodeScene(SceneWorldSnapshot initial, NodeId node, Cache cache, Func<string, SceneObjectId> objectId, SceneVehicles vehicles, DisplayRate displayRate,
        Func<string, SceneObjectId?> findId,
        Func<SceneWorldSnapshot, NodeId, Cached?, CancellationToken, Built>? generate = null,
        SceneVehicles.Cache? buildingCache = null, SceneVehicles.Cache? parkedCache = null,
        Func<Preparation, GridColumn, Part?, CancellationToken, Part>? buildChunk = null,
        Func<Preparation, RailSegment, Part?, CancellationToken, Part>? buildRail = null, bool background = false,
        PresentationState? presentation = null)
    {
        Node = node; this.cache = cache; this.objectId = objectId; this.findId = findId; this.presentation = presentation; owner = cache.Open(node); this.nodeBuild = generate;
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
            InitializeRange();
        }
        var previous = cache.Read(node);
        pending = Task.Run(() =>
        {
            var currentLayout = layout ?? NodeLayout.Build(initial, node, cancellation.Token);
            return BuildPreparation(initial, currentLayout, previous, cancellation.Token);
        }, cancellation.Token);
    }
    // 首个相机采样前以节点原点为焦点：站点和巢穴都在原点附近，范围中心可能离它们几百米（#674）。
    private void InitializeRange()
    {
        range = new(default, NodeStreaming.PrefetchCells);
        prefetch = range;
    }
    internal void Pump(SceneWorldSnapshot? current = null, ScenePoint? focus = null, bool allowBuild = true, TrainView? enteringTrain = null,
        FollowedPath? following = null, TrainView? followedTrain = null, WorldSceneTarget? target = null)
    {
        SetBuildAllowed(allowBuild);
        preparingCandidate = enteringTrain is not null;
        if (current is not null) snapshot = current;
        AcceptRefresh();
        if (cancellation.IsCancellationRequested || failure is not null) return;
        if (enteringTrain is { Position: TrainPosition.Edge entering } && Layout is { } candidateLayout &&
            candidateLayout.TrainEntry(entering.Id) is { } entry)
        {
            focus = candidateLayout.TrainBoundary(entry, entering.Track).Position;
            var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(Node)).Value;
            var crossing = new TrainPosition.Node(Node, entering.Id, entering.Track, null, null, NodeTravelPhase.Approaching, 0, null);
            var path = new NodeTrainPath(candidateLayout, dimensions, crossing, candidateLayout.Bypass,
                enteringTrain.LengthMeters * RailwayCoordinates.CellsPerMeter, AdjacentRailway);
            following = FollowPath(path, enteringTrain, 0);
        }
        if (followedTrain is { Position: TrainPosition.Node onNode } && Layout is { } ownLayout)
        {
            var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(Node)).Value;
            var founded = FoundedSinceOpen(snapshot);
            var stops = founded ? [] :
                settlement.Platforms(snapshot, ownLayout, ImmutableArray.CreateBuilder<SceneProblem>(), false);
            if (TrainStop(founded, ownLayout, onNode, stops) is { } placed)
            {
                var path = new NodeTrainPath(ownLayout, dimensions, onNode, placed.Stop,
                    followedTrain.LengthMeters * RailwayCoordinates.CellsPerMeter, AdjacentRailway);
                focus = path.Sample(path.Arc(onNode.DistanceMeters)).Position;
                following = FollowPath(path, followedTrain, onNode.DistanceMeters);
            }
        }
        var corridorTrain = enteringTrain ?? followedTrain;
        if (target is WorldSceneTarget.Follow follow && snapshot.Core.FindTrain(follow.Train) is Lookup<TrainView>.Found found)
            corridorTrain = found.Value;
        UpdateCorridor(corridorTrain);
        followedPath = following;
        if (focus is { } requestedFocus) range = new(requestedFocus, NodeStreaming.PrefetchCells);
        UpdatePrefetch(target, following);
        var hadLayout = layout is not null;
        if (hadLayout) settlement.Pump(snapshot, layout!, range, allowBuild, prefetch, corridor);
        if (prepared is null && pending?.IsCompleted == true)
        {
            try
            {
                prepared = pending.GetAwaiter().GetResult();
                built = prepared.Global;
                if (layout is null) { layout = built.Layout; InitializeRange(); }
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
        UpdatePrefetch(target, following);
        if (prepared is null) return;
        if (!hadLayout) settlement.Pump(snapshot, layout!, range, allowBuild, prefetch, corridor);
        lock (workGate) ReconcileParts();
        if (!ReferenceEquals(publishedBuilt, built) || !ReferenceEquals(publishedParts, cacheParts))
        {
            cache.Publish(Node, owner, built! with { Parts = cacheParts });
            publishedBuilt = built;
            publishedParts = cacheParts;
        }
        ScheduleRefresh();
    }

    private void UpdateCorridor(TrainView? train)
    {
        if (train is null || Layout is not { } currentLayout) return;
        var position = train.Position switch
        {
            TrainPosition.Node at => at,
            TrainPosition.Edge at => new TrainPosition.Node(Node, at.Id, at.Track, null, null, NodeTravelPhase.Approaching, 0, null),
            _ => throw new ArgumentException("Invalid corridor position.")
        };
        var platforms = Platforms(snapshot, currentLayout: currentLayout).Stops;
        var input = (currentLayout, position.EnteredFrom, position.EnteredTrack, position.NextEdge, position.NextTrack,
            position.PlatformNumber, position.Phase == NodeTravelPhase.Departing, train.LengthMeters);
        if (corridorInput == input && corridorPlatforms.SequenceEqual(platforms)) return;
        corridorInput = input;
        corridorPlatforms = platforms;
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(Node)).Value;
        corridor = new(currentLayout, dimensions, position, train.LengthMeters * RailwayCoordinates.CellsPerMeter, platforms);
        corridorTerrainX.Clear();
        corridorTerrainZ.Clear();
        foreach (var from in chunks.Keys)
            if (InCorridor(ChunkKey(from), ChunkBounds(from)))
            { corridorTerrainX.Add(from); corridorTerrainZ.Add(from); }
    }

    private void UpdatePrefetch(WorldSceneTarget? target, FollowedPath? following)
    {
        if (target is WorldSceneTarget.Location && layout is { Template: not null })
            prefetch = NodeStreaming.SettlementPrefetch(layout);
        else
            prefetch = new(following?.Ahead ?? range.Focus, NodeStreaming.PrefetchCells);
    }

    private void AcceptRefresh(SceneWorldSnapshot? current = null)
    {
        if (current is not null) snapshot = current;
        var task = refresh;
        if (task?.IsCompleted != true) return;
        refresh = null;
        if (cancellation.IsCancellationRequested || failure is not null) return;
        try
        {
            var next = task.GetAwaiter().GetResult();
            if (NeedsRefresh(snapshot, next.Global.Layout))
            {
                ScheduleRefresh();
                return;
            }
            Adopt(next);
        }
        catch (RailwayBuildException error) { Fail(error); }
        catch (OperationCanceledException) { }
    }

    private void ScheduleRefresh()
    {
        if (!AcceptTrackChanges || refresh is not null || layout is null || prepared is null || failure is not null ||
            cancellation.IsCancellationRequested || !NeedsRefresh(snapshot, layout)) return;
        var captured = snapshot;
        var node = Node;
        var previous = cache.Read(node);
        var token = cancellation.Token;
        refresh = Task.Run(() => BuildPreparation(captured, NodeLayout.Build(captured, node, token), previous, token), token);
    }

    private Preparation BuildPreparation(SceneWorldSnapshot current, NodeLayout currentLayout, Cached? previous, CancellationToken token) =>
        nodeBuild is null ? Prepare(current, currentLayout, previous, token) :
            new(current, nodeBuild(current, Node, previous, token), null, default, [], [], [], [], "", [], [], false);

    private static bool NeedsRefresh(SceneWorldSnapshot snapshot, NodeLayout layout) =>
        EntryTracksDiffer(snapshot, layout) || NodeLayout.LairModel(snapshot, layout.Node) != layout.Lair?.Model;

    private static bool EntryTracksDiffer(SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        foreach (var entry in layout.Entries)
        {
            var tracks = snapshot.Core.World.Edges.FirstOrDefault(edge => edge.Id == entry.Edge)?.Tracks;
            if (tracks is not null && tracks != layout.EntryTracks.GetValueOrDefault(entry.Edge)) return true;
        }
        return false;
    }

    private void Adopt(Preparation next)
    {
        RetireStreaming();
        corridorInput = null;
        retainedCorridorParts.Clear();
        var previousBuilt = built;
        prepared = next;
        built = next.Global;
        layout = next.Global.Layout;
        if (previousBuilt is not null)
            foreach (var key in previousBuilt.Parts.Keys)
                if (!built.Parts.ContainsKey(key)) Detach(key);
        foreach (var part in built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (previousBuilt?.Parts.TryGetValue(part.Key, out var old) == true && ReferenceEquals(old, part.Value)) continue;
            Attach(part.Key, part.Value);
        }
        foreach (var key in rails.Keys.Where(key => !next.Rails.ContainsKey(key)).ToArray())
        {
            rails.Remove(key);
            Detach(key);
            railFogDirty = true;
        }
        rebuilding.Clear();
        foreach (var pair in chunks)
            if (pair.Value.Inputs != next.ChunkInputs(pair.Key)) rebuilding.Add(ChunkKey(pair.Key));
        foreach (var pair in rails)
            if (pair.Value.Inputs != next.Rails[pair.Key].Inputs) rebuilding.Add(pair.Key);
        builtRailsByChunk.Clear();
        foreach (var key in rails.Keys) IndexRail(key, true);
        railFogDirty = true;
    }

    private void RetireStreaming()
    {
        lock (workGate)
        {
            foreach (var key in pendingParts.Keys.ToArray())
            {
                var work = pendingParts[key];
                work.Cancellation.Cancel();
                retiring.Add(work);
                pendingParts.Remove(key);
            }
            completedParts.Clear();
            waitingParts.Clear();
        }
    }

    internal bool NearPrepared => failure is null && StructuresReady(NodeStreaming.NearCells) &&
        !settlement.HasPendingBuilds && !vehicles.HasPendingBuilds;
    internal void MarkNearReady()
    {
        if (NearPrepared) nearCompleted = true;
    }
    internal SceneProblem? Failure => failure;
    internal void Report(SceneProblem problem) => reported.Add(problem);

    private bool StructuresReady(double radius)
    {
        if (prepared is null || layout is null) return false;
        var needed = range.Around(radius);
        if (prepared.HasChunks && needed.Chunks(layout).Any(from => !chunks.ContainsKey(from) ||
            rebuilding.Contains(ChunkKey(from)))) return false;
        return !prepared.Rails.Values.Any(segment => needed.Intersects(segment.Bounds) &&
            (!rails.ContainsKey(segment.Key) || rebuilding.Contains(segment.Key)));
    }

    private void Attach(string key, Part part)
    {
        attached[key] = WorldSceneSession.Attach(part.Geometry, "node:" + key + ":", objectId);
        cacheParts = cacheParts.SetItem(key, part);
        candidateChanges.Add(key);
        staticChanges.Add(key);
    }

    private void Detach(string key)
    {
        attached.Remove(key);
        cacheParts = cacheParts.Remove(key);
        candidateChanges.Add(key);
        staticChanges.Add(key);
    }

    private void ChangeTerrain(GridColumn from, bool present)
    {
        if (present)
        {
            terrainChunks.Add(from);
            unbuiltCorridorChunks.Remove(from);
            if (InCorridor(ChunkKey(from), ChunkBounds(from)))
            { corridorTerrainX.Add(from); corridorTerrainZ.Add(from); peripheralChunks.Remove(from); }
            else peripheralChunks.Add(from);
        }
        else
        { terrainChunks.Remove(from); corridorTerrainX.Remove(from); corridorTerrainZ.Remove(from); peripheralChunks.Remove(from); }
        dirtyTerrainRows.Add(from.Z);
        railFogDirty = true;
    }

    private static IEnumerable<GridColumn> BoundsChunks(SceneBounds bounds)
    {
        var size = VoxelGrid.ChunkSize;
        for (var z = (int)Math.Floor(bounds.Minimum.Z / size) * size; z < bounds.Maximum.Z; z += size)
        for (var x = (int)Math.Floor(bounds.Minimum.X / size) * size; x < bounds.Maximum.X; x += size)
            yield return new(x, z);
    }

    private void IndexRail(string key, bool present)
    {
        foreach (var from in BoundsChunks(prepared!.Rails[key].Bounds))
            if (present)
            {
                if (!builtRailsByChunk.TryGetValue(from, out var keys)) builtRailsByChunk.Add(from, keys = []);
                keys.Add(key);
            }
            else if (builtRailsByChunk.TryGetValue(from, out var keys))
            {
                keys.Remove(key);
                if (keys.Count == 0) builtRailsByChunk.Remove(from);
            }
    }
    internal static double GroundHeight(IEnumerable<Part> parts, double x, double z)
    {
        foreach (var part in parts)
        {
            if (part.Heights.Count == 0) continue;
            var column = new GridColumn((int)Math.Floor(x - part.Origin.X), (int)Math.Floor(z - part.Origin.Z));
            if (part.Heights.TryGetValue(column, out var height)) return height;
        }
        return 0;
    }

    internal static string ChunkKey(GridColumn from) => "terrain:" + from.X + ":" + from.Z;

    private void ReconcileParts()
    {
        if (indexedCorridor != corridor || indexedPreparation != prepared)
        {
            unbuiltCorridorChunks.Clear();
            if (prepared!.HasChunks && corridor is not null)
                unbuiltCorridorChunks.UnionWith(corridor.Chunks.Where(from => !chunks.ContainsKey(from) || rebuilding.Contains(ChunkKey(from))));
            corridorRails.Clear();
            foreach (var segment in prepared!.Rails.Values)
                if (corridor?.Intersects(segment.Bounds) == true) corridorRails.Add(segment.Key);
            peripheralChunks.Clear();
            peripheralChunks.UnionWith(chunks.Keys.Where(from => !InCorridor(ChunkKey(from), ChunkBounds(from))));
            peripheralRails.Clear();
            peripheralRails.UnionWith(rails.Keys.Where(key => !InCorridor(key, prepared.Rails[key].Bounds)));
            indexedCorridor = corridor;
            indexedPreparation = prepared;
        }
        var plan = (prepared!, range, prefetch, corridor, nearCompleted);
        var path = followedPath?.Priority ?? [];
        var changed = streamingPlan != plan || !plannedPath.SequenceEqual(path);
        if (changed)
        {
            var retention = range.Around(NodeStreaming.RetainCells);
            foreach (var key in completedParts.Keys.Where(key => !retention.Intersects(completedParts[key].Bounds) &&
                !prefetch.Intersects(completedParts[key].Bounds) && !InCorridor(key, completedParts[key].Bounds)).ToArray())
                completedParts.Remove(key);
            foreach (var key in pendingParts.Keys.Where(key => !range.Intersects(pendingParts[key].Bounds) &&
                !prefetch.Intersects(pendingParts[key].Bounds) && !InCorridor(key, pendingParts[key].Bounds)).ToArray())
            {
                var work = pendingParts[key];
                work.Cancellation.Cancel(); retiring.Add(work); pendingParts.Remove(key);
            }
            foreach (var from in peripheralChunks.Where(from => !retention.Intersects(from) && !prefetch.Intersects(from)).ToArray())
            { rebuilding.Remove(ChunkKey(from)); chunks.Remove(from); ChangeTerrain(from, false); Detach(ChunkKey(from)); }
            foreach (var key in peripheralRails.Where(key => !retention.Intersects(prepared!.Rails[key].Bounds) &&
                !prefetch.Intersects(prepared.Rails[key].Bounds)).ToArray())
            { rebuilding.Remove(key); IndexRail(key, false); rails.Remove(key); peripheralRails.Remove(key); Detach(key); railFogDirty = true; }
        }
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
        if (changed)
        {
            var neededChunks = range.Chunks(layout!);
            if (prefetch != range) neededChunks = neededChunks.Concat(prefetch.Chunks(layout!)).Distinct();
            if (corridor is not null) neededChunks = neededChunks.Concat(unbuiltCorridorChunks).Distinct();
            var terrain = prepared!.HasChunks ? neededChunks.Where(from =>
                {
                    var key = ChunkKey(from);
                    var current = chunks.ContainsKey(from) && !rebuilding.Contains(key);
                    return !current && !pendingParts.ContainsKey(key) && !completedParts.ContainsKey(key);
                }).Select(from => (Key: ChunkKey(from), Bounds: ChunkBounds(from), Chunk: (GridColumn?)from)) : [];
            var tracks = prepared.Rails.Values.Where(segment =>
                    (!rails.ContainsKey(segment.Key) || rebuilding.Contains(segment.Key)) &&
                    !pendingParts.ContainsKey(segment.Key) && !completedParts.ContainsKey(segment.Key) &&
                    (range.Intersects(segment.Bounds) || prefetch.Intersects(segment.Bounds) || corridorRails.Contains(segment.Key)))
                .Select(segment => (segment.Key, segment.Bounds, Chunk: (GridColumn?)null));
            waitingParts = new(terrain.Concat(tracks).OrderBy(item => Priority(item.Bounds)).ThenBy(item => item.Key, StringComparer.Ordinal));
            streamingPlan = plan;
            plannedPath = path;
            StreamingPlanRevision++;
        }
        StartParts();
    }

    private bool InCorridor(string key, SceneBounds bounds) => retainedCorridorParts.Contains(key) || corridor?.Intersects(bounds) == true;

    private (int Ring, double Distance, double Progress) Priority(SceneBounds bounds)
    {
        var (distance, progress) = followedPath?.Priority is { Count: > 0 } path
            ? NodeStreaming.PathPriority(bounds, path) : (range.DistanceSquared(bounds), 0);
        var ring = !nearCompleted && range.Around(NodeStreaming.NearCells).Intersects(bounds) ? 0 :
            range.Around(NodeStreaming.ViewCells).Intersects(bounds) ? 1 : corridor?.Intersects(bounds) == true ? 2 : 3;
        return (corridor is null ? !nearCompleted && !range.Around(NodeStreaming.NearCells).Intersects(bounds) ? 1 : 0 : ring,
            distance, progress);
    }

    private static SceneBounds ChunkBounds(GridColumn from) => new(new(from.X, -128, from.Z),
        new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize));

    private void AdmitCompletedParts()
    {
        if (!preparingCandidate && nearCompleted && awaitingDisplay) return;
        var near = range.Around(NodeStreaming.NearCells);
        long vertices = 0;
        var visuals = 0;
        var count = 0;
        var pathBatch = false;
        const double pathMargin = NodeStreaming.PathSampleCells;
        foreach (var (key, ready, priority) in completedParts
            .Select(item => (item.Key, item.Value, Priority: Priority(item.Value.Bounds)))
            .OrderBy(item => item.Priority).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            if (!preparingCandidate && !nearCompleted && !near.Intersects(ready.Bounds) && NodeStreaming.PathPriority(ready.Bounds, followedPath?.Loading ?? []).Distance >
                pathMargin * pathMargin) continue;
            // One indivisible part may exceed the batch target, but must never block the queue.
            // Keep path parts separate from peripheral work.
            if ((nearCompleted || preparingCandidate) && count > 0 && (vertices + ready.Vertices > NodeStreaming.AdmissionVertexLimit ||
                visuals + ready.Visuals > NodeStreaming.AdmissionVisualLimit ||
                pathBatch && priority.Distance != 0)) break;
            if (ready.Chunk is { } from) { chunks[from] = ready.Part; ChangeTerrain(from, true); }
            else
            {
                rails[key] = ready.Part;
                IndexRail(key, true);
                if (InCorridor(key, ready.Bounds)) peripheralRails.Remove(key); else peripheralRails.Add(key);
                railFogDirty = true;
            }
            rebuilding.Remove(key);
            Attach(key, ready.Part);
            if (corridor?.Intersects(ready.Bounds) == true) retainedCorridorParts.Add(key);
            completedParts.Remove(key);
            vertices += ready.Vertices;
            visuals += ready.Visuals;
            pathBatch |= followedPath?.Priority.Count > 0 && priority.Distance == 0;
            count++;
        }
        if (!preparingCandidate && nearCompleted && count > 0) awaitingDisplay = true;
    }

    internal ImmutableArray<SceneObject> CandidateObjects(SceneWorldSnapshot current)
    {
        var objects = attached.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SelectMany(item => item.Value.Geometry.Objects).ToImmutableArray();
        if (layout is null) return objects;
        var instances = settlement.Present(current, layout, default, ImmutableArray.CreateBuilder<SceneFog>(), findId,
            range, terrainChunks, prefetch, allowBuild: false, corridor: corridor);
        return objects.AddRange(instances.Objects);
    }

    internal (ImmutableArray<SceneObject> Upserts, ImmutableArray<SceneObjectId> Removed) CandidateChanges(
        SceneWorldSnapshot current, bool full)
    {
        var upserts = new Dictionary<SceneObjectId, SceneObject>();
        var removed = new HashSet<SceneObjectId>();
        foreach (var key in candidateChanges)
        {
            if (candidatePartObjects.Remove(key, out var old))
                foreach (var id in old) { candidateObjects.Remove(id); removed.Add(id); }
            if (!attached.TryGetValue(key, out var part)) continue;
            candidatePartObjects[key] = part.Geometry.Objects.Select(item => item.Id).ToImmutableArray();
            foreach (var item in part.Geometry.Objects)
            { candidateObjects[item.Id] = item; upserts[item.Id] = item; removed.Remove(item.Id); }
        }
        candidateChanges.Clear();
        if (layout is not null)
        {
            var instances = settlement.Present(current, layout, default, ImmutableArray.CreateBuilder<SceneFog>(), findId,
                range, terrainChunks, prefetch, allowBuild: false, corridor: corridor).Objects;
            var oldInstances = candidateInstances.ToDictionary(item => item.Id);
            foreach (var item in instances)
            {
                if (!oldInstances.Remove(item.Id, out var old) || item != old) upserts[item.Id] = item;
                candidateObjects[item.Id] = item;
            }
            foreach (var id in oldInstances.Keys) { candidateObjects.Remove(id); removed.Add(id); }
            candidateInstances = instances;
        }
        return (full ? candidateObjects.Values.OrderBy(item => item.Id.Value).ToImmutableArray() :
            upserts.Values.OrderBy(item => item.Id.Value).ToImmutableArray(),
            full ? [] : removed.OrderBy(id => id.Value).ToImmutableArray());
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
        AcceptRefresh(snapshot);
        var (start, seconds) = SceneFrame.SampleTime(snapshot, previous, request.Interpolation);
        displayRate.Update(seconds);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var fog = ImmutableArray.CreateBuilder<SceneFog>();
        var trainFog = ImmutableArray.CreateBuilder<SceneFog>();
        var following = layout is null ? null : RenderTrains(snapshot, start, request.Interpolation, origin, objects,
            picking, problems, trainFog, (target as WorldSceneTarget.Follow)?.Vehicle, seconds);
        var trainPicks = picking.Count;
        ScenePoint? focus = null;
        if (target is WorldSceneTarget.Follow follow && objects.FirstOrDefault(item => item.Parent is null &&
            item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(follow.Vehicle))) is { } vehicle)
            focus = RailwayCoordinates.Add(origin, vehicle.Pose.Position);
        else if (target is not WorldSceneTarget.Follow && request.Camera is { } camera &&
            camera.Frame.Update.Frame.Session == stamp.Session && camera.Frame.Update.Frame.Request == stamp.Request &&
            camera.Frame.Update.Frame.Space == stamp.Space)
            focus = RailwayCoordinates.Add(camera.Frame.Origin, camera.Focus);
        Pump(snapshot, focus, following: following, target: target);
        UpdateStaticGeometry(origin);
        problems.AddRange(staticGeometry.Diagnostics);
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
                if (lookup is not ContentLookup<SettlementLayoutProjection>.Found)
                {
                    if (lookup is ContentLookup<SettlementLayoutProjection>.Missing missing)
                        DesignGeometry.AddDiagnostics(missing.Diagnostics, problems, new WorldObjectId.Node(Node));
                    problems.Add(new("content_missing", SceneStage.Input, new WorldObjectId.Node(Node), null, null, null,
                        "The settlement layout copy is missing."));
                }
            }
            if (NodeLayout.LairContent(snapshot, Node) is { } lair &&
                lair.Appearance is not ContentLookup<LairAppearanceProjection>.Found)
            {
                var diagnostics = lair.Appearance is ContentLookup<LairAppearanceProjection>.Missing missing ? missing.Diagnostics : [];
                RailwayTerrain.ReportMissing(new ContentReference.Core(new CoreDefinitionReference.Lair(lair.Type)),
                    diagnostics, "The lair scene model is missing.", problems, new WorldObjectId.Node(Node));
            }
            Add(settlement.Present(snapshot, layout, origin, fog, findId, range, terrainChunks, prefetch, presentation, seconds, corridor: corridor));
            if (layout.Lair is { } placed &&
                snapshot.Core.World.Nodes.Single(item => item.Id == Node).Occupant is NodeOccupant.Lair occupant)
                objects.Add(new(objectId("lair:" + occupant.Id.Value), null,
                    new(RailwayCoordinates.Relative(new(placed.ShiftX, 0, placed.ShiftZ), origin), Quaternion.Identity),
                    new WorldObjectId.Lair(occupant.Id), [], [], []));
        }
        if (failure is not null) problems.Add(failure);
        foreach (var problem in reported) problems.Add(problem);
        reported.Clear();
        bool Complete(double radius)
        {
            if (!StructuresReady(radius)) return false;
            var needed = range.Around(radius);
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
        var effects = ImmutableArray<SceneEffectCue>.Empty;
        if (presentation is { } state && layout is not null)
        {
            state.AddCreatures(objects, snapshot, new WorldLocation.Node(Node), origin, null, layout, seconds, objectId,
                (x, z) => GroundHeight(staticParts, x, z));
            if (failure is null)
                effects = state.Finish(objects, snapshot, new WorldLocation.Node(Node), origin, null);
        }
        return new(stamp, origin, seconds, readiness,
            objects.ToImmutable(), picking.ToImmutable(), fog.ToImmutable(), RailwayTerrain.UniqueProblems(problems), displayed,
            node: layout, nodeParts: staticParts,
            debugLayers: request.DebugLayers, nodeTracks: layout?.EntryTracks, pickRay: request.PickRay,
            effects: effects, terrain: staticTerrain, staticGeometry: staticGeometry, staticPickOffset: trainPicks);

        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    private void UpdateStaticGeometry(ScenePoint origin)
    {
        if (ReferenceEquals(presentedParts, cacheParts) && staticOrigin == origin) return;
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var shift = new ScenePose(RailwayCoordinates.Relative(default, origin), Quaternion.Identity);
        if (staticOrigin != origin) staticChanges.UnionWith(attached.Keys);
        foreach (var key in staticChanges)
        {
            var terrainKey = "terrain:" + key;
            if (!attached.TryGetValue(key, out var part))
            { placedParts.Remove(key); terrainByPart.Remove(key); terrainPatches.Remove(terrainKey); continue; }
            placedParts[key] = SceneGeometry.Place(part.Geometry, shift);
            var raw = cacheParts[key];
            if (terrainPatches.Get(terrainKey, () => objectId(terrainKey), raw.Origin, origin, raw.Heights) is { } patch)
                terrainByPart[key] = patch;
            else terrainByPart.Remove(key);
        }
        staticChanges.Clear();
        foreach (var part in placedParts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var geometry = part.Value;
            objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics);
        }
        staticGeometry = new(objects.ToImmutable(), picking.ToImmutable(), RailwayTerrain.UniqueProblems(problems));
        var parts = new List<(string Key, Part Part)>();
        if (built is not null)
            parts.AddRange(built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (item.Key, item.Value)));
        parts.AddRange(chunks.OrderBy(item => item.Key.Z).ThenBy(item => item.Key.X).Select(item => (ChunkKey(item.Key), item.Value)));
        parts.AddRange(rails.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (item.Key, item.Value)));
        staticParts = parts.Select(item => item.Part).ToImmutableArray();
        staticTerrain = parts.Where(item => terrainByPart.ContainsKey(item.Key)).Select(item => terrainByPart[item.Key]).ToImmutableArray();
        presentedParts = cacheParts;
        staticOrigin = origin;
    }

    private ImmutableArray<SceneFog> RailFog(ScenePoint origin)
    {
        if (prepared is null) return [];
        bool NeedsFog(RailSegment segment) => !rails.ContainsKey(segment.Key) &&
            (range.Intersects(segment.Bounds) || NodeStreaming.OverlapsTerrain(segment.Bounds, terrainChunks));
        if (railFogDirty || railFogRange != range)
        {
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
                var missing = foggedRails.Select(key => prepared.Rails[key].Bounds).ToImmutableArray();
                var cutters = new HashSet<string>();
                foreach (var area in missing)
                foreach (var from in BoundsChunks(area))
                    if (builtRailsByChunk.TryGetValue(from, out var keys)) cutters.UnionWith(keys);
                railFogBounds = ConstructionFog.Clip(missing, cutters.Order(StringComparer.Ordinal)
                    .SelectMany(key => prepared.Rails[key].Footprint));
                railFogDirty = false;
                railFogOrigin = null;
            }
            railFogRange = range;
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
        var left = Math.Min(retained.Left, Math.Min(ahead.Left, corridorTerrainX.Count == 0 ? retained.Left : corridorTerrainX.Min.X));
        var right = Math.Max(retained.Right, Math.Max(ahead.Right, corridorTerrainX.Count == 0 ? retained.Right : corridorTerrainX.Max.X + VoxelGrid.ChunkSize));
        var back = Math.Min(retained.Back, Math.Min(ahead.Back, corridorTerrainZ.Count == 0 ? retained.Back : corridorTerrainZ.Min.Z));
        var front = Math.Max(retained.Front, Math.Max(ahead.Front, corridorTerrainZ.Count == 0 ? retained.Front : corridorTerrainZ.Max.Z + VoxelGrid.ChunkSize));
        if (terrainFogWindow == (left, right, back, front) && dirtyTerrainRows.Count == 0 && terrainFogOrigin == origin)
        { fog.AddRange(cachedTerrainFog); return; }
        Add(minimum.X, left, minimum.Y, maximum.Y);
        Add(right, maximum.X, minimum.Y, maximum.Y);
        Add(left, right, minimum.Y, back);
        Add(left, right, front, maximum.Y);
        var window = (left, right, back, front);
        if (terrainFogWindow is not { } old || old.Left != left || old.Right != right)
        {
            terrainFogRows.Clear();
            for (var z = back; z < front; z += VoxelGrid.ChunkSize) dirtyTerrainRows.Add(z);
        }
        else if (old.Back != back || old.Front != front)
        {
            for (var z = back; z < front; z += VoxelGrid.ChunkSize)
                if (z < old.Back || z >= old.Front) dirtyTerrainRows.Add(z);
            foreach (var z in terrainFogRows.Keys.Where(z => z < back || z >= front).ToArray()) terrainFogRows.Remove(z);
        }
        terrainFogWindow = window;
        foreach (var z in dirtyTerrainRows)
        {
            if (z < back || z >= front) continue;
            var row = ImmutableArray.CreateBuilder<SceneBounds>();
            int? start = null;
            for (var x = left; x < right; x += VoxelGrid.ChunkSize)
            {
                if (!terrainChunks.Contains(new(x, z))) start ??= x;
                else if (start is { } begin) { Missing(begin, x); start = null; }
            }
            if (start is { } last) Missing(last, right);
            terrainFogRows[z] = row.ToImmutable();
            void Missing(int begin, int end) => row.Add(new(new(begin, -128, z), new(end, 128, z + VoxelGrid.ChunkSize)));
        }
        dirtyTerrainRows.Clear();
        for (var z = back; z < front; z += VoxelGrid.ChunkSize)
            foreach (var bounds in terrainFogRows[z]) Add(bounds.Minimum.X, bounds.Maximum.X, bounds.Minimum.Z, bounds.Maximum.Z);
        cachedTerrainFog = fog.ToImmutable();
        terrainFogOrigin = origin;

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
        if (layout.Lair is { } placed)
            model = model.AddRange(placed.Model.Scene.Voxels.Select(voxel => voxel with
            {
                At = new(voxel.At.X + placed.ShiftX, voxel.At.Y, voxel.At.Z + placed.ShiftZ)
            }));
        foreach (var voxel in model.Select(v => v.Voxel).Distinct())
            appearances[voxel] = (snapshot.Content.FindVoxelAppearance(voxel) as ContentLookup<VoxelAppearanceProjection>.Found)?.Value;
        var modelChunks = model.GroupBy(v => new GridColumn((int)Math.Floor((double)v.At.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor((double)v.At.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize)).ToDictionary(g => g.Key, g => g.ToImmutableArray());
        var parts = ImmutableDictionary.CreateBuilder<string, Part>();
        var rails = ImmutableDictionary.CreateBuilder<string, RailSegment>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        var bed = new Dictionary<GridColumn, RailBed>();
        var boundaryColumns = new HashSet<GridColumn>();
        var boundarySurfaces = new Dictionary<GridColumn, double>();
        var boundaries = ImmutableDictionary.CreateBuilder<EdgeId, RailwayTerrain.BoundaryData>();
        var boundaryOwners = new Dictionary<GridColumn, (EdgeId Edge, double Distance)>();
        var terrainInputs = RailwayDependencies.Values(new object?[] { snapshot.Core.World.WorldSeed, map.Position, map.Terrain.TypeId,
            map.Terrain.Relief, TerrainInputs(snapshot.Content, map.Terrain.TypeId), NodeSettlement.Inputs(layout.Template),
            LairInputs(layout) });
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
        var allowedByEdge = new Dictionary<EdgeId, ImmutableHashSet<GridColumn>>();
        foreach (var entry in layout.Entries)
        {
            var boundary = boundaries[entry.Edge];
            GridColumn Global(GridColumn column) => new((int)Math.Round(column.X + boundary.Origin.X), (int)Math.Round(column.Z + boundary.Origin.Z));
            var allowed = boundary.Heights.Keys.Where(column => boundaryOwners[Global(column)].Edge == entry.Edge).ToImmutableHashSet();
            allowedByEdge[entry.Edge] = allowed;
            foreach (var column in allowed)
            {
                var global = Global(column);
                boundaryColumns.Add(global);
                boundarySurfaces[global] = boundary.Heights[column] - 1 + Math.Round(boundary.Origin.Y);
            }
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
        foreach (var entry in layout.Entries)
        {
            var boundary = boundaries[entry.Edge];
            var allowed = allowedByEdge[entry.Edge];
            var skirt = ExtendBoundary(boundary, allowed);
            var inputs = RailwayDependencies.Values(new object[] { layout.Inputs, boundary.Dependencies.Key,
                boundary.Heights.Keys.Where(column => !allowed.Contains(column)).OrderBy(column => column.X).ThenBy(column => column.Z).ToArray(),
                skirt.Bottoms });
            Put("boundary:" + entry.Edge.Value, inputs, () => GridPart(
                boundary.Grid.Cells.Where(cell => allowed.Contains(new(cell.At.X, cell.At.Z))).Concat(skirt.Cells).ToImmutableArray(),
                boundary.Heights.Where(item => allowed.Contains(item.Key)).ToImmutableDictionary(), [], boundary.Origin));
        }
        Put("diagnostics", problemInputs, () => GridPart([], ImmutableDictionary<GridColumn, double>.Empty, uniqueProblems));
        return new(snapshot, new(layout, parts.ToImmutable(), boundaries.ToImmutable()), terrain, ballastVoxel,
            appearances, modelChunks, bedChunks, boundaryChunks, terrainInputs, rails.ToImmutable(),
            BoundarySurfaces: boundarySurfaces);

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

        (SceneCell[] Cells, int[][] Bottoms) ExtendBoundary(RailwayTerrain.BoundaryData boundary, ImmutableHashSet<GridColumn> allowed)
        {
            var originX = (int)Math.Round(boundary.Origin.X);
            var originY = (int)Math.Round(boundary.Origin.Y);
            var originZ = (int)Math.Round(boundary.Origin.Z);
            var left = (int)layout.Bounds.Minimum.X;
            var right = (int)layout.Bounds.Maximum.X;
            var back = (int)layout.Bounds.Minimum.Y;
            var front = (int)layout.Bounds.Maximum.Y;
            var columns = new Dictionary<GridColumn, (int MinY, CellProperties Rock)>();
            foreach (var cell in boundary.Grid.Cells)
            {
                var local = new GridColumn(cell.At.X, cell.At.Z);
                if (!allowed.Contains(local)) continue;
                if (!columns.TryGetValue(local, out var known) || cell.At.Y < known.MinY)
                    columns[local] = (cell.At.Y, cell.Properties);
            }
            var cells = new List<SceneCell>();
            var bottoms = new List<int[]>();
            var ground = new Dictionary<GridColumn, int>();
            foreach (var (local, column) in columns)
            {
                token.ThrowIfCancellationRequested();
                var at = new GridColumn(local.X + originX, local.Z + originZ);
                int? lowest = null;
                foreach (var neighbor in new[] { new GridColumn(at.X + 1, at.Z), new GridColumn(at.X - 1, at.Z), new GridColumn(at.X, at.Z + 1), new GridColumn(at.X, at.Z - 1) })
                {
                    if (allowed.Contains(new(neighbor.X - originX, neighbor.Z - originZ))) continue;
                    int surface;
                    if (boundarySurfaces.TryGetValue(neighbor, out var owned))
                        surface = (int)Math.Floor(owned);
                    else if (neighbor.X >= left && neighbor.X < right && neighbor.Z >= back && neighbor.Z < front)
                    {
                        if (!ground.TryGetValue(neighbor, out surface))
                            ground[neighbor] = surface = NodeColumn(snapshot, map, layout, bed, neighbor).Height;
                    }
                    else continue;
                    if (lowest is null || surface < lowest) lowest = surface;
                }
                if (lowest is not int floor) continue;
                var start = floor + 1 - originY;
                if (start >= column.MinY) continue;
                for (var y = start; y < column.MinY; y++)
                    cells.Add(new(new(local.X, y, local.Z), column.Rock));
                bottoms.Add([at.X, at.Z, floor + 1]);
            }
            bottoms.Sort(static (left, right) => left[0] != right[0] ? left[0].CompareTo(right[0]) : left[1].CompareTo(right[1]));
            return (cells.ToArray(), bottoms.ToArray());
        }
    }

    private static object? LairInputs(NodeLayout layout) => layout.Lair is { } lair
        ? new object?[] { lair.Model.Design, lair.ShiftX, lair.ShiftZ } : null;

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
        GridColumn from, Dictionary<GridColumn, double> boundarySurfaces, CancellationToken token)
    {
        var cellCount = terrain is null ? 0 :
            (VoxelGrid.ChunkSize * VoxelGrid.ChunkSize - boundaryColumns.Count) * RailwayCoordinates.TerrainDepthCells;
        var cells = ImmutableArray.CreateBuilder<SceneCell>(cellCount);
        var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
        var subject = new PickSubject.Terrain(new WorldLocation.Node(node.Id));
        var layers = RailwayTerrain.PrepareLayers(terrain, id => appearances.GetValueOrDefault(id), subject);
        var ballast = bed.Count > 0 ? RailwayTerrain.PrepareVoxel(ballastVoxel, id => appearances.GetValueOrDefault(id), subject) : null;
        var bounds = layout.Bounds;
        var left = (int)bounds.Minimum.X;
        var right = (int)bounds.Maximum.X;
        var back = (int)bounds.Minimum.Y;
        var front = (int)bounds.Maximum.Y;
        var computed = new Dictionary<GridColumn, (int Height, double Distance)>();
        for (var z = from.Z; z < from.Z + VoxelGrid.ChunkSize; z++)
        for (var x = from.X; x < from.X + VoxelGrid.ChunkSize; x++)
        {
            token.ThrowIfCancellationRequested();
            var column = new GridColumn(x, z);
            if (boundaryColumns.Contains(column)) continue;
            var (height, distance) = At(column);
            heights.Add(column, height + 1);
            RailwayTerrain.FillColumn(cells, column, height, layers, ballast, distance, double.IsFinite(distance),
                lowestNeighbor: Lowest(column));
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
        cells.RemoveAll(cell => occupied.Contains(cell.At));
        cells.AddRange(modelCells);
        return GridPart(cells.DrainToImmutable(), heights.ToImmutable(), []);

        (int Height, double Distance) At(GridColumn column)
        {
            if (!computed.TryGetValue(column, out var value))
                computed[column] = value = NodeColumn(snapshot, node, layout, bed, column);
            return value;
        }

        double? Lowest(GridColumn column)
        {
            double? lowest = null;
            void Consider(GridColumn neighbor)
            {
                if (neighbor.X < left || neighbor.X >= right || neighbor.Z < back || neighbor.Z >= front) return;
                var surface = boundarySurfaces.TryGetValue(neighbor, out var owned) ? owned : At(neighbor).Height;
                if (lowest is null || surface < lowest) lowest = surface;
            }
            Consider(new(column.X + 1, column.Z));
            Consider(new(column.X - 1, column.Z));
            Consider(new(column.X, column.Z + 1));
            Consider(new(column.X, column.Z - 1));
            return lowest;
        }
    }

    private static (int Height, double Distance) NodeColumn(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        Dictionary<GridColumn, RailBed> bed, GridColumn column)
    {
        var point = new ScenePoint(column.X + 0.5, 0, column.Z + 0.5);
        var raw = RailwayTerrain.NodeGround(snapshot, node, point, layout.Template);
        if (layout.Lair is { } lair)
            raw = NodeSettlement.FlattenGround(lair.Model.Scene, lair.ShiftX, lair.ShiftZ, point, raw);
        var rail = bed.GetValueOrDefault(column);
        var distance = rail?.Distance ?? double.PositiveInfinity;
        if (rail is not null)
        {
            var blend = Math.Clamp((distance - RailwayCoordinates.BedBaseHalfWidth) / RailwayCoordinates.BedBlendWidth, 0, 1);
            raw = RailwayTerrain.BedHeight(rail.Height, distance) * (1 - blend) + raw * blend;
        }
        return ((int)Math.Floor(raw), distance);
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
            objects.AddRange(DesignGeometry.Lights(grid, root, null, cell => id(":light:" + cell.At)));
            foreach (var cell in cells.Where(cell => cell.MissingReference is not null))
                objects.Add(new(id("missing:" + cell.At), root, new(new(cell.At.X, cell.At.Y, cell.At.Z), Quaternion.Identity), null,
                    [new SceneVisual.Missing(cell.MissingReference!, true)], [], []));
            return new(objects.ToImmutable(),
                cells.IsEmpty ? [] : [new(root, pose, grid)], problems);
        }), heights, origin);

    private static (int Station, NodeLayout.Stop Stop)? TrainStop(bool founded, NodeLayout layout,
        TrainPosition.Node position, ImmutableArray<NodeLayout.Stop> platforms)
    {
        var station = founded ? -1 : position.PlatformNumber is { } number ? number - 1 : -1;
        if (station >= platforms.Length) return null;
        return (station, station >= 0 ? platforms[station] : layout.Bypass);
    }

    private TrainView[] NodeTrains(SceneWorldSnapshot snapshot) => snapshot.Core.World.Trains
        .Where(item => item.Position is TrainPosition.Node position && position.Id == Node)
        .Select(item => snapshot.Core.FindTrain(item.Id)).OfType<Lookup<TrainView>.Found>()
        .Select(item => item.Value).OrderBy(train => train.Id.Value).ToArray();

    private FollowedPath? RenderTrains(SceneWorldSnapshot snapshot, SceneWorldSnapshot? previous, double interpolation,
        ScenePoint origin, ImmutableArray<SceneObject>.Builder objects,
        ImmutableArray<PickGrid>.Builder picking, ImmutableArray<SceneProblem>.Builder problems, ImmutableArray<SceneFog>.Builder fog,
        VehicleId? followedVehicle, double seconds)
    {
        var trains = NodeTrains(snapshot);
        if (trains.Length == 0) return null;
        FollowedPath? following = null;
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(Node)).Value;
        var queues = QueuePositions(trains, dimensions);
        var founded = FoundedSinceOpen(snapshot);
        var mapping = founded ? new PlatformMap(layout!, [], []) : Platforms(snapshot);
        problems.AddRange(mapping.Problems);
        var platforms = mapping.Stops;
        var oldTrains = previous is null ? [] : NodeTrains(previous);
        var oldQueues = QueuePositions(oldTrains, dimensions);
        var oldPlatforms = founded || previous is null ? [] : Platforms(previous, cacheProjections: false).Stops;
        foreach (var train in trains)
        {
            var members = train.Vehicles.Select(id => ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(id))).Value).ToArray();
            var evaluations = members.Select(member => snapshot.Content.FindDesign(member.Design)).ToArray();
            var lengths = evaluations.Select(value => RailwayVehicles.Length((value as ContentLookup<DesignEvaluation>.Found)?.Value)).ToArray();
            var known = lengths.All(length => length is > 0);
            var total = known ? lengths.Sum(length => length!.Value) : 0;
            var position = (TrainPosition.Node)train.Position;
            var entry = layout!.TrainEntry(position.EnteredFrom);
            var positive = train.OrderFacing == TrainOrderFacing.FirstEndLeading;
            if (TrainStop(founded, layout, position, platforms) is not { } placed)
            {
                problems.Add(new("station_platform_mismatch", SceneStage.Reconcile, new WorldObjectId.Train(train.Id),
                    null, null, null, "The assigned platform has no matching station slot."));
                continue;
            }
            var station = placed.Station;
            var path = new NodeTrainPath(layout, dimensions, position, placed.Stop,
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
                null, null, null, "Node track counts still use the previous layout until the replacement connects."));
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
                {
                    Add(RailwayVehicles.Place(body, pose, (positive ? 1 : -1) * arc));
                    presentation?.ApplyTurrets(objects, member, "vehicle:" + member.Id + ":", findId, seconds, snapshot, origin);
                }
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

    internal static ScenePoint[] SamplePath(NodeTrainPath path, double begin, double end)
    {
        var intervals = Math.Max(1, (int)Math.Ceiling((end - begin) / NodeStreaming.PathSampleCells));
        var points = new ScenePoint[intervals + 1];
        for (var i = 0; i <= intervals; i++) points[i] = path.Sample(begin + (end - begin) * i / intervals).Position;
        return points;
    }

    private PlatformMap Platforms(SceneWorldSnapshot input, bool cacheProjections = true, NodeLayout? currentLayout = null)
    {
        currentLayout ??= layout!;
        if (platformMaps.TryGetValue(input, out var cached) && cached.Layout == currentLayout) return cached;
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var stops = settlement.Platforms(input, currentLayout, problems, cacheProjections);
        var result = new PlatformMap(currentLayout, stops, problems.ToImmutable());
        platformMaps.Remove(input);
        platformMaps.Add(input, result);
        return result;
    }

    private Dictionary<TrainId, double> QueuePositions(TrainView[] trains, NodeView dimensions)
    {
        var result = new Dictionary<TrainId, double>();
        var waiting = dimensions.ApproachLengthMeters;
        var queued = trains.Where(train => train.Position is TrainPosition.Node { PlatformNumber: null } position &&
            (train.PlatformQueueEnteredStep is not null && position.DistanceMeters <= waiting ||
             train.SpeedMetersPerSecond == 0 && position.Phase == NodeTravelPhase.Waiting && position.DistanceMeters == waiting));
        foreach (var group in queued.GroupBy(train =>
        {
            var position = (TrainPosition.Node)train.Position;
            var entry = layout!.TrainEntry(position.EnteredFrom);
            return (entry?.Edge, Track: entry is null ? null : (int?)layout.TrainTrackNumber(entry, position.EnteredTrack));
        }))
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
            ownedWork = (pending is null ? Array.Empty<Task>() : [pending]).Concat(refresh is null ? [] : [refresh])
                .Concat(parts.Select(item => item.Task)).ToArray();
        }
        try { await Task.WhenAll(ownedWork).ConfigureAwait(false); }
        catch (Exception) { /* Retired scene work cannot be attached or published. */ }
        lock (workGate) foreach (var work in parts) work.Cancellation.Dispose();
        await settlement.DisposeAsync();
        cancellation.Dispose();
    }
}

using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeScene : IAsyncDisposable
{
    internal sealed record Part(string Inputs, BuiltGeometry Geometry, ImmutableDictionary<GridColumn, double> Heights, ScenePoint Origin = default);
    internal sealed record Built(NodeLayout Layout, ImmutableDictionary<string, Part> Parts,
        ImmutableDictionary<EdgeId, RailwayTerrain.BoundaryData> Boundaries);
    internal sealed record RailSegment(string Key, Func<double, RailwaySample> Sample, double Begin, double End,
        SceneBounds Bounds, string Inputs)
    {
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
        TerrainAppearanceProjection? Terrain,
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
                Snapshot.Core.World.Nodes.Single(node => node.Id == Global.Layout.Node), Global.Layout, Terrain, Appearances,
                BedChunks.GetValueOrDefault(from) ?? [], BoundaryChunks.GetValueOrDefault(from) ?? [],
                ModelChunks.GetValueOrDefault(from, []), from, token) with { Inputs = inputs };
        }
    }

    internal sealed class Cache
    {
        private readonly object gate = new();
        private ulong next;
        private readonly Dictionary<NodeId, (ulong Owner, Built? Built)> nodes = [];
        internal ulong Open(NodeId node)
        {
            lock (gate)
            {
                var old = nodes.GetValueOrDefault(node);
                nodes[node] = (++next, old.Built);
                return next;
            }
        }
        internal Built? Read(NodeId node) { lock (gate) return nodes.GetValueOrDefault(node).Built; }
        internal void Publish(NodeId node, ulong owner, Built built)
        {
            lock (gate) if (nodes.TryGetValue(node, out var old) && old.Owner == owner) nodes[node] = (owner, built);
        }
        internal void Retain(NodeId? current, NodeId? candidate)
        { lock (gate) foreach (var id in nodes.Keys.Where(id => id != current && id != candidate).ToArray()) nodes.Remove(id); }
    }

    private readonly Cache cache;
    private readonly ulong owner;
    private readonly Func<string, SceneObjectId> objectId;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SceneVehicles vehicles;
    private readonly SettlementObjects settlement;
    private SceneWorldSnapshot snapshot;
    private readonly ImmutableDictionary<EdgeId, int> openingTracks;
    private readonly Dictionary<string, BuiltGeometry> attached = [];
    private readonly Task<Preparation>? pending;
    private sealed record PartWork(Task<Part> Task, CancellationTokenSource Cancellation, SceneBounds Bounds, GridColumn? Chunk);
    private readonly Dictionary<GridColumn, Part> chunks = [];
    private readonly Dictionary<string, Part> rails = [];
    private readonly Dictionary<string, PartWork> pendingParts = [];
    private readonly List<PartWork> retiring = [];
    private readonly Func<Preparation, GridColumn, Part?, CancellationToken, Part> buildChunk;
    private readonly Func<Preparation, RailSegment, Part?, CancellationToken, Part> buildRail;
    private Preparation? prepared;
    private NodeRange range;
    private bool nearCompleted;
    private readonly NodeLayout? layout;
    private Built? built;
    private SceneProblem? failure;
    internal NodeId Node { get; }
    internal NodeLayout? Layout => layout;
    internal Task? BuildWork => pending;
    internal Task[] PendingWork => (pending is not null && prepared is null && failure is null ? new Task[] { pending } : [])
        .Concat(pendingParts.Values.Select(work => work.Task)).Concat(retiring.Select(work => work.Task))
        .Concat(vehicles.PendingWork).Concat(settlement.PendingWork).ToArray();
    internal bool Failed => failure is not null;

    internal NodeScene(SceneWorldSnapshot initial, NodeId node, Cache cache, Func<string, SceneObjectId> objectId, SceneVehicles vehicles,
        Func<SceneWorldSnapshot, NodeId, Built?, CancellationToken, Built>? generate = null,
        SceneVehicles.Cache? buildingCache = null, SceneVehicles.Cache? parkedCache = null,
        Func<Preparation, GridColumn, Part?, CancellationToken, Part>? buildChunk = null,
        Func<Preparation, RailSegment, Part?, CancellationToken, Part>? buildRail = null)
    {
        Node = node; this.cache = cache; this.objectId = objectId; owner = cache.Open(node);
        this.vehicles = vehicles;
        this.buildChunk = buildChunk ?? ((data, from, old, token) => data.BuildChunk(from, old, token));
        this.buildRail = buildRail ?? ((data, segment, old, token) => segment.Build(old, token));
        snapshot = initial;
        settlement = new(objectId, buildingCache: buildingCache, parkedCache: parkedCache);
        openingTracks = initial.Core.World.Edges.Where(edge => edge.Start == node || edge.End == node).ToImmutableDictionary(edge => edge.Id, edge => edge.Tracks);
        try { layout = NodeLayout.Build(initial, node); }
        catch (RailwayBuildException error) { Fail(error); return; }
        var panBounds = layout.PanBounds(default);
        range = new(new((panBounds.Minimum.X + panBounds.Maximum.X) / 2, 0,
            (panBounds.Minimum.Y + panBounds.Maximum.Y) / 2), NodeStreaming.PrefetchCells);
        var previous = cache.Read(node);
        pending = Task.Run(() =>
        {
            return generate is null ? Prepare(initial, layout, previous, cancellation.Token) :
                new Preparation(initial, generate(initial, node, previous, cancellation.Token), null, [], [], [], [], "", [], false);
        }, cancellation.Token);
    }
    internal void Pump(SceneWorldSnapshot? current = null, ScenePoint? focus = null)
    {
        if (current is not null) snapshot = current;
        if (focus is { } point) range = new(point, NodeStreaming.PrefetchCells);
        if (cancellation.IsCancellationRequested || failure is not null) return;
        if (layout is not null) settlement.Pump(snapshot, layout, range);
        if (prepared is null && pending?.IsCompleted == true)
        {
            try
            {
                prepared = pending.GetAwaiter().GetResult();
                built = prepared.Global;
                foreach (var part in built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal)) Attach(part.Key, part.Value);
                if (cache.Read(Node) is { } previous)
                {
                    foreach (var segment in prepared.Rails.Values.Where(segment => range.Around(NodeStreaming.RetainCells).Intersects(segment.Bounds)))
                        if (previous.Parts.TryGetValue(segment.Key, out var old) && old.Inputs == segment.Inputs)
                        { rails.Add(segment.Key, old); Attach(segment.Key, old); }
                    if (prepared.HasChunks)
                    foreach (var from in range.Around(NodeStreaming.RetainCells).Chunks(layout!))
                        if (previous.Parts.TryGetValue(ChunkKey(from), out var old))
                        {
                            // Validate the value key before retaining geometry from another binding.
                            if (old.Inputs == prepared.ChunkInputs(from)) { chunks.Add(from, old); Attach(ChunkKey(from), old); }
                        }
                }
            }
            catch (RailwayBuildException error) { Fail(error); return; }
        }
        if (prepared is null) return;
        ReconcileParts();
        cache.Publish(Node, owner, built! with
        { Parts = prepared.Global.Parts.SetItems(chunks.Select(item => new KeyValuePair<string, Part>(ChunkKey(item.Key), item.Value))).SetItems(rails) });
    }

    private void Attach(string key, Part part) => attached[key] = WorldSceneSession.Attach(part.Geometry, "node:" + key + ":", objectId);
    internal static string ChunkKey(GridColumn from) => "terrain:" + from.X + ":" + from.Z;

    private void ReconcileParts()
    {
        var retention = range.Around(NodeStreaming.RetainCells);
        foreach (var key in pendingParts.Keys.Where(key => !range.Intersects(pendingParts[key].Bounds)).ToArray())
        {
            var work = pendingParts[key];
            work.Cancellation.Cancel(); retiring.Add(work); pendingParts.Remove(key);
        }
        foreach (var from in chunks.Keys.Where(from => !retention.Intersects(from)).ToArray())
        { chunks.Remove(from); attached.Remove(ChunkKey(from)); }
        foreach (var key in rails.Keys.Where(key => !retention.Intersects(prepared!.Rails[key].Bounds)).ToArray())
        { rails.Remove(key); attached.Remove(key); }
        foreach (var key in pendingParts.Where(pair => pair.Value.Task.IsCompleted).Select(pair => pair.Key).ToArray())
        {
            var work = pendingParts[key];
            pendingParts.Remove(key);
            try
            {
                var part = work.Task.GetAwaiter().GetResult();
                if (work.Chunk is { } from) chunks[from] = part;
                else rails[key] = part;
                Attach(key, part);
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
        var terrain = prepared!.HasChunks ? range.Chunks(layout!).Where(from => !chunks.ContainsKey(from) && !pendingParts.ContainsKey(ChunkKey(from)))
            .Select(from => (Key: ChunkKey(from), Bounds: new SceneBounds(new(from.X, -128, from.Z),
                new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize)), Chunk: (GridColumn?)from)) : [];
        var tracks = prepared.Rails.Values.Where(segment => !rails.ContainsKey(segment.Key) && !pendingParts.ContainsKey(segment.Key) && range.Intersects(segment.Bounds))
            .Select(segment => (segment.Key, segment.Bounds, Chunk: (GridColumn?)null));
        foreach (var item in terrain.Concat(tracks).OrderBy(item => range.DistanceSquared(item.Bounds)).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            if (pendingParts.Count + retiring.Count >= RailwayCoordinates.MaxConcurrentBuilds) break;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            var data = prepared;
            pendingParts.Add(item.Key, new(Task.Run(() => item.Chunk is { } from ? buildChunk(data, from, null, stop.Token) :
                buildRail(data, data.Rails[item.Key], null, stop.Token), stop.Token), stop, item.Bounds, item.Chunk));
        }
    }
    private void Fail(RailwayBuildException error) => failure = new("build_failed", SceneStage.Build, new WorldObjectId.Node(Node), null, null, null, error.Message);

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneFrameRequest request, SceneFrameStamp stamp,
        SceneFrame? displayed, ScenePoint origin, WorldSceneTarget? target = null)
    {
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var fog = ImmutableArray.CreateBuilder<SceneFog>();
        var trainFog = ImmutableArray.CreateBuilder<SceneFog>();
        if (layout is not null) RenderTrains(snapshot, origin, objects, picking, problems, trainFog);
        ScenePoint? focus = null;
        if (target is WorldSceneTarget.Follow follow && objects.FirstOrDefault(item => item.Parent is null &&
            item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(follow.Vehicle))) is { } vehicle)
            focus = RailwayCoordinates.Add(origin, vehicle.Pose.Position);
        else if (target is not WorldSceneTarget.Follow && request.Camera is { } camera &&
            camera.Frame.Update.Frame.Session == stamp.Session && camera.Frame.Update.Frame.Request == stamp.Request &&
            camera.Frame.Update.Frame.Space == stamp.Space)
            focus = RailwayCoordinates.Add(camera.Frame.Origin, camera.Focus);
        Pump(snapshot, focus);
        var shift = new ScenePose(RailwayCoordinates.Relative(default, origin), Quaternion.Identity);
        foreach (var part in attached.OrderBy(item => item.Key, StringComparer.Ordinal)) Add(SceneGeometry.Place(part.Value.Geometry, shift));
        var terrainFog = ImmutableArray.CreateBuilder<SceneFog>();
        if (layout is not null && prepared?.HasChunks != false) TerrainFog(origin, terrainFog);
        fog.AddRange(terrainFog);
        if (prepared is not null)
            foreach (var segment in prepared.Rails.Values.Where(segment => !rails.ContainsKey(segment.Key) &&
                (range.Intersects(segment.Bounds) || NodeStreaming.OverlapsTerrain(segment.Bounds, chunks.Keys))))
                fog.Add(new(new(segment.Bounds.Minimum - RailwayCoordinates.Relative(origin, default),
                    segment.Bounds.Maximum - RailwayCoordinates.Relative(origin, default))));
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
            Add(settlement.Present(snapshot, layout, origin, fog, range, chunks.Keys));
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
        if (!nearCompleted) nearCompleted = Complete(NodeStreaming.NearCells);
        var readiness = failure is not null ? SceneReadiness.Failed : !nearCompleted ? SceneReadiness.Loading :
            Complete(NodeStreaming.ViewCells) ? SceneReadiness.Ready : SceneReadiness.Partial;
        return new(stamp, origin, snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds, readiness,
            objects.ToImmutable(), picking.ToImmutable(), fog.ToImmutable(), RailwayTerrain.UniqueProblems(problems), displayed,
            node: layout, nodeParts: built is null ? [] : built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value)
                .Concat(chunks.OrderBy(item => item.Key.Z).ThenBy(item => item.Key.X).Select(item => item.Value))
                .Concat(rails.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value)).ToImmutableArray(),
            debugLayers: request.DebugLayers, nodeTracks: openingTracks, pickRay: request.PickRay);

        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    private void TerrainFog(ScenePoint origin, ImmutableArray<SceneFog>.Builder fog)
    {
        var minimum = layout!.Bounds.Minimum;
        var maximum = layout.Bounds.Maximum;
        if (prepared is null)
        { Add(minimum.X, maximum.X, minimum.Y, maximum.Y); return; }
        var (left, right, back, front) = range.Around(NodeStreaming.RetainCells).Window(layout);
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

    internal static Preparation Prepare(SceneWorldSnapshot snapshot, NodeLayout layout, Built? previous = null,
        CancellationToken token = default)
    {
        var node = layout.Node;
        var map = snapshot.Core.World.Nodes.Single(item => item.Id == node);
        var terrainLookup = snapshot.Content.FindTerrain(map.Terrain.TypeId);
        var terrain = (terrainLookup as ContentLookup<TerrainAppearanceProjection>.Found)?.Value;
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
            var boundary = previous?.Layout.Inputs == layout.Inputs && previous.Boundaries.TryGetValue(entry.Edge, out var oldBoundary) &&
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
        var parkingNumber = 0;
        foreach (var line in layout.Parking)
            AddRoute("parking-tracks:" + parkingNumber++, layout.Inputs,
                arc => new(RailwayCoordinates.Add(line.Start, line.Forward * (float)arc), line.Forward,
                    Vector3.Cross(Vector3.UnitY, line.Forward)), line.Length);
        GridColumn Chunk(GridColumn at) => new((int)Math.Floor((double)at.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor((double)at.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        var bedChunks = bed.GroupBy(item => Chunk(item.Key)).ToDictionary(group => group.Key, group => group.ToDictionary());
        var boundaryChunks = boundaryColumns.GroupBy(Chunk).ToDictionary(group => group.Key, group => group.ToHashSet());
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
        return new(snapshot, new(layout, parts.ToImmutable(), boundaries.ToImmutable()), terrain,
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
        TerrainAppearanceProjection? terrain, Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> appearances,
        Dictionary<GridColumn, RailBed> bed, HashSet<GridColumn> boundaryColumns, ImmutableArray<SceneVoxelProjection> model,
        GridColumn from, CancellationToken token)
    {
        var cellCount = terrain is null ? 0 :
            (VoxelGrid.ChunkSize * VoxelGrid.ChunkSize - boundaryColumns.Count) * RailwayCoordinates.TerrainDepthCells;
        var cells = ImmutableArray.CreateBuilder<SceneCell>(cellCount);
        var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
        var subject = new PickSubject.Terrain(new WorldLocation.Node(node.Id));
        var layers = RailwayTerrain.PrepareLayers(terrain, id => appearances.GetValueOrDefault(id), subject);
        for (var z = from.Z; z < from.Z + VoxelGrid.ChunkSize; z++)
        for (var x = from.X; x < from.X + VoxelGrid.ChunkSize; x++)
        {
            token.ThrowIfCancellationRequested();
            var column = new GridColumn(x, z);
            if (boundaryColumns.Contains(column)) continue;
            var (height, distance) = Column(column);
            heights.Add(column, height + 1);
            RailwayTerrain.FillColumn(cells, column, height, layers, layers?.Rock, distance, double.IsFinite(distance));
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
            foreach (var cell in cells.Where(cell => cell.MissingReference is not null))
                objects.Add(new(id("missing:" + cell.At), root, new(new(cell.At.X, cell.At.Y, cell.At.Z), Quaternion.Identity), null,
                    [new SceneVisual.Missing(cell.MissingReference!, true)], [], []));
            return new(objects.ToImmutable(),
                cells.IsEmpty ? [] : [new(root, pose, grid)], problems);
        }), heights, origin);

    private void RenderTrains(SceneWorldSnapshot snapshot, ScenePoint origin, ImmutableArray<SceneObject>.Builder objects,
        ImmutableArray<PickGrid>.Builder picking, ImmutableArray<SceneProblem>.Builder problems, ImmutableArray<SceneFog>.Builder fog)
    {
        var trains = snapshot.Core.World.Trains.Where(item => item.Position is TrainPosition.Node position && position.Id == Node)
            .Select(item => snapshot.Core.FindTrain(item.Id)).OfType<Lookup<TrainView>.Found>()
            .Select(item => item.Value).OrderBy(train => train.Id.Value).ToArray();
        var queues = new Dictionary<EdgeId, double>();
        var platforms = SettlementObjects.Platforms(snapshot, layout!, problems);
        foreach (var train in trains)
        {
            var members = train.Vehicles.Select(id => ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(id))).Value).ToArray();
            var evaluations = members.Select(member => snapshot.Content.FindDesign(member.Design)).ToArray();
            var lengths = evaluations.Select(value => RailwayVehicles.Length((value as ContentLookup<DesignEvaluation>.Found)?.Value)).ToArray();
            var known = lengths.All(length => length is > 0);
            var total = known ? lengths.Sum(length => length!.Value) : 0;
            var station = ((TrainPosition.Node)train.Position).PlatformNumber is { } number ? number - 1 : -1;
            var entry = (train.Position as TrainPosition.Node)?.EnteredFrom is { } entered ? layout!.Entries.FirstOrDefault(item => item.Edge == entered) :
                layout!.Entries.FirstOrDefault(item => openingTracks.GetValueOrDefault(item.Edge) > 0);
            var positive = train.OrderFacing == TrainOrderFacing.FirstEndLeading;
            Func<double, RailwaySample> sample;
            double center;
            if (station >= 0)
            {
                if (station >= platforms.Length)
                {
                    problems.Add(new("station_platform_mismatch", SceneStage.Reconcile, new WorldObjectId.Train(train.Id),
                        null, null, null, "The assigned platform has no matching station slot."));
                    continue;
                }
                var stop = platforms[station];
                sample = arc => new(RailwayCoordinates.Add(stop.Center, stop.Forward * (float)arc), stop.Forward,
                    Vector3.Cross(Vector3.UnitY, stop.Forward));
                positive = (Vector3.Dot(entry?.Outward ?? stop.Forward, stop.Forward) < 0) == positive;
                center = 0;
            }
            else if (entry is not null)
            {
                var tracks = openingTracks.GetValueOrDefault(entry.Edge);
                var offset = tracks > 0 ? RailwayCoordinates.TrackOffset(1, tracks) : 0;
                sample = arc => layout!.QueueSample(entry, -arc, offset);
                var queued = queues.GetValueOrDefault(entry.Edge);
                center = -queued - total / 2.0 - NodeLayout.QueueGap;
                queues[entry.Edge] = queued + total + NodeLayout.QueueGap;
            }
            else
            {
                sample = arc => new(new(0, 0, arc), Vector3.UnitZ, Vector3.UnitX);
                var queued = queues.GetValueOrDefault(default);
                center = -queued - total / 2.0;
                queues[default] = queued + total + NodeLayout.QueueGap;
            }
            var available = station >= 0 || entry is null || openingTracks.GetValueOrDefault(entry.Edge) > 0;
            var changed = !available || entry is not null &&
                snapshot.Core.World.Edges.FirstOrDefault(edge => edge.Id == entry.Edge)?.Tracks != openingTracks.GetValueOrDefault(entry.Edge);
            if (changed) problems.Add(new("railway_update_pending", SceneStage.Reconcile, new WorldObjectId.Train(train.Id),
                null, null, null, "Node track counts use the opening snapshot; updates are implemented by Issue #345."));
            var consumed = 0.0;
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i];
                var arc = center + (positive ? 1 : -1) * (known ? total / 2.0 - consumed - lengths[i]!.Value / 2.0 : 0);
                consumed += lengths[i] ?? 0;
                var pose = RailwayVehicles.Pose(sample(arc), origin, positive);
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
        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        foreach (var work in pendingParts.Values) work.Cancellation.Cancel();
        var ownedWork = (pending is null ? Array.Empty<Task>() : [pending])
            .Concat(pendingParts.Values.Select(item => item.Task)).Concat(retiring.Select(item => item.Task));
        try { await Task.WhenAll(ownedWork).ConfigureAwait(false); }
        catch (Exception) { /* Retired scene work cannot be attached or published. */ }
        foreach (var work in pendingParts.Values.Concat(retiring)) work.Cancellation.Dispose();
        await settlement.DisposeAsync();
        cancellation.Dispose();
    }
}

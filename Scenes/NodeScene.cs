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
    private readonly ImmutableDictionary<EdgeId, int> openingTracks;
    private readonly Dictionary<string, BuiltGeometry> attached = [];
    private readonly Task<Built>? pending;
    private readonly NodeLayout? layout;
    private Built? built;
    private SceneProblem? failure;
    internal NodeId Node { get; }
    internal NodeLayout? Layout => layout;
    internal Task? BuildWork => pending;
    internal Task[] PendingWork => (pending is not null && built is null && failure is null ? new Task[] { pending } : []).Concat(vehicles.PendingWork).ToArray();
    internal bool Failed => failure is not null;

    internal NodeScene(SceneWorldSnapshot initial, NodeId node, Cache cache, Func<string, SceneObjectId> objectId, SceneVehicles vehicles,
        Func<SceneWorldSnapshot, NodeId, Built?, CancellationToken, Built>? generate = null)
    {
        Node = node; this.cache = cache; this.objectId = objectId; owner = cache.Open(node);
        this.vehicles = vehicles;
        openingTracks = initial.Core.World.Edges.Where(edge => edge.Start == node || edge.End == node).ToImmutableDictionary(edge => edge.Id, edge => edge.Tracks);
        try { layout = NodeLayout.Build(initial, node); }
        catch (RailwayBuildException error) { Fail(error); return; }
        var previous = cache.Read(node);
        pending = Task.Run(() => generate is null ? Build(initial, layout, previous, cancellation.Token) :
            generate(initial, node, previous, cancellation.Token), cancellation.Token);
    }
    internal void Pump()
    {
        if (cancellation.IsCancellationRequested || built is not null || failure is not null || pending?.IsCompleted != true) return;
        try
        {
            built = pending.GetAwaiter().GetResult();
            foreach (var part in built.Parts.OrderBy(item => item.Key, StringComparer.Ordinal)) attached[part.Key] = WorldSceneSession.Attach(part.Value.Geometry, "node:" + part.Key + ":", objectId);
            cache.Publish(Node, owner, built);
        }
        catch (RailwayBuildException error) { Fail(error); }
    }
    private void Fail(RailwayBuildException error) => failure = new("build_failed", SceneStage.Build, new WorldObjectId.Node(Node), null, null, null, error.Message);

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneFrameRequest request, SceneFrameStamp stamp,
        SceneFrame? displayed, ScenePoint origin)
    {
        Pump();
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var fog = ImmutableArray.CreateBuilder<SceneFog>();
        var shift = new ScenePose(RailwayCoordinates.Relative(default, origin), Quaternion.Identity);
        foreach (var part in attached.OrderBy(item => item.Key, StringComparer.Ordinal)) Add(SceneGeometry.Place(part.Value.Geometry, shift));
        if (built is null && failure is null)
        {
            var size = layout!.HalfSize;
            fog.Add(new(new SceneBounds(new((float)(-size - origin.X), (float)(-128 - origin.Y), (float)(-size - origin.Z)),
                new((float)(size - origin.X), (float)(128 - origin.Y), (float)(size - origin.Z)))));
        }
        if (layout is not null) RenderTrains(snapshot, origin, objects, picking, problems, fog);
        if (failure is not null) problems.Add(failure);
        return new(stamp, origin, snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds,
            failure is not null ? SceneReadiness.Failed : built is null ? SceneReadiness.Loading : fog.Count > 0 ? SceneReadiness.Partial : SceneReadiness.Ready,
            objects.ToImmutable(), picking.ToImmutable(), fog.ToImmutable(), RailwayTerrain.UniqueProblems(problems), displayed,
            node: layout, nodeParts: built?.Parts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).ToImmutableArray() ?? [],
            debugLayers: request.DebugLayers, nodeTracks: openingTracks);

        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    internal static Built Build(SceneWorldSnapshot snapshot, NodeLayout layout, Built? previous = null,
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
        var concrete = layout.Platforms > 0 ? Concrete(snapshot.Content) : null;
        var parts = ImmutableDictionary.CreateBuilder<string, Part>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        var bed = new Dictionary<GridColumn, double>();
        var boundaryColumns = new HashSet<GridColumn>();
        var boundaries = ImmutableDictionary.CreateBuilder<EdgeId, RailwayTerrain.BoundaryData>();
        var boundaryOwners = new Dictionary<GridColumn, (EdgeId Edge, double Distance)>();
        var terrainInputs = RailwayDependencies.Values(new object?[] { snapshot.Core.World.WorldSeed, map.Position, map.Terrain.TypeId,
            map.Terrain.Relief, TerrainInputs(snapshot.Content, map.Terrain.TypeId) });
        foreach (var entry in layout.Entries)
        {
            var path = new RailwayPath(snapshot, entry.Edge, token);
            var edge = path.Edge;
            var tracks = edge.Tracks;
            var entryKey = RailwayDependencies.Values(new object[] { layout.Inputs, entry.Edge, tracks });
            var needsMesh = previous is null || !previous.Parts.TryGetValue("entry:" + entry.Edge.Value, out var oldEntry) || oldEntry.Inputs != entryKey;
            var meshes = ImmutableArray.CreateBuilder<SceneMesh>();
            foreach (var route in layout.EntryRoutes(entry, tracks))
                AddRoute(route.Sample, route.Length, meshes, needsMesh);
            Put("entry:" + entry.Edge.Value, entryKey, () => MeshPart(meshes.ToImmutable()));
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
                boundary.Cells.Where(cell => allowed.Contains(new(cell.At.X, cell.At.Z))).ToImmutableArray(),
                boundary.Heights.Where(item => allowed.Contains(item.Key)).ToImmutableDictionary(), [], boundary.Origin));
            foreach (var column in allowed) boundaryColumns.Add(Global(column));
        }
        var stationMeshes = ImmutableArray.CreateBuilder<SceneMesh>();
        var anyTracks = snapshot.Core.World.Edges.Any(edge => (edge.Start == node || edge.End == node) && edge.Tracks > 0);
        var stationKey = RailwayDependencies.Values(new object[] { layout.Inputs, anyTracks });
        var needsStationMesh = previous is null || !previous.Parts.TryGetValue("station-tracks", out var oldStation) || oldStation.Inputs != stationKey;
        foreach (var route in layout.StationRoutes(anyTracks))
            AddRoute(route.Sample, route.Length, stationMeshes, needsStationMesh);
        Put("station-tracks", stationKey, () => MeshPart(stationMeshes.ToImmutable()));
        GridColumn Chunk(GridColumn at) => new((int)Math.Floor((double)at.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            (int)Math.Floor((double)at.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        var bedChunks = bed.GroupBy(item => Chunk(item.Key)).ToDictionary(group => group.Key, group => group.ToDictionary());
        var boundaryChunks = boundaryColumns.GroupBy(Chunk).ToDictionary(group => group.Key, group => group.ToHashSet());
        var size = checked((int)layout.HalfSize);
        for (var z = -size; z < size; z += VoxelGrid.ChunkSize)
        for (var x = -size; x < size; x += VoxelGrid.ChunkSize)
        {
            token.ThrowIfCancellationRequested();
            var from = new GridColumn(x, z);
            var chunkBed = bedChunks.GetValueOrDefault(from) ?? [];
            var chunkBoundary = boundaryChunks.GetValueOrDefault(from) ?? [];
            var chunkInputs = RailwayDependencies.Values(new object[] { terrainInputs,
                chunkBed.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z).ToArray(),
                chunkBoundary.OrderBy(item => item.X).ThenBy(item => item.Z).ToArray() });
            Put("terrain:" + x + ":" + z, chunkInputs, () => TerrainPart(snapshot, map, layout, terrain, appearances, chunkBed, chunkBoundary, from, token));
        }
        for (var station = 0; station < layout.Platforms; station++)
        {
            var index = station;
            var inputs = RailwayDependencies.Values(new object?[] { layout.Platforms, layout.StationForward, concrete });
            Put("platform:" + index, inputs, () => PlatformPart(layout, index, concrete));
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
        return new(layout, parts.ToImmutable(), boundaries.ToImmutable());

        void Put(string name, string inputs, Func<Part> create)
        {
            token.ThrowIfCancellationRequested();
            parts.Add(name, previous?.Parts.TryGetValue(name, out var old) == true && old.Inputs == inputs ? old : create() with { Inputs = inputs });
        }
        void AddRoute(Func<double, RailwaySample> sample, double length, ImmutableArray<SceneMesh>.Builder meshes, bool needsMesh)
        {
            if (needsMesh) meshes.AddRange(RailwayMeshes.Build(sample, length, default));
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
                    if (!bed.TryGetValue(column, out var old) || distance < old) bed[column] = distance;
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

    internal static VoxelAppearanceProjection? Concrete(IContentView content)
    {
        foreach (var item in content.Voxels.OrderBy(item => item.Definition.Value, StringComparer.Ordinal))
            if (content.FindVoxelAppearance(item.Definition) is ContentLookup<VoxelAppearanceProjection>.Found
                { Value.Presentation: VoxelPresentation.Cube { Material: MaterialCategory.Concrete } cube } found &&
                (cube.Shapes.IsEmpty || cube.Shapes.Any(shape => shape.Name == "cube"))) return found.Value;
        return null;
    }

    private static Part MeshPart(ImmutableArray<SceneMesh> meshes) => new("", WorldSceneSession.BuildGeometry(id =>
        new([new(id(""), null, new(Vector3.Zero, Quaternion.Identity), null,
            meshes.Select(mesh => (SceneVisual)new SceneVisual.Geometry(mesh, new(false, 0, 0, false))).ToImmutableArray(), [], [])], [], [])),
        ImmutableDictionary<GridColumn, double>.Empty);

    internal static Part PlatformPart(NodeLayout layout, int station, VoxelAppearanceProjection? concrete)
    {
        if (concrete is null)
            return new("", WorldSceneSession.BuildGeometry(_ => new([], [], [new("content_missing", SceneStage.Build,
                new WorldObjectId.Node(layout.Node), null, null, null, "No usable concrete cube voxel is available for node platforms.")])),
                ImmutableDictionary<GridColumn, double>.Empty);
        var left = (int)NodeLayout.StationX(station, layout.Platforms) + NodeLayout.PlatformClearance;
        var cells = ImmutableArray.CreateBuilder<SceneCell>();
        for (var z = -NodeLayout.PlatformLength / 2; z < NodeLayout.PlatformLength / 2; z++)
        for (var x = left; x < left + NodeLayout.PlatformWidth; x++)
        for (var y = -1; y <= 0; y++)
        {
            var yellow = x == left || station + 1 < layout.Platforms && x == left + NodeLayout.PlatformWidth - 1;
            var at = layout.StationSample(x + .5, z + .5).Position;
            cells.Add(new(new((int)Math.Floor(at.X), y, (int)Math.Floor(at.Z)), VoxelOrientation.PxPy, concrete,
                ((VoxelPresentation.Cube)concrete.Presentation).Shapes.IsEmpty ? null : new("cube"),
                yellow ? new("#d9a62b") : null, new PickSubject.Terrain(new WorldLocation.Node(layout.Node)), []));
        }
        return GridPart(cells.ToImmutable(), ImmutableDictionary<GridColumn, double>.Empty, []);
    }

    private static Part TerrainPart(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        TerrainAppearanceProjection? terrain, Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> appearances,
        Dictionary<GridColumn, double> bed, HashSet<GridColumn> boundaryColumns, GridColumn from, CancellationToken token)
    {
        var cells = ImmutableArray.CreateBuilder<SceneCell>();
        var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
        for (var z = from.Z; z < from.Z + VoxelGrid.ChunkSize; z++)
        for (var x = from.X; x < from.X + VoxelGrid.ChunkSize; x++)
        {
            token.ThrowIfCancellationRequested();
            var column = new GridColumn(x, z);
            if (boundaryColumns.Contains(column)) continue;
            var (height, distance) = Column(column);
            heights.Add(column, height + 1);
            RailwayTerrain.FillColumn(cells, column, height, terrain, terrain, distance, double.IsFinite(distance),
                id => appearances.GetValueOrDefault(id), new PickSubject.Terrain(new WorldLocation.Node(node.Id)));
        }
        return GridPart(cells.ToImmutable(), heights.ToImmutable(), []);

        (int Height, double Distance) Column(GridColumn column)
        {
            var raw = RailwayTerrain.NodeGround(snapshot, node, new(column.X + 0.5, 0, column.Z + 0.5));
            var distance = bed.GetValueOrDefault(column, double.PositiveInfinity);
            if (double.IsFinite(distance))
            {
                var blend = Math.Clamp((distance - RailwayCoordinates.BedBaseHalfWidth) / RailwayCoordinates.BedBlendWidth, 0, 1);
                raw = RailwayTerrain.BedHeight(0, distance) * (1 - blend) + raw * blend;
            }
            var point = new Vector3(column.X + .5f, 0, column.Z + .5f);
            var u = Vector3.Dot(point, Vector3.Cross(Vector3.UnitY, layout.StationForward));
            var v = Vector3.Dot(point, layout.StationForward);
            if (layout.Platforms > 0 && Math.Abs(v) < NodeLayout.PlatformLength / 2.0)
                for (var p = 0; p < layout.Platforms; p++)
                    if (u >= NodeLayout.StationX(p, layout.Platforms) + NodeLayout.PlatformClearance &&
                        u < NodeLayout.StationX(p, layout.Platforms) + NodeLayout.PlatformClearance + NodeLayout.PlatformWidth) raw = RailwayTerrain.BedHeight(0, 0);
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
        var map = snapshot.Core.World.Nodes.Single(item => item.Id == Node);
        var trains = snapshot.Core.World.Trains.Where(item => item.Position is TrainPosition.Node position && position.Id == Node)
            .Select(item => snapshot.Core.FindTrain(item.Id)).OfType<Lookup<TrainView>.Found>()
            .Select(item => item.Value).OrderBy(train => train.Id.Value).ToArray();
        var serving = (map.Occupant is NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile }
            ? trains.Where(train => train.Activity == TrainActivity.Servicing).Take(layout!.Platforms) : []).Select(train => train.Id).ToArray();
        var queues = new Dictionary<EdgeId, double>();
        foreach (var train in trains)
        {
            var members = train.Vehicles.Select(id => ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(id))).Value).ToArray();
            var evaluations = members.Select(member => snapshot.Content.FindDesign(member.Design)).ToArray();
            var lengths = evaluations.Select(value => RailwayVehicles.Length((value as ContentLookup<DesignEvaluation>.Found)?.Value)).ToArray();
            var known = lengths.All(length => length is > 0);
            var total = known ? lengths.Sum(length => length!.Value) : 0;
            var station = Array.IndexOf(serving, train.Id);
            var entry = train.NodeTransit?.EnteredFrom is { } entered ? layout!.Entries.FirstOrDefault(item => item.Edge == entered) :
                layout!.Entries.FirstOrDefault(item => openingTracks.GetValueOrDefault(item.Edge) > 0);
            var positive = train.OrderFacing == TrainOrderFacing.FirstEndLeading;
            Func<double, RailwaySample> sample;
            double center;
            if (station >= 0)
            {
                var u = NodeLayout.StationX(station, layout!.Platforms);
                sample = arc => layout.StationSample(u, arc);
                positive = (Vector3.Dot(entry?.Outward ?? layout.StationForward, layout.StationForward) < 0) == positive;
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
            var changed = !available || map.Platforms != layout!.Platforms || entry is not null &&
                snapshot.Core.World.Edges.FirstOrDefault(edge => edge.Id == entry.Edge)?.Tracks != openingTracks.GetValueOrDefault(entry.Edge);
            if (changed) problems.Add(new("railway_update_pending", SceneStage.Reconcile, new WorldObjectId.Train(train.Id),
                null, null, null, "Node tracks and platform counts use the opening snapshot; updates are implemented by Issue #345."));
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
        try { if (pending is not null) await pending.ConfigureAwait(false); }
        catch (Exception) { /* Retired scene work cannot be attached or published. */ }
        cancellation.Dispose();
    }
}

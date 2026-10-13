using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeScene : IAsyncDisposable
{
    internal sealed record Part(string Inputs, BuiltGeometry Geometry, ImmutableDictionary<GridColumn, double> Heights, ScenePoint Origin = default);
    internal sealed record Built(NodeLayout Layout, ImmutableDictionary<string, Part> Parts);
    internal sealed record Vehicle(string Inputs, BuiltObject Body);
    internal sealed class Cache
    {
        private readonly object gate = new();
        private ulong next;
        private readonly Dictionary<NodeId, (ulong Owner, Built? Built, ImmutableDictionary<InstanceId, Vehicle> Vehicles)> nodes = [];
        internal ulong Open(NodeId node)
        {
            lock (gate)
            {
                var old = nodes.GetValueOrDefault(node);
                nodes[node] = (++next, old.Built, old.Vehicles ?? ImmutableDictionary<InstanceId, Vehicle>.Empty);
                while (nodes.Count > 2) nodes.Remove(nodes.MinBy(item => item.Value.Owner).Key);
                return next;
            }
        }
        internal Built? Read(NodeId node) { lock (gate) return nodes.GetValueOrDefault(node).Built; }
        internal void Publish(NodeId node, ulong owner, Built built, ImmutableDictionary<InstanceId, Vehicle> vehicles)
        {
            lock (gate) if (nodes.TryGetValue(node, out var old) && old.Owner == owner) nodes[node] = (owner, built, vehicles);
        }
        internal Vehicle? ReadVehicle(NodeId node, InstanceId id)
        { lock (gate) return nodes.GetValueOrDefault(node).Vehicles?.GetValueOrDefault(id); }
        internal void Retain(NodeId? current, NodeId? candidate)
        { lock (gate) foreach (var id in nodes.Keys.Where(id => id != current && id != candidate).ToArray()) nodes.Remove(id); }
    }

    private readonly Cache cache;
    private readonly ulong owner;
    private readonly Func<string, SceneObjectId> objectId;
    private readonly Func<SceneWorldSnapshot, NodeId, Built?, CancellationToken, Action<NodeLayout>?, Built> generate;
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<Task> tasks = [];
    private readonly Dictionary<InstanceId, Vehicle> vehicles = [];
    private readonly Dictionary<InstanceId, (string Inputs, Task<BuiltObject> Task)> pendingVehicles = [];
    private readonly Dictionary<string, BuiltGeometry> attached = [];
    private readonly object layoutGate = new();
    private Task<Built> pending;
    private volatile NodeLayout? layout;
    private Built? built;
    private string requested;
    private CancellationTokenSource buildCancellation;
    internal NodeId Node { get; }
    internal NodeLayout? Layout => layout;
    internal Task BuildWork => pending;
    internal Task[] PendingWork => tasks.Where(task => !task.IsCompleted).ToArray();

    internal NodeScene(SceneWorldSnapshot initial, NodeId node, Cache cache, Func<string, SceneObjectId> objectId,
        Func<SceneWorldSnapshot, NodeId, Built?, CancellationToken, Action<NodeLayout>?, Built>? generate = null)
    {
        Node = node; this.cache = cache; this.objectId = objectId; owner = cache.Open(node);
        this.generate = generate ?? Build;
        requested = BuildInputs(initial, node);
        buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        pending = Start(initial, cache.Read(node));
    }
    private Task<Built> Start(SceneWorldSnapshot snapshot, Built? previous)
    {
        var token = buildCancellation.Token;
        var inputs = requested;
        var work = Task.Run(() => generate(snapshot, Node, previous, token, value =>
        {
            lock (layoutGate) if (!token.IsCancellationRequested && requested == inputs) layout = value;
        }), token);
        tasks.Add(work); return work;
    }

    internal SceneFrame Present(SceneWorldSnapshot snapshot, SceneFrameRequest request, SceneFrameStamp stamp,
        SceneFrame? displayed, ScenePoint origin)
    {
        var key = BuildInputs(snapshot, Node);
        if (key != requested)
        {
            buildCancellation.Cancel(); buildCancellation.Dispose();
            buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            lock (layoutGate) { requested = key; layout = null; }
            attached.Clear();
            pending = Start(snapshot, built); built = null;
        }
        if (pending.IsCompleted && built is null)
        {
            built = pending.GetAwaiter().GetResult(); layout = built.Layout;
            foreach (var part in built.Parts) attached[part.Key] = WorldSceneSession.Attach(part.Value.Geometry, "node:" + part.Key + ":", objectId);
        }
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var fog = ImmutableArray.CreateBuilder<SceneFog>();
        var shift = new ScenePose(RailwayCoordinates.Relative(default, origin), Quaternion.Identity);
        foreach (var part in attached.Values) Add(SceneGeometry.Place(part.Geometry, shift));
        if (built is null)
        {
            var size = layout?.HalfSize ?? float.MaxValue / 2;
            fog.Add(new(new SceneBounds(new((float)(-size - origin.X), -128, (float)(-size - origin.Z)),
                new((float)(size - origin.X), 128, (float)(size - origin.Z)))));
        }
        else RenderTrains(snapshot, origin, objects, picking, problems, fog);
        tasks.RemoveAll(task => task.IsCompleted && !ReferenceEquals(task, pending) && !pendingVehicles.Values.Any(value => value.Task == task));
        if (built is not null) cache.Publish(Node, owner, built, vehicles.ToImmutableDictionary());
        return new(stamp, origin, snapshot.Core.Position.CompletedSteps * SimulationTime.StepSeconds,
            built is null ? SceneReadiness.Loading : fog.Count > 0 ? SceneReadiness.Partial : SceneReadiness.Ready,
            objects.ToImmutable(), picking.ToImmutable(), fog.ToImmutable(), problems.ToImmutable(), displayed,
            node: layout, nodeParts: built?.Parts.Values.ToImmutableArray() ?? []);

        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    private static string BuildInputs(SceneWorldSnapshot snapshot, NodeId node) => RailwayDependencies.Values(new object[]
    {
        NodeLayout.InputKey(snapshot, node), snapshot.Core.World.Edges.Where(edge => edge.Start == node || edge.End == node)
            .OrderBy(edge => edge.Id.Value).Select(edge => new object[] { edge.Id, edge.Tracks }).ToArray()
    });

    internal static Built Build(SceneWorldSnapshot snapshot, NodeId node, Built? previous = null,
        CancellationToken token = default, Action<NodeLayout>? readyLayout = null)
    {
        var layout = previous?.Layout.Inputs == NodeLayout.InputKey(snapshot, node) ? previous.Layout : NodeLayout.Build(snapshot, node, token);
        readyLayout?.Invoke(layout);
        var map = snapshot.Core.World.Nodes.Single(item => item.Id == node);
        var terrainLookup = snapshot.Content.FindTerrain(map.Terrain.TypeId);
        var terrain = (terrainLookup as ContentLookup<TerrainAppearanceProjection>.Found)?.Value;
        var appearances = new Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?>();
        if (terrain is not null)
            foreach (var id in new[] { terrain.SurfaceVoxel, terrain.SubsurfaceVoxel, terrain.RockVoxel }.Distinct())
                appearances[id] = (snapshot.Content.FindVoxelAppearance(id) as ContentLookup<VoxelAppearanceProjection>.Found)?.Value;
        var concrete = layout.Platforms > 0 ? Concrete(snapshot.Content) : null;
        var parts = ImmutableDictionary.CreateBuilder<string, Part>();
        var bed = new Dictionary<GridColumn, double>();
        var railInputs = RailwayDependencies.Values(new object[] { layout.Inputs,
            snapshot.Core.World.Edges.Where(edge => edge.Start == node || edge.End == node).OrderBy(edge => edge.Id.Value).Select(edge => new object[] { edge.Id, edge.Tracks }).ToArray() });
        var terrainInputs = RailwayDependencies.Values(new object?[] { railInputs, snapshot.Core.World.WorldSeed, map.Position, map.Terrain.TypeId,
            map.Terrain.Relief, TerrainInputs(snapshot.Content, map.Terrain.TypeId) });
        var needsTerrain = previous is null || previous.Layout.Inputs != layout.Inputs ||
            previous.Parts.Where(item => item.Key.StartsWith("terrain:", StringComparison.Ordinal)).Any(item => item.Value.Inputs != terrainInputs);
        foreach (var entry in layout.Entries)
        {
            var tracks = snapshot.Core.World.Edges.Single(edge => edge.Id == entry.Edge).Tracks;
            var entryKey = RailwayDependencies.Values(new object[] { layout.Inputs, entry.Edge, tracks });
            var needsMesh = previous is null || !previous.Parts.TryGetValue("entry:" + entry.Edge.Value, out var oldEntry) || oldEntry.Inputs != entryKey;
            var meshes = ImmutableArray.CreateBuilder<SceneMesh>();
            for (var number = 1; number <= tracks; number++)
            {
                var offset = RailwayCoordinates.TrackOffset(number, tracks);
                var start = RailwayCoordinates.Add(entry.Point, Vector3.Cross(Vector3.UnitY, -entry.Outward) * (float)offset);
                AddRoute(arc => new(RailwayCoordinates.Add(start, -entry.Outward * (float)arc), -entry.Outward,
                    Vector3.Cross(Vector3.UnitY, -entry.Outward)), NodeLayout.ApproachStraight, meshes, needsMesh);
                for (var station = 0; station < Math.Max(1, layout.Platforms); station++)
                {
                    var curve = layout.Connection(entry.Edge, offset, station);
                    AddRoute(curve.Sample, curve.Length, meshes, needsMesh);
                }
            }
            Put("entry:" + entry.Edge.Value, entryKey, () => MeshPart(meshes.ToImmutable()));
            var edge = snapshot.Core.World.Edges.Single(value => value.Id == entry.Edge);
            var boundaryInputs = RailwayDependencies.Values(new object[] { layout.Inputs,
                NodeLayout.InputKey(snapshot, edge.Start), NodeLayout.InputKey(snapshot, edge.End),
                edge.Id, edge.Start, edge.End, edge.LengthMeters, edge.Terrain, edge.Tracks, edge.TrackLimit, snapshot.Core.World.WorldSeed,
                snapshot.Content.FindRailwayAppearance(snapshot.Core.World.RailwayRule),
                TerrainInputs(snapshot.Content, edge.Terrain.TypeId), TerrainInputs(snapshot.Content, map.Terrain.TypeId) });
            Put("boundary:" + entry.Edge.Value, boundaryInputs, () =>
            {
                var boundary = RailwayTerrain.NodeBoundary(snapshot, new RailwayPath(snapshot, entry.Edge, token), node, entry, token);
                return GridPart(boundary.Cells, boundary.Heights, boundary.Problems, boundary.Origin);
            });
        }
        var stationMeshes = ImmutableArray.CreateBuilder<SceneMesh>();
        var anyTracks = snapshot.Core.World.Edges.Any(edge => (edge.Start == node || edge.End == node) && edge.Tracks > 0);
        var stationKey = RailwayDependencies.Values(new object[] { layout.Inputs, anyTracks });
        var needsStationMesh = previous is null || !previous.Parts.TryGetValue("station-tracks", out var oldStation) || oldStation.Inputs != stationKey;
        for (var station = 0; station < Math.Max(1, layout.Platforms); station++)
        {
            var x = layout.Platforms > 0 ? NodeLayout.StationX(station, layout.Platforms) : 0;
            var halfLength = layout.Platforms > 0 ? NodeLayout.PlatformLength / 2 + NodeLayout.PlatformClearance : NodeLayout.PlatformClearance;
            if (layout.Platforms > 0 || anyTracks)
                AddRoute(arc => new(new(x, 0, -halfLength + arc), Vector3.UnitZ, Vector3.UnitX), halfLength * 2, stationMeshes, needsStationMesh);
        }
        Put("station-tracks", stationKey, () => MeshPart(stationMeshes.ToImmutable()));
        var size = checked((int)layout.HalfSize);
        for (var z = -size; z < size; z += VoxelGrid.ChunkSize)
        for (var x = -size; x < size; x += VoxelGrid.ChunkSize)
        {
            token.ThrowIfCancellationRequested();
            var from = new GridColumn(x, z);
            Put("terrain:" + x + ":" + z, terrainInputs, () => TerrainPart(snapshot, map, layout, terrain, appearances, bed, from, token));
        }
        for (var station = 0; station < layout.Platforms; station++)
        {
            var index = station;
            var inputs = RailwayDependencies.Values(new object?[] { layout.Platforms, concrete });
            Put("platform:" + index, inputs, () => PlatformPart(layout, index, concrete));
        }
        return new(layout, parts.ToImmutable());

        void Put(string name, string inputs, Func<Part> create)
        {
            token.ThrowIfCancellationRequested();
            parts.Add(name, previous?.Parts.TryGetValue(name, out var old) == true && old.Inputs == inputs ? old : create() with { Inputs = inputs });
        }
        void AddRoute(Func<double, RailwaySample> sample, double length, ImmutableArray<SceneMesh>.Builder meshes, bool needsMesh)
        {
            if (needsMesh) meshes.AddRange(RailwayMeshes.Build(sample, length, default));
            if (!needsTerrain) return;
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
            cells.Add(new(new(x, y, z), VoxelOrientation.PxPy, concrete,
                ((VoxelPresentation.Cube)concrete.Presentation).Shapes.IsEmpty ? null : new("cube"),
                yellow ? new("#d9a62b") : null, new PickSubject.Terrain(new WorldLocation.Node(layout.Node)), []));
        }
        return GridPart(cells.ToImmutable(), ImmutableDictionary<GridColumn, double>.Empty, []);
    }

    private static Part TerrainPart(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        TerrainAppearanceProjection? terrain, Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> appearances,
        Dictionary<GridColumn, double> bed, GridColumn from, CancellationToken token)
    {
        var cells = ImmutableArray.CreateBuilder<SceneCell>();
        var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        if (terrain is null) problems.Add(new("content_missing", SceneStage.Build, new WorldObjectId.Node(node.Id),
            new ContentReference.Core(new CoreDefinitionReference.Terrain(node.Terrain.TypeId)), null, null, "Node terrain is missing."));
        for (var z = from.Z; z < from.Z + VoxelGrid.ChunkSize; z++)
        for (var x = from.X; x < from.X + VoxelGrid.ChunkSize; x++)
        {
            token.ThrowIfCancellationRequested();
            var column = new GridColumn(x, z);
            if (x * (double)x + z * (double)z > layout.HalfSize * layout.HalfSize || layout.Entries.Any(entry =>
                BoundaryContains(new(x + .5, 0, z + .5), entry, snapshot.Core.World.Edges.Single(edge => edge.Id == entry.Edge).Tracks))) continue;
            var (height, distance) = Column(column);
            heights.Add(column, height + 1);
            if (terrain is null) continue;
            for (var depth = 0; depth < RailwayCoordinates.TerrainDepthCells; depth++)
            {
                var id = distance <= RailwayCoordinates.BedBaseHalfWidth && depth < 2 ? terrain.RockVoxel :
                    depth == 0 ? terrain.SurfaceVoxel : depth < 3 ? terrain.SubsurfaceVoxel : terrain.RockVoxel;
                var appearance = appearances[id];
                var reference = appearance is null ? new ContentReference.Core(new CoreDefinitionReference.Voxel(id)) : null;
                cells.Add(new(new(x, height - depth, z), VoxelOrientation.PxPy, appearance, null, null,
                    new PickSubject.Terrain(new WorldLocation.Node(node.Id)), [], reference));
            }
        }
        foreach (var missing in appearances.Where(item => item.Value is null))
            problems.Add(new("content_missing", SceneStage.Build, new WorldObjectId.Node(node.Id),
                new ContentReference.Core(new CoreDefinitionReference.Voxel(missing.Key)), null, null, "Node voxel appearance is missing."));
        return GridPart(cells.ToImmutable(), heights.ToImmutable(), problems.ToImmutable());

        (int Height, double Distance) Column(GridColumn column)
        {
            var raw = RailwayTerrain.NodeGround(snapshot, node, new(column.X + 0.5, 0, column.Z + 0.5));
            var distance = bed.GetValueOrDefault(column, double.PositiveInfinity);
            if (double.IsFinite(distance))
            {
                var blend = Math.Clamp((distance - RailwayCoordinates.BedBaseHalfWidth) / RailwayCoordinates.BedBlendWidth, 0, 1);
                raw = (distance <= RailwayCoordinates.BedTopHalfWidth ? -2 : -3) * (1 - blend) + raw * blend;
            }
            if (layout.Platforms > 0 && Math.Abs(column.Z + 0.5) < NodeLayout.PlatformLength / 2.0)
                for (var p = 0; p < layout.Platforms; p++)
                    if (column.X >= NodeLayout.StationX(p, layout.Platforms) + NodeLayout.PlatformClearance &&
                        column.X < NodeLayout.StationX(p, layout.Platforms) + NodeLayout.PlatformClearance + NodeLayout.PlatformWidth) raw = -2;
            return ((int)Math.Floor(raw), distance);
        }
    }

    private static bool BoundaryContains(ScenePoint point, NodeLayout.Entry entry, int tracks)
    {
        var relative = RailwayCoordinates.Relative(point, entry.Point);
        var inward = Vector3.Dot(relative, -entry.Outward);
        var lateral = Vector3.Dot(relative, Vector3.Cross(Vector3.UnitY, -entry.Outward));
        return inward >= -1 && inward <= NodeLayout.GroundMargin && Math.Abs(lateral) <= RailwayCoordinates.TerrainHalfWidth(tracks);
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

    private static string VehicleInputs(IContentView content, InstanceView instance)
    {
        var lookup = content.FindDesign(instance.Design);
        if (lookup is not ContentLookup<DesignEvaluation>.Found found || found.Value is DesignEvaluation.Failed)
            return RailwayDependencies.Values(new object[] { instance.Design, lookup });
        var scene = DesignGeometry.Projection(found.Value);
        return RailwayDependencies.Values(new object[] { instance.Design, ((InstanceDetails.Vehicle)instance.Details).Size,
            WorldSceneSession.Destroyed(instance).OrderBy(part => part.Value).ToArray(), scene,
            found.Value is DesignEvaluation.Complete complete ? complete.Diagnostics : ((DesignEvaluation.Partial)found.Value).Diagnostics,
            scene.Voxels.Select(voxel => voxel.Voxel).Distinct().OrderBy(id => id.Value, StringComparer.Ordinal)
                .Select(id => content.FindVoxelAppearance(id)).ToArray() });
    }

    private void RenderTrains(SceneWorldSnapshot snapshot, ScenePoint origin, ImmutableArray<SceneObject>.Builder objects,
        ImmutableArray<PickGrid>.Builder picking, ImmutableArray<SceneProblem>.Builder problems, ImmutableArray<SceneFog>.Builder fog)
    {
        var map = snapshot.Core.World.Nodes.Single(item => item.Id == Node);
        var trains = snapshot.Core.World.Trains.Where(item => item.Position is TrainPosition.Node position && position.Id == Node)
            .Select(item => ((Lookup<TrainView>.Found)snapshot.Core.FindTrain(item.Id)).Value).OrderBy(train => train.Id.Value).ToArray();
        var serving = (map.Occupant is NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile }
            ? trains.Where(train => train.Activity == TrainActivity.Servicing).Take(layout!.Platforms) : []).Select(train => train.Id).ToArray();
        var queues = new Dictionary<EdgeId, double>();
        var live = new HashSet<InstanceId>();
        foreach (var train in trains)
        {
            var members = train.Vehicles.Select(id => ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Vehicle(id))).Value).ToArray();
            var evaluations = members.Select(member => snapshot.Content.FindDesign(member.Design)).ToArray();
            var lengths = evaluations.Select(value => RailwayVehicles.Length((value as ContentLookup<DesignEvaluation>.Found)?.Value)).ToArray();
            var known = lengths.All(length => length is > 0);
            var total = known ? lengths.Sum(length => length!.Value) : 0;
            var station = Array.IndexOf(serving, train.Id);
            var entry = train.NodeTransit?.EnteredFrom is { } entered ? layout!.Entries.FirstOrDefault(item => item.Edge == entered) :
                layout!.Entries.FirstOrDefault(item => snapshot.Core.World.Edges.Single(edge => edge.Id == item.Edge).Tracks > 0);
            var positive = train.OrderFacing == TrainOrderFacing.FirstEndLeading;
            Func<double, RailwaySample> sample;
            double center;
            if (station >= 0)
            {
                var x = NodeLayout.StationX(station, layout!.Platforms);
                sample = arc => new(new(x, 0, arc), Vector3.UnitZ, Vector3.UnitX);
                positive = ((entry?.Outward.Z ?? 1) < 0) == positive;
                center = 0;
            }
            else if (entry is not null)
            {
                var tracks = snapshot.Core.World.Edges.Single(edge => edge.Id == entry.Edge).Tracks;
                var route = layout!.Connection(entry.Edge, tracks > 0 ? RailwayCoordinates.TrackOffset(1, tracks) : 0, 0);
                sample = route.Sample;
                var queued = queues.GetValueOrDefault(entry.Edge);
                center = route.Length - queued - total / 2.0 - NodeLayout.QueueGap;
                queues[entry.Edge] = queued + total + NodeLayout.QueueGap;
            }
            else
            {
                sample = arc => new(new(0, 0, arc), Vector3.UnitZ, Vector3.UnitX);
                var queued = queues.GetValueOrDefault(default);
                center = -queued - total / 2.0;
                queues[default] = queued + total + NodeLayout.QueueGap;
            }
            var consumed = 0.0;
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i]; live.Add(member.Id);
                var arc = center + (positive ? 1 : -1) * (known ? total / 2.0 - consumed - lengths[i]!.Value / 2.0 : 0);
                consumed += lengths[i] ?? 0;
                var pose = RailwayVehicles.Pose(sample(arc), origin, positive);
                var subject = new WorldObjectId.Instance(member.Id);
                if (!known)
                {
                    if (evaluations[i] is not ContentLookup<DesignEvaluation>.Found evaluation || evaluation.Value is DesignEvaluation.Failed)
                        Add(WorldSceneSession.Attach(WorldSceneSession.BuildObject(snapshot.Content, member, pose).Geometry,
                            "vehicle:" + member.Id + ":", objectId).Geometry);
                    else
                    {
                        objects.Add(new(objectId("vehicle:" + member.Id + ":unplaced"), null, pose, subject, [], [], []));
                        if (lengths[i] is null) problems.Add(new("vehicle_length_unknown", SceneStage.Reconcile, subject,
                            new ContentReference.Design(member.Design), null, null, "A node consist has no known member lengths."));
                    }
                    continue;
                }
                var inputs = VehicleInputs(snapshot.Content, member);
                if (pendingVehicles.TryGetValue(member.Id, out var work) && work.Task.IsCompleted)
                {
                    pendingVehicles.Remove(member.Id);
                    var body = work.Task.GetAwaiter().GetResult();
                    if (work.Inputs == inputs) vehicles[member.Id] = new(inputs, body);
                }
                if (!vehicles.TryGetValue(member.Id, out var cached) || cached.Inputs != inputs)
                {
                    var reusable = cache.ReadVehicle(Node, member.Id);
                    if (reusable?.Inputs == inputs) vehicles[member.Id] = cached = reusable;
                    else if (!pendingVehicles.ContainsKey(member.Id))
                    {
                        var content = snapshot.Content;
                        var task = Task.Run(() => RailwayVehicles.Build(content, member), cancellation.Token);
                        pendingVehicles[member.Id] = (inputs, task); tasks.Add(task);
                    }
                }
                if (cached?.Inputs == inputs)
                {
                    var body = WorldSceneSession.Attach(cached.Body.Geometry, "vehicle:" + member.Id + ":", objectId);
                    Add(RailwayVehicles.Place(body.Geometry, pose, (positive ? 1 : -1) * arc));
                }
                else
                {
                    objects.Add(new(objectId("vehicle:" + member.Id + ":loading"), null, pose, subject, [], [], []));
                    var projection = DesignGeometry.Projection(((ContentLookup<DesignEvaluation>.Found)evaluations[i]).Value);
                    fog.Add(new(RailwayScene.VehicleBounds(projection, (InstanceDetails.Vehicle)member.Details), Pose: pose));
                }
            }
        }
        foreach (var id in vehicles.Keys.Concat(pendingVehicles.Keys).Where(id => !live.Contains(id)).Distinct().ToArray())
        { vehicles.Remove(id); pendingVehicles.Remove(id); }
        void Add(RenderedDesign geometry)
        { objects.AddRange(geometry.Objects); picking.AddRange(geometry.Picking); problems.AddRange(geometry.Diagnostics); }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel(); buildCancellation.Cancel();
        try { await Task.WhenAll(tasks); }
        catch (Exception) { /* Retired scene work cannot be attached or published. */ }
        buildCancellation.Dispose(); cancellation.Dispose();
    }
}

using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayTerrain
{
    internal sealed record BoundaryData(RailwayDependencies Dependencies,
        VoxelGrid Grid, ImmutableDictionary<GridColumn, double> Heights,
        ScenePoint Origin, ImmutableArray<SceneProblem> Problems);
    private const double BedTopBelowRailCells = 1;
    private const double TunnelPortalLookbackCells = 2;
    private const ulong EndpointMaterialPurpose = 9;
    private const ulong DecorationDensityPurpose = 10;
    private const ulong DecorationSelectionPurpose = 11;
    private const int BridgeSpanCells = 16;
    private const int BridgeJointMargin = 4;

    internal const int FlattenBlendRadiusCells = 8;

    internal static long ColumnKey(double x, double z) =>
        unchecked((long)x * 73856093 ^ (long)z * 19349663);

    internal static TerrainAppearanceProjection? EndpointTerrain(RailwayPath path, double arc, long columnKey,
        Func<TerrainTypeId, TerrainAppearanceProjection?> terrain)
    {
        var blend = Math.Min(RailwayCoordinates.NodeTransitionCells, path.LengthCells / 2);
        var random = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, columnKey, EndpointMaterialPurpose);
        if (arc < blend && random >= arc / blend) return terrain(path.Start.Terrain.TypeId);
        if (path.LengthCells - arc < blend && random >= (path.LengthCells - arc) / blend)
            return terrain(path.End.Terrain.TypeId);
        return terrain(path.Edge.Terrain.TypeId);
    }

    internal static int? DecorationSlot(RailwayPath path, TerrainAppearanceProjection? terrain, long columnKey)
    {
        if (terrain is null || terrain.Decorations.IsDefaultOrEmpty ||
            RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, columnKey, DecorationDensityPurpose) >=
            Math.Clamp(terrain.DecorationTargetPerSquareMeter / (RailwayCoordinates.CellsPerMeter * RailwayCoordinates.CellsPerMeter), 0, 1)) return null;
        var total = terrain.Decorations.Sum(item => Math.Max(0, item.Weight));
        if (total == 0) return null;
        var selection = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, columnKey, DecorationSelectionPurpose) * total;
        var slot = 0;
        while (slot < terrain.Decorations.Length - 1 && selection >= Math.Max(0, terrain.Decorations[slot].Weight))
            selection -= Math.Max(0, terrain.Decorations[slot++].Weight);
        return slot;
    }

    internal static double BedHeight(double railHeight, double distance) =>
        railHeight - BedTopBelowRailCells - (distance <= RailwayCoordinates.BedTopHalfWidth ? 1 : 2);

    internal sealed record Layers(CellProperties? Surface, CellProperties? Subsurface, CellProperties? Rock);

    internal static Layers? PrepareLayers(TerrainAppearanceProjection? terrain,
        Func<VoxelDefinitionId, VoxelAppearanceProjection?> appearance, PickSubject subject)
    {
        if (terrain is null) return null;
        var surface = PrepareVoxel(terrain.SurfaceVoxel, appearance, subject);
        var subsurface = terrain.SubsurfaceVoxel == terrain.SurfaceVoxel ? surface : PrepareVoxel(terrain.SubsurfaceVoxel, appearance, subject);
        var rock = terrain.RockVoxel == terrain.SurfaceVoxel ? surface : terrain.RockVoxel == terrain.SubsurfaceVoxel ? subsurface :
            PrepareVoxel(terrain.RockVoxel, appearance, subject);
        return new(surface, subsurface, rock);
    }

    internal static CellProperties PrepareVoxel(VoxelDefinitionId voxel,
        Func<VoxelDefinitionId, VoxelAppearanceProjection?> appearance, PickSubject subject)
    {
        var projection = appearance(voxel);
        return new(VoxelOrientation.PxPy, projection, null, null, subject, [], projection is null ?
            new ContentReference.Core(new CoreDefinitionReference.Voxel(voxel)) : null);
    }

    private static (Layers? Terrain, CellProperties? Ballast) PrepareColumn(TerrainAppearanceProjection? terrain,
        VoxelDefinitionId ballastVoxel, double height, double distance, bool hasBed,
        (double From, double To)? excludedY, Func<VoxelDefinitionId, CellProperties> property, double? lowestNeighbor = null)
    {
        // Prepare only emitted column layers: Appearance() records voxelReads and reports missing diagnostics.
        CellProperties? surface = null, subsurface = null, rock = null, ballast = null;
        var bottom = ColumnBottom(height, lowestNeighbor);
        for (var depth = 0; ; depth++)
        {
            var y = height - depth;
            if (y < bottom) break;
            if (excludedY is { } excluded && y >= excluded.From && y < excluded.To) continue;
            var bed = hasBed && BedLayer(depth, distance);
            if (bed)
            {
                ballast ??= Property(ballastVoxel);
            }
            else if (terrain is not null)
            {
                if (depth == 0) surface ??= Property(terrain.SurfaceVoxel);
                else if (depth < 3) subsurface ??= Property(terrain.SubsurfaceVoxel);
                else rock ??= Property(terrain.RockVoxel);
            }
        }
        return (terrain is null ? null : new(surface, subsurface, rock), ballast);

        CellProperties Property(VoxelDefinitionId id)
        {
            if (surface is not null && terrain!.SurfaceVoxel == id) return surface;
            if (subsurface is not null && terrain!.SubsurfaceVoxel == id) return subsurface;
            if (rock is not null && terrain!.RockVoxel == id) return rock;
            if (ballast is not null && ballastVoxel == id) return ballast;
            return property(id);
        }
    }

    internal static void FillColumn(ImmutableArray<SceneCell>.Builder cells, GridColumn column, double height,
        Layers? terrain, CellProperties? ballast, double trackDistance, bool hasBed,
        double originY = 0, (double From, double To)? excludedY = null, double? lowestNeighbor = null)
    {
        var bottom = ColumnBottom(height, lowestNeighbor);
        for (var depth = 0; ; depth++)
        {
            var y = height - depth;
            if (y < bottom) break;
            if (excludedY is { } excluded && y >= excluded.From && y < excluded.To) continue;
            var bed = hasBed && BedLayer(depth, trackDistance);
            var properties = bed ? ballast : depth == 0 ? terrain?.Surface : depth < 3 ? terrain?.Subsurface : terrain?.Rock;
            if (properties is not null)
                cells.Add(new(new(column.X, checked((int)(y - originY)), column.Z), properties));
        }
    }

    private static double ColumnBottom(double height, double? lowestNeighbor) =>
        Math.Min(height - (RailwayCoordinates.TerrainDepthCells - 1), (lowestNeighbor ?? height) + 1);

    private static bool BedLayer(int depth, double distance) => depth == 0 && distance <= RailwayCoordinates.BedBaseHalfWidth ||
        depth == 1 && distance <= RailwayCoordinates.BedTopHalfWidth;

    internal static void ReportMissing(ContentReference reference, ImmutableArray<ContentDiagnostic> sourceDiagnostics,
        string detail, ImmutableArray<SceneProblem>.Builder diagnostics, WorldObjectId? subject = null)
    {
        if (diagnostics.Any(problem => problem.Code == "content_missing" && problem.Content == reference)) return;
        var source = ImmutableArray.CreateBuilder<SceneProblem>();
        DesignGeometry.AddDiagnostics(sourceDiagnostics, source, subject);
        foreach (var problem in source)
            if (!diagnostics.Contains(problem)) diagnostics.Add(problem);
        var package = sourceDiagnostics.FirstOrDefault(problem => problem.Subject == reference && problem.Source is not null)?.Source?.Package
            ?? sourceDiagnostics.FirstOrDefault(problem => problem.Source is not null)?.Source?.Package;
        diagnostics.Add(new("content_missing", SceneStage.Build, subject, reference, package, null, detail));
    }

    internal static ImmutableArray<SceneProblem> UniqueProblems(IEnumerable<SceneProblem> problems) =>
        problems.DistinctBy(problem => problem.Code == "content_missing" && problem.Content is ContentReference.Core reference
            ? (object)reference : problem).ToImmutableArray();

    private sealed record TerrainColumn(double Arc, double Offset, double Height, TerrainAppearanceProjection? Terrain, bool Water, bool Tunnel);

    internal static RailwaySegment Build(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken cancellation) =>
    new SegmentBuilder(snapshot, path, index, cancellation).Build();

    internal sealed record FlattenPlacement(FlattenRegion Region, ScenePose Pose, ScenePoint Origin);

    internal static ImmutableArray<FlattenPlacement> HeightFlattens(SceneWorldSnapshot snapshot,
        RailwayPath path, int index, CancellationToken cancellation = default) =>
        new SegmentBuilder(snapshot, path, index, cancellation).HeightFlattens();

    internal static BoundaryData NodeBoundary(SceneWorldSnapshot snapshot, RailwayPath path, NodeId node, NodeLayout.Entry entry, CancellationToken token) =>
        new SegmentBuilder(snapshot, path, node == path.Start.Id ? 0 : path.SegmentCount - 1, token).Boundary(node, entry);

    internal static Dictionary<GridColumn, T> SampleColumns<T>(RailwayPath path, int index, ScenePoint origin,
        Func<ScenePoint, GridColumn, double, double, T> create, CancellationToken cancellation = default)
    {
        var columns = new Dictionary<GridColumn, T>();
        var begin = index * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
        var halfWidth = RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks);
        for (var arc = begin; arc <= end; arc = Math.Min(end, arc + 0.5))
        {
            cancellation.ThrowIfCancellationRequested();
            var sample = path.Sample(arc);
            for (var offset = -halfWidth; offset <= halfWidth; offset += 0.5)
            {
                var point = path.Sample(arc, offset).Position;
                var wx = Math.Floor(point.X); var wz = Math.Floor(point.Z);
                var column = new GridColumn(checked((int)(wx - origin.X)), checked((int)(wz - origin.Z)));
                if (columns.ContainsKey(column)) continue;
                var center = new ScenePoint(wx + 0.5, sample.Position.Y, wz + 0.5);
                var located = path.Locate(center);
                if (located < begin || located >= end && index != path.SegmentCount - 1) continue;
                var nearest = path.Sample(located);
                var lateral = (center.X - nearest.Position.X) * nearest.Right.X + (center.Z - nearest.Position.Z) * nearest.Right.Z;
                if (Math.Abs(lateral) > halfWidth) continue;
                columns.Add(column, create(center, column, located, lateral));
            }
            if (arc == end) break;
        }
        return columns;
    }

    private sealed class SegmentBuilder
    {
        private readonly SceneWorldSnapshot snapshot;
        private readonly RailwayPath path;
        private readonly int index;
        private readonly CancellationToken cancellation;
        private ImmutableArray<FlattenPlacement>.Builder? heightFlattens;
        private readonly double begin;
        private readonly double end;
        private readonly double endpointBlend;
        private readonly TerrainSurface.Railway surface;
        private readonly (double Center, double First, double Last, double Level)[] waterRegions;
        private readonly ScenePoint origin;
        private readonly ImmutableArray<SceneObject>.Builder objects;
        private readonly ImmutableArray<PickGrid>.Builder picking;
        private readonly ImmutableArray<SceneProblem>.Builder diagnostics;
        private readonly HashSet<TerrainTypeId> terrainReads;
        private readonly HashSet<RailwayModelRead> modelReads;
        private readonly HashSet<DesignHash> models;
        private readonly HashSet<VoxelDefinitionId> voxelReads;
        private readonly Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> appearances;
        private readonly Dictionary<TerrainTypeId, TerrainAppearanceProjection?> terrains = [];
        private readonly double halfWidth;
        private ulong nextId;
        private readonly bool readsStartTerrain;
        private readonly bool readsEndTerrain;
        private readonly SceneObjectId terrainId;

        internal SegmentBuilder(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken cancellation)
        {
            this.snapshot = snapshot;
            this.path = path;
            this.index = index;
            this.cancellation = cancellation;
            if (index < 0 || index >= path.SegmentCount) throw new ArgumentOutOfRangeException(nameof(index));
            begin = index * RailwayCoordinates.SegmentLengthCells;
            end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
            endpointBlend = Math.Min(RailwayCoordinates.NodeTransitionCells, path.LengthCells / 2);
            surface = new(path, id => terrains[id]);
            waterRegions = surface.WaterRegions;
            var anchor = path.Sample(begin).Position;
            origin = new ScenePoint(Math.Floor(anchor.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
                Math.Floor(anchor.Y / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize, Math.Floor(anchor.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
            objects = ImmutableArray.CreateBuilder<SceneObject>();
            picking = ImmutableArray.CreateBuilder<PickGrid>();
            diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
            terrainReads = new HashSet<TerrainTypeId>();
            modelReads = new HashSet<RailwayModelRead>();
            models = new HashSet<DesignHash>();
            voxelReads = new HashSet<VoxelDefinitionId>();
            appearances = new Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?>();
            halfWidth = RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks);
            nextId = 1UL;
            // Inside a curve, adjacent columns differ in arc by about R/(R-lateral).
            // At the 160-cell minimum radius and a 3-track half-width of 83, that is about 2.1, so the margin is 4.
            const int neighborArc = 4;
            var startArc = path.Edge.Tracks > 0 ? Math.Max(TunnelPortalLookbackCells, neighborArc) : neighborArc;
            readsStartTerrain = Math.Max(0, begin - startArc) < endpointBlend;
            readsEndTerrain = path.LengthCells - end - neighborArc < endpointBlend;
            terrainId = new SceneObjectId(0);
            GetTerrain(path.Edge.Terrain.TypeId);
            if (readsStartTerrain) GetTerrain(path.Start.Terrain.TypeId);
            if (readsEndTerrain) GetTerrain(path.End.Terrain.TypeId);
        }

        internal RailwaySegment Build()
        {
            var (tunnels, portals) = SampleTunnels();
            var columns = SampleColumns();
            foreach (var portal in portals) PlacePortal(portal.Arc, portal.Exit, columns);
            PlaceModels(columns);
            return Render(columns, tunnels);
        }

        internal ImmutableArray<FlattenPlacement> HeightFlattens()
        {
            heightFlattens = ImmutableArray.CreateBuilder<FlattenPlacement>();
            var (_, portals) = SampleTunnels();
            var empty = new Dictionary<GridColumn, TerrainColumn>();
            foreach (var portal in portals) PlacePortal(portal.Arc, portal.Exit, empty);
            PlaceRepairs(empty);
            foreach (var arc in waterRegions.Select(region => region.Center))
                if (path.Edge.Tracks > 0) PlaceBridge(arc, empty);
            return heightFlattens.ToImmutable();
        }

        private (ImmutableArray<(double Start, double End)> Intervals, List<(double Arc, bool Exit)> Portals) SampleTunnels()
        {
            if (path.Edge.Tracks == 0) return ([], []);
            var tunnelIntervals = ImmutableArray.CreateBuilder<(double Start, double End)>();
            var portals = new List<(double Arc, bool Exit)>();
            double? tunnelStart = null;
            for (var arc = begin; arc <= end; arc = Math.Min(end, arc + 0.5))
            {
                cancellation.ThrowIfCancellationRequested();
                var tunnel = TunnelAt(arc);
                if (tunnel && tunnelStart is null) tunnelStart = arc;
                if (tunnelStart is not null && (!tunnel || arc == end))
                {
                    var finish = tunnel ? end : arc;
                    tunnelIntervals.Add((tunnelStart.Value, finish));
                    if (tunnelStart.Value > 0)
                    {
                        var previousArc = Math.Max(0, tunnelStart.Value - TunnelPortalLookbackCells);
                        if (!TunnelAt(previousArc)) portals.Add((tunnelStart.Value, false));
                    }
                    if (!tunnel && finish < path.LengthCells)
                        portals.Add((finish, true));
                    tunnelStart = null;
                }
                if (arc == end) break;
            }
            return (tunnelIntervals.ToImmutable(), portals);
        }

        private Dictionary<GridColumn, TerrainColumn> SampleColumns() =>
            RailwayTerrain.SampleColumns(path, index, origin, Column, cancellation);

        private TerrainColumn Column(ScenePoint center, GridColumn column, double arc, double lateral)
        {
            var terrain = terrains[path.Edge.Terrain.TypeId];
            var height = surface.ColumnHeight(arc, center, lateral, terrain, out var water, out var tunnel);
            terrain = EndpointTerrain(path, arc, ColumnKey(column), id => terrains[id]);
            return new(arc, lateral, Math.Floor(height), terrain, water, tunnel);
        }

        private double? LowestNeighbor(Dictionary<GridColumn, TerrainColumn> columns, Dictionary<GridColumn, double?> outside,
            GridColumn column, double floorX, double floorZ)
        {
            double? lowest = null;
            void Consider(int keyX, int keyZ, double x, double z)
            {
                var surface = NeighborSurface(columns, outside, new(keyX, keyZ), new(x + 0.5, 0, z + 0.5));
                if (surface is double height && (lowest is null || height < lowest)) lowest = height;
            }
            Consider(column.X + 1, column.Z, floorX + 1, floorZ);
            Consider(column.X - 1, column.Z, floorX - 1, floorZ);
            Consider(column.X, column.Z + 1, floorX, floorZ + 1);
            Consider(column.X, column.Z - 1, floorX, floorZ - 1);
            return lowest;
        }

        private double? NeighborSurface(Dictionary<GridColumn, TerrainColumn> columns, Dictionary<GridColumn, double?> outside,
            GridColumn key, ScenePoint center)
        {
            if (columns.TryGetValue(key, out var owned)) return owned.Height;
            if (outside.TryGetValue(key, out var cached)) return cached;
            var arc = path.Locate(center);
            var frame = path.Sample(arc);
            var lateral = (center.X - frame.Position.X) * frame.Right.X + (center.Z - frame.Position.Z) * frame.Right.Z;
            double? surface = null;
            if (Math.Abs(lateral) <= halfWidth)
            {
                var local = new GridColumn((int)Math.Floor(center.X - origin.X), (int)Math.Floor(center.Z - origin.Z));
                surface = Column(center, local, arc, lateral).Height;
            }
            outside[key] = surface;
            return surface;
        }

        internal BoundaryData Boundary(NodeId node, NodeLayout.Entry entry)
        {
            var isStart = node == path.Start.Id;
            var endpoint = path.Sample(isStart ? 0 : path.LengthCells).Position;
            var shift = new ScenePoint(Math.Round(entry.Point.X - endpoint.X), entry.Point.Y - endpoint.Y, Math.Round(entry.Point.Z - endpoint.Z));
            var radius = NodeLayout.GroundMargin + halfWidth + RailwayCoordinates.BoundaryGridMargin;
            var cells = ImmutableArray.CreateBuilder<SceneCell>();
            var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
            var owned = new Dictionary<GridColumn, TerrainColumn>();
            var subject = new PickSubject.Terrain(new WorldLocation.Node(node));
            var properties = new Dictionary<VoxelDefinitionId, CellProperties>();
            for (var z = (int)Math.Floor(endpoint.Z - radius); z <= endpoint.Z + radius; z++)
            for (var x = (int)Math.Floor(endpoint.X - radius); x <= endpoint.X + radius; x++)
            {
                cancellation.ThrowIfCancellationRequested();
                var center = new ScenePoint(x + .5, 0, z + .5);
                var relative = RailwayCoordinates.Relative(center, endpoint);
                var inward = Vector3.Dot(relative, -entry.Outward);
                var lateral = Vector3.Dot(relative, Vector3.Cross(Vector3.UnitY, -entry.Outward));
                if (inward < -RailwayCoordinates.CellFootprintMargin || inward > NodeLayout.GroundMargin || Math.Abs(lateral) > halfWidth) continue;
                var arc = path.Locate(center);
                var rail = path.Sample(arc);
                var lineLateral = (center.X - rail.Position.X) * rail.Right.X + (center.Z - rail.Position.Z) * rail.Right.Z;
                var value = Column(center, new((int)(x - origin.X), (int)(z - origin.Z)), arc, lineLateral);
                var key = new GridColumn(x, z);
                owned.Add(key, value);
                heights.Add(key, value.Height + 1);
            }
            var outside = new Dictionary<GridColumn, double?>();
            foreach (var (key, value) in owned)
            {
                cancellation.ThrowIfCancellationRequested();
                var distance = surface.TrackDistance(value.Offset);
                var hasBed = path.Edge.Tracks > 0 && !value.Water && !value.Tunnel;
                var lowest = LowestNeighbor(owned, outside, key, key.X, key.Z);
                var layers = PrepareColumn(value.Terrain, path.Rules.BallastVoxel, value.Height, distance, hasBed, null, Property, lowest);
                FillColumn(cells, key, value.Height, layers.Terrain, layers.Ballast, distance, hasBed, lowestNeighbor: lowest);
            }
            return new(RailwayDependencies.Capture(snapshot, path, index, terrainReads, modelReads, models, voxelReads,
                readsStartTerrain, readsEndTerrain), VoxelGrid.FromBuilder(cells), heights.ToImmutable(), shift, diagnostics.ToImmutable());

            CellProperties Property(VoxelDefinitionId id)
            {
                if (!properties.TryGetValue(id, out var value))
                    properties.Add(id, value = PrepareVoxel(id, Appearance, subject));
                return value;
            }
        }

        private void PlaceModels(Dictionary<GridColumn, TerrainColumn> columns)
        {
            PlaceRepairs(columns);
            foreach (var arc in waterRegions.Select(region => region.Center))
                if (path.Edge.Tracks > 0) PlaceBridge(arc, columns);

            var decorations = new List<(GridColumn Column, DesignHash Design)>();
            foreach (var pair in columns)
            {
                cancellation.ThrowIfCancellationRequested();
                var value = pair.Value;
                var terrain = value.Terrain;
                if (value.Water || surface.TrackDistance(value.Offset) < RailwayCoordinates.DecorationRootClearance) continue;
                if (DecorationSlot(path, terrain, ColumnKey(pair.Key)) is not { } slot) continue;
                modelReads.Add(new(terrain!.Id, slot, RailwayModelRole.Decoration));
                decorations.Add((pair.Key, terrain!.Decorations[slot].SceneModel));
            }
            foreach (var decoration in decorations)
            {
                var value = columns[decoration.Column];
                AddModel(decoration.Design, new(new(decoration.Column.X, (float)(value.Height + 1 - origin.Y), decoration.Column.Z), Quaternion.Identity), decoration.Column, columns);
            }

        }

        private RailwaySegment Render(Dictionary<GridColumn, TerrainColumn> columns, ImmutableArray<(double Start, double End)> tunnels)
        {
            var cells = ImmutableArray.CreateBuilder<SceneCell>();
            var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
            var outside = new Dictionary<GridColumn, double?>();
            var waterCount = columns.Values.Count(value => value.Water);
            var vertices = ImmutableArray.CreateBuilder<SceneVertex>(waterCount * 4);
            var indices = ImmutableArray.CreateBuilder<int>(waterCount * 6);

            foreach (var (column, value) in columns)
            {
                cancellation.ThrowIfCancellationRequested();
                var trackDistance = surface.TrackDistance(value.Offset);
                var isTunnel = value.Tunnel;
                heights.Add(column, value.Height + 1);
                var subject = new PickSubject.Terrain(new WorldLocation.Edge(path.Edge.Id, value.Arc / RailwayCoordinates.CellsPerMeter));
                var hasBed = path.Edge.Tracks > 0 && !value.Water && !isTunnel;
                (double From, double To)? excluded = null;
                if (isTunnel && Math.Abs(value.Offset) < RailwayCoordinates.TunnelShoulder(path.Edge.Tracks))
                {
                    var railY = path.Sample(value.Arc).Position.Y;
                    var surface = value.Height;
                    var upper = Math.Min(railY + RailwayCoordinates.TunnelCrownCells, surface);
                    if (upper > railY) excluded = (railY, upper);
                }
                var lowest = LowestNeighbor(columns, outside, column, column.X + origin.X, column.Z + origin.Z);
                var layers = PrepareColumn(value.Terrain, path.Rules.BallastVoxel, value.Height, trackDistance, hasBed, excluded,
                    id => PrepareVoxel(id, Appearance, subject), lowest);
                FillColumn(cells, column, value.Height, layers.Terrain, layers.Ballast, trackDistance, hasBed, origin.Y, excluded, lowest);
                if (value.Water)
                {
                    var y = (float)(surface.WaterLevel(value.Arc) - origin.Y);
                    var first = vertices.Count;
                    foreach (var corner in new Vector2[] { new(0, 0), new(0, 1), new(1, 1), new(1, 0) })
                        vertices.Add(new(new(column.X + corner.X, y, column.Z + corner.Y), Vector3.UnitY, corner, corner, ExposedFaceEdges.None, 0, 0));
                    indices.AddRange((ReadOnlySpan<int>)[first, first + 1, first + 2, first, first + 2, first + 3]);
                }
            }
            var grid = VoxelGrid.FromBuilder(cells);
            var visuals = DesignGeometry.Meshes(grid, false).ToBuilder();
            objects.AddRange(DesignGeometry.Models(grid, terrainId, null, _ => new(nextId++), false));
            foreach (var missingTerrain in terrains.Where(item => item.Value is null))
                visuals.Add(new SceneVisual.Missing(new ContentReference.Core(new CoreDefinitionReference.Terrain(missingTerrain.Key)), false));
            foreach (var missing in cells.Where(cell => cell.MissingReference is not null))
                objects.Add(new(new(nextId++), terrainId, SceneGeometry.Placement(missing.At, missing.Orientation), null,
                [new SceneVisual.Missing(missing.MissingReference!, true)], [], []));
            foreach (var mesh in RailwayMeshes.Build(path, index, origin))
                visuals.Add(new SceneVisual.Geometry(mesh, new(false, 0, 0, false)));
            if (waterCount > 0) visuals.Add(new SceneVisual.Geometry(new(new SceneMaterial.BuiltIn(BuiltInMaterial.Water),
            vertices.MoveToImmutable(), indices.MoveToImmutable()), new(false, 0, 0, false)));
            objects.Insert(0, new(terrainId, null, new(Vector3.Zero, Quaternion.Identity), null, visuals.ToImmutable(), [], []));
            picking.Add(new(terrainId, new(Vector3.Zero, Quaternion.Identity), grid));
            return new(index, origin, new(objects.ToImmutable(), picking.ToImmutable(), diagnostics.ToImmutable()), heights.ToImmutable(),
            RailwayDependencies.Capture(snapshot, path, index, terrainReads, modelReads, models, voxelReads,
            readsStartTerrain, readsEndTerrain), tunnels);

        }

        private long ColumnKey(GridColumn column) => RailwayTerrain.ColumnKey(column.X + origin.X, column.Z + origin.Z);
        private TerrainAppearanceProjection? GetTerrain(TerrainTypeId id)
        {
            terrainReads.Add(id);
            if (terrains.TryGetValue(id, out var cached)) return cached;
            var lookup = snapshot.Content.FindTerrain(id);
            var result = lookup is ContentLookup<TerrainAppearanceProjection>.Found found ? found.Value : null;
            if (result is null) ReportMissing(new ContentReference.Core(new CoreDefinitionReference.Terrain(id)),
                lookup is ContentLookup<TerrainAppearanceProjection>.Missing missing ? missing.Diagnostics : [], "Railway terrain is missing.");
            terrains.Add(id, result); return result;
        }
        private VoxelAppearanceProjection? Appearance(VoxelDefinitionId id)
        {
            voxelReads.Add(id);
            if (appearances.TryGetValue(id, out var cached)) return cached;
            var lookup = snapshot.Content.FindVoxelAppearance(id);
            var result = lookup is ContentLookup<VoxelAppearanceProjection>.Found found ? found.Value : null;
            if (result is null) ReportMissing(new ContentReference.Core(new CoreDefinitionReference.Voxel(id)),
                lookup is ContentLookup<VoxelAppearanceProjection>.Missing missing ? missing.Diagnostics : [], "Railway voxel appearance is missing.");
            appearances.Add(id, result); return result;
        }
        private void ReportMissing(ContentReference reference, ImmutableArray<ContentDiagnostic> sourceDiagnostics, string detail) =>
            RailwayTerrain.ReportMissing(reference, sourceDiagnostics, detail, diagnostics);

        private bool TunnelAt(double arc)
        {
            var terrain = terrains[path.Edge.Terrain.TypeId];
            return surface.TunnelAt(arc, terrain);
        }
        private bool Water(double arc) => surface.Water(arc);
        private void PlaceRepairs(Dictionary<GridColumn, TerrainColumn> columns)
        {
            if (path.Edge.Breaks.IsDefaultOrEmpty || path.Edge.Works.IsDefaultOrEmpty) return;
            foreach (var gap in path.Edge.Breaks.OrderBy(item => item.Id.Value).ThenBy(item => item.Track))
            {
                if (gap.Track < 1 || gap.Track > path.Edge.Tracks) continue;
                if (!path.Edge.Works.Any(work => RailwayCoordinates.IsBreakRepair(work) && work.RepairingBreak == gap.Id)) continue;
                var center = gap.DistanceMeters * RailwayCoordinates.CellsPerMeter;
                if (!OwnsArc(center)) continue;
                var terrain = GetTerrain(path.Edge.Terrain.TypeId);
                modelReads.Add(new(path.Edge.Terrain.TypeId, -1, RailwayModelRole.RepairSite));
                var model = terrain?.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
                if (model is null)
                {
                    ReportMissingModel(center);
                    continue;
                }
                var offset = RailwayCoordinates.TrackOffset(gap.Track, path.Edge.Tracks);
                var sample = path.Sample(center, offset);
                var forward = offset < 0 ? -sample.Forward : sample.Forward;
                AddModel(model.RepairSite, new(RailwayCoordinates.Relative(sample.Position, origin),
                    RailwayCoordinates.Rotation(forward)), null, columns);
            }
        }

        private void PlacePortal(double arc, bool exit, Dictionary<GridColumn, TerrainColumn> columns)
        {
            var terrain = GetTerrain(path.Edge.Terrain.TypeId);
            modelReads.Add(new(path.Edge.Terrain.TypeId, -1, RailwayModelRole.Tunnel));
            var model = terrain?.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
            if (model is null)
            {
                ReportMissingModel(arc);
                return;
            }
            var sample = path.Sample(arc);
            var forward = exit ? -sample.Forward : sample.Forward;
            AddModel(model.TunnelPortal,
                new(RailwayCoordinates.Relative(sample.Position, origin), RailwayCoordinates.Rotation(forward)), null, columns);
        }

        private void PlaceBridge(double waterCenter, Dictionary<GridColumn, TerrainColumn> columns)
        {
            var terrain = GetTerrain(path.Edge.Terrain.TypeId);
            var model = terrain?.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
            modelReads.Add(new(path.Edge.Terrain.TypeId, -1, RailwayModelRole.Bridge));
            if (model is null)
            {
                if (OwnsArc(waterCenter)) ReportMissingModel(waterCenter);
                return;
            }
            var lookup = snapshot.Content.FindDesign(model.Bridge);
            models.Add(model.Bridge);
            if (lookup is not ContentLookup<DesignEvaluation>.Found found || found.Value is not (DesignEvaluation.Complete or DesignEvaluation.Partial))
            {
                if (!OwnsArc(waterCenter)) return;
                var sample = path.Sample(waterCenter);
                AddModel(model.Bridge, new(RailwayCoordinates.Relative(sample.Position, origin), RailwayCoordinates.Rotation(sample.Forward)), null, columns);
                return;
            }
            var scene = DesignGeometry.Projection(found.Value);
            var (kMin, kMax) = SpanRange(scene);
            var owned = new List<int>();
            for (var k = kMin; k <= kMax; k++)
                if (OwnsArc(waterCenter + k * BridgeSpanCells)) owned.Add(k);
            if (owned.Count == 0) return;
            var members = RootSpanMembers(scene, kMin, kMax, owned, waterCenter);
            var wearAt = path.Sample(waterCenter).Position;
            foreach (var k in owned)
            {
                var sliced = SliceSpan(found.Value, k, members[k]);
                if (sliced is null) continue;
                var sample = path.Sample(waterCenter + k * BridgeSpanCells);
                AddPlaced(sliced, new(RailwayCoordinates.Relative(sample.Position, origin), RailwayCoordinates.Rotation(sample.Forward)),
                    null, columns, wearAt);
            }
        }

        private void ReportMissingModel(double arc)
        {
            var reference = new ContentReference.Core(new CoreDefinitionReference.Terrain(path.Edge.Terrain.TypeId));
            objects.Add(new(new(nextId++), null, new(RailwayCoordinates.Relative(path.Sample(arc).Position, origin), Quaternion.Identity),
                null, [new SceneVisual.Missing(reference, false)], [], []));
            ReportMissing(reference, [], "No railway scene model matches the track count.");
        }

        private bool OwnsArc(double arc)
        {
            var owner = Math.Clamp(arc, 0, path.LengthCells);
            if (owner >= path.LengthCells) return index == path.SegmentCount - 1;
            return owner >= begin && owner < end;
        }

        private static int SpanIndex(int z) => (int)Math.Floor((z + BridgeSpanCells / 2.0) / BridgeSpanCells);

        private static (int Min, int Max) SpanRange(DesignSceneProjection scene)
        {
            var min = int.MaxValue;
            var max = int.MinValue;
            var any = false;
            void Include(int z)
            {
                any = true;
                var k = SpanIndex(z);
                if (k < min) min = k;
                if (k > max) max = k;
            }
            foreach (var voxel in scene.Voxels)
                if (voxel.ObjectPath.IsDefaultOrEmpty) Include(voxel.LocalAt.Z);
            foreach (var nested in scene.NestedObjects)
                if (!nested.NestedItemIndices.IsDefaultOrEmpty && nested.NestedItemIndices.Length == 1)
                    Include(nested.At.Z);
            if (scene.RootFlatten is { } flatten)
            {
                Include(flatten.From.Z);
                Include(flatten.To.Z);
            }
            return any ? (min, max) : (0, 0);
        }

        private Dictionary<int, List<int>> RootSpanMembers(DesignSceneProjection scene, int kMin, int kMax,
            List<int> owned, double waterCenter)
        {
            var members = new Dictionary<int, List<int>>(owned.Count);
            var poses = new (int K, int Z0, int Z1, ScenePoint Position, Quaternion Rotation)[owned.Count];
            for (var n = 0; n < owned.Count; n++)
            {
                var k = owned[n];
                var sample = path.Sample(waterCenter + k * BridgeSpanCells);
                poses[n] = (k, k * BridgeSpanCells - BridgeSpanCells / 2, k * BridgeSpanCells + BridgeSpanCells / 2,
                    sample.Position, RailwayCoordinates.Rotation(sample.Forward));
                members[k] = [];
            }
            for (var i = 0; i < scene.Voxels.Length; i++)
            {
                var voxel = scene.Voxels[i];
                if (!voxel.ObjectPath.IsDefaultOrEmpty) continue;
                var local = voxel.LocalAt;
                foreach (var pose in poses)
                {
                    if (local.Z < pose.Z0 - BridgeJointMargin || local.Z >= pose.Z1 + BridgeJointMargin) continue;
                    var center = Vector3.Transform(
                        new Vector3(local.X + 0.5f, local.Y + 0.5f, local.Z - pose.K * BridgeSpanCells + 0.5f), pose.Rotation);
                    var located = path.Locate(RailwayCoordinates.Add(pose.Position, center));
                    // Inside a curve, a cell in this span's own interval can fall past the joint and is omitted.
                    if (pose.K != kMin && located < waterCenter + pose.Z0) continue;
                    if (pose.K != kMax && located >= waterCenter + pose.Z1) continue;
                    members[pose.K].Add(i);
                }
            }
            return members;
        }

        private DesignEvaluation? SliceSpan(DesignEvaluation evaluation, int k, List<int> owners)
        {
            var scene = DesignGeometry.Projection(evaluation);
            var z0 = k * BridgeSpanCells - BridgeSpanCells / 2;
            var z1 = k * BridgeSpanCells + BridgeSpanCells / 2;
            var shift = k * BridgeSpanCells;
            var included = new HashSet<int>();
            foreach (var nested in scene.NestedObjects)
                if (nested.NestedItemIndices.Length == 1 && nested.At.Z >= z0 && nested.At.Z < z1)
                    included.Add(nested.NestedItemIndices[0]);
            var voxels = ImmutableArray.CreateBuilder<SceneVoxelProjection>();
            foreach (var i in owners)
            {
                var voxel = scene.Voxels[i];
                var localZ = voxel.LocalAt.Z - shift;
                voxels.Add(voxel with
                {
                    At = new(voxel.At.X, voxel.At.Y, voxel.At.Z - shift),
                    LocalAt = new(voxel.LocalAt.X, voxel.LocalAt.Y, localZ)
                });
            }
            foreach (var voxel in scene.Voxels)
                if (!voxel.ObjectPath.IsDefaultOrEmpty && included.Contains(voxel.ObjectPath[0]))
                    voxels.Add(voxel);
            var nestedObjects = ImmutableArray.CreateBuilder<NestedObjectProjection>();
            foreach (var nested in scene.NestedObjects)
            {
                if (nested.NestedItemIndices.IsDefaultOrEmpty || !included.Contains(nested.NestedItemIndices[0])) continue;
                nestedObjects.Add(nested.NestedItemIndices.Length == 1
                    ? nested with { At = new(nested.At.X, nested.At.Y, nested.At.Z - shift) }
                    : nested);
            }
            FlattenRegion? flatten = null;
            if (scene.RootFlatten is { } region)
            {
                var from = Math.Max(region.From.Z, z0);
                var to = Math.Min(region.To.Z, z1 - 1);
                if (from <= to)
                    flatten = region with { From = new(region.From.X, from - shift), To = new(region.To.X, to - shift) };
            }
            if (voxels.Count == 0 && nestedObjects.Count == 0 && flatten is null) return null;
            var sliced = scene with { Voxels = voxels.ToImmutable(), NestedObjects = nestedObjects.ToImmutable(), RootFlatten = flatten };
            return evaluation switch
            {
                DesignEvaluation.Complete complete => complete with { Scene = sliced },
                DesignEvaluation.Partial partial => partial with { Scene = sliced },
                _ => null
            };
        }

        private void AddModel(DesignHash hash, ScenePose pose, GridColumn? decoration, Dictionary<GridColumn, TerrainColumn> columns)
        {
            cancellation.ThrowIfCancellationRequested();
            models.Add(hash);
            var lookup = snapshot.Content.FindDesign(hash);
            if (lookup is not ContentLookup<DesignEvaluation>.Found found || found.Value is not (DesignEvaluation.Complete or DesignEvaluation.Partial))
            {
                var reference = new ContentReference.Design(hash);
                objects.Add(new(new(nextId++), null, pose, null, [new SceneVisual.Missing(reference, false)], [], []));
                var sourceDiagnostics = lookup switch
                {
                    ContentLookup<DesignEvaluation>.Missing missing => missing.Diagnostics,
                    ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Failed failed } => failed.Diagnostics,
                    _ => ImmutableArray<ContentDiagnostic>.Empty
                };
                ReportMissing(reference, sourceDiagnostics, "Railway scene model is missing.");
                return;
            }
            var projection = DesignGeometry.Projection(found.Value);
            if (decoration is not null && projection.Voxels.Any(voxel =>
            {
                var local = pose.Position + Vector3.Transform(new Vector3(voxel.At.X + 0.5f, voxel.At.Y + 0.5f, voxel.At.Z + 0.5f), pose.Rotation);
                var point = RailwayCoordinates.Add(origin, local);
                var arc = path.Locate(point);
                var rail = path.Sample(arc);
                var lateral = (point.X - rail.Position.X) * rail.Right.X + (point.Z - rail.Position.Z) * rail.Right.Z;
                return Water(arc) || surface.TrackDistance(lateral) < RailwayCoordinates.DecorationVoxelClearance;
            })) return;
            AddPlaced(found.Value, pose, decoration, columns, null);
        }

        private void AddPlaced(DesignEvaluation evaluation, ScenePose pose, GridColumn? decoration,
            Dictionary<GridColumn, TerrainColumn> columns, ScenePoint? wearAt)
        {
            var projection = DesignGeometry.Projection(evaluation);
            if (heightFlattens is not null)
            {
                if (projection.RootFlatten is { } region) heightFlattens.Add(new(region, pose, origin));
                return;
            }
            foreach (var voxel in projection.Voxels) Appearance(voxel.Voxel);
            if (projection.RootFlatten is { } flatten)
            {
                // Flatten footprints project horizontally, while railway models retain the rail pitch.
                var forward = Vector3.Transform(Vector3.UnitZ, pose.Rotation);
                var horizontalRotation = RailwayCoordinates.Rotation(Vector3.Normalize(new(forward.X, 0, forward.Z)));
                var inverse = Quaternion.Conjugate(horizontalRotation);
                foreach (var column in columns.Keys.ToArray())
                {
                    var value = columns[column];
                    if (decoration is not null && (value.Water || surface.TrackDistance(value.Offset) < RailwayCoordinates.DecorationVoxelClearance)) continue;
                    var local = Vector3.Transform(new Vector3(column.X + 0.5f - pose.Position.X, 0, column.Z + 0.5f - pose.Position.Z), inverse);
                    var distance = Math.Max(Math.Max(flatten.From.X - local.X, local.X - flatten.To.X - 1),
                        Math.Max(flatten.From.Z - local.Z, local.Z - flatten.To.Z - 1));
                    var blend = Math.Clamp(distance / FlattenBlendRadiusCells, 0, 1);
                    if (blend < 1)
                        columns[column] = value with { Height = Math.Floor((pose.Position.Y + origin.Y + flatten.Height - 1) * (1 - blend) + value.Height * blend) };
                }
            }
            var placedAt = RailwayCoordinates.Add(origin, pose.Position);
            var wearPoint = wearAt ?? placedAt;
            var modelArc = path.Locate(placedAt);
            var rendered = DesignGeometry.Build(snapshot.Content, evaluation, null, pose, _ => new(nextId++),
                terrainLocation: new WorldLocation.Edge(path.Edge.Id, modelArc / RailwayCoordinates.CellsPerMeter),
                wearSeed: DesignGeometry.PlacementWearSeed(path.Edge.Id, wearPoint));
            objects.AddRange(rendered.Objects); picking.AddRange(rendered.Picking); diagnostics.AddRange(rendered.Diagnostics);
        }
    }
}

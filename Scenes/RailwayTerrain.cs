using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayTerrain
{
    private const double BedTopBelowRailCells = 1;
    private const double TunnelPortalLookbackCells = 2;
    private const ulong HeightNoisePurpose = 3;
    private const ulong EndpointMaterialPurpose = 9;
    private const ulong DecorationDensityPurpose = 10;
    private const ulong DecorationSelectionPurpose = 11;
    private const ulong RiverPlacementPurpose = 20;
    private const ulong LakePlacementPurpose = 21;

    internal static double NodeGround(SceneWorldSnapshot snapshot, NodeMapItem node, ScenePoint localPoint)
    {
        var terrain = snapshot.Content.FindTerrain(node.Terrain.TypeId) is ContentLookup<TerrainAppearanceProjection>.Found found ? found.Value : null;
        var origin = RailwayCoordinates.NodePoint(node);
        var point = new ScenePoint(origin.X + localPoint.X, origin.Y + localPoint.Y, origin.Z + localPoint.Z);
        return Ground(snapshot.Core.World.WorldSeed, node.Id.Value, node.Terrain, terrain, point);
    }

    private static double Ground(ulong seed, ulong key, TerrainView view, TerrainAppearanceProjection? terrain, ScenePoint point)
    {
        var height = -RailwayCoordinates.TerrainBaselineOffset;
        if (terrain is null) return height;
        foreach (var octave in terrain.LocalHeightNoise)
        {
            if (octave.ScaleMeters <= 0) continue;
            var scale = octave.ScaleMeters * RailwayCoordinates.CellsPerMeter;
            var x = point.X / scale; var z = point.Z / scale;
            var ix = (long)Math.Floor(x); var iz = (long)Math.Floor(z);
            var tx = x - ix; var tz = z - iz;
            tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
            double At(long a, long b) => RailwayRandom.Unit(seed, key, unchecked(a * 73856093 ^ b * 19349663), HeightNoisePurpose) * 2 - 1;
            var noise = (At(ix, iz) * (1 - tx) + At(ix + 1, iz) * tx) * (1 - tz) +
            (At(ix, iz + 1) * (1 - tx) + At(ix + 1, iz + 1) * tx) * tz;
            height += noise * octave.Amplitude * view.Relief * RailwayCoordinates.CellsPerMeter;
        }
        return height;
    }

    private sealed record TerrainColumn(double Arc, double Offset, double Height, TerrainAppearanceProjection? Terrain, bool Water, bool Tunnel);

    internal static RailwaySegment Build(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken cancellation) =>
    new SegmentBuilder(snapshot, path, index, cancellation).Build();

    private sealed class SegmentBuilder
    {
        private readonly SceneWorldSnapshot snapshot;
        private readonly RailwayPath path;
        private readonly int index;
        private readonly CancellationToken cancellation;
        private readonly double begin;
        private readonly double end;
        private readonly double endpointBlend;
        private readonly ScenePoint startPoint;
        private readonly ScenePoint endPoint;
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
        private readonly Dictionary<TerrainTypeId, TerrainAppearanceProjection?> terrains;
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
            startPoint = path.Sample(0).Position;
            endPoint = path.Sample(path.LengthCells).Position;
            waterRegions = WaterFeatures().Select(feature =>
            {
                var first = Math.Max(0, feature.Center - feature.HalfWidth);
                var last = Math.Min(path.LengthCells, feature.Center + feature.HalfWidth);
                var level = Math.Min(path.Sample(first).Position.Y, Math.Min(path.Sample(last).Position.Y,
                path.Sample(Math.Clamp(path.LengthCells / 2, first, last)).Position.Y)) - RailwayCoordinates.WaterSurfaceOffset;
                return (feature.Center, First: first, Last: last, Level: level);
            }).ToArray();
            if (waterRegions.Length == 2 && Math.Max(waterRegions[0].First, waterRegions[1].First) <= Math.Min(waterRegions[0].Last, waterRegions[1].Last))
            {
                var level = Math.Min(waterRegions[0].Level, waterRegions[1].Level);
                waterRegions[0].Level = level; waterRegions[1].Level = level;
            }
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
            terrains = new Dictionary<TerrainTypeId, TerrainAppearanceProjection?>();
            halfWidth = RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks);
            nextId = 1UL;
            readsStartTerrain = Math.Max(0, begin - (path.Edge.Tracks > 0 ? TunnelPortalLookbackCells : 0)) < endpointBlend;
            readsEndTerrain = path.LengthCells - end < endpointBlend;
            terrainId = new SceneObjectId(0);
            GetTerrain(path.Edge.Terrain.TypeId);
            if (readsStartTerrain) GetTerrain(path.Start.Terrain.TypeId);
            if (readsEndTerrain) GetTerrain(path.End.Terrain.TypeId);
        }

        internal RailwaySegment Build()
        {
            var (tunnels, portals) = SampleTunnels();
            var columns = SampleColumns();
            foreach (var arc in portals) PlaceRailwayModel(arc, RailwayModelRole.Tunnel, columns);
            PlaceModels(columns);
            return Render(columns, tunnels);
        }

        private (ImmutableArray<(double Start, double End)> Intervals, List<double> Portals) SampleTunnels()
        {
            if (path.Edge.Tracks == 0) return ([], []);
            var tunnelIntervals = ImmutableArray.CreateBuilder<(double Start, double End)>();
            var portals = new List<double>();
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
                        if (!TunnelAt(previousArc)) portals.Add(tunnelStart.Value);
                    }
                    if (!tunnel && finish < path.LengthCells)
                        portals.Add(finish);
                    tunnelStart = null;
                }
                if (arc == end) break;
            }
            return (tunnelIntervals.ToImmutable(), portals);
        }

        private Dictionary<GridColumn, TerrainColumn> SampleColumns()
        {
            var columns = new Dictionary<GridColumn, TerrainColumn>();
            var terrain = terrains[path.Edge.Terrain.TypeId];
            for (var arc = begin; arc <= end; arc = Math.Min(end, arc + 0.5))
            {
                cancellation.ThrowIfCancellationRequested();
                var sample = path.Sample(arc);
                for (var offset = -(double)halfWidth; offset <= halfWidth; offset += 0.5)
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
                    var height = Height(located, center, terrain);
                    var water = Water(located);
                    if (water) height = Math.Min(height, WaterLevel(located) - RailwayCoordinates.WaterDepth);
                    var trackDistance = TrackDistance(lateral);
                    var tunnel = path.Edge.Tracks > 0 && TunnelAt(located);
                    if (path.Edge.Tracks > 0 && !water && !tunnel)
                    {
                        var bed = nearest.Position.Y - BedTopBelowRailCells - (trackDistance <= RailwayCoordinates.BedTopHalfWidth ? 1 : 2);
                        var blend = Math.Clamp((trackDistance - RailwayCoordinates.BedBaseHalfWidth) / RailwayCoordinates.BedBlendWidth, 0, 1);
                        height = bed * (1 - blend) + height * blend;
                    }
                    var selectedTerrain = terrain;
                    var random = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, ColumnKey(column), EndpointMaterialPurpose);
                    if (located < endpointBlend && random >= located / endpointBlend)
                        selectedTerrain = terrains[path.Start.Terrain.TypeId];
                    else if (path.LengthCells - located < endpointBlend && random >= (path.LengthCells - located) / endpointBlend)
                        selectedTerrain = terrains[path.End.Terrain.TypeId];
                    columns.Add(column, new(located, lateral, Math.Floor(height), selectedTerrain, water, tunnel));
                }
                if (arc == end) break;
            }
            return columns;
        }

        private void PlaceModels(Dictionary<GridColumn, TerrainColumn> columns)
        {
            // One bridge per deterministic water interval, placed only by the owning segment.
            foreach (var arc in waterRegions.Select(region => region.Center))
                if (arc >= begin && arc < end && path.Edge.Tracks > 0) PlaceRailwayModel(arc, RailwayModelRole.Bridge, columns);

            var decorations = new List<(GridColumn Column, DesignHash Design)>();
            foreach (var pair in columns)
            {
                cancellation.ThrowIfCancellationRequested();
                var value = pair.Value;
                var terrain = value.Terrain;
                if (terrain is null || terrain.Decorations.IsDefaultOrEmpty || value.Water || TrackDistance(value.Offset) < RailwayCoordinates.DecorationRootClearance) continue;
                var key = ColumnKey(pair.Key);
                if (RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, DecorationDensityPurpose) >= Math.Clamp(terrain.DecorationTargetPerSquareMeter / (RailwayCoordinates.CellsPerMeter * RailwayCoordinates.CellsPerMeter), 0, 1)) continue;
                var total = terrain.Decorations.Sum(item => Math.Max(0, item.Weight));
                if (total == 0) continue;
                var selection = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, DecorationSelectionPurpose) * total;
                var slot = 0;
                while (slot < terrain.Decorations.Length - 1 && selection >= Math.Max(0, terrain.Decorations[slot].Weight))
                    selection -= Math.Max(0, terrain.Decorations[slot++].Weight);
                modelReads.Add(new(terrain.Id, slot, RailwayModelRole.Decoration));
                decorations.Add((pair.Key, terrain.Decorations[slot].SceneModel));
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
            var waterVertices = new List<SceneVertex>();
            var waterIndices = new List<int>();
            var bedTerrain = GetTerrain(path.Edge.Terrain.TypeId);
            foreach (var pair in columns)
            {
                cancellation.ThrowIfCancellationRequested();
                var column = pair.Key; var value = pair.Value;
                var rail = path.Sample(value.Arc);
                var trackDistance = TrackDistance(value.Offset);
                var isTunnel = value.Tunnel;
                heights.Add(column, value.Height + 1);
                for (var depth = 0; depth < RailwayCoordinates.TerrainDepthCells; depth++)
                {
                    var y = value.Height - depth;
                    if (isTunnel && trackDistance < RailwayCoordinates.BedBlendWidth && y >= rail.Position.Y && y < rail.Position.Y + RailwayCoordinates.TunnelClearance) continue;
                    var terrain = value.Terrain;
                    var bed = path.Edge.Tracks > 0 && !value.Water && !isTunnel &&
                    (depth == 0 && trackDistance <= RailwayCoordinates.BedBaseHalfWidth || depth == 1 && trackDistance <= RailwayCoordinates.BedTopHalfWidth);
                    if (bed ? bedTerrain is null : terrain is null) continue;
                    var voxel = bed ? bedTerrain!.RockVoxel : depth == 0 ? terrain!.SurfaceVoxel : depth < 3 ? terrain!.SubsurfaceVoxel : terrain!.RockVoxel;
                    var appearance = Appearance(voxel);
                    var reference = appearance is null ? new ContentReference.Core(new CoreDefinitionReference.Voxel(voxel)) : null;
                    cells.Add(new(new(column.X, checked((int)(y - origin.Y)), column.Z), VoxelOrientation.PxPy,
                    appearance, null, null, new PickSubject.Terrain(new WorldLocation.Edge(path.Edge.Id, value.Arc / RailwayCoordinates.CellsPerMeter)), [], reference));
                }
                if (value.Water)
                {
                    var y = (float)(WaterLevel(value.Arc) - origin.Y);
                    var first = waterVertices.Count;
                    foreach (var corner in new Vector2[] { new(0, 0), new(0, 1), new(1, 1), new(1, 0) })
                        waterVertices.Add(new(new(column.X + corner.X, y, column.Z + corner.Y), Vector3.UnitY, corner, corner, ExposedFaceEdges.None, 0, 0));
                    waterIndices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
                }
            }
            var grid = new VoxelGrid(cells.ToImmutable());
            var visuals = DesignGeometry.Meshes(grid, false).ToBuilder();
            foreach (var missingTerrain in terrains.Where(item => item.Value is null))
                visuals.Add(new SceneVisual.Missing(new ContentReference.Core(new CoreDefinitionReference.Terrain(missingTerrain.Key)), false));
            foreach (var missing in cells.Where(cell => cell.MissingReference is not null))
                objects.Add(new(new(nextId++), terrainId, SceneGeometry.Placement(missing.At, missing.Orientation), null,
                [new SceneVisual.Missing(missing.MissingReference!, true)], [], []));
            foreach (var mesh in RailwayMeshes.Build(path, index, origin)) visuals.Add(new SceneVisual.Geometry(mesh, new(false, 0, 0, false)));
            if (waterVertices.Count > 0) visuals.Add(new SceneVisual.Geometry(new(new SceneMaterial.BuiltIn(BuiltInMaterial.Water),
            waterVertices.ToImmutableArray(), waterIndices.ToImmutableArray()), new(false, 0, 0, false)));
            objects.Insert(0, new(terrainId, null, new(Vector3.Zero, Quaternion.Identity), null, visuals.ToImmutable(), [], []));
            picking.Add(new(terrainId, new(Vector3.Zero, Quaternion.Identity), grid));
            return new(index, origin, new(objects.ToImmutable(), picking.ToImmutable(), diagnostics.ToImmutable()), heights.ToImmutable(),
            RailwayDependencies.Capture(snapshot, path, index, terrainReads, modelReads, models, voxelReads,
            readsStartTerrain, readsEndTerrain), tunnels);

        }

        private long ColumnKey(GridColumn column) => unchecked(((long)(column.X + origin.X) * 73856093) ^ ((long)(column.Z + origin.Z) * 19349663));
        private double TrackDistance(double offset) => path.Edge.Tracks == 0 ? double.PositiveInfinity :
        Enumerable.Range(1, path.Edge.Tracks).Min(track => Math.Abs(offset - RailwayCoordinates.TrackOffset(track, path.Edge.Tracks)));
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
        private void ReportMissing(ContentReference reference, ImmutableArray<ContentDiagnostic> sourceDiagnostics, string detail)
        {
            DesignGeometry.AddDiagnostics(sourceDiagnostics, diagnostics, null);
            var package = diagnostics.FirstOrDefault(problem => problem.Content == reference && problem.Package is not null)?.Package;
            diagnostics.Add(new("content_missing", SceneStage.Build, null, reference, package, null, detail));
        }

        private double Height(double arc, ScenePoint point, TerrainAppearanceProjection? terrain)
        {
            var railHeight = path.Sample(arc).Position.Y;
            var height = Ground(path.Seed, path.Edge.Id.Value, path.Edge.Terrain, terrain, point);
            if (arc < endpointBlend)
            {
                var weight = arc / endpointBlend;
                height = NodeHeight(path.Start, startPoint, point) *
                (1 - weight) + height * weight;
            }
            if (path.LengthCells - arc < endpointBlend)
            {
                var weight = (path.LengthCells - arc) / endpointBlend;
                height = NodeHeight(path.End, endPoint, point) *
                (1 - weight) + height * weight;
            }
            return railHeight + height;
        }
        private bool TunnelAt(double arc)
        {
            var sample = path.Sample(arc);
            return Height(arc, sample.Position, terrains[path.Edge.Terrain.TypeId]) > sample.Position.Y + RailwayCoordinates.TunnelClearance && !Water(arc);
        }
        private bool Water(double arc) => waterRegions.Any(region => arc >= region.First && arc <= region.Last);
        private double WaterLevel(double arc) => waterRegions.First(region => arc >= region.First && arc <= region.Last).Level;
        private IEnumerable<(double Center, double HalfWidth)> WaterFeatures()
        {
            if (path.Edge.Terrain.FeatureIds.Contains("river")) yield return (path.LengthCells *
            (0.5 + 0.2 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, RiverPlacementPurpose)), 12);
            if (path.Edge.Terrain.FeatureIds.Contains("lake")) yield return (path.LengthCells *
            (0.3 + 0.5 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, LakePlacementPurpose)), 48);
        }
        private void PlaceRailwayModel(double arc, RailwayModelRole role, Dictionary<GridColumn, TerrainColumn> columns)
        {
            var terrain = GetTerrain(path.Edge.Terrain.TypeId);
            modelReads.Add(new(path.Edge.Terrain.TypeId, -1, role));
            var model = terrain?.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
            if (model is null)
            {
                var reference = new ContentReference.Core(new CoreDefinitionReference.Terrain(path.Edge.Terrain.TypeId));
                objects.Add(new(new(nextId++), null, new(RailwayCoordinates.Relative(path.Sample(arc).Position, origin), Quaternion.Identity),
                null, [new SceneVisual.Missing(reference, false)], [], []));
                ReportMissing(reference, [], "No railway scene model matches the track count.");
                return;
            }
            var sample = path.Sample(arc);
            AddModel(role == RailwayModelRole.Bridge ? model.Bridge : model.TunnelPortal,
            new(RailwayCoordinates.Relative(sample.Position, origin), RailwayCoordinates.Rotation(sample.Forward)), null, columns);
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
                return Water(arc) || TrackDistance(lateral) < RailwayCoordinates.DecorationVoxelClearance;
            })) return;
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
                    if (decoration is not null && (value.Water || TrackDistance(value.Offset) < RailwayCoordinates.DecorationVoxelClearance)) continue;
                    var local = Vector3.Transform(new Vector3(column.X + 0.5f - pose.Position.X, 0, column.Z + 0.5f - pose.Position.Z), inverse);
                    var distance = Math.Max(Math.Max(flatten.From.X - local.X, local.X - flatten.To.X - 1),
                    Math.Max(flatten.From.Z - local.Z, local.Z - flatten.To.Z - 1));
                    var blend = Math.Clamp(distance / 8, 0, 1);
                    if (blend < 1)
                        columns[column] = value with { Height = Math.Floor((pose.Position.Y + origin.Y + flatten.Height - 1) * (1 - blend) + value.Height * blend) };
                }
            }
            var modelArc = path.Locate(RailwayCoordinates.Add(origin, pose.Position));
            var rendered = DesignGeometry.Build(snapshot.Content, found.Value, null, pose, _ => new(nextId++),
            terrainLocation: new WorldLocation.Edge(path.Edge.Id, modelArc / RailwayCoordinates.CellsPerMeter));
            objects.AddRange(rendered.Objects); picking.AddRange(rendered.Picking); diagnostics.AddRange(rendered.Diagnostics);
        }
        private double NodeHeight(NodeMapItem node, ScenePoint endpoint, ScenePoint point)
        {
            var mapOrigin = RailwayCoordinates.NodePoint(node);
            return Ground(path.Seed, node.Id.Value, node.Terrain, terrains[node.Terrain.TypeId],
            new(mapOrigin.X + point.X - endpoint.X, mapOrigin.Y + point.Y - endpoint.Y, mapOrigin.Z + point.Z - endpoint.Z));
        }
    }
}

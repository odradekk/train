using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayTerrain
{
    internal const double EndpointBlendCells = 128;
    private const int ColumnDepth = 6;

    internal static double NodeGround(SceneWorldSnapshot snapshot, NodeMapItem node, ScenePoint point)
    {
        var terrain = snapshot.Content.FindTerrain(node.Terrain.TypeId) is ContentLookup<TerrainAppearanceProjection>.Found found ? found.Value : null;
        return Ground(snapshot.Core.World.WorldSeed, node.Id.Value, node.Terrain, terrain, point);
    }

    private static double Ground(ulong seed, ulong key, TerrainView view, TerrainAppearanceProjection? terrain, ScenePoint point)
    {
        var height = -4.0;
        if (terrain is null) return height;
        foreach (var octave in terrain.LocalHeightNoise)
        {
            if (octave.ScaleMeters <= 0) continue;
            var scale = octave.ScaleMeters * 2;
            var x = point.X / scale; var z = point.Z / scale;
            var ix = (long)Math.Floor(x); var iz = (long)Math.Floor(z);
            var tx = x - ix; var tz = z - iz;
            tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
            double At(long a, long b) => RailwayRandom.Unit(seed, key, unchecked(a * 73856093 ^ b * 19349663), 3) * 2 - 1;
            var noise = (At(ix, iz) * (1 - tx) + At(ix + 1, iz) * tx) * (1 - tz) +
                (At(ix, iz + 1) * (1 - tx) + At(ix + 1, iz + 1) * tx) * tz;
            height += noise * octave.Amplitude * view.Relief * 2;
        }
        return height;
    }

    internal static RailwaySegment Build(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken cancellation)
    {
        if (index < 0 || index >= path.SegmentCount) throw new ArgumentOutOfRangeException(nameof(index));
        var begin = index * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
        var endpointBlend = Math.Min(EndpointBlendCells, path.LengthCells / 2);
        var waterRegions = WaterFeatures().Select(feature =>
        {
            var first = Math.Max(0, feature.Center - feature.HalfWidth);
            var last = Math.Min(path.LengthCells, feature.Center + feature.HalfWidth);
            var level = Math.Min(path.Sample(first).Position.Y, Math.Min(path.Sample(last).Position.Y,
                path.Sample(Math.Clamp(path.LengthCells / 2, first, last)).Position.Y)) - 2;
            return (feature.Center, First: first, Last: last, Level: level);
        }).ToArray();
        if (waterRegions.Length == 2 && Math.Max(waterRegions[0].First, waterRegions[1].First) <= Math.Min(waterRegions[0].Last, waterRegions[1].Last))
        {
            var level = Math.Min(waterRegions[0].Level, waterRegions[1].Level);
            waterRegions[0].Level = level; waterRegions[1].Level = level;
        }
        var anchor = path.Sample(begin).Position;
        var origin = new ScenePoint(Math.Floor(anchor.X / 32) * 32, Math.Floor(anchor.Y / 32) * 32, Math.Floor(anchor.Z / 32) * 32);
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        var terrainReads = new HashSet<TerrainTypeId>();
        var modelReads = new HashSet<RailwayModelRead>();
        var models = new HashSet<DesignHash>();
        var voxelReads = new HashSet<VoxelDefinitionId>();
        var appearances = new Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?>();
        var terrains = new Dictionary<TerrainTypeId, TerrainAppearanceProjection?>();
        var columns = new Dictionary<GridColumn, (double Arc, double Offset, double Height, TerrainAppearanceProjection? Terrain, bool Water)>();
        var halfWidth = Math.Max(48, path.Edge.Tracks * 17 + 32);
        var nextId = 1UL;
        var readsStartTerrain = false;
        var readsEndTerrain = false;
        var terrainId = new SceneObjectId(0);
        var tunnelIntervals = ImmutableArray.CreateBuilder<(double Start, double End)>();
        double? tunnelStart = null;
        for (var arc = begin; arc <= end; arc = Math.Min(end, arc + 0.5))
        {
            cancellation.ThrowIfCancellationRequested();
            var sample = path.Sample(arc);
            var terrain = GetTerrain(path.Edge.Terrain.TypeId);
            var tunnel = Height(arc, sample.Position, terrain) > sample.Position.Y + 20 && !Water(arc);
            if (path.Edge.Tracks > 0 && tunnel && tunnelStart is null) tunnelStart = arc;
            if (tunnelStart is not null && (!tunnel || arc == end))
            {
                var finish = tunnel ? end : arc;
                tunnelIntervals.Add((tunnelStart.Value, finish));
                if (tunnelStart.Value > 0 && !TunnelAt(Math.Max(0, tunnelStart.Value - 2)))
                    PlaceRailwayModel(tunnelStart.Value, RailwayModelRole.Tunnel);
                if (!tunnel && finish < path.LengthCells)
                    PlaceRailwayModel(finish, RailwayModelRole.Tunnel);
                tunnelStart = null;
            }
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
                if (water) height = Math.Min(height, WaterLevel(located) - 5);
                var trackDistance = TrackDistance(lateral);
                if (path.Edge.Tracks > 0 && !water && !TunnelAt(located))
                {
                    var bed = nearest.Position.Y - (trackDistance <= 9.5 ? 1 : 2);
                    var blend = Math.Clamp((trackDistance - 10.5) / 12, 0, 1);
                    height = bed * (1 - blend) + height * blend;
                }
                var selectedTerrain = terrain;
                var random = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, ColumnKey(column), 9);
                if (located < endpointBlend && random >= located / endpointBlend)
                    selectedTerrain = GetTerrain(path.Start.Terrain.TypeId);
                else if (path.LengthCells - located < endpointBlend && random >= (path.LengthCells - located) / endpointBlend)
                    selectedTerrain = GetTerrain(path.End.Terrain.TypeId);
                columns.Add(column, (located, lateral, Math.Floor(height), selectedTerrain, water));
            }
            if (arc == end) break;
        }

        // One bridge per deterministic water interval, placed only by the owning segment.
        foreach (var arc in waterRegions.Select(region => region.Center))
            if (arc >= begin && arc < end && path.Edge.Tracks > 0) PlaceRailwayModel(arc, RailwayModelRole.Bridge);

        var decorations = new List<(GridColumn Column, DesignHash Design)>();
        foreach (var pair in columns)
        {
            cancellation.ThrowIfCancellationRequested();
            var value = pair.Value;
            var terrain = value.Terrain;
            if (terrain is null || terrain.Decorations.IsDefaultOrEmpty || value.Water || TrackDistance(value.Offset) < 24) continue;
            var key = ColumnKey(pair.Key);
            if (RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, 10) >= Math.Clamp(terrain.DecorationTargetPerSquareMeter / 4, 0, 1)) continue;
            var total = terrain.Decorations.Sum(item => Math.Max(0, item.Weight));
            if (total == 0) continue;
            var selection = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, 11) * total;
            var slot = 0;
            while (slot < terrain.Decorations.Length - 1 && selection >= Math.Max(0, terrain.Decorations[slot].Weight))
                selection -= Math.Max(0, terrain.Decorations[slot++].Weight);
            modelReads.Add(new(terrain.Id, slot, RailwayModelRole.Decoration));
            decorations.Add((pair.Key, terrain.Decorations[slot].SceneModel));
        }
        foreach (var decoration in decorations)
        {
            var value = columns[decoration.Column];
            AddModel(decoration.Design, new(new(decoration.Column.X, (float)(value.Height + 1 - origin.Y), decoration.Column.Z), Quaternion.Identity), decoration.Column);
        }

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
            var isTunnel = tunnelIntervals.Any(interval => value.Arc >= interval.Start && value.Arc <= interval.End);
            heights.Add(column, value.Height + 1);
            for (var depth = 0; depth < ColumnDepth; depth++)
            {
                var y = value.Height - depth;
                if (isTunnel && trackDistance < 12 && y >= rail.Position.Y && y < rail.Position.Y + 20) continue;
                var terrain = value.Terrain;
                var bed = path.Edge.Tracks > 0 && !value.Water && depth < 2 && trackDistance <= (depth == 0 ? 9.5 : 10.5);
                if (bed ? bedTerrain is null : terrain is null) continue;
                var voxel = bed ? bedTerrain!.RockVoxel : depth == 0 ? terrain!.SurfaceVoxel : depth < 3 ? terrain!.SubsurfaceVoxel : terrain!.RockVoxel;
                var appearance = Appearance(voxel);
                var reference = appearance is null ? new ContentReference.Core(new CoreDefinitionReference.Voxel(voxel)) : null;
                cells.Add(new(new(column.X, checked((int)(y - origin.Y)), column.Z), VoxelOrientation.PxPy,
                    appearance, null, null, new PickSubject.Terrain(new WorldLocation.Edge(path.Edge.Id, value.Arc / 2)), [], reference));
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
                readsStartTerrain, readsEndTerrain), tunnelIntervals.ToImmutable());

        long ColumnKey(GridColumn column) => unchecked(((long)(column.X + origin.X) * 73856093) ^ ((long)(column.Z + origin.Z) * 19349663));
        double TrackDistance(double offset) => path.Edge.Tracks == 0 ? double.PositiveInfinity :
            Enumerable.Range(1, path.Edge.Tracks).Min(track => Math.Abs(offset - RailwayCoordinates.TrackOffset(track, path.Edge.Tracks)));
        TerrainAppearanceProjection? GetTerrain(TerrainTypeId id)
        {
            terrainReads.Add(id);
            if (terrains.TryGetValue(id, out var cached)) return cached;
            var lookup = snapshot.Content.FindTerrain(id);
            var result = lookup is ContentLookup<TerrainAppearanceProjection>.Found found ? found.Value : null;
            if (lookup is ContentLookup<TerrainAppearanceProjection>.Missing missing) DesignGeometry.AddDiagnostics(missing.Diagnostics, diagnostics, null);
            if (result is null) diagnostics.Add(new("content_missing", SceneStage.Build, null, new ContentReference.Core(new CoreDefinitionReference.Terrain(id)), null, null, "Railway terrain is missing."));
            terrains.Add(id, result); return result;
        }
        VoxelAppearanceProjection? Appearance(VoxelDefinitionId id)
        {
            voxelReads.Add(id);
            if (appearances.TryGetValue(id, out var cached)) return cached;
            var lookup = snapshot.Content.FindVoxelAppearance(id);
            var result = lookup is ContentLookup<VoxelAppearanceProjection>.Found found ? found.Value : null;
            if (lookup is ContentLookup<VoxelAppearanceProjection>.Missing missing) DesignGeometry.AddDiagnostics(missing.Diagnostics, diagnostics, null);
            if (result is null) diagnostics.Add(new("content_missing", SceneStage.Build, null, new ContentReference.Core(new CoreDefinitionReference.Voxel(id)), null, null, "Railway voxel appearance is missing."));
            appearances.Add(id, result); return result;
        }
        double Height(double arc, ScenePoint point, TerrainAppearanceProjection? terrain)
        {
            var height = RawHeight(path.Edge.Terrain, terrain, point);
            if (arc < endpointBlend)
            {
                readsStartTerrain = true;
                GetTerrain(path.Start.Terrain.TypeId);
                var weight = arc / endpointBlend;
                height = NodeGround(snapshot, path.Start, point) * (1 - weight) + height * weight;
            }
            if (path.LengthCells - arc < endpointBlend)
            {
                readsEndTerrain = true;
                GetTerrain(path.End.Terrain.TypeId);
                var weight = (path.LengthCells - arc) / endpointBlend;
                height = NodeGround(snapshot, path.End, point) * (1 - weight) + height * weight;
            }
            return height;
        }
        double RawHeight(TerrainView view, TerrainAppearanceProjection? terrain, ScenePoint point) => Ground(path.Seed, path.Edge.Id.Value, view, terrain, point);
        bool TunnelAt(double arc)
        {
            var sample = path.Sample(arc);
            return Height(arc, sample.Position, GetTerrain(path.Edge.Terrain.TypeId)) > sample.Position.Y + 20 && !Water(arc);
        }
        bool Water(double arc) => waterRegions.Any(region => arc >= region.First && arc <= region.Last);
        double WaterLevel(double arc) => waterRegions.First(region => arc >= region.First && arc <= region.Last).Level;
        IEnumerable<(double Center, double HalfWidth)> WaterFeatures()
        {
            if (path.Edge.Terrain.FeatureIds.Contains("river")) yield return (path.LengthCells *
                (0.5 + 0.2 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, 20)), 12);
            if (path.Edge.Terrain.FeatureIds.Contains("lake")) yield return (path.LengthCells *
                (0.3 + 0.5 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, 21)), 48);
        }
        void PlaceRailwayModel(double arc, RailwayModelRole role)
        {
            var terrain = GetTerrain(path.Edge.Terrain.TypeId);
            modelReads.Add(new(path.Edge.Terrain.TypeId, -1, role));
            var model = terrain?.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
            if (model is null)
            {
                var reference = new ContentReference.Core(new CoreDefinitionReference.Terrain(path.Edge.Terrain.TypeId));
                objects.Add(new(new(nextId++), null, new(RailwayCoordinates.Relative(path.Sample(arc).Position, origin), Quaternion.Identity),
                    null, [new SceneVisual.Missing(reference, false)], [], []));
                diagnostics.Add(new("content_missing", SceneStage.Build, null, null, null, null, "No railway scene model matches the track count."));
                return;
            }
            var sample = path.Sample(arc);
            var forward = Vector3.Normalize(new(sample.Forward.X, 0, sample.Forward.Z));
            var right = Vector3.Cross(Vector3.UnitY, forward);
            var rotation = Quaternion.CreateFromRotationMatrix(new(right.X, right.Y, right.Z, 0, 0, 1, 0, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
            AddModel(role == RailwayModelRole.Bridge ? model.Bridge : model.TunnelPortal,
                new(RailwayCoordinates.Relative(sample.Position, origin), rotation), null);
        }
        void AddModel(DesignHash hash, ScenePose pose, GridColumn? decoration)
        {
            cancellation.ThrowIfCancellationRequested();
            models.Add(hash);
            var lookup = snapshot.Content.FindDesign(hash);
            if (lookup is not ContentLookup<DesignEvaluation>.Found found || found.Value is not (DesignEvaluation.Complete or DesignEvaluation.Partial))
            {
                var reference = new ContentReference.Design(hash);
                objects.Add(new(new(nextId++), null, pose, null, [new SceneVisual.Missing(reference, false)], [], []));
                diagnostics.Add(new("content_missing", SceneStage.Build, null, reference, null, null, "Railway scene model is missing."));
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
                return Water(arc) || TrackDistance(lateral) < 17;
            })) return;
            foreach (var voxel in projection.Voxels) Appearance(voxel.Voxel);
            if (projection.RootFlatten is { } flatten)
            {
                // Flatten is in model-local coordinates, including the model's rotation.
                foreach (var column in columns.Keys.ToArray())
                {
                    var value = columns[column];
                    var local = Vector3.Transform(new Vector3(column.X + 0.5f, 0, column.Z + 0.5f) - pose.Position, Quaternion.Inverse(pose.Rotation));
                    var distance = Math.Max(Math.Max(flatten.From.X - local.X, local.X - flatten.To.X - 1),
                        Math.Max(flatten.From.Z - local.Z, local.Z - flatten.To.Z - 1));
                    var blend = Math.Clamp(distance / 8, 0, 1);
                    if (blend < 1)
                        columns[column] = value with { Height = Math.Floor((pose.Position.Y + origin.Y + flatten.Height - 1) * (1 - blend) + value.Height * blend) };
                }
            }
            var modelArc = path.Locate(RailwayCoordinates.Add(origin, pose.Position));
            var rendered = DesignGeometry.Build(snapshot.Content, found.Value, null, pose, _ => new(nextId++),
                terrainLocation: new WorldLocation.Edge(path.Edge.Id, modelArc / 2));
            objects.AddRange(rendered.Objects); picking.AddRange(rendered.Picking); diagnostics.AddRange(rendered.Diagnostics);
        }
    }
}

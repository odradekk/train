using System.Numerics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayTerrainCaps
{
    private readonly SceneWorldSnapshot snapshot;
    private readonly RailwayPath path;
    private readonly TerrainSurface.Railway surface;
    private readonly Dictionary<TerrainTypeId, TerrainAppearanceProjection?> terrains = [];
    private readonly Dictionary<DesignHash, FlattenRegion?> decorations = [];
    private readonly Dictionary<int, TerrainHeightCap.Builder> parts = [];
    private readonly ConcurrentDictionary<int, Lazy<ImmutableArray<RailwayTerrain.FlattenPlacement>>> heightFlattens = [];
    private readonly bool hasDecorationFlatten;
    private readonly bool hasRailwayFlatten;
    private readonly Func<string, TerrainHeightCap?>? completed;

    internal RailwayTerrainCaps(SceneWorldSnapshot snapshot, RailwayPath path, Func<string, TerrainHeightCap?>? completed = null)
    {
        this.completed = completed;
        this.snapshot = snapshot;
        this.path = path;
        foreach (var id in new[] { path.Edge.Terrain.TypeId, path.Start.Terrain.TypeId, path.End.Terrain.TypeId }.Distinct())
        {
            var terrain = (snapshot.Content.FindTerrain(id) as ContentLookup<TerrainAppearanceProjection>.Found)?.Value;
            terrains[id] = terrain;
            foreach (var item in terrain?.Decorations ?? [])
            {
                if (decorations.ContainsKey(item.SceneModel)) continue;
                var evaluation = (snapshot.Content.FindDesign(item.SceneModel) as ContentLookup<DesignEvaluation>.Found)?.Value;
                decorations[item.SceneModel] = evaluation is DesignEvaluation.Complete or DesignEvaluation.Partial
                    ? DesignGeometry.Projection(evaluation).RootFlatten : null;
            }
        }
        hasDecorationFlatten = decorations.Values.Any(value => value is not null);
        var railwayModels = terrains[path.Edge.Terrain.TypeId]?.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
        hasRailwayFlatten = railwayModels is not null &&
            new[] { railwayModels.Bridge, railwayModels.TunnelPortal, railwayModels.RepairSite }.Any(hash =>
                snapshot.Content.FindDesign(hash) is ContentLookup<DesignEvaluation>.Found found &&
                found.Value is DesignEvaluation.Complete or DesignEvaluation.Partial &&
                DesignGeometry.Projection(found.Value).RootFlatten is not null);
        surface = new(path, id => terrains[id]);
    }

    internal double Height(ScenePoint point) => surface.FarHeight(point);
    internal TerrainAppearanceProjection? Terrain => terrains[path.Edge.Terrain.TypeId];

    internal Func<ScenePanBounds, string> CreateInputs(SceneMaterial material, int textureSpan)
    {
        var ground = RailwayDependencies.Values(new object?[] { path.Seed, path.Edge.Id,
            path.Edge.Terrain.Relief, Terrain?.LocalHeightNoise, material, textureSpan });
        var geometry = RailwayDependencies.Values(new object?[] { path.Edge.LengthMeters, path.Edge.TrackLimit,
            path.Edge.Tracks, path.Start.Position, path.End.Position, path.Edge.Terrain.Relief,
            path.Rules.MinimumCurveRadiusMeters, path.Rules.MaximumGrade,
            path.Edge.Terrain.FeatureIds.Contains("river"), path.Edge.Terrain.FeatureIds.Contains("lake") });
        var segments = new ConcurrentDictionary<int, Lazy<string>>();
        var blend = Math.Min(RailwayCoordinates.NodeTransitionCells, path.LengthCells / 2);
        return bounds =>
        {
            var inputs = new List<object?> { ground };
            for (var index = 0; index < surface.InfluenceBands.Length; index++)
            {
                if (!Intersects(surface.InfluenceBands[index], bounds)) continue;
                var segment = segments.GetOrAdd(index, part => new Lazy<string>(() =>
                {
                    var begin = part * RailwayCoordinates.SegmentLengthCells;
                    var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
                    var values = new List<object?> { part, geometry, DecorationInputs(Terrain) };
                    if (begin < blend) values.Add(EndpointInputs(path.Start, path.StartLayout));
                    if (end > path.LengthCells - blend) values.Add(EndpointInputs(path.End, path.EndLayout));
                    values.Add(HeightFlattens(part)
                        .Select(item => new object[] { item.Region, item.Pose, item.Origin }).ToArray());
                    return RailwayDependencies.Values(values);
                })).Value;
                inputs.Add(segment);
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RailwayDependencies.Values(inputs))));
        };
    }

    private object? DecorationInputs(TerrainAppearanceProjection? terrain)
    {
        if (terrain is null || !terrain.Decorations.Any(item => decorations[item.SceneModel] is not null)) return null;
        return new object?[] { terrain.DecorationTargetPerSquareMeter,
            terrain.Decorations.Select(item => new object?[] { item.Weight, decorations[item.SceneModel] }).ToArray() };
    }

    private object[] EndpointInputs(NodeMapItem node, NodeLayout layout) =>
        [node.Id, node.Position, node.Terrain.Relief, terrains[node.Terrain.TypeId]?.LocalHeightNoise,
            layout.EntryPoint(path.Edge.Id),
            layout.Template is { } template ? NodeSettlement.Flatten(template.SceneModel.Scene)
                .Select(item => new object[] { item.Region, item.Pose }).ToArray() : Array.Empty<object[]>(),
            DecorationInputs(terrains[node.Terrain.TypeId])!];

    private static bool Intersects(ScenePanBounds left, ScenePanBounds right) =>
        left.Minimum.X <= right.Maximum.X && left.Maximum.X >= right.Minimum.X &&
        left.Minimum.Y <= right.Maximum.Y && left.Maximum.Y >= right.Minimum.Y;

    private ImmutableArray<RailwayTerrain.FlattenPlacement> HeightFlattens(int index) =>
        hasRailwayFlatten ? heightFlattens.GetOrAdd(index, part => new Lazy<ImmutableArray<RailwayTerrain.FlattenPlacement>>(
            () => RailwayTerrain.HeightFlattens(snapshot, path, part))).Value : [];

    internal IEnumerable<(string Key, TerrainHeightCap.Builder Work)> Parts(ScenePanBounds bounds)
    {
        for (var index = 0; index < path.SegmentCount; index++)
        {
            var area = Bounds(index);
            if (area.Minimum.X >= bounds.Maximum.X || area.Maximum.X <= bounds.Minimum.X ||
                area.Minimum.Y >= bounds.Maximum.Y || area.Maximum.Y <= bounds.Minimum.Y) continue;
            yield return (RailwayStreamingCoverage.SegmentKey(index), Part(index));
        }
    }

    private ScenePanBounds Bounds(int index)
    {
        var begin = index * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
        var bounds = path.Bounds(begin, end, default,
            RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks) + RailwayCoordinates.CellFootprintMargin);
        return new(new(bounds.Minimum.X, bounds.Minimum.Z), new(bounds.Maximum.X, bounds.Maximum.Z));
    }

    internal TerrainHeightCap.Builder Part(int index)
    {
        if (index < 0 || index >= path.SegmentCount) throw new ArgumentOutOfRangeException(nameof(index));
        return TerrainHeightCap.CachedPart(parts, index, RailwayStreamingCoverage.SegmentKey(index), completed,
            () => BeginPart(index));
    }

    private TerrainHeightCap.Builder BeginPart(int index)
    {
        var begin = index * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
        var bounds = Bounds(index);
        var fixedFlattens = HeightFlattens(index).Select(FlattenBounds).ToArray();
        var modelMinimum = fixedFlattens.Select(item => item.Top).DefaultIfEmpty(double.PositiveInfinity).Min();
        var width = RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks);
        var maskX = (int)Math.Floor(bounds.Minimum.X / (double)TerrainTile.NearSpacingCells) * TerrainTile.NearSpacingCells;
        var maskZ = (int)Math.Floor(bounds.Minimum.Y / (double)TerrainTile.NearSpacingCells) * TerrainTile.NearSpacingCells;
        var maskWidth = (int)Math.Ceiling(bounds.Maximum.X / (double)TerrainTile.NearSpacingCells) - maskX / TerrainTile.NearSpacingCells;
        var maskDepth = (int)Math.Ceiling(bounds.Maximum.Y / (double)TerrainTile.NearSpacingCells) - maskZ / TerrainTile.NearSpacingCells;
        var decorationMask = hasDecorationFlatten ? new int[checked(maskWidth * maskDepth)] : [];
        var negativeFlatten = 0.0;
        return TerrainHeightCap.Begin(bounds, Sample, Finish);

        double? Sample(GridColumn column)
        {
            var point = new ScenePoint(column.X + .5, 0, column.Z + .5);
            var arc = path.Locate(point);
            if (arc < begin || arc >= end && index != path.SegmentCount - 1) return null;
            var nearest = path.Sample(arc);
            var lateral = (point.X - nearest.Position.X) * nearest.Right.X + (point.Z - nearest.Position.Z) * nearest.Right.Z;
            if (Math.Abs(lateral) > width) return null;
            var top = Math.Floor(surface.ColumnHeight(arc, point, lateral, terrains[path.Edge.Terrain.TypeId],
                out var water, out _)) + 1;
            if (!hasDecorationFlatten || water || surface.TrackDistance(lateral) < RailwayCoordinates.DecorationRootClearance)
                return top;
            var key = RailwayTerrain.ColumnKey(column.X, column.Z);
            var terrain = RailwayTerrain.EndpointTerrain(path, arc, key, id => terrains[id]);
            if (RailwayTerrain.DecorationSlot(path, terrain, key) is not { } slot ||
                decorations[terrain!.Decorations[slot].SceneModel] is not { } flatten) return top;
            negativeFlatten += Math.Min(0, flatten.Height);
            Mark(column.X + flatten.From.X - RailwayTerrain.FlattenBlendRadiusCells, column.Z + flatten.From.Z - RailwayTerrain.FlattenBlendRadiusCells,
                column.X + flatten.To.X + 1 + RailwayTerrain.FlattenBlendRadiusCells, column.Z + flatten.To.Z + 1 + RailwayTerrain.FlattenBlendRadiusCells);
            return top;
        }

        double Finish(int x, int z, double minimum, double globalMinimum)
        {
            foreach (var flattened in fixedFlattens)
                if (x < flattened.Bounds.Maximum.X && x + TerrainTile.NearSpacingCells > flattened.Bounds.Minimum.X &&
                    z < flattened.Bounds.Maximum.Y && z + TerrainTile.NearSpacingCells > flattened.Bounds.Minimum.Y)
                    minimum = double.IsPositiveInfinity(minimum) ? minimum : Math.Min(minimum, flattened.Top);
            if (!hasDecorationFlatten) return minimum;
            var cx = (x - maskX) / TerrainTile.NearSpacingCells;
            var cz = (z - maskZ) / TerrainTile.NearSpacingCells;
            var offset = cz * maskWidth + cx;
            // finishCell visits every fine cell exactly once in row-major order.
            var covered = decorationMask[offset] + (cx > 0 ? decorationMask[offset - 1] : 0) +
                (cz > 0 ? decorationMask[offset - maskWidth] : 0) - (cx > 0 && cz > 0 ? decorationMask[offset - maskWidth - 1] : 0);
            decorationMask[offset] = covered;
            // Decoration roots inherit prior flattening. This bound holds for every placement order,
            // including negative flatten heights and conservatively included rejected decorations.
            return covered > 0 && !double.IsPositiveInfinity(minimum)
                ? Math.Min(minimum, Math.Min(globalMinimum, modelMinimum) + negativeFlatten) : minimum;
        }

        void Mark(double left, double back, double right, double front)
        {
            var x0 = Math.Clamp((int)Math.Floor((left - maskX) / TerrainTile.NearSpacingCells), 0, maskWidth);
            var x1 = Math.Clamp((int)Math.Ceiling((right - maskX) / TerrainTile.NearSpacingCells), 0, maskWidth);
            var z0 = Math.Clamp((int)Math.Floor((back - maskZ) / TerrainTile.NearSpacingCells), 0, maskDepth);
            var z1 = Math.Clamp((int)Math.Ceiling((front - maskZ) / TerrainTile.NearSpacingCells), 0, maskDepth);
            if (x0 >= x1 || z0 >= z1) return;
            decorationMask[z0 * maskWidth + x0]++;
            if (x1 < maskWidth) decorationMask[z0 * maskWidth + x1]--;
            if (z1 < maskDepth) decorationMask[z1 * maskWidth + x0]--;
            if (x1 < maskWidth && z1 < maskDepth) decorationMask[z1 * maskWidth + x1]++;
        }
    }

    private static (ScenePanBounds Bounds, double Top) FlattenBounds(RailwayTerrain.FlattenPlacement placement)
    {
        var region = placement.Region;
        var forward = Vector3.Transform(Vector3.UnitZ, placement.Pose.Rotation);
        var rotation = RailwayCoordinates.Rotation(Vector3.Normalize(new(forward.X, 0, forward.Z)));
        var minimum = new Vector2(float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity);
        var margin = RailwayTerrain.FlattenBlendRadiusCells + RailwayCoordinates.CellFootprintMargin;
        foreach (var x in new[] { region.From.X - margin, region.To.X + 1 + margin })
        foreach (var z in new[] { region.From.Z - margin, region.To.Z + 1 + margin })
        {
            var at = placement.Pose.Position + Vector3.Transform(new((float)x, 0, (float)z), rotation);
            var point = new Vector2((float)(placement.Origin.X + at.X), (float)(placement.Origin.Z + at.Z));
            minimum = Vector2.Min(minimum, point); maximum = Vector2.Max(maximum, point);
        }
        return (new(minimum, maximum), Math.Floor(placement.Pose.Position.Y + placement.Origin.Y + region.Height - 1) + 1);
    }
}

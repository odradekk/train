using System.Numerics;
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
    private readonly Dictionary<TerrainTypeId, double> decorationWeights = [];
    private readonly Dictionary<int, TerrainHeightCap.Builder> parts = [];
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
            if (terrain is not null) decorationWeights[id] = terrain.Decorations.Sum(item => Math.Max(0, item.Weight));
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
        if (parts.TryGetValue(index, out var existing)) return existing;
        if (completed?.Invoke(RailwayStreamingCoverage.SegmentKey(index)) is { } saved)
            return parts[index] = TerrainHeightCap.Builder.FromCompleted(saved);
        var begin = index * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells);
        var bounds = Bounds(index);
        var fixedFlattens = hasRailwayFlatten
            ? RailwayTerrain.HeightFlattens(snapshot, path, index).Select(FlattenBounds).ToArray() : [];
        var modelMinimum = fixedFlattens.Select(item => item.Top).DefaultIfEmpty(double.PositiveInfinity).Min();
        var endpointBlend = Math.Min(RailwayCoordinates.NodeTransitionCells, path.LengthCells / 2);
        var width = RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks);
        var maskX = (int)Math.Floor(bounds.Minimum.X / 4.0) * 4;
        var maskZ = (int)Math.Floor(bounds.Minimum.Y / 4.0) * 4;
        var maskWidth = (int)Math.Ceiling(bounds.Maximum.X / 4.0) - maskX / 4;
        var maskDepth = (int)Math.Ceiling(bounds.Maximum.Y / 4.0) - maskZ / 4;
        var decorationMask = hasDecorationFlatten ? new int[checked(maskWidth * maskDepth)] : [];
        var negativeFlatten = 0.0;
        var work = TerrainHeightCap.Begin(bounds, Sample, Finish);
        parts.Add(index, work);
        return work;

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
            var key = unchecked((long)column.X * 73856093 ^ (long)column.Z * 19349663);
            var terrain = terrains[path.Edge.Terrain.TypeId];
            var material = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, 9);
            if (arc < endpointBlend && material >= arc / endpointBlend) terrain = terrains[path.Start.Terrain.TypeId];
            else if (path.LengthCells - arc < endpointBlend && material >= (path.LengthCells - arc) / endpointBlend)
                terrain = terrains[path.End.Terrain.TypeId];
            if (terrain is null || terrain.Decorations.IsDefaultOrEmpty ||
                RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, 10) >= Math.Clamp(terrain.DecorationTargetPerSquareMeter / 4, 0, 1)) return top;
            var total = decorationWeights[terrain.Id];
            if (total == 0) return top;
            var selection = RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, key, 11) * total;
            var slot = 0;
            while (slot < terrain.Decorations.Length - 1 && selection >= Math.Max(0, terrain.Decorations[slot].Weight))
                selection -= Math.Max(0, terrain.Decorations[slot++].Weight);
            if (decorations[terrain.Decorations[slot].SceneModel] is not { } flatten) return top;
            negativeFlatten += Math.Min(0, flatten.Height);
            Mark(column.X + flatten.From.X - 8, column.Z + flatten.From.Z - 8,
                column.X + flatten.To.X + 9, column.Z + flatten.To.Z + 9);
            return top;
        }

        double Finish(int x, int z, double minimum, double globalMinimum)
        {
            foreach (var flattened in fixedFlattens)
                if (x < flattened.Bounds.Maximum.X && x + 4 > flattened.Bounds.Minimum.X &&
                    z < flattened.Bounds.Maximum.Y && z + 4 > flattened.Bounds.Minimum.Y)
                    minimum = double.IsPositiveInfinity(minimum) ? minimum : Math.Min(minimum, flattened.Top);
            if (!hasDecorationFlatten) return minimum;
            var cx = (x - maskX) / 4;
            var cz = (z - maskZ) / 4;
            var offset = cz * maskWidth + cx;
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
            var x0 = Math.Clamp((int)Math.Floor((left - maskX) / 4), 0, maskWidth);
            var x1 = Math.Clamp((int)Math.Ceiling((right - maskX) / 4), 0, maskWidth);
            var z0 = Math.Clamp((int)Math.Floor((back - maskZ) / 4), 0, maskDepth);
            var z1 = Math.Clamp((int)Math.Ceiling((front - maskZ) / 4), 0, maskDepth);
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
        var margin = 8 + RailwayCoordinates.CellFootprintMargin;
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

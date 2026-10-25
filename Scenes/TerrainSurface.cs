using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class TerrainSurface
{
    private const ulong HeightNoisePurpose = 3;

    internal static double Ground(ulong seed, ulong key, TerrainView view,
        TerrainAppearanceProjection? terrain, ScenePoint point)
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

    internal static double NodeGround(SceneWorldSnapshot snapshot, NodeMapItem node, ScenePoint localPoint) =>
        NodeGround(snapshot, node, localPoint, NodeSettlement.Read(snapshot, node.Id));

    internal static double NodeGround(SceneWorldSnapshot snapshot, NodeMapItem node, ScenePoint localPoint,
        SettlementLayoutProjection? template)
    {
        var terrain = snapshot.Content.FindTerrain(node.Terrain.TypeId) is ContentLookup<TerrainAppearanceProjection>.Found found ? found.Value : null;
        return NodeGround(snapshot, node, localPoint, template, terrain);
    }

    private static double NodeGround(SceneWorldSnapshot snapshot, NodeMapItem node, ScenePoint localPoint,
        SettlementLayoutProjection? template, TerrainAppearanceProjection? terrain)
    {
        var origin = RailwayCoordinates.NodePoint(node);
        var point = new ScenePoint(origin.X + localPoint.X, origin.Y + localPoint.Y, origin.Z + localPoint.Z);
        var raw = Ground(snapshot.Core.World.WorldSeed, node.Id.Value, node.Terrain, terrain, point);
        return template is not null ? NodeSettlement.FlattenGround(template, localPoint, raw) : raw;
    }

    internal static double NodeHeight(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        ScenePoint point, NodeScene.RailBed? rail)
    {
        var raw = NodeGround(snapshot, node, point, layout.Template);
        return NodeHeight(raw, layout, point, rail);
    }

    internal static double NodeHeight(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        ScenePoint point, NodeScene.RailBed? rail, TerrainAppearanceProjection? terrain) =>
        NodeHeight(NodeGround(snapshot, node, point, layout.Template, terrain), layout, point, rail);

    private static double NodeHeight(double raw, NodeLayout layout, ScenePoint point, NodeScene.RailBed? rail)
    {
        if (layout.Lair is { } lair)
            raw = NodeSettlement.FlattenGround(lair.Model.Scene, lair.ShiftX, lair.ShiftZ, point, raw);
        if (rail is not null)
        {
            var blend = Math.Clamp((rail.Distance - RailwayCoordinates.BedBaseHalfWidth) / RailwayCoordinates.BedBlendWidth, 0, 1);
            raw = RailwayTerrain.BedHeight(rail.Height, rail.Distance) * (1 - blend) + raw * blend;
        }
        return raw;
    }

    internal sealed class Railway(RailwayPath path, Func<TerrainTypeId, TerrainAppearanceProjection?> terrain)
    {
        internal const double FadeCells = 16 * RailwayCoordinates.CellsPerMeter;
        private readonly double endpointBlend = Math.Min(RailwayCoordinates.NodeTransitionCells, path.LengthCells / 2);
        private readonly ScenePoint startPoint = path.Sample(0).Position;
        private readonly ScenePoint endPoint = path.Sample(path.LengthCells).Position;
        private readonly Lazy<ScenePoint> startEntry = new(() => path.StartLayout.EntryPoint(path.Edge.Id));
        private readonly Lazy<ScenePoint> endEntry = new(() => path.EndLayout.EntryPoint(path.Edge.Id));
        private readonly Lazy<ScenePanBounds[]> influenceBands = new(() => Enumerable.Range(0, path.SegmentCount).Select(index =>
        {
            var begin = index * RailwayCoordinates.SegmentLengthCells;
            var bounds = path.Bounds(begin, Math.Min(path.LengthCells, begin + RailwayCoordinates.SegmentLengthCells), default,
                RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks) + FadeCells + RailwayCoordinates.CellFootprintMargin);
            return new ScenePanBounds(new(bounds.Minimum.X, bounds.Minimum.Z), new(bounds.Maximum.X, bounds.Maximum.Z));
        }).ToArray());
        internal ScenePanBounds[] InfluenceBands => influenceBands.Value;
        internal (double Center, double First, double Last, double Level)[] WaterRegions { get; } = PrepareWater(path);

        internal double FarHeight(ScenePoint point)
        {
            var appearance = terrain(path.Edge.Terrain.TypeId);
            var ground = Ground(path.Seed, path.Edge.Id.Value, path.Edge.Terrain, appearance, point);
            if (!InfluenceBands.Any(bounds => point.X >= bounds.Minimum.X && point.X <= bounds.Maximum.X &&
                point.Z >= bounds.Minimum.Y && point.Z <= bounds.Maximum.Y)) return ground;
            var arc = path.Locate(point);
            var nearest = path.Sample(arc);
            var dx = point.X - nearest.Position.X;
            var dz = point.Z - nearest.Position.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            var blend = Math.Clamp((distance - RailwayCoordinates.TerrainHalfWidth(path.Edge.Tracks)) / FadeCells, 0, 1);
            if (blend == 1) return ground;
            var lateral = dx * nearest.Right.X + dz * nearest.Right.Z;
            var near = ColumnHeight(arc, point, lateral, appearance, out _, out _);
            var weight = 1 - blend * blend * (3 - 2 * blend);
            return ground + weight * (near - ground);
        }

        internal double ColumnHeight(double arc, ScenePoint point, double lateral,
            TerrainAppearanceProjection? appearance, out bool water, out bool tunnel)
        {
            var height = Height(arc, point, appearance);
            water = Water(arc);
            if (water) height = Math.Min(height, WaterLevel(arc) - RailwayCoordinates.WaterDepth);
            var distance = TrackDistance(lateral);
            tunnel = path.Edge.Tracks > 0 && TunnelAt(arc, appearance);
            if (path.Edge.Tracks > 0 && !water && !tunnel)
            {
                var bed = RailwayTerrain.BedHeight(path.Sample(arc).Position.Y, distance);
                var cutting = height > bed;
                var weight = Math.Clamp(Math.Min(arc, path.LengthCells - arc) / endpointBlend, 0, 1);
                var flat = cutting
                    ? RailwayCoordinates.BedBaseHalfWidth + (RailwayCoordinates.TrackSpacing / 2 - RailwayCoordinates.BedBaseHalfWidth) * weight
                    : RailwayCoordinates.BedBaseHalfWidth;
                var blend = Math.Clamp((distance - flat) / RailwayCoordinates.BedBlendWidth, 0, 1);
                height = bed * (1 - blend) + height * blend;
            }
            return height;
        }

        internal double TrackDistance(double offset)
        {
            var distance = double.PositiveInfinity;
            for (var track = 1; track <= path.Edge.Tracks; track++)
                distance = Math.Min(distance, Math.Abs(offset - RailwayCoordinates.TrackOffset(track, path.Edge.Tracks)));
            return distance;
        }

        internal bool Water(double arc)
        {
            foreach (var region in WaterRegions)
                if (arc >= region.First && arc <= region.Last) return true;
            return false;
        }

        internal double WaterLevel(double arc)
        {
            foreach (var region in WaterRegions)
                if (arc >= region.First && arc <= region.Last) return region.Level;
            throw new InvalidOperationException("No water region contains the arc.");
        }

        internal bool TunnelAt(double arc, TerrainAppearanceProjection? appearance)
        {
            if (path.Edge.Tracks == 0 || Water(arc)) return false;
            var rail = path.Sample(arc).Position.Y;
            var shoulder = RailwayCoordinates.TunnelShoulder(path.Edge.Tracks);
            var floor = Math.Floor(Height(arc, path.Sample(arc).Position, appearance));
            floor = Math.Min(floor, Math.Floor(Height(arc, path.Sample(arc, shoulder).Position, appearance)));
            floor = Math.Min(floor, Math.Floor(Height(arc, path.Sample(arc, -shoulder).Position, appearance)));
            for (var track = 1; track <= path.Edge.Tracks; track++)
                floor = Math.Min(floor, Math.Floor(Height(arc, path.Sample(arc, RailwayCoordinates.TrackOffset(track, path.Edge.Tracks)).Position, appearance)));
            return floor >= rail + RailwayCoordinates.TunnelCrownCells;
        }

        private static (double Center, double First, double Last, double Level)[] PrepareWater(RailwayPath path)
        {
            var features = new List<(double Center, double HalfWidth)>();
            if (path.Edge.Terrain.FeatureIds.Contains("river")) features.Add((path.LengthCells *
                (0.5 + 0.2 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, 20)), 12));
            if (path.Edge.Terrain.FeatureIds.Contains("lake")) features.Add((path.LengthCells *
                (0.3 + 0.5 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, 21)), 48));
            var regions = features.Select(feature =>
            {
                var first = Math.Max(0, feature.Center - feature.HalfWidth);
                var last = Math.Min(path.LengthCells, feature.Center + feature.HalfWidth);
                var level = Math.Min(path.Sample(first).Position.Y, Math.Min(path.Sample(last).Position.Y,
                    path.Sample(Math.Clamp(path.LengthCells / 2, first, last)).Position.Y)) - RailwayCoordinates.WaterSurfaceOffset;
                return (feature.Center, First: first, Last: last, Level: level);
            }).ToArray();
            if (regions.Length == 2 && Math.Max(regions[0].First, regions[1].First) <= Math.Min(regions[0].Last, regions[1].Last))
            {
                var level = Math.Min(regions[0].Level, regions[1].Level);
                regions[0].Level = level; regions[1].Level = level;
            }
            return regions;
        }

        internal double Height(double arc, ScenePoint point, TerrainAppearanceProjection? appearance)
        {
            var railHeight = path.Sample(arc).Position.Y;
            var height = Ground(path.Seed, path.Edge.Id.Value, path.Edge.Terrain, appearance, point);
            if (arc < endpointBlend)
            {
                var weight = arc / endpointBlend;
                height = NodeHeight(path.Start, startPoint, point) * (1 - weight) + height * weight;
            }
            if (path.LengthCells - arc < endpointBlend)
            {
                var weight = (path.LengthCells - arc) / endpointBlend;
                height = NodeHeight(path.End, endPoint, point) * (1 - weight) + height * weight;
            }
            return railHeight + height;
        }

        private double NodeHeight(NodeMapItem node, ScenePoint endpoint, ScenePoint point)
        {
            var mapOrigin = RailwayCoordinates.NodePoint(node);
            var entry = (node.Id == path.Start.Id ? startEntry : endEntry).Value;
            var local = new ScenePoint(entry.X + point.X - endpoint.X, entry.Y + point.Y - endpoint.Y, entry.Z + point.Z - endpoint.Z);
            var raw = Ground(path.Seed, node.Id.Value, node.Terrain, terrain(node.Terrain.TypeId),
                new(mapOrigin.X + local.X, mapOrigin.Y + local.Y, mapOrigin.Z + local.Z));
            var template = node.Id == path.Start.Id ? path.StartLayout.Template : path.EndLayout.Template;
            return template is not null ? NodeSettlement.FlattenGround(template, local, raw) : raw;
        }
    }
}

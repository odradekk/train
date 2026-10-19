using System.Numerics;
using Train.Content.Contracts;

namespace Train.Scenes;

internal static class NodeLair
{
    private const double Clearance = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;

    internal static (int X, int Z) Translation(NodeLayout layout, DesignSceneProjection scene)
    {
        var footprint = Footprint(scene);
        if (footprint is null) return (0, 0);
        var samples = Samples(layout);
        var right = Vector3.Cross(Vector3.UnitY, layout.StationForward);
        var rx = (int)Math.Round(right.X);
        var rz = (int)Math.Round(right.Z);
        var innerLeft = (int)layout.Bounds.Minimum.X + NodeLayout.GroundMargin;
        var innerRight = (int)layout.Bounds.Maximum.X - NodeLayout.GroundMargin;
        var innerBack = (int)layout.Bounds.Minimum.Y + NodeLayout.GroundMargin;
        var innerFront = (int)layout.Bounds.Maximum.Y - NodeLayout.GroundMargin;
        var limit = Math.Max(0, Math.Max(innerRight - innerLeft, innerFront - innerBack)) +
            footprint.Value.Width + footprint.Value.Depth + 8;
        foreach (var sign in new[] { 1, -1 })
        {
            var stepX = rx * sign;
            var stepZ = rz * sign;
            for (var step = sign > 0 ? 0 : 1; step <= limit; step++)
            {
                var moved = footprint.Value.Translate(stepX * step, stepZ * step);
                if (Past(moved, stepX, stepZ, innerLeft, innerRight, innerBack, innerFront)) break;
                if (moved.Fits(innerLeft, innerRight, innerBack, innerFront) && moved.Clears(samples))
                    return (stepX * step, stepZ * step);
            }
        }
        // The rail-bed voxel filter drops cells that still overlap a track.
        return (0, 0);
    }

    private static List<(double X, double Z)> Samples(NodeLayout layout)
    {
        var routes = layout.Entries.SelectMany(entry => Enumerable.Range(1, entry.TrackLimit)
                .SelectMany(count => layout.EntryRoutes(entry, count)))
            .Concat(layout.StationRoutes(true)).Concat(layout.TurnRoutes());
        var samples = new List<(double X, double Z)>();
        foreach (var (sample, length) in routes)
            for (var arc = 0.0; arc < length + 2; arc += 2)
            {
                var point = sample(Math.Min(arc, length)).Position;
                samples.Add((point.X, point.Z));
            }
        return samples;
    }

    private static CellRect? Footprint(DesignSceneProjection scene)
    {
        double? minX = null, maxX = null, minZ = null, maxZ = null;
        void Include(double x, double z)
        {
            minX = minX is null ? x : Math.Min(minX.Value, x);
            maxX = maxX is null ? x : Math.Max(maxX.Value, x);
            minZ = minZ is null ? z : Math.Min(minZ.Value, z);
            maxZ = maxZ is null ? z : Math.Max(maxZ.Value, z);
        }
        if (scene.OccupiedBounds is { } occupied)
        {
            Include(occupied.Minimum.X, occupied.Minimum.Z);
            Include(occupied.Maximum.X + 1, occupied.Maximum.Z + 1);
        }
        foreach (var (region, pose) in NodeSettlement.Flatten(scene))
            foreach (var corner in new[]
            {
                NodeSettlement.Point(pose, region.From.X, 0, region.From.Z),
                NodeSettlement.Point(pose, region.To.X + 1, 0, region.From.Z),
                NodeSettlement.Point(pose, region.From.X, 0, region.To.Z + 1),
                NodeSettlement.Point(pose, region.To.X + 1, 0, region.To.Z + 1)
            }) Include(corner.X, corner.Z);
        if (minX is null || maxX is null || minZ is null || maxZ is null) return null;
        return new((int)Math.Floor(minX.Value), (int)Math.Ceiling(maxX.Value),
            (int)Math.Floor(minZ.Value), (int)Math.Ceiling(maxZ.Value));
    }

    private static bool Past(CellRect moved, int stepX, int stepZ, int innerLeft, int innerRight, int innerBack, int innerFront)
    {
        if (stepX > 0 && moved.Left - 8 >= innerRight) return true;
        if (stepX < 0 && moved.Right + 8 <= innerLeft) return true;
        if (stepZ > 0 && moved.Back - 8 >= innerFront) return true;
        if (stepZ < 0 && moved.Front + 8 <= innerBack) return true;
        return false;
    }

    private readonly record struct CellRect(int Left, int Right, int Back, int Front)
    {
        internal int Width => Right - Left;
        internal int Depth => Front - Back;
        internal CellRect Translate(int x, int z) => new(Left + x, Right + x, Back + z, Front + z);
        internal bool Fits(int innerLeft, int innerRight, int innerBack, int innerFront) =>
            Left - 8 >= innerLeft && Right + 8 <= innerRight && Back - 8 >= innerBack && Front + 8 <= innerFront;

        internal bool Clears(List<(double X, double Z)> samples)
        {
            var limit = Clearance * Clearance;
            foreach (var sample in samples)
            {
                var dx = sample.X < Left ? Left - sample.X : sample.X > Right ? sample.X - Right : 0;
                var dz = sample.Z < Back ? Back - sample.Z : sample.Z > Front ? sample.Z - Front : 0;
                if (dx * dx + dz * dz + 1e-4 < limit) return false;
            }
            return true;
        }
    }
}

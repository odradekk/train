using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class NodeLair
{
    private const double Clearance = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;

    internal static (int X, int Z) Translation(NodeLayout layout, DesignSceneProjection scene)
    {
        if (NodeSettlement.Bounds(scene) is not { } footprint) return (0, 0);
        var samples = Samples(layout);
        var right = Vector3.Cross(Vector3.UnitY, layout.StationForward);
        var rx = (int)Math.Round(right.X);
        var rz = (int)Math.Round(right.Z);
        var margin = new Vector2(NodeLayout.GroundMargin, NodeLayout.GroundMargin);
        var inner = new ScenePanBounds(layout.Bounds.Minimum + margin, layout.Bounds.Maximum - margin);
        var span = (int)Math.Round(Math.Abs(rx) >= Math.Abs(rz)
            ? inner.Maximum.X - inner.Minimum.X : inner.Maximum.Y - inner.Minimum.Y);
        foreach (var sign in new[] { 1, -1 })
            for (var step = sign > 0 ? 0 : 1; step <= span; step++)
            {
                var shift = (X: rx * sign * step, Z: rz * sign * step);
                var region = Region(footprint, shift);
                if (Fits(region, inner) && Clears(region, samples)) return shift;
            }
        // The rail-bed voxel filter drops cells that still overlap a track.
        return (0, 0);
    }

    private static List<(double X, double Z)> Samples(NodeLayout layout)
    {
        var routes = layout.Entries.SelectMany(entry => Enumerable.Range(1, entry.TrackLimit)
                .SelectMany(count => layout.EntryRoutes(entry, count)))
            .Concat(layout.StationRoutes(true)).Concat(layout.AllTurnRoutes());
        var samples = new List<(double X, double Z)>();
        foreach (var (sample, length) in routes)
            for (var arc = 0.0; arc < length + 2; arc += 2)
            {
                var point = sample(Math.Min(arc, length)).Position;
                samples.Add((point.X, point.Z));
            }
        return samples;
    }

    private static ScenePanBounds Region(ScenePanBounds footprint, (int X, int Z) shift) => new(
        footprint.Minimum + new Vector2(shift.X, shift.Z), footprint.Maximum + new Vector2(shift.X, shift.Z));

    private static bool Fits(ScenePanBounds region, ScenePanBounds inner)
    {
        var skirt = new Vector2(8, 8);
        return region.Minimum.X - skirt.X >= inner.Minimum.X && region.Minimum.Y - skirt.Y >= inner.Minimum.Y &&
            region.Maximum.X + skirt.X <= inner.Maximum.X && region.Maximum.Y + skirt.Y <= inner.Maximum.Y;
    }

    private static bool Clears(ScenePanBounds region, List<(double X, double Z)> samples)
    {
        var limit = Clearance * Clearance;
        foreach (var sample in samples)
        {
            var dx = sample.X < region.Minimum.X ? region.Minimum.X - sample.X :
                sample.X > region.Maximum.X ? sample.X - region.Maximum.X : 0;
            var dz = sample.Z < region.Minimum.Y ? region.Minimum.Y - sample.Z :
                sample.Z > region.Maximum.Y ? sample.Z - region.Maximum.Y : 0;
            if (dx * dx + dz * dz + 1e-4 < limit) return false;
        }
        return true;
    }
}

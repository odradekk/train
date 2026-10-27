using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.SceneScale;

internal static partial class Probe
{
    internal sealed record LocalRailway(int segment_count, string sha256, double quantization_cells,
        string[] normalized_segments, bool parkingOutside, ParkingExclusion parkingExclusion);
    internal sealed record ParkingExclusion(int? stationPlotNumber, Vector2? axis,
        double? minimumLateral, double? focusLateral, bool outside);

    internal static LocalRailway CaptureRailway(ISceneFrame frame, Vector2 focus)
    {
        var segments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in frame.DebugLines)
        {
            double x = line.From.X + frame.Origin.X, y = line.From.Y + frame.Origin.Y,
                z = line.From.Z + frame.Origin.Z;
            double dx = (double)line.To.X - line.From.X, dy = (double)line.To.Y - line.From.Y,
                dz = (double)line.To.Z - line.From.Z;
            var px = x - focus.X;
            var pz = z - focus.Y;
            var a = dx * dx + dz * dz;
            var c = px * px + pz * pz - 192 * 192;
            double start = 0, end = 1;
            if (a == 0)
            {
                if (c > 0) continue;
            }
            else
            {
                var b = 2 * (px * dx + pz * dz);
                var discriminant = b * b - 4 * a * c;
                if (discriminant < 0) continue;
                var root = Math.Sqrt(discriminant);
                start = Math.Max(0, (-b - root) / (2 * a));
                end = Math.Min(1, (-b + root) / (2 * a));
                if (start > end) continue;
            }
            string Point(double t) => $"{Q(x + dx * t)},{Q(y + dy * t)},{Q(z + dz * t)}";
            var first = Point(start);
            var last = Point(end);
            segments.Add(StringComparer.Ordinal.Compare(first, last) <= 0 ? first + ";" + last : last + ";" + first);
        }
        var normalized = segments.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var parking = ProveParkingOutside(frame, focus);
        return new(normalized.Length, Digest(normalized), .001, normalized, parking.outside, parking);
    }

    private static ParkingExclusion ProveParkingOutside(ISceneFrame frame, Vector2 focus)
    {
        var station = frame.NodeOverview?.Plots
            .Where(plot => plot.Size is PlotSize.SmallStation or PlotSize.LargeStation)
            .OrderBy(plot => plot.TemplatePlotNumber).FirstOrDefault();
        if (station is null || station.Corners.Length != 4)
            return new(station?.TemplatePlotNumber, null, null, null, false);
        var direction = station.Corners[1] - station.Corners[0];
        if (direction.LengthSquared() == 0) return new(station.TemplatePlotNumber, null, null, null, false);
        var axis = Vector2.Normalize(direction);
        var across = new Vector2(-axis.Y, axis.X);
        var minimum = station.Corners.Min(point =>
            (point.X + frame.Origin.X) * across.X + (point.Y + frame.Origin.Z) * across.Y);
        var focusLateral = (double)focus.X * across.X + (double)focus.Y * across.Y;
        // NodeLayout parks beyond template lateral.Min; this station's minimum is an upper bound on it.
        return new(station.TemplatePlotNumber, axis, minimum, focusLateral, minimum < focusLateral - 192);
    }
}

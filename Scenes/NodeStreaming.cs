using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class NodeStreaming
{
    // Provisional budgets; their gameplay values require confirmation after measurement.
    internal const double NearCells = 32 * RailwayCoordinates.CellsPerMeter;
    internal const double ViewCells = 64 * RailwayCoordinates.CellsPerMeter;
    internal const double PrefetchCells = 80 * RailwayCoordinates.CellsPerMeter;
    internal const double RetainCells = 96 * RailwayCoordinates.CellsPerMeter;
    internal const double PathSampleCells = 16 * RailwayCoordinates.CellsPerMeter;
    internal const double PrefetchLeadSeconds = .5;
    internal const double MaximumPrefetchLeadCells = 160 * RailwayCoordinates.CellsPerMeter;
    internal const int AdmissionVertexLimit = 48_000;
    internal const int AdmissionVisualLimit = 64;

    internal static double AdvanceCells(double mappedSpeedCells, double rate) =>
        Math.Clamp(mappedSpeedCells * rate * PrefetchLeadSeconds, 0, MaximumPrefetchLeadCells);

    // A Location view shows the settlement as a whole, so its prefetch circle must cover the template and parking lines.
    internal static NodeRange SettlementPrefetch(NodeLayout layout)
    {
        var bounds = NodeSettlement.Bounds(layout.Template!);
        var minimum = bounds.Minimum;
        var maximum = bounds.Maximum;
        foreach (var line in layout.Parking)
        {
            var end = RailwayCoordinates.Add(line.Start, line.Forward * (float)line.Length);
            minimum = Vector2.Min(minimum, Vector2.Min(new((float)line.Start.X, (float)line.Start.Z), new((float)end.X, (float)end.Z)));
            maximum = Vector2.Max(maximum, Vector2.Max(new((float)line.Start.X, (float)line.Start.Z), new((float)end.X, (float)end.Z)));
        }
        var center = (minimum + maximum) / 2;
        return new(new(center.X, 0, center.Y), Vector2.Distance(minimum, maximum) / 2);
    }

    internal static (double Distance, double Progress) PathPriority(SceneBounds bounds, IReadOnlyList<ScenePoint> path)
    {
        if (path.Count == 0) return (double.PositiveInfinity, 0);
        var distance = new NodeRange(path[0], 0).DistanceSquared(bounds);
        if (distance == 0) return (0, 0);
        for (var i = 1; i < path.Count; i++)
        {
            distance = Math.Min(distance, new NodeRange(path[i], 0).DistanceSquared(bounds));
            var from = path[i - 1];
            var dx = path[i].X - from.X;
            var dz = path[i].Z - from.Z;
            double enter = 0, exit = 1;
            for (var axis = 0; axis < 2; axis++)
            {
                var start = axis == 0 ? from.X : from.Z;
                var delta = axis == 0 ? dx : dz;
                var minimum = axis == 0 ? bounds.Minimum.X : bounds.Minimum.Z;
                var maximum = axis == 0 ? bounds.Maximum.X : bounds.Maximum.Z;
                if (delta == 0)
                {
                    if (start < minimum || start > maximum) { enter = 1; exit = 0; break; }
                }
                else
                {
                    var first = (minimum - start) / delta;
                    var last = (maximum - start) / delta;
                    enter = Math.Max(enter, Math.Min(first, last));
                    exit = Math.Min(exit, Math.Max(first, last));
                }
            }
            if (enter <= exit) return (0, i - 1 + enter);
            var lengthSquared = dx * dx + dz * dz;
            for (var corner = 0; corner < 4 && lengthSquared > 0; corner++)
            {
                var x = corner % 2 == 0 ? bounds.Minimum.X : bounds.Maximum.X;
                var z = corner < 2 ? bounds.Minimum.Z : bounds.Maximum.Z;
                var along = Math.Clamp(((x - from.X) * dx + (z - from.Z) * dz) / lengthSquared, 0, 1);
                var offsetX = from.X + along * dx - x;
                var offsetZ = from.Z + along * dz - z;
                distance = Math.Min(distance, offsetX * offsetX + offsetZ * offsetZ);
            }
        }
        return (distance, 0);
    }

    internal static bool OverlapsTerrain(SceneBounds bounds, IEnumerable<GridColumn> chunks)
    {
        if (chunks is IReadOnlySet<GridColumn> indexed)
        {
            var size = VoxelGrid.ChunkSize;
            for (var z = (int)Math.Floor(bounds.Minimum.Z / size) * size; z < bounds.Maximum.Z; z += size)
            for (var x = (int)Math.Floor(bounds.Minimum.X / size) * size; x < bounds.Maximum.X; x += size)
                if (indexed.Contains(new(x, z))) return true;
            return false;
        }
        return chunks.Any(from => bounds.Minimum.X < from.X + VoxelGrid.ChunkSize && bounds.Maximum.X > from.X &&
            bounds.Minimum.Z < from.Z + VoxelGrid.ChunkSize && bounds.Maximum.Z > from.Z);
    }
}

internal readonly record struct NodeRange(ScenePoint Focus, double RadiusCells)
{
    internal NodeRange Around(double radius) => new(Focus, radius);

    internal bool Intersects(SceneBounds bounds)
    {
        return DistanceSquared(bounds) <= RadiusCells * RadiusCells;
    }

    internal bool Intersects(SceneBounds bounds, ScenePose pose)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        foreach (var x in new[] { bounds.Minimum.X, bounds.Maximum.X })
        foreach (var y in new[] { bounds.Minimum.Y, bounds.Maximum.Y })
        foreach (var z in new[] { bounds.Minimum.Z, bounds.Maximum.Z })
        {
            var point = pose.Position + Vector3.Transform(new(x, y, z), pose.Rotation);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }
        return Intersects(new SceneBounds(minimum, maximum));
    }

    internal bool Intersects(GridColumn from) => Intersects(new SceneBounds(
        new(from.X, -128, from.Z), new(from.X + VoxelGrid.ChunkSize, 128, from.Z + VoxelGrid.ChunkSize)));

    internal (int Left, int Right, int Back, int Front) Window(NodeLayout layout)
    {
        var size = VoxelGrid.ChunkSize;
        var left = (int)layout.Bounds.Minimum.X;
        var back = (int)layout.Bounds.Minimum.Y;
        var right = (int)layout.Bounds.Maximum.X;
        var front = (int)layout.Bounds.Maximum.Y;
        return (
            Math.Clamp(left + (int)Math.Floor((Focus.X - RadiusCells - left) / size) * size, left, right),
            Math.Clamp(left + ((int)Math.Floor((Focus.X + RadiusCells - left) / size) + 1) * size, left, right),
            Math.Clamp(back + (int)Math.Floor((Focus.Z - RadiusCells - back) / size) * size, back, front),
            Math.Clamp(back + ((int)Math.Floor((Focus.Z + RadiusCells - back) / size) + 1) * size, back, front));
    }

    internal IEnumerable<GridColumn> Chunks(NodeLayout layout)
    {
        var (left, right, back, front) = Window(layout);
        for (var z = back; z < front; z += VoxelGrid.ChunkSize)
        for (var x = left; x < right; x += VoxelGrid.ChunkSize)
        {
            var from = new GridColumn(x, z);
            if (Intersects(from)) yield return from;
        }
    }

    internal double DistanceSquared(SceneBounds bounds)
    {
        var dx = Math.Max(bounds.Minimum.X - Focus.X, Math.Max(0, Focus.X - bounds.Maximum.X));
        var dz = Math.Max(bounds.Minimum.Z - Focus.Z, Math.Max(0, Focus.Z - bounds.Maximum.Z));
        return dx * dx + dz * dz;
    }
}

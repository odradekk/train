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
    internal const int AdmissionVertexLimit = 96_000;
    internal const int AdmissionVisualLimit = 64;

    internal static double AdvanceCells(double mappedSpeedCells, double rate) =>
        Math.Clamp(mappedSpeedCells * rate * PrefetchLeadSeconds, 0, MaximumPrefetchLeadCells);

    internal static bool OverlapsTerrain(SceneBounds bounds, IEnumerable<GridColumn> chunks) => chunks.Any(from =>
        bounds.Minimum.X < from.X + VoxelGrid.ChunkSize && bounds.Maximum.X > from.X &&
        bounds.Minimum.Z < from.Z + VoxelGrid.ChunkSize && bounds.Maximum.Z > from.Z);
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

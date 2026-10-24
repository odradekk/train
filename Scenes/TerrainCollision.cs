using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class TerrainCollision
{
    internal const double SpacingCells = 4;

    private sealed record Samples(int MinX, int MinZ, double OriginHeight, int Width, int Depth,
        ImmutableArray<float> Heights)
    {
        internal TerrainCollisionPatch At(SceneObjectId id, ScenePoint partOrigin, ScenePoint frameOrigin) =>
            new(id, new((float)(partOrigin.X + MinX - frameOrigin.X),
                (float)(OriginHeight - frameOrigin.Y), (float)(partOrigin.Z + MinZ - frameOrigin.Z)),
                Width, Depth, SpacingCells, Heights);
    }

    internal sealed class PatchCache
    {
        private readonly Dictionary<string, Entry> entries = [];
        private readonly record struct Entry(ImmutableDictionary<GridColumn, double> Heights, ScenePoint PartOrigin,
            ScenePoint FrameOrigin, Samples Samples, TerrainCollisionPatch Patch);

        internal TerrainCollisionPatch? Get(string key, Func<SceneObjectId> id, ScenePoint partOrigin, ScenePoint frameOrigin,
            ImmutableDictionary<GridColumn, double> heights)
        {
            if (heights.Count == 0) { entries.Remove(key); return null; }
            if (entries.TryGetValue(key, out var cached) && ReferenceEquals(cached.Heights, heights) &&
                cached.PartOrigin.Equals(partOrigin))
            {
                if (cached.FrameOrigin.Equals(frameOrigin)) return cached.Patch;
                var shifted = cached.Samples.At(cached.Patch.Id, partOrigin, frameOrigin);
                entries[key] = cached with { FrameOrigin = frameOrigin, Patch = shifted };
                return shifted;
            }
            var samples = Sample(heights);
            var patch = samples.At(id(), partOrigin, frameOrigin);
            entries[key] = new(heights, partOrigin, frameOrigin, samples, patch);
            return patch;
        }

        internal void Retain(IReadOnlySet<string> keys)
        {
            foreach (var key in entries.Keys.Where(item => !keys.Contains(item)).ToArray()) entries.Remove(key);
        }

        internal void Remove(string key) => entries.Remove(key);
    }

    internal static TerrainCollisionPatch? Patch(SceneObjectId id, ScenePoint partOrigin, ScenePoint frameOrigin,
        ImmutableDictionary<GridColumn, double> heights) =>
        heights.Count == 0 ? null : Sample(heights).At(id, partOrigin, frameOrigin);

    private static Samples Sample(ImmutableDictionary<GridColumn, double> heights)
    {
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minZ = int.MaxValue;
        var maxZ = int.MinValue;
        foreach (var column in heights.Keys)
        {
            if (column.X < minX) minX = column.X;
            if (column.X > maxX) maxX = column.X;
            if (column.Z < minZ) minZ = column.Z;
            if (column.Z > maxZ) maxZ = column.Z;
        }
        var columns = maxX - minX + 1;
        var rows = maxZ - minZ + 1;
        var nearestZ = new int[columns * rows];
        Array.Fill(nearestZ, -1);
        foreach (var column in heights.Keys)
            nearestZ[(column.X - minX) * rows + column.Z - minZ] = column.Z - minZ;
        for (var x = 0; x < columns; x++)
        {
            var offset = x * rows;
            var previous = -1;
            for (var z = 0; z < rows; z++)
            {
                if (nearestZ[offset + z] >= 0) previous = z;
                else nearestZ[offset + z] = previous;
            }
            var next = -1;
            for (var z = rows - 1; z >= 0; z--)
            {
                var previousZ = nearestZ[offset + z];
                if (previousZ == z) next = z;
                // 等距时保留前向扫描得到的较小 Z。
                else if (next >= 0 && (previousZ < 0 || next - z < z - previousZ))
                    nearestZ[offset + z] = next;
            }
        }
        var originHeight = Height(0, 0);
        var width = (columns - 1) / (int)SpacingCells + 1;
        var depth = (rows - 1) / (int)SpacingCells + 1;
        var samples = ImmutableArray.CreateBuilder<float>(width * depth);
        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
                samples.Add((float)(Height(x * (int)SpacingCells, z * (int)SpacingCells) - originHeight));
        return new(minX, minZ, originHeight, width, depth, samples.DrainToImmutable());

        double Height(int x, int z)
        {
            if (heights.TryGetValue(new(minX + x, minZ + z), out var known)) return known;
            var bestDistance = long.MaxValue;
            var bestX = 0;
            var bestZ = 0;
            for (var sourceX = 0; sourceX < columns; sourceX++)
            {
                var sourceZ = nearestZ[sourceX * rows + z];
                if (sourceZ < 0) continue;
                var dx = (long)sourceX - x;
                var dz = (long)sourceZ - z;
                var distance = dx * dx + dz * dz;
                // X 升序，严格比较保留等距时较小的 X。
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestX = sourceX;
                    bestZ = sourceZ;
                }
            }
            return heights[new(minX + bestX, minZ + bestZ)];
        }
    }
}

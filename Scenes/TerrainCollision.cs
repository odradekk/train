using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class TerrainCollision
{
    internal const double SpacingCells = 4;

    internal sealed class PatchCache
    {
        private readonly Dictionary<string, Entry> entries = [];
        private readonly record struct Entry(ImmutableDictionary<GridColumn, double> Heights, ScenePoint PartOrigin,
            ScenePoint FrameOrigin, TerrainCollisionPatch Patch);

        internal TerrainCollisionPatch? Get(string key, Func<SceneObjectId> id, ScenePoint partOrigin, ScenePoint frameOrigin,
            ImmutableDictionary<GridColumn, double> heights)
        {
            if (entries.TryGetValue(key, out var cached) && ReferenceEquals(cached.Heights, heights) &&
                cached.PartOrigin.Equals(partOrigin) && cached.FrameOrigin.Equals(frameOrigin))
                return cached.Patch;
            var patch = Patch(id(), partOrigin, frameOrigin, heights);
            if (patch is null)
            {
                entries.Remove(key);
                return null;
            }
            entries[key] = new(heights, partOrigin, frameOrigin, patch);
            return patch;
        }

        internal void Retain(IReadOnlySet<string> keys)
        {
            foreach (var key in entries.Keys.Where(item => !keys.Contains(item)).ToArray()) entries.Remove(key);
        }
    }

    internal static TerrainCollisionPatch? Patch(SceneObjectId id, ScenePoint partOrigin, ScenePoint frameOrigin,
        ImmutableDictionary<GridColumn, double> heights)
    {
        if (heights.Count == 0) return null;
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
        var originHeight = Height(heights, minX, minZ);
        var width = (maxX - minX) / (int)SpacingCells + 1;
        var depth = (maxZ - minZ) / (int)SpacingCells + 1;
        var samples = ImmutableArray.CreateBuilder<float>(width * depth);
        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
                samples.Add((float)(Height(heights, minX + x * (int)SpacingCells, minZ + z * (int)SpacingCells) - originHeight));
        return new(id, new(
            (float)(partOrigin.X + minX - frameOrigin.X),
            (float)(originHeight - frameOrigin.Y),
            (float)(partOrigin.Z + minZ - frameOrigin.Z)), width, depth, SpacingCells, samples.DrainToImmutable());
    }

    private static double Height(ImmutableDictionary<GridColumn, double> heights, int x, int z)
    {
        if (heights.TryGetValue(new(x, z), out var known)) return known;
        var best = 0d;
        var bestDistance = long.MaxValue;
        var bestX = int.MaxValue;
        var bestZ = int.MaxValue;
        foreach (var (column, height) in heights)
        {
            var dx = (long)column.X - x;
            var dz = (long)column.Z - z;
            var distance = dx * dx + dz * dz;
            if (distance < bestDistance || distance == bestDistance && (column.X < bestX || column.X == bestX && column.Z < bestZ))
            {
                bestDistance = distance;
                best = height;
                bestX = column.X;
                bestZ = column.Z;
            }
        }
        return best;
    }
}

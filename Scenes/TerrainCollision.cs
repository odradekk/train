using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class TerrainCollision
{
    internal const double SpacingCells = 4;

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
        if (!heights.TryGetValue(new(minX, minZ), out var originHeight)) return null;
        var width = (maxX - minX) / (int)SpacingCells + 1;
        var depth = (maxZ - minZ) / (int)SpacingCells + 1;
        var samples = ImmutableArray.CreateBuilder<float>(width * depth);
        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
            {
                if (!heights.TryGetValue(new(minX + x * (int)SpacingCells, minZ + z * (int)SpacingCells), out var height))
                    return null;
                samples.Add((float)(height - originHeight));
            }
        return new(id, new(
            (float)(partOrigin.X + minX - frameOrigin.X),
            (float)(originHeight - frameOrigin.Y),
            (float)(partOrigin.Z + minZ - frameOrigin.Z)), width, depth, SpacingCells, samples.DrainToImmutable());
    }
}

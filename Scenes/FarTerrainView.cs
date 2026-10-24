using System.Collections.Immutable;
using System.Numerics;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class FarTerrainView
{
    internal ImmutableArray<TerrainTile> Tiles { get; }
    private readonly (int Size, Dictionary<(double X, double Z), TerrainTile> Tiles)[] levels;

    internal FarTerrainView(ImmutableArray<TerrainTile> tiles)
    {
        Tiles = tiles;
        levels = tiles.GroupBy(tile => (tile.SizeCells, tile.SpacingCells))
            .OrderBy(group => group.Key.SpacingCells)
            .Select(group => (group.Key.SizeCells,
                group.ToDictionary(tile => (tile.Origin.X, tile.Origin.Z)))).ToArray();
    }

    internal double? Height(ScenePoint point)
    {
        foreach (var level in levels)
        {
            var x = Math.Floor(point.X / level.Size) * level.Size;
            var z = Math.Floor(point.Z / level.Size) * level.Size;
            // Adjacent tiles also own their outermost vertices.
            for (var dz = 0; dz <= 1; dz++)
            for (var dx = 0; dx <= 1; dx++)
                if (level.Tiles.TryGetValue((x - dx * level.Size, z - dz * level.Size), out var tile) &&
                    tile.Height(point.X - tile.Origin.X, point.Z - tile.Origin.Z) is { } height)
                    return tile.Origin.Y + height;
        }
        return null;
    }

    internal double? Intersect(SceneRay ray, ScenePoint frameOrigin, double limit)
    {
        double? nearest = null;
        foreach (var tile in Tiles)
        {
            var offset = new Vector3((float)(frameOrigin.X - tile.Origin.X),
                (float)(frameOrigin.Y - tile.Origin.Y), (float)(frameOrigin.Z - tile.Origin.Z));
            var local = new SceneRay(ray.Origin + offset, ray.Direction, nearest ?? limit);
            var bounds = tile.Bounds;
            // Slab intersection uses half-open bounds; mesh boundary triangles are closed.
            var maximum = bounds.Maximum;
            bounds = bounds with { Maximum = new(MathF.BitIncrement(maximum.X),
                MathF.BitIncrement(maximum.Y), MathF.BitIncrement(maximum.Z)) };
            if (!SceneGeometry.Intersect(local, bounds, out _, out _, out _)) continue;
            var mesh = tile.Mesh;
            for (var i = 0; i < mesh.TriangleIndices.Length; i += 3)
            {
                var a = mesh.Vertices[mesh.TriangleIndices[i]].Position;
                var b = mesh.Vertices[mesh.TriangleIndices[i + 1]].Position;
                var c = mesh.Vertices[mesh.TriangleIndices[i + 2]].Position;
                var edge = b - a;
                var other = c - a;
                var cross = Vector3.Cross(local.Direction, other);
                var determinant = Vector3.Dot(edge, cross);
                if (determinant == 0) continue;
                var from = local.Origin - a;
                var u = Vector3.Dot(from, cross) / (double)determinant;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(from, edge);
                var v = Vector3.Dot(local.Direction, q) / (double)determinant;
                if (v < 0 || u + v > 1) continue;
                var distance = Vector3.Dot(other, q) / (double)determinant;
                if (distance >= 0 && distance <= (nearest ?? limit)) nearest = distance;
            }
        }
        return nearest;
    }
}

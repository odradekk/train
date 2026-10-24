using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class TerrainTile
{
    internal const int ParentCells = 512 * 2;
    internal const int NearSpacingCells = 2 * 2;
    internal const int FarSpacingCells = 16 * 2;
    internal const int ColumnMarginCells = FarSpacingCells + NearSpacingCells;
    internal ScenePoint Origin { get; }
    internal int SizeCells { get; }
    internal int SpacingCells { get; }
    internal SceneMesh Mesh { get; }
    internal SceneBounds Bounds { get; }
    private readonly int side;

    private TerrainTile(ScenePoint origin, int size, int spacing, SceneMesh mesh)
    {
        Origin = origin; SizeCells = size; SpacingCells = spacing; Mesh = mesh;
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var vertex in mesh.Vertices)
        {
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }
        Bounds = new(minimum, maximum);
        side = size / spacing + 1;
    }

    internal static TerrainTile Build(ScenePoint origin, int sizeCells, int spacingCells,
        Func<ScenePoint, double> height, SceneMaterial material, CancellationToken token = default,
        int textureSpanCells = 1, IReadOnlyList<TerrainHeightCap>? lowerBounds = null)
    {
        var builder = Begin(origin, sizeCells, spacingCells, height, material, textureSpanCells, lowerBounds);
        while (!builder.Complete) builder.Advance(4096, token);
        return builder.Result;
    }

    internal static Builder Begin(ScenePoint origin, int sizeCells, int spacingCells,
        Func<ScenePoint, double> height, SceneMaterial material, int textureSpanCells = 1,
        IReadOnlyList<TerrainHeightCap>? lowerBounds = null)
    {
        var builder = new Builder();
        builder.Start(Generate(builder, origin, sizeCells, spacingCells, height, material, textureSpanCells, lowerBounds));
        return builder;
    }

    internal sealed class Builder
    {
        private IEnumerator<int>? work;
        private TerrainTile? result;
        internal bool Complete => result is not null;
        internal TerrainTile Result => result ?? throw new InvalidOperationException("Terrain tile is incomplete.");
        internal void Start(IEnumerable<int> steps) => work = steps.GetEnumerator();
        internal void Publish(TerrainTile tile) => result = tile;
        internal int Advance(int maxOperations, CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);
            var operations = 0;
            while (!Complete && operations < maxOperations)
            {
                token.ThrowIfCancellationRequested();
                work!.MoveNext();
                operations++;
            }
            if (Complete) { work?.Dispose(); work = null; }
            return operations;
        }
    }

    private static IEnumerable<int> Generate(Builder builder, ScenePoint origin, int sizeCells, int spacingCells,
        Func<ScenePoint, double> height, SceneMaterial material, int textureSpanCells,
        IReadOnlyList<TerrainHeightCap>? lowerBounds)
    {
        var side = sizeCells / spacingCells + 1;
        var surfaceCount = side * side;
        var positions = new Vector3[surfaceCount + 4 * side];
        var normals = new Vector3[positions.Length];
        var indices = new int[6 * (side - 1) * (side - 1) + 24 * (side - 1)];
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var point = new ScenePoint(origin.X + x * spacingCells, origin.Y, origin.Z + z * spacingCells);
            var y = At(point.X, point.Z, spacingCells) - origin.Y;
            var localY = (float)y;
            if (localY > y) localY = MathF.BitDecrement(localY);
            positions[z * side + x] = new(x * spacingCells, localY, z * spacingCells);
            yield return 0;
        }
        var cursor = 0;
        for (var z = 0; z < side - 1; z++)
        for (var x = 0; x < side - 1; x++)
        {
            var a = z * side + x;
            Triangle(a, a + side, a + 1);
            Triangle(a + 1, a + side, a + side + 1);
            yield return 0;
        }
        for (var edge = 0; edge < 4; edge++)
        {
            var bottom = surfaceCount + edge * side;
            for (var i = 0; i < side; i++)
            {
                var at = positions[Edge(i)];
                var worldX = origin.X + at.X;
                var worldZ = origin.Z + at.Z;
                var coarseX = Math.Floor(worldX / FarSpacingCells) * FarSpacingCells;
                var coarseZ = Math.Floor(worldZ / FarSpacingCells) * FarSpacingCells;
                var coarse = CoarseHeight(coarseX, coarseZ, worldX, worldZ);
                var depth = Math.Max(RailwayCoordinates.TerrainDepthCells, Math.Abs(at.Y + origin.Y - coarse) + 1);
                var bottomY = at.Y - (float)depth;
                if (spacingCells == FarSpacingCells)
                {
                    // When the fine edge is lower, its downward skirt cannot reach the coarse edge.
                    for (var offset = -FarSpacingCells; offset <= FarSpacingCells; offset += NearSpacingCells)
                    {
                        var p = new ScenePoint(worldX + (edge % 2 == 0 ? offset : 0), origin.Y,
                            worldZ + (edge % 2 == 1 ? offset : 0));
                        bottomY = Math.Min(bottomY, (float)(At(p.X, p.Z, NearSpacingCells) - origin.Y - 1));
                        yield return 0;
                    }
                }
                positions[bottom + i] = new(at.X, bottomY, at.Z);
                normals[bottom + i] = normals[Edge(i)];
                yield return 0;
            }
            for (var i = 0; i < side - 1; i++)
            {
                Triangle(Edge(i), Edge(i + 1), bottom + i, false);
                Triangle(Edge(i + 1), bottom + i + 1, bottom + i, false);
                yield return 0;
            }

            int Edge(int i) => edge switch
            {
                0 => i,
                1 => i * side + side - 1,
                2 => (side - 1) * side + side - 1 - i,
                _ => (side - 1 - i) * side
            };
        }
        var vertices = new SceneVertex[positions.Length];
        var phaseX = origin.X - Math.Floor(origin.X / textureSpanCells) * textureSpanCells;
        var phaseZ = origin.Z - Math.Floor(origin.Z / textureSpanCells) * textureSpanCells;
        for (var i = 0; i < vertices.Length; i++)
        {
            var p = positions[i];
            vertices[i] = new(p, Vector3.Normalize(normals[i]),
                new((float)((phaseX + p.X) / textureSpanCells), (float)(-(phaseZ + p.Z) / textureSpanCells)),
                new(p.X / sizeCells, p.Z / sizeCells), ExposedFaceEdges.None, 0, 0);
            yield return 0;
        }
        builder.Publish(new(origin, sizeCells, spacingCells, new(material,
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(vertices),
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(indices))));

        void Triangle(int a, int b, int c, bool surface = true)
        {
            indices[cursor++] = a; indices[cursor++] = b; indices[cursor++] = c;
            if (!surface) return;
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }

        double CoarseHeight(double x, double z, double px, double pz)
        {
            var u = (px - x) / FarSpacingCells;
            var v = (pz - z) / FarSpacingCells;
            return Interpolate(At(x, z, FarSpacingCells), At(x + FarSpacingCells, z, FarSpacingCells),
                At(x, z + FarSpacingCells, FarSpacingCells), At(x + FarSpacingCells, z + FarSpacingCells, FarSpacingCells), u, v);
        }

        double At(double x, double z, int spacing)
        {
            var value = height(new(x, origin.Y, z));
            if (lowerBounds is not null)
                foreach (var cap in lowerBounds)
                    // Keep exact integer L0 tops and far triangles distinct for rendering and picking.
                    value = Math.Min(value, cap.Minimum((int)x, (int)z, spacing) - 1.0 / 16);
            return value;
        }
    }

    internal double? Height(double x, double z)
    {
        if (x < 0 || z < 0 || x > SizeCells || z > SizeCells) return null;
        var column = Math.Min(side - 2, (int)(x / SpacingCells));
        var row = Math.Min(side - 2, (int)(z / SpacingCells));
        var u = (x - column * SpacingCells) / SpacingCells;
        var v = (z - row * SpacingCells) / SpacingCells;
        var a = row * side + column;
        return Interpolate(Mesh.Vertices[a].Position.Y, Mesh.Vertices[a + 1].Position.Y,
            Mesh.Vertices[a + side].Position.Y, Mesh.Vertices[a + side + 1].Position.Y, u, v);
    }

    private static double Interpolate(double a, double b, double c, double d, double u, double v)
    {
        var first = u + v <= 1;
        var value = first ? a + u * (b - a) + v * (c - a) :
            d + (1 - v) * (b - d) + (1 - u) * (c - d);
        var corner = first ? a : d;
        // Cancellation at a triangle edge must not put the query above a bounded vertex.
        return Math.Clamp(value, Math.Min(corner, Math.Min(b, c)), Math.Max(corner, Math.Max(b, c)));
    }
}

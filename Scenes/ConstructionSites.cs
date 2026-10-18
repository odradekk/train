using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class ConstructionSites
{
    internal static SceneMesh Plot(PlotSize size, PlotId id)
    {
        var (x, z, _) = NodeSettlement.Size(size);
        var hx = x / 2f;
        var hz = z / 2f;
        var vertices = ImmutableArray.CreateBuilder<SceneVertex>();
        var indices = ImmutableArray.CreateBuilder<int>();
        const float thickness = .5f;
        const float height = 2f;
        Wall(-hx, -hz, hx, -hz + thickness);
        Wall(-hx, hz - thickness, hx, hz);
        Wall(-hx, -hz, -hx + thickness, hz);
        Wall(hx - thickness, -hz, hx, hz);
        var insetX = Math.Min(hx - 2, 8);
        var insetZ = Math.Min(hz - 2, 8);
        Pile(Spread(id, 1, -insetX, insetX), Spread(id, 2, -insetZ, insetZ), 1.4f, 2.2f);
        Pile(Spread(id, 3, -insetX, insetX), Spread(id, 4, -insetZ, insetZ), 1.1f, 1.6f);
        Pile(Spread(id, 5, -insetX * .4f, insetX * .4f), Spread(id, 6, -insetZ * .4f, insetZ * .4f), 1.8f, 1.1f);
        return new(new SceneMaterial.BuiltIn(BuiltInMaterial.Construction), vertices.DrainToImmutable(), indices.DrainToImmutable());

        void Wall(float x0, float z0, float x1, float z1) =>
            Box((x0 + x1) / 2, height / 2, (z0 + z1) / 2, Math.Abs(x1 - x0) / 2, height / 2, Math.Abs(z1 - z0) / 2);
        void Pile(float cx, float cz, float half, float pileHeight) =>
            Box(cx, pileHeight / 2, cz, half, pileHeight / 2, half);
        void Box(float cx, float cy, float cz, float halfX, float halfY, float halfZ)
        {
            Vector3 Corner(float px, float py, float pz) => new(cx + px, cy + py, cz + pz);
            var a = Corner(-halfX, -halfY, -halfZ); var b = Corner(halfX, -halfY, -halfZ);
            var c = Corner(halfX, halfY, -halfZ); var d = Corner(-halfX, halfY, -halfZ);
            var e = Corner(-halfX, -halfY, halfZ); var f = Corner(halfX, -halfY, halfZ);
            var g = Corner(halfX, halfY, halfZ); var h = Corner(-halfX, halfY, halfZ);
            Face(d, c, b, a); Face(e, f, g, h); Face(a, b, f, e);
            Face(d, h, g, c); Face(a, e, h, d); Face(b, c, g, f);
        }
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Triangle(a, b, c);
            Triangle(a, c, d);
        }
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Add(a); Add(b); Add(c);
            void Add(Vector3 point)
            {
                indices.Add(vertices.Count);
                vertices.Add(new(point, normal, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0));
            }
        }
    }

    private static float Spread(PlotId id, int salt, float min, float max)
    {
        var unit = (id.Value * 17 + (ulong)salt * 13) % 100 / 100f;
        return min + (max - min) * unit;
    }
}

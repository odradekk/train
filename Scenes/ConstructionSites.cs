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
        var mesh = new RailwayMeshes.MeshBuilder(BuiltInMaterial.Construction);
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
        return mesh.Build();

        void Wall(float x0, float z0, float x1, float z1) =>
            Box((x0 + x1) / 2, height / 2, (z0 + z1) / 2, Math.Abs(x1 - x0) / 2, height / 2, Math.Abs(z1 - z0) / 2);
        void Pile(float cx, float cz, float half, float pileHeight) =>
            Box(cx, pileHeight / 2, cz, half, pileHeight / 2, half);
        void Box(float cx, float cy, float cz, float halfX, float halfY, float halfZ) =>
            mesh.Box(new(cx, cy, cz), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, halfX, halfY, halfZ);
    }

    private static float Spread(PlotId id, int salt, float min, float max) =>
        min + (max - min) * (float)RailwayRandom.Unit(id.Value, (ulong)salt, 0, 1);
}

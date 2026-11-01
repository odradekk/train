using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeTerrainCaps
{
    private readonly NodeScene.Preparation prepared;
    private readonly NodeMapItem node;
    private readonly (string Key, ScenePanBounds Bounds)[] boundaries;
    private readonly ScenePanBounds[] layoutRegions;
    private readonly Dictionary<string, TerrainHeightCap.Builder> parts = [];
    private readonly Func<string, TerrainHeightCap?>? completed;

    internal NodeTerrainCaps(NodeScene.Preparation prepared, Func<string, TerrainHeightCap?>? completed = null)
    {
        this.completed = completed;
        this.prepared = prepared;
        node = prepared.Snapshot.Core.World.Nodes.Single(item => item.Id == prepared.Global.Layout.Node);
        boundaries = prepared.Global.Parts.Where(item => NodePartKeys.IsBoundary(item.Key) && item.Value.Heights.Count != 0).Select(item =>
        {
            var part = item.Value;
            var minX = int.MaxValue; var minZ = int.MaxValue;
            var maxX = int.MinValue; var maxZ = int.MinValue;
            foreach (var column in part.Heights.Keys)
            {
                minX = Math.Min(minX, column.X); minZ = Math.Min(minZ, column.Z);
                maxX = Math.Max(maxX, column.X); maxZ = Math.Max(maxZ, column.Z);
            }
            return (item.Key, new ScenePanBounds(
                new((float)(minX + part.Origin.X), (float)(minZ + part.Origin.Z)),
                new((float)(maxX + part.Origin.X + 1), (float)(maxZ + part.Origin.Z + 1))));
        }).ToArray();
        var regions = new List<ScenePanBounds> { prepared.Global.Layout.Bounds };
        regions.AddRange(boundaries.Select(item => item.Bounds));
        if (prepared.Global.Layout.Template is { } template) AddFlatten(NodeSettlement.Bounds(template));
        if (prepared.Global.Layout.Lair is { } lair && NodeLair.Region(lair) is { } lairBounds) AddFlatten(lairBounds);
        layoutRegions = regions.ToArray();

        void AddFlatten(ScenePanBounds bounds)
        {
            var blend = new Vector2(NodeSettlement.FlattenBlend);
            regions.Add(new(bounds.Minimum - blend, bounds.Maximum + blend));
        }
    }

    internal bool UsesLayout(ScenePanBounds bounds) => UsesLayout(
        bounds.Minimum.X, bounds.Minimum.Y, bounds.Maximum.X, bounds.Maximum.Y);

    private bool UsesLayout(double minX, double minZ, double maxX, double maxZ)
    {
        foreach (var region in layoutRegions)
            if (region.Minimum.X <= maxX && region.Maximum.X >= minX &&
                region.Minimum.Y <= maxZ && region.Maximum.Y >= minZ) return true;
        return false;
    }

    internal double Height(ScenePoint point)
    {
        if (!UsesLayout(point.X, point.Z, point.X, point.Z))
        {
            var origin = RailwayCoordinates.NodePoint(node);
            return TerrainSurface.Ground(prepared.Snapshot.Core.World.WorldSeed, node.Id.Value, node.Terrain,
                prepared.Terrain, new(origin.X + point.X, origin.Y + point.Y, origin.Z + point.Z));
        }
        var column = new GridColumn((int)Math.Floor(point.X), (int)Math.Floor(point.Z));
        var bed = prepared.Beds.At(column);
        return TerrainSurface.NodeHeight(prepared.Snapshot, node, prepared.Global.Layout, point, bed, prepared.Terrain);
    }

    internal IEnumerable<(string Key, TerrainHeightCap.Builder Work)> Parts(ScenePanBounds bounds)
    {
        var layout = prepared.Global.Layout.Bounds;
        var minimum = Vector2.Max(layout.Minimum, bounds.Minimum);
        var maximum = Vector2.Min(layout.Maximum, bounds.Maximum);
        if (prepared.HasChunks && minimum.X < maximum.X && minimum.Y < maximum.Y)
            foreach (var from in NodeScene.BoundsChunks(new(new(minimum.X, 0, minimum.Y), new(maximum.X, 0, maximum.Y))))
                yield return (NodeScene.ChunkKey(from), Chunk(from));
        foreach (var boundary in boundaries)
            if (boundary.Bounds.Minimum.X < bounds.Maximum.X && boundary.Bounds.Maximum.X > bounds.Minimum.X &&
                boundary.Bounds.Minimum.Y < bounds.Maximum.Y && boundary.Bounds.Maximum.Y > bounds.Minimum.Y)
                yield return (boundary.Key, Boundary(boundary.Key));
    }

    internal TerrainHeightCap.Builder Chunk(GridColumn from)
    {
        var key = NodeScene.ChunkKey(from);
        return TerrainHeightCap.CachedPart(parts, key, key, completed, () => BeginChunk(from));
    }

    private TerrainHeightCap.Builder BeginChunk(GridColumn from)
    {
        var bounds = prepared.Global.Layout.Bounds;
        var beds = prepared.Beds.ForChunk(from);
        return TerrainHeightCap.Begin(new(new(from.X, from.Z),
            new(from.X + VoxelGrid.ChunkSize, from.Z + VoxelGrid.ChunkSize)), column =>
        {
            if (column.X < bounds.Minimum.X || column.X >= bounds.Maximum.X ||
                column.Z < bounds.Minimum.Y || column.Z >= bounds.Maximum.Y ||
                prepared.BoundarySurfaces.ContainsKey(column)) return null;
            var height = TerrainSurface.NodeHeight(prepared.Snapshot, node, prepared.Global.Layout,
                new(column.X + .5, 0, column.Z + .5), beds.GetValueOrDefault(column), prepared.Terrain);
            return Math.Floor(height) + 1;
        });
    }

    internal TerrainHeightCap.Builder Boundary(string key)
    {
        return TerrainHeightCap.CachedPart(parts, key, key, completed, () => BeginBoundary(key));
    }

    private TerrainHeightCap.Builder BeginBoundary(string key)
    {
        var part = prepared.Global.Parts[key];
        var bounds = boundaries.Single(item => item.Key == key).Bounds;
        return TerrainHeightCap.Begin(bounds, column =>
        {
            var local = new GridColumn((int)Math.Floor(column.X + .5 - part.Origin.X),
                (int)Math.Floor(column.Z + .5 - part.Origin.Z));
            return part.Heights.TryGetValue(local, out var height) ? height + part.Origin.Y : null;
        });
    }
}

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
    private readonly Dictionary<string, TerrainHeightCap.Builder> parts = [];
    private readonly Func<string, TerrainHeightCap?>? completed;

    internal NodeTerrainCaps(NodeScene.Preparation prepared, Func<string, TerrainHeightCap?>? completed = null)
    {
        this.completed = completed;
        this.prepared = prepared;
        node = prepared.Snapshot.Core.World.Nodes.Single(item => item.Id == prepared.Global.Layout.Node);
        boundaries = prepared.Global.Parts.Where(item => NodePartKeys.IsBoundary(item.Key) && !item.Value.Heights.IsEmpty).Select(item =>
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
    }

    internal IEnumerable<(string Key, TerrainHeightCap.Builder Work)> Parts(ScenePanBounds bounds)
    {
        var layout = prepared.Global.Layout.Bounds;
        var size = VoxelGrid.ChunkSize;
        var minimum = Vector2.Max(layout.Minimum, bounds.Minimum);
        var maximum = Vector2.Min(layout.Maximum, bounds.Maximum);
        if (prepared.HasChunks && minimum.X < maximum.X && minimum.Y < maximum.Y)
            for (var z = (int)Math.Floor(minimum.Y / size) * size; z < maximum.Y; z += size)
            for (var x = (int)Math.Floor(minimum.X / size) * size; x < maximum.X; x += size)
            {
                var from = new GridColumn(x, z);
                yield return (NodeScene.ChunkKey(from), Chunk(from));
            }
        foreach (var boundary in boundaries)
            if (boundary.Bounds.Minimum.X < bounds.Maximum.X && boundary.Bounds.Maximum.X > bounds.Minimum.X &&
                boundary.Bounds.Minimum.Y < bounds.Maximum.Y && boundary.Bounds.Maximum.Y > bounds.Minimum.Y)
                yield return (boundary.Key, Boundary(boundary.Key));
    }

    internal TerrainHeightCap.Builder Chunk(GridColumn from)
    {
        var key = NodeScene.ChunkKey(from);
        if (parts.TryGetValue(key, out var cached)) return cached;
        if (completed?.Invoke(key) is { } saved)
            return parts[key] = TerrainHeightCap.Builder.FromCompleted(saved);
        var bounds = prepared.Global.Layout.Bounds;
        var beds = prepared.BedChunks.GetValueOrDefault(from);
        var work = TerrainHeightCap.Begin(new(new(from.X, from.Z),
            new(from.X + VoxelGrid.ChunkSize, from.Z + VoxelGrid.ChunkSize)), column =>
        {
            if (column.X < bounds.Minimum.X || column.X >= bounds.Maximum.X ||
                column.Z < bounds.Minimum.Y || column.Z >= bounds.Maximum.Y ||
                prepared.BoundarySurfaces.ContainsKey(column)) return null;
            var height = TerrainSurface.NodeHeight(prepared.Snapshot, node, prepared.Global.Layout,
                new(column.X + .5, 0, column.Z + .5), beds?.GetValueOrDefault(column), prepared.Terrain);
            return Math.Floor(height) + 1;
        });
        parts.Add(key, work);
        return work;
    }

    internal TerrainHeightCap.Builder Boundary(string key)
    {
        if (parts.TryGetValue(key, out var cached)) return cached;
        if (completed?.Invoke(key) is { } saved)
            return parts[key] = TerrainHeightCap.Builder.FromCompleted(saved);
        var part = prepared.Global.Parts[key];
        var bounds = boundaries.Single(item => item.Key == key).Bounds;
        var work = TerrainHeightCap.Begin(bounds, column =>
        {
            var local = new GridColumn((int)Math.Floor(column.X + .5 - part.Origin.X),
                (int)Math.Floor(column.Z + .5 - part.Origin.Z));
            return part.Heights.TryGetValue(local, out var height) ? height + part.Origin.Y : null;
        });
        parts.Add(key, work);
        return work;
    }
}

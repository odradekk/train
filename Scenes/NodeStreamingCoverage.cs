using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeStreamingCoverage
{
    private readonly NodeLayout layout;
    private readonly bool hasChunks;
    private readonly SceneStreamingSceneId scene;
    private readonly ImmutableDictionary<GridColumn, SceneStreamingPartId> boundaries;

    internal NodeStreamingCoverage(NodeLayout layout, bool hasChunks,
        ImmutableDictionary<string, NodeScene.Part> parts, SceneStreamingSceneId scene)
    {
        this.layout = layout;
        this.hasChunks = hasChunks;
        this.scene = scene;
        var columns = ImmutableDictionary.CreateBuilder<GridColumn, SceneStreamingPartId>();
        foreach (var (key, part) in parts.Where(part => part.Key.StartsWith("boundary:", StringComparison.Ordinal)))
            foreach (var column in part.Heights.Keys)
                columns[new((int)Math.Round(column.X + part.Origin.X), (int)Math.Round(column.Z + part.Origin.Z))] =
                    new(scene, SceneStreamingPartKind.NodeBoundary, key);
        boundaries = columns.ToImmutable();
    }

    internal ISceneStreamingCoverage At(ScenePoint origin) => new Snapshot(this, origin);

    private sealed class Snapshot(NodeStreamingCoverage coverage, ScenePoint origin) : ISceneStreamingCoverage
    {
        public ImmutableArray<SceneStreamingPartId> FindTerrainParts(double x, double z) =>
            coverage.Find(x + origin.X, z + origin.Z);
    }

    private ImmutableArray<SceneStreamingPartId> Find(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentException("Coordinates must be finite.");
        var column = new GridColumn((int)Math.Floor(x), (int)Math.Floor(z));
        if (boundaries.TryGetValue(column, out var boundary)) return [boundary];
        if (!hasChunks || x < layout.Bounds.Minimum.X || x >= layout.Bounds.Maximum.X ||
            z < layout.Bounds.Minimum.Y || z >= layout.Bounds.Maximum.Y) return [];
        var size = VoxelGrid.ChunkSize;
        var from = new GridColumn((int)layout.Bounds.Minimum.X + (int)Math.Floor((x - layout.Bounds.Minimum.X) / size) * size,
            (int)layout.Bounds.Minimum.Y + (int)Math.Floor((z - layout.Bounds.Minimum.Y) / size) * size);
        return [new(scene, SceneStreamingPartKind.NodeTerrain, NodeScene.ChunkKey(from))];
    }
}

using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class VoxelGrid
{
    internal VoxelGrid(ImmutableArray<SceneCell> cells)
        : this(cells.IsDefault
            ? throw new ArgumentException("Cells must be initialized.", nameof(cells))
            : (IReadOnlyList<SceneCell>)cells) { }

    internal static VoxelGrid FromBuilder(ImmutableArray<SceneCell>.Builder cells) => new((IReadOnlyList<SceneCell>)cells);

    private VoxelGrid(IReadOnlyList<SceneCell> cells)
    {
        var properties = ImmutableArray.CreateBuilder<CellProperties?>();
        properties.Add(null);
        var ids = new Dictionary<CellProperties, int>();
        var minimum = new GridPosition(int.MaxValue, int.MaxValue, int.MaxValue);
        var maximum = new GridPosition(int.MinValue, int.MinValue, int.MinValue);
        var previousChunk = default(GridPosition);
        Indices[]? values = null;
        CellProperties? previousProperties = null;
        var previousId = 0;
        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            var cell = cells[cellIndex];
            var key = Chunk(cell.At);
            if (values is null || key != previousChunk)
            {
                if (!chunks.TryGetValue(key, out values))
                    chunks.Add(key, values = [new(), new()]);
                previousChunk = key;
            }
            var index = Index(cell.At);
            var half = values[index / HalfSize];
            var offset = index % HalfSize;
            if (half[offset] != 0) throw new ArgumentException("Cells overlap.", nameof(cells));
            var id = previousId;
            if (id == 0 || !ReferenceEquals(previousProperties, cell.Properties))
            {
                if (!ids.TryGetValue(cell.Properties, out id))
                {
                    id = properties.Count;
                    ids.Add(cell.Properties, id);
                    properties.Add(cell.Properties);
                }
                previousProperties = cell.Properties;
                previousId = id;
            }
            half[offset] = id;
            minimum = new(Math.Min(minimum.X, cell.At.X), Math.Min(minimum.Y, cell.At.Y), Math.Min(minimum.Z, cell.At.Z));
            maximum = new(Math.Max(maximum.X, cell.At.X), Math.Max(maximum.Y, cell.At.Y), Math.Max(maximum.Z, cell.At.Z));
        }
        entries = properties.ToImmutable();
        if (cells.Count != 0)
            Bounds = new(new(minimum.X, minimum.Y, minimum.Z), new(maximum.X + 1f, maximum.Y + 1f, maximum.Z + 1f));
    }
}

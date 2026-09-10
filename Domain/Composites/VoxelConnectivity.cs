namespace Train.Domain.Composites;

public static class VoxelConnectivity
{
    private static readonly (int X, int Y, int Z)[] Neighbors =
    {
        (1, 0, 0), (-1, 0, 0),
        (0, 1, 0), (0, -1, 0),
        (0, 0, 1), (0, 0, -1)
    };

    public static bool IsSixConnected(IReadOnlyCollection<LocalVoxelCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
            return false;

        var remaining = cells.ToHashSet();
        var start = remaining.Min();
        var queue = new Queue<LocalVoxelCoord>();
        queue.Enqueue(start);
        remaining.Remove(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (x, y, z) in Neighbors)
            {
                var neighbor = new LocalVoxelCoord(current.X + x, current.Y + y, current.Z + z);
                if (!remaining.Remove(neighbor))
                    continue;
                queue.Enqueue(neighbor);
            }
        }

        return remaining.Count == 0;
    }

    public static IReadOnlyList<IReadOnlyList<T>> Components<T>(
        IReadOnlyList<T> items,
        Func<T, LocalVoxelCoord> cellOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(cellOf);
        if (items.Count == 0)
            return Array.Empty<IReadOnlyList<T>>();

        var byCell = new Dictionary<LocalVoxelCoord, T>();
        foreach (var item in items)
            byCell[cellOf(item)] = item;

        var remaining = byCell.Keys.ToHashSet();
        var components = new List<IReadOnlyList<T>>();
        while (remaining.Count > 0)
        {
            var start = remaining.Min();
            var queue = new Queue<LocalVoxelCoord>();
            var cells = new List<LocalVoxelCoord>();
            queue.Enqueue(start);
            remaining.Remove(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cells.Add(current);
                foreach (var (x, y, z) in Neighbors)
                {
                    var neighbor = new LocalVoxelCoord(current.X + x, current.Y + y, current.Z + z);
                    if (!remaining.Remove(neighbor))
                        continue;
                    queue.Enqueue(neighbor);
                }
            }

            components.Add(cells.OrderBy(cell => cell).Select(cell => byCell[cell]).ToArray());
        }

        return components
            .OrderBy(component => cellOf(component[0]))
            .ToArray();
    }
}

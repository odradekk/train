using Train.Domain.Commands;

namespace Train.Domain.Voxels;

/// <summary>Provisional interaction limits for explicit cell-list edits, pending larger-world measurements.</summary>
public static class VoxelRegionLimits
{
    public const int MaxBrushSize = 16;
    public const int ConfirmationThreshold = 512;
    public const int MaxCells = 4096;
}

/// <summary>The covered range, including skipped and conflicting cells. Box corners are inclusive.</summary>
public sealed class VoxelCellRegion
{
    public IReadOnlyList<WorldVoxelCoord> Cells { get; }
    public WorldVoxelCoord Min { get; }
    public WorldVoxelCoord Max { get; }
    public long Count { get; }
    public VoxelCellSetStatus Status { get; }

    private VoxelCellRegion(IReadOnlyList<WorldVoxelCoord> cells, WorldVoxelCoord min, WorldVoxelCoord max,
        long count, VoxelCellSetStatus status)
    {
        Cells = cells;
        Min = min;
        Max = max;
        Count = count;
        Status = status;
    }

    public static VoxelCellRegion Box(WorldVoxelCoord first, WorldVoxelCoord second, int height)
    {
        var min = new WorldVoxelCoord(Math.Min(first.X, second.X), first.Y, Math.Min(first.Z, second.Z));
        var top = (long)first.Y + height - 1;
        if (height < 1 || top > int.MaxValue)
            return new(Array.Empty<WorldVoxelCoord>(), min, min, 0, VoxelCellSetStatus.InvalidRange);
        var max = new WorldVoxelCoord(Math.Max(first.X, second.X), (int)top, Math.Max(first.Z, second.Z));
        var volume = (decimal)((long)max.X - min.X + 1) * height * ((long)max.Z - min.Z + 1);
        var count = (long)Math.Min(volume, long.MaxValue);
        if (count > VoxelRegionLimits.MaxCells)
            return new(Array.Empty<WorldVoxelCoord>(), min, max, count, VoxelCellSetStatus.RangeTooLarge);
        var cells = new List<WorldVoxelCoord>((int)count);
        for (var x = (long)min.X; x <= max.X; x++)
        for (var y = (long)min.Y; y <= max.Y; y++)
        for (var z = (long)min.Z; z <= max.Z; z++)
            cells.Add(new((int)x, (int)y, (int)z));
        return new(cells.AsReadOnly(), min, max, count, VoxelCellSetStatus.Valid);
    }

    public static VoxelCellRegion FromCells(IEnumerable<WorldVoxelCoord> cells)
    {
        // Sorting also makes brush encoding independent of pointer event frequency and HashSet enumeration.
        var sorted = cells.Distinct().OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToArray();
        if (sorted.Length == 0)
            return new(sorted, default, default, 0, VoxelCellSetStatus.NoTargets);
        var min = new WorldVoxelCoord(sorted.Min(cell => cell.X), sorted.Min(cell => cell.Y), sorted.Min(cell => cell.Z));
        var max = new WorldVoxelCoord(sorted.Max(cell => cell.X), sorted.Max(cell => cell.Y), sorted.Max(cell => cell.Z));
        return new(Array.AsReadOnly(sorted), min, max, sorted.Length,
            sorted.Length > VoxelRegionLimits.MaxCells ? VoxelCellSetStatus.RangeTooLarge : VoxelCellSetStatus.Valid);
    }
}

public sealed record VoxelRegionPreview(VoxelCellRegion Range, VoxelCellSetEdit Edit,
    int SkippedCount, VoxelCellSetResult Validation)
{
    public int AffectedCount => Edit.Targets.Count;
    public bool RequiresConfirmation => AffectedCount > VoxelRegionLimits.ConfirmationThreshold;
}

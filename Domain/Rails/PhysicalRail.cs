using Train.Core.Content.Voxels;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public enum RailEnd
{
    Start = 0,
    End = 1
}

public enum RailEditStatus
{
    Committed = 0,
    InvalidPlan = 1,
    OutOfBounds = 2,
    Occupied = 3,
    WorldObstacle = 4,
    ClearanceConflict = 5,
    IndependentLineConflict = 6,
    UnknownDefinition = 7,
    UnsupportedGeometry = 8,
    AlreadyPresent = 9,
    TransactionExhausted = 10,
    EntityIdExhausted = 11,
    InputExhausted = 12,
    SimulationFaulted = 13,
    WorldChanging = 14,
    HistoryConfirmationRequired = 15
}

public enum RailRepairStatus
{
    Repaired = 0,
    Empty = 1,
    Intact = 2,
    UnknownCell = 3,
    Occupied = 4,
    Conflict = 5,
    InvalidRepair = 6,
    TransactionExhausted = 7,
    InputExhausted = 8,
    SimulationFaulted = 9,
    WorldChanging = 10,
    HistoryConfirmationRequired = 11
}

public readonly record struct RailInterval(double Start, double End)
{
    public bool HasPositiveLength => End > Start && double.IsFinite(Start) && double.IsFinite(End);

    public RailInterval Translate(double offset) => new(Start + offset, End + offset);

    public static RailInterval? Overlap(RailInterval left, RailInterval right)
    {
        var start = Math.Max(left.Start, right.Start);
        var end = Math.Min(left.End, right.End);
        return end > start ? new RailInterval(start, end) : null;
    }
}

public readonly record struct RailVolumeBox(
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ)
{
    public bool HasPositiveVolume =>
        MaxX > MinX && MaxY > MinY && MaxZ > MinZ &&
        double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MinZ) &&
        double.IsFinite(MaxX) && double.IsFinite(MaxY) && double.IsFinite(MaxZ);

    public bool OverlapsCell(WorldVoxelCoord cell)
    {
        var overlapX = Math.Min(MaxX, cell.X + 1.0) - Math.Max(MinX, cell.X);
        var overlapY = Math.Min(MaxY, cell.Y + 1.0) - Math.Max(MinY, cell.Y);
        var overlapZ = Math.Min(MaxZ, cell.Z + 1.0) - Math.Max(MinZ, cell.Z);
        return overlapX > 0 && overlapY > 0 && overlapZ > 0;
    }

    public bool TryOverlapX(WorldVoxelCoord cell, out RailInterval interval)
    {
        interval = default;
        if (!OverlapsCell(cell))
            return false;
        interval = new RailInterval(Math.Max(MinX, cell.X), Math.Min(MaxX, cell.X + 1.0));
        return interval.HasPositiveLength;
    }

    public bool Overlaps(RailVolumeBox other)
    {
        var overlapX = Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX);
        var overlapY = Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY);
        var overlapZ = Math.Min(MaxZ, other.MaxZ) - Math.Max(MinZ, other.MinZ);
        return overlapX > 0 && overlapY > 0 && overlapZ > 0;
    }

    public RailVolumeBox? Intersection(RailVolumeBox other)
    {
        var box = new RailVolumeBox(
            Math.Max(MinX, other.MinX),
            Math.Max(MinY, other.MinY),
            Math.Max(MinZ, other.MinZ),
            Math.Min(MaxX, other.MaxX),
            Math.Min(MaxY, other.MaxY),
            Math.Min(MaxZ, other.MaxZ));
        return box.HasPositiveVolume ? box : null;
    }

    public static RailVolumeBox ForCell(WorldVoxelCoord cell) =>
        new(cell.X, cell.Y, cell.Z, cell.X + 1.0, cell.Y + 1.0, cell.Z + 1.0);

    public static RailVolumeBox ForChunk(ChunkCoord chunk, int chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");
        return new(
            chunk.X * (double)chunkSize,
            chunk.Y * (double)chunkSize,
            chunk.Z * (double)chunkSize,
            (chunk.X + 1) * (double)chunkSize,
            (chunk.Y + 1) * (double)chunkSize,
            (chunk.Z + 1) * (double)chunkSize);
    }

    public static IReadOnlyList<RailVolumeBox> Subtract(RailVolumeBox box, IEnumerable<RailVolumeBox> holes)
    {
        ArgumentNullException.ThrowIfNull(holes);
        IReadOnlyList<RailVolumeBox> remaining = box.HasPositiveVolume ? new[] { box } : Array.Empty<RailVolumeBox>();
        foreach (var hole in holes)
        {
            var next = new List<RailVolumeBox>();
            foreach (var piece in remaining)
                next.AddRange(Subtract(piece, hole));
            remaining = next;
        }

        return remaining;
    }

    public static IReadOnlyList<RailVolumeBox> Subtract(RailVolumeBox box, RailVolumeBox hole)
    {
        if (!box.HasPositiveVolume)
            return Array.Empty<RailVolumeBox>();
        if (!box.Overlaps(hole))
            return new[] { box };

        var ix0 = Math.Max(box.MinX, hole.MinX);
        var ix1 = Math.Min(box.MaxX, hole.MaxX);
        var iy0 = Math.Max(box.MinY, hole.MinY);
        var iy1 = Math.Min(box.MaxY, hole.MaxY);
        var iz0 = Math.Max(box.MinZ, hole.MinZ);
        var iz1 = Math.Min(box.MaxZ, hole.MaxZ);
        var remaining = new List<RailVolumeBox>(6);
        Add(remaining, box.MinX, box.MinY, box.MinZ, ix0, box.MaxY, box.MaxZ);
        Add(remaining, ix1, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);
        Add(remaining, ix0, box.MinY, box.MinZ, ix1, iy0, box.MaxZ);
        Add(remaining, ix0, iy1, box.MinZ, ix1, box.MaxY, box.MaxZ);
        Add(remaining, ix0, iy0, box.MinZ, ix1, iy1, iz0);
        Add(remaining, ix0, iy0, iz1, ix1, iy1, box.MaxZ);
        return remaining;
    }

    private static void Add(
        List<RailVolumeBox> remaining,
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ)
    {
        var piece = new RailVolumeBox(minX, minY, minZ, maxX, maxY, maxZ);
        if (piece.HasPositiveVolume)
            remaining.Add(piece);
    }
}

public readonly record struct HorizontalStraightAlignment(
    double StartX,
    double StartY,
    double StartZ,
    double EndX,
    double EndY,
    double EndZ)
{
    public double Length => EndX - StartX;
    public bool IsPositiveX =>
        EndX > StartX &&
        StartY == EndY &&
        StartZ == EndZ &&
        double.IsFinite(StartX) &&
        double.IsFinite(EndX) &&
        double.IsFinite(StartY) &&
        double.IsFinite(StartZ);

    public RailInterval WorldX => new(StartX, EndX);
}

public sealed record PhysicalRailSegment(
    EntityId Id,
    HorizontalStraightAlignment Alignment,
    IReadOnlyList<RailVolumeBox> RailVolumes,
    IReadOnlyList<RailVolumeBox> SleeperVolumes);

public sealed record RailConnection(EntityId FromSegment, RailEnd FromEnd, EntityId ToSegment, RailEnd ToEnd);

public sealed record RailCellSupport(EntityId SegmentId, RailInterval WorldX, RailInterval AlongTrack);

public sealed record RailOccupancyCell(
    WorldVoxelCoord Cell,
    IReadOnlyList<RailCellSupport> Supports);

public sealed record RailSegmentQuery(
    EntityId Id,
    HorizontalStraightAlignment Alignment,
    IReadOnlyList<RailInterval> TraversableWorldX,
    IReadOnlyList<RailInterval> TraversableAlongTrack,
    int OccupiedCellCount);

public sealed record RailQuery(
    IReadOnlyList<RailSegmentQuery> Segments,
    IReadOnlyList<RailConnection> Connections,
    IReadOnlyList<WorldVoxelCoord> OccupiedCells,
    IReadOnlyList<RailOccupancyCell> Occupancy,
    int SleeperCount);

public sealed record DerivedRailGraph(
    bool Current,
    IReadOnlyList<RailSegmentQuery> Segments,
    IReadOnlyList<RailConnection> Connections)
{
    public static DerivedRailGraph Stale(IReadOnlyList<RailConnection> connections) =>
        new(false, Array.Empty<RailSegmentQuery>(), connections);

    public bool TreatsIntervalAsTraversable(EntityId segmentId, RailInterval worldX)
    {
        if (!Current)
            return false;
        var segment = Segments.FirstOrDefault(item => item.Id == segmentId);
        if (segment is null)
            return false;
        return segment.TraversableWorldX.Any(interval =>
            RailInterval.Overlap(interval, worldX) is { } overlap &&
            overlap.Start <= worldX.Start &&
            overlap.End >= worldX.End);
    }
}

public sealed record RailEditResult(
    RailEditStatus Status,
    IReadOnlyList<EntityId>? SegmentIds = null)
{
    public bool Succeeded => Status == RailEditStatus.Committed;
}

public sealed record RailRepairResult(
    RailRepairStatus Status,
    WorldVoxelCoord Cell,
    IReadOnlyList<WorldVoxelCoord>? Restored = null)
{
    public bool Succeeded => Status == RailRepairStatus.Repaired;
}

public sealed record RailAuthoritySnapshot(
    int WorldVoxelCount,
    int SegmentCount,
    int OccupiedCellCount,
    int ConnectionCount,
    IReadOnlyList<EntityId> SegmentIds,
    IReadOnlyList<(WorldVoxelCoord Cell, VoxelDefinitionId DefinitionId, int Durability)> Occupied,
    ulong NextEntityId,
    ulong TransactionCount,
    bool DerivedGraphCurrent)
{
    public bool Equals(RailAuthoritySnapshot? other) =>
        other is not null &&
        WorldVoxelCount == other.WorldVoxelCount &&
        SegmentCount == other.SegmentCount &&
        OccupiedCellCount == other.OccupiedCellCount &&
        ConnectionCount == other.ConnectionCount &&
        NextEntityId == other.NextEntityId &&
        TransactionCount == other.TransactionCount &&
        DerivedGraphCurrent == other.DerivedGraphCurrent &&
        SegmentIds.SequenceEqual(other.SegmentIds) &&
        Occupied.SequenceEqual(other.Occupied);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WorldVoxelCount);
        hash.Add(SegmentCount);
        hash.Add(OccupiedCellCount);
        hash.Add(ConnectionCount);
        hash.Add(NextEntityId);
        hash.Add(TransactionCount);
        hash.Add(DerivedGraphCurrent);
        foreach (var id in SegmentIds)
            hash.Add(id);
        foreach (var occupied in Occupied)
            hash.Add(occupied);
        return hash.ToHashCode();
    }
}

public static class RailIntervals
{
    public static IReadOnlyList<RailInterval> Merge(IEnumerable<RailInterval> intervals)
    {
        var ordered = intervals
            .Where(interval => interval.HasPositiveLength)
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End)
            .ToArray();
        if (ordered.Length == 0)
            return Array.Empty<RailInterval>();

        var merged = new List<RailInterval>();
        var current = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.Start <= current.End)
            {
                current = new RailInterval(current.Start, Math.Max(current.End, next.End));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    public static IReadOnlyList<RailInterval> Subtract(
        IReadOnlyList<RailInterval> source,
        IEnumerable<RailInterval> holes)
    {
        var remaining = source.Where(interval => interval.HasPositiveLength).ToList();
        foreach (var hole in Merge(holes))
        {
            var next = new List<RailInterval>();
            foreach (var interval in remaining)
            {
                if (hole.End <= interval.Start || hole.Start >= interval.End)
                {
                    next.Add(interval);
                    continue;
                }

                if (hole.Start > interval.Start)
                    next.Add(new RailInterval(interval.Start, hole.Start));
                if (hole.End < interval.End)
                    next.Add(new RailInterval(hole.End, interval.End));
            }

            remaining = next;
        }

        return Merge(remaining);
    }

    public static IReadOnlyList<RailInterval> ClipTo(IEnumerable<RailInterval> intervals, RailInterval span)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var clipped = new List<RailInterval>();
        foreach (var interval in intervals)
        {
            if (RailInterval.Overlap(interval, span) is { } overlap)
                clipped.Add(overlap);
        }

        return Merge(clipped);
    }
}

internal static class RailSnapshots
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Array.AsReadOnly(items.ToArray());
    }
}

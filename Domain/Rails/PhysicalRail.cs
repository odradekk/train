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
    EntityIdExhausted = 11
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
    TransactionExhausted = 7
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
    bool DerivedGraphCurrent);

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
}

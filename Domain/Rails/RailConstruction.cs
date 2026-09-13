using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public sealed record PlannedRailSegment(
    HorizontalStraightAlignment Alignment,
    IReadOnlyList<RailVolumeBox> RailVolumes,
    IReadOnlyList<RailVolumeBox> SleeperVolumes);

public sealed record PlannedRailConnection(int FromSegment, RailEnd FromEnd, int ToSegment, RailEnd ToEnd);

public sealed class RailConstructionPlan
{
    public IReadOnlyList<PlannedRailSegment> Segments { get; }
    public IReadOnlyList<PlannedRailConnection> Connections { get; }

    public RailConstructionPlan(
        IReadOnlyList<PlannedRailSegment> segments,
        IReadOnlyList<PlannedRailConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(connections);
        Segments = segments;
        Connections = connections;
    }
}

public sealed class PreparedRailEdit
{
    public IReadOnlyList<PhysicalRailSegment> Segments { get; }
    public IReadOnlyList<RailConnection> Connections { get; }
    public IReadOnlyList<RailOccupancyCell> Occupancy { get; }
    public IReadOnlyList<WorldVoxelCoord> CellsToAdd { get; }
    public int RequiredEntityIds { get; }
    public int SleeperCount { get; }

    internal PreparedRailEdit(
        IReadOnlyList<PhysicalRailSegment> segments,
        IReadOnlyList<RailConnection> connections,
        IReadOnlyList<RailOccupancyCell> occupancy,
        IReadOnlyList<WorldVoxelCoord> cellsToAdd,
        int requiredEntityIds,
        int sleeperCount)
    {
        Segments = segments;
        Connections = connections;
        Occupancy = occupancy;
        CellsToAdd = cellsToAdd;
        RequiredEntityIds = requiredEntityIds;
        SleeperCount = sleeperCount;
    }
}

public static class RailEditTransaction
{
    public static RailEditStatus TryPrepare(
        RailConstructionPlan plan,
        VoxelWorld world,
        VoxelCatalogSnapshot catalog,
        PhysicalRailState existing,
        out PreparedRailEdit prepared)
    {
        prepared = null!;
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(existing);

        if (plan.Segments.Count == 0)
            return RailEditStatus.InvalidPlan;
        if (!catalog.TryGet(StraightRailFixture.RailDefinitionId, out _))
            return RailEditStatus.UnknownDefinition;

        for (var index = 0; index < plan.Segments.Count; index++)
        {
            var segment = plan.Segments[index];
            if (!segment.Alignment.IsPositiveX)
                return RailEditStatus.UnsupportedGeometry;
            if (segment.RailVolumes.Count == 0 || segment.RailVolumes.Any(volume => !volume.HasPositiveVolume))
                return RailEditStatus.InvalidPlan;
            if (segment.SleeperVolumes.Any(volume => !volume.HasPositiveVolume))
                return RailEditStatus.InvalidPlan;
        }

        foreach (var connection in plan.Connections)
        {
            if (connection.FromSegment < 0 || connection.FromSegment >= plan.Segments.Count ||
                connection.ToSegment < 0 || connection.ToSegment >= plan.Segments.Count ||
                connection.FromSegment == connection.ToSegment ||
                !IsContinuousConnection(plan, connection))
                return RailEditStatus.InvalidPlan;
        }

        var occupancyByCell = new Dictionary<WorldVoxelCoord, List<RailCellSupport>>();
        var uniqueSleepers = new HashSet<(double X, double Z)>();
        for (var index = 0; index < plan.Segments.Count; index++)
        {
            var planned = plan.Segments[index];
            var placeholder = new EntityId((ulong)index + 1);
            CollectOccupancy(planned, placeholder, occupancyByCell);
            foreach (var sleeper in planned.SleeperVolumes)
                uniqueSleepers.Add((sleeper.MinX, sleeper.MinZ));
        }

        if (occupancyByCell.Count == 0)
            return RailEditStatus.InvalidPlan;
        if (HasIndependentOverlapInPlan(occupancyByCell, plan))
            return RailEditStatus.IndependentLineConflict;

        var plannedCells = occupancyByCell.Keys.ToHashSet();
        var cells = occupancyByCell.Keys.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToArray();
        foreach (var cell in cells)
        {
            if (!world.Bounds.Contains(cell))
                return RailEditStatus.OutOfBounds;
            var query = world.Query(cell);
            if (!query.IsOccupied)
                continue;
            if (existing.Occupies(cell))
                return RailEditStatus.IndependentLineConflict;
            return RailEditStatus.WorldObstacle;
        }

        if (HasClearanceConflict(world, plannedCells, plan))
            return RailEditStatus.ClearanceConflict;

        var requiredIds = plan.Segments.Count;
        prepared = new PreparedRailEdit(
            Array.Empty<PhysicalRailSegment>(),
            Array.Empty<RailConnection>(),
            occupancyByCell.Select(pair => new RailOccupancyCell(pair.Key, RailSnapshots.Freeze(pair.Value))).ToArray(),
            cells,
            requiredIds,
            uniqueSleepers.Count);
        return RailEditStatus.Committed;
    }

    public static PhysicalRailState Commit(
        PreparedRailEdit prepared,
        RailConstructionPlan plan,
        ulong firstEntityId)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(plan);
        var segments = new PhysicalRailSegment[plan.Segments.Count];
        var idByIndex = new EntityId[plan.Segments.Count];
        for (var index = 0; index < plan.Segments.Count; index++)
        {
            var id = new EntityId(firstEntityId + (ulong)index);
            idByIndex[index] = id;
            var planned = plan.Segments[index];
            segments[index] = new PhysicalRailSegment(
                id,
                planned.Alignment,
                RailSnapshots.Freeze(planned.RailVolumes),
                RailSnapshots.Freeze(planned.SleeperVolumes));
        }

        var occupancy = new List<RailOccupancyCell>(prepared.Occupancy.Count);
        foreach (var cell in prepared.Occupancy)
        {
            var supports = cell.Supports.Select(support =>
            {
                var segmentIndex = (int)support.SegmentId.Value - 1;
                return support with { SegmentId = idByIndex[segmentIndex] };
            });
            occupancy.Add(new RailOccupancyCell(cell.Cell, RailSnapshots.Freeze(supports)));
        }

        var connections = RailSnapshots.Freeze(plan.Connections
            .Select(connection => new RailConnection(
                idByIndex[connection.FromSegment],
                connection.FromEnd,
                idByIndex[connection.ToSegment],
                connection.ToEnd)));

        return PhysicalRailState.FromCommitted(segments, connections, occupancy, prepared.SleeperCount);
    }

    private static void CollectOccupancy(
        PlannedRailSegment planned,
        EntityId placeholder,
        Dictionary<WorldVoxelCoord, List<RailCellSupport>> occupancy)
    {
        foreach (var volume in planned.RailVolumes.Concat(planned.SleeperVolumes))
        {
            foreach (var cell in CellsTouchedBy(volume))
            {
                if (!volume.TryOverlapX(cell, out var worldX))
                    continue;
                var along = worldX.Translate(-planned.Alignment.StartX);
                if (!occupancy.TryGetValue(cell, out var supports))
                {
                    supports = new List<RailCellSupport>();
                    occupancy[cell] = supports;
                }

                if (!supports.Any(item => item.SegmentId == placeholder && SameInterval(item.WorldX, worldX)))
                    supports.Add(new RailCellSupport(placeholder, worldX, along));
            }
        }
    }

    private static IEnumerable<WorldVoxelCoord> CellsTouchedBy(RailVolumeBox volume)
    {
        var minX = (int)Math.Floor(volume.MinX);
        var maxX = (int)Math.Ceiling(volume.MaxX) - 1;
        var minY = (int)Math.Floor(volume.MinY);
        var maxY = (int)Math.Ceiling(volume.MaxY) - 1;
        var minZ = (int)Math.Floor(volume.MinZ);
        var maxZ = (int)Math.Ceiling(volume.MaxZ) - 1;
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var cell = new WorldVoxelCoord(x, y, z);
            if (volume.OverlapsCell(cell))
                yield return cell;
        }
    }

    private static bool SameInterval(RailInterval left, RailInterval right) =>
        left.Start == right.Start && left.End == right.End;

    private static bool HasIndependentOverlapInPlan(
        Dictionary<WorldVoxelCoord, List<RailCellSupport>> occupancy,
        RailConstructionPlan plan)
    {
        foreach (var (cell, supports) in occupancy)
        {
            var indices = supports
                .Select(support => (int)support.SegmentId.Value - 1)
                .Distinct()
                .ToArray();
            for (var left = 0; left < indices.Length; left++)
            for (var right = left + 1; right < indices.Length; right++)
            {
                if (!SharesTechnicalSeam(plan, indices[left], indices[right], cell))
                    return true;
            }
        }

        return false;
    }

    private static bool SharesTechnicalSeam(
        RailConstructionPlan plan,
        int left,
        int right,
        WorldVoxelCoord cell)
    {
        foreach (var connection in plan.Connections)
        {
            var paired =
                connection.FromSegment == left && connection.ToSegment == right ||
                connection.FromSegment == right && connection.ToSegment == left;
            if (!paired || !TryConnectionPoint(plan, connection, out var seamX, out _, out _))
                continue;
            if (cell.X <= seamX && seamX <= cell.X + 1.0)
                return true;
        }

        return false;
    }

    private static bool IsContinuousConnection(RailConstructionPlan plan, PlannedRailConnection connection)
    {
        if (!TryEndPoint(plan.Segments[connection.FromSegment], connection.FromEnd, out var fromX, out var fromY, out var fromZ) ||
            !TryEndPoint(plan.Segments[connection.ToSegment], connection.ToEnd, out var toX, out var toY, out var toZ))
            return false;
        if (connection.FromEnd == connection.ToEnd)
            return false;
        return fromX == toX && fromY == toY && fromZ == toZ;
    }

    private static bool TryConnectionPoint(
        RailConstructionPlan plan,
        PlannedRailConnection connection,
        out double x,
        out double y,
        out double z)
    {
        x = y = z = 0;
        if (!IsContinuousConnection(plan, connection))
            return false;
        return TryEndPoint(plan.Segments[connection.FromSegment], connection.FromEnd, out x, out y, out z);
    }

    private static bool TryEndPoint(
        PlannedRailSegment segment,
        RailEnd end,
        out double x,
        out double y,
        out double z)
    {
        switch (end)
        {
            case RailEnd.Start:
                x = segment.Alignment.StartX;
                y = segment.Alignment.StartY;
                z = segment.Alignment.StartZ;
                return true;
            case RailEnd.End:
                x = segment.Alignment.EndX;
                y = segment.Alignment.EndY;
                z = segment.Alignment.EndZ;
                return true;
            default:
                x = y = z = 0;
                return false;
        }
    }

    private static bool HasClearanceConflict(
        VoxelWorld world,
        IReadOnlySet<WorldVoxelCoord> plannedCells,
        RailConstructionPlan plan)
    {
        var clearance = plan.Segments
            .Select(segment => StraightRailFixture.SweptClearanceFor(segment.Alignment))
            .ToArray();
        foreach (var (position, _) in world.OccupiedVoxels())
        {
            if (plannedCells.Contains(position))
                continue;
            if (clearance.Any(box => box.OverlapsCell(position)))
                return true;
        }

        return false;
    }
}

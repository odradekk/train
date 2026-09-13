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
                connection.FromSegment == connection.ToSegment)
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

        if (existing.Segments.Count > 0 && CellsOverlapExistingIndependentLine(cells, existing, plan))
            return RailEditStatus.IndependentLineConflict;

        if (HasClearanceConflict(world, existing, occupancyByCell.Keys))
            return RailEditStatus.ClearanceConflict;

        var requiredIds = plan.Segments.Count;
        prepared = new PreparedRailEdit(
            Array.Empty<PhysicalRailSegment>(),
            Array.Empty<RailConnection>(),
            occupancyByCell.Select(pair => new RailOccupancyCell(pair.Key, pair.Value)).ToArray(),
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
            segments[index] = new PhysicalRailSegment(id, planned.Alignment, planned.RailVolumes, planned.SleeperVolumes);
        }

        var occupancy = new List<RailOccupancyCell>(prepared.Occupancy.Count);
        foreach (var cell in prepared.Occupancy)
        {
            var supports = cell.Supports.Select(support =>
            {
                var segmentIndex = (int)support.SegmentId.Value - 1;
                return support with { SegmentId = idByIndex[segmentIndex] };
            }).ToArray();
            occupancy.Add(new RailOccupancyCell(cell.Cell, supports));
        }

        var connections = plan.Connections
            .Select(connection => new RailConnection(
                idByIndex[connection.FromSegment],
                connection.FromEnd,
                idByIndex[connection.ToSegment],
                connection.ToEnd))
            .ToArray();

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

    private static bool CellsOverlapExistingIndependentLine(
        IReadOnlyList<WorldVoxelCoord> cells,
        PhysicalRailState existing,
        RailConstructionPlan plan)
    {
        foreach (var cell in cells)
        {
            if (!existing.TryGetOccupancy(cell, out var occupancy))
                continue;
            if (!IsTechnicalSeamShare(occupancy, plan))
                return true;
        }

        return false;
    }

    private static bool IsTechnicalSeamShare(RailOccupancyCell occupancy, RailConstructionPlan plan)
    {
        if (plan.Connections.Count == 0)
            return false;
        return occupancy.Cell.X == (int)StraightRailFixture.SeamX ||
               occupancy.Cell.X == (int)StraightRailFixture.SeamX - 1;
    }

    private static bool HasClearanceConflict(
        VoxelWorld world,
        PhysicalRailState existing,
        IReadOnlyCollection<WorldVoxelCoord> plannedCells)
    {
        var clearance = StraightRailFixture.SmallVehicleSweptClearance();
        foreach (var (position, _) in world.OccupiedVoxels())
        {
            if (plannedCells.Contains(position) || existing.Occupies(position))
                continue;
            if (clearance.OverlapsCell(position))
                return true;
        }

        return false;
    }
}

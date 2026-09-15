using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public sealed class PhysicalRailState
{
    private readonly List<PhysicalRailSegment> _segments = new();
    private readonly List<RailConnection> _connections = new();
    private readonly Dictionary<WorldVoxelCoord, RailOccupancyCell> _occupancy = new();
    private readonly HashSet<WorldVoxelCoord> _destroyed = new();
    private DerivedRailGraph _derived;
    private bool _derivedCurrent;

    public IReadOnlyList<PhysicalRailSegment> Segments => RailSnapshots.Freeze(_segments);
    public IReadOnlyList<RailConnection> Connections => RailSnapshots.Freeze(_connections);
    public IReadOnlyList<RailOccupancyCell> OccupancyCells => _occupancy.Values
        .OrderBy(item => item.Cell.X)
        .ThenBy(item => item.Cell.Y)
        .ThenBy(item => item.Cell.Z)
        .ToArray();
    public int OccupiedCellCount => _occupancy.Count - _destroyed.Count;
    public int RegisteredCellCount => _occupancy.Count;
    public int SleeperCount { get; private set; }
    public bool HasRailway => _segments.Count > 0;
    public DerivedRailGraph DerivedGraph => _derived;

    public PhysicalRailState()
    {
        _derived = DerivedRailGraph.Stale(Array.Empty<RailConnection>());
        _derivedCurrent = false;
    }

    public static PhysicalRailState FromCommitted(
        IReadOnlyList<PhysicalRailSegment> segments,
        IReadOnlyList<RailConnection> connections,
        IReadOnlyList<RailOccupancyCell> occupancy,
        int sleeperCount)
    {
        var state = new PhysicalRailState { SleeperCount = sleeperCount };
        state._segments.AddRange(segments);
        state._connections.AddRange(connections);
        foreach (var cell in occupancy)
            state._occupancy[cell.Cell] = new RailOccupancyCell(cell.Cell, RailSnapshots.Freeze(cell.Supports));
        state.RebuildDerivedGraph();
        return state;
    }

    public void RestoreFrom(
        IReadOnlyList<PhysicalRailSegment> segments,
        IReadOnlyList<RailConnection> connections,
        IReadOnlyList<RailOccupancyCell> occupancy,
        int sleeperCount,
        VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(world);
        Clear();
        SleeperCount = sleeperCount;
        _segments.AddRange(segments);
        _connections.AddRange(connections);
        foreach (var cell in occupancy)
            _occupancy[cell.Cell] = new RailOccupancyCell(cell.Cell, RailSnapshots.Freeze(cell.Supports));
        foreach (var cell in _occupancy.Keys)
        {
            if (!world.Query(cell).IsOccupied)
                _destroyed.Add(cell);
        }

        RebuildDerivedGraph();
    }

    public void MergeCommitted(PhysicalRailState committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        _segments.AddRange(committed._segments);
        _connections.AddRange(committed._connections);
        foreach (var pair in committed._occupancy)
        {
            if (_occupancy.ContainsKey(pair.Key))
                throw new InvalidOperationException($"Prepared rail occupancy overlaps existing cell {pair.Key}.");
            _occupancy[pair.Key] = pair.Value;
        }

        SleeperCount = _segments
            .SelectMany(segment => segment.SleeperVolumes)
            .Select(volume => (volume.MinX, volume.MinZ))
            .Distinct()
            .Count();
        RebuildDerivedGraph();
    }

    public void Clear()
    {
        _segments.Clear();
        _connections.Clear();
        _occupancy.Clear();
        _destroyed.Clear();
        SleeperCount = 0;
        _derived = DerivedRailGraph.Stale(Array.Empty<RailConnection>());
        _derivedCurrent = false;
    }

    public bool Occupies(WorldVoxelCoord cell) => _occupancy.ContainsKey(cell);

    public bool TryGetOccupancy(WorldVoxelCoord cell, out RailOccupancyCell occupancy) =>
        _occupancy.TryGetValue(cell, out occupancy!);

    public bool IsDestroyed(WorldVoxelCoord cell) => _destroyed.Contains(cell);

    public RailQuery Query(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var occupancy = _occupancy.Values
            .OrderBy(item => item.Cell.X)
            .ThenBy(item => item.Cell.Y)
            .ThenBy(item => item.Cell.Z)
            .ToArray();
        var occupied = occupancy
            .Where(item => world.Query(item.Cell).IsOccupied && !_destroyed.Contains(item.Cell))
            .Select(item => item.Cell)
            .ToArray();
        var segments = _segments.Select(segment => ToSegmentQuery(segment, world)).ToArray();
        return new RailQuery(
            segments,
            RailSnapshots.Freeze(_connections),
            occupied,
            occupancy.Select(cell => new RailOccupancyCell(cell.Cell, RailSnapshots.Freeze(cell.Supports))).ToArray(),
            SleeperCount);
    }

    public IReadOnlyList<RailInterval> TraversableWorldX(EntityId segmentId, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var segment = _segments.FirstOrDefault(item => item.Id == segmentId);
        if (segment is null)
            return Array.Empty<RailInterval>();
        return TraversableFor(segment, world, alongTrack: false);
    }

    public IReadOnlyList<RailInterval> TraversableAlongTrack(EntityId segmentId, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var segment = _segments.FirstOrDefault(item => item.Id == segmentId);
        if (segment is null)
            return Array.Empty<RailInterval>();
        return TraversableFor(segment, world, alongTrack: true);
    }

    public bool AreConnected(EntityId from, EntityId to) =>
        _connections.Any(connection =>
            connection.FromSegment == from && connection.ToSegment == to ||
            connection.FromSegment == to && connection.ToSegment == from);

    public void NoteWorldChange(WorldVoxelCoord cell, bool occupancyChanged)
    {
        if (!_occupancy.ContainsKey(cell))
            return;
        if (occupancyChanged)
            _destroyed.Add(cell);
        MarkDerivedStale();
    }

    public void NoteRestored(WorldVoxelCoord cell)
    {
        _destroyed.Remove(cell);
        MarkDerivedStale();
    }

    public void RebuildDerivedGraph()
    {
        _derived = new DerivedRailGraph(
            true,
            _segments.Select(segment => ToSegmentQuery(segment, world: null)).ToArray(),
            _connections.ToArray());
        _derivedCurrent = true;
    }

    public DerivedRailGraph QueryDerivedGraph() =>
        _derivedCurrent ? _derived : DerivedRailGraph.Stale(_connections.ToArray());

    public RailRepairStatus ValidateRepair(
        WorldVoxelCoord cell,
        VoxelWorld world,
        out RailOccupancyCell occupancy)
    {
        occupancy = null!;
        ArgumentNullException.ThrowIfNull(world);
        if (!_occupancy.TryGetValue(cell, out occupancy!))
            return RailRepairStatus.UnknownCell;
        if (!world.Bounds.Contains(cell))
            return RailRepairStatus.InvalidRepair;
        var query = world.Query(cell);
        if (query.IsOccupied)
        {
            if (query.Voxel!.DefinitionId != StraightRailFixture.RailDefinitionId)
                return RailRepairStatus.Conflict;
            if (query.Voxel.CurrentDurability >= StraightRailFixture.RailCellDurability && !_destroyed.Contains(cell))
                return RailRepairStatus.Intact;
        }

        if (!query.IsOccupied && !_destroyed.Contains(cell) && !_occupancy.ContainsKey(cell))
            return RailRepairStatus.Empty;
        return RailRepairStatus.Repaired;
    }

    public RailAuthoritySnapshot Capture(VoxelWorld world, ulong nextEntityId, ulong transactionCount)
    {
        ArgumentNullException.ThrowIfNull(world);
        var occupied = world.OccupiedVoxels()
            .Where(item => item.Value.DefinitionId == StraightRailFixture.RailDefinitionId)
            .OrderBy(item => item.Key.X)
            .ThenBy(item => item.Key.Y)
            .ThenBy(item => item.Key.Z)
            .Select(item => (item.Key, item.Value.DefinitionId, item.Value.CurrentDurability))
            .ToArray();
        return new RailAuthoritySnapshot(
            world.VoxelCount,
            _segments.Count,
            occupied.Length,
            _connections.Count,
            _segments.Select(segment => segment.Id).ToArray(),
            occupied,
            nextEntityId,
            transactionCount,
            _derivedCurrent);
    }

    private void MarkDerivedStale()
    {
        _derivedCurrent = false;
        _derived = DerivedRailGraph.Stale(_connections.ToArray());
    }

    private RailSegmentQuery ToSegmentQuery(PhysicalRailSegment segment, VoxelWorld? world)
    {
        var worldX = world is null
            ? TraversableFromSupports(segment, alongTrack: false, requireWorld: null)
            : TraversableFor(segment, world, alongTrack: false);
        var along = world is null
            ? worldX.Select(interval => interval.Translate(-segment.Alignment.StartX)).ToArray()
            : TraversableFor(segment, world, alongTrack: true);
        var occupied = _occupancy.Values.Count(item =>
            item.Supports.Any(support => support.SegmentId == segment.Id) &&
            !_destroyed.Contains(item.Cell) &&
            (world is null || world.Query(item.Cell).IsOccupied));
        return new RailSegmentQuery(segment.Id, segment.Alignment, worldX, along, occupied);
    }

    private IReadOnlyList<RailInterval> TraversableFor(
        PhysicalRailSegment segment,
        VoxelWorld world,
        bool alongTrack) =>
        TraversableFromSupports(segment, alongTrack, world);

    private IReadOnlyList<RailInterval> TraversableFromSupports(
        PhysicalRailSegment segment,
        bool alongTrack,
        VoxelWorld? requireWorld)
    {
        var supported = new List<RailInterval>();
        var holes = new List<RailInterval>();
        foreach (var occupancy in _occupancy.Values)
        {
            foreach (var support in occupancy.Supports.Where(item => item.SegmentId == segment.Id))
            {
                var interval = alongTrack ? support.AlongTrack : support.WorldX;
                supported.Add(interval);
                var present = !_destroyed.Contains(occupancy.Cell) &&
                              (requireWorld is null ||
                               requireWorld.Query(occupancy.Cell).IsOccupied &&
                               requireWorld.Query(occupancy.Cell).Voxel!.DefinitionId ==
                               StraightRailFixture.RailDefinitionId);
                if (!present)
                    holes.Add(interval);
            }
        }

        var span = alongTrack ? new RailInterval(0, segment.Alignment.Length) : segment.Alignment.WorldX;
        return RailIntervals.Subtract(RailIntervals.ClipTo(supported, span), holes);
    }
}

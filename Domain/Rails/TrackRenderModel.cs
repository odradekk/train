using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public enum TrackSolidKind
{
    Rail = 0,
    Sleeper = 1
}

public sealed record TrackSolid(TrackSolidKind Kind, RailVolumeBox Volume, EntityId SegmentId);

public sealed record TrackRenderSection(
    ChunkCoord Chunk,
    IReadOnlyList<TrackSolid> Solids);

public sealed class TrackRenderModel
{
    public IReadOnlyList<TrackRenderSection> Sections { get; }
    public int RailSolidCount => Sections.Sum(section => section.Solids.Count(solid => solid.Kind == TrackSolidKind.Rail));
    public int SleeperSolidCount => Sections.Sum(section => section.Solids.Count(solid => solid.Kind == TrackSolidKind.Sleeper));
    public int SolidCount => Sections.Sum(section => section.Solids.Count);

    public TrackRenderModel(IReadOnlyList<TrackRenderSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        Sections = sections;
    }

    public static TrackRenderModel From(
        PhysicalRailState state,
        VoxelWorld world,
        int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(world);
        var byChunk = new SortedDictionary<ChunkCoord, List<TrackSolid>>();
        var seenSleepers = new HashSet<RailVolumeBox>();
        foreach (var segment in state.Segments)
        {
            foreach (var volume in segment.RailVolumes)
                AddFragments(byChunk, world, state, segment.Id, TrackSolidKind.Rail, volume, chunkSize);
            foreach (var volume in segment.SleeperVolumes)
            {
                if (!seenSleepers.Add(volume))
                    continue;
                AddFragments(byChunk, world, state, segment.Id, TrackSolidKind.Sleeper, volume, chunkSize);
            }
        }

        var sections = byChunk
            .Select(pair => new TrackRenderSection(pair.Key, pair.Value))
            .ToArray();
        return new TrackRenderModel(sections);
    }

    public TrackRenderModel RefreshLocal(
        PhysicalRailState state,
        VoxelWorld world,
        int chunkSize,
        IReadOnlyList<ChunkCoord> dirtyChunks)
    {
        ArgumentNullException.ThrowIfNull(dirtyChunks);
        var rebuilt = From(state, world, chunkSize);
        if (dirtyChunks.Count == 0)
            return rebuilt;

        var dirty = dirtyChunks.ToHashSet();
        var kept = Sections.Where(section => !dirty.Contains(section.Chunk)).ToList();
        foreach (var section in rebuilt.Sections)
        {
            if (dirty.Contains(section.Chunk))
                kept.Add(section);
        }

        return new TrackRenderModel(kept.OrderBy(section => section.Chunk).ToArray());
    }

    public bool HasGapNear(WorldVoxelCoord cell) =>
        !Sections.Any(section => section.Solids.Any(solid => solid.Volume.OverlapsCell(cell)));

    private static void AddFragments(
        IDictionary<ChunkCoord, List<TrackSolid>> byChunk,
        VoxelWorld world,
        PhysicalRailState state,
        EntityId segmentId,
        TrackSolidKind kind,
        RailVolumeBox volume,
        int chunkSize)
    {
        var holes = new List<RailInterval>();
        foreach (var occupancy in state.OccupancyCells)
        {
            if (!volume.OverlapsCell(occupancy.Cell))
                continue;
            var present = world.Query(occupancy.Cell).IsOccupied &&
                          !state.IsDestroyed(occupancy.Cell) &&
                          world.Query(occupancy.Cell).Voxel!.DefinitionId == StraightRailFixture.RailDefinitionId;
            if (present)
                continue;
            if (volume.TryOverlapX(occupancy.Cell, out var hole))
                holes.Add(hole);
        }

        var remaining = RailIntervals.Subtract(new[] { new RailInterval(volume.MinX, volume.MaxX) }, holes);
        foreach (var interval in remaining)
        {
            var start = interval.Start;
            while (start < interval.End)
            {
                var cellX = (int)Math.Floor(start);
                var chunk = VoxelChunks.Of(
                    new WorldVoxelCoord(cellX, (int)Math.Floor(volume.MinY), (int)Math.Floor(volume.MinZ)),
                    chunkSize);
                var chunkEndX = (chunk.X + 1) * (double)chunkSize;
                var pieceEnd = Math.Min(interval.End, chunkEndX);
                if (pieceEnd <= start)
                    break;
                var fragment = volume with { MinX = start, MaxX = pieceEnd };
                if (!byChunk.TryGetValue(chunk, out var solids))
                {
                    solids = new List<TrackSolid>();
                    byChunk[chunk] = solids;
                }

                solids.Add(new TrackSolid(kind, fragment, segmentId));
                start = pieceEnd;
            }
        }
    }

}

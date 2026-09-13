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

public sealed record TrackRenderRefresh(
    TrackRenderModel Model,
    IReadOnlyList<ChunkCoord> RebuiltChunks,
    IReadOnlyList<ChunkCoord> RetainedChunks);

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
        int chunkSize) =>
        new(BuildSections(state, world, chunkSize, onlyChunks: null));

    public TrackRenderRefresh RefreshLocal(
        PhysicalRailState state,
        VoxelWorld world,
        int chunkSize,
        IReadOnlyList<ChunkCoord> dirtyChunks)
    {
        ArgumentNullException.ThrowIfNull(dirtyChunks);
        if (dirtyChunks.Count == 0)
            return new TrackRenderRefresh(this, Array.Empty<ChunkCoord>(), Sections.Select(section => section.Chunk).ToArray());

        var dirty = dirtyChunks.ToHashSet();
        var rebuilt = BuildSections(state, world, chunkSize, dirty);
        var kept = Sections.Where(section => !dirty.Contains(section.Chunk)).ToList();
        var retained = kept.Select(section => section.Chunk).ToArray();
        kept.AddRange(rebuilt);
        return new TrackRenderRefresh(
            new TrackRenderModel(kept.OrderBy(section => section.Chunk).ToArray()),
            dirty.OrderBy(chunk => chunk).ToArray(),
            retained);
    }

    public bool HasGapNear(WorldVoxelCoord cell) =>
        !Sections.Any(section => section.Solids.Any(solid => solid.Volume.OverlapsCell(cell)));

    private static IReadOnlyList<TrackRenderSection> BuildSections(
        PhysicalRailState state,
        VoxelWorld world,
        int chunkSize,
        IReadOnlySet<ChunkCoord>? onlyChunks)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(world);
        var byChunk = new SortedDictionary<ChunkCoord, List<TrackSolid>>();
        var seenSleepers = new HashSet<RailVolumeBox>();
        foreach (var segment in state.Segments)
        {
            foreach (var volume in segment.RailVolumes)
                AddFragments(byChunk, world, state, segment.Id, TrackSolidKind.Rail, volume, chunkSize, onlyChunks);
            foreach (var volume in segment.SleeperVolumes)
            {
                if (!seenSleepers.Add(volume))
                    continue;
                AddFragments(byChunk, world, state, segment.Id, TrackSolidKind.Sleeper, volume, chunkSize, onlyChunks);
            }
        }

        return byChunk
            .Select(pair => new TrackRenderSection(pair.Key, pair.Value))
            .ToArray();
    }

    private static void AddFragments(
        IDictionary<ChunkCoord, List<TrackSolid>> byChunk,
        VoxelWorld world,
        PhysicalRailState state,
        EntityId segmentId,
        TrackSolidKind kind,
        RailVolumeBox volume,
        int chunkSize,
        IReadOnlySet<ChunkCoord>? onlyChunks)
    {
        if (onlyChunks is not null && !OverlapsAnyChunk(volume, onlyChunks, chunkSize))
            return;

        var holes = new List<RailVolumeBox>();
        foreach (var occupancy in state.OccupancyCells)
        {
            if (!volume.OverlapsCell(occupancy.Cell))
                continue;
            var present = world.Query(occupancy.Cell).IsOccupied &&
                          !state.IsDestroyed(occupancy.Cell) &&
                          world.Query(occupancy.Cell).Voxel!.DefinitionId == StraightRailFixture.RailDefinitionId;
            if (present)
                continue;
            holes.Add(RailVolumeBox.ForCell(occupancy.Cell));
        }

        foreach (var remaining in RailVolumeBox.Subtract(volume, holes))
        foreach (var chunk in ChunksOverlapping(remaining, chunkSize))
        {
            if (onlyChunks is not null && !onlyChunks.Contains(chunk))
                continue;
            if (remaining.Intersection(RailVolumeBox.ForChunk(chunk, chunkSize)) is not { } fragment)
                continue;
            if (!byChunk.TryGetValue(chunk, out var solids))
            {
                solids = new List<TrackSolid>();
                byChunk[chunk] = solids;
            }

            solids.Add(new TrackSolid(kind, fragment, segmentId));
        }
    }

    private static bool OverlapsAnyChunk(
        RailVolumeBox volume,
        IReadOnlySet<ChunkCoord> chunks,
        int chunkSize)
    {
        foreach (var chunk in chunks)
        {
            if (volume.Overlaps(RailVolumeBox.ForChunk(chunk, chunkSize)))
                return true;
        }

        return false;
    }

    private static IEnumerable<ChunkCoord> ChunksOverlapping(RailVolumeBox volume, int chunkSize)
    {
        if (!volume.HasPositiveVolume)
            yield break;

        var min = new WorldVoxelCoord(
            (int)Math.Floor(volume.MinX),
            (int)Math.Floor(volume.MinY),
            (int)Math.Floor(volume.MinZ));
        var max = new WorldVoxelCoord(
            Math.Max(min.X, (int)Math.Ceiling(volume.MaxX) - 1),
            Math.Max(min.Y, (int)Math.Ceiling(volume.MaxY) - 1),
            Math.Max(min.Z, (int)Math.Ceiling(volume.MaxZ) - 1));
        var minChunk = VoxelChunks.Of(min, chunkSize);
        var maxChunk = VoxelChunks.Of(max, chunkSize);
        for (var x = minChunk.X; x <= maxChunk.X; x++)
        for (var y = minChunk.Y; y <= maxChunk.Y; y++)
        for (var z = minChunk.Z; z <= maxChunk.Z; z++)
        {
            var chunk = new ChunkCoord(x, y, z);
            if (volume.Overlaps(RailVolumeBox.ForChunk(chunk, chunkSize)))
                yield return chunk;
        }
    }
}

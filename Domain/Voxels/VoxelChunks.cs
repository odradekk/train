namespace Train.Domain.Voxels;

public readonly record struct ChunkCoord(int X, int Y, int Z) : IComparable<ChunkCoord>
{
    public int CompareTo(ChunkCoord other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
            return x;
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }
}

public static class VoxelChunks
{
    private static readonly (int X, int Y, int Z)[] NeighborOffsets =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)
    };

    public static ChunkCoord Of(WorldVoxelCoord position, int chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");
        return new(FloorDiv(position.X, chunkSize), FloorDiv(position.Y, chunkSize), FloorDiv(position.Z, chunkSize));
    }

    public static IReadOnlyList<ChunkCoord> All(VoxelWorldIdentity identity)
    {
        var size = identity.ChunkSize;
        var bounds = identity.Bounds;
        var chunks = new List<ChunkCoord>(
            DivCeil(bounds.SizeX, size) * DivCeil(bounds.SizeY, size) * DivCeil(bounds.SizeZ, size));
        for (var x = 0; x < DivCeil(bounds.SizeX, size); x++)
        for (var y = 0; y < DivCeil(bounds.SizeY, size); y++)
        for (var z = 0; z < DivCeil(bounds.SizeZ, size); z++)
            chunks.Add(new ChunkCoord(x, y, z));
        return chunks;
    }

    public static IReadOnlyList<ChunkCoord> AffectedByOccupancy(WorldVoxelCoord position, VoxelWorldIdentity identity)
    {
        var dirty = new SortedSet<ChunkCoord>();
        if (identity.Bounds.Contains(position))
            dirty.Add(Of(position, identity.ChunkSize));
        foreach (var (x, y, z) in NeighborOffsets)
        {
            var neighbor = new WorldVoxelCoord(position.X + x, position.Y + y, position.Z + z);
            if (identity.Bounds.Contains(neighbor))
                dirty.Add(Of(neighbor, identity.ChunkSize));
        }

        return dirty.ToArray();
    }

    public static IReadOnlyList<ChunkCoord> AffectedByDurability(WorldVoxelCoord position, VoxelWorldIdentity identity) =>
        identity.Bounds.Contains(position)
            ? new[] { Of(position, identity.ChunkSize) }
            : Array.Empty<ChunkCoord>();

    private static int DivCeil(int value, int divisor) => (value + divisor - 1) / divisor;

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
}

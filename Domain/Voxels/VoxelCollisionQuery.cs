namespace Train.Domain.Voxels;

public readonly record struct VoxelCollisionBox
{
    public double MinX { get; }
    public double MinY { get; }
    public double MinZ { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double MaxZ { get; }

    public VoxelCollisionBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(minZ) ||
            !IsFinite(maxX) || !IsFinite(maxY) || !IsFinite(maxZ))
            throw new ArgumentOutOfRangeException(nameof(minX), "Collision probe coordinates must be finite.");
        if (maxX <= minX || maxY <= minY || maxZ <= minZ)
            throw new ArgumentOutOfRangeException(nameof(maxX), "Collision probe must have a positive volume.");

        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
    }

    public static VoxelCollisionBox ForCell(WorldVoxelCoord cell) =>
        new(cell.X, cell.Y, cell.Z, cell.X + 1, cell.Y + 1, cell.Z + 1);

    public static VoxelCollisionBox ForWorld(VoxelWorldBounds bounds) =>
        new(0, 0, 0, bounds.SizeX, bounds.SizeY, bounds.SizeZ);

    public static bool TryCreate(
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ,
        out VoxelCollisionBox box)
    {
        box = default;
        if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(minZ) ||
            !IsFinite(maxX) || !IsFinite(maxY) || !IsFinite(maxZ) ||
            maxX <= minX || maxY <= minY || maxZ <= minZ)
            return false;

        box = new VoxelCollisionBox(minX, minY, minZ, maxX, maxY, maxZ);
        return true;
    }

    public bool Overlaps(VoxelCollisionBox other) =>
        MinX < other.MaxX && MaxX > other.MinX &&
        MinY < other.MaxY && MaxY > other.MinY &&
        MinZ < other.MaxZ && MaxZ > other.MinZ;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

public enum VoxelCollisionQueryStatus
{
    Hit,
    Miss,
    OutOfBounds,
    InvalidQuery
}

public sealed record VoxelCollisionHit(
    WorldVoxelCoord Cell,
    VoxelDefinitionId DefinitionId,
    VoxelCollisionKind Kind);

public sealed record VoxelCollisionQueryResult(
    VoxelCollisionQueryStatus Status,
    VoxelCollisionBox? Probe,
    IReadOnlyList<VoxelCollisionHit> Hits)
{
    public bool HasHit => Status == VoxelCollisionQueryStatus.Hit && Hits.Count > 0;

    public static VoxelCollisionQueryResult Invalid() =>
        new(VoxelCollisionQueryStatus.InvalidQuery, null, Array.Empty<VoxelCollisionHit>());

    public static VoxelCollisionQueryResult OutOfBounds(VoxelCollisionBox probe) =>
        new(VoxelCollisionQueryStatus.OutOfBounds, probe, Array.Empty<VoxelCollisionHit>());

    public static VoxelCollisionQueryResult Miss(VoxelCollisionBox probe) =>
        new(VoxelCollisionQueryStatus.Miss, probe, Array.Empty<VoxelCollisionHit>());

    public static VoxelCollisionQueryResult Hit(VoxelCollisionBox probe, IReadOnlyList<VoxelCollisionHit> hits) =>
        new(VoxelCollisionQueryStatus.Hit, probe, hits);
}

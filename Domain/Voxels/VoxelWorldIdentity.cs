namespace Train.Domain.Voxels;

public enum VoxelDevelopmentSlice
{
    Slice1 = 1,
    Slice2 = 2
}

public readonly record struct VoxelWorldIdentity
{
    public const string Slice1GeneratorVersion = "slice-1-integer-fixture-v1";
    public const string Slice2GeneratorVersion = "slice-2-integer-fixture-v1";

    public VoxelDevelopmentSlice Slice { get; init; }
    public ulong Seed { get; init; }
    public string GeneratorVersion { get; init; }
    public VoxelWorldBounds Bounds { get; init; }
    public int ChunkSize { get; init; }

    public static VoxelWorldIdentity Slice1 { get; } = new(
        VoxelDevelopmentSlice.Slice1,
        1,
        Slice1GeneratorVersion,
        new VoxelWorldBounds(16, 16, 16),
        16);

    public static VoxelWorldIdentity Slice2 { get; } = new(
        VoxelDevelopmentSlice.Slice2,
        2,
        Slice2GeneratorVersion,
        new VoxelWorldBounds(32, 32, 32),
        16);

    public VoxelWorldIdentity(
        VoxelDevelopmentSlice slice,
        ulong seed,
        string generatorVersion,
        VoxelWorldBounds bounds,
        int chunkSize)
    {
        if (string.IsNullOrWhiteSpace(generatorVersion))
            throw new ArgumentException("Generator version is required.", nameof(generatorVersion));
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");

        Slice = slice;
        Seed = seed;
        GeneratorVersion = generatorVersion;
        Bounds = bounds;
        ChunkSize = chunkSize;
    }

    public static VoxelWorldIdentity FromBounds(VoxelWorldBounds bounds, int chunkSize = 16) =>
        new(VoxelDevelopmentSlice.Slice1, 0, "unspecified", bounds, chunkSize);
}

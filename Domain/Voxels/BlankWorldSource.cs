using System.Globalization;
using Train.Content;
using Train.Domain.WorldEditor;

namespace Train.Domain.Voxels;

/// <summary>An empty, content-independent base. These are conservative development bounds, not a capacity promise.</summary>
public static class BlankWorldSource
{
    public const int Version = 1;
    public const string GeneratorVersion = "blank-world-v1";
    public const int MinSize = 1;
    public const int MaxSizeX = 320;
    public const int MaxSizeY = 32;
    public const int MaxSizeZ = 32;
    public const int ChunkSize = 16;

    public static string ExtentDescription => $"X {MinSize}..{MaxSizeX}, Y {MinSize}..{MaxSizeY}, Z {MinSize}..{MaxSizeZ}";

    public static bool TryIdentity(int sizeX, int sizeY, int sizeZ, ulong seed, out VoxelWorldIdentity identity)
    {
        identity = default;
        if (sizeX is < MinSize or > MaxSizeX || sizeY is < MinSize or > MaxSizeY || sizeZ is < MinSize or > MaxSizeZ)
            return false;
        identity = new VoxelWorldIdentity(VoxelDevelopmentSlice.Blank, seed, GeneratorVersion,
            new VoxelWorldBounds(sizeX, sizeY, sizeZ), ChunkSize);
        return true;
    }

    public static VoxelDevelopmentSession Establish(
        VoxelWorldIdentity identity, VoxelCatalog catalog, CompositeCatalog composites, IWorldSaveStore? store)
    {
        if (!TryIdentity(identity.Bounds.SizeX, identity.Bounds.SizeY, identity.Bounds.SizeZ, identity.Seed, out var supported) ||
            identity != supported)
            throw new ArgumentException("Unsupported blank world identity.", nameof(identity));
        var session = new VoxelDevelopmentSession(catalog, identity, store, composites);
        session.BindEstablishedSource(Describe(identity));
        session.MarkNewWorldUnsaved();
        return session;
    }

    private static WorldSource Describe(VoxelWorldIdentity identity) => new(
        WorldSource.BlankKind, WorldSource.BlankKind, Version,
        new WorldSourceParameter[]
        {
            new("base.seed", identity.Seed.ToString(CultureInfo.InvariantCulture)),
            new("base.bounds", $"{identity.Bounds.SizeX},{identity.Bounds.SizeY},{identity.Bounds.SizeZ}"),
            new("base.chunk_size", ChunkSize.ToString(CultureInfo.InvariantCulture))
        }, Array.Empty<WorldSourceDependency>());

    internal static WorldSourceCheck Check(WorldSaveDocument document, VoxelCatalog catalog, CompositeCatalog composites)
    {
        var source = document.Source!;
        if (source.ConfigurationVersion != Version)
            return Reject("source_version", "the blank source version is not supported");
        if (!TryIdentity(document.Bounds.SizeX, document.Bounds.SizeY, document.Bounds.SizeZ, document.Seed, out var identity))
            return Reject("bounds", $"blank extent requires {ExtentDescription}");
        if (document.GeneratorVersion != GeneratorVersion || document.ChunkSize != ChunkSize)
            return Reject("identity", "the blank source substrate or chunk size does not match");
        var expected = Describe(identity);
        if (source.ConfigurationId != WorldSource.BlankKind || source.ContentDependencies.Count != 0 ||
            source.EffectiveParameters.Count != expected.EffectiveParameters.Count ||
            expected.EffectiveParameters.Any(parameter => source.Parameter(parameter.Name) != parameter.Value))
            return Reject("effective_parameters", "the blank source inputs must match the document and have no content dependencies");
        var candidate = Establish(identity, catalog, composites, store: null);
        try
        {
            return candidate.WorldSource!.BaseFingerprint == source.BaseFingerprint
                ? WorldSourceCheck.Pass()
                : Reject("base_fingerprint", "the rebuilt blank base does not match the saved fingerprint");
        }
        finally
        {
            candidate.Detach();
        }
    }

    private static WorldSourceCheck Reject(string relationship, string detail) => WorldSourceCheck.Fail(
        WorldSaveStatus.IncompatibleBase, new WorldSaveDiagnostic("base", "blank", relationship, detail));
}

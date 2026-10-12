using System.Collections.Immutable;

namespace Train.Content.Contracts;

public readonly record struct ContentRoot(string Key);
public sealed record ContentPath(ContentRoot Root, string RelativePath);
public enum ContentEntryKind { File, Directory, Link }
public sealed record ContentFileEntry(string Name, ContentEntryKind Kind);
public enum ContentIoFailure { Missing, LimitExceeded, LinkedPath, Inaccessible }
public abstract record ContentIoResult<T> where T : notnull
{
    private ContentIoResult() { }
    public sealed record Success(T Value) : ContentIoResult<T>;
    public sealed record Failure(ContentIoFailure Kind, string Diagnostic) : ContentIoResult<T>;
}
public interface IContentFiles
{
    ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory);
    ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes);
}
public enum AssetKind { Png, Glb }
public enum AssetInspectionFailure { CannotDecode, UnsupportedContent }
public enum AssetAlphaMode { Opaque, Mask, Blend }
public sealed record ModelBounds(LocalPoint MinimumMeters, LocalPoint MaximumMeters);
public abstract record AssetMetadata
{
    private AssetMetadata() { }
    public sealed record Png(int Width, int Height) : AssetMetadata;
    public sealed record Glb(int MeshNodeCount, ImmutableArray<AssetAlphaMode> Surfaces,
        ModelBounds Bounds, bool HasSkeleton, bool HasMorphTargets, bool HasAnimation,
        bool HasCamera, bool HasLight, bool HasExternalImages) : AssetMetadata;
}
public abstract record AssetInspection
{
    private AssetInspection() { }
    public sealed record Success(AssetMetadata Metadata) : AssetInspection;
    public sealed record Failure(AssetInspectionFailure Kind, string Diagnostic) : AssetInspection;
}
public interface IAssetInspector
{
    AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes);
}
public sealed record ContentAsset(AssetRevision Revision,
    ImmutableArray<byte> Bytes, AssetMetadata Metadata, ImmutableArray<ContentSourceLocation> Sources);
public sealed record ContentInstallation(ContentRoot BuiltIn, ContentRoot Mods,
    ContentRoot? UserLibrary, string GameVersion, ImmutableArray<PackageId> EnabledMods);

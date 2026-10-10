// 内容定义宿主的文件与资产检查接口；只有宿主接触文件系统或图像/模型解码器。
// 读取在调用线程同步完成，检查只报告元数据，不执行内容合法性规则。
using System.Collections.Immutable;

namespace Train.Content.Contracts;

/// <summary>宿主分配的根句柄；可代表开发目录、Mod、用户库或 PCK，内容不解释绝对路径。</summary>
public readonly record struct ContentRoot(string Key);
/// <summary>包根内的规范相对路径；禁止绝对路径、..、空段和路径链接。</summary>
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
    /// <summary>只列直接子项；空相对路径表示根。内容负责排序、文件种类和目录规则。</summary>
    ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory);
    /// <summary>读取至多 maximumBytes 的固定字节；超限失败，不返回截断数据。返回后字节不可改变。</summary>
    ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes);
}
public enum AssetKind { Png, Glb }
public enum AssetInspectionFailure { CannotDecode, UnsupportedContent }
public enum AssetAlphaMode { Opaque, Mask, Blend }
/// <summary>模型节点变换后的包围盒，单位米；原点及允许范围由内容校验。</summary>
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
    /// <summary>检查给定原字节；不重读路径，不回传像素/模型几何，返回后不再使用调用方资源。</summary>
    AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes);
}
/// <summary>检查通过的同一份原始字节和元数据；来源可有多个，修订仅取决于字节。</summary>
public sealed record ContentAsset(AssetRevision Revision, AssetKind Kind,
    ImmutableArray<byte> Bytes, AssetMetadata Metadata, ImmutableArray<ContentSourceLocation> Sources);
/// <summary>启用列表只选 Mod，core 必需；用户库可不提供，草稿目录不参与有效内容扫描。</summary>
public sealed record ContentInstallation(ContentRoot BuiltIn, ContentRoot Mods,
    ContentRoot? UserLibrary, string GameVersion, ImmutableArray<PackageId> EnabledMods);

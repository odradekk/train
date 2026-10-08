// 属于内容层的目录模块。
// 保存体素定义的发布快照与变化通知，按玩法数据和表现资产计算修订及目录差异。
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Train.Core.Content.Behaviors;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>表示内容目录中一个已校验的体素定义，以及加载该定义时确定的来源和修订信息。</summary>
/// <remarks>
/// 定义校验、资产校验和修订计算由内容加载器完成；构造函数只检查必填引用与修订字符串非空。
/// 资产和图片的索引被冻结，定义、来源与图片对象直接保留；图片像素数组仍须由提供方保持稳定。
/// </remarks>
/// <example>语义、资产和发布变化的区分见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
public sealed class VoxelCatalogEntry
{
    /// <summary>获取已规范化且不可变的体素定义。</summary>
    public VoxelDefinition Definition { get; }
    /// <summary>获取定义所属的内容包和包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取语义字段规范编码的 SHA-256 大写十六进制修订。</summary>
    /// <remarks>由加载器按 <see cref="ContentRevisions.ComputeSemanticRevision"/> 生成；构造函数不检查哈希格式或与定义的一致性。</remarks>
    public string SemanticRevision { get; }
    /// <summary>获取按规范化内容路径索引的原始资产字节 SHA-256 大写十六进制修订。</summary>
    /// <remarks>加载器按原始图片文件字节计算修订，解码后像素相同的文件也可能具有不同修订；条目本身不校验路径或修订格式。</remarks>
    public IReadOnlyDictionary<string, string> AssetRevisions { get; }
    /// <summary>获取按规范化内容路径索引的已校验、已解码表现图片。</summary>
    /// <remarks>未提供图片映射时为空；构造函数不检查图片尺寸、像素内容或与表现路径的对应关系。</remarks>
    public IReadOnlyDictionary<string, DecodedImage> PresentationImages { get; }
    /// <summary>获取语义修订、名称、说明、表现形式、表现路径及各路径资产修订共同形成的 SHA-256 大写十六进制发布指纹。</summary>
    /// <remarks>用于目录差异比较；构造函数直接保存传入值，不重新计算或检查哈希格式。</remarks>
    public string PublicationFingerprint { get; }

    /// <summary>创建体素目录条目，并将资产修订和表现图片映射冻结为只读快照。</summary>
    /// <param name="definition">已校验、已规范化的体素定义。</param>
    /// <param name="origin">定义所属的内容包和包内源文件。</param>
    /// <param name="semanticRevision">非空的语义修订字符串；原文保存，不修剪空白。</param>
    /// <param name="assetRevisions">从规范化内容路径到资产修订的映射；允许为空。</param>
    /// <param name="publicationFingerprint">非空的发布指纹；原文保存，不修剪空白。</param>
    /// <param name="presentationImages">从规范化内容路径到已解码图片的映射；为 <see langword="null"/> 时使用空映射。</param>
    /// <remarks>传入冻结字典时连同比较器直接复用；其他字典按 Ordinal 路径比较复制后冻结，不复制映射中的图片对象。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/>、<paramref name="origin"/>、<paramref name="semanticRevision"/>、<paramref name="assetRevisions"/> 或 <paramref name="publicationFingerprint"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="semanticRevision"/> 或 <paramref name="publicationFingerprint"/> 为空字符串。</exception>
    public VoxelCatalogEntry(
        VoxelDefinition definition,
        ContentOrigin origin,
        string semanticRevision,
        IReadOnlyDictionary<string, string> assetRevisions,
        string publicationFingerprint,
        IReadOnlyDictionary<string, DecodedImage>? presentationImages = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentNullException.ThrowIfNull(assetRevisions);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Definition = definition;
        Origin = origin;
        SemanticRevision = semanticRevision;
        AssetRevisions = assetRevisions as FrozenDictionary<string, string> ??
                         assetRevisions.ToFrozenDictionary(StringComparer.Ordinal);
        PresentationImages = presentationImages as FrozenDictionary<string, DecodedImage> ??
                             (presentationImages ?? new Dictionary<string, DecodedImage>()).ToFrozenDictionary(StringComparer.Ordinal);
        PublicationFingerprint = publicationFingerprint;
    }
}

/// <summary>表示某一目录世代完整且不可变的体素定义集合。</summary>
/// <remarks>条目索引被冻结，条目对象直接保留；世代由调用方提供，不检查其取值范围、递增关系或索引键与条目定义身份的一致性。</remarks>
/// <example>首次发布后按 <c>core:ferrous-ore</c> 查找条目的用例见 <c>ContentReloaderTests.Initial_valid_voxel_catalog_commits</c>。</example>
public sealed class VoxelCatalogSnapshot
{
    /// <summary>获取本快照的目录世代；内容加载器从 1 开始发布，仅在体素目录有变化时递增。</summary>
    public long Generation { get; }
    /// <summary>获取按体素定义身份索引的全部条目。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> Entries { get; }

    /// <summary>创建目录快照，并按体素定义标识冻结全部条目。</summary>
    /// <param name="generation">本快照的目录世代；原样保存，不校验范围。</param>
    /// <param name="entries">按体素定义标识索引的完整条目集合；可以为空，调用方须保证条目非 null 且身份与键一致。</param>
    /// <remarks>冻结字典及其比较器直接复用；其他字典按标识的默认相等规则复制后冻结，后续修改原字典不会改变本索引。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <see langword="null"/>。</exception>
    public VoxelCatalogSnapshot(long generation, IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Generation = generation;
        Entries = entries as FrozenDictionary<VoxelDefinitionId, VoxelCatalogEntry> ?? entries.ToFrozenDictionary();
    }

    /// <summary>尝试按体素定义标识取得本世代的目录条目。</summary>
    /// <param name="id">要查找的体素定义标识。</param>
    /// <param name="entry">成功时为对应目录条目；失败时为 <see langword="null"/>。</param>
    /// <returns>存在对应标识时为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    /// <example>读取 <c>core:ferrous-ore</c> 条目的用例见 <c>ContentReloaderTests.Initial_valid_voxel_catalog_commits</c>。</example>
    public bool TryGet(VoxelDefinitionId id, out VoxelCatalogEntry entry) => Entries.TryGetValue(id, out entry!);
}

/// <summary>表示一次体素目录发布相对上一快照的新增、移除和发布内容变化。</summary>
/// <remarks>三组标识被冻结，但构造函数不计算差异或检查各组是否互斥；世代及各组内容的正确性由调用方保证。</remarks>
/// <example>同时新增、移除和改变条目的通知见 <c>ContentReloaderTests.Successful_reload_reports_added_removed_and_pixel_changes</c>。</example>
public sealed class VoxelCatalogChange
{
    /// <summary>获取变化对应的目标目录世代；仅记录调用方提供的值，不表示已执行发布。</summary>
    public long Generation { get; }
    /// <summary>获取新增定义身份。</summary>
    public IReadOnlySet<VoxelDefinitionId> Added { get; }
    /// <summary>获取移除定义身份。</summary>
    public IReadOnlySet<VoxelDefinitionId> Removed { get; }
    /// <summary>获取发布指纹改变的既有定义身份。</summary>
    public IReadOnlySet<VoxelDefinitionId> Changed { get; }

    /// <summary>创建目录变化，并冻结三组体素定义标识。</summary>
    /// <param name="generation">变化对应的目标目录世代；原样保存，不校验范围。</param>
    /// <param name="added">相对上一快照新增的体素定义标识；允许为空。</param>
    /// <param name="removed">相对上一快照移除的体素定义标识；允许为空。</param>
    /// <param name="changed">身份仍存在但发布指纹改变的体素定义标识；允许为空。</param>
    /// <remarks>已有冻结集合及其比较器直接复用；其他集合按标识的默认相等规则复制后冻结，不保证枚举顺序。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="added"/>、<paramref name="removed"/> 或 <paramref name="changed"/> 为 <see langword="null"/>。</exception>
    public VoxelCatalogChange(
        long generation,
        IReadOnlySet<VoxelDefinitionId> added,
        IReadOnlySet<VoxelDefinitionId> removed,
        IReadOnlySet<VoxelDefinitionId> changed)
    {
        Generation = generation;
        Added = Freeze(added);
        Removed = Freeze(removed);
        Changed = Freeze(changed);
    }

    /// <summary>复用已有冻结集合，或按默认标识相等规则复制并冻结变化集合。</summary>
    /// <param name="set">要冻结的标识集合；可以为空，不能为 null。</param>
    /// <returns>不可修改的标识集合；传入冻结集合时保留其比较器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> 为 null。</exception>
    private static IReadOnlySet<VoxelDefinitionId> Freeze(IReadOnlySet<VoxelDefinitionId> set) =>
        set as FrozenSet<VoxelDefinitionId> ?? set.ToFrozenSet();
}

/// <summary>原子保存当前体素目录快照，并在成功发布变化后通知订阅方。</summary>
/// <remarks>候选校验、差异计算、世代递增和通知时机由内容加载器负责；快照替换与通知是两个独立操作，不构成完整重载事务。</remarks>
/// <example>相同内容不替换快照且不通知订阅方的用例见 <c>ContentReloaderTests.Unchanged_reload_keeps_version_and_does_not_notify</c>。</example>
public sealed class VoxelCatalog
{
    /// <summary>保存当前发布快照的引用；尚未发布时为 null，通过 Volatile 读写。</summary>
    private VoxelCatalogSnapshot? _current;

    /// <summary>原子读取当前已发布快照；首次成功发布前为 <see langword="null"/>。</summary>
    /// <remarks>读取后持有的旧快照不会因后续发布而被替换或修改。</remarks>
    public VoxelCatalogSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>由内容加载器在发布有变化的新快照后，同步通知体素目录变化。</summary>
    /// <remarks>逐个调用通知开始时已登记的订阅者；订阅者异常交给故障回调，回调正常返回后继续通知其余订阅者。</remarks>
    /// <example>一个订阅者抛出异常后仍通知下一个，且保留已发布快照；见 <c>ContentReloaderTests.Notify_exception_keeps_the_commit_and_allows_a_later_reload</c>。</example>
    public event Action<VoxelCatalogChange>? Changed;

    /// <summary>原子替换当前已发布快照。</summary>
    /// <param name="snapshot">要发布的完整体素目录快照；其世代由调用方确定。</param>
    /// <remarks>不校验世代递增或内容变化，也不发送事件；变化通知由调用方另行调用 <see cref="NotifyChanged"/> 发出。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> 为 null。</exception>
    /// <example>新快照发布后旧快照仍包含被移除的 <c>core:coal</c>；见 <c>ContentReloaderTests.Successful_reload_reports_added_removed_and_pixel_changes</c>。</example>
    internal void Publish(VoxelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    /// <summary>依次通知本次通知开始时已登记的订阅方，并把每个订阅异常交给故障回调。</summary>
    /// <param name="change">本次通知的目录世代和新增、移除、改变集合。</param>
    /// <param name="onFault">处理单个订阅者异常的回调；正常返回后继续后续通知。</param>
    /// <remarks>无订阅者时直接返回。故障回调自身抛出的异常向调用方传播并终止本次通知；本方法不修改快照，也不检查变化集是否与当前快照一致。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> 或 <paramref name="onFault"/> 为 null。</exception>
    /// <example>订阅者失败不撤销已经完成的提交；见 <c>ContentReloaderTests.Notify_exception_keeps_the_commit_and_allows_a_later_reload</c>。</example>
    internal void NotifyChanged(VoxelCatalogChange change, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(onFault);
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (Action<VoxelCatalogChange> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                onFault(exception);
            }
        }
    }
}

/// <summary>把体素玩法数据和表现绑定分别编码为规范 UTF-8 JSON，并计算语义修订、资产修订和发布指纹。</summary>
/// <remarks>编码使用固定字段顺序；标签按 Ordinal 排序，Behavior 保留定义中的顺序。名称、说明、表现配置及资产字节不参与语义修订。</remarks>
/// <example>标签顺序和 JSON 排版不改变修订，显示字段与图片变化影响发布；见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
internal static class ContentRevisions
{
    /// <summary>语义编码中 encoding_version 的固定值；当前规范编码使用版本 2，版本值本身参与哈希。</summary>
    /// <remarks>与定义文件的 schema_version 分开管理；改变编码规则时需同步调整。修订用途见 docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md。</remarks>
    public const int SemanticEncodingVersion = 2;
    /// <summary>语义编码中 kind 字段的固定值 voxel，用于区分不同种类的内容。</summary>
    public const string VoxelKind = "voxel";

    /// <summary>计算体素定义的语义修订。</summary>
    /// <param name="definition">待编码的已规范化体素定义；不重新执行内容校验。</param>
    /// <returns>内容种类、编码版本、身份、货币价值、质量、耐久上限、标签、Behavior 及其有效参数、允许朝向和碰撞数据的 SHA-256 大写十六进制字符串。</returns>
    /// <remarks>标签排序，Behavior 顺序原样保留；各类行为的参数随类型编码，局部盒坐标仅在 LocalBox 时参与编码。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> 为 <see langword="null"/>。</exception>
    /// <example>将 value 从 100 改为 250 会改变语义修订，调整标签顺序不会；见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
    public static string ComputeSemanticRevision(VoxelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Sha256Hex(EncodeSemantic(definition));
    }

    /// <summary>计算资产原始字节的修订。</summary>
    /// <param name="rawBytes">原始资产文件字节；允许为空，不进行图片解码或格式校验。</param>
    /// <returns>原始字节的 SHA-256 大写十六进制字符串。</returns>
    /// <example>解码像素相同但原始 PNG 字节不同的资产具有不同修订；见 <c>ContentReloaderTests.Same_pixels_different_raw_png_bytes_change_only_the_asset_revision</c>。</example>
    public static string ComputeAssetRevision(ReadOnlySpan<byte> rawBytes) =>
        Sha256Hex(rawBytes);

    /// <summary>计算体素定义一次完整发布的指纹。</summary>
    /// <param name="definition">待编码的已规范化体素定义。</param>
    /// <param name="semanticRevision">该定义已经计算出的非空语义修订；原文编码，不重新计算或验证哈希格式。</param>
    /// <param name="assetRevisions">表现路径对应的资产修订映射，须包含四种表现绑定引用的所有路径；多余的键不参与编码。</param>
    /// <returns>语义修订、名称、说明、表现形式、图标和各体素面路径，以及去重排序后每条表现路径的资产修订共同编码得到的 SHA-256 大写十六进制字符串。</returns>
    /// <remarks>路径按 Ordinal 去重与排序，资产修订按输入字典的键比较器查询；不包含来源信息、已解码像素或目录世代。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/>、<paramref name="semanticRevision"/> 或 <paramref name="assetRevisions"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="semanticRevision"/> 为空字符串。</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="assetRevisions"/> 缺少定义所引用的任一表现路径。</exception>
    /// <example>把图片绑定从 iron.png 改为字节相同的 copy.png 仍触发发布变化；见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
    public static string ComputePublicationFingerprint(
        VoxelDefinition definition,
        string semanticRevision,
        IReadOnlyDictionary<string, string> assetRevisions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentNullException.ThrowIfNull(assetRevisions);
        return Sha256Hex(EncodePublication(definition, semanticRevision, assetRevisions));
    }

    /// <summary>按固定字段顺序编码体素玩法数据，并排序无序标签。</summary>
    /// <param name="definition">非 null 的已规范化体素定义。</param>
    /// <returns>不含缩进的 UTF-8 JSON 字节，供语义修订计算使用。</returns>
    /// <remarks>数值使用定义模块的规范格式；行为按定义中的声明顺序编码。</remarks>
    private static byte[] EncodeSemantic(VoxelDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("behaviors");
            foreach (var behavior in definition.Behaviors)
            {
                writer.WriteStartObject();
                switch (behavior)
                {
                    case PropulsionBehavior propulsion:
                        writer.WriteString("type", BehaviorTypes.Propulsion);
                        writer.WritePropertyName("traction");
                        writer.WriteRawValue(VoxelDefinition.FormatMass(propulsion.Traction));
                        writer.WritePropertyName("rated_speed");
                        writer.WriteRawValue(VoxelDefinition.FormatMass(propulsion.RatedSpeed));
                        break;
                    case ControlBehavior:
                        writer.WriteString("type", BehaviorTypes.Control);
                        break;
                    case CargoBehavior cargo:
                        writer.WriteString("type", BehaviorTypes.Cargo);
                        writer.WriteNumber("capacity", cargo.Capacity);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported behavior type {behavior.GetType().Name}.");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("collision");
            writer.WriteString("kind", VoxelDefinition.ToWire(definition.Collision.Kind));
            if (definition.Collision.Kind == VoxelCollisionKind.LocalBox)
            {
                writer.WritePropertyName("offset_x");
                writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Collision.OffsetX));
                writer.WritePropertyName("offset_y");
                writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Collision.OffsetY));
                writer.WritePropertyName("offset_z");
                writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Collision.OffsetZ));
                writer.WritePropertyName("size_x");
                writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Collision.SizeX));
                writer.WritePropertyName("size_y");
                writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Collision.SizeY));
                writer.WritePropertyName("size_z");
                writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Collision.SizeZ));
            }

            writer.WriteEndObject();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", definition.Id.Value);
            writer.WriteString("kind", VoxelKind);
            writer.WritePropertyName("mass");
            writer.WriteRawValue(VoxelDefinition.FormatMass(definition.Mass));
            writer.WriteNumber("max_durability", definition.MaxDurability);
            writer.WriteString("orientations", VoxelDefinition.ToWire(definition.AllowedOrientations));
            writer.WriteStartArray("tags");
            foreach (var tag in definition.Tags.Order(StringComparer.Ordinal))
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
            writer.WriteNumber("value", definition.Value);
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>编码显示文本、表现绑定、各绑定资产修订和语义修订组成的完整发布状态。</summary>
    /// <param name="definition">非 null 的已规范化体素定义。</param>
    /// <param name="semanticRevision">要嵌入发布状态的语义修订。</param>
    /// <param name="assetRevisions">包含全部表现路径的资产修订映射。</param>
    /// <returns>字段顺序固定、绑定路径按 Ordinal 去重并排序的 UTF-8 JSON 字节。</returns>
    /// <remarks>四种表现绑定各自的路径单独保留，因此相同资产改绑到另一路径也会改变编码。</remarks>
    private static byte[] EncodePublication(
        VoxelDefinition definition,
        string semanticRevision,
        IReadOnlyDictionary<string, string> assetRevisions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("bindings");
            foreach (var path in new[]
                     {
                         definition.Presentation.BottomPath,
                         definition.Presentation.IconPath,
                         definition.Presentation.SidePath,
                         definition.Presentation.TopPath
                     }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("hash", assetRevisions[path]);
                writer.WriteString("path", path);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("description", definition.Description);
            writer.WriteString("name", definition.Name);
            writer.WriteStartObject("presentation");
            writer.WriteString("icon", definition.Presentation.IconPath);
            writer.WriteString("kind", VoxelDefinition.ToWire(definition.Presentation.Kind));
            writer.WriteString("voxel_bottom", definition.Presentation.BottomPath);
            writer.WriteString("voxel_side", definition.Presentation.SidePath);
            writer.WriteString("voxel_top", definition.Presentation.TopPath);
            writer.WriteEndObject();
            writer.WriteString("semantic_revision", semanticRevision);
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>计算字节序列的 SHA-256，并返回大写十六进制文本。</summary>
    /// <param name="bytes">要计算哈希的原始字节序列；允许为空。</param>
    /// <returns>32 字节摘要对应的 64 个大写十六进制字符。</returns>
    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash);
    }
}

/// <summary>按体素身份和发布指纹比较完整目录，计算新增、移除和改变的标识集合。</summary>
/// <example>新增 core:stone、移除 core:coal、修改 core:ferrous-ore 图片的用例见 <c>ContentReloaderTests.Successful_reload_reports_added_removed_and_pixel_changes</c>。</example>
internal static class CatalogDiff
{
    /// <summary>以发布指纹比较前后快照，并把身份分类为新增、移除或改变。</summary>
    /// <param name="generation">结果变化集对应的目标目录世代；原样保存。</param>
    /// <param name="previous">上一目录快照；为 null 时，候选目录的全部标识归入新增集合。</param>
    /// <param name="next">按身份索引的完整候选目录，不能为 null，条目须非 null。</param>
    /// <returns>包含三个冻结标识集合的目录差异；集合可以为空，不保证枚举顺序。</returns>
    /// <remarks>共有身份的发布指纹按 Ordinal 精确比较；来源、对象引用和世代本身不参与差异判断。不修改输入目录，也不执行发布。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="next"/> 为 null，或比较共有身份时遇到 null 条目。</exception>
    /// <example>新增、移除和图片变化分别进入三组标识；见 <c>ContentReloaderTests.Successful_reload_reports_added_removed_and_pixel_changes</c>。</example>
    public static VoxelCatalogChange Compute(
        long generation,
        VoxelCatalogSnapshot? previous,
        IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> next)
    {
        var added = new HashSet<VoxelDefinitionId>();
        var removed = new HashSet<VoxelDefinitionId>();
        var changed = new HashSet<VoxelDefinitionId>();
        var previousEntries = previous?.Entries;

        if (previousEntries is null)
        {
            added.UnionWith(next.Keys);
            return new VoxelCatalogChange(generation, added, removed, changed);
        }

        foreach (var (id, entry) in next)
        {
            if (!previousEntries.TryGetValue(id, out var prior))
                added.Add(id);
            else if (!string.Equals(prior.PublicationFingerprint, entry.PublicationFingerprint, StringComparison.Ordinal))
                changed.Add(id);
        }

        foreach (var id in previousEntries.Keys)
        {
            if (!next.ContainsKey(id))
                removed.Add(id);
        }

        return new VoxelCatalogChange(generation, added, removed, changed);
    }

    /// <summary>判断变化集中是否至少有一个新增、移除或改变的体素定义。</summary>
    /// <param name="change">要检查的目录差异，不能为 null。</param>
    /// <returns>任一集合非空时为 true；三组均为空时为 false，不比较目录世代。</returns>
    /// <exception cref="NullReferenceException"><paramref name="change"/> 为 null。</exception>
    /// <example>同一份内容再次重载时没有目录变化，也不发出通知；见 <c>ContentReloaderTests.Unchanged_reload_keeps_version_and_does_not_notify</c>。</example>
    public static bool HasChanges(VoxelCatalogChange change) =>
        change.Added.Count > 0 || change.Removed.Count > 0 || change.Changed.Count > 0;
}

/// <summary>提供重载结果共用的空体素标识集合，并将诊断列表转换为只读集合。</summary>
/// <remarks>已有 ReadOnlyCollection 诊断集合会直接复用，其他诊断列表复制后包装；复用集合的底层列表仍须由原持有方保持稳定。</remarks>
/// <example>重载结果保留无效 Mod 的警告诊断；见 <c>ModLoadingTests.Invalid_mod_is_isolated_while_unrelated_valid_packages_commit</c>。</example>
internal static class ImmutableSets
{
    /// <summary>获取可复用的冻结空体素标识集合，用于无变化或被拒绝的重载结果。</summary>
    public static IReadOnlySet<VoxelDefinitionId> Empty { get; } = FrozenSet<VoxelDefinitionId>.Empty;

    /// <summary>复用已有只读诊断集合，或按当前顺序复制列表并包装为只读集合。</summary>
    /// <param name="diagnostics">要保存的诊断列表；允许为空，不过滤或校验条目。</param>
    /// <returns>只读诊断列表；传入 ReadOnlyCollection 时保留原集合，否则保存数组副本，诊断对象均直接保留。</returns>
    /// <remarks>复用集合时，其底层列表仍须由原持有方保持稳定；其余输入列表的后续修改不会反映到副本。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> 为 null。</exception>
    public static IReadOnlyList<ContentDiagnostic> Freeze(IReadOnlyList<ContentDiagnostic> diagnostics) =>
        diagnostics as ReadOnlyCollection<ContentDiagnostic> ?? new ReadOnlyCollection<ContentDiagnostic>(diagnostics.ToArray());
}

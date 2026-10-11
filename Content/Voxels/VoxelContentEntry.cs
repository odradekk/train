// 属于内容层的体素定义模块。
// 保存已校验体素的来源和表现资产，规范编码语义修订及发布指纹。
using System.Collections.Frozen;
using System.Text.Json;
using Train.Content.Behaviors;

namespace Train.Content.Voxels;

/// <summary>表示内容目录中一个已校验的体素定义，以及加载该定义时确定的来源和修订信息。</summary>
/// <remarks>
/// 定义校验、资产校验和修订计算由内容加载器完成；构造函数只检查必填引用与修订字符串非空。
/// 资产和图片的索引被冻结，定义、来源与拥有像素副本的不可变图片对象直接保留。
/// 条目只能由内容模块构造，调用方不能注入未经校验的修订或资产。
/// </remarks>
/// <example>语义、资产和发布变化的区分见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
public sealed class VoxelContentEntry
{
    /// <summary>获取已规范化且不可变的体素定义。</summary>
    public VoxelDefinition Definition { get; }
    /// <summary>获取定义所属的内容包和包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取语义字段规范编码的 SHA-256 大写十六进制修订。</summary>
    /// <remarks>由加载器按 <see cref="VoxelRevisions.ComputeSemanticRevision"/> 生成；构造函数不检查哈希格式或与定义的一致性。</remarks>
    public string SemanticRevision { get; }
    /// <summary>获取按规范化内容路径索引的原始资产字节 SHA-256 大写十六进制修订。</summary>
    /// <remarks>加载器按原始图片文件字节计算修订，解码后像素相同的文件也可能具有不同修订；条目本身不校验路径或修订格式。</remarks>
    public IReadOnlyDictionary<string, string> AssetRevisions { get; }
    /// <summary>获取按规范化内容路径索引的已校验、已解码表现图片。</summary>
    /// <remarks>未提供图片映射时为空；构造函数不检查图片尺寸、像素内容或与表现路径的对应关系。</remarks>
    public IReadOnlyDictionary<string, DecodedImage> PresentationImages { get; }
    /// <summary>获取语义修订、显示文本、碰撞、表现绑定和资产修订共同形成的 SHA-256 发布指纹。</summary>
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
    internal VoxelContentEntry(
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

/// <summary>把体素玩法数据和表现绑定分别编码为规范 UTF-8 JSON，并计算语义修订、资产修订和发布指纹。</summary>
/// <remarks>编码使用固定字段顺序；标签和 Behavior 类型标记按 Ordinal 排序。显示文本、碰撞、表现配置及资产字节不参与语义修订。</remarks>
/// <example>标签顺序和 JSON 排版不改变修订，显示字段与图片变化影响发布；见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
internal static class VoxelRevisions
{
    /// <summary>体素语义编码版本，用于区分规范编码规则；与定义文件的 schema 版本独立管理。</summary>
    /// <remarks>改变编码规则时需同步调整。修订用途见 docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md。</remarks>
    public const int SemanticEncodingVersion = 4;
    /// <summary>语义编码中 kind 字段的固定值 voxel，用于区分不同种类的内容。</summary>
    public const string VoxelKind = "voxel";

    /// <summary>计算体素定义的语义修订。</summary>
    /// <param name="definition">待编码的已规范化体素定义；不重新执行内容校验。</param>
    /// <returns>内容种类、编码版本、身份、Value、质量、耐久、标签、Behavior 参数和允许朝向的 SHA-256 修订。</returns>
    /// <remarks>标签和 Behavior 类型标记稳定排序；碰撞不参与本编码。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> 为 <see langword="null"/>。</exception>
    /// <example>将 value 从 100 改为 250 会改变语义修订，调整标签顺序不会；见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
    public static string ComputeSemanticRevision(VoxelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ContentHash.FromBytes(EncodeSemantic(definition));
    }

    /// <summary>计算资产原始字节的修订。</summary>
    /// <param name="rawBytes">原始资产文件字节；允许为空，不进行图片解码或格式校验。</param>
    /// <returns>原始字节的 SHA-256 大写十六进制字符串。</returns>
    /// <example>解码像素相同但原始 PNG 字节不同的资产具有不同修订；见 <c>ContentReloaderTests.Same_pixels_different_raw_png_bytes_change_only_the_asset_revision</c>。</example>
    public static string ComputeAssetRevision(ReadOnlySpan<byte> rawBytes) =>
        ContentHash.FromBytes(rawBytes);

    /// <summary>计算体素定义一次完整发布的指纹。</summary>
    /// <param name="definition">待编码的已规范化体素定义。</param>
    /// <param name="semanticRevision">该定义已经计算出的非空语义修订；原文编码，不重新计算或验证哈希格式。</param>
    /// <param name="assetRevisions">表现路径对应的资产修订映射，须包含四种表现绑定引用的所有路径；多余的键不参与编码。</param>
    /// <returns>语义修订、显示文本、碰撞、表现绑定和去重排序的资产修订共同编码得到的 SHA-256 指纹。</returns>
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
        return ContentHash.FromBytes(EncodePublication(definition, semanticRevision, assetRevisions));
    }

    /// <summary>按固定字段顺序编码体素玩法数据，并排序无序标签。</summary>
    /// <param name="definition">非 null 的已规范化体素定义。</param>
    /// <returns>不含缩进的 UTF-8 JSON 字节，供语义修订计算使用。</returns>
    /// <remarks>数值使用定义模块的规范格式；行为沿用定义中按类型标记 Ordinal 排好的顺序编码。</remarks>
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
                writer.WriteString("type", BehaviorRegistry.TypeOf(behavior));
                switch (behavior)
                {
                    case PropulsionBehavior propulsion:
                        writer.WritePropertyName("traction");
                        writer.WriteRawValue(VoxelDefinition.FormatMass(propulsion.Traction));
                        writer.WritePropertyName("rated_speed");
                        writer.WriteRawValue(VoxelDefinition.FormatMass(propulsion.RatedSpeed));
                        break;
                    case ControlBehavior:
                        break;
                    case CargoBehavior cargo:
                        writer.WriteNumber("capacity", cargo.Capacity);
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
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

    /// <summary>编码碰撞、显示文本、表现绑定、资产修订及语义修订组成的完整发布状态。</summary>
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
}

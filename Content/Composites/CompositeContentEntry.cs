// 属于内容层的蓝图定义模块。
// 保存已校验蓝图的来源和设计属性，规范编码语义修订及发布指纹。
using Train.Content.Voxels;

namespace Train.Content.Composites;

/// <summary>表示一个蓝图的不可变定义、来源、修订和按当前体素定义计算的设计属性。</summary>
/// <example>部件数值变化后更新属性而保持蓝图语义修订，见 <c>CompositeContentTests</c>。</example>
public sealed class CompositeContentEntry
{
    /// <summary>获取已规范化的蓝图定义。</summary>
    public CompositeDefinition Definition { get; }
    /// <summary>获取所属内容包与包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取文件显示名；未声明时为 null。</summary>
    public string? FileDisplayName { get; }
    /// <summary>获取身份、尺寸档位和布局规范编码的 SHA-256 修订；不包含引用目标的修订。</summary>
    public string SemanticRevision { get; }
    /// <summary>获取定义、显示文本和设计属性共同形成的 SHA-256 发布指纹。</summary>
    public string PublicationFingerprint { get; }
    /// <summary>获取本快照使用的设计属性和 BOM；不包含实例状态。</summary>
    public DesignAttributes Attributes { get; }

    /// <summary>保存加载器校验和计算完成的蓝图发布条目，不重新计算修订或属性。</summary>
    /// <param name="definition">已规范化的蓝图定义。</param>
    /// <param name="origin">所属包与包内源文件。</param>
    /// <param name="fileDisplayName">文件显示名；未声明时为 null。</param>
    /// <param name="semanticRevision">非空的语义修订。</param>
    /// <param name="publicationFingerprint">非空的发布指纹。</param>
    /// <param name="attributes">按候选体素目录计算的不可变设计属性。</param>
    /// <exception cref="ArgumentNullException">必填引用为 null。</exception>
    /// <exception cref="ArgumentException">修订或指纹为空字符串。</exception>
    internal CompositeContentEntry(
        CompositeDefinition definition, ContentOrigin origin, string? fileDisplayName,
        string semanticRevision, string publicationFingerprint, DesignAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Definition = definition;
        Origin = origin;
        FileDisplayName = fileDisplayName;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
        Attributes = attributes;
    }
}

/// <summary>规范编码蓝图定义与发布属性；引用目标的修订不进入蓝图自身修订。</summary>
internal static class CompositeRevisions
{
    /// <summary>蓝图语义编码版本，用于区分规范编码规则；与定义文件的 schema 版本独立管理。</summary>
    /// <remarks>编码与修订约定见 Content/README.md。</remarks>
    public const int SemanticEncodingVersion = 2;

    /// <summary>计算身份、尺寸档位及按坐标排序的布局的 SHA-256 修订。</summary>
    public static string ComputeSemanticRevision(CompositeDefinition definition)
    {
        return ContentHash.FromCanonicalJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", definition.Id.Value);
            writer.WriteString("kind", "composite");
            writer.WriteString("size_class", VehicleSize.ToWire(definition.SizeClass));
            writer.WriteStartArray("voxels");
            foreach (var voxel in definition.Voxels)
            {
                writer.WriteStartObject();
                writer.WriteString("definition", voxel.DefinitionId.Value);
                writer.WriteString("orientation", voxel.Orientation.ToString());
                writer.WriteNumber("x", voxel.Cell.X);
                writer.WriteNumber("y", voxel.Cell.Y);
                writer.WriteNumber("z", voxel.Cell.Z);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>计算显示字段、设计属性和自身语义修订的发布指纹。</summary>
    public static string ComputePublicationFingerprint(CompositeDefinition definition, string? fileDisplayName,
        string semanticRevision, DesignAttributes attributes)
    {
        return ContentHash.FromCanonicalJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("attributes");
            writer.WriteStartObject("bom");
            foreach (var (id, count) in attributes.Bom.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
                writer.WriteNumber(id.Value, count);
            writer.WriteEndObject();
            writer.WriteNumber("bom_value", attributes.BomValue);
            writer.WriteNumber("cargo_capacity", attributes.CargoCapacity);
            writer.WriteBoolean("has_control", attributes.HasControl);
            writer.WriteNumber("mass", attributes.Mass);
            if (attributes.RatedSpeed is { } speed)
                writer.WriteNumber("rated_speed", speed);
            else
                writer.WriteNull("rated_speed");
            writer.WriteNumber("structural_durability_max", attributes.StructuralDurabilityMax);
            writer.WriteNumber("traction", attributes.Traction);
            writer.WriteEndObject();
            writer.WriteString("description", definition.Description);
            writer.WriteString("display_name", fileDisplayName ?? "");
            writer.WriteString("name", definition.Name);
            writer.WriteString("semantic_revision", semanticRevision);
            writer.WriteEndObject();
        });
    }
}

// 属于内容层的聚居地类型模块。
// 保存候选校验完成的类型定义及其来源和修订。
namespace Train.Core.Content.Settlements;

/// <summary>表示一个已校验聚居地类型的不可变发布条目。</summary>
/// <remarks>
/// 数据种类：结构。语义修订包含身份、供需、地貌偏好、权重、人口范围和初始站台数，不包含显示名或引用目标修订。
/// 发布指纹另包含显示名，不包含来源路径。两个修订均为 SHA-256 大写十六进制值。
/// </remarks>
/// <example>DepotFile 与 QuietTypeFile 发布供需非空及供需为空两种类型；见 MarketSettlementExamples。</example>
public sealed class SettlementTypeContentEntry
{
    /// <summary>获取已规范化并完成货物引用校验的聚居地类型。</summary>
    public SettlementTypeDefinition Definition { get; }
    /// <summary>获取所属内容包和包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取类型自身的语义修订。</summary>
    public string SemanticRevision { get; }
    /// <summary>获取定义和显示信息共同形成的发布指纹。</summary>
    public string PublicationFingerprint { get; }

    /// <summary>保存加载器已校验的定义和计算好的修订，不重新读取依赖。</summary>
    internal SettlementTypeContentEntry(
        SettlementTypeDefinition definition, ContentOrigin origin,
        string semanticRevision, string publicationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Definition = definition;
        Origin = origin;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
    }
}

/// <summary>规范编码类型参数和当前显示名；引用目标内容不进入类型自身语义。</summary>
internal static class SettlementTypeRevisions
{
    /// <summary>规范编码版本，与定义文件 schema 分开。</summary>
    private const int SemanticEncodingVersion = 1;

    /// <summary>按固定 Ordinal 字段顺序、货物身份和地貌排序计算非显示参数的语义修订。</summary>
    public static string ComputeSemanticRevision(SettlementTypeDefinition definition) => ContentHash.FromCanonicalJson(writer =>
    {
        writer.WriteStartObject();
        WriteQuantities(writer, "demand", definition.Demand);
        writer.WriteNumber("encoding_version", SemanticEncodingVersion);
        writer.WriteString("id", definition.Id.Value);
        writer.WriteNumber("initial_platform_count", definition.InitialPlatformCount);
        writer.WriteString("kind", "settlement_type");
        writer.WriteNumber("population_max", definition.Population.Max);
        writer.WriteNumber("population_min", definition.Population.Min);
        writer.WriteStartArray("preferred_terrain");
        foreach (var terrain in definition.PreferredTerrain.Order(StringComparer.Ordinal))
            writer.WriteStringValue(terrain);
        writer.WriteEndArray();
        WriteQuantities(writer, "supply", definition.Supply);
        writer.WriteNumber("weight", definition.Weight);
        writer.WriteEndObject();
    });

    /// <summary>将显示名和自身语义修订编码为发布指纹。</summary>
    public static string ComputePublicationFingerprint(SettlementTypeDefinition definition, string semanticRevision) => ContentHash.FromCanonicalJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("name", definition.Name);
        writer.WriteString("semantic_revision", semanticRevision);
        writer.WriteEndObject();
    });

    /// <summary>按货物身份排序编码一边供需，每项使用固定字段顺序。</summary>
    private static void WriteQuantities(System.Text.Json.Utf8JsonWriter writer, string name,
        IReadOnlyDictionary<Voxels.VoxelDefinitionId, SettlementMarketQuantity> quantities)
    {
        writer.WriteStartArray(name);
        foreach (var (id, quantity) in quantities.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteNumber("capacity", quantity.Capacity);
            writer.WriteString("definition", id.Value);
            writer.WriteNumber("restore_per_tick", quantity.RestorePerTick);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

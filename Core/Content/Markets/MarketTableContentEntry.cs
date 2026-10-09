// 属于内容层的市场表模块。
// 保存候选校验完成的价格定义及其来源和修订。
namespace Train.Core.Content.Markets;

/// <summary>表示一个已校验市场表的不可变发布条目。</summary>
/// <remarks>
/// 数据种类：结构。语义修订包含身份和按货物排序的价格参数；发布指纹不包含来源路径或引用体素的 Value。
/// 体素 Value 的变化由完整快照发布，读取方从同一快照取得它，不在市场表条目中保存副本。
/// 所有有效字段都属于语义，发布指纹直接复用语义修订，均为 SHA-256 大写十六进制值。
/// </remarks>
/// <example>PricesFile 发布后可通过 PricesId 查到两条规则；见 MarketSettlementExamples。</example>
public sealed class MarketTableContentEntry
{
    /// <summary>获取已规范化并完成货物引用校验的市场表。</summary>
    public MarketTableDefinition Definition { get; }
    /// <summary>获取所属内容包和包内源文件。</summary>
    public ContentOrigin Origin { get; }
    /// <summary>获取市场表自身的语义修订，不包含引用目标修订。</summary>
    public string SemanticRevision { get; }
    /// <summary>获取本条目的有效发布指纹。</summary>
    public string PublicationFingerprint => SemanticRevision;

    /// <summary>保存加载器已校验的定义和计算好的修订，不重新读取依赖。</summary>
    internal MarketTableContentEntry(
        MarketTableDefinition definition, ContentOrigin origin, string semanticRevision)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        Definition = definition;
        Origin = origin;
        SemanticRevision = semanticRevision;
    }
}

/// <summary>按 Ordinal 字段与货物身份顺序编码市场表自身语义。</summary>
internal static class MarketTableRevisions
{
    /// <summary>规范编码版本，与定义文件 schema 分开。</summary>
    private const int SemanticEncodingVersion = 1;

    /// <summary>计算身份和价格规则的语义修订；无显示字段，亦作为发布指纹。</summary>
    public static string ComputeSemanticRevision(MarketTableDefinition definition) => ContentHash.FromCanonicalJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("encoding_version", SemanticEncodingVersion);
        writer.WriteStartArray("goods");
        foreach (var (id, rule) in definition.Goods.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("definition", id.Value);
            writer.WriteNumber("max_price", rule.MaxPrice);
            writer.WriteNumber("min_price", rule.MinPrice);
            writer.WriteNumber("sensitivity", rule.Sensitivity);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("id", definition.Id.Value);
        writer.WriteString("kind", "market_table");
        writer.WriteEndObject();
    });
}

namespace Train.Content.Markets;

public sealed class MarketTableContentEntry
{
    public MarketTableDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string SemanticRevision { get; }
    public string PublicationFingerprint => SemanticRevision;

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

internal static class MarketTableRevisions
{
    private const int SemanticEncodingVersion = 1;

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

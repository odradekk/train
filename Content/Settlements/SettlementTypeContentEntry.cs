namespace Train.Content.Settlements;

public sealed class SettlementTypeContentEntry
{
    public SettlementTypeDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string SemanticRevision { get; }
    public string PublicationFingerprint { get; }

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

internal static class SettlementTypeRevisions
{
    private const int SemanticEncodingVersion = 1;

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

    public static string ComputePublicationFingerprint(SettlementTypeDefinition definition, string semanticRevision) => ContentHash.FromCanonicalJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("name", definition.Name);
        writer.WriteString("semantic_revision", semanticRevision);
        writer.WriteEndObject();
    });

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

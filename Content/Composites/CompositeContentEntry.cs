using Train.Content.Voxels;

namespace Train.Content.Composites;

public sealed class CompositeContentEntry
{
    public CompositeDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string? FileDisplayName { get; }
    public string SemanticRevision { get; }
    public string PublicationFingerprint { get; }
    public DesignAttributes Attributes { get; }

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

internal static class CompositeRevisions
{
    public const int SemanticEncodingVersion = 2;

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

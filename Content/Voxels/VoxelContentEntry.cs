using System.Collections.Frozen;
using System.Text.Json;
using Train.Content.Behaviors;

namespace Train.Content.Voxels;

public sealed class VoxelContentEntry
{
    public VoxelDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string SemanticRevision { get; }
    public IReadOnlyDictionary<string, string> AssetRevisions { get; }
    public IReadOnlyDictionary<string, DecodedImage> PresentationImages { get; }
    public string PublicationFingerprint { get; }

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

internal static class VoxelRevisions
{
    public const int SemanticEncodingVersion = 4;
    public const string VoxelKind = "voxel";

    public static string ComputeSemanticRevision(VoxelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ContentHash.FromBytes(EncodeSemantic(definition));
    }

    public static string ComputeAssetRevision(ReadOnlySpan<byte> rawBytes) =>
        ContentHash.FromBytes(rawBytes);

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

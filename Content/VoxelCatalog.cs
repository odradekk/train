using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Train.Domain.Behaviors;
using Train.Domain.Settlements;
using Train.Domain.Voxels;

namespace Train.Content;

public sealed class VoxelCatalogEntry
{
    public VoxelDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string SemanticRevision { get; }
    public IReadOnlyDictionary<string, string> AssetRevisions { get; }
    public IReadOnlyDictionary<string, DecodedImage> PresentationImages { get; }
    public string PublicationFingerprint { get; }

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

public sealed class VoxelCatalogSnapshot
{
    public long Generation { get; }
    public IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> Entries { get; }

    public VoxelCatalogSnapshot(long generation, IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Generation = generation;
        Entries = entries as FrozenDictionary<VoxelDefinitionId, VoxelCatalogEntry> ?? entries.ToFrozenDictionary();
    }

    public bool TryGet(VoxelDefinitionId id, out VoxelCatalogEntry entry) => Entries.TryGetValue(id, out entry!);
}

public sealed class VoxelCatalogChange
{
    public long Generation { get; }
    public IReadOnlySet<VoxelDefinitionId> Added { get; }
    public IReadOnlySet<VoxelDefinitionId> Removed { get; }
    public IReadOnlySet<VoxelDefinitionId> Changed { get; }

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

    private static IReadOnlySet<VoxelDefinitionId> Freeze(IReadOnlySet<VoxelDefinitionId> set) =>
        set as FrozenSet<VoxelDefinitionId> ?? set.ToFrozenSet();
}

public sealed class VoxelCatalog
{
    private VoxelCatalogSnapshot? _current;

    public VoxelCatalogSnapshot? Current => Volatile.Read(ref _current);

    public event Action<VoxelCatalogChange>? Changed;

    internal void Publish(VoxelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

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

internal static class ContentRevisions
{
    public const int SemanticEncodingVersion = 1;
    public const string VoxelKind = "voxel";

    public static string ComputeSemanticRevision(VoxelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Sha256Hex(EncodeSemantic(definition));
    }

    public static string ComputeAssetRevision(ReadOnlySpan<byte> rawBytes) =>
        Sha256Hex(rawBytes);

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

    private static byte[] EncodeSemantic(VoxelDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("behaviors");
            foreach (var behavior in definition.Behaviors)
                writer.WriteStringValue(behavior);
            writer.WriteEndArray();
            if (definition.Behaviors.Contains(BehaviorTypes.Propulsion))
            {
                var propulsion = definition.Propulsion ?? PropulsionParameters.Default;
                writer.WritePropertyName("propulsion_target_speed");
                writer.WriteRawValue(VoxelDefinition.FormatMass(propulsion.TargetSpeed));
                writer.WritePropertyName("propulsion_acceleration");
                writer.WriteRawValue(VoxelDefinition.FormatMass(propulsion.Acceleration));
            }
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

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash);
    }
}

internal static class CatalogDiff
{
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

    public static bool HasChanges(VoxelCatalogChange change) =>
        change.Added.Count > 0 || change.Removed.Count > 0 || change.Changed.Count > 0;
}

internal static class ImmutableSets
{
    public static IReadOnlySet<VoxelDefinitionId> Empty { get; } = FrozenSet<VoxelDefinitionId>.Empty;
    public static IReadOnlySet<SettlementId> EmptySettlements { get; } = FrozenSet<SettlementId>.Empty;

    public static IReadOnlyList<ContentDiagnostic> Freeze(IReadOnlyList<ContentDiagnostic> diagnostics) =>
        diagnostics as ReadOnlyCollection<ContentDiagnostic> ?? new ReadOnlyCollection<ContentDiagnostic>(diagnostics.ToArray());
}

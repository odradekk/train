using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Train.Domain.Composites;
using Train.Domain.Settlements;
using Train.Domain.Voxels;

namespace Train.Content;

public sealed class CompositeArchetypeCatalogEntry
{
    public CompositeArchetype Archetype { get; }
    public ContentOrigin Origin { get; }
    public string SemanticRevision { get; }
    public string PublicationFingerprint { get; }

    public CompositeArchetypeCatalogEntry(
        CompositeArchetype archetype,
        ContentOrigin origin,
        string semanticRevision,
        string publicationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Archetype = archetype;
        Origin = origin;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
    }
}

public sealed class CompositeCatalogEntry
{
    public CompositeDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string? FileDisplayName { get; }
    public string SemanticRevision { get; }
    public string PublicationFingerprint { get; }

    public CompositeCatalogEntry(
        CompositeDefinition definition,
        ContentOrigin origin,
        string? fileDisplayName,
        string semanticRevision,
        string publicationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        ArgumentException.ThrowIfNullOrEmpty(publicationFingerprint);
        Definition = definition;
        Origin = origin;
        FileDisplayName = fileDisplayName;
        SemanticRevision = semanticRevision;
        PublicationFingerprint = publicationFingerprint;
    }
}

public sealed class CompositeCatalogSnapshot
{
    public long Generation { get; }
    public IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> Archetypes { get; }
    public IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> Composites { get; }

    public CompositeCatalogSnapshot(
        long generation,
        IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        ArgumentNullException.ThrowIfNull(archetypes);
        ArgumentNullException.ThrowIfNull(composites);
        Generation = generation;
        Archetypes = archetypes as FrozenDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> ??
                     archetypes.ToFrozenDictionary();
        Composites = composites as FrozenDictionary<CompositeDefinitionId, CompositeCatalogEntry> ??
                     composites.ToFrozenDictionary();
    }

    public static CompositeCatalogSnapshot Empty { get; } = new(
        0,
        FrozenDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry>.Empty,
        FrozenDictionary<CompositeDefinitionId, CompositeCatalogEntry>.Empty);

    public bool TryGet(CompositeDefinitionId id, out CompositeCatalogEntry entry) =>
        Composites.TryGetValue(id, out entry!);

    public bool TryGet(CompositeArchetypeId id, out CompositeArchetypeCatalogEntry entry) =>
        Archetypes.TryGetValue(id, out entry!);
}

public sealed class CompositeCatalogChange
{
    public long Generation { get; }
    public IReadOnlySet<CompositeDefinitionId> Added { get; }
    public IReadOnlySet<CompositeDefinitionId> Removed { get; }
    public IReadOnlySet<CompositeDefinitionId> Changed { get; }
    public IReadOnlySet<CompositeArchetypeId> AddedArchetypes { get; }
    public IReadOnlySet<CompositeArchetypeId> RemovedArchetypes { get; }
    public IReadOnlySet<CompositeArchetypeId> ChangedArchetypes { get; }

    public CompositeCatalogChange(
        long generation,
        IReadOnlySet<CompositeDefinitionId> added,
        IReadOnlySet<CompositeDefinitionId> removed,
        IReadOnlySet<CompositeDefinitionId> changed,
        IReadOnlySet<CompositeArchetypeId> addedArchetypes,
        IReadOnlySet<CompositeArchetypeId> removedArchetypes,
        IReadOnlySet<CompositeArchetypeId> changedArchetypes)
    {
        Generation = generation;
        Added = added;
        Removed = removed;
        Changed = changed;
        AddedArchetypes = addedArchetypes;
        RemovedArchetypes = removedArchetypes;
        ChangedArchetypes = changedArchetypes;
    }

    public bool HasChanges =>
        Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0 ||
        AddedArchetypes.Count > 0 || RemovedArchetypes.Count > 0 || ChangedArchetypes.Count > 0;
}

public sealed class CompositeCatalog
{
    private CompositeCatalogSnapshot _current = CompositeCatalogSnapshot.Empty;

    public CompositeCatalogSnapshot Current => Volatile.Read(ref _current);

    public event Action<CompositeCatalogChange>? Changed;

    internal void Publish(CompositeCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    internal void NotifyChanged(CompositeCatalogChange change, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(onFault);
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (Action<CompositeCatalogChange> handler in handlers.GetInvocationList())
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

internal static class CompositeRevisions
{
    public const int SemanticEncodingVersion = 1;
    public const string CompositeKind = "composite";
    public const string ArchetypeKind = "composite_archetype";

    public static string ComputeSemanticRevision(CompositeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Sha256Hex(EncodeCompositeSemantic(definition));
    }

    public static string ComputeSemanticRevision(CompositeArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        return Sha256Hex(EncodeArchetypeSemantic(archetype));
    }

    public static string ComputePublicationFingerprint(
        CompositeDefinition definition,
        string? fileDisplayName,
        string semanticRevision)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        return Sha256Hex(EncodeCompositePublication(definition, fileDisplayName, semanticRevision));
    }

    public static string ComputePublicationFingerprint(CompositeArchetype archetype, string semanticRevision)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentException.ThrowIfNullOrEmpty(semanticRevision);
        return semanticRevision;
    }

    private static byte[] EncodeCompositeSemantic(CompositeDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("archetype", definition.ArchetypeId.Value);
            writer.WriteStartArray("contacts");
            foreach (var contact in definition.Contacts
                         .OrderBy(item => item.X)
                         .ThenBy(item => item.Y)
                         .ThenBy(item => item.Z)
                         .ThenBy(item => item.BoundCell))
            {
                writer.WriteStartObject();
                writer.WriteNumber("cell_x", contact.BoundCell.X);
                writer.WriteNumber("cell_y", contact.BoundCell.Y);
                writer.WriteNumber("cell_z", contact.BoundCell.Z);
                writer.WritePropertyName("x");
                writer.WriteRawValue(VoxelDefinition.FormatMass(contact.X));
                writer.WritePropertyName("y");
                writer.WriteRawValue(VoxelDefinition.FormatMass(contact.Y));
                writer.WritePropertyName("z");
                writer.WriteRawValue(VoxelDefinition.FormatMass(contact.Z));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", definition.Id.Value);
            writer.WriteString("kind", CompositeKind);
            writer.WriteString("size_class", VehicleSize.ToWire(definition.SizeClass));
            writer.WriteStartArray("voxels");
            foreach (var voxel in definition.Voxels.OrderBy(item => item.Cell))
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
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static byte[] EncodeArchetypeSemantic(CompositeArchetype archetype)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("construction");
            writer.WriteNumber("contact_anchor_count", archetype.Construction.ContactAnchorCount);
            writer.WriteBoolean("contacts_must_be_distinct", archetype.Construction.ContactsMustBeDistinct);
            writer.WriteBoolean("contacts_must_bind_existing_parts", archetype.Construction.ContactsMustBindExistingParts);
            writer.WriteBoolean("require_declared_size", archetype.Construction.RequireDeclaredSize);
            writer.WriteBoolean("require_nonempty_six_connected", archetype.Construction.RequireNonEmptySixConnected);
            writer.WriteEndObject();
            writer.WriteNumber("encoding_version", SemanticEncodingVersion);
            writer.WriteString("id", archetype.Id.Value);
            writer.WriteStartObject("identity_retention");
            writer.WriteString("require_core_tag", archetype.IdentityRetention.RequiredCoreTag);
            writer.WriteEndObject();
            writer.WriteString("kind", ArchetypeKind);
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static byte[] EncodeCompositePublication(
        CompositeDefinition definition,
        string? fileDisplayName,
        string semanticRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("description", definition.Description);
            writer.WriteString("display_name", fileDisplayName ?? "");
            writer.WriteString("name", definition.Name);
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

internal static class CompositeCatalogDiff
{
    public static CompositeCatalogChange Compute(
        long generation,
        CompositeCatalogSnapshot? previous,
        IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> nextArchetypes,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> nextComposites)
    {
        var added = new HashSet<CompositeDefinitionId>();
        var removed = new HashSet<CompositeDefinitionId>();
        var changed = new HashSet<CompositeDefinitionId>();
        var addedArchetypes = new HashSet<CompositeArchetypeId>();
        var removedArchetypes = new HashSet<CompositeArchetypeId>();
        var changedArchetypes = new HashSet<CompositeArchetypeId>();
        var previousComposites = previous?.Composites;
        var previousArchetypes = previous?.Archetypes;

        if (previousComposites is null)
            added.UnionWith(nextComposites.Keys);
        else
        {
            foreach (var (id, entry) in nextComposites)
            {
                if (!previousComposites.TryGetValue(id, out var prior))
                    added.Add(id);
                else if (!string.Equals(prior.PublicationFingerprint, entry.PublicationFingerprint, StringComparison.Ordinal))
                    changed.Add(id);
            }

            foreach (var id in previousComposites.Keys)
            {
                if (!nextComposites.ContainsKey(id))
                    removed.Add(id);
            }
        }

        if (previousArchetypes is null)
            addedArchetypes.UnionWith(nextArchetypes.Keys);
        else
        {
            foreach (var (id, entry) in nextArchetypes)
            {
                if (!previousArchetypes.TryGetValue(id, out var prior))
                    addedArchetypes.Add(id);
                else if (!string.Equals(prior.PublicationFingerprint, entry.PublicationFingerprint, StringComparison.Ordinal))
                    changedArchetypes.Add(id);
            }

            foreach (var id in previousArchetypes.Keys)
            {
                if (!nextArchetypes.ContainsKey(id))
                    removedArchetypes.Add(id);
            }
        }

        return new CompositeCatalogChange(
            generation,
            added,
            removed,
            changed,
            addedArchetypes,
            removedArchetypes,
            changedArchetypes);
    }
}

internal static class CompositeReferenceValidator
{
    public static ContentPreCommitDecision Filter(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<SettlementId, SettlementCandidate> settlements,
        IDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(settlements);
        ArgumentNullException.ThrowIfNull(archetypes);
        ArgumentNullException.ThrowIfNull(composites);
        var diagnostics = new List<ContentDiagnostic>();
        while (true)
        {
            var failedMods = new HashSet<string>(StringComparer.Ordinal);
            var failedPlayers = new HashSet<string>(StringComparer.Ordinal);
            var rejectBuiltIn = false;
            foreach (var composite in composites.Values.ToArray())
            {
                if (CompositeReferencesAreValid(voxels, archetypes, composite, diagnostics))
                    continue;
                if (composite.Origin.Namespace == VoxelDefinitionId.BuiltInNamespace)
                    rejectBuiltIn = true;
                else if (Guid.TryParseExact(composite.Origin.Namespace, "D", out _))
                    failedPlayers.Add(composite.Origin.Namespace);
                else
                    failedMods.Add(composite.Origin.Namespace);
            }

            if (rejectBuiltIn)
                return ContentPreCommitDecision.Reject(diagnostics);
            if (failedMods.Count == 0 && failedPlayers.Count == 0)
                return diagnostics.Count == 0
                    ? ContentPreCommitDecision.Allow()
                    : ContentPreCommitDecision.Allow(diagnostics);

            foreach (var ns in failedMods.Concat(failedPlayers))
                DropNamespace(ns, voxels, settlements, archetypes, composites);
        }
    }

    private static bool CompositeReferencesAreValid(
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        CompositeCatalogEntry composite,
        List<ContentDiagnostic> diagnostics)
    {
        var valid = true;
        var severity = composite.Origin.Namespace == VoxelDefinitionId.BuiltInNamespace
            ? ContentDiagnosticSeverity.Error
            : ContentDiagnosticSeverity.Warning;
        if (!archetypes.ContainsKey(composite.Definition.ArchetypeId))
        {
            valid = false;
            diagnostics.Add(new ContentDiagnostic(
                severity,
                ContentDiagnosticCodes.UnresolvedArchetypeReference,
                "A composite references an archetype missing from the candidate catalog.",
                composite.Origin.SourceFile,
                composite.Definition.ArchetypeId.Value));
        }

        foreach (var voxel in composite.Definition.Voxels)
        {
            if (voxels.ContainsKey(voxel.DefinitionId))
                continue;
            valid = false;
            diagnostics.Add(new ContentDiagnostic(
                severity,
                ContentDiagnosticCodes.UnresolvedVoxelReference,
                "A composite references a voxel missing from the candidate catalog.",
                composite.Origin.SourceFile,
                voxel.DefinitionId.Value));
        }

        return valid;
    }

    private static void DropNamespace(
        string ns,
        IDictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
        IDictionary<SettlementId, SettlementCandidate> settlements,
        IDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        foreach (var id in voxels.Keys.Where(id => id.Namespace == ns).ToArray())
            voxels.Remove(id);
        foreach (var id in settlements.Keys.Where(id => id.Value.StartsWith(ns + ":", StringComparison.Ordinal)).ToArray())
            settlements.Remove(id);
        foreach (var id in archetypes.Keys.Where(id => id.Namespace == ns).ToArray())
            archetypes.Remove(id);
        foreach (var id in composites.Keys.Where(id => id.Namespace == ns).ToArray())
            composites.Remove(id);
    }
}

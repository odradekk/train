using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Train.Domain.Goods;
using Train.Domain.Settlements;

namespace Train.Content;

public sealed class GoodCatalogEntry
{
    public GoodDefinition Definition { get; }
    public ContentOrigin Origin { get; }
    public string Fingerprint { get; }

    public GoodCatalogEntry(GoodDefinition definition, ContentOrigin origin, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        Definition = definition;
        Origin = origin;
        Fingerprint = fingerprint;
    }
}

public sealed class GoodCatalogSnapshot
{
    public long Version { get; }
    public IReadOnlyDictionary<GoodId, GoodCatalogEntry> Entries { get; }

    public GoodCatalogSnapshot(long version, IReadOnlyDictionary<GoodId, GoodCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Version = version;
        Entries = entries as FrozenDictionary<GoodId, GoodCatalogEntry> ?? entries.ToFrozenDictionary();
    }

    public bool TryGet(GoodId id, out GoodCatalogEntry entry) => Entries.TryGetValue(id, out entry!);
}

public sealed class GoodCatalogChange
{
    public long Version { get; }
    public IReadOnlySet<GoodId> Added { get; }
    public IReadOnlySet<GoodId> Removed { get; }
    public IReadOnlySet<GoodId> Changed { get; }

    public GoodCatalogChange(
        long version,
        IReadOnlySet<GoodId> added,
        IReadOnlySet<GoodId> removed,
        IReadOnlySet<GoodId> changed)
    {
        Version = version;
        Added = Freeze(added);
        Removed = Freeze(removed);
        Changed = Freeze(changed);
    }

    private static IReadOnlySet<GoodId> Freeze(IReadOnlySet<GoodId> set) =>
        set as FrozenSet<GoodId> ?? set.ToFrozenSet();
}

public sealed class GoodCatalog
{
    private GoodCatalogSnapshot? _current;

    public GoodCatalogSnapshot? Current => Volatile.Read(ref _current);

    public event Action<GoodCatalogChange>? Changed;

    internal bool TryPublish(GoodCatalogSnapshot snapshot, GoodCatalogChange change, bool notify)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(change);
        Volatile.Write(ref _current, snapshot);
        if (!notify)
            return true;
        Changed?.Invoke(change);
        return true;
    }
}

internal static class DefinitionFingerprint
{
    public static string Compute(GoodDefinition definition, IReadOnlyDictionary<string, DecodedImage> images)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, definition.Id.Value);
        Append(hash, GoodCategoryText.ToWire(definition.Category));
        Append(hash, definition.Name);
        Append(hash, definition.Description);
        Append(hash, definition.BaseMonetaryValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendImage(hash, definition.VoxelVisual.IconPath, images);
        AppendImage(hash, definition.VoxelVisual.TopPath, images);
        AppendImage(hash, definition.VoxelVisual.SidePath, images);
        AppendImage(hash, definition.VoxelVisual.BottomPath, images);
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static void AppendImage(IncrementalHash hash, string path, IReadOnlyDictionary<string, DecodedImage> images)
    {
        Append(hash, path);
        var image = images[path];
        Append(hash, image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, image.Format);
        hash.AppendData(image.Pixels as byte[] ?? image.Pixels.ToArray());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData("\n"u8);
    }
}

internal static class CatalogDiff
{
    public static GoodCatalogChange Compute(long version, GoodCatalogSnapshot? previous, IReadOnlyDictionary<GoodId, GoodCatalogEntry> next)
    {
        var added = new HashSet<GoodId>();
        var removed = new HashSet<GoodId>();
        var changed = new HashSet<GoodId>();
        var previousEntries = previous?.Entries;

        if (previousEntries is null)
        {
            added.UnionWith(next.Keys);
            return new GoodCatalogChange(version, added, removed, changed);
        }

        foreach (var (id, entry) in next)
        {
            if (!previousEntries.TryGetValue(id, out var prior))
                added.Add(id);
            else if (!string.Equals(prior.Fingerprint, entry.Fingerprint, StringComparison.Ordinal))
                changed.Add(id);
        }

        foreach (var id in previousEntries.Keys)
        {
            if (!next.ContainsKey(id))
                removed.Add(id);
        }

        return new GoodCatalogChange(version, added, removed, changed);
    }

    public static bool HasChanges(GoodCatalogChange change) =>
        change.Added.Count > 0 || change.Removed.Count > 0 || change.Changed.Count > 0;
}

internal static class ImmutableSets
{
    public static IReadOnlySet<GoodId> Empty { get; } = FrozenSet<GoodId>.Empty;
    public static IReadOnlySet<SettlementId> EmptySettlements { get; } = FrozenSet<SettlementId>.Empty;

    public static IReadOnlyList<ContentDiagnostic> Freeze(IReadOnlyList<ContentDiagnostic> diagnostics) =>
        diagnostics as ReadOnlyCollection<ContentDiagnostic> ?? new ReadOnlyCollection<ContentDiagnostic>(diagnostics.ToArray());
}

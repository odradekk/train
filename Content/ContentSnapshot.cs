using System.Collections.Frozen;
using Train.Content.Composites;
using Train.Content.Markets;
using Train.Content.Settlements;
using Train.Content.Voxels;

namespace Train.Content;

public sealed class ContentSnapshot
{
    public long Generation { get; }

    public IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> Voxels { get; }

    public IReadOnlyDictionary<CompositeDefinitionId, CompositeContentEntry> Blueprints { get; }

    public IReadOnlyDictionary<MarketTableDefinitionId, MarketTableContentEntry> MarketTables { get; }

    public IReadOnlyDictionary<SettlementTypeDefinitionId, SettlementTypeContentEntry> SettlementTypes { get; }

    internal ContentSnapshot(
        long generation,
        IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeContentEntry> blueprints,
        IReadOnlyDictionary<MarketTableDefinitionId, MarketTableContentEntry> marketTables,
        IReadOnlyDictionary<SettlementTypeDefinitionId, SettlementTypeContentEntry> settlementTypes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        Voxels = Freeze(voxels, static entry => entry.Definition.Id);
        Blueprints = Freeze(blueprints, static entry => entry.Definition.Id);
        MarketTables = Freeze(marketTables, static entry => entry.Definition.Id);
        SettlementTypes = Freeze(settlementTypes, static entry => entry.Definition.Id);
    }

    private static IReadOnlyDictionary<TId, TEntry> Freeze<TId, TEntry>(
        IReadOnlyDictionary<TId, TEntry> entries, Func<TEntry, TId> identity)
        where TId : notnull
        where TEntry : class
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var (id, entry) in entries)
        {
            if (entry is null || !EqualityComparer<TId>.Default.Equals(id, identity(entry)))
                throw new ArgumentException("Content index keys must match their definitions.", nameof(entries));
        }
        return entries.ToFrozenDictionary();
    }
}

public sealed class ContentChange
{
    public ContentSnapshot? Previous { get; }

    public ContentSnapshot Current { get; }

    internal ContentChange(ContentSnapshot? previous, ContentSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Generation != checked((previous?.Generation ?? 0) + 1))
            throw new ArgumentException("A content publication must advance one generation.", nameof(current));
        Previous = previous;
        Current = current;
    }
}

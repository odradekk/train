using System.Collections.Frozen;
using Train.Content.Voxels;

namespace Train.Content.Settlements;

public readonly record struct SettlementTypeDefinitionId
{
    public string Namespace { get; }
    public string LocalId { get; }
    public string Value => $"{Namespace}:{LocalId}";

    public SettlementTypeDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    public static SettlementTypeDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(SettlementTypeDefinitionId));
        return new SettlementTypeDefinitionId(ns, localId);
    }

    public override string ToString() => Value;
}

public sealed class SettlementMarketQuantity
{
    public int Capacity { get; }
    public int RestorePerTick { get; }

    internal SettlementMarketQuantity(int capacity, int restorePerTick)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        if (restorePerTick < 0)
            throw new ArgumentOutOfRangeException(nameof(restorePerTick), restorePerTick, "Restore per tick cannot be negative.");

        Capacity = capacity;
        RestorePerTick = restorePerTick;
    }
}

public sealed class SettlementPopulationRange
{
    public int Min { get; }
    public int Max { get; }

    internal SettlementPopulationRange(int min, int max)
    {
        if (min <= 0)
            throw new ArgumentOutOfRangeException(nameof(min), min, "Minimum population must be positive.");
        if (max <= 0)
            throw new ArgumentOutOfRangeException(nameof(max), max, "Maximum population must be positive.");
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(max), max, "Maximum population cannot be less than minimum population.");

        Min = min;
        Max = max;
    }
}

public sealed class SettlementTypeDefinition
{
    public SettlementTypeDefinitionId Id { get; }
    public string Name { get; }
    public IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> Supply { get; }
    public IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> Demand { get; }
    public IReadOnlySet<string> PreferredTerrain { get; }
    public int Weight { get; }
    public SettlementPopulationRange Population { get; }
    public int InitialPlatformCount { get; }

    internal SettlementTypeDefinition(
        SettlementTypeDefinitionId id, string name,
        IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> supply,
        IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> demand,
        IEnumerable<string> preferredTerrain, int weight,
        SettlementPopulationRange population, int initialPlatformCount)
    {
        ArgumentNullException.ThrowIfNull(supply);
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(preferredTerrain);
        ArgumentNullException.ThrowIfNull(population);
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("SettlementTypeDefinitionId cannot be blank.", nameof(id));
        if (supply.Keys.Any(static key => string.IsNullOrEmpty(key.Namespace) || string.IsNullOrEmpty(key.LocalId)))
            throw new ArgumentException("Supply cannot contain an invalid good identity.", nameof(supply));
        if (supply.Values.Any(static quantity => quantity is null))
            throw new ArgumentNullException(nameof(supply), "Supply cannot contain null quantities.");
        if (demand.Keys.Any(static key => string.IsNullOrEmpty(key.Namespace) || string.IsNullOrEmpty(key.LocalId)))
            throw new ArgumentException("Demand cannot contain an invalid good identity.", nameof(demand));
        if (demand.Values.Any(static quantity => quantity is null))
            throw new ArgumentNullException(nameof(demand), "Demand cannot contain null quantities.");
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be positive.");
        if (initialPlatformCount < 0)
            throw new ArgumentOutOfRangeException(nameof(initialPlatformCount), initialPlatformCount, "Initial platform count cannot be negative.");

        var terrain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in preferredTerrain)
        {
            if (!ContentIdentity.IsValidFullId(identity))
                throw new ArgumentException("Terrain identities must be canonical namespace:local-id strings.", nameof(preferredTerrain));
            terrain.Add(identity);
        }

        Id = id;
        Name = VoxelDefinition.NormalizeDisplayText(
            name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, nameof(name));
        Supply = supply.ToFrozenDictionary();
        Demand = demand.ToFrozenDictionary();
        PreferredTerrain = terrain.ToFrozenSet(StringComparer.Ordinal);
        Weight = weight;
        Population = population;
        InitialPlatformCount = initialPlatformCount;
    }
}

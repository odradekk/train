using System.Collections.Frozen;
using Train.Content.Voxels;

namespace Train.Content.Markets;

public readonly record struct MarketTableDefinitionId
{
    public string Namespace { get; }
    public string LocalId { get; }
    public string Value => $"{Namespace}:{LocalId}";

    public MarketTableDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    public static MarketTableDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(MarketTableDefinitionId));
        return new MarketTableDefinitionId(ns, localId);
    }

    public override string ToString() => Value;
}

public sealed class MarketPriceRule
{
    public double Sensitivity { get; }
    public long MinPrice { get; }
    public long MaxPrice { get; }

    internal MarketPriceRule(double sensitivity, long minPrice, long maxPrice)
    {
        if (!double.IsFinite(sensitivity) || sensitivity < 0)
            throw new ArgumentOutOfRangeException(nameof(sensitivity), sensitivity, "Sensitivity must be finite and nonnegative.");
        if (minPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(minPrice), minPrice, "Minimum price must be positive.");
        if (maxPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPrice), maxPrice, "Maximum price must be positive.");
        if (minPrice > maxPrice)
            throw new ArgumentOutOfRangeException(nameof(maxPrice), maxPrice, "Maximum price cannot be less than minimum price.");

        Sensitivity = sensitivity == 0 ? 0d : sensitivity;
        MinPrice = minPrice;
        MaxPrice = maxPrice;
    }
}

public sealed class MarketTableDefinition
{
    public MarketTableDefinitionId Id { get; }
    public IReadOnlyDictionary<VoxelDefinitionId, MarketPriceRule> Goods { get; }

    internal MarketTableDefinition(
        MarketTableDefinitionId id, IReadOnlyDictionary<VoxelDefinitionId, MarketPriceRule> goods)
    {
        ArgumentNullException.ThrowIfNull(goods);
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("MarketTableDefinitionId cannot be blank.", nameof(id));
        if (goods.Count == 0)
            throw new ArgumentException("A market table must contain at least one good.", nameof(goods));
        if (goods.Keys.Any(static key => string.IsNullOrEmpty(key.Namespace) || string.IsNullOrEmpty(key.LocalId)))
            throw new ArgumentException("A market table cannot contain an invalid good identity.", nameof(goods));
        if (goods.Values.Any(static rule => rule is null))
            throw new ArgumentNullException(nameof(goods), "A market table cannot contain null price rules.");

        Id = id;
        Goods = goods.ToFrozenDictionary();
    }
}

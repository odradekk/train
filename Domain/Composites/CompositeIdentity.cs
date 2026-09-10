using System.Globalization;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public readonly record struct EntityId
{
    public ulong Value { get; }

    public EntityId(ulong value)
    {
        if (value == 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Runtime entity ids start at 1.");
        Value = value;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct LocalVoxelCoord(int X, int Y, int Z) : IComparable<LocalVoxelCoord>
{
    public int CompareTo(LocalVoxelCoord other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
            return x;
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    public override string ToString() => $"({X},{Y},{Z})";
}

public readonly record struct CompositeDefinitionId
{
    public string Namespace { get; }
    public string LocalId { get; }

    public CompositeDefinitionId(string @namespace, string localId)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        LocalId = VoxelDefinitionId.NormalizePart(localId, nameof(localId));
    }

    public string Value => $"{Namespace}:{LocalId}";

    public override string ToString() => Value;

    public static CompositeDefinitionId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException("CompositeDefinitionId must have the form namespace:local-id.", nameof(value));

        return new CompositeDefinitionId(trimmed[..separator], trimmed[(separator + 1)..]);
    }
}

public readonly record struct CompositeArchetypeId
{
    public string Namespace { get; }
    public string LocalId { get; }

    public CompositeArchetypeId(string @namespace, string localId)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        LocalId = VoxelDefinitionId.NormalizePart(localId, nameof(localId));
    }

    public string Value => $"{Namespace}:{LocalId}";

    public override string ToString() => Value;

    public static CompositeArchetypeId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException("CompositeArchetypeId must have the form namespace:local-id.", nameof(value));

        return new CompositeArchetypeId(trimmed[..separator], trimmed[(separator + 1)..]);
    }
}

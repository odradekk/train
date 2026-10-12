namespace Train.Content.Composites;

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
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    public string Value => $"{Namespace}:{LocalId}";
    public override string ToString() => Value;

    public static CompositeDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(CompositeDefinitionId));
        return new CompositeDefinitionId(ns, localId);
    }
}

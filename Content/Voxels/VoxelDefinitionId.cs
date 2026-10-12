namespace Train.Content.Voxels;

public readonly record struct VoxelDefinitionId
{
    public const int PartMinLength = ContentIdentity.PartMinLength;
    public const int PartMaxLength = ContentIdentity.PartMaxLength;
    public const string BuiltInNamespace = "core";

    public string Namespace { get; }
    public string LocalId { get; }

    public VoxelDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    public string Value => $"{Namespace}:{LocalId}";

    public override string ToString() => Value;

    public static VoxelDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(VoxelDefinitionId));
        return new VoxelDefinitionId(ns, localId);
    }

    public static bool IsValidPart(string? value) => ContentIdentity.IsValidPart(value);

    internal static bool TryResolve(string raw, string packageNamespace, out VoxelDefinitionId id)
    {
        if (ContentIdentity.IsValidFullId(raw))
            id = Parse(raw);
        else if (ContentIdentity.IsValidPart(raw))
            id = new VoxelDefinitionId(packageNamespace, raw);
        else
        {
            id = default;
            return false;
        }
        return true;
    }
}

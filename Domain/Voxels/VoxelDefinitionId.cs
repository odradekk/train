namespace Train.Domain.Voxels;

public readonly record struct VoxelDefinitionId
{
    public const int PartMinLength = 1;
    public const int PartMaxLength = 64;
    public const string BuiltInNamespace = "core";

    public string Namespace { get; }
    public string LocalId { get; }

    public VoxelDefinitionId(string @namespace, string localId)
    {
        Namespace = NormalizePart(@namespace, nameof(@namespace));
        LocalId = NormalizePart(localId, nameof(localId));
    }

    public string Value => $"{Namespace}:{LocalId}";

    public override string ToString() => Value;

    public static VoxelDefinitionId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException("VoxelDefinitionId must have the form namespace:local-id.", nameof(value));

        return new VoxelDefinitionId(trimmed[..separator], trimmed[(separator + 1)..]);
    }

    public static bool IsValidPart(string? value)
    {
        if (value is null || value.Length is < PartMinLength or > PartMaxLength)
            return false;
        if (!IsStartChar(value[0]))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!IsPartChar(value[i]))
                return false;
        }

        return true;
    }

    internal static string NormalizePart(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (!IsValidPart(trimmed))
            throw new ArgumentException("Must be 1-64 lowercase ASCII letters, digits, underscores, or hyphens, starting with a letter or digit.", paramName);
        return trimmed;
    }

    private static bool IsStartChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static bool IsPartChar(char c) => IsStartChar(c) || c is '_' or '-';
}

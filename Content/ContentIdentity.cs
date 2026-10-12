namespace Train.Content;

internal static class ContentIdentity
{
    public const int PartMinLength = 1;
    public const int PartMaxLength = 64;

    public static bool IsValidPart(string? value)
    {
        if (value is null || value.Length is < PartMinLength or > PartMaxLength)
            return false;
        if (!IsStartChar(value[0]))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!IsStartChar(value[i]) && value[i] is not '_' and not '-')
                return false;
        }
        return true;
    }

    public static bool IsValidFullId(string? value)
    {
        if (value is null)
            return false;
        var separator = value.IndexOf(':');
        return separator > 0 && IsValidPart(value[..separator]) && IsValidPart(value[(separator + 1)..]);
    }

    public static string NormalizePart(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (!IsValidPart(trimmed))
            throw new ArgumentException("Must be 1-64 lowercase ASCII letters, digits, underscores, or hyphens, starting with a letter or digit.", paramName);
        return trimmed;
    }

    public static (string Namespace, string LocalId) Split(string value, string typeName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator != trimmed.LastIndexOf(':') || separator == trimmed.Length - 1)
            throw new ArgumentException($"{typeName} must have the form namespace:local-id.", nameof(value));
        return (trimmed[..separator], trimmed[(separator + 1)..]);
    }

    private static bool IsStartChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}

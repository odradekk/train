namespace Train.Core.World;

internal sealed class CoreStateFormatException(string field) : Exception
{
    internal string Field { get; } = field;
}

internal static class WorldFormat
{
    internal static bool IsIdentity(string? value)
    {
        if (value is null) return false;
        var colon = value.IndexOf(':');
        return colon > 0 && colon < value.Length - 1 && colon == value.LastIndexOf(':')
            && !value.Any(char.IsWhiteSpace) && value == value.ToLowerInvariant();
    }

    internal static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

}

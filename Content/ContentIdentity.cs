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

    private static bool IsStartChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}

using System.Text.RegularExpressions;

namespace Train.Content;

internal static class PackageIdentity
{
    private static readonly Regex CanonicalUuid = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryCanonicalUuid(string? value, out string canonical)
    {
        canonical = "";
        if (value is null || !CanonicalUuid.IsMatch(value))
            return false;
        if (value == "00000000-0000-0000-0000-000000000000")
            return false;
        canonical = value;
        return true;
    }
}

internal static class SemVer
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var plus = value.IndexOf('+');
        var coreAndPre = plus < 0 ? value : value[..plus];
        var build = plus < 0 ? null : value[(plus + 1)..];
        if (plus >= 0 && build!.Length == 0)
            return false;

        var dash = coreAndPre.IndexOf('-');
        var core = dash < 0 ? coreAndPre : coreAndPre[..dash];
        var pre = dash < 0 ? null : coreAndPre[(dash + 1)..];
        if (dash >= 0 && pre!.Length == 0)
            return false;

        var coreParts = core.Split('.');
        if (coreParts.Length != 3)
            return false;
        if (!IsNumericIdentifier(coreParts[0]) ||
            !IsNumericIdentifier(coreParts[1]) ||
            !IsNumericIdentifier(coreParts[2]))
            return false;

        if (pre is not null && !AreIdentifiers(pre, prerelease: true))
            return false;
        if (build is not null && !AreIdentifiers(build, prerelease: false))
            return false;
        return true;
    }

    private static bool AreIdentifiers(string text, bool prerelease)
    {
        if (text.Length == 0)
            return false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '.')
                continue;
            if (i == start)
                return false;
            if (!IsIdentifier(text.AsSpan(start, i - start), prerelease))
                return false;
            start = i + 1;
        }

        return true;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> value, bool prerelease)
    {
        if (value.Length == 0)
            return false;
        var digitsOnly = true;
        foreach (var c in value)
        {
            if (c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-')
            {
                if (c is not (>= '0' and <= '9'))
                    digitsOnly = false;
                continue;
            }

            return false;
        }

        if (prerelease && digitsOnly)
            return IsNumericIdentifier(value.ToString());
        return true;
    }

    private static bool IsNumericIdentifier(string value)
    {
        if (value.Length == 0)
            return false;
        if (value == "0")
            return true;
        if (value[0] is < '1' or > '9')
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
                return false;
        }

        return true;
    }
}

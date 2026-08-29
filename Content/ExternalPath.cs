namespace Train.Content;

public static class ExternalPath
{
    public static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.GetFullPath(path);
    }

    public static bool TryStat(string path, out FileAttributes attributes, out bool exists)
    {
        attributes = default;
        exists = false;
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        if (!TryStat(path, out var attributes, out var exists) || !exists)
            return false;
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    public static bool IsSafeUnderRoot(string root, string candidate)
    {
        if (!TryGetFullPath(root, out var rootFull) || !TryGetFullPath(candidate, out var candidateFull))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootTrimmed = TrimSeparators(rootFull);
        var candidateTrimmed = TrimSeparators(candidateFull);
        if (string.Equals(rootTrimmed, candidateTrimmed, comparison))
            return true;

        var prefix = rootTrimmed + Path.DirectorySeparatorChar;
        return candidateTrimmed.StartsWith(prefix, comparison);
    }

    public static bool AnyReparsePointOnChain(string root, string candidate)
    {
        if (!TryGetFullPath(root, out var rootFull) || !TryGetFullPath(candidate, out var candidateFull))
            return true;
        if (!IsSafeUnderRoot(rootFull, candidateFull))
            return true;

        foreach (var segment in EnumerateChain(rootFull, candidateFull))
        {
            if (!TryStat(segment, out var attributes, out var exists))
                return true;
            if (!exists)
                continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }

    public static bool IsUnsafe(string root, string candidate) =>
        !IsSafeUnderRoot(root, candidate) || AnyReparsePointOnChain(root, candidate);

    public static string CombineUnder(string root, string relativeForwardSlash)
    {
        var native = relativeForwardSlash.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, native));
    }

    public static IEnumerable<string> EnumerateChain(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootFull = TrimSeparators(Path.GetFullPath(root));
        var current = TrimSeparators(Path.GetFullPath(candidate));
        var stack = new Stack<string>();
        while (current.Length > 0)
        {
            stack.Push(current);
            if (string.Equals(current, rootFull, comparison))
                break;
            var parent = TrimSeparators(Path.GetDirectoryName(current) ?? "");
            if (parent.Length == 0 || string.Equals(parent, current, comparison))
                break;
            current = parent;
        }

        return stack;
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        fullPath = "";
        try
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal))
                return false;
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

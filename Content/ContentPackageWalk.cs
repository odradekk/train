namespace Train.Content;

public readonly record struct ContentTreeEntry(string Name, bool IsDirectory, bool IsLink);

public sealed class ContentFileDiscovery
{
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    public bool Invalid { get; }

    public ContentFileDiscovery(
        IReadOnlyList<string> files,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        bool invalid)
    {
        Files = files;
        Diagnostics = diagnostics;
        Invalid = invalid;
    }

    public static ContentFileDiscovery Of(IReadOnlyList<string> files) =>
        new(files, Array.Empty<ContentDiagnostic>(), invalid: false);
}

public static class ContentPackageWalk
{
    public static ContentFileDiscovery Discover(
        string root,
        Func<string, IReadOnlyList<ContentTreeEntry>?> listChildren,
        string fileSuffix = ".voxels.json")
    {
        var files = new List<string>();
        var bag = new DiagnosticBag();
        Collect(root, root, listChildren, files, bag, fileSuffix);
        files.Sort(StringComparer.Ordinal);
        return new ContentFileDiscovery(files, bag.Items.ToArray(), bag.Invalid);
    }

    public static ContentDiagnostic LinkedPathDiagnostic(string path) =>
        new(
            ContentDiagnosticSeverity.Error,
            ContentDiagnosticCodes.UnsafeLinkedPath,
            "Linked or reparse-point content is not allowed.",
            path);

    private static void Collect(
        string root,
        string current,
        Func<string, IReadOnlyList<ContentTreeEntry>?> listChildren,
        List<string> files,
        DiagnosticBag bag,
        string fileSuffix)
    {
        var entries = listChildren(current);
        if (entries is null)
        {
            AddLinked(bag, current);
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;
            var path = current.TrimEnd('/') + "/" + entry.Name;
            if (entry.IsLink)
            {
                AddLinked(bag, path);
                continue;
            }

            if (entry.IsDirectory)
            {
                Collect(root, path, listChildren, files, bag, fileSuffix);
                continue;
            }

            if (!entry.Name.EndsWith(fileSuffix, StringComparison.Ordinal))
                continue;
            var relative = path.StartsWith(root + "/", StringComparison.Ordinal)
                ? path[(root.Length + 1)..]
                : entry.Name;
            if (ContentPath.TryNormalize(relative, out var normalized))
                files.Add(normalized);
        }
    }

    private static void AddLinked(DiagnosticBag bag, string path)
    {
        var diagnostic = LinkedPathDiagnostic(path);
        bag.Error(diagnostic.Code, diagnostic.Message, path);
    }
}

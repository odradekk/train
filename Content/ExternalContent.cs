namespace Train.Content;

public static class ExternalContent
{
    public const string ModsGodotPath = "user://mods";
    public const string BlueprintsGodotPath = "user://blueprints";

    public static bool TryEnsureDirectory(string trustedUserDataRoot, string modsPath, out string? error)
    {
        if (!ExternalPath.TryValidateTrustedChain(trustedUserDataRoot, modsPath, out error))
            return false;

        try
        {
            Directory.CreateDirectory(modsPath);
            if (!Directory.Exists(modsPath))
            {
                error = "Mods directory could not be created.";
                return false;
            }

            if (!ExternalPath.TryValidateTrustedChain(trustedUserDataRoot, modsPath, out error))
                return false;

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryListDirectChildren(
        string trustedUserDataRoot,
        string modsPath,
        out IReadOnlyList<ModFolderCandidate> children,
        out string? error)
    {
        children = Array.Empty<ModFolderCandidate>();
        error = null;
        if (!ExternalPath.TryValidateTrustedChain(trustedUserDataRoot, modsPath, out error))
            return false;

        string root;
        try
        {
            root = ExternalPath.NormalizeRoot(modsPath);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        if (!ExternalPath.TryStat(root, out var rootAttributes, out var exists))
        {
            error = "Mods directory could not be read.";
            return false;
        }

        if (!exists)
        {
            error = "Mods directory is unavailable.";
            return false;
        }

        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            error = "Mods directory is a linked or reparse path.";
            return false;
        }

        try
        {
            var candidates = new List<ModFolderCandidate>();
            foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (info.Name is "." or "..")
                    continue;

                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                if (!isDirectory)
                    continue;

                var packageRoot = info.FullName;
                var packageUnsafe = isLink || ExternalPath.IsUnsafe(root, packageRoot);
                var manifestPath = Path.Combine(packageRoot, ModDiscovery.ManifestFileName);
                var (manifestBytes, missing, unsafeManifest) = ReadManifest(root, packageRoot, manifestPath, packageUnsafe);
                candidates.Add(new ModFolderCandidate(
                    info.Name,
                    packageRoot,
                    isDirectory: true,
                    isLink: isLink,
                    packageRootUnsafe: packageUnsafe,
                    manifestMissing: missing,
                    manifestUnsafe: unsafeManifest,
                    manifestBytes: manifestBytes));
            }

            candidates.Sort((left, right) => string.CompareOrdinal(left.FolderName, right.FolderName));
            children = candidates;
            return true;
        }
        catch (Exception exception)
        {
            children = Array.Empty<ModFolderCandidate>();
            error = exception.Message;
            return false;
        }
    }

    public static IReadOnlyList<ModFolderCandidate> ListDirectChildren(string trustedUserDataRoot, string modsPath) =>
        TryListDirectChildren(trustedUserDataRoot, modsPath, out var children, out _)
            ? children
            : Array.Empty<ModFolderCandidate>();

    public static bool TryListBlueprintFiles(
        string trustedUserDataRoot,
        string blueprintsPath,
        out IReadOnlyList<(string FileName, string FullPath)> files,
        out string? error)
    {
        files = Array.Empty<(string, string)>();
        error = null;
        if (!ExternalPath.TryValidateTrustedChain(trustedUserDataRoot, blueprintsPath, out error))
            return false;

        string root;
        try
        {
            root = ExternalPath.NormalizeRoot(blueprintsPath);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        if (!ExternalPath.TryStat(root, out var rootAttributes, out var exists))
        {
            error = "Blueprints directory could not be read.";
            return false;
        }

        if (!exists)
        {
            error = "Blueprints directory is unavailable.";
            return false;
        }

        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            error = "Blueprints directory is a linked or reparse path.";
            return false;
        }

        try
        {
            var found = new List<(string FileName, string FullPath)>();
            foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (info.Name is "." or ".." ||
                    (info.Attributes & FileAttributes.Directory) != 0)
                    continue;
                if (!info.Name.EndsWith(".composites.json", StringComparison.Ordinal))
                    continue;
                found.Add((info.Name, info.FullName));
            }

            found.Sort((left, right) => string.CompareOrdinal(left.FileName, right.FileName));
            files = found;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static ContentDiagnostic BlueprintsRootUnavailable(string source, string message) =>
        new(
            ContentDiagnosticSeverity.Warning,
            ContentDiagnosticCodes.BlueprintsRootUnavailable,
            message,
            source);

    public static ContentFileDiscovery ListDefinitionFiles(string resourcesRoot)
    {
        if (!ExternalPath.TryStat(resourcesRoot, out var attributes, out var exists))
        {
            return new ContentFileDiscovery(
                Array.Empty<string>(),
                new[] { ContentPackageWalk.LinkedPathDiagnostic(resourcesRoot) },
                invalid: true);
        }

        if (!exists)
            return ContentFileDiscovery.Of(Array.Empty<string>());

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new ContentFileDiscovery(
                Array.Empty<string>(),
                new[] { ContentPackageWalk.LinkedPathDiagnostic(resourcesRoot) },
                invalid: true);
        }

        var walkRoot = resourcesRoot.Replace('\\', '/');
        return ContentPackageWalk.Discover(walkRoot, path => ListPhysicalEntries(path, resourcesRoot), ".json");
    }

    public static ContentBytesResult ReadFile(string resourcesRoot, string normalizedRelativePath, int maxBytes)
    {
        if (!ContentPath.TryNormalize(normalizedRelativePath, out var normalized) ||
            !ContentPath.IsAllowedDataFile(normalized))
            return ContentBytesResult.Missing();

        string combined;
        try
        {
            combined = ExternalPath.CombineUnder(resourcesRoot, normalized);
        }
        catch
        {
            return ContentBytesResult.Linked();
        }

        if (ExternalPath.IsUnsafe(resourcesRoot, combined))
            return ContentBytesResult.Linked();

        try
        {
            var info = new FileInfo(combined);
            if (!info.Exists)
                return ContentBytesResult.Missing();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return ContentBytesResult.Linked();
            if (info.Length > maxBytes)
                return ContentBytesResult.LimitExceeded();
            return ContentBytesResult.Ok(File.ReadAllBytes(combined));
        }
        catch
        {
            return ContentBytesResult.Linked();
        }
    }

    public static ContentDiagnostic ModsRootUnavailable(string source, string message) =>
        new(
            ContentDiagnosticSeverity.Warning,
            ContentDiagnosticCodes.ModsRootUnavailable,
            message,
            source);

    private static (byte[]? Bytes, bool Missing, bool Unsafe) ReadManifest(
        string modsRoot,
        string packageRoot,
        string manifestPath,
        bool packageUnsafe)
    {
        if (packageUnsafe)
            return (null, true, true);
        if (!ExternalPath.TryStat(manifestPath, out var attributes, out var exists))
            return (null, false, true);
        if (exists && (attributes & FileAttributes.ReparsePoint) != 0)
            return (null, false, true);
        if (ExternalPath.IsUnsafe(modsRoot, manifestPath) || ExternalPath.IsUnsafe(packageRoot, manifestPath))
            return (null, false, true);
        if (!exists)
            return (null, true, false);

        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > ContentLimits.JsonFileMaxBytes)
                return (new byte[ContentLimits.JsonFileMaxBytes + 1], false, false);
            return (File.ReadAllBytes(manifestPath), false, false);
        }
        catch
        {
            return (null, false, true);
        }
    }

    private static IReadOnlyList<ContentTreeEntry>? ListPhysicalEntries(string current, string resourcesRoot)
    {
        var native = current.Replace('/', Path.DirectorySeparatorChar);
        if (ExternalPath.IsUnsafe(resourcesRoot, native))
            return null;
        if (!ExternalPath.TryStat(native, out var attributes, out var exists))
            return null;
        if (!exists || (attributes & FileAttributes.Directory) == 0)
            return null;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return null;

        try
        {
            var entries = new List<ContentTreeEntry>();
            foreach (var info in new DirectoryInfo(native).EnumerateFileSystemInfos())
            {
                if (info.Name is "." or "..")
                    continue;
                entries.Add(new ContentTreeEntry(
                    info.Name,
                    (info.Attributes & FileAttributes.Directory) != 0,
                    (info.Attributes & FileAttributes.ReparsePoint) != 0));
            }

            return entries;
        }
        catch
        {
            return null;
        }
    }
}

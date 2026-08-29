namespace Train.Content;

public sealed class ModFolderCandidate
{
    public string FolderName { get; }
    public string PackageRoot { get; }
    public bool IsDirectory { get; }
    public bool IsLink { get; }
    public bool PackageRootUnsafe { get; }
    public bool ManifestMissing { get; }
    public bool ManifestUnsafe { get; }
    public byte[]? ManifestBytes { get; }

    public ModFolderCandidate(
        string folderName,
        string packageRoot,
        bool isDirectory,
        bool isLink = false,
        bool packageRootUnsafe = false,
        bool manifestMissing = false,
        bool manifestUnsafe = false,
        byte[]? manifestBytes = null)
    {
        FolderName = folderName;
        PackageRoot = packageRoot;
        IsDirectory = isDirectory;
        IsLink = isLink;
        PackageRootUnsafe = packageRootUnsafe;
        ManifestMissing = manifestMissing;
        ManifestUnsafe = manifestUnsafe;
        ManifestBytes = manifestBytes;
    }
}

public sealed class ModScanResult
{
    public IReadOnlyList<ContentPackage> Packages { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    public int CandidateCount { get; }
    public int AcceptedCount { get; }

    public ModScanResult(
        IReadOnlyList<ContentPackage> packages,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        int candidateCount,
        int acceptedCount)
    {
        Packages = packages;
        Diagnostics = diagnostics;
        CandidateCount = candidateCount;
        AcceptedCount = acceptedCount;
    }
}

public static class ModDiscovery
{
    public const string ManifestFileName = "mod.json";
    public const string ResourcesDirectoryName = "resources";

    public static ModScanResult Scan(IEnumerable<ModFolderCandidate> candidates)
    {
        var packages = new List<ContentPackage>();
        var diagnostics = new List<ContentDiagnostic>();
        var parsed = new List<(ModFolderCandidate Candidate, ModManifest Manifest)>();
        var candidateCount = 0;

        foreach (var candidate in candidates)
        {
            if (!candidate.IsDirectory)
                continue;

            candidateCount++;
            var source = candidate.FolderName + "/" + ManifestFileName;
            var bag = new DiagnosticBag { Source = source };

            if (candidate.IsLink || candidate.PackageRootUnsafe)
            {
                bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.");
                diagnostics.AddRange(bag.Items);
                continue;
            }

            if (candidate.ManifestUnsafe)
            {
                bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.");
                diagnostics.AddRange(bag.Items);
                continue;
            }

            if (candidate.ManifestMissing || candidate.ManifestBytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.ModManifestMissing, "Mod directories must contain a root mod.json.");
                diagnostics.AddRange(bag.Items);
                continue;
            }

            var manifest = ModManifest.Parse(candidate.ManifestBytes, source, bag);
            diagnostics.AddRange(bag.Items);
            if (manifest is null || bag.Invalid)
                continue;

            parsed.Add((candidate, manifest));
        }

        var duplicateIds = parsed
            .GroupBy(item => item.Manifest.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in parsed)
        {
            if (duplicateIds.Contains(item.Manifest.Id))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Warning,
                    ContentDiagnosticCodes.DuplicateModUuid,
                    "Duplicate mod UUID disables every package using that UUID.",
                    item.Candidate.FolderName + "/" + ManifestFileName,
                    item.Manifest.Id));
                continue;
            }

            packages.Add(new ContentPackage(
                item.Manifest.Id,
                ResourcesRoot(item.Candidate.PackageRoot),
                available: true,
                ContentPackageKind.Mod,
                item.Candidate.FolderName + "/" + ManifestFileName));
        }

        return new ModScanResult(packages, diagnostics, candidateCount, packages.Count);
    }

    public static string ResourcesRoot(string packageRoot)
    {
        var trimmed = packageRoot.TrimEnd('/', '\\');
        return trimmed + "/" + ResourcesDirectoryName;
    }
}

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
        var declared = new List<(ModFolderCandidate Candidate, string? CanonicalId, ModManifest? Manifest, DiagnosticBag Bag)>();
        var candidateCount = 0;

        foreach (var candidate in candidates)
        {
            if (!candidate.IsDirectory)
                continue;

            candidateCount++;
            var source = candidate.FolderName + "/" + ManifestFileName;
            var bag = new DiagnosticBag(ContentDiagnosticSeverity.Warning) { Source = source };

            if (candidate.IsLink || candidate.PackageRootUnsafe)
            {
                bag.Reject(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.");
                diagnostics.AddRange(bag.Items);
                continue;
            }

            if (candidate.ManifestUnsafe)
            {
                bag.Reject(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.");
                diagnostics.AddRange(bag.Items);
                continue;
            }

            if (candidate.ManifestMissing || candidate.ManifestBytes is null)
            {
                bag.Reject(ContentDiagnosticCodes.ModManifestMissing, "Mod directories must contain a root mod.json.");
                diagnostics.AddRange(bag.Items);
                continue;
            }

            var read = ModManifest.Read(candidate.ManifestBytes, source, bag);
            declared.Add((candidate, read.CanonicalId, read.Manifest, bag));
        }

        var duplicateIds = declared
            .Where(item => item.CanonicalId is not null)
            .GroupBy(item => item.CanonicalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in declared)
        {
            if (item.CanonicalId is not null && duplicateIds.Contains(item.CanonicalId))
            {
                item.Bag.Reject(
                    ContentDiagnosticCodes.DuplicateModUuid,
                    "Duplicate mod UUID disables every package using that UUID.",
                    item.CanonicalId);
                diagnostics.AddRange(item.Bag.Items);
                continue;
            }

            diagnostics.AddRange(item.Bag.Items);
            if (item.Manifest is null)
                continue;

            packages.Add(new ContentPackage(
                item.Manifest.Id,
                ResourcesRoot(item.Candidate.PackageRoot),
                available: true,
                ContentPackageKind.Mod,
                item.Candidate.FolderName + "/" + ManifestFileName,
                new ModPackageMetadata(item.Manifest)));
        }

        return new ModScanResult(packages, diagnostics, candidateCount, packages.Count);
    }

    public static string ResourcesRoot(string packageRoot)
    {
        var trimmed = packageRoot.TrimEnd('/', '\\');
        return trimmed + "/" + ResourcesDirectoryName;
    }
}

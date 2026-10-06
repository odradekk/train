// Train.Core 内容基础设施模块。
// 从 Mod 根目录候选中发现、隔离并规范化可加载包。
namespace Train.Core.Content;

/// <summary>表示 Mod 根目录中的一个直接子项及其已读取的清单状态。</summary>
public sealed class ModFolderCandidate
{
    /// <summary>取得 Mod 根下的直接子目录名称。</summary>
    public string FolderName { get; }
    /// <summary>取得候选包的物理根路径。</summary>
    public string PackageRoot { get; }
    /// <summary>取得候选是否为目录；文件候选不会参与扫描计数。</summary>
    public bool IsDirectory { get; }
    /// <summary>取得候选本身是否为链接或重解析点。</summary>
    public bool IsLink { get; }
    /// <summary>取得候选包根的可信路径链是否已判定为不安全。</summary>
    public bool PackageRootUnsafe { get; }
    /// <summary>取得根清单是否缺失。</summary>
    public bool ManifestMissing { get; }
    /// <summary>取得根清单路径是否经过链接或重解析点。</summary>
    public bool ManifestUnsafe { get; }
    /// <summary>取得清单原始字节；未读取或缺失时为 <see langword="null"/>。</summary>
    public byte[]? ManifestBytes { get; }

    /// <summary>创建 Mod 文件夹候选。</summary>
    /// <param name="folderName">直接子目录名称。</param><param name="packageRoot">包的物理根路径。</param><param name="isDirectory">候选是否为目录。</param><param name="isLink">候选是否为链接或重解析点。</param><param name="packageRootUnsafe">包根链是否不安全。</param><param name="manifestMissing">根清单是否缺失。</param><param name="manifestUnsafe">清单路径是否不安全。</param><param name="manifestBytes">清单原始字节；未读取时为 <see langword="null"/>。</param>
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

/// <summary>表示一次 Mod 根目录扫描的包与诊断快照。</summary>
public sealed class ModScanResult
{
    /// <summary>取得接受的 Mod 包快照。</summary>
    public IReadOnlyList<ContentPackage> Packages { get; }
    /// <summary>取得所有候选产生的诊断快照。</summary>
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    /// <summary>取得参与扫描的直接子目录数；普通文件不计入。</summary>
    public int CandidateCount { get; }
    /// <summary>取得成功接受的包数。</summary>
    public int AcceptedCount { get; }

    /// <summary>创建 Mod 扫描结果。</summary><param name="packages">接受的包。</param><param name="diagnostics">扫描诊断。</param><param name="candidateCount">参与扫描的直接子目录数。</param><param name="acceptedCount">成功接受的包数。</param>
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

/// <summary>从直接子目录候选中发现数据 Mod，并隔离不安全或冲突的包。</summary>
public static class ModDiscovery
{
    /// <summary>Mod 根清单文件名；由当前内容 schema 约定。</summary>
    public const string ManifestFileName = "mod.json";
    /// <summary>Mod 数据资源目录名；由当前内容 schema 约定。</summary>
    public const string ResourcesDirectoryName = "resources";

    /// <summary>扫描候选，严格解析清单，并禁用共享同一 UUID 的所有声明。</summary><param name="candidates">Mod 根目录的直接子项。</param><returns>接受的包与有界诊断。</returns>
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
                item.Bag.Warn(
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

    /// <summary>计算 Mod 包根下的资源目录路径。</summary><param name="packageRoot">Mod 包根路径。</param><returns>使用正斜杠连接的资源目录路径。</returns>
    public static string ResourcesRoot(string packageRoot)
    {
        var trimmed = packageRoot.TrimEnd('/', '\\');
        return trimmed + "/" + ResourcesDirectoryName;
    }
}

using Train.Content;
using Train.Content.Voxels;
using Godot;
using Train.App.Logging;

namespace Train.App;

public sealed class GodotContentSource : IContentSource
{
    // 资源放在 builtin/ 下，与 Content/ 源码目录分开，避免 Windows 上目录名称的冲突。
    public const string BuiltInResourcesRoot = "res://builtin/core/resources";
    public const string ModsGodotPath = ExternalContent.ModsGodotPath;
    public const string BlueprintsGodotPath = ExternalContent.BlueprintsGodotPath;

    private readonly TrainLogger? _logger;
    private readonly string? _userDataDirectory;
    private readonly string _builtInResourcesRoot;
    private readonly bool _usesPhysicalBuiltIn;
    private IReadOnlyList<ContentDiagnostic> _scanDiagnostics = Array.Empty<ContentDiagnostic>();

    public GodotContentSource(
        TrainLogger? logger = null,
        string? userDataDirectory = null,
        string? builtInResourcesRoot = null)
    {
        _logger = logger;
        _userDataDirectory = userDataDirectory;
        _builtInResourcesRoot = string.IsNullOrWhiteSpace(builtInResourcesRoot)
            ? BuiltInResourcesRoot
            : builtInResourcesRoot;
        _usesPhysicalBuiltIn = !_builtInResourcesRoot.StartsWith("res://", StringComparison.Ordinal);
    }

    public string UserDataDirectory => _userDataDirectory ?? OS.GetUserDataDir();
    public string BuiltInResourcesRootPath => _builtInResourcesRoot;

    public IReadOnlyList<ContentPackage> ListPackages()
    {
        bool available;
        if (_usesPhysicalBuiltIn)
        {
            available = Directory.Exists(_builtInResourcesRoot);
        }
        else
        {
            using var probe = DirAccess.Open(_builtInResourcesRoot);
            available = probe is not null;
        }
        var packages = new List<ContentPackage>
        {
            new(VoxelDefinitionId.BuiltInNamespace, _builtInResourcesRoot, available)
        };

        var userData = UserDataDirectory;
        var physical = Path.Combine(userData, "mods");
        _logger?.Info(
            ContentLog.Category,
            ContentLog.ModsRootEventId,
            "Mods directory resolved.",
            new Dictionary<string, object?>
            {
                ["godot_path"] = ModsGodotPath,
                ["physical_path"] = physical
            });

        if (_userDataDirectory is null)
        {
            try
            {
                using var user = DirAccess.Open("user://");
                user?.MakeDir("mods");
                user?.MakeDir("blueprints");
            }
            catch
            {
            }
        }

        var diagnostics = new List<ContentDiagnostic>();
        string? error = null;
        if (!ExternalContent.TryEnsureDirectory(userData, physical, out error))
        {
            diagnostics.Add(ExternalContent.ModsRootUnavailable(ModsGodotPath, error ?? "Mods directory is unavailable."));
        }
        else if (!ExternalContent.TryListDirectChildren(userData, physical, out var children, out var listError))
        {
            diagnostics.Add(ExternalContent.ModsRootUnavailable(ModsGodotPath, listError ?? "Mods directory could not be read."));
        }
        else
        {
            try
            {
                var scan = ModDiscovery.Scan(children);
                diagnostics.AddRange(scan.Diagnostics);
                packages.AddRange(scan.Packages);
                _logger?.Info(
                    ContentLog.Category,
                    ContentLog.ModsScanSummaryEventId,
                    "Mod scan finished.",
                    new Dictionary<string, object?>
                    {
                        ["godot_path"] = ModsGodotPath,
                        ["physical_path"] = physical,
                        ["candidate_count"] = scan.CandidateCount,
                        ["accepted_count"] = scan.AcceptedCount
                    });
            }
            catch (Exception exception)
            {
                diagnostics.Add(ExternalContent.ModsRootUnavailable(ModsGodotPath, exception.Message));
            }
        }

        packages.AddRange(ListBlueprintPackages(userData, diagnostics));
        _scanDiagnostics = diagnostics;
        return packages;
    }

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _scanDiagnostics;

    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package)
    {
        if (package.Kind is ContentPackageKind.Mod or ContentPackageKind.PlayerBlueprint || IsPhysicalBuiltIn(package))
            return ExternalContent.ListDefinitionFiles(package.ResourcesRoot);

        if (IsLinkedOrUnresolved(package.ResourcesRoot))
            return new ContentFileDiscovery(
                Array.Empty<string>(),
                new[] { ContentPackageWalk.LinkedPathDiagnostic(package.ResourcesRoot) },
                invalid: true);

        return ContentPackageWalk.Discover(package.ResourcesRoot, ListEntries, ".json");
    }

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (package.Kind is ContentPackageKind.Mod or ContentPackageKind.PlayerBlueprint || IsPhysicalBuiltIn(package))
            return ExternalContent.ReadFile(package.ResourcesRoot, normalizedRelativePath, maxBytes);

        if (!ContentPath.TryNormalize(normalizedRelativePath, out var normalized))
            return ContentBytesResult.Missing();

        var virtualPath = package.ResourcesRoot.TrimEnd('/') + "/" + normalized;
        if (HasUnsafeLink(package.ResourcesRoot, virtualPath))
            return ContentBytesResult.Linked();
        if (!global::Godot.FileAccess.FileExists(virtualPath))
            return ContentBytesResult.Missing();

        using var file = global::Godot.FileAccess.Open(virtualPath, global::Godot.FileAccess.ModeFlags.Read);
        if (file is null)
            return ContentBytesResult.Missing();

        var length = file.GetLength();
        if (length > (ulong)maxBytes)
            return ContentBytesResult.LimitExceeded();
        return ContentBytesResult.Ok(file.GetBuffer((long)length));
    }

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath)
    {
        var read = ReadFile(package, normalizedRelativePath, ContentLimits.PngMaxBytes);
        if (read.UnsafeLink)
            return ImageDecodeResult.Linked();
        if (read.TooLarge)
            return ImageDecodeResult.LimitExceeded();
        if (read.Bytes is null)
            return ImageDecodeResult.Missing();
        return DecodePng(package, normalizedRelativePath, read.Bytes);
    }

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (!ContentPath.IsPngPath(normalizedRelativePath))
            return ImageDecodeResult.Missing();
        if (rawBytes.Length > ContentLimits.PngMaxBytes)
            return ImageDecodeResult.LimitExceeded();

        var image = new Image();
        if (image.LoadPngFromBuffer(rawBytes) != Error.Ok)
            return ImageDecodeResult.Invalid();
        if (image.GetWidth() > ContentLimits.ImageMaxDimension || image.GetHeight() > ContentLimits.ImageMaxDimension)
            return ImageDecodeResult.LimitExceeded();

        return ImageDecodeResult.Ok(new DecodedImage(
            image.GetWidth(),
            image.GetHeight(),
            NormalizeFormat(image.GetFormat()),
            image.GetData()));
    }

    private IReadOnlyList<ContentPackage> ListBlueprintPackages(string userData, List<ContentDiagnostic> diagnostics)
    {
        var physical = Path.Combine(userData, "blueprints");
        string? error = null;
        if (!ExternalContent.TryEnsureDirectory(userData, physical, out error))
        {
            diagnostics.Add(ExternalContent.BlueprintsRootUnavailable(
                BlueprintsGodotPath,
                error ?? "Blueprints directory is unavailable."));
            return Array.Empty<ContentPackage>();
        }

        if (!ExternalContent.TryListBlueprintFiles(userData, physical, out var files, out var listError))
        {
            diagnostics.Add(ExternalContent.BlueprintsRootUnavailable(
                BlueprintsGodotPath,
                listError ?? "Blueprints directory could not be read."));
            return Array.Empty<ContentPackage>();
        }

        var packages = new List<ContentPackage>();
        foreach (var (fileName, _) in files)
        {
            var idText = fileName[..^".composites.json".Length];
            if (!Guid.TryParseExact(idText, "D", out var guid) ||
                !string.Equals(idText, guid.ToString("D"), StringComparison.Ordinal))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Warning,
                    ContentDiagnosticCodes.UuidInvalid,
                    "Player blueprint file name must be a lowercase UUID.",
                    fileName));
                continue;
            }

            packages.Add(new ContentPackage(
                idText,
                physical,
                available: true,
                ContentPackageKind.PlayerBlueprint,
                fileName));
        }

        return packages;
    }

    private bool IsPhysicalBuiltIn(ContentPackage package) =>
        package.Kind == ContentPackageKind.BuiltIn && _usesPhysicalBuiltIn;

    private static IReadOnlyList<ContentTreeEntry>? ListEntries(string current)
    {
        if (IsLinkedOrUnresolved(current))
            return null;

        using var dir = DirAccess.Open(current);
        if (dir is null)
            return null;

        var entries = new List<ContentTreeEntry>();
        dir.ListDirBegin();
        for (var name = dir.GetNext(); name.Length > 0; name = dir.GetNext())
        {
            if (name is "." or "..")
                continue;
            entries.Add(new ContentTreeEntry(name, dir.CurrentIsDir(), dir.IsLink(name)));
        }

        dir.ListDirEnd();
        return entries;
    }

    // Godot 4.7 的 C# 绑定将 is_link(path) 暴露为已打开父目录上的实例方法。
    private static bool IsLinkedOrUnresolved(string virtualPath)
    {
        var parent = virtualPath.GetBaseDir();
        var name = virtualPath.GetFile();
        if (string.IsNullOrEmpty(name))
            return true;
        using var dir = DirAccess.Open(parent);
        return dir is null || dir.IsLink(name);
    }

    private static bool HasUnsafeLink(string root, string virtualPath)
    {
        if (!ContentPath.IsUnderRoot(root, virtualPath) || IsLinkedOrUnresolved(root))
            return true;

        var prefix = root.TrimEnd('/') + "/";
        if (!virtualPath.StartsWith(prefix, StringComparison.Ordinal))
            return true;

        var current = root.TrimEnd('/');
        foreach (var segment in virtualPath[prefix.Length..].Split('/'))
        {
            current += "/" + segment;
            if (segment.Length == 0 || IsLinkedOrUnresolved(current))
                return true;
        }

        return false;
    }

    private static string NormalizeFormat(Image.Format format) => format.ToString().ToLowerInvariant();
}

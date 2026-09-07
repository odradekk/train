using Godot;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Content;

public sealed class GodotContentSource : IContentSource
{
    // Keep assets off Content/ so Windows can host C# sources and built-in files side by side.
    public const string BuiltInResourcesRoot = "res://builtin/core/resources";
    public const string ModsGodotPath = ExternalContent.ModsGodotPath;

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
            }
            catch
            {
                // Physical creation below is authoritative.
            }
        }

        if (!ExternalContent.TryEnsureDirectory(userData, physical, out var error))
        {
            _scanDiagnostics = new[]
            {
                ExternalContent.ModsRootUnavailable(ModsGodotPath, error ?? "Mods directory is unavailable.")
            };
            return packages;
        }

        if (!ExternalContent.TryListDirectChildren(userData, physical, out var children, out var listError))
        {
            _scanDiagnostics = new[]
            {
                ExternalContent.ModsRootUnavailable(ModsGodotPath, listError ?? "Mods directory could not be read.")
            };
            return packages;
        }

        try
        {
            var scan = ModDiscovery.Scan(children);
            _scanDiagnostics = scan.Diagnostics;
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
            _scanDiagnostics = new[]
            {
                ExternalContent.ModsRootUnavailable(ModsGodotPath, exception.Message)
            };
        }

        return packages;
    }

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _scanDiagnostics;

    public ContentFileDiscovery ListVoxelFiles(ContentPackage package) =>
        ListDataFiles(package, ".voxels.json");

    public ContentFileDiscovery ListSettlementFiles(ContentPackage package) =>
        ListDataFiles(package, ".settlements.json");

    private ContentFileDiscovery ListDataFiles(ContentPackage package, string fileSuffix)
    {
        if (package.Kind == ContentPackageKind.Mod || IsPhysicalBuiltIn(package))
            return ExternalContent.ListDataFiles(package.ResourcesRoot, fileSuffix);

        if (IsLinkedOrUnresolved(package.ResourcesRoot))
            return new ContentFileDiscovery(
                Array.Empty<string>(),
                new[] { ContentPackageWalk.LinkedPathDiagnostic(package.ResourcesRoot) },
                invalid: true);

        return ContentPackageWalk.Discover(package.ResourcesRoot, ListEntries, fileSuffix);
    }

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (package.Kind == ContentPackageKind.Mod || IsPhysicalBuiltIn(package))
            return ExternalContent.ReadFile(package.ResourcesRoot, normalizedRelativePath, maxBytes);

        if (!ContentPath.TryNormalize(normalizedRelativePath, out var normalized))
            return ContentBytesResult.Missing();

        var virtualPath = package.ResourcesRoot.TrimEnd('/') + "/" + normalized;
        if (HasUnsafeLink(package.ResourcesRoot, virtualPath))
            return ContentBytesResult.Linked();
        if (!Godot.FileAccess.FileExists(virtualPath))
            return ContentBytesResult.Missing();

        using var file = Godot.FileAccess.Open(virtualPath, Godot.FileAccess.ModeFlags.Read);
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

    // Godot 4.7 C# binds is_link(path) as an instance method on an opened parent directory.
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

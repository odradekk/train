using Godot;
using Train.Domain.Goods;

namespace Train.Content;

public sealed class GodotContentSource : IContentSource
{
    // Keep assets off Content/ so Windows can host C# sources and built-in files side by side.
    public const string BuiltInResourcesRoot = "res://builtin/core/resources";

    public IReadOnlyList<ContentPackage> ListPackages()
    {
        using var probe = DirAccess.Open(BuiltInResourcesRoot);
        var available = probe is not null;
        return new[] { new ContentPackage(GoodId.BuiltInNamespace, BuiltInResourcesRoot, available) };
    }

    public ContentFileDiscovery ListGoodsFiles(ContentPackage package)
    {
        if (IsLinkedOrUnresolved(package.ResourcesRoot))
            return new ContentFileDiscovery(
                Array.Empty<string>(),
                new[] { ContentPackageWalk.LinkedPathDiagnostic(package.ResourcesRoot) },
                invalid: true);

        return ContentPackageWalk.Discover(package.ResourcesRoot, ListEntries);
    }

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
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

        var image = new Image();
        if (image.LoadPngFromBuffer(read.Bytes) != Error.Ok)
            return ImageDecodeResult.Invalid();
        if (image.GetWidth() > ContentLimits.ImageMaxDimension || image.GetHeight() > ContentLimits.ImageMaxDimension)
            return ImageDecodeResult.LimitExceeded();

        return ImageDecodeResult.Ok(new DecodedImage(
            image.GetWidth(),
            image.GetHeight(),
            NormalizeFormat(image.GetFormat()),
            image.GetData()));
    }

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

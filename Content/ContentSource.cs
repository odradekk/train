namespace Train.Content;

public sealed class ContentBytesResult
{
    public bool TooLarge { get; }
    public bool UnsafeLink { get; }
    public byte[]? Bytes { get; }

    private ContentBytesResult(bool tooLarge, bool unsafeLink, byte[]? bytes)
    {
        TooLarge = tooLarge;
        UnsafeLink = unsafeLink;
        Bytes = bytes;
    }

    public static ContentBytesResult Ok(byte[] bytes) => new(false, false, bytes);
    public static ContentBytesResult Missing() => new(false, false, null);
    public static ContentBytesResult LimitExceeded() => new(true, false, null);
    public static ContentBytesResult Linked() => new(false, true, null);
}

public sealed class ImageDecodeResult
{
    public DecodedImage? Image { get; }
    public bool TooLarge { get; }
    public bool Failed { get; }
    public bool UnsafeLink { get; }

    private ImageDecodeResult(DecodedImage? image, bool tooLarge, bool failed, bool unsafeLink)
    {
        Image = image;
        TooLarge = tooLarge;
        Failed = failed;
        UnsafeLink = unsafeLink;
    }

    public static ImageDecodeResult Ok(DecodedImage image) => new(image, false, false, false);
    public static ImageDecodeResult Missing() => new(null, false, true, false);
    public static ImageDecodeResult LimitExceeded() => new(null, true, true, false);
    public static ImageDecodeResult Invalid() => new(null, false, true, false);
    public static ImageDecodeResult Linked() => new(null, false, true, true);
}

public interface IContentSource
{
    IReadOnlyList<ContentPackage> ListPackages();
    IReadOnlyList<ContentDiagnostic> ListScanDiagnostics();
    ContentFileDiscovery ListGoodsFiles(ContentPackage package);
    ContentFileDiscovery ListSettlementFiles(ContentPackage package);
    ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes);
    ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath);
}

public sealed class MemoryContentSource : IContentSource
{
    private readonly List<ContentPackage> _packages = new();
    private readonly List<ContentDiagnostic> _scanDiagnostics = new();
    private readonly Dictionary<string, List<string>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageDecodeResult> _images = new(StringComparer.Ordinal);

    public void AddPackage(
        string @namespace,
        string resourcesRoot = "memory://resources",
        bool available = true,
        ContentPackageKind kind = ContentPackageKind.BuiltIn,
        string? source = null,
        ModPackageMetadata? mod = null)
    {
        var package = new ContentPackage(@namespace, resourcesRoot, available, kind, source, mod);
        if (kind == ContentPackageKind.BuiltIn)
            _packages.RemoveAll(item => item.Kind == ContentPackageKind.BuiltIn && item.Namespace == package.Namespace);
        else
            _packages.RemoveAll(item => item.ResourcesRoot == package.ResourcesRoot);
        _packages.Add(package);
        _files.TryAdd(PackageKey(package), new List<string>());
    }

    public void AddMod(string folderName, string uuid, bool available = true, ModManifest? manifest = null)
    {
        if (manifest is null)
        {
            if (!ModManifest.TryParse(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"schema_version\":1,\"id\":\"{uuid}\",\"name\":\"Test Mod\",\"version\":\"1.0.0\",\"authors\":[\"Test\"]}}"),
                    folderName + "/" + ModDiscovery.ManifestFileName,
                    out var parsed,
                    out _) ||
                parsed is null)
            {
                throw new ArgumentException("A valid mod manifest is required.", nameof(uuid));
            }

            manifest = parsed;
        }

        AddPackage(
            uuid,
            "memory://mods/" + folderName + "/resources",
            available,
            ContentPackageKind.Mod,
            folderName + "/" + ModDiscovery.ManifestFileName,
            new ModPackageMetadata(manifest ?? throw new ArgumentException("A valid mod manifest is required.", nameof(manifest))));
    }

    public void AddScanDiagnostic(ContentDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _scanDiagnostics.Add(diagnostic);
    }

    public void AddGoodsFile(string @namespace, string relativePath, string utf8Text, string? resourcesRoot = null)
    {
        AddGoodsFile(@namespace, relativePath, System.Text.Encoding.UTF8.GetBytes(utf8Text), resourcesRoot);
    }

    public void AddGoodsFile(string @namespace, string relativePath, byte[] bytes, string? resourcesRoot = null)
    {
        AddDataFile(@namespace, relativePath, bytes, ".goods.json", "Goods files must use a safe relative *.goods.json path.", resourcesRoot);
    }

    public void AddSettlementsFile(string @namespace, string relativePath, string utf8Text, string? resourcesRoot = null)
    {
        AddSettlementsFile(@namespace, relativePath, System.Text.Encoding.UTF8.GetBytes(utf8Text), resourcesRoot);
    }

    public void AddSettlementsFile(string @namespace, string relativePath, byte[] bytes, string? resourcesRoot = null)
    {
        AddDataFile(@namespace, relativePath, bytes, ".settlements.json", "Settlement files must use a safe relative *.settlements.json path.", resourcesRoot);
    }

    private void AddDataFile(
        string @namespace,
        string relativePath,
        byte[] bytes,
        string suffix,
        string error,
        string? resourcesRoot)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized) || !normalized.EndsWith(suffix, StringComparison.Ordinal))
            throw new ArgumentException(error, nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        var files = _files[PackageKey(package)];
        if (!files.Contains(normalized, StringComparer.Ordinal))
            files.Add(normalized);
        _bytes[Key(package, normalized)] = bytes;
    }

    public void AddImage(string @namespace, string relativePath, DecodedImage image, byte[]? rawBytes = null, string? resourcesRoot = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized) || !ContentPath.IsPngPath(normalized))
            throw new ArgumentException("Images must use a safe relative PNG path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        _images[Key(package, normalized)] = ImageDecodeResult.Ok(image);
        _bytes[Key(package, normalized)] = rawBytes ?? Array.Empty<byte>();
    }

    public void AddMissingImage(string @namespace, string relativePath, string? resourcesRoot = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized))
            throw new ArgumentException("Images must use a safe relative path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        _images[Key(package, normalized)] = ImageDecodeResult.Missing();
    }

    public void AddOversizedImageBytes(string @namespace, string relativePath, string? resourcesRoot = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized))
            throw new ArgumentException("Images must use a safe relative path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        _images[Key(package, normalized)] = ImageDecodeResult.LimitExceeded();
        _bytes[Key(package, normalized)] = new byte[ContentLimits.PngMaxBytes + 1];
    }

    public IReadOnlyList<ContentPackage> ListPackages() => _packages.ToArray();

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _scanDiagnostics.ToArray();

    public ContentFileDiscovery ListGoodsFiles(ContentPackage package) =>
        ListDataFiles(package, ".goods.json");

    public ContentFileDiscovery ListSettlementFiles(ContentPackage package) =>
        ListDataFiles(package, ".settlements.json");

    private ContentFileDiscovery ListDataFiles(ContentPackage package, string suffix)
    {
        var files = _files.TryGetValue(PackageKey(package), out var listed)
            ? listed.Where(file => file.EndsWith(suffix, StringComparison.Ordinal)).ToArray()
            : Array.Empty<string>();
        return ContentFileDiscovery.Of(files);
    }

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (!ContentPath.IsAllowedDataFile(normalizedRelativePath))
            return ContentBytesResult.Missing();
        if (!_bytes.TryGetValue(Key(package, normalizedRelativePath), out var bytes))
            return ContentBytesResult.Missing();
        if (bytes.Length > maxBytes)
            return ContentBytesResult.LimitExceeded();
        return ContentBytesResult.Ok(bytes);
    }

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath)
    {
        if (!ContentPath.IsPngPath(normalizedRelativePath))
            return ImageDecodeResult.Missing();
        if (_bytes.TryGetValue(Key(package, normalizedRelativePath), out var raw) &&
            raw.Length > ContentLimits.PngMaxBytes)
            return ImageDecodeResult.LimitExceeded();
        return _images.TryGetValue(Key(package, normalizedRelativePath), out var image)
            ? image
            : ImageDecodeResult.Missing();
    }

    private ContentPackage EnsurePackage(string @namespace, string? resourcesRoot)
    {
        var matches = _packages.Where(item => item.Namespace == @namespace);
        if (resourcesRoot is not null)
            matches = matches.Where(item => item.ResourcesRoot == resourcesRoot);
        var package = matches.FirstOrDefault();
        if (package is not null)
            return package;
        AddPackage(@namespace, resourcesRoot ?? "memory://resources");
        return _packages.First(item => item.Namespace == @namespace);
    }

    private static string PackageKey(ContentPackage package) => package.ResourcesRoot + "\n" + package.Namespace;

    private static string Key(ContentPackage package, string relativePath) =>
        PackageKey(package) + "\n" + relativePath;
}

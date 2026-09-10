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
    ContentFileDiscovery ListVoxelFiles(ContentPackage package);
    ContentFileDiscovery ListSettlementFiles(ContentPackage package);
    ContentFileDiscovery ListCompositeFiles(ContentPackage package) => ContentFileDiscovery.Of(Array.Empty<string>());
    ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes);
    ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath);
    ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes);
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

    public void AddVoxelFile(string @namespace, string relativePath, string utf8Text, string? resourcesRoot = null)
    {
        AddVoxelFile(@namespace, relativePath, System.Text.Encoding.UTF8.GetBytes(utf8Text), resourcesRoot);
    }

    public void AddVoxelFile(string @namespace, string relativePath, byte[] bytes, string? resourcesRoot = null)
    {
        AddDataFile(@namespace, relativePath, bytes, ".voxels.json", "Voxel files must use a safe relative *.voxels.json path.", resourcesRoot);
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
        _bytes[Key(package, normalized)] = rawBytes ?? (image.Pixels as byte[] ?? image.Pixels.ToArray());
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

    public ContentFileDiscovery ListVoxelFiles(ContentPackage package) =>
        ListDataFiles(package, ".voxels.json");

    public ContentFileDiscovery ListSettlementFiles(ContentPackage package) =>
        ListDataFiles(package, ".settlements.json");

    public ContentFileDiscovery ListCompositeFiles(ContentPackage package)
    {
        var discovery = ListDataFiles(package, ".composites.json");
        if (package.Kind != ContentPackageKind.PlayerBlueprint)
            return discovery;
        var expected = package.Namespace + ".composites.json";
        return new ContentFileDiscovery(
            discovery.Files.Where(file => string.Equals(file, expected, StringComparison.Ordinal)).ToArray(),
            discovery.Diagnostics,
            discovery.Invalid);
    }

    public void AddCompositeFile(string @namespace, string relativePath, string utf8Text, string? resourcesRoot = null) =>
        AddCompositeFile(@namespace, relativePath, System.Text.Encoding.UTF8.GetBytes(utf8Text), resourcesRoot);

    public void AddCompositeFile(string @namespace, string relativePath, byte[] bytes, string? resourcesRoot = null) =>
        AddDataFile(@namespace, relativePath, bytes, ".composites.json", "Composite files must use a safe relative *.composites.json path.", resourcesRoot);

    public void AddBlueprint(string uuid, string utf8Text)
    {
        var file = uuid + ".composites.json";
        AddPackage(uuid, "memory://blueprints/" + uuid, kind: ContentPackageKind.PlayerBlueprint, source: file);
        AddCompositeFile(uuid, file, utf8Text, "memory://blueprints/" + uuid);
    }

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
        if (_bytes.TryGetValue(Key(package, normalizedRelativePath), out var stored) &&
            !stored.AsSpan().SequenceEqual(rawBytes))
            return ImageDecodeResult.Invalid();
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

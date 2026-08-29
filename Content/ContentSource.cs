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
    ContentFileDiscovery ListGoodsFiles(ContentPackage package);
    ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes);
    ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath);
}

public sealed class MemoryContentSource : IContentSource
{
    private readonly List<ContentPackage> _packages = new();
    private readonly Dictionary<string, List<string>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageDecodeResult> _images = new(StringComparer.Ordinal);

    public void AddPackage(string @namespace, string resourcesRoot = "memory://resources", bool available = true)
    {
        var package = new ContentPackage(@namespace, resourcesRoot, available);
        _packages.RemoveAll(item => item.Namespace == package.Namespace);
        _packages.Add(package);
        _files.TryAdd(package.Namespace, new List<string>());
    }

    public void AddGoodsFile(string @namespace, string relativePath, string utf8Text)
    {
        AddGoodsFile(@namespace, relativePath, System.Text.Encoding.UTF8.GetBytes(utf8Text));
    }

    public void AddGoodsFile(string @namespace, string relativePath, byte[] bytes)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized) || !normalized.EndsWith(".goods.json", StringComparison.Ordinal))
            throw new ArgumentException("Goods files must use a safe relative *.goods.json path.", nameof(relativePath));
        EnsurePackage(@namespace);
        var files = _files[@namespace];
        if (!files.Contains(normalized, StringComparer.Ordinal))
            files.Add(normalized);
        _bytes[Key(@namespace, normalized)] = bytes;
    }

    public void AddImage(string @namespace, string relativePath, DecodedImage image, byte[]? rawBytes = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized) || !ContentPath.IsPngPath(normalized))
            throw new ArgumentException("Images must use a safe relative PNG path.", nameof(relativePath));
        EnsurePackage(@namespace);
        _images[Key(@namespace, normalized)] = ImageDecodeResult.Ok(image);
        _bytes[Key(@namespace, normalized)] = rawBytes ?? Array.Empty<byte>();
    }

    public void AddMissingImage(string @namespace, string relativePath)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized))
            throw new ArgumentException("Images must use a safe relative path.", nameof(relativePath));
        EnsurePackage(@namespace);
        _images[Key(@namespace, normalized)] = ImageDecodeResult.Missing();
    }

    public void AddOversizedImageBytes(string @namespace, string relativePath)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized))
            throw new ArgumentException("Images must use a safe relative path.", nameof(relativePath));
        EnsurePackage(@namespace);
        _images[Key(@namespace, normalized)] = ImageDecodeResult.LimitExceeded();
        _bytes[Key(@namespace, normalized)] = new byte[ContentLimits.PngMaxBytes + 1];
    }

    public IReadOnlyList<ContentPackage> ListPackages() => _packages.ToArray();

    public ContentFileDiscovery ListGoodsFiles(ContentPackage package) =>
        ContentFileDiscovery.Of(
            _files.TryGetValue(package.Namespace, out var files) ? files.ToArray() : Array.Empty<string>());

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (!_bytes.TryGetValue(Key(package.Namespace, normalizedRelativePath), out var bytes))
            return ContentBytesResult.Missing();
        if (bytes.Length > maxBytes)
            return ContentBytesResult.LimitExceeded();
        return ContentBytesResult.Ok(bytes);
    }

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath)
    {
        if (_bytes.TryGetValue(Key(package.Namespace, normalizedRelativePath), out var raw) &&
            raw.Length > ContentLimits.PngMaxBytes)
            return ImageDecodeResult.LimitExceeded();
        return _images.TryGetValue(Key(package.Namespace, normalizedRelativePath), out var image)
            ? image
            : ImageDecodeResult.Missing();
    }

    private void EnsurePackage(string @namespace)
    {
        if (_packages.All(item => item.Namespace != @namespace))
            AddPackage(@namespace);
    }

    private static string Key(string @namespace, string relativePath) => @namespace + "\n" + relativePath;
}

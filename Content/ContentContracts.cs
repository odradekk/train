using System.Collections.ObjectModel;
using Train.Domain.Voxels;

namespace Train.Content;

public static class ContentLimits
{
    public const int JsonFileMaxBytes = 1 * 1024 * 1024;
    public const int PngMaxBytes = 4 * 1024 * 1024;
    public const int ImageMaxDimension = 4096;
    public const int MaxVoxels = 10_000;
    public const int MaxVoxelsPerMod = 1000;
    public const int MaxDiagnosticsPerPackage = 100;
}

public static class ContentDiagnosticCodes
{
    public const string InvalidJson = "invalid_json";
    public const string DuplicateProperty = "duplicate_property";
    public const string UnknownField = "unknown_field";
    public const string SchemaVersionUnsupported = "schema_version_unsupported";
    public const string LocalIdInvalid = "local_id_invalid";
    public const string NameInvalid = "name_invalid";
    public const string DescriptionInvalid = "description_invalid";
    public const string ValueInvalid = "value_invalid";
    public const string MassInvalid = "mass_invalid";
    public const string MaxDurabilityInvalid = "max_durability_invalid";
    public const string TagInvalid = "tag_invalid";
    public const string BehaviorUnregistered = "behavior_unregistered";
    public const string OrientationInvalid = "orientation_invalid";
    public const string CollisionInvalid = "collision_invalid";
    public const string PresentationInvalid = "presentation_invalid";
    public const string PresentationRequired = "presentation_required";
    public const string CategoryUnsupported = "category_unsupported";
    public const string PathInvalid = "path_invalid";
    public const string ImageInvalid = "image_invalid";
    public const string DuplicateLocalId = "duplicate_local_id";
    public const string FileTooLarge = "file_too_large";
    public const string ImageTooLarge = "image_too_large";
    public const string GlobalVoxelLimitExceeded = "global_voxel_limit_exceeded";
    public const string DiagnosticsTruncated = "diagnostics_truncated";
    public const string PackageInvalid = "package_invalid";
    public const string ReloadAlreadyInProgress = "reload_already_in_progress";
    public const string UnresolvedVoxelReference = "unresolved_voxel_reference";
    public const string NaturalResourceTagRequired = "natural_resource_tag_required";
    public const string BuiltInPackageUnavailable = "built_in_package_unavailable";
    public const string EmptyInitialCatalog = "empty_initial_catalog";
    public const string UnsafeLinkedPath = "unsafe_linked_path";
    public const string UuidInvalid = "uuid_invalid";
    public const string VersionInvalid = "version_invalid";
    public const string AuthorsInvalid = "authors_invalid";
    public const string DuplicateModUuid = "duplicate_mod_uuid";
    public const string ModsRootUnavailable = "mods_root_unavailable";
    public const string ModVoxelLimitExceeded = "mod_voxel_limit_exceeded";
    public const string ModManifestMissing = "mod_manifest_missing";
    public const string PopulationInvalid = "population_invalid";
    public const string PositionInvalid = "position_invalid";
    public const string DuplicatePosition = "duplicate_position";
    public const string DuplicateNaturalResource = "duplicate_natural_resource";
    public const string EmptyInitialSettlements = "empty_initial_settlements";
}

public enum ContentDiagnosticSeverity
{
    Warning = 0,
    Error = 1
}

public sealed class ContentDiagnostic
{
    public ContentDiagnosticSeverity Severity { get; }
    public string Code { get; }
    public string Message { get; }
    public string? Source { get; }
    public string? JsonPath { get; }

    public ContentDiagnostic(
        ContentDiagnosticSeverity severity,
        string code,
        string message,
        string? source = null,
        string? jsonPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        Severity = severity;
        Code = code;
        Message = message;
        Source = source;
        JsonPath = jsonPath;
    }
}

public sealed class ContentOrigin
{
    public string Namespace { get; }
    public string SourceFile { get; }

    public ContentOrigin(string @namespace, string sourceFile)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        SourceFile = sourceFile;
    }
}

public sealed class DecodedImage
{
    public int Width { get; }
    public int Height { get; }
    public string Format { get; }
    public IReadOnlyList<byte> Pixels { get; }

    public DecodedImage(int width, int height, string format, byte[] pixels)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(pixels);
        Width = width;
        Height = height;
        Format = format;
        Pixels = new ReadOnlyCollection<byte>(pixels);
    }
}

public enum ContentPackageKind
{
    BuiltIn = 0,
    Mod = 1
}

public sealed class ModPackageMetadata
{
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public IReadOnlyList<string> Authors { get; }
    public string Description { get; }

    public ModPackageMetadata(ModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Id = manifest.Id;
        Name = manifest.Name;
        Version = manifest.Version;
        Authors = new ReadOnlyCollection<string>(manifest.Authors.ToArray());
        Description = manifest.Description;
    }
}

public sealed class ContentPackage
{
    public string Namespace { get; }
    public string ResourcesRoot { get; }
    public bool Available { get; }
    public ContentPackageKind Kind { get; }
    public string? Source { get; }
    public ModPackageMetadata? Mod { get; }

    public ContentPackage(
        string @namespace,
        string resourcesRoot,
        bool available = true,
        ContentPackageKind kind = ContentPackageKind.BuiltIn,
        string? source = null,
        ModPackageMetadata? mod = null)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        ArgumentException.ThrowIfNullOrEmpty(resourcesRoot);
        ResourcesRoot = resourcesRoot;
        Available = available;
        Kind = kind;
        Source = source;
        if (kind == ContentPackageKind.Mod)
        {
            ArgumentNullException.ThrowIfNull(mod);
            if (!string.Equals(mod.Id, Namespace, StringComparison.Ordinal))
                throw new ArgumentException("Mod metadata UUID must match the package namespace.", nameof(mod));
            Mod = mod;
        }
        else if (mod is not null)
        {
            throw new ArgumentException("Built-in packages must not carry mod metadata.", nameof(mod));
        }
    }

    public bool IsRequired => Kind == ContentPackageKind.BuiltIn;
}

public static class ContentLog
{
    public const string Category = "content";
    public const string ScanStartedEventId = "content_scan_started";
    public const string DefinitionAcceptedEventId = "content_definition_accepted";
    public const string FileScannedEventId = "content_file_scanned";
    public const string PackageEmptyEventId = "content_package_empty";
    public const string InvalidEventId = "content_invalid";
    public const string ReloadSummaryEventId = "content_reload_summary";
    public const string ReloadFailedEventId = "content_reload_failed";
    public const string NotifyFailedEventId = "content_notify_failed";
    public const string ModsRootEventId = "content_mods_root";
    public const string ModsScanSummaryEventId = "content_mods_scan_summary";
}

public static class ContentPath
{
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(path))
            return false;
        if (path.Contains('\0', StringComparison.Ordinal))
            return false;
        if (path.Contains('\\', StringComparison.Ordinal))
            return false;
        if (path.StartsWith('/') || HasUriSchemePrefix(path))
            return false;
        if (path.Length >= 2 && path[1] == ':')
            return false;

        var segments = path.Split('/');
        if (segments.Length == 0)
            return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    public static bool IsPngPath(string normalizedPath) =>
        normalizedPath.EndsWith(".png", StringComparison.Ordinal);

    public static bool IsAllowedDataFile(string normalizedPath) =>
        normalizedPath.EndsWith(".json", StringComparison.Ordinal) ||
        IsPngPath(normalizedPath);

    private static bool HasUriSchemePrefix(string path)
    {
        if (path.Length == 0 || !IsAsciiLetter(path[0]))
            return false;
        var colon = path.IndexOf(':');
        if (colon <= 0)
            return false;
        for (var i = 1; i < colon; i++)
        {
            var c = path[i];
            if (IsAsciiLetter(c) || char.IsAsciiDigit(c) || c is '+' or '-' or '.')
                continue;
            return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    public static bool IsUnderRoot(string root, string candidate)
    {
        var prefix = root.TrimEnd('/') + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ||
               string.Equals(candidate.TrimEnd('/'), root.TrimEnd('/'), StringComparison.Ordinal);
    }
}

internal sealed class DiagnosticBag
{
    private readonly List<ContentDiagnostic> _items = new();

    public string? Source { get; set; }
    public bool Invalid { get; private set; }
    public IReadOnlyList<ContentDiagnostic> Items => _items;

    public void Add(
        ContentDiagnosticSeverity severity,
        string code,
        string message,
        string? jsonPath = null,
        bool invalidate = true)
    {
        if (invalidate)
            Invalid = true;
        if (_items.Count >= ContentLimits.MaxDiagnosticsPerPackage)
            return;
        _items.Add(new ContentDiagnostic(severity, code, message, Source, jsonPath));
    }

    public void MarkInvalid() => Invalid = true;
}

internal static class DiagnosticBagExtensions
{
    public static void Warn(this DiagnosticBag bag, string code, string message, string? jsonPath = null) =>
        bag.Add(ContentDiagnosticSeverity.Warning, code, message, jsonPath);

    public static void WarnSoft(this DiagnosticBag bag, string code, string message, string? jsonPath = null) =>
        bag.Add(ContentDiagnosticSeverity.Warning, code, message, jsonPath, invalidate: false);

    public static void Error(this DiagnosticBag bag, string code, string message, string? jsonPath = null) =>
        bag.Add(ContentDiagnosticSeverity.Error, code, message, jsonPath);
}

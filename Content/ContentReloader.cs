using System.Collections.Frozen;
using Train.Domain.Goods;
using Train.Logging;

namespace Train.Content;

public enum ContentReloadStatus
{
    Committed = 0,
    CommittedWithWarnings = 1,
    Rejected = 2
}

public sealed class ContentReloadResult
{
    public ContentReloadStatus Status { get; }
    public bool Changed { get; }
    public long CatalogVersion { get; }
    public int ScannedFileCount { get; }
    public int AcceptedDefinitionCount { get; }
    public int WarningCount { get; }
    public int ErrorCount { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    public IReadOnlySet<GoodId> Added { get; }
    public IReadOnlySet<GoodId> Removed { get; }
    public IReadOnlySet<GoodId> ChangedIds { get; }

    public ContentReloadResult(
        ContentReloadStatus status,
        bool changed,
        long catalogVersion,
        int scannedFileCount,
        int acceptedDefinitionCount,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        IReadOnlySet<GoodId> added,
        IReadOnlySet<GoodId> removed,
        IReadOnlySet<GoodId> changedIds)
    {
        Status = status;
        Changed = changed;
        CatalogVersion = catalogVersion;
        ScannedFileCount = scannedFileCount;
        AcceptedDefinitionCount = acceptedDefinitionCount;
        Diagnostics = ImmutableSets.Freeze(diagnostics);
        WarningCount = Diagnostics.Count(item => item.Severity == ContentDiagnosticSeverity.Warning);
        ErrorCount = Diagnostics.Count(item => item.Severity == ContentDiagnosticSeverity.Error);
        Added = added;
        Removed = removed;
        ChangedIds = changedIds;
    }

    public bool IsCommitted => Status is ContentReloadStatus.Committed or ContentReloadStatus.CommittedWithWarnings;
}

public sealed class ContentPreCommitDecision
{
    public bool CanCommit { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    private ContentPreCommitDecision(bool canCommit, IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        CanCommit = canCommit;
        Diagnostics = ImmutableSets.Freeze(diagnostics);
    }

    public static ContentPreCommitDecision Allow(IEnumerable<ContentDiagnostic>? warnings = null) =>
        new(true, warnings?.ToArray() ?? Array.Empty<ContentDiagnostic>());

    public static ContentPreCommitDecision Reject(IEnumerable<ContentDiagnostic> diagnostics) =>
        new(false, diagnostics.ToArray());
}

public interface IContentPreCommitValidator
{
    ContentPreCommitDecision Validate(GoodCatalogSnapshot candidate);
}

public sealed class ReferencedGoodIdValidator : IContentPreCommitValidator
{
    private readonly Func<IReadOnlySet<GoodId>> _referencedIds;

    public ReferencedGoodIdValidator(Func<IReadOnlySet<GoodId>> referencedIds)
    {
        ArgumentNullException.ThrowIfNull(referencedIds);
        _referencedIds = referencedIds;
    }

    public ContentPreCommitDecision Validate(GoodCatalogSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var diagnostics = new List<ContentDiagnostic>();
        foreach (var id in _referencedIds())
        {
            if (!candidate.TryGet(id, out var entry))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnresolvedGoodReference,
                    "A referenced good is missing from the candidate catalog.",
                    jsonPath: id.Value));
                continue;
            }

            if (entry.Definition.Category != GoodCategory.NaturalResource)
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.NaturalResourceCategoryRequired,
                    "A settlement natural resource id does not resolve to a natural_resource good.",
                    jsonPath: id.Value));
            }
        }

        return diagnostics.Count == 0
            ? ContentPreCommitDecision.Allow()
            : ContentPreCommitDecision.Reject(diagnostics);
    }
}

public sealed class ContentReloader
{
    private readonly IContentSource _source;
    private readonly TrainLogger _logger;
    private readonly IContentPreCommitValidator? _validator;
    private int _busy;

    public GoodCatalog Catalog { get; }
    public bool InitializationFailed { get; private set; }

    public ContentReloader(
        IContentSource source,
        TrainLogger logger,
        GoodCatalog? catalog = null,
        IContentPreCommitValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
        _validator = validator;
        Catalog = catalog ?? new GoodCatalog();
    }

    public ContentReloadResult Reload()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            var busy = Reject(
                Catalog.Current?.Version ?? 0,
                scanned: 0,
                accepted: 0,
                new[]
                {
                    new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.ReloadAlreadyInProgress,
                        "A content reload is already in progress.")
                });
            LogFailure(busy);
            return busy;
        }

        try
        {
            return ReloadExclusive();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private ContentReloadResult ReloadExclusive()
    {
        _logger.Info(ContentLog.Category, ContentLog.ScanStartedEventId, "Scanning built-in goods content.");
        var candidate = CatalogCandidateBuilder.Build(_source, _logger);
        var diagnostics = candidate.Diagnostics.ToList();

        if (!candidate.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Version ?? 0, candidate.ScannedFileCount, 0, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        if (Catalog.Current is null && candidate.Entries.Count == 0)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.EmptyInitialCatalog,
                "The initial built-in catalog must contain at least one good definition."));
            var rejected = Reject(0, candidate.ScannedFileCount, 0, diagnostics);
            InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var nextVersion = (Catalog.Current?.Version ?? 0) + 1;
        var snapshot = new GoodCatalogSnapshot(nextVersion, candidate.Entries);
        var decision = _validator?.Validate(snapshot) ?? ContentPreCommitDecision.Allow();
        diagnostics.AddRange(decision.Diagnostics);

        if (!decision.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Version ?? 0, candidate.ScannedFileCount, candidate.Entries.Count, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var change = CatalogDiff.Compute(snapshot.Version, Catalog.Current, candidate.Entries);
        var status = diagnostics.Any(item => item.Severity == ContentDiagnosticSeverity.Warning)
            ? ContentReloadStatus.CommittedWithWarnings
            : ContentReloadStatus.Committed;

        if (!CatalogDiff.HasChanges(change))
        {
            var unchanged = new ContentReloadResult(
                status,
                changed: false,
                Catalog.Current?.Version ?? 0,
                candidate.ScannedFileCount,
                candidate.Entries.Count,
                diagnostics,
                ImmutableSets.Empty,
                ImmutableSets.Empty,
                ImmutableSets.Empty);
            InitializationFailed = false;
            LogSummary(unchanged);
            return unchanged;
        }

        Catalog.TryPublish(snapshot, change, notify: true);
        InitializationFailed = false;
        var committed = new ContentReloadResult(
            status,
            changed: true,
            snapshot.Version,
            candidate.ScannedFileCount,
            candidate.Entries.Count,
            diagnostics,
            change.Added,
            change.Removed,
            change.Changed);
        LogSummary(committed);
        return committed;
    }

    private static ContentReloadResult Reject(
        long version,
        int scanned,
        int accepted,
        IReadOnlyList<ContentDiagnostic> diagnostics) =>
        new(
            ContentReloadStatus.Rejected,
            changed: false,
            version,
            scanned,
            accepted,
            diagnostics,
            ImmutableSets.Empty,
            ImmutableSets.Empty,
            ImmutableSets.Empty);

    private void LogSummary(ContentReloadResult result)
    {
        _logger.Info(
            ContentLog.Category,
            ContentLog.ReloadSummaryEventId,
            "Content reload finished.",
            new Dictionary<string, object?>
            {
                ["status"] = result.Status.ToString(),
                ["changed"] = result.Changed,
                ["catalog_version"] = result.CatalogVersion,
                ["scanned_files"] = result.ScannedFileCount,
                ["accepted"] = result.AcceptedDefinitionCount,
                ["warning_count"] = result.WarningCount,
                ["error_count"] = result.ErrorCount,
                ["added"] = result.Added.Select(id => id.Value).ToArray(),
                ["removed"] = result.Removed.Select(id => id.Value).ToArray(),
                ["changed_ids"] = result.ChangedIds.Select(id => id.Value).ToArray()
            });
    }

    private void LogFailure(ContentReloadResult result)
    {
        _logger.Error(
            ContentLog.Category,
            ContentLog.ReloadFailedEventId,
            "Content catalog was not committed.",
            new Dictionary<string, object?>
            {
                ["error_count"] = result.ErrorCount,
                ["warning_count"] = result.WarningCount,
                ["scanned_files"] = result.ScannedFileCount
            });
        LogSummary(result);
    }
}

internal sealed class CatalogCandidate
{
    public IReadOnlyDictionary<GoodId, GoodCatalogEntry> Entries { get; }
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    public int ScannedFileCount { get; }
    public bool CanCommit { get; }
    public bool PackagesInvalid { get; }

    public CatalogCandidate(
        IReadOnlyDictionary<GoodId, GoodCatalogEntry> entries,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        int scannedFileCount,
        bool canCommit,
        bool packagesInvalid)
    {
        Entries = entries;
        Diagnostics = diagnostics;
        ScannedFileCount = scannedFileCount;
        CanCommit = canCommit;
        PackagesInvalid = packagesInvalid;
    }
}

internal static class CatalogCandidateBuilder
{
    public static CatalogCandidate Build(IContentSource source, TrainLogger logger)
    {
        var packages = source.ListPackages();
        var diagnostics = new List<ContentDiagnostic>();
        var accepted = new Dictionary<GoodId, GoodCatalogEntry>();
        var scanned = 0;
        var packagesInvalid = false;

        if (packages.Count == 0)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.BuiltInPackageUnavailable,
                "The built-in content package was not provided."));
            return new CatalogCandidate(accepted, diagnostics, 0, false, true);
        }

        foreach (var package in packages)
        {
            var bag = new DiagnosticBag();
            var packageGoods = BuildPackage(source, package, bag, logger, ref scanned);
            foreach (var diagnostic in bag.Items)
                LogDiagnostic(logger, diagnostic);
            diagnostics.AddRange(bag.Items);
            if (!package.Available || bag.Invalid)
            {
                packagesInvalid = true;
                continue;
            }

            if (packageGoods.Count == 0)
            {
                logger.Debug(
                    ContentLog.Category,
                    ContentLog.PackageEmptyEventId,
                    "Content package contains no goods.",
                    new Dictionary<string, object?> { ["package"] = package.Namespace });
            }

            foreach (var (id, entry) in packageGoods)
                accepted[id] = entry;
        }

        if (accepted.Count > ContentLimits.MaxGoods)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.GlobalGoodLimitExceeded,
                "The catalog exceeds the global good limit."));
            return new CatalogCandidate(accepted, diagnostics, scanned, false, true);
        }

        return new CatalogCandidate(
            accepted.ToFrozenDictionary(),
            diagnostics,
            scanned,
            canCommit: !packagesInvalid,
            packagesInvalid);
    }

    private static Dictionary<GoodId, GoodCatalogEntry> BuildPackage(
        IContentSource source,
        ContentPackage package,
        DiagnosticBag bag,
        TrainLogger logger,
        ref int scanned)
    {
        var accepted = new Dictionary<GoodId, GoodCatalogEntry>();
        if (!package.Available)
        {
            bag.Error(ContentDiagnosticCodes.BuiltInPackageUnavailable, "The built-in content package is unavailable.");
            return accepted;
        }

        var discovery = source.ListGoodsFiles(package);
        scanned += discovery.Files.Count;
        foreach (var diagnostic in discovery.Diagnostics)
            bag.Add(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.JsonPath ?? diagnostic.Source);
        if (discovery.Invalid)
            bag.MarkInvalid();
        var parsed = new List<ParsedGood>();
        foreach (var file in discovery.Files)
        {
            bag.Source = file;
            var read = source.ReadFile(package, file, ContentLimits.GoodsFileMaxBytes);
            if (read.UnsafeLink)
            {
                bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                continue;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.FileTooLarge, "Goods JSON exceeds the 1 MiB limit.");
                continue;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.InvalidJson, "Goods file could not be read.");
                continue;
            }

            parsed.AddRange(GoodsJsonParser.Parse(read.Bytes, package.Namespace, file, bag));
        }

        var duplicates = parsed
            .GroupBy(item => item.LocalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in duplicates)
        {
            duplicateIds.Add(group.Key);
            bag.Source = group.First().SourceFile;
            bag.Warn(ContentDiagnosticCodes.DuplicateLocalId, "Duplicate local id in the package.", group.Key);
        }

        foreach (var item in parsed)
        {
            if (duplicateIds.Contains(item.LocalId))
                continue;
            bag.Source = item.SourceFile;
            if (!TryAccept(source, package, item, bag, logger, out var entry))
                continue;
            accepted[entry.Definition.Id] = entry;
        }

        return accepted;
    }

    private static bool TryAccept(
        IContentSource source,
        ContentPackage package,
        ParsedGood parsed,
        DiagnosticBag bag,
        TrainLogger logger,
        out GoodCatalogEntry entry)
    {
        entry = null!;
        if (!TryLoadImages(source, package, parsed.Visual, bag, out var images))
            return false;

        var id = new GoodId(package.Namespace, parsed.LocalId);
        var definition = new GoodDefinition(
            id,
            parsed.Category,
            parsed.Name,
            parsed.Description,
            parsed.BaseMonetaryValue,
            parsed.Visual);
        var origin = new ContentOrigin(package.Namespace, parsed.SourceFile);
        entry = new GoodCatalogEntry(definition, origin, DefinitionFingerprint.Compute(definition, images));
        logger.Debug(
            ContentLog.Category,
            ContentLog.DefinitionAcceptedEventId,
            "Accepted good definition.",
            new Dictionary<string, object?>
            {
                ["good_id"] = id.Value,
                ["source"] = parsed.SourceFile
            });
        return true;
    }

    private static bool TryLoadImages(
        IContentSource source,
        ContentPackage package,
        VoxelVisualDefinition visual,
        DiagnosticBag bag,
        out Dictionary<string, DecodedImage> images)
    {
        images = new Dictionary<string, DecodedImage>(StringComparer.Ordinal);
        var paths = new[] { visual.IconPath, visual.TopPath, visual.SidePath, visual.BottomPath };
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!TryDecode(source, package, path, bag, out var decoded))
                return false;
            images[path] = decoded;
        }

        if (!IsSquare(images[visual.IconPath]))
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Icon must be square.", visual.IconPath);
            return false;
        }

        var top = images[visual.TopPath];
        var side = images[visual.SidePath];
        var bottom = images[visual.BottomPath];
        if (!IsSquare(top) || !IsSquare(side) || !IsSquare(bottom) ||
            !IsPowerOfTwo(top.Width) || !IsPowerOfTwo(side.Width) || !IsPowerOfTwo(bottom.Width) ||
            top.Width != side.Width || side.Width != bottom.Width)
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Voxel faces must be equal power-of-two squares.", visual.TopPath);
            return false;
        }

        return true;
    }

    private static bool TryDecode(
        IContentSource source,
        ContentPackage package,
        string path,
        DiagnosticBag bag,
        out DecodedImage image)
    {
        image = null!;
        var decoded = source.DecodePng(package, path);
        if (decoded.UnsafeLink)
        {
            bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
            return false;
        }

        if (decoded.TooLarge)
        {
            bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG exceeds the size limit.", path);
            return false;
        }

        if (decoded.Failed || decoded.Image is null)
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "PNG could not be decoded.", path);
            return false;
        }

        if (decoded.Image.Width is < 1 or > ContentLimits.ImageMaxDimension ||
            decoded.Image.Height is < 1 or > ContentLimits.ImageMaxDimension)
        {
            bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "Decoded image exceeds the dimension limit.", path);
            return false;
        }

        image = decoded.Image;
        return true;
    }

    private static bool IsSquare(DecodedImage image) => image.Width == image.Height && image.Width > 0;

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static void LogDiagnostic(TrainLogger logger, ContentDiagnostic diagnostic)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["code"] = diagnostic.Code,
            ["source"] = diagnostic.Source,
            ["json_path"] = diagnostic.JsonPath
        };
        if (diagnostic.Severity == ContentDiagnosticSeverity.Error)
        {
            logger.Error(ContentLog.Category, ContentLog.InvalidEventId, diagnostic.Message, attributes);
            return;
        }

        logger.Warning(ContentLog.Category, ContentLog.InvalidEventId, diagnostic.Message, attributes);
    }
}

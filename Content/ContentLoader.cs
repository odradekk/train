using Train.Content.Composites;
using Train.Content.Markets;
using Train.Content.Settlements;
using Train.Content.Voxels;

namespace Train.Content;

internal sealed class ContentLoader
{
    private readonly IContentSource _source;
    private readonly IContentLogger _logger;
    private readonly VoxelContentFormat _voxels = new();
    private readonly CompositeContentFormat _blueprints = new();
    private readonly MarketTableContentFormat _marketTables = new();
    private readonly SettlementTypeContentFormat _settlementTypes = new();
    private readonly ContentFormat[] _formats;
    private readonly DiagnosticBag _loadDiagnostics = new(ContentDiagnosticSeverity.Error);
    private readonly List<ContentPackageLoad> _packages = new();
    private readonly List<ContentDiagnostic> _scanDiagnostics = new();

    public int ScannedFileCount { get; private set; }
    public int DefinitionCount => _formats.Sum(format => _packages.Sum(format.CountFor));
    public IReadOnlyList<ContentDiagnostic> Diagnostics => _scanDiagnostics.Concat(_loadDiagnostics.Items)
        .Concat(_packages.SelectMany(package => package.Diagnostics.Items)).ToArray();

    public ContentLoader(IContentSource source, IContentLogger logger)
    {
        _source = source;
        _logger = logger;
        _formats = [_voxels, _blueprints, _marketTables, _settlementTypes];
    }

    public bool Load(bool initial)
    {
        DiscoverPackages();
        foreach (var package in _packages)
            ReadPackage(package);
        ResolvePackages();
        if (!_loadDiagnostics.Invalid && !_packages.Any(package => package.Package.IsRequired && package.Diagnostics.Invalid))
            _voxels.CheckGlobalLimits(_loadDiagnostics, initial);
        LogDiagnostics();
        LogModSummary();
        return !_loadDiagnostics.Invalid && !_packages.Any(package => package.Package.IsRequired && package.Diagnostics.Invalid);
    }

    public bool HasChanges(ContentSnapshot? previous) =>
        previous is null || _formats.Any(format => format.HasChanges(previous));

    public ContentSnapshot Freeze(long generation) => new(generation, _voxels.Entries, _blueprints.Entries,
        _marketTables.Entries, _settlementTypes.Entries);

    private void DiscoverPackages()
    {
        _packages.AddRange(_source.ListPackages().Select(package => new ContentPackageLoad(package)));
        _scanDiagnostics.AddRange(_source.ListScanDiagnostics());
        if (!_packages.Any(package => package.Package.IsRequired))
            _loadDiagnostics.Reject(ContentDiagnosticCodes.BuiltInPackageUnavailable, "The built-in content package was not provided.");
        foreach (var duplicate in _packages.Where(package => package.Package.Kind == ContentPackageKind.Mod)
                     .GroupBy(package => package.Package.Namespace, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            foreach (var package in duplicate)
            {
                package.Diagnostics.Source = package.Package.Source ?? package.Package.ResourcesRoot;
                package.Diagnostics.Reject(ContentDiagnosticCodes.DuplicateModUuid,
                    $"Multiple Mod packages declare UUID '{duplicate.Key}'.");
            }
        }
    }

    private void ReadPackage(ContentPackageLoad load)
    {
        var package = load.Package;
        var bag = load.Diagnostics;
        if (bag.Invalid)
            return;
        bag.Source = package.ResourcesRoot;
        if (!package.Available)
        {
            bag.Reject(
                package.IsRequired ? ContentDiagnosticCodes.BuiltInPackageUnavailable : ContentDiagnosticCodes.PackageInvalid,
                "The content package resources are unavailable.");
            return;
        }
        var discovery = _source.ListDefinitionFiles(package);
        foreach (var diagnostic in discovery.Diagnostics)
        {
            bag.Source = diagnostic.Source;
            bag.Add(discovery.Invalid ? bag.RejectionSeverity : diagnostic.Severity,
                diagnostic.Code, diagnostic.Message, diagnostic.JsonPath, invalidate: false);
        }
        if (discovery.Invalid)
            bag.MarkInvalid();
        foreach (var file in discovery.Files)
        {
            if (package.Kind == ContentPackageKind.PlayerBlueprint && file != package.Namespace + _blueprints.FileSuffix)
                continue;
            var format = _formats.FirstOrDefault(format => file.EndsWith(format.FileSuffix, StringComparison.Ordinal));
            if (format is null)
                continue;
            ScannedFileCount++;
            bag.Source = file;
            var bytes = ReadJson(package, file, bag);
            var accepted = bytes is null ? 0 : format.ReadFile(_source, load, file, bytes);
            _logger.Debug(ContentLog.Category, ContentLog.FileScannedEventId, "Content file scanned.",
                new Dictionary<string, object?>
                {
                    ["source"] = file, ["package"] = package.Namespace,
                    ["kind"] = format.LogKind, ["accepted"] = accepted
                });
        }
        if (!bag.Invalid && _formats.Sum(format => format.CountFor(load)) == 0)
            _logger.Debug(ContentLog.Category, ContentLog.PackageEmptyEventId, "Content package contains no definitions.",
                new Dictionary<string, object?> { ["package"] = package.Namespace });
    }

    private byte[]? ReadJson(ContentPackage package, string file, DiagnosticBag bag)
    {
        if (!ContentPath.TryNormalize(file, out var path))
        {
            bag.Reject(ContentDiagnosticCodes.PathInvalid, "A definition file path is unsafe.");
            return null;
        }
        var read = _source.ReadFile(package, path, ContentLimits.JsonFileMaxBytes);
        if (read.UnsafeLink)
            bag.Reject(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked definition files are not allowed.");
        else if (read.TooLarge || read.Bytes?.Length > ContentLimits.JsonFileMaxBytes)
            bag.Reject(ContentDiagnosticCodes.FileTooLarge, "Definition JSON exceeds the 1 MiB limit.");
        else if (read.Bytes is null)
            bag.Reject(ContentDiagnosticCodes.InvalidJson, "Definition JSON could not be read.");
        else
            return read.Bytes;
        return null;
    }

    private void ResolvePackages()
    {
        var invalidCount = -1;
        while (invalidCount != _packages.Count(package => package.Diagnostics.Invalid))
        {
            invalidCount = _packages.Count(package => package.Diagnostics.Invalid);
            foreach (var format in _formats)
                format.Merge();
            // 后合并的格式可能隔离了前一个格式已有条目的包；引用只能看到有效包。
            if (invalidCount != _packages.Count(package => package.Diagnostics.Invalid))
                continue;
            _blueprints.ResolveReferences(_voxels.Entries);
            _marketTables.ResolveReferences(_voxels.Entries);
            _settlementTypes.ResolveReferences(_voxels.Entries);
        }
    }

    private void LogDiagnostics()
    {
        foreach (var diagnostic in Diagnostics)
        {
            var properties = new Dictionary<string, object?>
            {
                ["code"] = diagnostic.Code, ["source"] = diagnostic.Source, ["json_path"] = diagnostic.JsonPath
            };
            if (diagnostic.Severity == ContentDiagnosticSeverity.Error)
                _logger.Error(ContentLog.Category, ContentLog.InvalidEventId, diagnostic.Message, properties);
            else
                _logger.Warning(ContentLog.Category, ContentLog.InvalidEventId, diagnostic.Message, properties);
        }
    }

    private void LogModSummary()
    {
        var mods = _packages.Where(package => package.Package.Kind == ContentPackageKind.Mod).ToArray();
        _logger.Info(ContentLog.Category, ContentLog.ModsScanSummaryEventId, "Mod content scan finished.",
            new Dictionary<string, object?>
            {
                ["candidate_count"] = mods.Length,
                ["accepted_count"] = mods.Count(package => !package.Diagnostics.Invalid),
                ["isolated_count"] = mods.Count(package => package.Diagnostics.Invalid)
            });
    }
}

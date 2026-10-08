// 属于内容加载模块。
// 统一发现和读取文件、隔离整个包、重查级联引用，并准备一次完整发布。
using Train.Core.Content.Composites;
using Train.Core.Content.Markets;
using Train.Core.Content.Settlements;
using Train.Core.Content.Voxels;
using Train.Core.Logging;

namespace Train.Core.Content;

/// <summary>保存一轮加载的格式、包和诊断；候选只在本轮使用，不是可供外部查询的目录。</summary>
/// <remarks>包失败是单调状态；每轮引用检查若发现失败，至少隔离一个包，因包数有限而终止。</remarks>
internal sealed class ContentLoader
{
    private readonly IContentSource _source;
    private readonly TrainLogger _logger;
    private readonly VoxelContentFormat _voxels = new();
    private readonly CompositeContentFormat _blueprints = new();
    private readonly MarketTableContentFormat _marketTables = new();
    private readonly SettlementTypeContentFormat _settlementTypes = new();
    private readonly ContentFormat[] _formats;
    private readonly DiagnosticBag _loadDiagnostics = new();
    private readonly List<ContentPackageLoad> _packages = new();
    private readonly List<ContentDiagnostic> _scanDiagnostics = new();

    public int ScannedFileCount { get; private set; }
    public int DefinitionCount => _formats.Sum(format => _packages.Sum(format.CountFor));
    public IReadOnlyList<ContentDiagnostic> Diagnostics => _scanDiagnostics.Concat(_loadDiagnostics.Items)
        .Concat(_packages.SelectMany(package => package.Diagnostics.Items)).ToArray();

    /// <summary>初始化本轮内容来源、日志与四种领域格式。</summary>
    public ContentLoader(IContentSource source, TrainLogger logger)
    {
        _source = source;
        _logger = logger;
        _formats = [_voxels, _blueprints, _marketTables, _settlementTypes];
    }

    /// <summary>发现、读取和校验全部包，并迭代隔离失效引用；返回候选是否允许发布。</summary>
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

    /// <summary>判断所有格式的有效内容是否发生变化；首次发布一定有变化。</summary>
    public bool HasChanges(ContentSnapshot? previous) =>
        previous is null || _formats.Any(format => format.HasChanges(previous));

    /// <summary>冻结校验完成的候选；这里是各领域索引组成完整快照的唯一位置。</summary>
    public ContentSnapshot Freeze(long generation) => new(generation, _voxels.Entries, _blueprints.Entries,
        _marketTables.Entries, _settlementTypes.Entries);

    /// <summary>读取包发现结果，检查必需包存在性，并使重复 UUID 的全部 Mod 无效。</summary>
    private void DiscoverPackages()
    {
        _packages.AddRange(_source.ListPackages().Select(package => new ContentPackageLoad(package)));
        _scanDiagnostics.AddRange(_source.ListScanDiagnostics());
        if (!_packages.Any(package => package.Package.IsRequired))
            _loadDiagnostics.Error(ContentDiagnosticCodes.BuiltInPackageUnavailable, "The built-in content package was not provided.");
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

    /// <summary>发现一个包的通用 JSON 文件，路由已支持的格式并记录每个实际读取文件的结果。</summary>
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
            // 玩家包共用蓝图根目录，只能读取属于该包 UUID 的根级蓝图。
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

    /// <summary>共用 JSON 路径、链接与字节上限检查；失败保留所在文件的诊断。</summary>
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

    /// <summary>重新合并所有有效包，再查所有格式引用；直到没有新的包失效或必需包失败。</summary>
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

    /// <summary>将发现及领域校验的所有保留诊断写入同一结构化日志类别。</summary>
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

    /// <summary>记录进入加载器的 Mod 包数及最终接受、隔离数；不作为发布结果的公共字段。</summary>
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

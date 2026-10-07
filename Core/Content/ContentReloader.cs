// 属于内容模块。
// 协调候选内容校验、目录发布和发布后的变更通知。

using System.Collections.Frozen;
using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Train.Core.Logging;

namespace Train.Core.Content;

/// <summary>一次内容发布的结果：成功、带警告成功或拒绝。</summary>
/// <remarks>成功也包括内容无变化的重复加载。发布许可由各校验阶段决定，成功状态只按最终诊断中是否存在 Warning 区分，不以 Error 数量重新判断许可。</remarks>
/// <example>隔离无效 Mod 后仍发布有效内容并返回 CommittedWithWarnings；见 <c>ModLoadingTests.Invalid_mod_is_isolated_while_unrelated_valid_packages_commit</c>。</example>
public enum ContentReloadStatus
{
    /// <summary>候选内容成功发布且没有警告。</summary>
    Committed = 0,
    /// <summary>排除无效可选内容或保留其他警告后成功发布。</summary>
    CommittedWithWarnings = 1,
    /// <summary>候选内容被拒绝；保留原目录，首次失败时仍没有有效体素目录。</summary>
    Rejected = 2
}

/// <summary>一次内容扫描和发布的统计、诊断及体素定义变更集合。</summary>
/// <remarks>蓝图或原型变化也会令 Changed 为 true；CatalogVersion 只表示体素目录代数。诊断通常复制为只读集合，输入已是 ReadOnlyCollection 时保留该集合；三个变更集合始终保留输入引用。构造时统计警告和错误，不根据这些数量推导发布状态。</remarks>
/// <example>重复加载相同内容时 IsCommitted 为 true、Changed 为 false，目录代数不变；见 <c>ContentReloaderTests.Unchanged_reload_keeps_version_and_does_not_notify</c>。</example>
public sealed class ContentReloadResult
{
    /// <summary>获取本次发布状态。</summary>
    public ContentReloadStatus Status { get; }
    /// <summary>获取体素、蓝图或原型目录是否发生可观察的发布变化。</summary>
    public bool Changed { get; }
    /// <summary>获取发布后保留的体素目录代数；尚无有效目录时为零。</summary>
    public long CatalogVersion { get; }
    /// <summary>获取已扫描包所列出的体素和蓝图 JSON 文件总数；包含读取或解析失败的文件，不包含图片。</summary>
    public int ScannedFileCount { get; }
    /// <summary>获取本次报告的候选体素定义数；成功时为引用过滤后的数量，拒绝时可能为零或过滤前的数量，不表示已发布数量。</summary>
    public int AcceptedDefinitionCount { get; }
    /// <summary>获取构造结果时诊断中的 Warning 条数。</summary>
    public int WarningCount { get; }
    /// <summary>获取构造结果时诊断中的 Error 条数。</summary>
    public int ErrorCount { get; }
    /// <summary>获取诊断的只读集合，保持扫描与校验的报告顺序；输入已为 ReadOnlyCollection 时保留原集合。</summary>
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    /// <summary>获取本次新增的体素定义标识；重载被拒绝或体素无变化时为空。</summary>
    public IReadOnlySet<VoxelDefinitionId> Added { get; }
    /// <summary>获取本次移除的体素定义标识；重载被拒绝或体素无变化时为空。</summary>
    public IReadOnlySet<VoxelDefinitionId> Removed { get; }
    /// <summary>获取发布指纹变化的已有体素定义标识；包括仅表现资产变化的定义，不包含新增项。</summary>
    public IReadOnlySet<VoxelDefinitionId> ChangedIds { get; }

    /// <summary>保存扫描结果，将诊断转换为只读集合并统计其严重程度。</summary>
    /// <param name="status">候选内容的发布状态。</param>
    /// <param name="changed">是否有体素或蓝图目录变化。</param>
    /// <param name="catalogVersion">本轮结束时保留的体素目录代数，尚无有效目录时为零。</param>
    /// <param name="scannedFileCount">非负的体素与蓝图 JSON 文件数，单位为文件。</param>
    /// <param name="acceptedDefinitionCount">非负的候选体素定义数，单位为定义。</param>
    /// <param name="diagnostics">非 null 且不含 null 条目的诊断列表；允许为空。</param>
    /// <param name="added">新增体素标识集合，允许为空；不复制。</param>
    /// <param name="removed">移除体素标识集合，允许为空；不复制。</param>
    /// <param name="changedIds">已有体素中发布指纹变化的标识集合，允许为空；不复制。</param>
    /// <remarks>标量和变更集合原样保存，不校验范围或相互一致性；调用方须保持保留集合的内容稳定。</remarks>
    /// <exception cref="ArgumentNullException">diagnostics 为 null。</exception>
    /// <exception cref="NullReferenceException">diagnostics 包含 null 条目，无法统计严重程度。</exception>
    public ContentReloadResult(
        ContentReloadStatus status,
        bool changed,
        long catalogVersion,
        int scannedFileCount,
        int acceptedDefinitionCount,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        IReadOnlySet<VoxelDefinitionId> added,
        IReadOnlySet<VoxelDefinitionId> removed,
        IReadOnlySet<VoxelDefinitionId> changedIds)
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

    /// <summary>获取状态是否为 Committed 或 CommittedWithWarnings；内容无变化时也可以为 true。</summary>
    public bool IsCommitted => Status is ContentReloadStatus.Committed or ContentReloadStatus.CommittedWithWarnings;
}

/// <summary>扫描内容包、校验候选引用并发布体素与蓝图目录。</summary>
/// <remarks>尚无体素快照时，先检查引用过滤前的候选体素集合非空。每个实例拒绝并发或重入 Reload；有变化的体素和蓝图目录依次替换，再按各目录的订阅顺序通知。无变化或返回拒绝结果时保留当前快照。</remarks>
/// <example>首次加载 ferrous-ore 和 coal 后生成有效目录；见 <c>ContentReloaderTests.Initial_valid_voxel_catalog_commits</c>。</example>
public sealed class ContentReloader
{
    private readonly IContentSource _source;
    private readonly TrainLogger _logger;
    private int _busy;

    /// <summary>获取对外发布的体素目录。</summary>
    public VoxelCatalog Catalog { get; }
    /// <summary>获取对外发布的蓝图与原型目录。</summary>
    public CompositeCatalog Composites { get; }
    /// <summary>获取初始化失败标记；尚无体素快照时的显式校验拒绝将其置为 true，成功加载后清除。</summary>
    /// <remarks>并发拒绝不更改此标记；它记录已执行的校验或发布步骤，不表示本轮是否抛出异常。</remarks>
    public bool InitializationFailed { get; private set; }

    /// <summary>创建重载器，使用传入目录或创建空目录。</summary>
    /// <param name="source">提供包、文件发现、原始资源字节与图片解码的非 null 内容源。</param>
    /// <param name="logger">记录扫描和发布诊断的日志实例。</param>
    /// <param name="catalog">要更新的体素目录；null 时创建新的空目录。</param>
    /// <param name="composites">要更新的蓝图目录；null 时创建新的空目录。</param>
    /// <remarks>保存依赖和目录引用，不在构造时扫描或发布。传入的目录保留已有快照；本实例的重载互斥不覆盖共享这些目录的其他重载器。</remarks>
    /// <exception cref="ArgumentNullException">source 或 logger 为 null。</exception>
    /// <example>先创建重载器再首次发布 ferrous-ore 和 coal；见 <c>ContentReloaderTests.Initial_valid_voxel_catalog_commits</c>。</example>
    public ContentReloader(
        IContentSource source,
        TrainLogger logger,
        VoxelCatalog? catalog = null,
        CompositeCatalog? composites = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
        Catalog = catalog ?? new VoxelCatalog();
        Composites = composites ?? new CompositeCatalog();
    }

    /// <summary>同步扫描和校验候选内容，依次发布有变化的目录并通知订阅者。</summary>
    /// <returns>成功、带警告成功或拒绝的结果；无变化时成功但 Changed 为 false，返回拒绝结果时保留原目录。</returns>
    /// <remarks>
    /// 候选构建与引用检查依次执行；全部通过后才检查目录差异，无变化时不发布或通知。体素与蓝图各自只在有变化时增加代数，CatalogVersion 不报告蓝图代数。
    /// 两个目录各自原子替换，不保证同时可见；读取方可能在两次写入之间读到新体素快照和旧蓝图快照。
    /// 重入或并发调用立即返回 reload_already_in_progress 拒绝诊断。内容源和日志调用的异常向上传播，取得的重载权在 finally 中释放；发布后订阅者异常交给日志回调，回调正常返回后继续通知，不撤回目录或加入返回诊断。
    /// </remarks>
    /// <example>订阅者抛出异常后仍保留已发布快照，下一轮也能成功加载；见 <c>ContentReloaderTests.Notify_exception_keeps_the_commit_and_allows_a_later_reload</c>。并发拒绝见 <c>ContentReloaderTests.Concurrent_reload_is_rejected_immediately</c>。</example>
    public ContentReloadResult Reload()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            var busy = Reject(
                Catalog.Current?.Generation ?? 0,
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

    /// <summary>在已取得重载权的调用中准备并提交完整候选目录。</summary>
    /// <returns>反映当前保留目录、候选统计和诊断的重载结果。</returns>
    /// <remarks>内容或引用校验拒绝时保留旧目录，尚无体素快照时标记初始化失败。校验通过后准备快照并计算差异；有变化时替换目录，最后通知。异常由 Reload 的 finally 释放重载权，不在此处转换为拒绝结果。</remarks>
    private ContentReloadResult ReloadExclusive()
    {
        _logger.Info(ContentLog.Category, ContentLog.ScanStartedEventId, "Scanning built-in voxel content.");
        var candidate = CatalogCandidateBuilder.Build(_source, _logger);
        var diagnostics = candidate.Diagnostics.ToList();

        if (!candidate.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, 0, diagnostics);
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
                "The initial built-in catalog must contain at least one voxel definition."));
            var rejected = Reject(0, candidate.ScannedFileCount, 0, diagnostics);
            InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var entries = new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>(candidate.Entries);
        var archetypes = new Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry>(candidate.Archetypes);
        var composites = new Dictionary<CompositeDefinitionId, CompositeCatalogEntry>(candidate.Composites);
        var references = CompositeReferenceValidator.Filter(entries, archetypes, composites);
        diagnostics.AddRange(references.Diagnostics);
        if (!references.CanCommit)
        {
            var rejected = Reject(Catalog.Current?.Generation ?? 0, candidate.ScannedFileCount, candidate.Entries.Count, diagnostics);
            if (Catalog.Current is null)
                InitializationFailed = true;
            LogFailure(rejected);
            return rejected;
        }

        var nextGeneration = (Catalog.Current?.Generation ?? 0) + 1;
        var snapshot = new VoxelCatalogSnapshot(nextGeneration, entries);
        var nextCompositeGeneration = Composites.Current.Generation + 1;
        var compositeSnapshot = new CompositeCatalogSnapshot(nextCompositeGeneration, archetypes, composites);

        var change = CatalogDiff.Compute(snapshot.Generation, Catalog.Current, entries);
        var compositeChange = CompositeCatalogDiff.Compute(
            compositeSnapshot.Generation,
            Composites.Current.Generation == 0 ? null : Composites.Current,
            archetypes,
            composites);
        var status = diagnostics.Any(item => item.Severity == ContentDiagnosticSeverity.Warning)
            ? ContentReloadStatus.CommittedWithWarnings
            : ContentReloadStatus.Committed;
        var voxelsChanged = CatalogDiff.HasChanges(change);
        var compositesChanged = compositeChange.HasChanges;

        if (!voxelsChanged && !compositesChanged)
        {
            var unchanged = new ContentReloadResult(
                status,
                changed: false,
                Catalog.Current?.Generation ?? 0,
                candidate.ScannedFileCount,
                entries.Count,
                diagnostics,
                ImmutableSets.Empty,
                ImmutableSets.Empty,
                ImmutableSets.Empty);
            InitializationFailed = false;
            LogSummary(unchanged);
            return unchanged;
        }

        if (voxelsChanged)
            Catalog.Publish(snapshot);
        if (compositesChanged)
            Composites.Publish(compositeSnapshot);
        InitializationFailed = false;
        if (voxelsChanged)
            Catalog.NotifyChanged(change, LogNotifyFailure);
        if (compositesChanged)
            Composites.NotifyChanged(compositeChange, LogNotifyFailure);
        var committed = new ContentReloadResult(
            status,
            changed: true,
            voxelsChanged ? snapshot.Generation : Catalog.Current?.Generation ?? 0,
            candidate.ScannedFileCount,
            entries.Count,
            diagnostics,
            voxelsChanged ? change.Added : ImmutableSets.Empty,
            voxelsChanged ? change.Removed : ImmutableSets.Empty,
            voxelsChanged ? change.Changed : ImmutableSets.Empty);
        LogSummary(committed);
        return committed;
    }

    /// <summary>以原目录代数创建不包含变更的拒绝结果。</summary>
    /// <param name="version">拒绝后仍保留的体素目录代数，无目录时为零。</param>
    /// <param name="scanned">本次已发现的 JSON 文件数。</param>
    /// <param name="accepted">调用拒绝的阶段所报告的候选体素数，不表示已发布。</param>
    /// <param name="diagnostics">需要保留到返回结果的诊断。</param>
    /// <returns>Changed 为 false 且三个体素变化集合均为空的拒绝结果。</returns>
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

    /// <summary>记录发布结果和体素定义变更集合。</summary>
    /// <param name="result">成功、无变化或拒绝的本轮结果；日志中的 failed 是警告与错误条数之和。</param>
    private void LogSummary(ContentReloadResult result)
    {
        _logger.Info(
            ContentLog.Category,
            ContentLog.ReloadSummaryEventId,
            $"Content reload finished: {result.ScannedFileCount} files scanned, {result.AcceptedDefinitionCount} accepted, {result.WarningCount + result.ErrorCount} failed.",
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

    /// <summary>记录拒绝原因数量及本次扫描摘要。</summary>
    /// <param name="result">待记录的拒绝结果；同时输出失败事件和重载摘要。</param>
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

    /// <summary>报告发布后订阅者异常，不撤销已发布目录。</summary>
    /// <param name="exception">订阅者抛出的异常；日志保存其类型全名和说明，不写入重载结果的诊断。</param>
    private void LogNotifyFailure(Exception exception) =>
        _logger.Error(
            ContentLog.Category,
            ContentLog.NotifyFailedEventId,
            "A catalog change subscriber failed after content was committed.",
            new Dictionary<string, object?>
            {
                ["exception"] = exception.GetType().FullName,
                ["message"] = exception.Message
            });
}

/// <summary>一次扫描准备的体素、原型和蓝图候选及其校验状态。</summary>
/// <remarks>构造函数直接保存集合引用，不保证集合冻结。此时尚未进行跨定义引用过滤；构建被拒绝时仍可能保留其他有效包的条目，使用方须先检查 CanCommit。</remarks>
/// <example>无效 Mod 被隔离后仍可保留并发布内置内容；见 <c>CompositeContentTests.Illegal_mod_composites_are_isolated_without_rejecting_built_in</c>。</example>
internal sealed class CatalogCandidate
{
    /// <summary>获取已合并有效包的候选体素条目；字典键为完整定义标识。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> Entries { get; }
    /// <summary>获取已合并有效包的候选原型条目；引用完整性留待后续检查。</summary>
    public IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> Archetypes { get; }
    /// <summary>获取已合并有效包的候选蓝图条目；引用完整性留待后续检查。</summary>
    public IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> Composites { get; }
    /// <summary>获取扫描和包校验诊断的只读接口；直接保留输入集合引用。</summary>
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    /// <summary>获取进入扫描的包所列出的体素和蓝图 JSON 文件总数；包括失败文件，不含图片。</summary>
    public int ScannedFileCount { get; }
    /// <summary>获取包校验和全局体素数量检查是否允许继续；为 true 时仍须进行引用及宿主约束校验。</summary>
    public bool CanCommit { get; }
    /// <summary>获取构建器记录的必需包缺失、无效或全局体素超限标记；可选包被隔离不会单独令其为 true。</summary>
    public bool PackagesInvalid { get; }

    /// <summary>保存候选集合、诊断和构建状态，不复制或校验输入。</summary>
    /// <param name="entries">已合并的候选体素条目。</param>
    /// <param name="archetypes">已合并的候选原型条目。</param>
    /// <param name="composites">已合并的候选蓝图条目。</param>
    /// <param name="diagnostics">按报告顺序保存的扫描和包诊断。</param>
    /// <param name="scannedFileCount">已发现的 JSON 文件数，单位为文件。</param>
    /// <param name="canCommit">当前构建阶段是否允许继续。</param>
    /// <param name="packagesInvalid">构建阶段是否记录必需包问题或全局体素超限。</param>
    public CatalogCandidate(
        IReadOnlyDictionary<VoxelDefinitionId, VoxelCatalogEntry> entries,
        IReadOnlyDictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeCatalogEntry> composites,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        int scannedFileCount,
        bool canCommit,
        bool packagesInvalid)
    {
        Entries = entries;
        Archetypes = archetypes;
        Composites = composites;
        Diagnostics = diagnostics;
        ScannedFileCount = scannedFileCount;
        CanCommit = canCommit;
        PackagesInvalid = packagesInvalid;
    }
}

/// <summary>以内容包为失败隔离边界构造候选定义集合。</summary>
/// <remarks>扫描、格式和资产校验在本阶段完成；可选包整体隔离，必需包失败阻止发布。跨定义引用由重载器在候选构建后检查。</remarks>
/// <example>同一 Mod UUID 的所有包都被禁用，不选择其中一个；见 <c>ModLoadingTests.Duplicate_mod_uuids_are_all_disabled_without_choosing_a_winner</c>。</example>
internal static class CatalogCandidateBuilder
{
    /// <summary>按内容源顺序扫描包，隔离无效可选包并检查全局数量限制。</summary>
    /// <param name="source">提供包列表、扫描诊断及资源访问的内容源。</param>
    /// <param name="logger">记录扫描、定义接受和诊断事件的日志实例。</param>
    /// <returns>合并有效包后的候选和统计；缺少内置包、必需包无效或全局体素超限时 CanCommit 为 false。</returns>
    /// <remarks>
    /// 至少需要一个 BuiltIn 包；重复命名空间的 Mod 全部跳过。单个 Mod 上限只统计已接受体素，全局上限只统计合并后的体素，均使用 ContentLimits。可选包警告也会标记包无效，但扫描诊断本身不直接决定候选许可。
    /// 合并时同一完整标识由后出现的有效包覆盖。正常返回时冻结体素字典，其他集合保留引用；提前拒绝的候选集合也可能未冻结。内容源和日志异常原样传播。
    /// </remarks>
    /// <example>超过包上限的 Mod 被隔离，其他有效包仍可发布；见 <c>ModLoadingTests.Per_mod_good_limit_isolates_only_that_package</c>。全局体素超限见 <c>ContentReloaderTests.File_and_global_limits_reject_oversized_candidates</c>。</example>
    public static CatalogCandidate Build(IContentSource source, TrainLogger logger)
    {
        var packages = source.ListPackages();
        var diagnostics = new List<ContentDiagnostic>();
        var accepted = new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>();
        var acceptedArchetypes = new Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry>();
        var acceptedComposites = new Dictionary<CompositeDefinitionId, CompositeCatalogEntry>();
        var scanned = 0;
        var requiredInvalid = false;
        var isolatedCount = 0;
        var acceptedMods = 0;

        foreach (var diagnostic in source.ListScanDiagnostics())
        {
            LogDiagnostic(logger, diagnostic);
            diagnostics.Add(diagnostic);
        }

        if (packages.Count == 0 || packages.All(package => package.Kind != ContentPackageKind.BuiltIn))
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.BuiltInPackageUnavailable,
                "The built-in content package was not provided."));
            return new CatalogCandidate(accepted, acceptedArchetypes, acceptedComposites, diagnostics, 0, false, true);
        }

        var duplicateModIds = packages
            .Where(package => package.Kind == ContentPackageKind.Mod)
            .GroupBy(package => package.Namespace, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            if (package.Kind == ContentPackageKind.Mod && duplicateModIds.Contains(package.Namespace))
            {
                var duplicate = new ContentDiagnostic(
                    ContentDiagnosticSeverity.Warning,
                    ContentDiagnosticCodes.DuplicateModUuid,
                    "Duplicate mod UUID disables every package using that UUID.",
                    package.Source ?? package.Namespace,
                    package.Namespace);
                LogDiagnostic(logger, duplicate);
                diagnostics.Add(duplicate);
                isolatedCount++;
                continue;
            }

            var bag = new DiagnosticBag();
            var packageContent = BuildPackage(source, package, bag, logger, ref scanned);
            if (package.Kind == ContentPackageKind.Mod && packageContent.Voxels.Count > ContentLimits.MaxVoxelsPerMod)
            {
                bag.Warn(ContentDiagnosticCodes.ModVoxelLimitExceeded, "A mod exceeds the 1000-voxel package limit.");
                packageContent = new PackageContent(
                    new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>(),
                    new Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry>(),
                    new Dictionary<CompositeDefinitionId, CompositeCatalogEntry>());
            }

            foreach (var diagnostic in bag.Items)
                LogDiagnostic(logger, diagnostic);
            diagnostics.AddRange(bag.Items);
            if (!package.Available || bag.Invalid)
            {
                if (package.IsRequired)
                    requiredInvalid = true;
                else
                    isolatedCount++;
                continue;
            }

            if (package.Kind == ContentPackageKind.Mod)
                acceptedMods++;

            if (packageContent.Voxels.Count == 0 &&
                packageContent.Archetypes.Count == 0 &&
                packageContent.Composites.Count == 0)
            {
                logger.Debug(
                    ContentLog.Category,
                    ContentLog.PackageEmptyEventId,
                    "Content package contains no voxels.",
                    new Dictionary<string, object?> { ["package"] = package.Namespace });
            }

            foreach (var (id, entry) in packageContent.Voxels)
                accepted[id] = entry;
            foreach (var (id, entry) in packageContent.Archetypes)
                acceptedArchetypes[id] = entry;
            foreach (var (id, entry) in packageContent.Composites)
                acceptedComposites[id] = entry;
        }

        var modCount = packages.Count(package => package.Kind == ContentPackageKind.Mod);
        if (modCount > 0 || source.ListScanDiagnostics().Count > 0)
        {
            logger.Info(
                ContentLog.Category,
                ContentLog.ModsScanSummaryEventId,
                "Mod scan finished.",
                new Dictionary<string, object?>
                {
                    ["candidate_count"] = modCount,
                    ["accepted_count"] = acceptedMods,
                    ["isolated_count"] = isolatedCount
                });
        }

        if (accepted.Count > ContentLimits.MaxVoxels)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.GlobalVoxelLimitExceeded,
                "The catalog exceeds the global voxel limit."));
            return new CatalogCandidate(accepted, acceptedArchetypes, acceptedComposites, diagnostics, scanned, false, true);
        }

        return new CatalogCandidate(
            accepted.ToFrozenDictionary(),
            acceptedArchetypes,
            acceptedComposites,
            diagnostics,
            scanned,
            canCommit: !requiredInvalid,
            requiredInvalid);
    }

    /// <summary>单个内容包中暂存的有效候选定义。</summary>
    /// <remarks>三个可变字典均保留输入引用；其中的条目只完成当前包内校验。诊断容器已无效时仍可暂存部分条目，是否合并由 Build 按包决定。</remarks>
    /// <example>重复本地标识使整个包无效，即使其他条目可暂存也不会合并；见 <c>ContentReloaderTests.Duplicate_local_ids_invalidate_the_whole_package</c>。</example>
    private sealed class PackageContent
    {
        /// <summary>获取本包通过定义与资产校验的体素条目。</summary>
        public Dictionary<VoxelDefinitionId, VoxelCatalogEntry> Voxels { get; }
        /// <summary>获取本包暂存的原型条目。</summary>
        public Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> Archetypes { get; }
        /// <summary>获取本包暂存的蓝图条目，引用目标在后续候选校验阶段检查。</summary>
        public Dictionary<CompositeDefinitionId, CompositeCatalogEntry> Composites { get; }

        /// <summary>保存单包构建的三类可变字典，不复制或冻结。</summary>
        /// <param name="voxels">包内接受的体素条目。</param>
        /// <param name="archetypes">包内接受的原型条目。</param>
        /// <param name="composites">包内接受的蓝图条目。</param>
        public PackageContent(
            Dictionary<VoxelDefinitionId, VoxelCatalogEntry> voxels,
            Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
            Dictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
        {
            Voxels = voxels;
            Archetypes = archetypes;
            Composites = composites;
        }
    }

    /// <summary>校验一个内容包的 JSON、定义和图片，并记录扫描文件数。</summary>
    /// <param name="source">提供文件发现、字节读取与图片解码的内容源。</param>
    /// <param name="package">待扫描的包；不可用时记录诊断并返回空集合。</param>
    /// <param name="bag">本包共用的诊断容器，保存无效状态；后续校验会更新 Source。</param>
    /// <param name="logger">记录定义接受及每个文件的扫描统计。</param>
    /// <param name="scanned">累计 JSON 文件数；本方法加上两类发现列表的项数，包括读取或解析失败的文件。</param>
    /// <returns>单包暂存的体素、原型和蓝图集合；即使包无效也可能非空，调用方须依据 bag.Invalid 整包拒绝或隔离。</returns>
    /// <remarks>体素本地标识在整个包内按 Ordinal 查重，重复组全部排除。即使某阶段已使包无效，也继续检查其他条目和蓝图，以保留诊断及逐文件日志。</remarks>
    /// <example>拒绝的 JSON 文件也记录 accepted 为零的扫描事件；见 <c>ContentReloaderTests.Rejected_content_file_still_emits_a_zero_accept_scan_event</c>。</example>
    private static PackageContent BuildPackage(
        IContentSource source,
        ContentPackage package,
        DiagnosticBag bag,
        TrainLogger logger,
        ref int scanned)
    {
        var accepted = new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>();
        var archetypes = new Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry>();
        var composites = new Dictionary<CompositeDefinitionId, CompositeCatalogEntry>();
        if (!package.Available)
        {
            if (package.IsRequired)
                bag.Error(ContentDiagnosticCodes.BuiltInPackageUnavailable, "The built-in content package is unavailable.");
            else
                bag.Warn(ContentDiagnosticCodes.PackageInvalid, "Mod package is unavailable.");
            return new PackageContent(accepted, archetypes, composites);
        }

        var discovery = source.ListVoxelFiles(package);
        var compositeDiscovery = source.ListCompositeFiles(package);
        scanned += discovery.Files.Count + compositeDiscovery.Files.Count;
        foreach (var diagnostic in discovery.Diagnostics.Concat(compositeDiscovery.Diagnostics))
            bag.Add(
                package.IsRequired ? diagnostic.Severity : ContentDiagnosticSeverity.Warning,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.JsonPath ?? diagnostic.Source);
        if (discovery.Invalid || compositeDiscovery.Invalid)
            bag.MarkInvalid();
        var parsed = new List<ParsedVoxel>();
        var acceptedByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in discovery.Files)
            acceptedByFile[file] = 0;
        foreach (var file in discovery.Files)
        {
            bag.Source = file;
            var read = source.ReadFile(package, file, ContentLimits.JsonFileMaxBytes);
            if (read.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                continue;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.FileTooLarge, "Voxel JSON exceeds the 1 MiB limit.");
                continue;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.InvalidJson, "Voxel file could not be read.");
                continue;
            }

            parsed.AddRange(VoxelsJsonParser.Parse(read.Bytes, file, bag));
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
            acceptedByFile[item.SourceFile] = acceptedByFile.GetValueOrDefault(item.SourceFile) + 1;
        }

        foreach (var file in discovery.Files)
            LogFileScanned(logger, package, file, "voxels", acceptedByFile[file]);

        AcceptComposites(source, package, compositeDiscovery, bag, logger, archetypes, composites);
        return new PackageContent(accepted, archetypes, composites);
    }

    /// <summary>解析蓝图文件，排除重复局部标识并计算发布指纹。</summary>
    /// <param name="source">提供蓝图 JSON 原始字节的内容源。</param>
    /// <param name="package">定义所属包，用于补全身份命名空间并区分必需包。</param>
    /// <param name="discovery">本包的蓝图文件发现结果；诊断和无效标记已由调用方并入 bag。</param>
    /// <param name="bag">共享包诊断容器；字段或文件失败可使整个包无效。</param>
    /// <param name="logger">记录暂存定义和逐文件统计；蓝图文件的 accepted 同时计入原型与蓝图。</param>
    /// <param name="archetypes">接收新原型条目的可变字典，按完整标识写入。</param>
    /// <param name="composites">接收新蓝图条目的可变字典，保留文件显示名称和发布指纹。</param>
    /// <remarks>原型与蓝图分别在包内查重，同一本地标识跨两类使用不算重复。此处构造定义但不检查引用目标是否存在；包最终被隔离时，先前的接受日志仍可能已写出。</remarks>
    /// <example>重复蓝图标识使其所属包无效；见 <c>CompositeContentTests.Duplicate_composite_ids_invalidate_the_owning_package</c>。</example>
    private static void AcceptComposites(
        IContentSource source,
        ContentPackage package,
        ContentFileDiscovery discovery,
        DiagnosticBag bag,
        TrainLogger logger,
        Dictionary<CompositeArchetypeId, CompositeArchetypeCatalogEntry> archetypes,
        Dictionary<CompositeDefinitionId, CompositeCatalogEntry> composites)
    {
        var parsedArchetypes = new List<ParsedCompositeArchetype>();
        var parsedComposites = new List<(ParsedCompositeDefinition Parsed, string? DisplayName)>();
        var acceptedByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in discovery.Files)
            acceptedByFile[file] = 0;
        foreach (var file in discovery.Files)
        {
            bag.Source = file;
            var read = source.ReadFile(package, file, ContentLimits.JsonFileMaxBytes);
            if (read.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", file);
                continue;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.FileTooLarge, "Composite JSON exceeds the 1 MiB limit.");
                continue;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.InvalidJson, "Composite file could not be read.");
                continue;
            }

            var document = CompositesJsonParser.Parse(read.Bytes, package.Namespace, file, bag);
            if (document is null)
                continue;
            foreach (var archetype in document.Archetypes)
                parsedArchetypes.Add(archetype);
            foreach (var composite in document.Composites)
                parsedComposites.Add((composite, document.DisplayName));
        }

        var duplicateArchetypes = parsedArchetypes
            .GroupBy(item => item.LocalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        var duplicateArchetypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in duplicateArchetypes)
        {
            duplicateArchetypeIds.Add(group.Key);
            bag.Source = group.First().SourceFile;
            bag.Warn(ContentDiagnosticCodes.DuplicateLocalId, "Duplicate local id in the package.", group.Key);
        }

        var duplicateComposites = parsedComposites
            .GroupBy(item => item.Parsed.LocalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        var duplicateCompositeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in duplicateComposites)
        {
            duplicateCompositeIds.Add(group.Key);
            bag.Source = group.First().Parsed.SourceFile;
            bag.Warn(ContentDiagnosticCodes.DuplicateLocalId, "Duplicate local id in the package.", group.Key);
        }

        foreach (var parsed in parsedArchetypes)
        {
            if (duplicateArchetypeIds.Contains(parsed.LocalId))
                continue;
            var id = new CompositeArchetypeId(package.Namespace, parsed.LocalId);
            var archetype = new CompositeArchetype(id, parsed.Construction, parsed.IdentityRetention);
            var semantic = CompositeRevisions.ComputeSemanticRevision(archetype);
            archetypes[id] = new CompositeArchetypeCatalogEntry(
                archetype,
                new ContentOrigin(package.Namespace, parsed.SourceFile),
                semantic,
                CompositeRevisions.ComputePublicationFingerprint(archetype, semantic));
            acceptedByFile[parsed.SourceFile] = acceptedByFile.GetValueOrDefault(parsed.SourceFile) + 1;
            logger.Debug(
                ContentLog.Category,
                ContentLog.DefinitionAcceptedEventId,
                "Accepted composite archetype.",
                new Dictionary<string, object?>
                {
                    ["archetype_id"] = id.Value,
                    ["source"] = parsed.SourceFile,
                    ["semantic_revision"] = semantic
                });
        }

        foreach (var (parsed, displayName) in parsedComposites)
        {
            if (duplicateCompositeIds.Contains(parsed.LocalId))
                continue;
            bag.Source = parsed.SourceFile;
            var id = new CompositeDefinitionId(package.Namespace, parsed.LocalId);
            var definition = new CompositeDefinition(
                id,
                parsed.Name,
                parsed.Description,
                CompositesJsonParser.ResolveArchetypeId(parsed.ArchetypeRaw, package.Namespace),
                parsed.SizeClass,
                parsed.Voxels,
                parsed.Contacts);
            var origin = new ContentOrigin(package.Namespace, parsed.SourceFile);
            var semantic = CompositeRevisions.ComputeSemanticRevision(definition);
            var publication = CompositeRevisions.ComputePublicationFingerprint(definition, displayName, semantic);
            composites[id] = new CompositeCatalogEntry(definition, origin, displayName, semantic, publication);
            acceptedByFile[parsed.SourceFile] = acceptedByFile.GetValueOrDefault(parsed.SourceFile) + 1;
            logger.Debug(
                ContentLog.Category,
                ContentLog.DefinitionAcceptedEventId,
                "Accepted composite definition.",
                new Dictionary<string, object?>
                {
                    ["composite_id"] = id.Value,
                    ["source"] = parsed.SourceFile,
                    ["semantic_revision"] = semantic
                });
        }

        foreach (var file in discovery.Files)
            LogFileScanned(logger, package, file, "composites", acceptedByFile[file]);
    }

    /// <summary>在表现图片有效时构造体素目录项及其语义和发布版本。</summary>
    /// <param name="source">提供表现图片读取和解码的内容源。</param>
    /// <param name="package">定义所属包，命名空间已通过包身份校验。</param>
    /// <param name="parsed">已通过 JSON 字段校验的体素数据；本方法不重复解析字段。</param>
    /// <param name="bag">记录资产失败的包诊断容器，Source 应已指向定义文件。</param>
    /// <param name="logger">成功构造候选条目时记录定义接受事件。</param>
    /// <param name="entry">成功时为含定义、来源、修订及解码图片的目录项；返回 false 时为 null。</param>
    /// <returns>全部表现资产有效且已构造目录项时为 true，资产校验失败时为 false。</returns>
    /// <remarks>定义构造、修订计算和日志异常不转换为 false，会原样传播；暂存成功也不表示所属包最终获准发布。</remarks>
    /// <example>语义、图片字节和目录代数的变化分别可观察；见 <c>ContentReloaderTests.Hash_matrix_separates_semantic_asset_and_catalog_generation</c>。</example>
    private static bool TryAccept(
        IContentSource source,
        ContentPackage package,
        ParsedVoxel parsed,
        DiagnosticBag bag,
        TrainLogger logger,
        out VoxelCatalogEntry entry)
    {
        entry = null!;
        if (!TryLoadPresentationAssets(source, package, parsed.Presentation, bag, out var assetRevisions, out var images))
            return false;

        var id = new VoxelDefinitionId(package.Namespace, parsed.LocalId);
        var definition = new VoxelDefinition(
            id,
            parsed.Name,
            parsed.Description,
            parsed.Value,
            parsed.Mass,
            parsed.MaxDurability,
            parsed.Tags,
            parsed.Behaviors,
            parsed.AllowedOrientations,
            parsed.Presentation,
            parsed.Collision,
            parsed.Propulsion,
            parsed.TrackContact);
        var origin = new ContentOrigin(package.Namespace, parsed.SourceFile);
        var semantic = ContentRevisions.ComputeSemanticRevision(definition);
        var publication = ContentRevisions.ComputePublicationFingerprint(definition, semantic, assetRevisions);
        entry = new VoxelCatalogEntry(definition, origin, semantic, assetRevisions, publication, images);
        logger.Debug(
            ContentLog.Category,
            ContentLog.DefinitionAcceptedEventId,
            "Accepted voxel definition.",
            new Dictionary<string, object?>
            {
                ["voxel_id"] = id.Value,
                ["source"] = parsed.SourceFile,
                ["semantic_revision"] = semantic
            });
        return true;
    }

    /// <summary>读取并解码表现图片，校验尺寸和形状，以参与解码的原始 PNG 字节计算资产修订。</summary>
    /// <param name="source">提供限量字节读取及从指定字节解码 PNG 的内容源。</param>
    /// <param name="package">资产所属包；链接诊断对必需包使用 Error，对可选包使用 Warning。</param>
    /// <param name="visual">已校验路径的图标、顶面、侧面和底面表现声明。</param>
    /// <param name="bag">记录资产诊断并标记当前包无效的容器。</param>
    /// <param name="revisions">按 Ordinal 路径索引的原始 PNG 字节修订；失败时仍可能含已处理资产。</param>
    /// <param name="images">按 Ordinal 路径索引的解码图片；失败时仍可能部分或全部填充，调用方不应接受这些结果。</param>
    /// <returns>字节读取、解码、尺寸及形状全部有效时为 true；首个失败条件记录诊断并返回 false。</returns>
    /// <remarks>
    /// 相同路径按 Ordinal 去重，每条路径读取一次，读取上限为 PngMaxBytes；同一份 read.Bytes 同时用于解码和资产修订。解码宽高各为 1 至 ImageMaxDimension 像素；图标须为正方形，三种面贴图还须边长相同且为 2 的幂，图标不要求与面贴图等大。
    /// 本方法依赖内容源遵守读取上限并正确报告链接；所有诊断均标记包无效，内容源异常原样传播。
    /// </remarks>
    /// <example>内容源后续读取会返回不同 PNG 字节时，当前资产修订仍使用已通过解码校验的那份字节；见 <c>ContentReloaderTests.Asset_hash_uses_the_same_bytes_that_passed_png_validation</c>。相同像素但不同 PNG 字节会改变资产修订，见 <c>ContentReloaderTests.Same_pixels_different_raw_png_bytes_change_only_the_asset_revision</c>。</example>
    private static bool TryLoadPresentationAssets(
        IContentSource source,
        ContentPackage package,
        VoxelPresentation visual,
        DiagnosticBag bag,
        out Dictionary<string, string> revisions,
        out Dictionary<string, DecodedImage> images)
    {
        revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        images = new Dictionary<string, DecodedImage>(StringComparer.Ordinal);
        var paths = new[] { visual.IconPath, visual.TopPath, visual.SidePath, visual.BottomPath };
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var read = source.ReadFile(package, path, ContentLimits.PngMaxBytes);
            if (read.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                return false;
            }

            if (read.TooLarge)
            {
                bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG exceeds the size limit.", path);
                return false;
            }

            if (read.Bytes is null)
            {
                bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Asset bytes could not be read.", path);
                return false;
            }

            var decoded = source.DecodePng(package, path, read.Bytes);
            if (decoded.UnsafeLink)
            {
                if (package.IsRequired)
                    bag.Error(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
                else
                    bag.Warn(ContentDiagnosticCodes.UnsafeLinkedPath, "Linked or reparse-point content is not allowed.", path);
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

            images[path] = decoded.Image;
            revisions[path] = ContentRevisions.ComputeAssetRevision(read.Bytes);
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

    /// <summary>判断解码图片是否为正尺寸的正方形。</summary>
    private static bool IsSquare(DecodedImage image) => image.Width == image.Height && image.Width > 0;

    /// <summary>判断像素边长是否为正的二次幂。</summary>
    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    /// <summary>记录文件所属包、内容类型及接受的定义数。</summary>
    /// <param name="logger">扫描事件写入的日志实例。</param>
    /// <param name="package">文件所属包，日志记录其命名空间。</param>
    /// <param name="source">包内文件路径。</param>
    /// <param name="kind">扫描的内容类别，如 voxels 或 composites。</param>
    /// <param name="accepted">该文件成功暂存的定义数；蓝图文件同时统计原型和蓝图，失败文件可为零。</param>
    private static void LogFileScanned(
        TrainLogger logger,
        ContentPackage package,
        string source,
        string kind,
        int accepted) =>
        logger.Debug(
            ContentLog.Category,
            ContentLog.FileScannedEventId,
            "Content file scanned.",
            new Dictionary<string, object?>
            {
                ["source"] = source,
                ["package"] = package.Namespace,
                ["kind"] = kind,
                ["accepted"] = accepted
            });

    /// <summary>将 Error 诊断输出为错误日志，其他级别输出为警告日志，不改变包有效性。</summary>
    /// <param name="logger">诊断事件写入的日志实例。</param>
    /// <param name="diagnostic">提供严重程度、说明及 code、source、json_path 字段的诊断。</param>
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

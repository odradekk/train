// 属于内容模块。
// 定义统一内容重载的查询、结果和发布通知契约。

namespace Train.Content;

/// <summary>表示一次内容重载的候选是否被接受。</summary>
/// <remarks>数据种类：枚举；内容无变化也可成功，是否发布由 ContentReloadResult.Changed 表示。</remarks>
public enum ContentReloadStatus
{
    /// <summary>候选被拒绝，保留重载前的完整快照。</summary>
    Rejected,
    /// <summary>候选有效且没有警告。</summary>
    Committed,
    /// <summary>候选有效，但发现警告或隔离了无效可选包。</summary>
    CommittedWithWarnings
}

/// <summary>表示一次重载的接受状态、发布效果、保留快照和诊断。</summary>
/// <remarks>
/// 数据种类：结构。Snapshot 为 null 表示尚无成功发布；否则是本轮结束时保留的整个内容批次。
/// 拒绝时 Changed 为 false，Snapshot 与重载前相同；成功但无变化时同样保留原快照。
/// 成功且 Changed 为 true 时恰好发布一个新世代。诊断按报告顺序保存不可修改的副本。
/// </remarks>
/// <example>三种状态、首次拒绝、失败保留旧内容和无变化的数据例子见 ContentReloadExamples。</example>
public sealed class ContentReloadResult
{
    /// <summary>获取候选的接受状态。</summary>
    public ContentReloadStatus Status { get; }

    /// <summary>获取本轮是否发布了一个新内容批次。</summary>
    public bool Changed { get; }

    /// <summary>获取本轮保留的完整快照；首次成功发布前为 null。</summary>
    public ContentSnapshot? Snapshot { get; }

    /// <summary>获取发现、解析和校验诊断的不可修改副本。</summary>
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    /// <summary>获取候选是否被接受；无变化时也可以为 true。</summary>
    public bool IsCommitted => Status is ContentReloadStatus.Committed or ContentReloadStatus.CommittedWithWarnings;

    /// <summary>保存重载结果，并检查状态与快照、发布标记的组合。</summary>
    /// <param name="status">候选的接受状态。</param>
    /// <param name="changed">是否发布新快照；拒绝时必须为 false。</param>
    /// <param name="snapshot">本轮保留的快照；候选被接受时必须非 null。</param>
    /// <param name="diagnostics">按报告顺序排列的非 null 诊断集合，允许为空。</param>
    /// <exception cref="ArgumentOutOfRangeException">status 不属于已声明状态。</exception>
    /// <exception cref="ArgumentNullException">diagnostics 为 null。</exception>
    /// <exception cref="ArgumentException">状态组合无效，或诊断集合包含 null。</exception>
    internal ContentReloadResult(
        ContentReloadStatus status, bool changed, ContentSnapshot? snapshot,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == ContentReloadStatus.Rejected && changed ||
            status != ContentReloadStatus.Rejected && snapshot is null)
            throw new ArgumentException("Reload status, publication flag, and snapshot are inconsistent.");
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Content diagnostics cannot contain null.", nameof(diagnostics));
        Status = status;
        Changed = changed;
        Snapshot = snapshot;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
}

/// <summary>校验完整候选内容并发布一个不可变的内容批次。</summary>
/// <remarks>
/// 数据种类：带状态的结构；状态为尚未发布（Current 为 null）或持有一个完整快照。
/// 本实例独占发布权，拒绝并发及重入重载。依赖和订阅者不属于内容定义，也不写入存档。
/// </remarks>
/// <example>状态和手算预期见 ContentReloadExamples。</example>
public sealed class ContentReloader
{
    private readonly IContentSource _source;
    private readonly IContentLogger _logger;
    private ContentSnapshot? _current = null;
    private int _busy;

    /// <summary>获取当前完整快照；首次成功发布前为 null。</summary>
    /// <remarks>每次查询取得一个完整批次；跨定义查询应持有本次返回值，避免多次查询跨越重载。</remarks>
    public ContentSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>在新快照发布后同步通知该次发布前后的完整内容。</summary>
    /// <remarks>无变化或拒绝时不通知；单个订阅者异常记录到日志并继续通知其余订阅者，不撤销已完成发布。</remarks>
    public event Action<ContentChange>? Changed;

    /// <summary>创建持有内容源和日志依赖、尚未发布内容的重载器。</summary>
    /// <param name="source">提供包发现、JSON 文件发现、字节读取和 PNG 解码的内容源。</param>
    /// <param name="logger">记录扫描、校验和发布诊断的日志。</param>
    /// <remarks>保存依赖，不访问内容源、不发布快照、不发送通知。</remarks>
    /// <exception cref="ArgumentNullException">source 或 logger 为 null。</exception>
    public ContentReloader(IContentSource source, IContentLogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
    }

    /// <summary>从内容源构建自洽候选，并报告候选接受状态和本轮保留的快照。</summary>
    /// <returns>候选被拒绝、被接受但无变化，或被接受并发布新快照的结果。</returns>
    /// <remarks>
    /// 目标效果：有效内容变化时一次替换完整快照、增加一个世代并通知；无变化或拒绝时保留原快照且不通知。
    /// 内置内容失败拒绝整轮；可选包失败整包隔离，并重查受其影响的引用。所有派生属性对应同一候选批次。
    /// 并发或重入立即返回 reload_already_in_progress。内容源的未预期异常向上传播，仍释放本实例的重载权。
    /// </remarks>
    public ContentReloadResult Reload()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            var rejected = new ContentReloadResult(ContentReloadStatus.Rejected, false, Current,
                new[] { new ContentDiagnostic(ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.ReloadAlreadyInProgress, "A content reload is already in progress.") });
            LogResult(rejected, 0, 0);
            return rejected;
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

    /// <summary>构建完整候选、一次发布并通知；校验失败或无变化时保留旧快照。</summary>
    private ContentReloadResult ReloadExclusive()
    {
        _logger.Info(ContentLog.Category, ContentLog.ScanStartedEventId, "Scanning content packages.");
        var previous = Current;
        var loader = new ContentLoader(_source, _logger);
        if (!loader.Load(initial: previous is null))
        {
            var rejected = new ContentReloadResult(ContentReloadStatus.Rejected, false, previous, loader.Diagnostics);
            LogResult(rejected, loader.ScannedFileCount, loader.DefinitionCount);
            return rejected;
        }
        var diagnostics = loader.Diagnostics;
        var status = diagnostics.Count == 0 ? ContentReloadStatus.Committed : ContentReloadStatus.CommittedWithWarnings;
        var changed = loader.HasChanges(previous);
        var current = changed ? loader.Freeze(checked((previous?.Generation ?? 0) + 1)) : previous!;
        if (changed)
        {
            Volatile.Write(ref _current, current);
            Notify(new ContentChange(previous, current));
        }
        var committed = new ContentReloadResult(status, changed, current, diagnostics);
        LogResult(committed, loader.ScannedFileCount, loader.DefinitionCount);
        return committed;
    }

    /// <summary>逐个通知本次发布的订阅者，记录异常并继续；不撤销已经发布的快照。</summary>
    private void Notify(ContentChange change)
    {
        if (Changed is not { } subscribers)
            return;
        foreach (Action<ContentChange> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(change);
            }
            catch (Exception exception)
            {
                _logger.Error(ContentLog.Category, ContentLog.NotifyFailedEventId,
                    "A content change subscriber failed after publication.",
                    new Dictionary<string, object?>
                    {
                        ["exception"] = exception.GetType().FullName, ["message"] = exception.Message
                    });
            }
        }
    }

    /// <summary>记录本轮结果和候选扫描统计；统计不代表已经发布的定义数量。</summary>
    private void LogResult(ContentReloadResult result, int scanned, int accepted)
    {
        var warnings = result.Diagnostics.Count(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Warning);
        var errors = result.Diagnostics.Count(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Error);
        var properties = new Dictionary<string, object?>
        {
            ["status"] = result.Status.ToString(), ["changed"] = result.Changed,
            ["generation"] = result.Snapshot?.Generation ?? 0, ["scanned_files"] = scanned,
            ["accepted"] = accepted, ["warning_count"] = warnings, ["error_count"] = errors
        };
        if (!result.IsCommitted)
            _logger.Error(ContentLog.Category, ContentLog.ReloadFailedEventId, "Content was not published.", properties);
        _logger.Info(ContentLog.Category, ContentLog.ReloadSummaryEventId, "Content reload finished.", properties);
    }
}


namespace Train.Content;

public enum ContentReloadStatus
{
    Rejected,
    Committed,
    CommittedWithWarnings
}

public sealed class ContentReloadResult
{
    public ContentReloadStatus Status { get; }

    public bool Changed { get; }

    public ContentSnapshot? Snapshot { get; }

    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    public bool IsCommitted => Status is ContentReloadStatus.Committed or ContentReloadStatus.CommittedWithWarnings;

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

public sealed class ContentReloader
{
    private readonly IContentSource _source;
    private readonly IContentLogger _logger;
    private ContentSnapshot? _current = null;
    private int _busy;

    public ContentSnapshot? Current => Volatile.Read(ref _current);

    public event Action<ContentChange>? Changed;

    public ContentReloader(IContentSource source, IContentLogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
    }

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

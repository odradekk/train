using System.Collections.Immutable;
using System.Diagnostics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Saves;

internal sealed record SaveEntry(string Path, SaveHeader Header, bool Automatic);
internal sealed record LeavePrompt(bool CanSave, bool Waiting);
internal sealed record MissingPrompt(ImmutableArray<string> Details);
internal enum LeaveChoice { Save, Discard, Cancel }

internal sealed class SaveService : IDisposable
{
    private readonly HostCoordinator _host;
    private readonly FileSaveStore _store;
    private readonly Func<double> _seconds;
    private readonly Func<DateTimeOffset> _date;
    private readonly List<PendingWrite> _writes = [];
    private readonly Dictionary<Guid, int> _slots = [];
    private PendingRead? _read;
    private PendingImport? _import;
    private long? _restore;
    private Action? _leave;
    private bool _leaveSaving;
    private bool _leaveClosesSession;
    private long? _shownMissing;
    private HostBinding? _autoBinding;
    private HostBinding? _autoPending;
    private double _nextAuto;
    private bool _disposed;

    internal SaveService(HostCoordinator host, FileSaveStore store, Func<double>? seconds = null,
        Func<DateTimeOffset>? date = null)
    {
        _host = host; _store = store;
        _seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _date = date ?? (() => DateTimeOffset.UtcNow);
        _host.ReplacementCompleted += OnReplacement;
    }

    internal event Action? Changed;
    internal SaveStatus? Status => _host.GetSaveStatus();
    internal string? LastMessage { get; private set; }
    internal LeavePrompt? Leaving { get; private set; }
    internal bool HasPendingWork => _writes.Count != 0 || _read is not null || _import is not null || _restore is not null;
    internal bool IsBusy => HasPendingWork || _leave is not null || _host.IsBusy;
    internal TimeSpan? AutoSaveInterval { get; private set; } = TimeSpan.FromMinutes(5);
    internal MissingPrompt? Missing
    {
        get
        {
            if (_import?.Prepared is { } imported) return MissingFor(imported.Prepared);
            return _restore is not null && _host.MissingConfirmation is { } missing
                ? new(MissingDetails(missing.Content, missing.Core)) : null;
        }
    }

    internal void SetAutoSaveInterval(TimeSpan? interval)
    {
        if (interval is { } value && value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        AutoSaveInterval = interval;
        _autoPending = null;
        _nextAuto = interval is { } duration ? _seconds() + duration.TotalSeconds : 0;
    }

    internal IReadOnlyList<SaveEntry> List()
    {
        var entries = new List<SaveEntry>();
        var root = Path.Combine(_store.Root, FileSaveStore.SavesFolder);
        if (!Directory.Exists(root)) return entries;
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { LastMessage = $"无法读取存档目录：{error.Message}"; return entries; }
        foreach (var directory in directories)
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out var id)) continue;
            foreach (var path in new[] { _store.PathFor(id), _store.AutoPathFor(id, 0),
                _store.AutoPathFor(id, 1), _store.AutoPathFor(id, 2) })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var header = _store.ReadHeader(path);
                    if (header.Id != id) throw new SaveFormatException("identity-mismatch", "存档头部与目录身份不匹配");
                    entries.Add(new(path, header, Path.GetFileName(path) != FileSaveStore.FileName));
                }
                catch (Exception error) when (error is SaveFormatException or IOException or UnauthorizedAccessException)
                { LastMessage = $"无法读取存档列表项 {path}：{error.Message}"; }
            }
        }
        return entries.OrderByDescending(entry => entry.Header.SavedAt).ThenBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    internal SaveFailure? Save() => StartWrite(WriteKind.Manual);
    internal SaveFailure? SaveAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fail("invalid-name", "存档名称不能为空。");
        return StartWrite(WriteKind.SaveAs, new(Guid.NewGuid(), name.Trim()));
    }
    internal SaveFailure? Export(string path, bool overwriteConfirmed = false)
    {
        if (File.Exists(path) && !overwriteConfirmed)
            return Fail("overwrite-confirmation", "目标文件已存在，请确认覆盖。");
        return StartWrite(WriteKind.Export, path: path, overwrite: overwriteConfirmed);
    }

    private SaveFailure? StartWrite(WriteKind kind, SaveIdentity? target = null, string? path = null,
        bool overwrite = true, bool leaving = false)
    {
        if (_read is not null || _import is not null || _restore is not null || (!leaving && _leave is not null))
            return Fail("busy", "正在处理其他存档操作。");
        var status = Status;
        if (status is null) return Fail("no-session", "当前没有对局。");
        if (_writes.Any(write => write.Capture.Binding == status.Binding)) return Fail("busy", "当前对局正在写入存档。");
        var (capture, failure) = _host.CaptureSave();
        if (failure is not null) return Fail(failure.Code, failure.Message);
        target ??= capture!.Identity;
        var slot = kind == WriteKind.Automatic ? NextSlot(target.Id) : -1;
        path = Path.GetFullPath(path ?? (slot >= 0 ? _store.AutoPathFor(target.Id, slot) : _store.PathFor(target.Id)));
        if (_writes.Any(write => string.Equals(write.Path, path, StringComparison.OrdinalIgnoreCase)))
            return Fail("busy", "目标文件正在写入。");
        var cancellation = new CancellationTokenSource();
        var savedAt = _date();
        var writeTarget = target;
        var writePath = path;
        var work = Task.Run(() => _store.Write(writePath, capture!.Encode(writeTarget, savedAt),
            kind == WriteKind.SaveAs ? false : overwrite, cancellation.Token));
        _writes.Add(new(capture!, target, path, kind, slot, cancellation, work, leaving));
        LastMessage = "正在保存…";
        Changed?.Invoke();
        return null;
    }

    internal SaveFailure? Load(SaveEntry entry) => RequestLeave(() => StartRead(entry));

    private void StartRead(SaveEntry entry)
    {
        if (_host.SessionOptions is null) { Fail("session-options-unavailable", "宿主尚未配置会话环境。"); return; }
        if (_host.LockLeaving() is not null) { Fail("busy", "正在处理其他操作。"); return; }
        _read = new(entry, Task.Run(() => _store.Read(entry.Path)));
        LastMessage = "正在读取存档…";
        Changed?.Invoke();
    }

    internal SaveFailure? Import(string path)
    {
        if (IsBusy) return Fail("busy", "正在处理其他存档操作。");
        if (_host.SessionOptions is not { } options)
            return Fail("session-options-unavailable", "宿主尚未配置会话环境。");
        _import = new(Task.Run(() =>
        {
            var document = _store.Read(path);
            return new ImportPreparation(document, _host.PrepareRestore(document.CorePayload, document.ContentPayload, options));
        }));
        LastMessage = "正在校验导入存档…";
        Changed?.Invoke();
        return null;
    }

    internal bool ConfirmMissing()
    {
        if (_import?.Prepared is { } imported) { PublishImport(imported); return true; }
        return _restore is { } operation && _host.ConfirmMissing(operation);
    }

    internal SaveFailure? ReturnToMenu() => RequestLeave(_host.LeaveSession, closesSession: true);

    internal SaveFailure? RequestLeave(Action continuation) => RequestLeave(continuation, closesSession: false);
    private SaveFailure? RequestLeave(Action continuation, bool closesSession)
    {
        if (_leave is not null || _read is not null || _import is { Published: null } || _restore is not null)
            return Fail("busy", "正在处理其他操作，请先完成或取消。");
        if (_host.LockLeaving() is not null) return Fail("busy", "正在处理其他操作，请先完成或取消。");
        _leave = continuation;
        _leaveClosesSession = closesSession;
        _autoPending = null;
        ContinueLeaving();
        return null;
    }

    internal void ChooseLeave(LeaveChoice choice)
    {
        if (_leave is null || Leaving is not { } prompt) return;
        if (choice == LeaveChoice.Cancel)
        {
            foreach (var write in _writes.Where(write => write.Leaving)) write.Cancellation.Cancel();
            AbortLeaving();
            return;
        }
        if (prompt.Waiting) return;
        if (choice == LeaveChoice.Discard) { FinishLeaving(); return; }
        if (!prompt.CanSave) return;
        _leaveSaving = true;
        Leaving = new(true, true);
        if (StartWrite(WriteKind.Manual, leaving: true) is not null) AbortLeaving();
    }

    private void ContinueLeaving()
    {
        if (_leave is null || _leaveSaving) return;
        LeavePrompt prompt;
        if (_writes.Count != 0 || _import?.Published is not null) prompt = new(false, true);
        else if (Status is { Unsaved: true } status) prompt = new(!status.Frozen, false);
        else { FinishLeaving(); return; }
        if (Leaving != prompt) { Leaving = prompt; Changed?.Invoke(); }
    }

    private void FinishLeaving()
    {
        var continuation = _leave!;
        var closesSession = _leaveClosesSession;
        _leave = null; Leaving = null; _leaveSaving = false;
        if (closesSession) continuation();
        _host.UnlockLeaving();
        if (!closesSession) continuation();
        Changed?.Invoke();
    }

    private void AbortLeaving()
    {
        _leave = null; Leaving = null; _leaveSaving = false;
        _host.UnlockLeaving();
        Changed?.Invoke();
    }

    internal void Cancel()
    {
        if (_restore is { } operation) { _host.Cancel(operation); return; }
        if (_read is { } read)
        {
            _read = null; _ = Observe(read.Work);
            _host.UnlockLeaving();
            LastMessage = "读档已取消。";
        }
        if (_import is { } import)
        {
            if (import.Published is not null)
            {
                import.Cancellation!.Cancel();
                return;
            }
            _import = null;
            if (import.Prepared is { } prepared) prepared.Prepared.Dispose();
            else _ = DiscardImport(import.Work);
            LastMessage = "导入已取消。";
        }
        foreach (var write in _writes) write.Cancellation.Cancel();
        if (_leave is not null && !_leaveSaving) AbortLeaving();
        Changed?.Invoke();
    }

    internal void Process()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var write in _writes.Where(write => write.Work.IsCompleted).ToArray())
        {
            _writes.Remove(write);
            FileWriteResult result;
            try { result = write.Work.GetAwaiter().GetResult(); }
            catch (Exception error) { result = new(false, "save-fault", error.ToString()); }
            write.Cancellation.Dispose();
            if (result.Published)
            {
                if (write.Kind is WriteKind.Manual or WriteKind.SaveAs) _host.CompleteSave(write.Capture, write.Target);
                if (write.Kind == WriteKind.Automatic) _slots[write.Target.Id] = (write.Slot + 1) % 3;
                LastMessage = result.Message ?? "保存完成。";
            }
            else LastMessage = result.Message ?? "保存失败。";
            if (result.CleanupMessage is { } cleanup) LastMessage += $"\n{cleanup}";
            if (write.Leaving && _leaveSaving)
            {
                _leaveSaving = false;
                if (result.Published) FinishLeaving();
                else AbortLeaving();
            }
            Changed?.Invoke();
        }
        PumpRead();
        PumpImport();
        if (_restore is { } operation && _shownMissing != operation && _host.MissingConfirmation is not null)
        { _shownMissing = operation; Changed?.Invoke(); }
        ContinueLeaving();
        ScheduleAutomatic();
    }

    private void PumpRead()
    {
        if (_read is not { } read || !read.Work.IsCompleted) return;
        _read = null;
        _host.UnlockLeaving();
        try
        {
            var document = read.Work.GetAwaiter().GetResult();
            var begun = _host.BeginRestore(document.CorePayload, document.ContentPayload, _host.SessionOptions!,
                new(document.Header.Id, document.Header.Name), read.Entry.Automatic);
            if (begun.Problem is { } problem) Fail(problem.Code, "无法开始读档。");
            else _restore = begun.Operation;
        }
        catch (Exception error) { ReportFileFailure(error); }
        Changed?.Invoke();
    }

    private void PumpImport()
    {
        if (_import is not { } import) return;
        if (import.Published is { } published)
        {
            if (!published.IsCompleted) return;
            _import = null;
            try
            {
                var result = published.GetAwaiter().GetResult();
                LastMessage = result.Message ?? (result.Published ? "导入完成，已建立新存档。" : "导入失败。");
                if (result.CleanupMessage is { } cleanup) LastMessage += $"\n{cleanup}";
            }
            catch (Exception error) { ReportFileFailure(error); }
            import.Cancellation!.Dispose();
            Changed?.Invoke();
            return;
        }
        if (import.Prepared is not null || !import.Work.IsCompleted) return;
        ImportPreparation prepared;
        try { prepared = import.Work.GetAwaiter().GetResult(); }
        catch (Exception error) { _import = null; ReportFileFailure(error); Changed?.Invoke(); return; }
        if (PreparationFailure(prepared.Prepared) is { } failure)
        { prepared.Prepared.Dispose(); _import = null; Fail(failure.Code, failure.Message); return; }
        import.Prepared = prepared;
        if (MissingFor(prepared.Prepared) is null) PublishImport(prepared);
        else Changed?.Invoke();
    }

    private void PublishImport(ImportPreparation imported)
    {
        imported.Prepared.Dispose();
        var id = Guid.NewGuid();
        var document = imported.Document with { Header = imported.Document.Header with { Id = id } };
        var cancellation = new CancellationTokenSource();
        _import!.Prepared = null;
        _import.Published = Task.Run(() => _store.Write(_store.PathFor(id), document, false, cancellation.Token));
        _import.Cancellation = cancellation;
        LastMessage = "正在写入导入存档…";
        Changed?.Invoke();
    }

    private void OnReplacement(long operation, bool succeeded)
    {
        if (_restore != operation) return;
        _restore = null;
        LastMessage = succeeded ? "读档完成，游戏已暂停。" : "读档失败或已取消，保留原对局。";
        Changed?.Invoke();
    }

    private void ScheduleAutomatic()
    {
        var status = Status;
        var now = _seconds();
        if (_autoBinding != status?.Binding)
        { _autoBinding = status?.Binding; _autoPending = null; _nextAuto = now + (AutoSaveInterval?.TotalSeconds ?? 0); }
        if (status is null || AutoSaveInterval is not { } interval || status.Frozen || _leave is not null) return;
        if (now >= _nextAuto) { _autoPending = status.Binding; _nextAuto = now + interval.TotalSeconds; }
        if (_autoPending == status.Binding && !IsBusy)
        { _autoPending = null; StartWrite(WriteKind.Automatic); }
    }

    private int NextSlot(Guid id)
    {
        if (_slots.TryGetValue(id, out var slot)) return slot;
        var oldest = DateTimeOffset.MaxValue;
        for (var index = 0; index < 3; index++)
        {
            try
            {
                var header = _store.ReadHeader(_store.AutoPathFor(id, index));
                if (header.SavedAt < oldest) { oldest = header.SavedAt; slot = index; }
            }
            catch (Exception error) when (error is SaveFormatException or IOException or UnauthorizedAccessException)
            { return index; }
        }
        return slot;
    }

    private SaveFailure Fail(string code, string message)
    { LastMessage = message; Changed?.Invoke(); return new(code, message); }
    private void ReportFileFailure(Exception error) => Fail(error is SaveFormatException format ? format.Code : "file-failed", error.Message);
    private static SaveFailure? PreparationFailure(HostCoordinator.PreparedOperation prepared) => prepared switch
    {
        { Content: ContentResult<IContentCandidate>.Failure failure } => new(failure.Diagnostics[0].Code, failure.Diagnostics[0].Message),
        { Content: ContentResult<IContentCandidate>.Faulted fault } => new(fault.Code, fault.Message),
        { Core: CoreResult<ICoreSessionCandidate>.Failure failure } => new(failure.Problems[0].Code, $"核心恢复校验失败：{failure.Problems[0].Field}"),
        { Core: CoreResult<ICoreSessionCandidate>.Faulted fault } => new(fault.Fault.Code, fault.Diagnostic?.Message ?? "核心恢复校验失败。"),
        { CoreCandidate: not null, ContentCandidate: not null } => null,
        _ => new("invalid-save", "存档候选不完整。")
    };
    private static MissingPrompt? MissingFor(HostCoordinator.PreparedOperation prepared)
    {
        var details = MissingDetails(prepared.ContentCandidate!.Report, prepared.CoreCandidate!.MissingReferences);
        return details.Length == 0 ? null : new(details);
    }
    private static ImmutableArray<string> MissingDetails(ContentPreparationReport content, ImmutableArray<MissingCoreReference> core)
        => content.RuntimeMissing.Select(missing => $"{missing.Reference}（包 {missing.Package?.Value ?? "未知"}；{missing.Field}）")
            .Concat(core.Select(missing => $"{missing.Reference}（{missing.FieldPath}）")).ToImmutableArray();
    private static async Task Observe(Task work) { try { await work.ConfigureAwait(false); } catch { /* 异常退出的后台结果已失去接入资格。 */ } }
    private static async Task DiscardImport(Task<ImportPreparation> work)
    { try { (await work.ConfigureAwait(false)).Prepared.Dispose(); } catch { /* 取消后的准备只负责清理。 */ } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.ReplacementCompleted -= OnReplacement;
        Cancel();
        foreach (var write in _writes) _ = FinishWrite(write.Work, write.Cancellation);
        _writes.Clear();
        if (_import?.Published is { } imported) _ = FinishWrite(imported, _import.Cancellation!);
    }

    private static async Task FinishWrite(Task<FileWriteResult> work, CancellationTokenSource cancellation)
    { try { await work.ConfigureAwait(false); } catch { /* 异常退出后只观察发布结果，不再修改活动组。 */ } finally { cancellation.Dispose(); } }

    private enum WriteKind { Manual, SaveAs, Automatic, Export }
    private sealed record PendingWrite(SaveCapture Capture, SaveIdentity Target, string Path, WriteKind Kind,
        int Slot, CancellationTokenSource Cancellation, Task<FileWriteResult> Work, bool Leaving);
    private sealed record PendingRead(SaveEntry Entry, Task<SaveDocument> Work);
    private sealed record ImportPreparation(SaveDocument Document, HostCoordinator.PreparedOperation Prepared);
    private sealed class PendingImport(Task<ImportPreparation> work)
    {
        internal Task<ImportPreparation> Work { get; } = work;
        internal ImportPreparation? Prepared { get; set; }
        internal Task<FileWriteResult>? Published { get; set; }
        internal CancellationTokenSource? Cancellation { get; set; }
    }
}

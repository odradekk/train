using System.Collections.Immutable;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Recording;

internal sealed record RecordingStatus(SimulationPosition From, SimulationPosition Until,
    ImmutableArray<RecordingGap> Gaps, bool HasUsableStart, bool Frozen, bool Incomplete);

internal sealed class RecordingService : IDisposable
{
    internal const ulong StartIntervalSteps = 18_000;
    private readonly HostCoordinator _host;
    private readonly ulong _interval;
    private readonly Func<RecordingSnapshot, string, bool, CancellationToken, int, Task<FileWriteResult>> _write;
    private readonly Queue<(RecordingSnapshot Snapshot, string Path)> _faults = [];
    private readonly HashSet<HostBinding> _faultBindings = [];
    private PendingWrite? _pending;
    private HostBinding? _binding;
    private CoreRecordingWindow? _window;
    private ulong _lastStart;
    private bool _initialized;
    private bool _hasUsableStart;
    private string? _faultFailure;
    private string? _lastMessage;
    private bool _disposed;
    private bool _closing;
    private Action? _closed;
    private int _diagnosisSequence;
    private static readonly object DiagnosisTraceLock = new();

    internal RecordingService(HostCoordinator host, FileSaveStore store,
        ulong intervalSteps = StartIntervalSteps,
        Func<RecordingSnapshot, string, bool, CancellationToken, Task<FileWriteResult>>? write = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(intervalSteps);
        _host = host; _interval = intervalSteps;
        EvidenceDirectory = Path.Combine(store.Root, "recordings");
        SnapshotPath = Path.Combine(EvidenceDirectory, "snapshot.zip");
        _write = write is not null ? (snapshot, path, overwrite, cancellation, _) => write(snapshot, path, overwrite, cancellation)
            : (snapshot, path, overwrite, cancellation, id) => Task.Run(() =>
            {
                var package = RecordingPackage.Capture(snapshot.Window, snapshot.Content, snapshot.Runtime);
                TraceDiagnosis(id, "capture-completed", null);
                return store.Write(path, candidate =>
                {
                    package.Write(candidate);
                    TraceDiagnosis(id, "write-completed", null);
                }, candidate =>
                {
                    package.Validate(candidate);
                    TraceDiagnosis(id, "validate-completed", null);
                }, overwrite, cancellation);
            });
        host.BoundaryCompleted += OnBoundary;
    }

    internal event Action? Changed;
    internal string EvidenceDirectory { get; }
    internal string SnapshotPath { get; }
    internal string? LastMessage
    {
        get => _faultFailure is null || _lastMessage == _faultFailure ? _lastMessage : $"{_lastMessage}\n{_faultFailure}";
        private set => _lastMessage = value;
    }
    internal bool IsBusy => _pending is not null || _faults.Count != 0 || _closing || _host.IsBusy;
    internal (bool PendingIsNull, bool? SnapshotBindingEqualsCurrent, bool? WorkIsCompleted) DiagnosisState =>
        _pending is { } pending ? (false, pending.Snapshot.Binding == _host.GetStatus()?.Binding, pending.Work.IsCompleted)
            : (true, null, null);
    internal bool IsReplay => _host.GetStatus() is { IsReplay: true };
    private HostStatus? OrdinaryStatus => _host.GetStatus() is { IsReplay: false } status ? status : null;
    internal RecordingStatus? Status => OrdinaryStatus is not { } status ? null : new(
        _binding == status.Binding && _window is { Starts.Length: > 0 } ? _window.Starts[0].State.Position : status.View.Position,
        status.View.Position, _binding == status.Binding ? _window?.Gaps ?? [] : [],
        _binding == status.Binding && _hasUsableStart, status.IsFrozen,
        _binding == status.Binding && (_faultFailure is not null || (_window?.Gaps.Length ?? 0) != 0));

    internal void Process()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObserveBinding(OrdinaryStatus?.Binding);
        if (_pending is { } completed && completed.Work.IsCompleted)
        {
            _pending = null;
            TraceDiagnosis(completed.DiagnosisId, "cleared", completed.Snapshot.Binding == _binding);
            FileWriteResult result;
            try { result = completed.Work.GetAwaiter().GetResult(); }
            catch (Exception error) { result = new(false, "recording-fault", error.Message); }
            TraceDiagnosis(completed.DiagnosisId, result.Published ? "published" : "publication-failed",
                completed.Snapshot.Binding == _binding);
            completed.Cancellation.Dispose();
            if (completed.Point is { } point)
            {
                _host.ConfirmRecordingStart(completed.Snapshot.Binding, point,
                    result.Published ? RecordingWriteOutcome.PublishedWithDependencies : RecordingWriteOutcome.Failed);
                if (_binding == completed.Snapshot.Binding)
                {
                    _hasUsableStart |= result.Published;
                    _window = _host.ReadRecording(completed.Snapshot.Binding);
                }
            }
            else if (completed.Fault && !result.Published && _binding == completed.Snapshot.Binding)
                _faultFailure = $"故障证据写入失败：{result.Message ?? result.Code}";
            LastMessage = result.Published ? $"复现资料已写入：{completed.Path}" : $"复现资料写入失败：{result.Message ?? result.Code}";
            if (result.CleanupMessage is { } cleanup) LastMessage += $"\n{cleanup}";
            Changed?.Invoke();
        }
        if (_pending is not null) return;
        if (_faults.TryDequeue(out var fault))
        { StartWrite(fault.Snapshot, fault.Path, false, fault: true); return; }
        if (_closing)
        {
            var closed = _closed;
            _closed = null;
            closed?.Invoke();
            return;
        }
        if (OrdinaryStatus is not { } status || _host.IsBusy) return;
        RecordingStart start;
        if (!_initialized)
        {
            _window = _host.ReadRecording(status.Binding)!;
            start = _window.Starts[0];
        }
        else
        {
            if (status.IsFrozen || status.View.Position.CompletedSteps - _lastStart < _interval) return;
            var captured = _host.CaptureRecordingStart(status.Binding);
            if (captured is not CoreResult<RecordingStart>.Success success)
            { Fail("capture-failed", "无法建立新的录制起点。"); return; }
            start = success.Value;
            _window = _host.ReadRecording(status.Binding)!;
        }
        _initialized = true;
        _lastStart = start.State.Position.CompletedSteps;
        var (snapshot, failure) = _host.CaptureRecording(status.Binding, _window);
        if (failure is not null)
        {
            _host.ConfirmRecordingStart(status.Binding, start.Id, RecordingWriteOutcome.Failed);
            _window = _host.ReadRecording(status.Binding);
            Fail(failure.Code, failure.Message);
            return;
        }
        StartWrite(snapshot!, SnapshotPath, true, start.Id);
    }

    private void OnBoundary(HostBinding binding, ICoreReadView view, IContentView content,
        ImmutableArray<EventEnvelope> events)
    {
        if (OrdinaryStatus is not { } status || status.Binding != binding ||
            view.Fault is null || !_faultBindings.Add(binding)) return;
        ObserveBinding(binding);
        var window = _host.ReadRecording(binding)!;
        _window = window;
        var (snapshot, failure) = _host.CaptureRecording(binding, window);
        if (failure is not null)
        { _faultFailure = $"故障证据捕获失败：{failure.Message}"; Fail(failure.Code, _faultFailure); return; }
        _faults.Enqueue((snapshot!, Path.Combine(EvidenceDirectory, $"fault-{binding.Session:N}-{Guid.NewGuid():N}.zip")));
    }

    private void ObserveBinding(HostBinding? binding)
    {
        if (_binding == binding) return;
        _binding = binding; _window = null;
        _initialized = false; _hasUsableStart = false; _faultFailure = null;
    }

    internal SaveFailure? Export(string path, bool overwriteConfirmed = false)
    {
        var status = OrdinaryStatus;
        if (status is null) return _host.GetStatus() is null
            ? Fail("no-session", "当前没有对局。")
            : Fail("replay_read_only", "重放不录制新的复现包。");
        if (IsBusy) return Fail("busy", "正在写入复现资料，请稍后再试。");
        if (File.Exists(path) && !overwriteConfirmed) return Fail("overwrite-confirmation", "目标文件已存在，请确认覆盖。");
        var window = _host.ReadRecording(status.Binding)!;
        var (snapshot, failure) = _host.CaptureRecording(status.Binding, window);
        if (failure is not null) return Fail(failure.Code, failure.Message);
        _window = window;
        StartWrite(snapshot!, path, overwriteConfirmed);
        return null;
    }

    private void StartWrite(RecordingSnapshot snapshot, string path, bool overwrite,
        RecordingPointId? point = null, bool fault = false)
    {
        var cancellation = new CancellationTokenSource();
        var diagnosisId = ++_diagnosisSequence;
        TraceDiagnosis(diagnosisId, "started", snapshot.Binding == _host.GetStatus()?.Binding);
        var work = _write(snapshot, path, overwrite, cancellation.Token, diagnosisId);
        _pending = new(snapshot, Path.GetFullPath(path), point, fault, cancellation, work, diagnosisId);
        if (Environment.GetEnvironmentVariable("TRAIN_HEAP_DIAGNOSIS") is { Length: > 0 } directory)
            _ = work.ContinueWith(static (task, state) =>
            {
                var (traceDirectory, id) = ((string, int))state!;
                WriteDiagnosis(traceDirectory, id, task.Status == TaskStatus.RanToCompletion ? "work-completed" : "work-failed", null);
            }, (directory, diagnosisId), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        LastMessage = "正在写入复现资料…";
        Changed?.Invoke();
    }

    private SaveFailure Fail(string code, string message)
    { LastMessage = message; Changed?.Invoke(); return new(code, message); }

    internal void Close(Action closed)
    {
        _closing = true;
        _closed = closed;
        Process();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _closing = true; _closed = null;
        _faults.Clear();
        if (_pending?.Work.IsCompleted == true) Process();
        _disposed = true;
        _host.BoundaryCompleted -= OnBoundary;
        if (_pending is { } pending)
        {
            TraceDiagnosis(pending.DiagnosisId, "cancelled", pending.Snapshot.Binding == _binding);
            pending.Cancellation.Cancel();
            // 正常退出先 Close 等待确认；强制移除节点时只保留已发布文件，不阻塞引擎线程。
            _ = FinishAfterRemoval(pending);
            _pending = null;
        }
        _faults.Clear();
    }

    private static async Task FinishAfterRemoval(PendingWrite pending)
    { try { await pending.Work.ConfigureAwait(false); } catch { } finally { pending.Cancellation.Dispose(); } }

    private static void TraceDiagnosis(int id, string phase, bool? sameBinding)
    {
        if (Environment.GetEnvironmentVariable("TRAIN_HEAP_DIAGNOSIS") is { Length: > 0 } directory)
            WriteDiagnosis(directory, id, phase, sameBinding);
    }

    private static void WriteDiagnosis(string directory, int id, string phase, bool? sameBinding)
    {
        lock (DiagnosisTraceLock)
            File.AppendAllText(Path.Combine(directory, "recording.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { process_id = Environment.ProcessId, id, phase, same_binding = sameBinding,
                    utc = DateTimeOffset.UtcNow, timestamp = System.Diagnostics.Stopwatch.GetTimestamp() }) + "\n");
    }

    private sealed record PendingWrite(RecordingSnapshot Snapshot, string Path, RecordingPointId? Point,
        bool Fault, CancellationTokenSource Cancellation, Task<FileWriteResult> Work, int DiagnosisId);
}

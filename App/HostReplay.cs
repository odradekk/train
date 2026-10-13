using System.Collections.Immutable;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

internal sealed record ReplayRecording(string Path, SimulationPosition From, SimulationPosition Until,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<RecordingGap> Gaps);

internal sealed partial class HostCoordinator
{
    private PendingReplay? _pendingReplay;
    private readonly List<Task<PreparedReplay>> _abandonedReplays = [];
    internal string? ReplayMessage { get; private set; }
    internal ReplayRecording? ReplayRecording { get { EnsureOwner(); return _active?.ReplayRecording; } }

    internal (long Operation, CoreProblem? Problem) BeginReplay(string path, Func<string, RecordingPackage>? read = null)
    {
        EnsureOwner();
        if (IsBusy) return (0, Problem("busy"));
        if (SessionOptions is not { } options) return (0, Problem("session_options_unavailable"));
        var id = checked(++_nextOperation);
        _pendingReplay = new(id, _active?.Binding, _content, Task.Run(() => PrepareReplay(path, options.Runtime, read ?? RecordingPackage.Read)));
        _active?.Driver.SetOperationLocked(true);
        ReplayMessage = "正在准备隔离重放…";
        return (id, null);
    }

    internal bool CancelReplay(long operation)
    {
        EnsureOwner();
        if (_insideBoundary || _pendingReplay is not { } pending || pending.Id != operation) return false;
        _abandonedReplays.Add(pending.Work);
        _pendingReplay = null;
        _active?.Driver.SetOperationLocked(_leaveLocked);
        _skipNextFrame = true;
        ReplayMessage = "重放准备已取消，保留原会话。";
        return true;
    }

    private PreparedReplay PrepareReplay(string path, RuntimeIdentity runtime, Func<string, RecordingPackage> read)
    {
        ICoreReplay? replay = null;
        try
        {
            var package = read(path);
            var input = SelectReplayInput(package);
            var decoded = _contentFactory.DecodeReplay(package.ContentPayload);
            if (decoded is not ContentResult<IContentReplayCapture>.Success content)
                return new(null, [], null, ContentFailure(decoded));
            var history = content.Value.History;
            var result = _core.PrepareReplay(input, runtime, history.Select(view => view.Core).ToImmutableArray());
            if (result is CoreResult<ICoreReplay>.Failure rejected)
                return new(null, [], null, new(rejected.Problems[0].Code, $"重放被拒绝：{rejected.Problems[0].Field}"));
            if (result is CoreResult<ICoreReplay>.Faulted fault)
                return new(null, [], null, new(fault.Fault.Code, fault.Diagnostic?.Message ?? "重放准备发生内部故障。"));
            replay = ((CoreResult<ICoreReplay>.Success)result).Value;
            _ = ReplayContent(history, replay.Read().InputIdentity);
            return new(replay, history, new(Path.GetFullPath(path), replay.Read().Position,
                input.EndPosition, input.Commands, package.Header.Gaps), null);
        }
        catch (Exception error)
        {
            replay?.Dispose();
            return new(null, [], null, new("replay-preparation-failed", $"无法打开复现包：{error.Message}"));
        }
    }

    internal static CoreReplayInput SelectReplayInput(RecordingPackage package)
    {
        var header = package.Header;
        if (!header.FaultSegments.IsEmpty)
        {
            var fault = header.FaultSegments[0];
            return new(1, header.Runtime, fault.Start.Payload, fault.Commands, fault.Checkpoints,
                fault.Fault.Position, fault.Fault);
        }
        var start = header.Starts.OrderBy(point => point.Position.CompletedSteps).ThenBy(point => point.Position.Sequence)
            .FirstOrDefault(point => ComparePosition(point.Position, header.EndPosition) <= 0 &&
                !header.Gaps.Any(gap => ComparePosition(gap.From, header.EndPosition) < 0 &&
                    (gap.Until is null || ComparePosition(gap.Until.Value, point.Position) > 0)));
        if (start is null) throw new InvalidDataException("录制窗口没有之后连续、无缺口的可用起点。");
        return new(1, header.Runtime, start.Payload,
            package.Commands.Where(command => ComparePosition(command.Result.After, start.Position) > 0 &&
                ComparePosition(command.Result.After, header.EndPosition) <= 0).ToImmutableArray(),
            header.Checkpoints.Where(checkpoint => ComparePosition(checkpoint.Position, start.Position) >= 0 &&
                ComparePosition(checkpoint.Position, header.EndPosition) <= 0).ToImmutableArray(), header.EndPosition, null);
    }

    internal static int ComparePosition(SimulationPosition left, SimulationPosition right) =>
        left.CompletedSteps != right.CompletedSteps ? left.CompletedSteps.CompareTo(right.CompletedSteps)
            : left.Sequence.CompareTo(right.Sequence);

    private static IContentView ReplayContent(IEnumerable<IContentView> history, CoreInputId input) =>
        history.LastOrDefault(view => view.Core.Identity == input)
            ?? throw new InvalidDataException("复现包缺少当前输入的原内容。");

    private static SaveFailure ContentFailure<T>(ContentResult<T> result) where T : notnull => result switch
    {
        ContentResult<T>.Failure invalid => new(invalid.Diagnostics[0].Code, invalid.Diagnostics[0].Message),
        ContentResult<T>.Faulted fault => new(fault.Code, fault.Message),
        _ => throw new InvalidOperationException("无法识别的内容失败。")
    };

    private void PumpReplayPreparations()
    {
        foreach (var work in _abandonedReplays.Where(work => work.IsCompleted).ToArray())
        { work.GetAwaiter().GetResult().Replay?.Dispose(); _abandonedReplays.Remove(work); }
        if (_pendingReplay is not { } pending || !pending.Work.IsCompleted) return;
        var prepared = pending.Work.GetAwaiter().GetResult();
        _pendingReplay = null;
        if (prepared.Failure is { } failure)
        { ReplayMessage = failure.Message; _active?.Driver.SetOperationLocked(_leaveLocked); return; }
        if (_active?.Binding != pending.Binding || !ReferenceEquals(_content, pending.Content))
        { prepared.Replay!.Dispose(); _active?.Driver.SetOperationLocked(_leaveLocked); return; }
        var replay = prepared.Replay!;
        var old = _active;
        _insideBoundary = true;
        try
        {
            _active = new(replay, ReplayContent(prepared.History, replay.Read().InputIdentity), prepared.History,
                new(_seconds), SessionOptions!.Runtime, prepared.Recording!);
            _content = _active.Content;
            UpdateFocusPause();
            Dispatch([]);
            try { old?.Dispose(); }
            catch (Exception error) { LogHostFailure("host_retirement_failed", error); }
            ReplayMessage = $"隔离重放已打开：{prepared.Recording!.Path}";
        }
        finally { _insideBoundary = false; }
    }

    internal ReplayAdvanceResult? AdvanceReplay(uint maximumSteps, SimulationPosition? stopAt = null)
    {
        EnsureOwner();
        if (IsBusy || _active is not { Replay: { } replay } group) return null;
        if (group.Driver.Frozen) return null;
        _insideBoundary = true;
        try
        {
            var result = replay.Advance(maximumSteps, stopAt);
            group.View = replay.Read();
            group.Content = ReplayContent(group.History, group.View.InputIdentity);
            _content = group.Content;
            if (result.Fault is not null) group.Driver.Freeze();
            Dispatch(result.Events);
            ReplayMessage = $"重放停止：{result.Stop} · {result.Position.CompletedSteps}:{result.Position.Sequence}" +
                (result.Difference is { } difference ? $" · {difference.Field}: {difference.Expected} → {difference.Actual}" : "") +
                (result.Fault is { } fault ? $" · {fault.Code}" : "");
            return result;
        }
        finally { _insideBoundary = false; }
    }

    internal (SaveCapture? Capture, SaveFailure? Failure) CaptureReplaySave()
    {
        EnsureOwner();
        if (IsBusy) return (null, new("busy", "正在处理其他操作。"));
        if (_active is not { Replay: { } replay } group) return (null, new("no-replay", "当前不是重放会话。"));
        _insideBoundary = true;
        try
        {
            var core = replay.CaptureBoundary();
            var content = _contentFactory.Capture(group.Content, core.References);
            if (content is not ContentResult<IContentCapture>.Success captured) return (null, ContentFailure(content));
            var identity = new SaveIdentity(Guid.NewGuid(), "重放边界副本");
            return (new(group.Binding, identity, new(core.Revision, captured.Value.Revision, identity.Name), core, captured.Value), null);
        }
        finally { _insideBoundary = false; }
    }

    private void CloseReplayPreparations()
    {
        if (_pendingReplay is { } pending) _abandonedReplays.Add(pending.Work);
        _pendingReplay = null;
        foreach (var work in _abandonedReplays) _ = DisposeReplayWhenReady(work);
        _abandonedReplays.Clear();
    }
    private async Task DisposeReplayWhenReady(Task<PreparedReplay> work)
    {
        try { var prepared = await work.ConfigureAwait(false); prepared.Replay?.Dispose(); }
        catch (Exception error) { LogHostFailure("host_replay_cleanup_failed", error); }
    }

    private sealed record PendingReplay(long Id, HostBinding? Binding, IContentView? Content, Task<PreparedReplay> Work);
    private sealed record PreparedReplay(ICoreReplay? Replay, ImmutableArray<IContentView> History,
        ReplayRecording? Recording, SaveFailure? Failure);
}

using System.Collections.Immutable;
using Train.App.Logging;
using Train.App.Saves;
using Train.App.Recording;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

internal readonly record struct HostBinding(Guid Session);
internal enum NotificationAction { None, SlowToOne, Pause }
internal sealed record ReplayRecording(string Path, SimulationPosition From, SimulationPosition Until,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<RecordingGap> Gaps);
internal sealed record HostStatus(HostBinding Binding, ICoreReadView View,
    bool PlayerPaused, bool FocusPaused, bool OperationLocked, bool IsFrozen,
    SimulationSpeed Speed, double ActualSpeed, double Remainder, bool IsReplay = false)
{
    internal bool IsPaused => PlayerPaused || FocusPaused || OperationLocked || IsFrozen;
    internal double SimulationSeconds => View.Position.CompletedSteps / (double)SimulationTime.StepsPerSecond;
}

internal sealed class HostCoordinator : IDisposable
{
    private readonly ICoreFactory _core;
    private readonly IContentFactory _contentFactory;
    private readonly IContentFiles _files;
    private readonly IAssetInspector _assets;
    private readonly ContentInstallation _installation;
    private readonly TrainLogger _logger;
    private readonly Func<CoreEvent, NotificationAction> _notification;
    private readonly Func<double>? _seconds;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly List<PendingOperation> _abandoned = [];
    private ActiveGroup? _active;
    private IContentView? _content;
    private PendingOperation? _pending;
    private long _nextOperation;
    private bool _insideBoundary;
    private bool _focused = true;
    private bool _autoPauseOnFocusLoss;
    private bool _disposed;
    private bool _skipNextFrame;
    private bool _leaveLocked;
    private bool _boundaryStop;

    internal HostCoordinator(ICoreFactory core, IContentFactory content, IContentFiles files,
        IAssetInspector assets, ContentInstallation installation, TrainLogger logger,
        Func<CoreEvent, NotificationAction>? notification = null, Func<double>? seconds = null,
        CoreSessionOptions? sessionOptions = null)
    {
        _core = core; _contentFactory = content; _files = files; _assets = assets;
        _installation = installation; _logger = logger;
        _notification = notification ?? (_ => NotificationAction.None);
        _seconds = seconds;
        SessionOptions = sessionOptions;
    }

    internal event Action<HostBinding, ICoreReadView, IContentView, ImmutableArray<EventEnvelope>>? BoundaryCompleted;
    internal event Action<long, bool>? ReplacementCompleted;
    internal CoreSessionOptions? SessionOptions { get; }
    internal RuntimeIdentity? Runtime { get { EnsureOwner(); return _active?.Runtime; } }
    internal ReplayRecording? ReplayRecording { get { EnsureOwner(); return _active?.ReplayRecording; } }
    internal NotificationAction LastNotification { get; private set; }
    internal IContentView? Content { get { EnsureOwner(); return _content; } }
    internal bool IsBusy { get { EnsureOwner(); return _pending is not null || _insideBoundary || _leaveLocked; } }
    internal bool AutoPauseOnFocusLoss
    {
        get { EnsureOwner(); return _autoPauseOnFocusLoss; }
        set { EnsureOwner(); _autoPauseOnFocusLoss = value; UpdateFocusPause(); }
    }

    internal HostStatus? GetStatus()
    {
        EnsureOwner();
        if (_active is not { } group) return null;
        var driver = group.Driver;
        return new(group.Binding, group.View, driver.PlayerPaused, driver.FocusPaused,
            driver.OperationLocked, driver.Frozen, driver.Speed, driver.ActualSpeed, driver.Remainder,
            group.Replay is not null);
    }

    internal void SetFocused(bool focused) { EnsureOwner(); _focused = focused; UpdateFocusPause(); }
    private void UpdateFocusPause() => _active?.Driver.SetFocusPaused(_autoPauseOnFocusLoss && !_focused);

    internal CoreProblem? SetPaused(bool paused)
    {
        EnsureOwner();
        var problem = MutationProblem(allowFrozen: paused, allowReplay: true);
        if (problem is not null) return problem;
        _active!.Driver.SetPlayerPaused(paused);
        return null;
    }

    internal CoreProblem? SetSpeed(SimulationSpeed speed)
    {
        EnsureOwner();
        var problem = MutationProblem(allowFrozen: true);
        if (problem is not null) return problem;
        _active!.Driver.SetSpeed(speed);
        return null;
    }

    internal (ulong Completed, CoreProblem? Problem) Step(ulong steps)
    {
        EnsureOwner();
        var problem = MutationProblem();
        if (problem is not null) return (0, problem);
        if (!_active!.Driver.IsPaused) return (0, Problem("simulation_requires_pause"));
        if (steps == 0) throw new ArgumentOutOfRangeException(nameof(steps));
        _insideBoundary = true;
        try
        {
            var completed = _active.Driver.Step(steps, () => AdvanceOne(manual: true));
            return (completed, _active.Driver.Frozen ? Problem("frozen") : null);
        }
        finally { _insideBoundary = false; }
    }

    internal CoreResult<ICoreCapture> CaptureCheckpoint()
    {
        EnsureOwner();
        return _active is null ? new CoreResult<ICoreCapture>.Failure([Problem("simulation_session_unavailable")])
            : CaptureCurrent();
    }

    internal CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual)
    {
        EnsureOwner();
        return _core.CompareCheckpoints(expected, actual);
    }

    internal void RequestPauseAfterBoundary()
    {
        EnsureOwner();
        if (!_insideBoundary) throw new InvalidOperationException("边界暂停只能由边界通知请求。");
        // 不重入核心写入口；分发结束后由当前驱动停止下一步。
        _boundaryStop = true;
        _active!.Driver.SetPlayerPaused(true);
    }

    internal CoreRecordingWindow? ReadRecording(HostBinding binding)
    {
        EnsureOwner();
        return _active is { Replay: null } group && group.Binding == binding ? group.Core.ReadRecording() : null;
    }

    internal CoreResult<RecordingStart> CaptureRecordingStart(HostBinding binding)
    {
        EnsureOwner();
        if (MutationProblem() is { } problem) return new CoreResult<RecordingStart>.Failure([problem]);
        if (_active!.Binding != binding) return new CoreResult<RecordingStart>.Failure([Problem("content_changed")]);
        return _active.Core.CaptureRecordingStart();
    }

    internal (RecordingSnapshot? Snapshot, SaveFailure? Failure) CaptureRecording(
        HostBinding binding, CoreRecordingWindow window)
    {
        EnsureOwner();
        if (_active is not { Replay: null } group || group.Binding != binding)
            return (null, new("no-session", "录制所属对局已离开。"));
        var result = _contentFactory.CaptureReplay(group.History.ToImmutableArray(), window.RequiredReferences);
        return result switch
        {
            ContentResult<IContentReplayCapture>.Success captured => (new(binding, group.Runtime, window, captured.Value), null),
            ContentResult<IContentReplayCapture>.Failure failed => (null, new(failed.Diagnostics[0].Code, failed.Diagnostics[0].Message)),
            ContentResult<IContentReplayCapture>.Faulted fault => (null, new(fault.Code, fault.Message)),
            _ => throw new InvalidOperationException("无法识别的内容捕获结果。")
        };
    }

    internal void ConfirmRecordingStart(HostBinding binding, RecordingPointId point,
        RecordingWriteOutcome outcome)
    {
        EnsureOwner();
        if (_active is not { Replay: null } group || group.Binding != binding) return;
        var result = group.Core.ConfirmRecordingStart(point, outcome);
        group.ReleasedInputs.UnionWith(result.ReleasedInputs);
        group.History.RemoveAll(view => group.ReleasedInputs.Contains(view.Core.Identity)
            && !ReferenceEquals(view, group.Content));
    }

    internal SaveStatus? GetSaveStatus()
    {
        EnsureOwner();
        return _active is not { Replay: null } group ? null : new(group.Binding, group.Save,
            group.Saved != new SaveRevision(group.View.Revision, group.Content.PersistentRevision, group.Save.Name),
            group.Driver.Frozen);
    }

    internal (SaveCapture? Capture, SaveFailure? Failure) CaptureSave()
    {
        EnsureOwner();
        if (_pending is not null || _insideBoundary) return (null, new("busy", "正在处理其他操作。"));
        if (_active is not { } group) return (null, new("no_session", "当前没有对局。"));
        if (group.Driver.Frozen) return (null, new("frozen", "核心已冻结，不能保存普通存档。"));
        _insideBoundary = true;
        try
        {
            var core = CaptureCurrent();
            if (core is CoreResult<ICoreCapture>.Failure rejected)
                return (null, new(rejected.Problems[0].Code, "核心状态捕获失败。"));
            if (core is CoreResult<ICoreCapture>.Faulted failed)
                return (null, new(failed.Fault.Code, failed.Diagnostic?.Message ?? "核心状态捕获失败。"));
            var fixedCore = ((CoreResult<ICoreCapture>.Success)core).Value;
            var content = _contentFactory.Capture(group.Content, fixedCore.References);
            if (content is ContentResult<IContentCapture>.Failure invalid)
                return (null, new(invalid.Diagnostics[0].Code, invalid.Diagnostics[0].Message));
            if (content is ContentResult<IContentCapture>.Faulted faulted)
                return (null, new(faulted.Code, faulted.Message));
            var fixedContent = ((ContentResult<IContentCapture>.Success)content).Value;
            var identity = group.Replay is null ? group.Save : new SaveIdentity(Guid.NewGuid(), "重放边界副本");
            return (new(group.Binding, identity,
                new(fixedCore.Revision, fixedContent.Revision, identity.Name), fixedCore, fixedContent), null);
        }
        finally { _insideBoundary = false; }
    }

    internal bool CompleteSave(SaveCapture capture, SaveIdentity target)
    {
        EnsureOwner();
        if (_active is not { } group || group.Binding != capture.Binding || group.Save.Id != capture.Identity.Id)
            return false;
        group.Save = new(target.Id, group.Save.Name == capture.Identity.Name ? target.Name : group.Save.Name);
        group.Saved = capture.Revision with { Name = target.Name };
        return true;
    }

    internal CoreProblem? RenameSave(string name)
    {
        EnsureOwner();
        if (MutationProblem() is { } problem) return problem;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _active!.Save = _active.Save with { Name = name };
        return null;
    }

    internal CoreProblem? LockLeaving()
    {
        EnsureOwner();
        if (IsBusy) return Problem("busy");
        _leaveLocked = true;
        _active?.Driver.SetOperationLocked(true);
        return null;
    }

    internal void UnlockLeaving()
    {
        EnsureOwner();
        _leaveLocked = false;
        _active?.Driver.SetOperationLocked(_pending is not null);
        _skipNextFrame = true;
    }

    internal void LeaveSession()
    {
        EnsureOwner();
        if (!_leaveLocked || _pending is not null || _insideBoundary)
            throw new InvalidOperationException("离开对局需要先取得离开保护锁。");
        var old = _active;
        _active = null;
        old?.Dispose();
    }

    private CoreResult<ICoreCapture> CaptureCurrent()
    {
        var group = _active!;
        if (group.Replay is { } replay)
            return new CoreResult<ICoreCapture>.Success(replay.CaptureBoundary());
        var result = group.Core.Capture(group.Driver.Frozen ? CapturePurpose.Diagnostic : CapturePurpose.Save);
        if (result is CoreResult<ICoreCapture>.Faulted failed)
        {
            // 只读捕获失败不等于活动核心已冻结，以会话读视图的故障为准。
            group.View = group.Core.Read();
            if (group.View.Fault is { } fault) RecordCoreFault(fault, failed.Diagnostic);
        }
        return result;
    }

    internal SubmissionResult Submit(HostBinding binding, CoreCommand command, CommandSource source)
    {
        EnsureOwner();
        var problem = MutationProblem();
        if (problem is not null) return new SubmissionResult.NotAccepted(problem);
        if (_active!.Binding != binding) return new SubmissionResult.NotAccepted(Problem("content_changed", "host.binding"));
        _insideBoundary = true;
        try
        {
            var result = _active.Core.Submit(command, source);
            if (result is SubmissionResult.Completed completed)
                CompleteSubmission(completed, target: null);
            return result;
        }
        finally { _insideBoundary = false; }
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
            return result;
        }
        finally { _insideBoundary = false; }
    }

    internal (long Operation, CoreProblem? Problem) BeginNewGame(GenerationProfileId profile,
        ulong seed, string generatorVersion, CoreSessionOptions options)
        => Begin(OperationKind.NewGame, () => PrepareNew(profile, seed, generatorVersion, options), CommandSource.UserInterface, options: options);

    internal (long Operation, CoreProblem? Problem) BeginRestore(string corePayload,
        string contentPayload, CoreSessionOptions options, SaveIdentity? save = null, bool unsaved = true)
        => Begin(OperationKind.Restore, () => PrepareRestore(corePayload, contentPayload, options), CommandSource.UserInterface, save, unsaved, options);

    internal (long Operation, CoreProblem? Problem) BeginReplay(string path, Func<string, RecordingPackage>? read = null)
    {
        EnsureOwner();
        if (SessionOptions is not { } options) return (0, Problem("session_options_unavailable"));
        return Begin(OperationKind.Replay, () => PrepareReplay(path, options.Runtime, read ?? RecordingPackage.Read),
            CommandSource.UserInterface, options: options);
    }

    private PreparedOperation PrepareReplay(string path, RuntimeIdentity runtime, Func<string, RecordingPackage> read)
    {
        ICoreReplay? replay = null;
        try
        {
            var package = read(path);
            var input = SelectReplayInput(package);
            var decoded = _contentFactory.DecodeReplay(package.ContentPayload);
            if (decoded is ContentResult<IContentReplayCapture>.Failure invalid)
                return new(new ContentResult<IContentCandidate>.Failure(invalid.Diagnostics), null, null);
            if (decoded is ContentResult<IContentReplayCapture>.Faulted failed)
                return new(new ContentResult<IContentCandidate>.Faulted(failed.Code, failed.Message, failed.StackTrace), null, null);
            var history = ((ContentResult<IContentReplayCapture>.Success)decoded).Value.History;
            var result = _core.PrepareReplay(input, runtime, history.Select(view => view.Core).ToImmutableArray());
            if (result is CoreResult<ICoreReplay>.Failure rejected)
                return new(null, new CoreResult<ICoreSessionCandidate>.Failure(rejected.Problems), null);
            if (result is CoreResult<ICoreReplay>.Faulted fault)
                return new(null, new CoreResult<ICoreSessionCandidate>.Faulted(fault.Fault, fault.Diagnostic), null);
            replay = ((CoreResult<ICoreReplay>.Success)result).Value;
            var view = replay.Read();
            _ = ReplayContent(history, view.InputIdentity);
            return new(null, null, null) { Replay = replay, History = history,
                Recording = new(Path.GetFullPath(path), view.Position, input.EndPosition, input.Commands, package.Header.Gaps) };
        }
        catch
        {
            replay?.Dispose();
            throw;
        }
    }

    internal (long Operation, CoreProblem? Problem) BeginReload(CommandSource source)
    {
        EnsureOwner();
        if (_active?.Replay is not null) return (0, Problem("replay_read_only"));
        if (IsBusy || _abandoned.Any(operation => operation.Reload && !operation.Work.IsCompleted))
            return (0, Problem("busy"));
        var references = new CoreReferenceManifest([], [], [], []);
        if (_active is not null)
        {
            var captured = CaptureCurrent();
            if (captured is CoreResult<ICoreCapture>.Failure rejected)
            {
                LogReloadResult(checked(++_nextOperation), null, "Rejected", false, rejected.Problems);
                return (0, rejected.Problems[0]);
            }
            if (captured is CoreResult<ICoreCapture>.Faulted failed)
            {
                LogReloadResult(checked(++_nextOperation), null, "Faulted", false,
                    [new(failed.Fault.Code, failed.Fault.Field, failed.Fault.Subject, [])], failed.Diagnostic);
                return (0, _active.Driver.Frozen ? Problem("frozen") :
                    new(failed.Fault.Code, failed.Fault.Field, failed.Fault.Subject, []));
            }
            references = ((CoreResult<ICoreCapture>.Success)captured).Value.References;
        }
        var basis = _content;
        ContentLoadPurpose purpose = basis is null ? new ContentLoadPurpose.Initial()
            : new ContentLoadPurpose.Reload(basis, references);
        return Begin(OperationKind.Reload, () => new(_contentFactory.PrepareLoad(
            new(_installation, purpose), _files, _assets), null, null), source);
    }

    private (long Operation, CoreProblem? Problem) Begin(OperationKind kind, Func<PreparedOperation> prepare,
        CommandSource source, SaveIdentity? save = null, bool unsaved = true, CoreSessionOptions? options = null)
    {
        EnsureOwner();
        if (IsBusy) return (0, Problem("busy"));
        var operation = new PendingOperation(checked(++_nextOperation), kind, _active?.Binding,
            _content, Task.Run(prepare), source) { Save = save, Unsaved = unsaved, Options = options };
        _pending = operation;
        _active?.Driver.SetOperationLocked(true);
        return (operation.Id, null);
    }

    internal bool Cancel(long operationId)
    {
        EnsureOwner();
        if (_insideBoundary || _pending is not { } operation || operation.Id != operationId) return false;
        _pending = null;
        _active?.Driver.SetOperationLocked(_leaveLocked);
        _skipNextFrame = true;
        if (operation.Prepared is { } prepared) Discard(prepared);
        else _abandoned.Add(operation);
        NotifyReplacement(operation);
        return true;
    }

    internal (long Operation, ContentPreparationReport Content, ImmutableArray<MissingCoreReference> Core)? MissingConfirmation
    {
        get
        {
            EnsureOwner();
            if (_pending is not { Prepared: { } prepared } operation) return null;
            return (operation.Id, prepared.ContentCandidate!.Report, prepared.CoreCandidate!.MissingReferences);
        }
    }

    internal bool ConfirmMissing(long operationId)
    {
        EnsureOwner();
        if (_insideBoundary || _pending is not { Prepared: { } prepared } operation || operation.Id != operationId)
            return false;
        Commit(operation, prepared);
        return true;
    }

    internal void ProcessFrame(double delta)
    {
        EnsureOwner();
        if (_insideBoundary) throw new InvalidOperationException("宿主边界内不能重入帧驱动。");
        var skipAdvance = _skipNextFrame;
        _skipNextFrame = false;
        PumpPreparations();
        _insideBoundary = true;
        try
        {
            if (!skipAdvance && _active is { Replay: null } group)
                group.Driver.AdvanceFrame(delta, () => AdvanceOne(manual: false));
        }
        finally { _insideBoundary = false; }
    }

    private void PumpPreparations()
    {
        foreach (var operation in _abandoned.Where(operation => operation.Work.IsCompleted).ToArray())
        {
            TakeAndDiscard(operation);
            _abandoned.Remove(operation);
        }
        if (_pending is not { Prepared: null } current || !current.Work.IsCompleted) return;
        PreparedOperation prepared;
        try { prepared = current.Work.GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            if (current.Reload) LogReloadResult(current.Id, null, "Faulted", false,
                diagnostic: new(exception.Message, exception.StackTrace ?? ""));
            else LogReplacementFailure(current, "Faulted", [], "preparation_exception", exception: exception);
            EndOperation(current);
            return;
        }
        if (_active?.Binding != current.BasisBinding || !ReferenceEquals(_content, current.BasisContent))
        { Discard(prepared); EndOperation(current); return; }
        if (ReportPreparation(prepared) is { } failureStatus)
        {
            if (current.Reload) LogReloadResult(current.Id, prepared, failureStatus, false);
            else LogReplacementFailure(current, failureStatus,
                (prepared.Core as CoreResult<ICoreSessionCandidate>.Failure)?.Problems ?? [],
                (prepared.Content as ContentResult<IContentCandidate>.Faulted)?.Code ??
                    (prepared.Core as CoreResult<ICoreSessionCandidate>.Faulted)?.Fault.Code ??
                    (prepared.Content as ContentResult<IContentCandidate>.Failure)?.Diagnostics.FirstOrDefault()?.Code ??
                    (prepared.Core as CoreResult<ICoreSessionCandidate>.Failure)?.Problems.FirstOrDefault()?.Code,
                (prepared.Core as CoreResult<ICoreSessionCandidate>.Faulted)?.Diagnostic ??
                    (prepared.Content is ContentResult<IContentCandidate>.Faulted contentFault
                        ? new(contentFault.Message, contentFault.StackTrace ?? "") : null));
            Discard(prepared); EndOperation(current); return;
        }
        if (!current.Reload && prepared.Replay is null && (prepared.ContentCandidate!.Report.RuntimeMissing.Length > 0 ||
            prepared.CoreCandidate!.MissingReferences.Length > 0))
        { current.Prepared = prepared; return; }
        Commit(current, prepared);
    }

    private PreparedOperation PrepareNew(GenerationProfileId profile, ulong seed,
        string generatorVersion, CoreSessionOptions options)
    {
        var content = _contentFactory.PrepareLoad(new(_installation, new ContentLoadPurpose.Initial()), _files, _assets);
        if (content is not ContentResult<IContentCandidate>.Success loaded) return new(content, null, null);
        ICoreSessionCandidate? candidate = null;
        try
        {
            var world = _contentFactory.CreateWorldInput(loaded.Value.View, profile, seed, generatorVersion);
            if (world is ContentResult<WorldGenerationInput>.Failure rejected)
            { loaded.Value.Dispose(); return new(new ContentResult<IContentCandidate>.Failure(rejected.Diagnostics), null, null); }
            if (world is ContentResult<WorldGenerationInput>.Faulted faulted)
            { loaded.Value.Dispose(); return new(new ContentResult<IContentCandidate>.Faulted(faulted.Code, faulted.Message, faulted.StackTrace), null, null); }
            var core = _core.PrepareNew(((ContentResult<WorldGenerationInput>.Success)world).Value, loaded.Value.View.Core, options);
            candidate = (core as CoreResult<ICoreSessionCandidate>.Success)?.Value;
            var paired = candidate is not null && candidate.References.Templates.Length > 0
                ? _contentFactory.WithSettlementCopies(loaded.Value.View, candidate.References.Templates) : loaded.Value.View;
            return new(content, core, paired);
        }
        catch { candidate?.Dispose(); loaded.Value.Dispose(); throw; }
    }

    internal PreparedOperation PrepareRestore(string corePayload, string contentPayload, CoreSessionOptions options)
    {
        var state = _core.DecodeState(corePayload);
        if (state is CoreResult<ICoreCapture>.Failure rejected)
            return new(null, new CoreResult<ICoreSessionCandidate>.Failure(rejected.Problems), null);
        if (state is CoreResult<ICoreCapture>.Faulted failed)
            return new(null, new CoreResult<ICoreSessionCandidate>.Faulted(failed.Fault, failed.Diagnostic), null);
        var saved = ((CoreResult<ICoreCapture>.Success)state).Value;
        var embedded = _contentFactory.DecodeCapture(contentPayload);
        if (embedded is ContentResult<IContentCapture>.Failure invalid)
            return new(new ContentResult<IContentCandidate>.Failure(invalid.Diagnostics), null, null);
        if (embedded is ContentResult<IContentCapture>.Faulted faulted)
            return new(new ContentResult<IContentCandidate>.Faulted(faulted.Code, faulted.Message, faulted.StackTrace), null, null);
        var content = _contentFactory.PrepareLoad(new(_installation,
            new ContentLoadPurpose.Restore(((ContentResult<IContentCapture>.Success)embedded).Value, saved.References)), _files, _assets);
        if (content is not ContentResult<IContentCandidate>.Success loaded) return new(content, null, null);
        try { return new(content, _core.PrepareRestore(saved, loaded.Value.View.Core, options), loaded.Value.View)
            { Saved = new(saved.Revision, ((ContentResult<IContentCapture>.Success)embedded).Value.Revision, "") }; }
        catch { loaded.Value.Dispose(); throw; }
    }

    private void Commit(PendingOperation operation, PreparedOperation prepared)
    {
        _insideBoundary = true;
        ICoreSession? activatedCore = null;
        var coreWasCalled = false;
        try
        {
            var candidate = prepared.ContentCandidate!;
            if (operation.Reload)
            {
                if (candidate.Changes == ContentChange.None && _content is not null)
                { LogReloadResult(operation.Id, prepared, "Committed", false); return; }
                if (_active is not null && candidate.Changes.HasFlag(ContentChange.CoreInput))
                {
                    coreWasCalled = true;
                    var result = _active.Core.Submit(new CoreCommand.PublishContent(candidate.View.Core), operation.Source);
                    if (result is SubmissionResult.Completed completed)
                    {
                        var published = CompleteSubmission(completed, candidate);
                        var status = published ? "Committed" : completed.Receipt.Decision is CommandDecision.Rejected
                            ? "Rejected" : "Faulted";
                        LogReloadResult(operation.Id, prepared, status, published,
                            completed.Receipt.Decision is CommandDecision.Rejected rejected ? rejected.Problems : []);
                    }
                    else
                        LogReloadResult(operation.Id, prepared, "NotAccepted", false,
                            [((SubmissionResult.NotAccepted)result).Reason]);
                }
                else
                {
                    _content = candidate.Activate();
                    if (_active is not null) { _active.UseContent(_content); Dispatch([]); }
                    LogReloadResult(operation.Id, prepared, "Committed", true);
                }
            }
            else
            {
                var old = _active;
                ActiveGroup group;
                if (prepared.Replay is { } replay)
                {
                    group = new(replay, ReplayContent(prepared.History, replay.Read().InputIdentity), prepared.History,
                        new(_seconds), operation.Options!.Runtime, prepared.Recording!);
                    prepared.Replay = null;
                }
                else
                {
                    var content = candidate.Activate();
                    activatedCore = prepared.CoreCandidate!.Activate();
                    group = new(activatedCore, content, new(_seconds), operation.Options!.Runtime);
                    group.UseContent(prepared.Paired ?? content);
                    if (operation.Save is { } identity) group.Save = identity;
                    if (!operation.Unsaved && prepared.Saved is { } saved)
                        group.Saved = saved with { Name = group.Save.Name };
                }
                if (group.View.Fault is not null) group.Driver.Freeze();
                _active = group; _content = group.Content;
                operation.Succeeded = true;
                activatedCore = null;
                UpdateFocusPause();
                foreach (var problem in prepared.CoreCandidate?.AdaptationReport ?? [])
                    _logger.Log(LogLevel.Info, "application", "host_state_adapted", problem.Code,
                        new Dictionary<string, object?> { ["operation_id"] = operation.Id, ["code"] = problem.Code,
                            ["field"] = problem.Field, ["subject"] = problem.Subject?.ToString() }, null);
                Dispatch([]);
                try { old?.Dispose(); }
                catch (Exception exception) { LogHostFailure("host_retirement_failed", exception); }
            }
        }
        catch (Exception exception)
        {
            if (coreWasCalled) _active?.Driver.Freeze();
            try { activatedCore?.Dispose(); }
            catch (Exception cleanup) { LogHostFailure("host_cleanup_failed", cleanup); }
            LogHostFailure("host_activation_failed", exception);
            if (operation.Reload) LogReloadResult(operation.Id, prepared, "Faulted", false);
        }
        finally { Discard(prepared); EndOperation(operation); _insideBoundary = false; }
    }

    private bool CompleteSubmission(SubmissionResult.Completed completed, IContentCandidate? target)
    {
        var group = _active!;
        if (target is not null) group.Retain(target.View);
        if (completed.Receipt.Decision is CommandDecision.Accepted accepted)
        {
            try
            {
                var content = target?.Activate() ?? group.Content;
                group.Retain(content);
                if (accepted.SettlementCopies.Length > 0)
                    content = _contentFactory.WithSettlementCopies(content, accepted.SettlementCopies);
                group.UseContent(content); _content = content;
            }
            catch (Exception exception)
            { group.Driver.Freeze(); LogHostFailure("host_pairing_failed", exception); return false; }
        }
        group.View = group.Core.Read();
        if (completed.Receipt.Decision is CommandDecision.Faulted faulted)
            RecordCoreFault(faulted.Fault, completed.Diagnostic, completed.Receipt.Number);
        Dispatch(completed.Events);
        return completed.Receipt.Decision is CommandDecision.Accepted;
    }

    private (uint Completed, bool Stop) AdvanceOne(bool manual)
    {
        var group = _active!;
        var speed = group.Driver.Speed;
        var result = group.Core.Advance(1);
        group.View = group.Core.Read();
        if (result.Fault is not null) RecordCoreFault(result.Fault, result.Diagnostic);
        var action = Dispatch(result.Events);
        return (result.CompletedSteps, group.Driver.Frozen || action == NotificationAction.Pause ||
            _boundaryStop || (!manual && speed > SimulationSpeed.One && action == NotificationAction.SlowToOne));
    }

    private NotificationAction Dispatch(ImmutableArray<EventEnvelope> events)
    {
        var group = _active!;
        var action = NotificationAction.None;
        _boundaryStop = false;
        foreach (var envelope in events)
            try { var next = _notification(envelope.Event); if (next > action) action = next; }
            catch (Exception exception) { LogHostFailure("host_notification_failed", exception); }
        if (action == NotificationAction.Pause) group.Driver.SetPlayerPaused(true);
        else if (action == NotificationAction.SlowToOne) group.Driver.SetSpeed(SimulationSpeed.One);
        LastNotification = action;
        if (BoundaryCompleted is { } handlers)
            foreach (Action<HostBinding, ICoreReadView, IContentView, ImmutableArray<EventEnvelope>> handler in handlers.GetInvocationList())
                try { handler(group.Binding, group.View, group.Content, events); }
                catch (Exception exception) { LogHostFailure("host_consumer_failed", exception); }
        return action;
    }

    private void RecordCoreFault(CoreFault fault, CoreDiagnostic? diagnostic, ulong? commandId = null)
    {
        if (_active!.Driver.Frozen) return;
        _active.Driver.Freeze();
        _logger.Log(LogLevel.Error, "simulation", "simulation_fault", "核心模拟发生内部故障，已冻结在最后完整边界。",
            new Dictionary<string, object?>
            {
                ["fault_code"] = fault.Code, ["operation"] = fault.Operation, ["field"] = fault.Field,
                ["fault_completed_steps"] = fault.Position.CompletedSteps, ["fault_sequence"] = fault.Position.Sequence,
                ["command_id"] = commandId, ["subject"] = fault.Subject?.ToString(),
                ["run_session"] = _active.Binding.Session.ToString(),
                ["diagnostic_message"] = diagnostic?.Message, ["diagnostic_stack_trace"] = diagnostic?.StackTrace
            }, null);
    }

    private string? ReportPreparation(PreparedOperation prepared)
    {
        var diagnostics = prepared.ContentCandidate?.Report.Diagnostics ??
            (prepared.Content as ContentResult<IContentCandidate>.Failure)?.Diagnostics ?? [];
        foreach (var diagnostic in diagnostics)
                _logger.Log(diagnostic.Severity switch { ContentDiagnosticSeverity.Information => LogLevel.Info,
                    ContentDiagnosticSeverity.Warning => LogLevel.Warning, _ => LogLevel.Error },
                    "content", "content_invalid", diagnostic.Message,
                    new Dictionary<string, object?> { ["code"] = diagnostic.Code, ["stage"] = diagnostic.Stage,
                        ["field"] = diagnostic.Field, ["source"] = diagnostic.Source?.ToString(),
                        ["subject"] = diagnostic.Subject?.ToString() }, null);
        if (prepared.Content is ContentResult<IContentCandidate>.Faulted failed)
            _logger.Log(LogLevel.Error, "content", "content_invalid", failed.Message,
                new Dictionary<string, object?> { ["code"] = failed.Code, ["diagnostic_stack_trace"] = failed.StackTrace }, null);
        if (prepared.Replay is not null || prepared.ContentCandidate is not null && (prepared.Core is null || prepared.CoreCandidate is not null)) return null;
        return prepared.Content is ContentResult<IContentCandidate>.Faulted ||
            prepared.Core is CoreResult<ICoreSessionCandidate>.Faulted ? "Faulted" : "Rejected";
    }

    private void LogReplacementFailure(PendingOperation operation, string status,
        ImmutableArray<CoreProblem> problems, string? code, CoreDiagnostic? diagnostic = null, Exception? exception = null) =>
        _logger.Log(status == "Faulted" ? LogLevel.Error : LogLevel.Warning, "application",
            operation.Kind switch { OperationKind.NewGame => "host_new_game_failed", OperationKind.Replay => "host_replay_failed", _ => "host_restore_failed" },
            "候选准备失败，保留原活动组。", new Dictionary<string, object?>
            {
                ["operation_id"] = operation.Id, ["status"] = status, ["code"] = code,
                ["problems"] = problems.Select(problem => new Dictionary<string, object?>
                    { ["code"] = problem.Code, ["field"] = problem.Field, ["subject"] = problem.Subject?.ToString() }).ToArray(),
                ["diagnostic_message"] = diagnostic?.Message, ["diagnostic_stack_trace"] = diagnostic?.StackTrace
            }, exception);

    private void LogReloadResult(long operationId, PreparedOperation? prepared, string status,
        bool changed, ImmutableArray<CoreProblem> problems = default, CoreDiagnostic? diagnostic = null)
    {
        var diagnostics = prepared?.Content is ContentResult<IContentCandidate>.Failure failed
            ? failed.Diagnostics : prepared?.ContentCandidate?.Report.Diagnostics ?? [];
        var warnings = diagnostics.Count(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Warning);
        if (status == "Committed" && warnings > 0) status = "CommittedWithWarnings";
        var attributes = new Dictionary<string, object?>
        {
            ["operation_id"] = operationId, ["status"] = status, ["changed"] = changed,
            ["generation"] = _content?.Generation.Value ?? 0, ["warning_count"] = warnings,
            ["error_count"] = diagnostics.Count(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Error),
            ["code"] = problems.IsDefaultOrEmpty ? (prepared?.Content as ContentResult<IContentCandidate>.Faulted)?.Code : problems[0].Code,
            ["diagnostic_message"] = diagnostic?.Message,
            ["diagnostic_stack_trace"] = diagnostic?.StackTrace,
            ["problems"] = problems.IsDefault ? [] : problems.Select(problem => new Dictionary<string, object?>
                { ["code"] = problem.Code, ["field"] = problem.Field, ["subject"] = problem.Subject?.ToString() }).ToArray()
        };
        if (status is not ("Committed" or "CommittedWithWarnings"))
            _logger.Log(LogLevel.Error, "content", "content_reload_failed", "内容未发布。", attributes, null);
        _logger.Log(LogLevel.Info, "content", "content_reload_summary", "内容重载结束。", attributes, null);
    }

    private void EndOperation(PendingOperation operation)
    {
        if (!ReferenceEquals(_pending, operation)) return;
        _pending = null;
        _active?.Driver.SetOperationLocked(_leaveLocked);
        _skipNextFrame = true;
        NotifyReplacement(operation);
    }
    private void NotifyReplacement(PendingOperation operation)
    {
        if (operation.Reload || ReplacementCompleted is not { } handlers) return;
        foreach (Action<long, bool> handler in handlers.GetInvocationList())
            try { handler(operation.Id, operation.Succeeded); }
            catch (Exception exception) { LogHostFailure("host_consumer_failed", exception); }
    }
    private CoreProblem? MutationProblem(bool allowFrozen = false, bool allowReplay = false) => IsBusy ? Problem("busy") : _active is null
        ? Problem("simulation_session_unavailable") : _active.Replay is not null && !allowReplay ? Problem("replay_read_only")
        : _active.Driver.Frozen && !allowFrozen ? Problem("frozen") : null;
    private static CoreProblem Problem(string code, string field = "host") => new(code, field, null, []);
    private void EnsureOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("宿主操作只能由协调线程调用。");
    }
    private void LogHostFailure(string eventId, Exception exception) =>
        _logger.Log(LogLevel.Error, "application", eventId, "宿主接入发生异常。", null, exception);
    private void Discard(PreparedOperation prepared)
    {
        try { prepared.Dispose(); }
        catch (Exception exception) { LogHostFailure("host_cleanup_failed", exception); }
    }
    private void TakeAndDiscard(PendingOperation operation)
    {
        try { Discard(operation.Work.GetAwaiter().GetResult()); }
        catch (Exception exception) { LogHostFailure("host_abandoned_preparation_failed", exception); }
    }
    private async Task CleanAfterClose(PendingOperation operation)
    {
        try { Discard(await operation.Work.ConfigureAwait(false)); }
        catch (Exception exception) { LogHostFailure("host_abandoned_preparation_failed", exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureOwner();
        if (_pending is { } pending)
        {
            if (pending.Prepared is { } prepared) Discard(prepared);
            else _abandoned.Add(pending);
        }
        _pending = null;
        _disposed = true;
        foreach (var operation in _abandoned) _ = CleanAfterClose(operation);
        _abandoned.Clear();
        _active?.Dispose();
        _active = null; _content = null;
    }

    private sealed class ActiveGroup : IDisposable
    {
        private readonly ICoreSession? _core;
        internal ActiveGroup(ICoreSession core, IContentView content, SimulationFrameDriver driver, RuntimeIdentity runtime)
        { _core = core; Content = content; Driver = driver; Runtime = runtime; View = core.Read(); History.Add(content); }
        internal ActiveGroup(ICoreReplay replay, IContentView content, ImmutableArray<IContentView> history,
            SimulationFrameDriver driver, RuntimeIdentity runtime, ReplayRecording recording)
        { Replay = replay; Content = content; Driver = driver; Runtime = runtime; View = replay.Read();
            History.AddRange(history); ReplayRecording = recording; }
        internal HostBinding Binding { get; } = new(Guid.NewGuid());
        internal ICoreSession Core => _core ?? throw new InvalidOperationException("重放没有核心写入口。");
        internal ICoreReplay? Replay { get; }
        internal ReplayRecording? ReplayRecording { get; }
        internal IContentView Content { get; set; }
        internal ICoreReadView View { get; set; }
        internal SimulationFrameDriver Driver { get; }
        internal SaveIdentity Save { get; set; } = new(Guid.NewGuid(), "未命名存档");
        internal SaveRevision? Saved { get; set; }
        internal RuntimeIdentity Runtime { get; }
        internal List<IContentView> History { get; } = [];
        internal HashSet<CoreInputId> ReleasedInputs { get; } = [];
        internal void Retain(IContentView view)
        {
            ReleasedInputs.Remove(view.Core.Identity);
            if (!History.Any(old => ReferenceEquals(old, view))) History.Add(view);
        }
        internal void UseContent(IContentView view) { Retain(view); Content = view; }
        public void Dispose() { if (Replay is not null) Replay.Dispose(); else Core.Dispose(); }
    }
    private enum OperationKind { NewGame, Restore, Reload, Replay }
    private sealed class PendingOperation(long id, OperationKind kind, HostBinding? basisBinding,
        IContentView? basisContent, Task<PreparedOperation> work, CommandSource source)
    {
        internal long Id { get; } = id;
        internal OperationKind Kind { get; } = kind;
        internal bool Reload => Kind == OperationKind.Reload;
        internal HostBinding? BasisBinding { get; } = basisBinding;
        internal IContentView? BasisContent { get; } = basisContent;
        internal Task<PreparedOperation> Work { get; } = work;
        internal CommandSource Source { get; } = source;
        internal PreparedOperation? Prepared { get; set; }
        internal SaveIdentity? Save { get; init; }
        internal bool Unsaved { get; init; }
        internal bool Succeeded { get; set; }
        internal CoreSessionOptions? Options { get; init; }
    }
    internal sealed record PreparedOperation(ContentResult<IContentCandidate>? Content,
        CoreResult<ICoreSessionCandidate>? Core, IContentView? Paired) : IDisposable
    {
        internal SaveRevision? Saved { get; init; }
        internal ICoreReplay? Replay { get; set; }
        internal ImmutableArray<IContentView> History { get; init; } = [];
        internal ReplayRecording? Recording { get; init; }
        internal IContentCandidate? ContentCandidate => (Content as ContentResult<IContentCandidate>.Success)?.Value;
        internal ICoreSessionCandidate? CoreCandidate => (Core as CoreResult<ICoreSessionCandidate>.Success)?.Value;
        public void Dispose()
        {
            try { Replay?.Dispose(); }
            finally { try { CoreCandidate?.Dispose(); } finally { ContentCandidate?.Dispose(); } }
        }
    }
}

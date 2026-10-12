using System.Collections.Immutable;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using HostLog = System.Action<Train.App.Logging.LogLevel, string, string, string,
    System.Collections.Generic.IReadOnlyDictionary<string, object?>?, System.Exception?>;

namespace Train.App;

internal readonly record struct HostBinding(Guid Session);
internal enum NotificationAction { None, SlowToOne, Pause }
internal sealed record HostStatus(HostBinding Binding, ICoreReadView View,
    bool PlayerPaused, bool FocusPaused, bool OperationLocked, bool IsFrozen,
    SimulationSpeed Speed, double ActualSpeed, double Remainder)
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
    private readonly HostLog _log;
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

    internal HostCoordinator(ICoreFactory core, IContentFactory content, IContentFiles files,
        IAssetInspector assets, ContentInstallation installation, HostLog log,
        Func<CoreEvent, NotificationAction>? notification = null, Func<double>? seconds = null)
    {
        _core = core; _contentFactory = content; _files = files; _assets = assets;
        _installation = installation; _log = log;
        _notification = notification ?? (_ => NotificationAction.None);
        _seconds = seconds;
    }

    internal event Action<HostBinding, ICoreReadView, IContentView, ImmutableArray<EventEnvelope>>? BoundaryCompleted;
    internal IContentView? Content { get { EnsureOwner(); return _content; } }
    internal bool IsBusy { get { EnsureOwner(); return _pending is not null || _insideBoundary; } }
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
            driver.OperationLocked, driver.Frozen, driver.Speed, driver.ActualSpeed, driver.Remainder);
    }

    internal void SetFocused(bool focused) { EnsureOwner(); _focused = focused; UpdateFocusPause(); }
    private void UpdateFocusPause() => _active?.Driver.SetFocusPaused(_autoPauseOnFocusLoss && !_focused);

    internal CoreProblem? SetPaused(bool paused)
    {
        EnsureOwner();
        var problem = MutationProblem();
        if (problem is not null) return problem;
        _active!.Driver.SetPlayerPaused(paused);
        return null;
    }

    internal CoreProblem? SetSpeed(SimulationSpeed speed)
    {
        EnsureOwner();
        var problem = MutationProblem();
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

    internal CoreResult<CoreCheckpoint> CaptureCheckpoint()
    {
        EnsureOwner();
        if (_active is null) return new CoreResult<CoreCheckpoint>.Failure([Problem("simulation_session_unavailable")]);
        var result = CaptureCurrent();
        if (result is CoreResult<ICoreCapture>.Success saved)
            return new CoreResult<CoreCheckpoint>.Success(saved.Value.Checkpoint);
        if (result is CoreResult<ICoreCapture>.Failure rejected)
            return new CoreResult<CoreCheckpoint>.Failure(rejected.Problems);
        var faulted = (CoreResult<ICoreCapture>.Faulted)result;
        return new CoreResult<CoreCheckpoint>.Faulted(faulted.Fault, faulted.Diagnostic);
    }

    private CoreResult<ICoreCapture> CaptureCurrent()
    {
        var group = _active!;
        var result = group.Core.Capture(group.Driver.Frozen ? CapturePurpose.Diagnostic : CapturePurpose.Save);
        if (result is CoreResult<ICoreCapture>.Faulted failed)
        {
            // 只读捕获失败不等于活动核心已冻结，以会话读视图的故障为准。
            group.View = group.Core.Read();
            if (group.View.Fault is { } fault) RecordCoreFault(fault, failed.Diagnostic);
            else
                _log(LogLevel.Error, "simulation", "simulation_checkpoint_failed", failed.Fault.Code,
                    new Dictionary<string, object?> { ["code"] = failed.Fault.Code, ["field"] = failed.Fault.Field,
                        ["operation"] = failed.Fault.Operation, ["diagnostic_message"] = failed.Diagnostic?.Message,
                        ["diagnostic_stack_trace"] = failed.Diagnostic?.StackTrace }, null);
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

    internal (long Operation, CoreProblem? Problem) BeginNewGame(GenerationProfileId profile,
        ulong seed, string generatorVersion, CoreSessionOptions options)
        => Begin(reload: false, () => PrepareNew(profile, seed, generatorVersion, options));

    internal (long Operation, CoreProblem? Problem) BeginRestore(string corePayload,
        string contentPayload, CoreSessionOptions options)
        => Begin(reload: false, () => PrepareRestore(corePayload, contentPayload, options));

    internal (long Operation, CoreProblem? Problem) BeginReload(CommandSource source = CommandSource.Console)
    {
        EnsureOwner();
        if (IsBusy || _abandoned.Any(operation => operation.Reload && !operation.Work.IsCompleted))
            return (0, Problem("busy"));
        var references = new CoreReferenceManifest([], [], [], []);
        if (_active is not null)
        {
            var captured = CaptureCurrent();
            if (captured is CoreResult<ICoreCapture>.Failure rejected) return (0, rejected.Problems[0]);
            if (captured is CoreResult<ICoreCapture>.Faulted failed)
            {
                return (0, _active.Driver.Frozen ? Problem("frozen") :
                    new(failed.Fault.Code, failed.Fault.Field, failed.Fault.Subject, []));
            }
            references = ((CoreResult<ICoreCapture>.Success)captured).Value.References;
        }
        var basis = _content;
        ContentLoadPurpose purpose = basis is null ? new ContentLoadPurpose.Initial()
            : new ContentLoadPurpose.Reload(basis, references);
        return Begin(reload: true, () => new(_contentFactory.PrepareLoad(
            new(_installation, purpose), _files, _assets), null, null), source);
    }

    private (long Operation, CoreProblem? Problem) Begin(bool reload, Func<PreparedOperation> prepare,
        CommandSource source = CommandSource.UserInterface)
    {
        EnsureOwner();
        if (IsBusy) return (0, Problem("busy"));
        var operation = new PendingOperation(checked(++_nextOperation), reload, _active?.Binding,
            _content, Task.Run(prepare), source);
        _pending = operation;
        _active?.Driver.SetOperationLocked(true);
        return (operation.Id, null);
    }

    internal bool Cancel(long operationId)
    {
        EnsureOwner();
        if (_insideBoundary || _pending is not { } operation || operation.Id != operationId) return false;
        _pending = null;
        _active?.Driver.SetOperationLocked(false);
        _skipNextFrame = true;
        if (operation.Prepared is { } prepared) Discard(prepared);
        else _abandoned.Add(operation);
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
        var completedOperation = PumpPreparations();
        var skipAdvance = completedOperation || _skipNextFrame;
        _skipNextFrame = false;
        _insideBoundary = true;
        try
        {
            if (!skipAdvance) _active?.Driver.AdvanceFrame(delta, () => AdvanceOne(manual: false));
        }
        finally { _insideBoundary = false; }
    }

    private bool PumpPreparations()
    {
        foreach (var operation in _abandoned.Where(operation => operation.Work.IsCompleted).ToArray())
        {
            TakeAndDiscard(operation);
            _abandoned.Remove(operation);
        }
        if (_pending is not { Prepared: null } current || !current.Work.IsCompleted) return false;
        PreparedOperation prepared;
        try { prepared = current.Work.GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            _log(LogLevel.Error, "application", "host_preparation_failed", "候选准备失败，保留原活动组。",
                new Dictionary<string, object?> { ["operation_id"] = current.Id }, exception);
            if (current.Reload) LogReloadResult(current, null, "Rejected", false);
            EndOperation(current);
            return true;
        }
        if (_active?.Binding != current.BasisBinding || !ReferenceEquals(_content, current.BasisContent))
        { Discard(prepared); EndOperation(current); return true; }
        if (!ReportPreparation(prepared))
        {
            if (current.Reload) LogReloadResult(current, prepared, "Rejected", false);
            Discard(prepared); EndOperation(current); return true;
        }
        if (!current.Reload && (prepared.ContentCandidate!.Report.RuntimeMissing.Length > 0 ||
            prepared.CoreCandidate!.MissingReferences.Length > 0))
        { current.Prepared = prepared; return true; }
        Commit(current, prepared);
        return true;
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

    private PreparedOperation PrepareRestore(string corePayload, string contentPayload, CoreSessionOptions options)
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
        try { return new(content, _core.PrepareRestore(saved, loaded.Value.View.Core, options), loaded.Value.View); }
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
                { LogReloadResult(operation, prepared, "Committed", false); return; }
                if (_active is not null && candidate.Changes.HasFlag(ContentChange.CoreInput))
                {
                    coreWasCalled = true;
                    var result = _active.Core.Submit(new CoreCommand.PublishContent(candidate.View.Core), operation.Source);
                    if (result is SubmissionResult.Completed completed)
                    {
                        var published = CompleteSubmission(completed, candidate);
                        var status = published ? "Committed" : completed.Receipt.Decision is CommandDecision.Rejected
                            ? "Rejected" : "Faulted";
                        LogReloadResult(operation, prepared, status, published,
                            completed.Receipt.Decision is CommandDecision.Rejected rejected ? rejected.Problems : []);
                    }
                    else
                        LogReloadResult(operation, prepared, "NotAccepted", false,
                            [((SubmissionResult.NotAccepted)result).Reason]);
                }
                else
                {
                    _content = candidate.Activate();
                    if (_active is not null) { _active.Content = _content; Dispatch([]); }
                    LogReloadResult(operation, prepared, "Committed", true);
                }
            }
            else
            {
                var old = _active;
                var content = candidate.Activate();
                activatedCore = prepared.CoreCandidate!.Activate();
                var group = new ActiveGroup(activatedCore, prepared.Paired ?? content, new(_seconds));
                if (group.View.Fault is not null) group.Driver.Freeze();
                _active = group; _content = group.Content;
                activatedCore = null;
                UpdateFocusPause();
                foreach (var problem in prepared.CoreCandidate.AdaptationReport)
                    _log(LogLevel.Info, "application", "host_state_adapted", problem.Code,
                        new Dictionary<string, object?> { ["operation_id"] = operation.Id, ["code"] = problem.Code,
                            ["field"] = problem.Field, ["subject"] = problem.Subject?.ToString() }, null);
                Dispatch([]);
                try { old?.Core.Dispose(); }
                catch (Exception exception) { LogHostFailure("host_retirement_failed", exception); }
            }
        }
        catch (Exception exception)
        {
            if (coreWasCalled) _active?.Driver.Freeze();
            try { activatedCore?.Dispose(); }
            catch (Exception cleanup) { LogHostFailure("host_cleanup_failed", cleanup); }
            LogHostFailure("host_activation_failed", exception);
            if (operation.Reload) LogReloadResult(operation, prepared, "Faulted", false);
        }
        finally { Discard(prepared); EndOperation(operation); _insideBoundary = false; }
    }

    private bool CompleteSubmission(SubmissionResult.Completed completed, IContentCandidate? target)
    {
        var group = _active!;
        if (target is not null) group.History.Add(target.View);
        if (completed.Receipt.Decision is CommandDecision.Accepted accepted)
        {
            try
            {
                var content = target?.Activate() ?? group.Content;
                if (accepted.SettlementCopies.Length > 0)
                    content = _contentFactory.WithSettlementCopies(content, accepted.SettlementCopies);
                if (!ReferenceEquals(content, group.Content)) group.History.Add(content);
                group.Content = content; _content = content;
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
        var result = group.Core.Advance(1);
        group.View = group.Core.Read();
        if (result.Fault is not null) RecordCoreFault(result.Fault, result.Diagnostic);
        var action = Dispatch(result.Events);
        return (result.CompletedSteps, group.Driver.Frozen || action == NotificationAction.Pause ||
            (!manual && action == NotificationAction.SlowToOne));
    }

    private NotificationAction Dispatch(ImmutableArray<EventEnvelope> events)
    {
        var group = _active!;
        var action = NotificationAction.None;
        foreach (var envelope in events) action = (NotificationAction)Math.Max((int)action, (int)_notification(envelope.Event));
        if (action == NotificationAction.Pause) group.Driver.SetPlayerPaused(true);
        else if (action == NotificationAction.SlowToOne) group.Driver.SetSpeed(SimulationSpeed.One);
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
        _log(LogLevel.Error, "simulation", "simulation_fault", "核心模拟发生内部故障，已冻结在最后完整边界。",
            new Dictionary<string, object?>
            {
                ["fault_code"] = fault.Code, ["operation"] = fault.Operation, ["field"] = fault.Field,
                ["fault_completed_steps"] = fault.Position.CompletedSteps, ["fault_sequence"] = fault.Position.Sequence,
                ["command_id"] = commandId, ["subject"] = fault.Subject?.ToString(),
                ["run_session"] = _active.Binding.Session.ToString(),
                ["diagnostic_message"] = diagnostic?.Message, ["diagnostic_stack_trace"] = diagnostic?.StackTrace
            }, null);
    }

    private bool ReportPreparation(PreparedOperation prepared)
    {
        if (prepared.ContentCandidate is { } candidate)
            foreach (var diagnostic in candidate.Report.Diagnostics)
                _log(diagnostic.Severity switch { ContentDiagnosticSeverity.Information => LogLevel.Info,
                    ContentDiagnosticSeverity.Warning => LogLevel.Warning, _ => LogLevel.Error },
                    "content", "content_invalid", diagnostic.Message,
                    new Dictionary<string, object?> { ["code"] = diagnostic.Code, ["stage"] = diagnostic.Stage,
                        ["field"] = diagnostic.Field, ["source"] = diagnostic.Source?.ToString(),
                        ["subject"] = diagnostic.Subject?.ToString() }, null);
        if (prepared.Content is ContentResult<IContentCandidate>.Failure invalid)
            foreach (var diagnostic in invalid.Diagnostics)
                _log(LogLevel.Error, "content", "content_reload_failed", diagnostic.Message,
                    new Dictionary<string, object?> { ["code"] = diagnostic.Code, ["stage"] = diagnostic.Stage,
                        ["field"] = diagnostic.Field, ["source"] = diagnostic.Source?.ToString() }, null);
        if (prepared.Content is ContentResult<IContentCandidate>.Faulted failed)
            _log(LogLevel.Error, "content", "content_reload_failed", failed.Message,
                new Dictionary<string, object?> { ["code"] = failed.Code, ["diagnostic_stack_trace"] = failed.StackTrace }, null);
        if (prepared.Core is CoreResult<ICoreSessionCandidate>.Failure rejected)
            foreach (var problem in rejected.Problems)
                _log(LogLevel.Warning, "application", "host_preparation_rejected", problem.Code,
                    new Dictionary<string, object?> { ["code"] = problem.Code, ["field"] = problem.Field,
                        ["subject"] = problem.Subject?.ToString() }, null);
        if (prepared.Core is CoreResult<ICoreSessionCandidate>.Faulted faulted)
            _log(LogLevel.Error, "application", "host_preparation_failed", faulted.Fault.Code,
                new Dictionary<string, object?> { ["code"] = faulted.Fault.Code, ["field"] = faulted.Fault.Field,
                    ["diagnostic_message"] = faulted.Diagnostic?.Message, ["diagnostic_stack_trace"] = faulted.Diagnostic?.StackTrace }, null);
        return prepared.ContentCandidate is not null &&
            (prepared.Core is null || prepared.CoreCandidate is not null);
    }

    private void LogReloadResult(PendingOperation operation, PreparedOperation? prepared, string status,
        bool changed, ImmutableArray<CoreProblem> problems = default)
    {
        var diagnostics = prepared?.Content is ContentResult<IContentCandidate>.Failure failed
            ? failed.Diagnostics : prepared?.ContentCandidate?.Report.Diagnostics ?? [];
        var warnings = diagnostics.Count(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Warning);
        if (status == "Committed" && warnings > 0) status = "CommittedWithWarnings";
        var attributes = new Dictionary<string, object?>
        {
            ["operation_id"] = operation.Id, ["status"] = status, ["changed"] = changed,
            ["generation"] = _content?.Generation.Value ?? 0, ["warning_count"] = warnings,
            ["error_count"] = diagnostics.Count(diagnostic => diagnostic.Severity == ContentDiagnosticSeverity.Error),
            ["code"] = problems.IsDefaultOrEmpty ? null : problems[0].Code,
            ["problems"] = problems.IsDefault ? [] : problems.Select(problem => new Dictionary<string, object?>
                { ["code"] = problem.Code, ["field"] = problem.Field, ["subject"] = problem.Subject?.ToString() }).ToArray()
        };
        if (status is not ("Committed" or "CommittedWithWarnings"))
            _log(LogLevel.Error, "content", "content_reload_failed", "内容未发布。", attributes, null);
        _log(LogLevel.Info, "content", "content_reload_summary", "内容重载结束。", attributes, null);
    }

    private void EndOperation(PendingOperation operation)
    {
        if (!ReferenceEquals(_pending, operation)) return;
        _pending = null;
        _active?.Driver.SetOperationLocked(false);
        _skipNextFrame = true;
    }
    private CoreProblem? MutationProblem() => IsBusy ? Problem("busy") : _active is null
        ? Problem("simulation_session_unavailable") : _active.Driver.Frozen ? Problem("frozen") : null;
    private static CoreProblem Problem(string code, string field = "host") => new(code, field, null, []);
    private void EnsureOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("宿主操作只能由协调线程调用。");
    }
    private void LogHostFailure(string eventId, Exception exception) =>
        _log(LogLevel.Error, "application", eventId, "宿主接入发生异常。", null, exception);
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
        _active?.Core.Dispose();
        _active = null; _content = null;
    }

    private sealed class ActiveGroup(ICoreSession core, IContentView content, SimulationFrameDriver driver)
    {
        internal HostBinding Binding { get; } = new(Guid.NewGuid());
        internal ICoreSession Core { get; } = core;
        internal IContentView Content { get; set; } = content;
        internal ICoreReadView View { get; set; } = core.Read();
        internal SimulationFrameDriver Driver { get; } = driver;
        // Completed 的目标依赖须保留，拒绝与故障也不能随候选释放；导出由 #322 接入。
        internal List<IContentView> History { get; } = [content];
    }
    private sealed class PendingOperation(long id, bool reload, HostBinding? basisBinding,
        IContentView? basisContent, Task<PreparedOperation> work, CommandSource source)
    {
        internal long Id { get; } = id;
        internal bool Reload { get; } = reload;
        internal HostBinding? BasisBinding { get; } = basisBinding;
        internal IContentView? BasisContent { get; } = basisContent;
        internal Task<PreparedOperation> Work { get; } = work;
        internal CommandSource Source { get; } = source;
        internal PreparedOperation? Prepared { get; set; }
    }
    private sealed record PreparedOperation(ContentResult<IContentCandidate>? Content,
        CoreResult<ICoreSessionCandidate>? Core, IContentView? Paired) : IDisposable
    {
        internal IContentCandidate? ContentCandidate => (Content as ContentResult<IContentCandidate>.Success)?.Value;
        internal ICoreSessionCandidate? CoreCandidate => (Core as CoreResult<ICoreSessionCandidate>.Success)?.Value;
        public void Dispose() { try { CoreCandidate?.Dispose(); } finally { ContentCandidate?.Dispose(); } }
    }
}

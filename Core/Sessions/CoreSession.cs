using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Sessions;

internal sealed record CoreState(SimulationPosition Position, ulong NextCommandNumber,
    CoreRevision Revision, CoreContentProjection Content, WorldState World, CoreFault? Fault = null);

internal abstract record CommandEffect
{
    private CommandEffect() { }
    internal sealed record Accepted(CoreContentProjection Content, ImmutableArray<CoreEvent> Events) : CommandEffect;
    internal sealed record Rejected(ImmutableArray<CoreProblem> Problems) : CommandEffect;
}

internal sealed class CoreSession(CoreState state, int retainedRecordingStarts = 1) : ICoreSession
{
    private bool _disposed;
    private readonly CoreRecording _recording = new(state, retainedRecordingStarts);
    internal CoreState State { get; private set; } = state;

    public ICoreReadView Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new CoreReadView(State.Position, State.Revision, State.Content.Identity, State.Fault, State.World);
    }

    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (command is not CoreCommand.PublishContent publish)
            throw new NotImplementedException($"{command.GetType().Name} 尚未实现。");
        WorldInputs.ValidateContent(publish.Content);
        if (publish.Content.Identity == State.Content.Identity)
            throw new ArgumentException("发布内容的输入身份必须不同于当前身份。", nameof(command));
        return ExecuteCommand(publish, source, _ => new CommandEffect.Accepted(publish.Content, []));
    }

    internal SubmissionResult ExecuteCommand(CoreCommand.PublishContent command, CommandSource source,
        Func<CoreContentProjection, CommandEffect> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var before = State;
        if (before.Fault is not null)
            return new SubmissionResult.NotAccepted(new("frozen", "Fault", null, []));
        if (before.NextCommandNumber == ulong.MaxValue || before.Position.Sequence == ulong.MaxValue)
            return new SubmissionResult.NotAccepted(new("identity_exhausted",
                before.NextCommandNumber == ulong.MaxValue ? "NextCommandNumber" : "Position.Sequence", null, []));

        var payload = CoreCommandEncoding.Encode(command);
        var position = new SimulationPosition(before.Position.CompletedSteps, checked(before.Position.Sequence + 1));
        CoreState after;
        SubmissionResult.Completed result;
        try
        {
            var effect = body(before.Content);
            var decision = effect switch
            {
                CommandEffect.Accepted => (CommandDecision)new CommandDecision.Accepted([], []),
                CommandEffect.Rejected rejected => new CommandDecision.Rejected(rejected.Problems),
                _ => throw new InvalidOperationException("命令结果无效。")
            };
            var events = effect is CommandEffect.Accepted accepted
                ? Envelopes(position, accepted.Events) : ImmutableArray<EventEnvelope>.Empty;
            after = before with
            {
                Position = position,
                NextCommandNumber = checked(before.NextCommandNumber + 1),
                Revision = new(checked(before.Revision.Value + 1)),
                Content = effect is CommandEffect.Accepted change ? change.Content : before.Content
            };
            result = new SubmissionResult.Completed(new(before.NextCommandNumber, before.Position,
                after.Position, decision), events, null);
        }
        catch (Exception exception)
        {
            var fault = new CoreFault("internal_error", position, nameof(Submit), "", null);
            after = before with
            {
                Fault = fault,
                Revision = before.Revision.Value == ulong.MaxValue
                    ? before.Revision : new(checked(before.Revision.Value + 1))
            };
            result = new SubmissionResult.Completed(new(before.NextCommandNumber, before.Position,
                before.Position, new CommandDecision.Faulted(fault)), [],
                new(exception.Message, exception.StackTrace));
        }
        _recording.Record(new(source, payload, result.Receipt, before.Content.Identity, after.Content.Identity));
        State = after;
        if (after.Fault is not null)
            _recording.FreezeFault(after);
        return result;
    }

    public AdvanceResult Advance(uint steps) => Advance(steps, content => (content, []));

    internal AdvanceResult Advance(uint steps,
        Func<CoreContentProjection, (CoreContentProjection Content, ImmutableArray<CoreEvent> Events)> stepBody)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completed = 0u;
        var events = ImmutableArray.CreateBuilder<EventEnvelope>();
        CoreDiagnostic? diagnostic = null;
        while (completed < steps && State.Fault is null)
        {
            var before = State;
            var position = before.Position;
            try
            {
                position = new(checked(before.Position.CompletedSteps + 1), 0);
                var change = stepBody(before.Content);
                var stepEvents = Envelopes(position, change.Events);
                var after = before with
                {
                    Position = position,
                    Revision = new(checked(before.Revision.Value + 1)),
                    Content = change.Content
                };
                events.AddRange(stepEvents);
                State = after;
                completed = checked(completed + 1);
            }
            catch (Exception exception)
            {
                var fault = new CoreFault("internal_error", position, nameof(Advance), "", null);
                State = before with
                {
                    Fault = fault,
                    Revision = before.Revision.Value == ulong.MaxValue
                        ? before.Revision : new(checked(before.Revision.Value + 1))
                };
                _recording.FreezeFault(State);
                diagnostic = new(exception.Message, exception.StackTrace);
            }
        }
        return new(completed, State.Position, events.ToImmutable(), State.Fault, diagnostic);
    }

    private static ImmutableArray<EventEnvelope> Envelopes(SimulationPosition position, ImmutableArray<CoreEvent> events)
        => events.Select((value, ordinal) => new EventEnvelope(position, ordinal, value)).ToImmutableArray();

    public CoreResult<ICoreCapture> Capture(CapturePurpose purpose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return purpose == CapturePurpose.Save && State.Fault is not null
            ? new CoreResult<ICoreCapture>.Failure([new("frozen", "Fault", null, [])])
            : new CoreResult<ICoreCapture>.Success(new CoreCapture(State));
    }

    public CoreRecordingWindow ReadRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _recording.Read(State);
    }

    public CoreResult<RecordingStart> CaptureRecordingStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return State.Fault is not null
            ? new CoreResult<RecordingStart>.Failure([new("frozen", "Fault", null, [])])
            : new CoreResult<RecordingStart>.Success(_recording.CaptureStart(State));
    }

    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _recording.Confirm(point, outcome);
    }

    public void Dispose()
    {
        _disposed = true;
        _recording.Clear();
    }
}

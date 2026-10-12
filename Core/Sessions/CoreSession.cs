using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal sealed record CoreState(SimulationPosition Position, ulong NextCommandNumber,
    CoreRevision Revision, CoreContentProjection Content, CoreFault? Fault = null);

internal abstract record CommandEffect
{
    private CommandEffect() { }
    internal sealed record Accepted(CoreContentProjection Content, ImmutableArray<CoreEvent> Events) : CommandEffect;
    internal sealed record Rejected(ImmutableArray<CoreProblem> Problems) : CommandEffect;
}

internal sealed class CoreSession(CoreState state) : ICoreSession
{
    private bool _disposed;
    internal CoreState State { get; private set; } = state;

    public ICoreReadView Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new CoreReadView(State.Position, State.Revision, State.Content.Identity, State.Fault);
    }

    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (command is not CoreCommand.PublishContent publish)
            throw new NotImplementedException($"{command.GetType().Name} 尚未实现。");
        ArgumentNullException.ThrowIfNull(publish.Content);
        if (publish.Content.Identity == State.Content.Identity)
            throw new ArgumentException("发布内容的输入身份必须不同于当前身份。", nameof(command));
        return ExecuteCommand(_ => new CommandEffect.Accepted(publish.Content, []));
    }

    internal SubmissionResult ExecuteCommand(Func<CoreContentProjection, CommandEffect> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var before = State;
        if (before.Fault is not null)
            return new SubmissionResult.NotAccepted(new("frozen", "Fault", null, []));
        if (before.NextCommandNumber == ulong.MaxValue || before.Position.Sequence == ulong.MaxValue)
            return new SubmissionResult.NotAccepted(new("identity_exhausted",
                before.NextCommandNumber == ulong.MaxValue ? "NextCommandNumber" : "Position.Sequence", null, []));

        var position = new SimulationPosition(before.Position.CompletedSteps, checked(before.Position.Sequence + 1));
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
            var after = before with
            {
                Position = position,
                NextCommandNumber = checked(before.NextCommandNumber + 1),
                Revision = new(checked(before.Revision.Value + 1)),
                Content = effect is CommandEffect.Accepted change ? change.Content : before.Content
            };
            State = after;
            return new SubmissionResult.Completed(new(before.NextCommandNumber, before.Position,
                after.Position, decision), events, null);
        }
        catch (Exception exception)
        {
            var fault = new CoreFault("internal_error", position, nameof(Submit), "", null);
            State = before with
            {
                Fault = fault,
                Revision = before.Revision.Value == ulong.MaxValue
                    ? before.Revision : new(checked(before.Revision.Value + 1))
            };
            return new SubmissionResult.Completed(new(before.NextCommandNumber, before.Position,
                before.Position, new CommandDecision.Faulted(fault)), [],
                new(exception.Message, exception.StackTrace));
        }
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
        throw new NotImplementedException("Capture 尚未实现。");
    }

    public CoreRecordingWindow ReadRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotImplementedException("ReadRecording 尚未实现。");
    }

    public CoreResult<RecordingStart> CaptureRecordingStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotImplementedException("CaptureRecordingStart 尚未实现。");
    }

    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotImplementedException("ConfirmRecordingStart 尚未实现。");
    }

    public void Dispose() => _disposed = true;
}

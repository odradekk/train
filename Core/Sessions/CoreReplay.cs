using System.Collections.Immutable;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Sessions;

internal sealed class CoreReplay(CoreSession session, CoreReplayInput input,
    ImmutableArray<CoreCommand> commands) : ICoreReplay
{
    private int _command;
    private int _checkpoint;
    private bool _disposed;
    private ReplayStop? _terminal;
    private CheckpointDifference? _difference;
    private readonly SimulationPosition _initial = session.State.Position;

    internal static CoreResult<ICoreReplay> Prepare(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> history)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(input.OriginalRuntime);
        if (history.IsDefault || input.Commands.IsDefault || input.Checkpoints.IsDefault)
            throw new ArgumentException("数组必须初始化。");
        foreach (var projection in history)
            WorldInputs.ValidateContent(projection);
        if (input.FormatVersion != 2)
            return Failure("unsupported_format", "FormatVersion");
        var runtimeFields = new[]
        {
            ("GameBuild", input.OriginalRuntime.GameBuild, environment.GameBuild),
            ("SimulationVersion", input.OriginalRuntime.SimulationVersion, environment.SimulationVersion),
            ("OperatingSystem", input.OriginalRuntime.OperatingSystem, environment.OperatingSystem),
            ("Architecture", input.OriginalRuntime.Architecture, environment.Architecture),
            ("DotNetRuntime", input.OriginalRuntime.DotNetRuntime, environment.DotNetRuntime)
        };
        foreach (var (field, expected, actual) in runtimeFields)
            if (!StringComparer.Ordinal.Equals(expected, actual))
                return Failure("incompatible_replay", "OriginalRuntime." + field);
        var decoded = CoreCapture.Decode(input.InitialStatePayload);
        if (decoded is CoreResult<ICoreCapture>.Failure failed)
            return new CoreResult<ICoreReplay>.Failure(failed.Problems);
        var capture = (CoreCapture)((CoreResult<ICoreCapture>.Success)decoded).Value;
        if (capture.Fault is not null)
            return Failure("frozen", "InitialStatePayload.Fault");
        var content = new Dictionary<CoreInputId, CoreContentProjection>();
        foreach (var projection in history)
            content.TryAdd(projection.Identity, projection);
        if (!content.TryGetValue(capture.InputIdentity, out var initial))
            return Failure("content_missing", "InitialStatePayload.InputIdentity");
        var decodedCommands = ImmutableArray.CreateBuilder<CoreCommand>();
        var position = capture.Position;
        for (var index = 0; index < input.Commands.Length; index++)
        {
            var record = input.Commands[index];
            var field = $"Commands[{index}]";
            var target = CoreCommandEncoding.Decode(record.CanonicalPayload);
            if (target is CoreResult<DecodedCommand>.Failure invalid)
                return new CoreResult<ICoreReplay>.Failure(invalid.Problems.Select(problem =>
                    problem with { Field = field + ".CanonicalPayload" + problem.Field }).ToImmutableArray());
            var decodedCommand = ((CoreResult<DecodedCommand>.Success)target).Value;
            var command = decodedCommand.Command;
            if (decodedCommand.InputIdentity is { } identity)
            {
                if (!content.TryGetValue(identity, out var projection))
                    return Failure("content_missing", field + ".CanonicalPayload/input_identity");
                command = new CoreCommand.PublishContent(projection);
            }
            if (!content.ContainsKey(record.InputBefore))
                return Failure("content_missing", field + ".InputBefore");
            if (!content.ContainsKey(record.InputAfter))
                return Failure("content_missing", field + ".InputAfter");
            if (!Enum.IsDefined(record.Source) || !Reachable(position, record.Result.Before)
                || record.Result.Number == 0)
                return Failure("invalid_state", field + ".Result");
            var receipt = record.Result;
            if (receipt.Decision is CommandDecision.Faulted)
            {
                if (receipt.After != receipt.Before || index != input.Commands.Length - 1 || input.ExpectedFault is null)
                    return Failure("invalid_state", field + ".Result.After");
            }
            else if (receipt.Before.Sequence == ulong.MaxValue
                || receipt.After != new SimulationPosition(receipt.Before.CompletedSteps, receipt.Before.Sequence + 1))
                return Failure("invalid_state", field + ".Result.After");
            // 预检整段记录，不能在已经执行前缀后才发现无法解释的决策。
            CoreCheckpoints.ReceiptText(receipt);
            decodedCommands.Add(command!);
            position = receipt.After;
        }
        if (!Reachable(position, input.EndPosition))
            return Failure("invalid_state", "EndPosition");
        if (input.ExpectedFault is { } expectedFault
            && (expectedFault.Operation is not (nameof(ICoreSession.Submit) or nameof(ICoreSession.Advance))
                || expectedFault.Operation == nameof(ICoreSession.Submit)
                    && (input.Commands.IsEmpty || input.Commands[^1].Result.Decision is not CommandDecision.Faulted)))
            return Failure("invalid_state", "ExpectedFault.Operation");
        var previous = capture.Position;
        foreach (var checkpoint in input.Checkpoints)
        {
            var problem = CoreCapture.ValidateCheckpoint(checkpoint);
            if (problem is not null) return new CoreResult<ICoreReplay>.Failure([problem]);
            if (ComparePosition(checkpoint.Position, previous) < 0
                || ComparePosition(checkpoint.Position, input.EndPosition) > 0
                || !Boundary(input, capture.Position, checkpoint.Position))
                return Failure("invalid_state", "Checkpoints.Position");
            previous = checkpoint.Position;
        }
        return new CoreResult<ICoreReplay>.Success(new CoreReplay(
            new CoreSession(new(capture.Position, capture.NextCommandNumber, capture.Revision, initial, capture.World)),
            input, decodedCommands.ToImmutable()));
    }

    public ICoreReadView Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return session.Read();
    }

    public ICoreCapture CaptureBoundary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new CoreCapture(session.State);
    }

    public ReplayAdvanceResult Advance(uint maximumSteps, SimulationPosition? stopAt = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stopAt is { } target && (ComparePosition(target, session.State.Position) < 0
            || ComparePosition(target, input.EndPosition) <= 0 && !Boundary(input,
                _initial, target)))
            throw new ArgumentOutOfRangeException(nameof(stopAt));
        var events = ImmutableArray.CreateBuilder<EventEnvelope>();
        if (_terminal is { } terminal) return Result(terminal, events);
        var completed = 0u;
        while (true)
        {
            var difference = CheckBoundary();
            if (difference is not null)
            {
                _difference = difference;
                return Finish(ReplayStop.FirstDifference, events);
            }
            if (session.State.Fault is { } fault)
                return Finish(fault == input.ExpectedFault ? ReplayStop.RecordedFault : ReplayStop.UnexpectedFault, events);
            if (stopAt == session.State.Position) return Result(ReplayStop.RequestedPosition, events);
            if (_command < commands.Length && input.Commands[_command].Result.Before == session.State.Position)
            {
                var expected = input.Commands[_command];
                if (expected.InputBefore != session.State.Content.Identity)
                    return Different("InputBefore", expected.InputBefore.Value, session.State.Content.Identity.Value, events);
                if (commands[_command] is CoreCommand.PublishContent publish
                    && publish.Content.Identity == session.State.Content.Identity)
                    return Different("CanonicalPayload/input_identity", "different input identity", session.State.Content.Identity.Value, events);
                var beforeCommand = session.State;
                var result = session.Submit(commands[_command], expected.Source);
                if (result is not SubmissionResult.Completed actual)
                    return Different("Result", "Completed", ((SubmissionResult.NotAccepted)result).Reason.Code, events);
                if (expected.Result.Decision is CommandDecision.Faulted && actual.Receipt.Decision is not CommandDecision.Faulted)
                {
                    session.Dispose();
                    session = new CoreSession(beforeCommand);
                }
                else events.AddRange(actual.Events);
                if (session.State.Fault is not null && session.State.Fault != input.ExpectedFault)
                    return Finish(ReplayStop.UnexpectedFault, events);
                var receiptDifference = CoreCheckpoints.Difference(session.State.Position,
                    CoreCheckpoints.ReceiptText(expected.Result), CoreCheckpoints.ReceiptText(actual.Receipt));
                if (receiptDifference is not null)
                {
                    _difference = receiptDifference with { Field = "Result" + receiptDifference.Field };
                    return Finish(ReplayStop.FirstDifference, events);
                }
                if (expected.InputAfter != session.State.Content.Identity)
                    return Different("InputAfter", expected.InputAfter.Value, session.State.Content.Identity.Value, events);
                _command++;
                continue;
            }
            if (_command == commands.Length && session.State.Position == input.EndPosition
                && input.ExpectedFault is null)
                return Finish(ReplayStop.EndOfRecording, events);
            if (completed == maximumSteps) return Result(ReplayStop.BudgetReached, events);
            var before = session.State;
            var advance = session.Advance(1);
            if (advance.Fault is null && ComparePosition(session.State.Position, input.EndPosition) > 0)
            {
                // 验证记录末尾的失败步骤时，未重现故障不能暴露超出录制范围的状态。
                session.Dispose();
                session = new CoreSession(before);
                return Different("Fault", JsonSerializer.Serialize(input.ExpectedFault), "null", events);
            }
            events.AddRange(advance.Events);
            completed += advance.CompletedSteps;
        }
    }

    private CheckpointDifference? CheckBoundary()
    {
        while (_checkpoint < input.Checkpoints.Length)
        {
            var expected = input.Checkpoints[_checkpoint];
            var order = ComparePosition(expected.Position, session.State.Position);
            if (order > 0) return null;
            if (order < 0)
                return new(expected.Position, "/position", JsonSerializer.Serialize(expected.Position),
                    JsonSerializer.Serialize(session.State.Position));
            using var document = JsonDocument.Parse(expected.CanonicalText);
            if (document.RootElement.GetProperty("fault").ValueKind != JsonValueKind.Null && session.State.Fault is null
                && input.ExpectedFault is not null && expected.Position == input.EndPosition)
                return null;
            var difference = CoreCheckpoints.Difference(expected.Position, expected.CanonicalText,
                new CoreCapture(session.State).Checkpoint.CanonicalText);
            if (difference is not null) return difference;
            _checkpoint++;
        }
        return null;
    }

    private ReplayAdvanceResult Different(string field, string expected, string actual,
        ImmutableArray<EventEnvelope>.Builder events)
    {
        _difference = new(session.State.Position, field, expected, actual);
        return Finish(ReplayStop.FirstDifference, events);
    }

    private ReplayAdvanceResult Finish(ReplayStop stop, ImmutableArray<EventEnvelope>.Builder events)
    {
        _terminal = stop;
        return Result(stop, events);
    }

    private ReplayAdvanceResult Result(ReplayStop stop, ImmutableArray<EventEnvelope>.Builder events)
        => new(stop, session.State.Position, events.ToImmutable(), _difference, session.State.Fault);

    private static bool Reachable(SimulationPosition from, SimulationPosition to)
        => to == from || to.CompletedSteps > from.CompletedSteps && to.Sequence == 0;

    private static bool Boundary(CoreReplayInput input, SimulationPosition initial, SimulationPosition position)
        => position == initial || position.Sequence == 0 && position.CompletedSteps > initial.CompletedSteps
            || input.Commands.Any(record => record.Result.Before == position || record.Result.After == position);

    private static int ComparePosition(SimulationPosition left, SimulationPosition right)
        => left.CompletedSteps == right.CompletedSteps ? left.Sequence.CompareTo(right.Sequence)
            : left.CompletedSteps.CompareTo(right.CompletedSteps);

    private static CoreResult<ICoreReplay> Failure(string code, string field)
        => new CoreResult<ICoreReplay>.Failure([new(code, field, null, [])]);

    public void Dispose()
    {
        _disposed = true;
        session.Dispose();
    }
}

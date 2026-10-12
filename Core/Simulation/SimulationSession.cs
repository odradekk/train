
using System.Collections.Immutable;

namespace Train.Core.Simulation;

public sealed class SimulationSession
{
    private PublishedBoundary _published;
    private readonly SimulationCalculations _calculations;
    private readonly SimulationCommandCodec _commands;
    private bool _isRunning;

    public SimulationSession(
        SimulationState initialState, SimulationCalculations calculations, SimulationCommandCodec commands)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(calculations);
        ArgumentNullException.ThrowIfNull(commands);
        _published = new(initialState, CommandLogSnapshot.Empty);
        _calculations = calculations;
        _commands = commands;
    }

    public SimulationState GetState() => _published.State;

    public CommandLogSnapshot GetCommandLog() => _published.Log;

    public AdvanceResult Advance(ulong steps)
    {
        if (_isRunning)
            throw new InvalidOperationException("模拟计算期间不能重入修改入口。");
        _isRunning = true;
        try
        {
            var completed = 0UL;
            var events = ImmutableArray.CreateBuilder<DomainEvent>();
            SimulationFaultDiagnostic? diagnostic = null;
            while (completed < steps && _published.State.Fault is null)
            {
                var before = _published.State;
                if (before.CompletedSteps == ulong.MaxValue || before.TransactionCount == ulong.MaxValue)
                    break;
                var context = new TransactionContext(before.TransactionCount + 1, SimulationOperation.Step,
                    new SimulationPosition(before.CompletedSteps + 1, 0));
                var phase = SimulationPhase.Evaluate;
                try
                {
                    var transition = _calculations.ComputeStep(before, context);
                    phase = SimulationPhase.Validation;
                    CheckTransition(before, transition, context);
                    events.AddRange(transition.Events);
                    _published = new(transition.State, _published.Log);
                    completed++;
                }
                catch (Exception exception)
                {
                    var state = CreateFaultState(before, exception, phase, context);
                    _published = new(state, _published.Log);
                    diagnostic = new(state.Fault!, exception);
                }
            }
            return new AdvanceResult(completed, _published.State, events.ToImmutable(), diagnostic);
        }
        finally
        {
            _isRunning = false;
        }
    }

    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        if (_isRunning)
            throw new InvalidOperationException("模拟计算期间不能重入修改入口。");
        ArgumentNullException.ThrowIfNull(command);
        var publishedBefore = _published;
        var before = publishedBefore.State;
        if (before.Fault is not null)
            return new SubmissionResult.NotAccepted("session_frozen");
        if (before.NextCommandId == ulong.MaxValue)
            return new SubmissionResult.NotAccepted("command_id_exhausted");
        if (before.BoundarySequence == ulong.MaxValue)
            return new SubmissionResult.NotAccepted("boundary_sequence_exhausted");
        if (before.TransactionCount == ulong.MaxValue)
            return new SubmissionResult.NotAccepted("transaction_id_exhausted");

        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        _isRunning = true;
        try
        {
            var payload = _commands.Encode(command);
            var context = new TransactionContext(before.TransactionCount + 1, SimulationOperation.Command,
                new SimulationPosition(before.CompletedSteps, before.BoundarySequence + 1), before.NextCommandId);
            var phase = SimulationPhase.Command;
            try
            {
                var decision = _calculations.ComputeCommand(before, command, context);
                var transition = decision.Transition;
                phase = SimulationPhase.Validation;
                CheckTransition(before, transition, context);
                var outcome = CommandOutcome.Executed;
                string? reason = null;
                if (decision is CommandDecision.Reject rejected)
                {
                    var expected = before with
                    {
                        NextCommandId = before.NextCommandId + 1,
                        BoundarySequence = context.Position.Sequence,
                        TransactionCount = context.TransactionId
                    };
                    if (transition.State != expected || !transition.Events.IsEmpty)
                        throw new SimulationCalculationException("invalid_rejection", SimulationPhase.Validation);
                    outcome = CommandOutcome.Rejected;
                    reason = rejected.Reason;
                }
                else if (decision is not CommandDecision.Execute)
                {
                    throw new SimulationCalculationException("invalid_command_result", SimulationPhase.Validation);
                }

                var execution = new CommandExecution(before.NextCommandId, context.Position, context.TransactionId,
                    source, outcome, reason);
                var log = SimulationCommandLog.Append(publishedBefore.Log, execution, payload);
                _published = new(transition.State, log);
                return new SubmissionResult.Completed(execution, transition.State, transition.Events);
            }
            catch (Exception exception)
            {
                var state = CreateFaultState(before, exception, phase, context);
                var execution = new CommandExecution(before.NextCommandId, context.Position, context.TransactionId,
                    source, CommandOutcome.Faulted, state.Fault!.Code);
                var log = SimulationCommandLog.Append(publishedBefore.Log, execution, payload);
                _published = new(state, log);
                return new SubmissionResult.Completed(execution, state, ImmutableArray<DomainEvent>.Empty,
                    new SimulationFaultDiagnostic(state.Fault!, exception));
            }
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void CheckTransition(SimulationState before, Transition transition, TransactionContext context)
    {
        var violation = _calculations.CheckInvariants(before, transition.State, context);
        if (violation is not null)
            throw new SimulationCalculationException(violation.Code, SimulationPhase.Validation);
        if (transition.Events.IsDefault || transition.Events.Any(domainEvent => domainEvent is null))
            throw new SimulationCalculationException("invalid_events", SimulationPhase.Validation);
    }

    private static SimulationState CreateFaultState(
        SimulationState before, Exception exception, SimulationPhase phase, TransactionContext context)
    {
        // 完整步覆盖实现负责报告其内部阶段；命令和验证的阶段由会话入口确定。
        var fault = exception is SimulationCalculationException calculation
            ? new SimulationFault(calculation.Code,
                context.Operation == SimulationOperation.Step && phase != SimulationPhase.Validation
                    ? calculation.Phase
                    : phase,
                context, calculation.EntityId)
            : new SimulationFault("internal_error", phase, context);
        return SimulationCalculations.CreateFaultState(before, fault);
    }

    private sealed record PublishedBoundary(SimulationState State, CommandLogSnapshot Log);
}

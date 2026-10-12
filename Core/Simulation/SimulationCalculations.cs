
using System.Collections.Immutable;

namespace Train.Core.Simulation;

public abstract record StepRequests;

public abstract record ResolvedStep;

public abstract class SimulationCalculations
{
    public virtual InvariantViolation? CheckInitialState(SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.NextCommandId == 0)
            return new("invalid_command_id", nameof(state.NextCommandId));
        if (state.NextEntityId == 0)
            return new("invalid_entity_id", nameof(state.NextEntityId));
        var commands = state.NextCommandId - 1;
        if (state.BoundarySequence > commands)
            return new("invalid_boundary_sequence", nameof(state.BoundarySequence));
        if (state.CompletedSteps > ulong.MaxValue - commands || state.TransactionCount != state.CompletedSteps + commands)
            return new("invalid_transaction_count", nameof(state.TransactionCount));
        var fault = state.Fault;
        if (fault is null)
            return null;
        if (string.IsNullOrEmpty(fault.Code) || !Enum.IsDefined(fault.Phase) || fault.EntityId == 0)
            return new("invalid_fault", nameof(state.Fault));
        return fault.Context is { } context ? CheckFaultContext(state, context) : null;
    }

    private static InvariantViolation? CheckFaultContext(SimulationState state, TransactionContext context)
    {
        if (state.TransactionCount == ulong.MaxValue || context.TransactionId != state.TransactionCount + 1)
            return new("invalid_fault_context", nameof(state.Fault));
        var validAttempt = context.Operation switch
        {
            SimulationOperation.Command => state.NextCommandId != ulong.MaxValue &&
                state.BoundarySequence != ulong.MaxValue && context.CommandId == state.NextCommandId &&
                context.Position == new SimulationPosition(state.CompletedSteps, state.BoundarySequence + 1),
            SimulationOperation.Step => state.CompletedSteps != ulong.MaxValue && context.CommandId is null &&
                context.Position == new SimulationPosition(state.CompletedSteps + 1, 0),
            _ => false
        };
        return validAttempt ? null : new("invalid_fault_context", nameof(state.Fault));
    }

    public virtual Transition ComputeStep(SimulationState before, TransactionContext context)
    {
        var phase = SimulationPhase.Evaluate;
        try
        {
            var requests = Evaluate(before, context);
            phase = SimulationPhase.Resolve;
            var plan = Resolve(before, requests, context);
            phase = SimulationPhase.Commit;
            var candidate = Commit(before, plan, context) with
            {
                CompletedSteps = checked(before.CompletedSteps + 1),
                BoundarySequence = 0,
                TransactionCount = checked(before.TransactionCount + 1)
            };
            return new Transition(candidate, ImmutableArray<DomainEvent>.Empty);
        }
        catch (SimulationCalculationException exception)
        {
            throw new SimulationCalculationException(exception.Code, phase, exception.EntityId, exception);
        }
        catch (Exception exception)
        {
            throw new SimulationCalculationException("calculation_failed", phase, innerException: exception);
        }
    }

    public abstract StepRequests Evaluate(SimulationState before, TransactionContext context);

    public abstract ResolvedStep Resolve(
        SimulationState before, StepRequests requests, TransactionContext context);

    public abstract SimulationState Commit(
        SimulationState before, ResolvedStep plan, TransactionContext context);

    public abstract CommandDecision ComputeCommand(
        SimulationState before, CoreCommand command, TransactionContext context);

    public static EntityAllocation? AllocateEntityId(SimulationState candidate)
    {
        if (candidate.NextEntityId == 0)
            throw new SimulationCalculationException("invalid_entity_id", SimulationPhase.Commit);
        return candidate.NextEntityId == ulong.MaxValue
            ? null
            : new EntityAllocation(candidate.NextEntityId,
                candidate with { NextEntityId = candidate.NextEntityId + 1 });
    }

    public virtual InvariantViolation? CheckInvariants(
        SimulationState before, SimulationState candidate, TransactionContext context)
    {
        if (candidate.GetType() != before.GetType())
            return new("state_type_changed", nameof(SimulationState));
        if (candidate.WorldSeed != before.WorldSeed)
            return new("world_seed_changed", nameof(SimulationState.WorldSeed));
        if (!string.Equals(candidate.GeneratorVersion, before.GeneratorVersion, StringComparison.Ordinal))
            return new("generator_version_changed", nameof(SimulationState.GeneratorVersion));
        if (before.Fault is not null || candidate.Fault is not null)
            return new("unexpected_fault", nameof(SimulationState.Fault));
        if (before.NextEntityId == 0 || candidate.NextEntityId == 0)
            return new("invalid_entity_id", nameof(SimulationState.NextEntityId));
        if (candidate.NextEntityId < before.NextEntityId)
            return new("entity_id_reused", nameof(SimulationState.NextEntityId));
        if (before.NextCommandId == 0 || candidate.NextCommandId == 0)
            return new("invalid_command_id", nameof(SimulationState.NextCommandId));
        if (before.TransactionCount == ulong.MaxValue || context.TransactionId != before.TransactionCount + 1)
            return new("invalid_transaction_id", nameof(TransactionContext.TransactionId));
        if (candidate.TransactionCount != context.TransactionId)
            return new("invalid_transaction_count", nameof(SimulationState.TransactionCount));

        switch (context.Operation)
        {
            case SimulationOperation.Step:
                if (context.CommandId is not null)
                    return new("unexpected_command_id", nameof(TransactionContext.CommandId));
                if (before.CompletedSteps == ulong.MaxValue ||
                    candidate.CompletedSteps != before.CompletedSteps + 1)
                    return new("invalid_step_count", nameof(SimulationState.CompletedSteps));
                if (candidate.BoundarySequence != 0)
                    return new("invalid_boundary_sequence", nameof(SimulationState.BoundarySequence));
                if (candidate.NextCommandId != before.NextCommandId)
                    return new("invalid_command_id", nameof(SimulationState.NextCommandId));
                break;
            case SimulationOperation.Command:
                if (context.CommandId != before.NextCommandId)
                    return new("invalid_command_id", nameof(TransactionContext.CommandId));
                if (before.NextCommandId == ulong.MaxValue || candidate.NextCommandId != before.NextCommandId + 1)
                    return new("invalid_command_id", nameof(SimulationState.NextCommandId));
                if (candidate.CompletedSteps != before.CompletedSteps)
                    return new("invalid_step_count", nameof(SimulationState.CompletedSteps));
                if (before.BoundarySequence == ulong.MaxValue ||
                    candidate.BoundarySequence != before.BoundarySequence + 1)
                    return new("invalid_boundary_sequence", nameof(SimulationState.BoundarySequence));
                break;
            default:
                return new("invalid_operation", nameof(TransactionContext.Operation));
        }

        return context.Position == new SimulationPosition(candidate.CompletedSteps, candidate.BoundarySequence)
            ? null
            : new("invalid_position", nameof(TransactionContext.Position));
    }

    public static SimulationState CreateFaultState(SimulationState before, SimulationFault fault) =>
        before with { Fault = fault };
}

public sealed class SimulationCalculationException : Exception
{
    public string Code { get; }

    public SimulationPhase Phase { get; }

    public ulong? EntityId { get; }

    public SimulationCalculationException(
        string code, SimulationPhase phase, ulong? entityId = null, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
        Phase = phase;
        EntityId = entityId;
    }
}

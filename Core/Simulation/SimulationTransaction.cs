
using System.Collections.Immutable;

namespace Train.Core.Simulation;

public readonly record struct TransactionContext(
    ulong TransactionId,
    SimulationOperation Operation,
    SimulationPosition Position,
    ulong? CommandId = null);

public abstract record DomainEvent;

public sealed record Transition(SimulationState State, ImmutableArray<DomainEvent> Events);

public sealed record SimulationFaultDiagnostic(SimulationFault Fault, Exception Exception);

public sealed record AdvanceResult(
    ulong StepsAdvanced,
    SimulationState StateAfter,
    ImmutableArray<DomainEvent> Events,
    SimulationFaultDiagnostic? Diagnostic = null);

public sealed record EntityAllocation(ulong EntityId, SimulationState Candidate);

public sealed record InvariantViolation(string Code, string Field);

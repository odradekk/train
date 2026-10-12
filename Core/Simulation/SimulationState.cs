
namespace Train.Core.Simulation;

public record SimulationState(ulong WorldSeed, string GeneratorVersion)
{
    public ulong CompletedSteps { get; init; }

    public ulong BoundarySequence { get; init; }

    public ulong NextCommandId { get; init; } = 1;

    public ulong NextEntityId { get; init; } = 1;

    public ulong TransactionCount { get; init; }

    public SimulationFault? Fault { get; init; }
}

public readonly record struct SimulationPosition(ulong CompletedSteps, ulong Sequence)
    : IComparable<SimulationPosition>
{
    public int CompareTo(SimulationPosition other)
    {
        var stepOrder = CompletedSteps.CompareTo(other.CompletedSteps);
        return stepOrder != 0 ? stepOrder : Sequence.CompareTo(other.Sequence);
    }
}

public enum SimulationOperation
{
    Command,
    Step
}

public enum SimulationPhase
{
    Boundary,
    Command,
    Evaluate,
    Resolve,
    Commit,
    Validation
}

public sealed record SimulationFault(
    string Code,
    SimulationPhase Phase,
    TransactionContext? Context,
    ulong? EntityId = null);

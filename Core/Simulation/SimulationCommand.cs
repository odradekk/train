
using System.Collections.Immutable;

namespace Train.Core.Simulation;

public abstract record CoreCommand;

public enum CommandSource
{
    Ui,
    Console,
    Automation
}

public enum CommandOutcome
{
    Executed,
    Rejected,
    Faulted
}

public abstract record CommandDecision(Transition Transition)
{
    public sealed record Execute(Transition Result) : CommandDecision(Result);

    public sealed record Reject(Transition Result, string Reason) : CommandDecision(Result);
}

public sealed record CommandExecution(
    ulong CommandId,
    SimulationPosition Position,
    ulong TransactionId,
    CommandSource Source,
    CommandOutcome Outcome,
    string? Reason = null);

public abstract record SubmissionResult
{
    public sealed record NotAccepted(string Reason) : SubmissionResult;

    public sealed record Completed(
        CommandExecution Execution,
        SimulationState StateAfter,
        ImmutableArray<DomainEvent> Events,
        SimulationFaultDiagnostic? Diagnostic = null) : SubmissionResult;
}

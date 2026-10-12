
using System.Collections.Immutable;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

internal sealed record ResourceState() : SimulationState(42, "test-generator-v1")
{
    public ulong Stock { get; init; } = 5;
    public ImmutableDictionary<ulong, ulong> Holdings { get; init; } =
        ImmutableDictionary<ulong, ulong>.Empty;
    public ImmutableSortedSet<ulong> EntityIds { get; init; } = ImmutableSortedSet<ulong>.Empty;
    public ImmutableArray<ResourceDemand> Demands { get; init; } = ImmutableArray<ResourceDemand>.Empty;
}

internal sealed record ResourceDemand(
    ulong EntityId, ulong RequestId, int Priority, ulong Amount, bool AllowPartial = true);

internal sealed record ResourceRequests(ImmutableArray<ResourceDemand> Items) : StepRequests;

internal sealed record ResourceGrant(ulong EntityId, ulong RequestId, ulong Amount);

internal sealed record ResourcePlan(ImmutableArray<ResourceGrant> Grants) : ResolvedStep;

internal sealed class ResourceCalculations : SimulationCalculations
{
    public override InvariantViolation? CheckInitialState(SimulationState state)
    {
        var common = base.CheckInitialState(state);
        if (common is not null)
            return common;
        var resources = (ResourceState)state;
        if (resources.EntityIds.Any(id => id == 0 || id >= resources.NextEntityId))
            return new("invalid_entity_id", nameof(ResourceState.EntityIds));
        if (resources.Holdings.Keys.Any(id => !resources.EntityIds.Contains(id)))
            return new("missing_entity_reference", nameof(ResourceState.Holdings));
        if (resources.Demands.Any(demand => !resources.EntityIds.Contains(demand.EntityId)))
            return new("missing_entity_reference", nameof(ResourceState.Demands));
        if (resources.Demands.Select(demand => (demand.EntityId, demand.RequestId)).Distinct().Count() != resources.Demands.Length)
            return new("duplicate_request_identity", nameof(ResourceState.Demands));
        return null;
    }

    public override StepRequests Evaluate(SimulationState before, TransactionContext context) =>
        new ResourceRequests(((ResourceState)before).Demands);

    public override ResolvedStep Resolve(
        SimulationState before, StepRequests requests, TransactionContext context)
    {
        var remaining = ((ResourceState)before).Stock;
        var grants = ImmutableArray.CreateBuilder<ResourceGrant>();

        foreach (var demand in ((ResourceRequests)requests).Items
                     .OrderBy(demand => demand.Priority)
                     .ThenBy(demand => demand.EntityId)
                     .ThenBy(demand => demand.RequestId))
        {
            var amount = demand.AllowPartial ? Math.Min(demand.Amount, remaining) : demand.Amount;
            if (amount == 0 || amount > remaining)
                continue;

            grants.Add(new ResourceGrant(demand.EntityId, demand.RequestId, amount));
            remaining -= amount;
        }

        return new ResourcePlan(grants.ToImmutable());
    }

    public override SimulationState Commit(
        SimulationState before, ResolvedStep plan, TransactionContext context)
    {
        var state = (ResourceState)before;
        var stock = state.Stock;
        var holdings = state.Holdings;

        foreach (var grant in ((ResourcePlan)plan).Grants)
        {
            stock -= grant.Amount;
            holdings = holdings.SetItem(
                grant.EntityId, checked(holdings.GetValueOrDefault(grant.EntityId) + grant.Amount));
        }

        return state with { Stock = stock, Holdings = holdings };
    }

    public override CommandDecision ComputeCommand(
        SimulationState before, CoreCommand command, TransactionContext context)
    {
        var state = (ResourceState)before;
        var completed = state with
        {
            NextCommandId = checked(context.CommandId!.Value + 1),
            BoundarySequence = context.Position.Sequence,
            TransactionCount = context.TransactionId
        };
        var noEvents = ImmutableArray<DomainEvent>.Empty;

        return command switch
        {
            NoChangeCommand => Execute(completed),
            CreateEntityCommand => Create(state, completed),
            DeleteEntityCommand delete when !state.EntityIds.Contains(delete.EntityId) =>
                Reject(completed, "target_missing"),
            DeleteEntityCommand delete =>
                Execute(completed with { EntityIds = state.EntityIds.Remove(delete.EntityId) }),
            CreateThenRejectCommand => Reject(completed, "requirements_not_met"),
            InvalidCandidateCommand => Execute(completed with { NextEntityId = 0 }),
            _ => throw new InvalidOperationException($"未知测试命令：{command.GetType().Name}。")
        };

        CommandDecision Execute(ResourceState candidate) =>
            new CommandDecision.Execute(new Transition(candidate, noEvents));

        CommandDecision Reject(ResourceState candidate, string reason) =>
            new CommandDecision.Reject(new Transition(candidate, noEvents), reason);

        CommandDecision Create(ResourceState original, ResourceState boundary)
        {
            var allocation = AllocateEntityId(original);
            if (allocation is null)
                return Reject(boundary, "entity_id_exhausted");

            var allocated = (ResourceState)allocation.Candidate;
            return Execute(allocated with
            {
                EntityIds = original.EntityIds.Add(allocation.EntityId),
                NextCommandId = boundary.NextCommandId,
                BoundarySequence = boundary.BoundarySequence,
                TransactionCount = boundary.TransactionCount
            });
        }
    }
}

internal sealed record NoChangeCommand : CoreCommand;
internal sealed record CreateEntityCommand : CoreCommand;
internal sealed record DeleteEntityCommand(ulong EntityId) : CoreCommand;
internal sealed record CreateThenRejectCommand : CoreCommand;
internal sealed record InvalidCandidateCommand : CoreCommand;

internal sealed record MarkerEvent(ulong Number) : DomainEvent;

internal sealed class ScriptedCalculations : SimulationCalculations
{
    public List<(SimulationState Before, CoreCommand Command, TransactionContext Context)> CommandInputs { get; } = [];

    public Func<SimulationState, TransactionContext, Transition> Step { get; init; } = StepResult;
    public Func<SimulationState, CoreCommand, TransactionContext, CommandDecision> Command { get; init; } =
        (_, _, _) => throw new InvalidOperationException("测试未指定命令结果。");

    public override Transition ComputeStep(SimulationState before, TransactionContext context) =>
        Step(before, context);

    public override StepRequests Evaluate(SimulationState before, TransactionContext context) =>
        throw new InvalidOperationException("预设计算只通过 Step 委托计算完整模拟步。");

    public override ResolvedStep Resolve(
        SimulationState before, StepRequests requests, TransactionContext context) =>
        throw new InvalidOperationException("预设计算只通过 Step 委托计算完整模拟步。");

    public override SimulationState Commit(
        SimulationState before, ResolvedStep plan, TransactionContext context) =>
        throw new InvalidOperationException("预设计算只通过 Step 委托计算完整模拟步。");

    public override CommandDecision ComputeCommand(
        SimulationState before, CoreCommand command, TransactionContext context)
    {
        CommandInputs.Add((before, command, context));
        return Command(before, command, context);
    }

    public static Transition StepResult(SimulationState before, TransactionContext context) =>
        new(before with
        {
            CompletedSteps = context.Position.CompletedSteps,
            BoundarySequence = context.Position.Sequence,
            TransactionCount = context.TransactionId
        }, ImmutableArray<DomainEvent>.Empty);

    public static ScriptedCalculations WithCommands(params CommandDecision[] decisions)
    {
        var remaining = new Queue<CommandDecision>(decisions);
        return new ScriptedCalculations { Command = (_, _, _) => remaining.Dequeue() };
    }
}

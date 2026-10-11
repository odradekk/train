// Train.Core.Tests.Simulation 提供运行边界的不可变输入输出例子。
// 资源领域计算只用于固定测试例子；预设结果夹具用于隔离会话测试。

using System.Collections.Immutable;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

/// <summary>测试用资源状态；库存和持有量按整数单位计，实体集合按 ID 排序。</summary>
internal sealed record ResourceState() : SimulationState(42, "test-generator-v1")
{
    public ulong Stock { get; init; } = 5;
    public ImmutableDictionary<ulong, ulong> Holdings { get; init; } =
        ImmutableDictionary<ulong, ulong>.Empty;
    public ImmutableSortedSet<ulong> EntityIds { get; init; } = ImmutableSortedSet<ulong>.Empty;
    public ImmutableArray<ResourceDemand> Demands { get; init; } = ImmutableArray<ResourceDemand>.Empty;
}

/// <summary>资源例子的需求；较小的 Priority 表示较高优先级，RequestId 是提交前确定的身份。</summary>
internal sealed record ResourceDemand(
    ulong EntityId, ulong RequestId, int Priority, ulong Amount, bool AllowPartial = true);

/// <summary>资源例子的请求集合；Items 的枚举顺序不能决定授予结果。</summary>
internal sealed record ResourceRequests(ImmutableArray<ResourceDemand> Items) : StepRequests;

/// <summary>资源例子中某个稳定请求获准的数量。</summary>
internal sealed record ResourceGrant(ulong EntityId, ulong RequestId, ulong Amount);

/// <summary>按优先级与稳定身份裁决后得到的资源执行计划。</summary>
internal sealed record ResourcePlan(ImmutableArray<ResourceGrant> Grants) : ResolvedStep;

/// <summary>实现测试资源例子的请求、裁决、提交和命令计算。</summary>
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

/// <summary>只消耗执行身份、不修改业务状态的测试命令。</summary>
internal sealed record NoChangeCommand : CoreCommand;
/// <summary>从当前候选分配器创建一个测试实体。</summary>
internal sealed record CreateEntityCommand : CoreCommand;
/// <summary>删除指定测试实体；目标消失时拒绝，不能改选其他实体。</summary>
internal sealed record DeleteEntityCommand(ulong EntityId) : CoreCommand;
/// <summary>临时创建实体后因前提不满足而整体拒绝的复合测试命令。</summary>
internal sealed record CreateThenRejectCommand : CoreCommand;
/// <summary>用于隔离测试的损坏候选输入，不得作为正常状态继续运行。</summary>
internal sealed record InvalidCandidateCommand : CoreCommand;

/// <summary>表示成功事务发布的测试标记；Number 是预先确定的标记身份。</summary>
internal sealed record MarkerEvent(ulong Number) : DomainEvent;

/// <summary>向会话提供预设候选结果或异常，隔离领域算法，验证会话本身的运行契约。</summary>
/// <remarks>步结果只复制会话给出的身份，不计算下一个身份；断言用独立常量检查会话分配是否正确。</remarks>
internal sealed class ScriptedCalculations : SimulationCalculations
{
    /// <summary>记录实际送入命令计算的完整起点、命令值及身份，供输入合同断言使用。</summary>
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

    /// <summary>把入口给出的完整下一边界复制到预设结果，保留全部领域数据。</summary>
    public static Transition StepResult(SimulationState before, TransactionContext context) =>
        new(before with
        {
            CompletedSteps = context.Position.CompletedSteps,
            BoundarySequence = context.Position.Sequence,
            TransactionCount = context.TransactionId
        }, ImmutableArray<DomainEvent>.Empty);

    /// <summary>按已给出的结果顺序回答命令；重复调用或自动重试会耗尽队列并使测试失败。</summary>
    public static ScriptedCalculations WithCommands(params CommandDecision[] decisions)
    {
        var remaining = new Queue<CommandDecision>(decisions);
        return new ScriptedCalculations { Command = (_, _, _) => remaining.Dequeue() };
    }
}

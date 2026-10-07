// Train.Tests.Core.Simulation 提供运行边界的不可变输入输出例子。
// 资源领域仍是待填模板；预设结果夹具只控制测试输入，不实现任何生产计算。

using System.Collections.Immutable;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

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

/// <summary>本轮只给出资源领域的计算模板，输入输出例子见 SimulationCalculationTests。</summary>
internal sealed class ResourceCalculations : SimulationCalculations
{
    public override StepRequests Evaluate(SimulationState before, TransactionContext context) =>
        throw new NotImplementedException();

    public override ResolvedStep Resolve(
        SimulationState before, StepRequests requests, TransactionContext context) =>
        throw new NotImplementedException();

    public override SimulationState Commit(
        SimulationState before, ResolvedStep plan, TransactionContext context) =>
        throw new NotImplementedException();

    public override CommandDecision ComputeCommand(
        SimulationState before, CoreCommand command, TransactionContext context) =>
        throw new NotImplementedException();
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

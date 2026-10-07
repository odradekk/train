// Train.Core.Simulation 计算完整模拟步、实体分配和事务后继是否合法。
// 共享计算只操作运行边界；请求、仲裁、业务提交和命令由具体领域实现。

using System.Collections.Immutable;

namespace Train.Core.Simulation;

/// <summary>表示 Evaluate 产生的不可变领域请求；具体字段由相应规则定义。</summary>
/// <remarks>集合身份和序列顺序必须明确，请求身份不得由遍历下标产生。</remarks>
/// <example>测试夹具提出实体 10 请求 3、实体 20 请求 4，但尚不授予库存。</example>
public abstract record StepRequests;

/// <summary>表示 Resolve 产生的不可变执行计划；Commit 使用此计划而不重复仲裁。</summary>
/// <remarks>计划包含具体规则确定的获准结果及稳定执行顺序。</remarks>
/// <example>库存为 5 且允许部分分配时，测试夹具的计划是实体 10 得 3、实体 20 得 2。</example>
public abstract record ResolvedStep;

/// <summary>给出已提交状态到候选状态的计算约定，供会话和领域计算共同使用。</summary>
/// <remarks>领域实现必须提供四个业务计算。计算不能修改输入状态、发布事件或重入会话；内部异常交给会话冻结处理。</remarks>
/// <example>同一完整起点执行三个单步或一次三步推进，最终边界应一致。</example>
public abstract class SimulationCalculations
{
    /// <summary>计算一个完整创建或恢复起点的首个不变量错误。</summary>
    /// <param name="state">完整候选起点，不是从 before 到 candidate 的后继。</param>
    /// <returns>合法时为 null，否则为按固定检查顺序找到的首个字段错误。</returns>
    /// <remarks>领域覆盖时先调用基础检查，再检查业务字段；不修改状态。Fault 的尝试身份须对应此边界下一次事务，不计入完成数。</remarks>
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

    /// <summary>检查故障尝试身份与冻结前完整边界一致，不据故障阶段推断事务种类。</summary>
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

    /// <summary>计算一个完整固定模拟步的候选状态与待发布事件。</summary>
    /// <param name="before">本步开始时的完整状态，各阶段读取同一已提交起点。</param>
    /// <param name="context">入口为本步计算的事务号和下一边界位置，Operation 必须为 Step。</param>
    /// <returns>完成所有阶段后的候选状态：步号加一、步内序号归零、事务计数增加一次。</returns>
    /// <remarks>默认执行 Evaluate → Resolve → Commit，返回空事件。领域增加其他阶段或事件计算时须纳入同一完整步，可覆盖此方法返回完整候选，并负责准确报告内部失败阶段。发布由会话负责。</remarks>
    /// <exception cref="SimulationCalculationException">领域阶段发生内部异常，保留稳定代码和实际失败阶段。</exception>
    /// <example>起点为 (60, 2)，成功后候选边界为 (61, 0)；见 SimulationCalculationTests.Step_computes_a_new_boundary_from_the_same_committed_input。</example>
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

    /// <summary>根据已提交状态计算本步应提出的行为和资源请求。</summary>
    /// <param name="before">完整已提交状态，不读取候选修改。</param>
    /// <param name="context">本步稳定身份，用于周期规则和带键随机等领域计算。</param>
    /// <returns>尚未仲裁的不可变请求；资源不足不会阻止提出请求。</returns>
    /// <example>库存 5 仍提出需求 3 和 4，而不是先到者拿走库存；见 SimulationCalculationTests.Evaluate_describes_demand_without_allocating_stock。</example>
    public abstract StepRequests Evaluate(SimulationState before, TransactionContext context);

    /// <summary>计算竞争请求各自获准的结果和稳定执行顺序。</summary>
    /// <param name="before">与 Evaluate 相同的完整起点，用于读取共享资源。</param>
    /// <param name="requests">本步全部请求；不能用其枚举顺序决定资源归属。</param>
    /// <param name="context">本步稳定身份。</param>
    /// <returns>按业务优先级再按稳定身份裁决的执行计划，区分完整授予与部分授予规则。</returns>
    /// <example>同优先级、允许部分分配：库存 5，实体 10 要 3、实体 20 要 4，结果为 3、2。</example>
    public abstract ResolvedStep Resolve(
        SimulationState before, StepRequests requests, TransactionContext context);

    /// <summary>将获准计划计算为候选状态，不重新仲裁或发布。</summary>
    /// <param name="before">本步的完整起点。</param>
    /// <param name="plan">已确定顺序和获准数量的执行计划。</param>
    /// <param name="context">本步稳定身份。</param>
    /// <returns>包含获准业务修改的候选状态；完整步的边界计数由 ComputeStep 完成。</returns>
    /// <example>将授予 3、2 应用到库存 5，候选库存为零，旧状态仍为 5。</example>
    public abstract SimulationState Commit(
        SimulationState before, ResolvedStep plan, TransactionContext context);

    /// <summary>计算一条命令的全部业务修改或预期拒绝，以及对应的边界计数变化。</summary>
    /// <param name="before">命令执行前的完整状态。</param>
    /// <param name="command">已解析的不可变领域命令。</param>
    /// <param name="context">入口分配的命令号、位置及事务号，Operation 必须为 Command。</param>
    /// <returns>成功候选结果，或保留业务状态与实体分配器、只消耗执行身份的拒绝结果。</returns>
    /// <remarks>内部异常不能变成普通拒绝。复合命令任一部分拒绝时丢弃全部业务修改；拒绝候选须从 before 复制，仅改变执行身份字段，沿用其不可变业务集合。</remarks>
    /// <example>删除已消失的实体返回 target_missing，后续独立命令仍可执行。</example>
    public abstract CommandDecision ComputeCommand(
        SimulationState before, CoreCommand command, TransactionContext context);

    /// <summary>从候选状态的实体分配器取得一个 ID 并计算新的候选值。</summary>
    /// <param name="candidate">事务内的不可变候选状态，NextEntityId 必须非零。</param>
    /// <returns>分配结果；下一 ID 无法递增时为 null，不消耗 ID 或改变输入。</returns>
    /// <exception cref="SimulationCalculationException">分配器为零，属于损坏的候选状态。</exception>
    /// <example>NextEntityId 为 7 时返回 ID 7 和下一 ID 为 8 的候选状态；达到 ulong.MaxValue 时返回 null。</example>
    public static EntityAllocation? AllocateEntityId(SimulationState candidate)
    {
        if (candidate.NextEntityId == 0)
            throw new SimulationCalculationException("invalid_entity_id", SimulationPhase.Commit);
        return candidate.NextEntityId == ulong.MaxValue
            ? null
            : new EntityAllocation(candidate.NextEntityId,
                candidate with { NextEntityId = candidate.NextEntityId + 1 });
    }

    /// <summary>计算候选状态是否构成合法的命令或模拟步后继。</summary>
    /// <param name="before">本次事务的完整起点。</param>
    /// <param name="candidate">包含本次业务修改及边界计数的候选状态。</param>
    /// <param name="context">本次事务应使用的身份和后继位置。</param>
    /// <returns>按固定顺序找到的首个不变量错误；合法时为 null。</returns>
    /// <remarks>下一命令号和实体 ID 的 ulong.MaxValue 是合法耗尽状态，零非法。种子、生成器版本和状态类型不能改变；实体分配器不能后退。领域扩展须先调用此基础检查。</remarks>
    /// <example>候选 NextEntityId 为零，报告 NextEntityId；见 SimulationCalculationTests.Invariant_check_rejects_an_invalid_entity_allocator。</example>
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

    /// <summary>由执行前状态计算只附有故障的最后完整边界。</summary>
    /// <param name="before">失败事务开始前的完整状态，业务状态和所有计数保持此值。</param>
    /// <param name="fault">稳定故障信息，不包含异常消息、调用栈或墙钟。</param>
    /// <returns>保持执行前全部权威数据、只增加故障字段的不可变状态。</returns>
    /// <example>命令尝试创建实体后故障，恢复原 NextEntityId，再记录故障并冻结。</example>
    public static SimulationState CreateFaultState(SimulationState before, SimulationFault fault) =>
        before with { Fault = fault };
}

/// <summary>携带稳定错误代码和阶段的内部计算异常，供会话恢复完整起点后记录故障。</summary>
/// <remarks>它不是业务拒绝。InnerException 只用于诊断，不进入权威故障字段。</remarks>
/// <example>测试夹具在第 4 步 Commit 抛出带 test_failure 代码的异常，前三步仍保留。</example>
public sealed class SimulationCalculationException : Exception
{
    /// <summary>与异常显示消息无关的稳定错误代码。</summary>
    public string Code { get; }

    /// <summary>计算方报告的失败阶段；共享流水线、命令和验证入口以实际执行阶段为准。</summary>
    public SimulationPhase Phase { get; }

    /// <summary>能定位到的非零实体 ID；不能定位时为 null。</summary>
    public ulong? EntityId { get; }

    /// <summary>保存内部故障的稳定信息及可选诊断异常，不处理恢复。</summary>
    /// <param name="code">稳定故障代码。</param>
    /// <param name="phase">失败阶段。</param>
    /// <param name="entityId">可定位的实体 ID；无具体对象时为 null。</param>
    /// <param name="innerException">只供诊断的底层异常；允许为 null。</param>
    public SimulationCalculationException(
        string code, SimulationPhase phase, ulong? entityId = null, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
        Phase = phase;
        EntityId = entityId;
    }
}

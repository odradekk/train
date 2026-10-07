// Train.Core.Simulation 驱动命令与完整模拟步，并原子发布权威状态。
// 会话只保存最后完整边界；失败候选和事件丢弃，内部故障令会话冻结。

using System.Collections.Immutable;

namespace Train.Core.Simulation;

/// <summary>拥有最后完整边界，在单一宿主线程上接受命令并原子发布完整模拟步。</summary>
/// <remarks>调用结束后才向外层返回事件。须由单一宿主线程调用；计算期间可以查询，但不能重入修改入口。</remarks>
/// <example>Advance(10) 的第 4 步故障时，前三步保留，返回完成 3 步，并冻结在第 3 步结束后的状态。</example>
public sealed class SimulationSession
{
    private SimulationState _state;
    private readonly SimulationCalculations _calculations;
    private bool _isRunning;

    /// <summary>保存一个完整起点及其领域计算，准备由调用方驱动会话。</summary>
    /// <param name="initialState">不可变起点；正式运行前应通过对应不变量检查。</param>
    /// <param name="calculations">读取此领域状态、计算候选结果的计算对象，不拥有会话状态。</param>
    /// <remarks>构造器只保存输入，不执行模拟、验证、恢复或外部写入。</remarks>
    /// <exception cref="ArgumentNullException">起点或领域计算对象为空。</exception>
    public SimulationSession(SimulationState initialState, SimulationCalculations calculations)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(calculations);
        _state = initialState;
        _calculations = calculations;
    }

    /// <summary>返回最后一个完整边界的不可变状态，后续发布不会修改此值。</summary>
    /// <returns>会话当前的完整状态；故障时包含冻结原因。</returns>
    public SimulationState GetState() => _state;

    /// <summary>依次计算和发布指定数量的完整模拟步，首次内部故障后停止。</summary>
    /// <param name="steps">要求完整推进的步数；零不产生事务，冻结时不推进。</param>
    /// <returns>实际完成步数、最终完整状态及成功步的事件。</returns>
    /// <remarks>各步独立发布，单步调用也使用此入口。阶段计算和不变量检查全部成功后才能发布候选状态。步号或事务计数达到上限时停止，不分配身份或附加故障。</remarks>
    /// <exception cref="InvalidOperationException">在计算期间重入修改入口。</exception>
    /// <example>从 (60, 2) 推进一步后到达 (61, 0)，下一条命令使用 (61, 1)。</example>
    public AdvanceResult Advance(ulong steps)
    {
        if (_isRunning)
            throw new InvalidOperationException("模拟计算期间不能重入修改入口。");
        _isRunning = true;
        try
        {
            var completed = 0UL;
            var events = ImmutableArray.CreateBuilder<DomainEvent>();
            while (completed < steps && _state.Fault is null)
            {
                var before = _state;
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
                    _state = transition.State;
                    completed++;
                }
                catch (Exception exception)
                {
                    Freeze(before, exception, phase, context);
                }
            }
            return new AdvanceResult(completed, _state, events.ToImmutable());
        }
        finally
        {
            _isRunning = false;
        }
    }

    /// <summary>在当前边界分配命令身份，计算、检查并发布一条命令的结果。</summary>
    /// <param name="command">已解析命令，不带目标步号。</param>
    /// <param name="source">仅用于记录来源，不改变校验和事务规则。</param>
    /// <returns>无身份的入口拒收，或执行、预期拒绝、内部故障的完整结果。</returns>
    /// <remarks>拒绝不重试、不换目标、不撤销此前命令。故障恢复命令前全部状态再附故障；冻结时不接受新命令。</remarks>
    /// <exception cref="ArgumentNullException">命令为空。</exception>
    /// <exception cref="InvalidOperationException">在计算期间重入修改入口。</exception>
    /// <example>暂停时依次成功、拒绝、成功，三条命令占用连续位置，前后成功结果均保留。</example>
    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        if (_isRunning)
            throw new InvalidOperationException("模拟计算期间不能重入修改入口。");
        ArgumentNullException.ThrowIfNull(command);
        var before = _state;
        if (before.Fault is not null)
            return new SubmissionResult.NotAccepted("session_frozen");
        if (before.NextCommandId == ulong.MaxValue)
            return new SubmissionResult.NotAccepted("command_id_exhausted");
        if (before.BoundarySequence == ulong.MaxValue)
            return new SubmissionResult.NotAccepted("boundary_sequence_exhausted");
        if (before.TransactionCount == ulong.MaxValue)
            return new SubmissionResult.NotAccepted("transaction_id_exhausted");

        var context = new TransactionContext(before.TransactionCount + 1, SimulationOperation.Command,
            new SimulationPosition(before.CompletedSteps, before.BoundarySequence + 1), before.NextCommandId);
        _isRunning = true;
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
                // 拒绝只能沿用起点的业务字段和不可变集合，不能夹带临时分配或事件。
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
            _state = transition.State;
            return new SubmissionResult.Completed(execution, _state, transition.Events);
        }
        catch (Exception exception)
        {
            Freeze(before, exception, phase, context);
            var execution = new CommandExecution(before.NextCommandId, context.Position, context.TransactionId,
                source, CommandOutcome.Faulted, _state.Fault!.Code);
            return new SubmissionResult.Completed(execution, _state, ImmutableArray<DomainEvent>.Empty);
        }
        finally
        {
            _isRunning = false;
        }
    }

    /// <summary>验证完整候选及事件容器，失败时在发布前抛出稳定的内部错误。</summary>
    private void CheckTransition(SimulationState before, Transition transition, TransactionContext context)
    {
        var violation = _calculations.CheckInvariants(before, transition.State, context);
        if (violation is not null)
            throw new SimulationCalculationException(violation.Code, SimulationPhase.Validation);
        if (transition.Events.IsDefault || transition.Events.Any(domainEvent => domainEvent is null))
            throw new SimulationCalculationException("invalid_events", SimulationPhase.Validation);
    }

    /// <summary>丢弃失败事务，只在完整起点上附加稳定故障信息。</summary>
    private void Freeze(SimulationState before, Exception exception, SimulationPhase phase, TransactionContext context)
    {
        // 完整步覆盖实现负责报告其内部阶段；命令和验证的阶段由会话入口确定。
        var fault = exception is SimulationCalculationException calculation
            ? new SimulationFault(calculation.Code,
                context.Operation == SimulationOperation.Step && phase != SimulationPhase.Validation
                    ? calculation.Phase
                    : phase,
                context, calculation.EntityId)
            : new SimulationFault("internal_error", phase, context);
        _state = SimulationCalculations.CreateFaultState(before, fault);
    }
}

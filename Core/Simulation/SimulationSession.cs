// Train.Core.Simulation 驱动命令与完整模拟步，并原子发布权威状态。
// 会话一次发布状态与命令日志；失败候选和事件丢弃，内部故障令会话冻结。

using System.Collections.Immutable;

namespace Train.Core.Simulation;

/// <summary>拥有最后完整边界，在单一宿主线程上接受命令并原子发布完整模拟步。</summary>
/// <remarks>调用结束后才向外层返回事件。须由单一宿主线程调用；计算期间可以查询，但不能重入修改入口。</remarks>
/// <example>Advance(10) 的第 4 步故障时，前三步保留，返回完成 3 步，并冻结在第 3 步结束后的状态。</example>
public sealed class SimulationSession
{
    private PublishedBoundary _published;
    private readonly SimulationCalculations _calculations;
    private readonly SimulationCommandCodec _commands;
    private bool _isRunning;

    /// <summary>保存完整起点、领域计算与命令格式，初始化一个新的空历史链。</summary>
    /// <param name="initialState">不可变完整起点；创建或恢复时须先检查起点不变量。</param>
    /// <param name="calculations">该领域的事务计算与起点检查。</param>
    /// <param name="commands">支持此领域命令的版本 2 编码器。</param>
    /// <remarks>构造只保存输入，不执行模拟、验证、恢复或外部写入；命令编码和记录统一由 Submit 完成。</remarks>
    /// <exception cref="ArgumentNullException">起点、领域计算或命令格式为空。</exception>
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

    /// <summary>返回最后一个完整边界的不可变状态，后续发布不会修改此值。</summary>
    /// <returns>会话当前的完整状态；故障时包含冻结原因。</returns>
    public SimulationState GetState() => _published.State;

    /// <summary>返回当前不可变执行日志窗口，不读取或修改权威状态。</summary>
    /// <returns>最近至多 256 条记录与窗口前摘要；新建或恢复的会话从空链开始。</returns>
    public CommandLogSnapshot GetCommandLog() => _published.Log;

    /// <summary>依次计算和发布指定数量的完整模拟步，首次内部故障后停止。</summary>
    /// <param name="steps">要求完整推进的步数；零不产生事务，冻结时不推进。</param>
    /// <returns>实际完成步数、最终完整状态、成功步的事件及本次新发生故障的诊断。</returns>
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

    /// <summary>在当前边界分配命令身份，计算、检查并发布一条命令的结果。</summary>
    /// <param name="command">已解析命令，不带目标步号。</param>
    /// <param name="source">仅用于记录来源，不改变校验和事务规则。</param>
    /// <returns>无身份的入口拒收，或执行、预期拒绝、内部故障的完整结果；新故障附宿主诊断。</returns>
    /// <remarks>拒绝不重试、不换目标、不撤销此前命令。故障恢复命令前全部状态再附故障；冻结时不接受新命令。</remarks>
    /// <exception cref="ArgumentNullException">命令为空。</exception>
    /// <exception cref="ArgumentException">命令不属于此领域格式、字段非法，或来源不受支持；拒收前不会消耗身份。</exception>
    /// <exception cref="InvalidOperationException">在计算期间重入修改入口。</exception>
    /// <example>暂停时依次成功、拒绝、成功，三条命令占用连续位置，前后成功结果均保留。</example>
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
            // 编码属于接受前的输入检查；失败时保持原状态与原日志，不分配尝试身份。
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

    /// <summary>一次发布一个完整状态和同一时刻的日志，避免先发布业务候选再计算日志。</summary>
    private sealed record PublishedBoundary(SimulationState State, CommandLogSnapshot Log);
}

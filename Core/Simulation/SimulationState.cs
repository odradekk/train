// Train.Core.Simulation 定义完整模拟边界上的权威状态。
// 本文件只保存不可变数据；状态是否合法由计算模板中的不变量检查负责。

namespace Train.Core.Simulation;

/// <summary>表示一个完整边界上的核心状态，不包含宿主暂停、倍速、帧余量或日志。</summary>
/// <param name="WorldSeed">所有核心随机计算的世界种子，允许为零。</param>
/// <param name="GeneratorVersion">初始化来源的生成器版本标识；本模块不执行世界生成。</param>
/// <remarks>后续领域状态及测试夹具可以派生不可变记录；所包含的集合和元素也必须不可变。合法状态的下一命令号和下一实体 ID 非零。</remarks>
/// <example>完成 60 步后执行两条命令，CompletedSteps 为 60、BoundarySequence 为 2；下一步完成后分别为 61、0。</example>
public record SimulationState(ulong WorldSeed, string GeneratorVersion)
{
    /// <summary>已完整完成的模拟步数，从零开始；模拟秒数由此值除以每秒步数得到。</summary>
    /// <remarks>ulong.MaxValue 表示已达到步号上限，不能再推进；不能回绕到零。</remarks>
    public ulong CompletedSteps { get; init; }

    /// <summary>当前边界最后使用的命令步内序号；零表示此边界尚未执行命令。</summary>
    /// <remarks>ulong.MaxValue 表示该边界序号已达到上限，下一条命令整次拒收；成功模拟步才将序号归零。</remarks>
    public ulong BoundarySequence { get; init; }

    /// <summary>下一条已接受命令将使用的非零编号，从 1 开始；递增无法表示时整次拒收。</summary>
    /// <remarks>ulong.MaxValue 是耗尽状态，不分配给新命令，因为分配后无法保存下一个编号；合法状态允许此耗尽值。</remarks>
    public ulong NextCommandId { get; init; } = 1;

    /// <summary>下一次成功提交的实体将使用的非零 ID，从 1 开始；临时分配随事务回滚。</summary>
    /// <remarks>ulong.MaxValue 是耗尽状态，不分配给新实体；最大可分配值为 ulong.MaxValue - 1，分配后下一值达到耗尽状态。</remarks>
    public ulong NextEntityId { get; init; } = 1;

    /// <summary>已完成的顶层事务数，从零开始；成功模拟步及执行或预期拒绝的命令各增加一次。</summary>
    /// <remarks>此值是已完成计数，ulong.MaxValue 可以表示最后一个已完成事务；之后无法产生新事务，不得回绕。</remarks>
    public ulong TransactionCount { get; init; }

    /// <summary>最后完整边界上的内部故障；null 表示没有故障，非 null 时会话冻结。</summary>
    public SimulationFault? Fault { get; init; }
}

/// <summary>以已完成步号和步内序号表示命令的全序位置。</summary>
/// <param name="CompletedSteps">命令执行前已完成的模拟步数。</param>
/// <param name="Sequence">边界内的命令序号；零表示尚未执行命令的完整边界。</param>
/// <example>(60, 2) 早于 (61, 0)，也早于 (60, 3)。</example>
public readonly record struct SimulationPosition(ulong CompletedSteps, ulong Sequence)
    : IComparable<SimulationPosition>
{
    /// <summary>待实现：先比较已完成步号，再比较同一边界内的序号。</summary>
    /// <param name="other">需要与当前位置比较的模拟位置。</param>
    /// <returns>当前位置更早时为负数，相同时为零，更晚时为正数。</returns>
    /// <exception cref="NotImplementedException">当前是计算模板。</exception>
    /// <example>(60, 2) 与 (60, 3) 比较得到负数；见 SimulationCalculationTests.Positions_compare_by_step_then_sequence。</example>
    public int CompareTo(SimulationPosition other) => throw new NotImplementedException();
}

/// <summary>区分一条命令和一个完整模拟步这两种顶层事务。</summary>
/// <example>Advance(3) 产生三个 Step 事务，而非一个总事务。</example>
public enum SimulationOperation
{
    /// <summary>当前完整边界上的一条命令。</summary>
    Command,
    /// <summary>一个固定为 1/60 秒的完整模拟步。</summary>
    Step
}

/// <summary>指出内部故障发生在哪个运行阶段。</summary>
/// <example>候选状态中的 NextEntityId 为零属于 Validation 故障。</example>
public enum SimulationPhase
{
    /// <summary>准备边界身份或检查入口条件。</summary>
    Boundary,
    /// <summary>计算一条命令的结果。</summary>
    Command,
    /// <summary>根据已提交状态产生本步请求。</summary>
    Evaluate,
    /// <summary>仲裁竞争并生成执行计划。</summary>
    Resolve,
    /// <summary>根据执行计划构造候选状态。</summary>
    Commit,
    /// <summary>检查候选状态的不变量。</summary>
    Validation
}

/// <summary>表示需要冻结核心的内部故障；只保存稳定身份和错误代码。</summary>
/// <param name="Code">与墙钟和异常消息无关的稳定错误代码。</param>
/// <param name="Phase">故障发生的运行阶段。</param>
/// <param name="Context">故障事务尝试使用的身份和位置；不表示这些编号已经提交。准备身份前故障时为 null。</param>
/// <param name="EntityId">可定位到的实体 ID；无法定位到实体时为 null。</param>
/// <remarks>异常消息、调用栈和墙钟时间留在诊断日志中。记录故障前须丢弃整个失败事务的候选状态。</remarks>
/// <example>第 4 步的 Commit 故障保留第 3 步完成后的状态，并记录尝试执行第 4 步的上下文。</example>
public sealed record SimulationFault(
    string Code,
    SimulationPhase Phase,
    TransactionContext? Context,
    ulong? EntityId = null);

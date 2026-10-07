// Train.Core.Simulation 描述事务的输入身份、候选结果与推进输出。
// 这些数据不发布状态；事件必须随完整事务成功发布，失败时一起丢弃。

using System.Collections.Immutable;

namespace Train.Core.Simulation;

/// <summary>表示会话入口一次性计算出的事务身份，不含宿主时间或可修改的世界引用。</summary>
/// <param name="TransactionId">本次事务尝试使用的非零编号。</param>
/// <param name="Operation">此次计算是一条命令还是一个完整模拟步。</param>
/// <param name="Position">命令的执行位置，或模拟步成功后将到达的边界位置。</param>
/// <param name="CommandId">命令事务的编号；模拟步事务为 null。</param>
/// <example>初始状态的第一步使用事务号 1、位置 (1, 0)、CommandId 为 null。</example>
public readonly record struct TransactionContext(
    ulong TransactionId,
    SimulationOperation Operation,
    SimulationPosition Position,
    ulong? CommandId = null);

/// <summary>表示随成功事务发布给场景和界面的不可变领域事件。</summary>
/// <remarks>具体事件由对应领域定义。事件不是修改状态的回调，不得在模拟阶段重入会话。</remarks>
/// <example>测试夹具中的资源分配事件只在整个模拟步成功时交给调用方。</example>
public abstract record DomainEvent;

/// <summary>表示一次计算得到的完整候选状态与待发布事件。</summary>
/// <param name="State">尚未成为权威事实的候选状态，包含本次边界和计数器变化。</param>
/// <param name="Events">按领域确定的顺序排列的不可变事件；无事件时使用 Empty。</param>
/// <example>库存 5 分配为实体 10 得 3、实体 20 得 2，候选库存为零，但查询在发布前仍得到旧库存。</example>
public sealed record Transition(SimulationState State, ImmutableArray<DomainEvent> Events);

/// <summary>表示本次调用新发生的内部故障及交给宿主记录的原始异常。</summary>
/// <param name="Fault">冻结状态中的稳定故障，身份与位置指向失败的事务尝试。</param>
/// <param name="Exception">本次捕获的原始异常，保留消息、调用栈和内部异常链。</param>
/// <remarks>只随故障发生的调用返回一次，不进入状态捕获、检查点或命令摘要；宿主负责诊断输出。</remarks>
/// <example>第 4 步异常时返回其诊断；冻结后再次推进只返回原故障状态，不再返回诊断。</example>
public sealed record SimulationFaultDiagnostic(SimulationFault Fault, Exception Exception);

/// <summary>表示一次批量推进实际完成的步数、最终完整状态和成功事务的事件。</summary>
/// <param name="StepsAdvanced">本次调用实际完整完成的步数；零表示没有推进。</param>
/// <param name="StateAfter">最终完整边界；发生故障时其中的 Fault 非 null。</param>
/// <param name="Events">仅包含本次成功模拟步的事件，保持步间及步内顺序；失败步的事件不在其中。</param>
/// <param name="Diagnostic">仅本次调用新发生故障时非 null；正常或已冻结的会话不重复返回诊断。</param>
/// <example>请求推进 10 步而第 4 步故障，StepsAdvanced 为 3，前三步的事件仍保留。</example>
public sealed record AdvanceResult(
    ulong StepsAdvanced,
    SimulationState StateAfter,
    ImmutableArray<DomainEvent> Events,
    SimulationFaultDiagnostic? Diagnostic = null);

/// <summary>表示一次尚未提交的实体 ID 分配。</summary>
/// <param name="EntityId">本次分配的非零实体 ID。</param>
/// <param name="Candidate">NextEntityId 已更新的候选状态；分配不会修改输入状态。</param>
/// <example>NextEntityId 为 7 时，分配 ID 7，并得到 NextEntityId 为 8 的候选值。</example>
public sealed record EntityAllocation(ulong EntityId, SimulationState Candidate);

/// <summary>表示按固定检查顺序找到的首个状态不变量错误。</summary>
/// <param name="Code">稳定错误代码。</param>
/// <param name="Field">发生错误的权威字段名称。</param>
/// <example>候选状态的 NextEntityId 为零时，报告该字段；不会把损坏状态作为正常结果发布。</example>
public sealed record InvariantViolation(string Code, string Field);

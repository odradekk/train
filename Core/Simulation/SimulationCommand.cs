// Train.Core.Simulation 定义已解析命令、计算结果和入口返回值。
// 本文件不定义命令种类或规范编码；这些由后续命令切片补齐。

using System.Collections.Immutable;

namespace Train.Core.Simulation;

/// <summary>表示已解析的一次核心操作；具体命令以不可变领域数据派生。</summary>
/// <remarks>命令不携带目标步号，执行位置由会话在接受时分配。</remarks>
/// <example>测试夹具中的删除实体命令包含固定目标 ID；目标消失时拒绝，不替换目标。</example>
public abstract record CoreCommand;

/// <summary>标识命令的提交来源；来源不改变命令的校验和事务规则。</summary>
/// <example>Ui、Console 和 Automation 提交相同命令时共用 Submit 入口。</example>
public enum CommandSource
{
    /// <summary>玩家界面提交。</summary>
    Ui,
    /// <summary>控制台提交。</summary>
    Console,
    /// <summary>自动测试或驱动程序提交。</summary>
    Automation
}

/// <summary>区分已接受命令的执行、预期拒绝和内部故障。</summary>
/// <example>目标已消失是 Rejected；计算抛内部异常是 Faulted。</example>
public enum CommandOutcome
{
    /// <summary>命令成功提交全部业务修改。</summary>
    Executed,
    /// <summary>命令被预期业务条件拒绝，但占用执行位置。</summary>
    Rejected,
    /// <summary>命令发生内部故障，回滚到执行前并冻结。</summary>
    Faulted
}

/// <summary>表示命令计算出的候选结果；内部故障由会话处理，不作为普通拒绝返回。</summary>
/// <param name="Transition">完整候选结果；预期拒绝只推进命令号、步内序号及事务计数，事件为空。</param>
/// <example>复合命令临时创建实体后拒绝，候选业务状态和 NextEntityId 仍等于命令前状态。</example>
public abstract record CommandDecision(Transition Transition)
{
    /// <summary>表示命令计算成功，但结果仍待入口检查和发布。</summary>
    /// <param name="Result">包含全部业务修改与边界计数变化的候选结果。</param>
    /// <example>创建实体后返回完整候选值，入口检查通过后才发布。</example>
    public sealed record Execute(Transition Result) : CommandDecision(Result);

    /// <summary>表示预期拒绝；保留拒绝原因与仅消耗执行身份的候选结果。</summary>
    /// <param name="Result">业务状态和实体分配器未变化的候选结果。</param>
    /// <param name="Reason">稳定的业务拒绝原因，例如 target_missing。</param>
    /// <example>目标消失时返回 target_missing，候选业务状态保留执行前值。</example>
    public sealed record Reject(Transition Result, string Reason) : CommandDecision(Result);
}

/// <summary>表示一条已接受命令的身份、来源与执行结果，不包含规范负载或日志摘要。</summary>
/// <param name="CommandId">接受时尝试使用的命令编号。</param>
/// <param name="Position">接受时分配的模拟位置。</param>
/// <param name="TransactionId">该命令事务尝试使用的编号。</param>
/// <param name="Source">命令的提交来源。</param>
/// <param name="Outcome">执行、预期拒绝或内部故障。</param>
/// <param name="Reason">拒绝原因或故障代码；成功时为 null。</param>
/// <example>第 60 步暂停期间的第二条命令记录位置 (60, 2)；拒绝也保留该位置。</example>
public sealed record CommandExecution(
    ulong CommandId,
    SimulationPosition Position,
    ulong TransactionId,
    CommandSource Source,
    CommandOutcome Outcome,
    string? Reason = null);

/// <summary>表示命令入口拒收，或已接受命令执行完成后的结果。</summary>
/// <example>命令号达到上限是 NotAccepted；目标消失是 Completed，其执行记录为 Rejected。</example>
public abstract record SubmissionResult
{
    /// <summary>表示没有分配命令号、位置或事务号的入口拒收。</summary>
    /// <param name="Reason">入口拒收原因，例如会话已冻结或编号耗尽。</param>
    /// <example>NextCommandId 达到 ulong.MaxValue 时拒收，全部计数器保持原值。</example>
    public sealed record NotAccepted(string Reason) : SubmissionResult;

    /// <summary>表示已接受命令的执行记录与最后完整状态。</summary>
    /// <param name="Execution">命令的身份和结果；故障记录中的编号不表示已提交。</param>
    /// <param name="StateAfter">执行后的完整状态；故障时保留命令前状态并附故障。</param>
    /// <param name="Events">成功命令的不可变事件；拒绝或故障时为空。</param>
    /// <param name="Diagnostic">本命令新发生内部故障时的诊断；执行或预期拒绝时为 null。</param>
    /// <example>成功、目标消失和内部异常均返回执行记录，只有内部异常令 StateAfter.Fault 非 null。</example>
    public sealed record Completed(
        CommandExecution Execution,
        SimulationState StateAfter,
        ImmutableArray<DomainEvent> Events,
        SimulationFaultDiagnostic? Diagnostic = null) : SubmissionResult;
}

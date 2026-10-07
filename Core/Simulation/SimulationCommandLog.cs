// Train.Core.Simulation 定义有界命令历史及其链式摘要。
// 日志不是权威状态；裁剪显示窗口时保留此前历史的摘要锚点。

using System.Collections.Immutable;

namespace Train.Core.Simulation;

/// <summary>表示一条已接受命令的负载、尝试身份、结果和链式摘要。</summary>
/// <param name="Execution">复用运行边界的来源、命令号、位置、事务号和三种结果。</param>
/// <param name="Payload">命令规范负载及单条负载摘要。</param>
/// <param name="CanonicalText">完整执行记录的规范文本；不是只含命令字段的负载。</param>
/// <param name="ChainSha256">前一链摘要加 LF 再加 CanonicalText 的 UTF-8 SHA-256。</param>
/// <example>目标消失记录 Rejected；故障记录尝试身份，而非宣称那些计数已经提交。</example>
public sealed record CommandLogEntry(
    CommandExecution Execution, EncodedCommand Payload, string CanonicalText, string ChainSha256);

/// <summary>表示最近至多 256 条命令的不可变日志窗口及窗口前摘要。</summary>
/// <param name="PrefixSha256">首条保留记录之前的链摘要；空历史从 64 个零开始。</param>
/// <param name="Entries">按实际接受次序排列的执行、拒绝或故障记录，不含入口拒收。</param>
/// <remarks>裁剪最旧记录不重新计算全历史链；恢复会话不恢复日志，开始一条新链。</remarks>
/// <example>追加第 257 条后只保留第 2 至 257 条，PrefixSha256 为第 1 条的链摘要。</example>
public sealed record CommandLogSnapshot(string PrefixSha256, ImmutableArray<CommandLogEntry> Entries)
{
    /// <summary>没有历史记录的新链。</summary>
    public static CommandLogSnapshot Empty { get; } = new(
        SimulationCommandLog.InitialSha256, ImmutableArray<CommandLogEntry>.Empty);
}

/// <summary>计算命令日志的下一个完整窗口；调用方在事务发布时替换旧窗口。</summary>
/// <remarks>摘要覆盖全历史；内存窗口的裁剪不会重置历史链。</remarks>
public static class SimulationCommandLog
{
    /// <summary>执行记录的第一版；与命令负载版本分别解释。</summary>
    private const uint RecordFormatVersion = 1;

    /// <summary>内存执行日志的最大记录数，由命令日志设计暂定为 256 条。</summary>
    /// <seealso href="../../docs/design/04-deterministic-simulation.md#命令日志"/>
    public const int Capacity = 256;

    /// <summary>新历史链的初始摘要文本，固定为 64 个零。</summary>
    /// <seealso href="../../docs/design/04-deterministic-simulation.md#规范编码"/>
    public const string InitialSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>计算追加一条已接受命令后的记录文本、全历史链摘要和最近窗口。</summary>
    /// <param name="before">追加前的不可变日志快照。</param>
    /// <param name="execution">运行边界已计算出的执行、拒绝或故障结果。</param>
    /// <param name="payload">在接受命令前完成编码的规范负载。</param>
    /// <returns>追加后的完整窗口；超过上限时移除最旧记录并推进摘要锚点。</returns>
    /// <remarks>此方法本身不修改 before、会话或外部文件。</remarks>
    public static CommandLogSnapshot Append(
        CommandLogSnapshot before, CommandExecution execution, EncodedCommand payload)
    {
        var text = EncodeRecord(execution, payload);
        var previous = before.Entries.IsEmpty ? before.PrefixSha256 : before.Entries[^1].ChainSha256;
        var entry = new CommandLogEntry(execution, payload, text, ComputeChainSha256(previous, text));
        return TrimWindow(before, entry);
    }

    /// <summary>计算含尝试身份、来源、负载及结果的记录规范文本。</summary>
    private static string EncodeRecord(CommandExecution execution, EncodedCommand payload)
    {
        var source = execution.Source switch
        {
            CommandSource.Ui => "ui",
            CommandSource.Console => "console",
            CommandSource.Automation => "automation",
            _ => throw new ArgumentOutOfRangeException(nameof(execution))
        };
        var outcome = execution.Outcome switch
        {
            CommandOutcome.Executed => "executed",
            CommandOutcome.Rejected => "rejected",
            CommandOutcome.Faulted => "faulted",
            _ => throw new ArgumentOutOfRangeException(nameof(execution))
        };
        return SimulationJson.Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("record_format_version", RecordFormatVersion);
            writer.WriteString("source", source);
            writer.WriteNumber("command_id", execution.CommandId);
            writer.WriteStartObject("position");
            writer.WriteNumber("completed_steps", execution.Position.CompletedSteps);
            writer.WriteNumber("sequence", execution.Position.Sequence);
            writer.WriteEndObject();
            writer.WriteNumber("transaction_id", execution.TransactionId);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payload.CanonicalText);
            writer.WriteString("outcome", outcome);
            writer.WriteString("reason", execution.Reason);
            writer.WriteEndObject();
        });
    }

    /// <summary>计算前一摘要文本、LF 和记录文本的 UTF-8 SHA-256。</summary>
    private static string ComputeChainSha256(string previous, string text) =>
        SimulationJson.Sha256(previous + "\n" + text);

    /// <summary>计算追加后的最近窗口与被裁历史的摘要锚点。</summary>
    private static CommandLogSnapshot TrimWindow(CommandLogSnapshot before, CommandLogEntry entry)
    {
        var entries = before.Entries.Add(entry);
        var removed = Math.Max(0, entries.Length - Capacity);
        return removed == 0
            ? new(before.PrefixSha256, entries)
            : new(entries[removed - 1].ChainSha256, entries.RemoveRange(0, removed));
    }
}

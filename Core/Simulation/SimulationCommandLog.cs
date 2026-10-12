
using System.Collections.Immutable;

namespace Train.Core.Simulation;

public sealed record CommandLogEntry(
    CommandExecution Execution, EncodedCommand Payload, string CanonicalText, string ChainSha256);

public sealed record CommandLogSnapshot(string PrefixSha256, ImmutableArray<CommandLogEntry> Entries)
{
    public static CommandLogSnapshot Empty { get; } = new(
        SimulationCommandLog.InitialSha256, ImmutableArray<CommandLogEntry>.Empty);
}

public static class SimulationCommandLog
{
    private const uint RecordFormatVersion = 1;

    public const int Capacity = 256;

    public const string InitialSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    public static CommandLogSnapshot Append(
        CommandLogSnapshot before, CommandExecution execution, EncodedCommand payload)
    {
        var text = EncodeRecord(execution, payload);
        var previous = before.Entries.IsEmpty ? before.PrefixSha256 : before.Entries[^1].ChainSha256;
        var entry = new CommandLogEntry(execution, payload, text, ComputeChainSha256(previous, text));
        return TrimWindow(before, entry);
    }

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

    private static string ComputeChainSha256(string previous, string text) =>
        SimulationJson.Sha256(previous + "\n" + text);

    private static CommandLogSnapshot TrimWindow(CommandLogSnapshot before, CommandLogEntry entry)
    {
        var entries = before.Entries.Add(entry);
        var removed = Math.Max(0, entries.Length - Capacity);
        return removed == 0
            ? new(before.PrefixSha256, entries)
            : new(entries[removed - 1].ChainSha256, entries.RemoveRange(0, removed));
    }
}

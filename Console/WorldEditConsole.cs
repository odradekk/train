using System.Globalization;
using Train.Domain.Commands;
using Train.Domain.Voxels;

namespace Train;

/// <summary>
/// Console surface for the ordered world-edit entry: the optional trailing <c>at &lt;step&gt;</c> on modification
/// commands and the <c>voxel edits</c> queries. Parsed payloads go to <see cref="VoxelDevelopmentSession.SubmitEdit"/>.
/// </summary>
internal static class WorldEditConsole
{
    public const int DefaultLogCount = 8;

    public static bool TrySplitSubmissionOptions(
        IReadOnlyList<string> arguments,
        out IReadOnlyList<string> rest,
        out ulong? step,
        out bool confirmEndHistory)
    {
        TrySplitHistoryConfirmation(arguments, out rest, out confirmEndHistory);
        return TrySplitAt(rest, out rest, out step);
    }

    public static void TrySplitHistoryConfirmation(
        IReadOnlyList<string> arguments,
        out IReadOnlyList<string> rest,
        out bool confirmEndHistory)
    {
        confirmEndHistory = arguments.Count > 0 &&
            string.Equals(arguments[^1], "confirm-end-history", StringComparison.OrdinalIgnoreCase);
        rest = confirmEndHistory ? arguments.Take(arguments.Count - 1).ToArray() : arguments;
    }

    /// <summary>Splits a trailing <c>at &lt;step&gt;</c>; <paramref name="step"/> is null when absent.</summary>
    public static bool TrySplitAt(IReadOnlyList<string> arguments, out IReadOnlyList<string> rest, out ulong? step)
    {
        rest = arguments;
        step = null;
        if (arguments.Count < 2 || !string.Equals(arguments[^2], "at", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!ulong.TryParse(arguments[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;
        rest = arguments.Take(arguments.Count - 2).ToArray();
        step = parsed;
        return true;
    }

    public static string FormatSubmission(WorldEditSubmission submission)
    {
        if (!submission.Accepted)
            return submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired
                ? "Edit HistoryConfirmationRequired. This irreversible edit will clear undo and redo history; resubmit with trailing confirm-end-history."
                : $"Edit {submission.Refusal}.";
        var record = submission.Record!;
        var head = $"Edit {record.Id} {WorldEditCodec.WireKind(record.Edit.Kind)}";
        var text = record.State == WorldEditState.Pending
            ? string.Join('\n', $"{head} pending", $"accepted {record.AcceptedAt} target {record.TargetStep}")
            : string.Join('\n', $"{head} {WorldEditCodec.WireState(record.State)} {record.Status}", $"accepted {record.AcceptedAt} executed {record.ExecutedAt}");
        return submission.Result is VoxelCellSetResult cells && cells.OwnerKind != VoxelCellOwnerKind.None
            ? string.Join('\n', text, FormatCellConflict(cells))
            : text;
    }

    public static string FormatHistorySubmission(VoxelDevelopmentSession session, Func<WorldEditSubmission> submit)
        => FormatWithHistoryEvictionNotice(session, () => FormatSubmission(submit()));

    public static string FormatWithHistoryEvictionNotice(VoxelDevelopmentSession session, Func<string> format)
    {
        var evictions = session.HistoryEvictionCount;
        var text = format();
        if (session.HistoryEvictionCount > evictions)
            text = string.Join('\n', text, "World undo history capacity reached; oldest record evicted.");
        return text;
    }

    private static string FormatCellConflict(VoxelCellSetResult result) => result.OwnerKind switch
    {
        VoxelCellOwnerKind.RailSegment => $"conflict rail-segment {result.OwnerId} at ({result.Position.X}, {result.Position.Y}, {result.Position.Z})",
        VoxelCellOwnerKind.VehiclePart => $"conflict vehicle {result.OwnerId} part {result.PartId} at ({result.Position.X}, {result.Position.Y}, {result.Position.Z})",
        _ => throw new ArgumentOutOfRangeException(nameof(result), "Cell conflict owner must be defined.")
    };

    public static string? Execute(VoxelDevelopmentSession session, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 1)
            return FormatSummary(session);
        if (arguments.Count == 2 && string.Equals(arguments[1], "pending", StringComparison.OrdinalIgnoreCase))
            return FormatPending(session);
        if (arguments.Count is 2 or 3 && string.Equals(arguments[1], "log", StringComparison.OrdinalIgnoreCase))
        {
            var count = DefaultLogCount;
            if (arguments.Count == 3 && (!int.TryParse(arguments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1))
                return null;
            return FormatLog(session, count);
        }

        if (arguments.Count == 2 && string.Equals(arguments[1], "fault", StringComparison.OrdinalIgnoreCase))
            return FormatFault(session.Fault);
        return null;
    }

    private static string FormatSummary(VoxelDevelopmentSession session) =>
        string.Join('\n',
            $"Edits boundary {session.EditBoundary} next {session.NextEditId}",
            $"pending {session.QueryPendingEdits().Count} log {session.EditLogCount}",
            $"digest {session.EditLogDigest}",
            session.Fault is null ? "fault none" : $"fault {FaultSource(session.Fault)} at {session.Fault.Position}");

    private static string FaultSource(SimulationFault fault) =>
        fault.EditId is { } id ? $"edit {id}" : "motion";

    private static string FormatPending(VoxelDevelopmentSession session)
    {
        var pending = session.QueryPendingEdits();
        var lines = new List<string> { $"Edits pending {pending.Count}" };
        lines.AddRange(pending.Select(record =>
            $"edit {record.Id} {WorldEditCodec.WireKind(record.Edit.Kind)} accepted {record.AcceptedAt} target {record.TargetStep}"));
        return string.Join('\n', lines);
    }

    private static string FormatLog(VoxelDevelopmentSession session, int count)
    {
        var log = session.QueryEditLog();
        var lines = new List<string> { $"Edits log {session.EditLogCount} shown {Math.Min(count, log.Count)}" };
        lines.AddRange(log.Skip(Math.Max(0, log.Count - count)).Select(record =>
            $"edit {record.Id} {WorldEditCodec.WireKind(record.Edit.Kind)} {WorldEditCodec.WireState(record.State)} {record.Status} at {record.ExecutedAt} accepted {record.AcceptedAt}"));
        return string.Join('\n', lines);
    }

    private static string FormatFault(SimulationFault? fault) =>
        fault is null
            ? "Edits fault none."
            : string.Join('\n',
                fault.Kind is { } kind
                    ? $"Edits fault edit {fault.EditId} {WorldEditCodec.WireKind(kind)} at {fault.Position}"
                    : $"Edits fault motion at {fault.Position}",
                $"exception {fault.ExceptionType}",
                $"message {fault.Message}");
}

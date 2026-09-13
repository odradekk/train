using System.Globalization;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train;

public sealed class RailConsoleCommands
{
    private readonly VoxelDevelopmentSession _session;

    public RailConsoleCommands(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public string Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 1 && string.Equals(arguments[0], "construct", StringComparison.OrdinalIgnoreCase))
            return FormatConstruct(_session.ConstructRails());

        if (arguments.Count >= 2 &&
            string.Equals(arguments[0], "repair", StringComparison.OrdinalIgnoreCase) &&
            (arguments.Count - 1) % 3 == 0 &&
            TryCells(arguments, 1, out var cells))
            return FormatRepair(_session.RepairRails(cells));

        if (arguments.Count >= 1 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase))
            return FormatQuery(arguments.Skip(1).ToArray());

        return string.Join('\n',
            "Usage: rail construct | rail query [occupancy|intervals|connections|graph|rebuild] | rail query <x> <y> <z>",
            "rail repair <x> <y> <z> [<x> <y> <z> ...].");
    }

    private string FormatConstruct(RailEditResult result)
    {
        if (!result.Succeeded)
            return $"Rail {result.Status}.";
        var query = _session.QueryRails();
        return string.Join('\n',
            $"Rail {result.Status}.",
            $"segments {query.Segments.Count}",
            $"sleepers {query.SleeperCount}",
            $"cells {query.OccupiedCells.Count}",
            $"ids {string.Join(',', result.SegmentIds!.Select(id => id.Value.ToString(CultureInfo.InvariantCulture)))}");
    }

    private string FormatRepair(RailRepairResult result)
    {
        if (!result.Succeeded)
            return $"Rail {result.Status} at ({result.Cell.X}, {result.Cell.Y}, {result.Cell.Z}).";
        return string.Join('\n',
            $"Rail {result.Status} at ({result.Cell.X}, {result.Cell.Y}, {result.Cell.Z}).",
            $"restored {result.Restored!.Count}",
            $"cells {_session.QueryRails().OccupiedCells.Count}");
    }

    private string FormatQuery(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 3 && TryCell(arguments, 0, out var cell))
            return FormatCell(cell);

        var query = _session.QueryRails();
        if (arguments.Count == 0)
            return string.Join('\n',
                $"Rail segments {query.Segments.Count}.",
                $"connections {query.Connections.Count}",
                $"sleepers {query.SleeperCount}",
                $"cells {query.OccupiedCells.Count}");

        if (arguments.Count == 1 && string.Equals(arguments[0], "occupancy", StringComparison.OrdinalIgnoreCase))
            return string.Join('\n',
                $"Rail occupancy {query.OccupiedCells.Count}.",
                $"registered {query.Occupancy.Count}");

        if (arguments.Count == 1 && string.Equals(arguments[0], "intervals", StringComparison.OrdinalIgnoreCase))
            return string.Join('\n', query.Segments.Select(segment =>
                $"Rail {segment.Id.Value} world-x {FormatIntervals(segment.TraversableWorldX)}"));

        if (arguments.Count == 1 && string.Equals(arguments[0], "connections", StringComparison.OrdinalIgnoreCase))
        {
            if (query.Connections.Count == 0)
                return "Rail connections 0.";
            return string.Join('\n', query.Connections.Select(connection =>
                $"Rail {connection.FromSegment.Value}:{connection.FromEnd} -> {connection.ToSegment.Value}:{connection.ToEnd}"));
        }

        if (arguments.Count == 1 && string.Equals(arguments[0], "graph", StringComparison.OrdinalIgnoreCase))
        {
            var graph = _session.QueryDerivedRailGraph();
            return $"Rail graph {(graph.Current ? "current" : "stale")} segments {graph.Segments.Count}.";
        }

        if (arguments.Count == 1 && string.Equals(arguments[0], "rebuild", StringComparison.OrdinalIgnoreCase))
        {
            _session.RebuildDerivedRailGraph();
            return FormatQuery(new[] { "graph" });
        }

        return "Usage: rail query [occupancy|intervals|connections|graph|rebuild] | rail query <x> <y> <z>";
    }

    private string FormatCell(WorldVoxelCoord cell)
    {
        if (!_session.Rails.TryGetOccupancy(cell, out var occupancy))
            return $"Rail cell ({cell.X}, {cell.Y}, {cell.Z}): empty.";

        var occupied = _session.Query(cell).IsOccupied && !_session.Rails.IsDestroyed(cell);
        var supports = string.Join(';', occupancy.Supports.Select(support =>
            $"{support.SegmentId.Value}={FormatNumber(support.WorldX.Start)}..{FormatNumber(support.WorldX.End)}"));
        return string.Join('\n',
            $"Rail cell ({cell.X}, {cell.Y}, {cell.Z}): {(occupied ? "occupied" : "missing")}",
            $"supports {supports}");
    }

    private static string FormatIntervals(IReadOnlyList<RailInterval> intervals) =>
        intervals.Count == 0
            ? "none"
            : string.Join(',', intervals.Select(interval =>
                $"{FormatNumber(interval.Start)}..{FormatNumber(interval.End)}"));

    private static string FormatNumber(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static bool TryCells(IReadOnlyList<string> arguments, int start, out IReadOnlyList<WorldVoxelCoord> cells)
    {
        var parsed = new List<WorldVoxelCoord>();
        for (var index = start; index + 2 < arguments.Count; index += 3)
        {
            if (!TryCell(arguments, index, out var cell))
            {
                cells = Array.Empty<WorldVoxelCoord>();
                return false;
            }

            parsed.Add(cell);
        }

        cells = parsed;
        return parsed.Count > 0;
    }

    private static bool TryCell(IReadOnlyList<string> arguments, int start, out WorldVoxelCoord cell)
    {
        cell = default;
        if (start + 2 >= arguments.Count ||
            !int.TryParse(arguments[start], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(arguments[start + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(arguments[start + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
            return false;
        cell = new WorldVoxelCoord(x, y, z);
        return true;
    }
}

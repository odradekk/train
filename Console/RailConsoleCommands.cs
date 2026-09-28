using System.Globalization;
using Train.Domain.Composites;
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

        if (arguments.Count == 1 && string.Equals(arguments[0], "assemble", StringComparison.OrdinalIgnoreCase))
            return FormatAssemble(_session.AssembleStaticTrain());

        if (arguments.Count >= 1 && string.Equals(arguments[0], "start", StringComparison.OrdinalIgnoreCase))
        {
            if (TryStartArguments(arguments, out var heading, out var startAt, out var assemblyId))
                return FormatMotion(_session.StartTrain(heading, startAt, assemblyId));
            return Usage();
        }

        if (arguments.Count >= 1 && string.Equals(arguments[0], "stop", StringComparison.OrdinalIgnoreCase))
        {
            if (TryStopArguments(arguments, out var stopAt, out var stopAssemblyId))
                return FormatMotion(_session.StopTrain(stopAt, stopAssemblyId));
            return Usage();
        }

        if (arguments.Count == 1 && string.Equals(arguments[0], "pause", StringComparison.OrdinalIgnoreCase))
            return FormatMotion(_session.PauseSimulation());

        if (arguments.Count == 1 && string.Equals(arguments[0], "resume", StringComparison.OrdinalIgnoreCase))
            return FormatMotion(_session.ResumeSimulation());

        if (arguments.Count >= 1 && string.Equals(arguments[0], "step", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count == 1)
                return FormatMotion(_session.AdvanceSimulation(1));
            if (arguments.Count == 2 &&
                ulong.TryParse(arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps))
                return FormatMotion(_session.AdvanceSimulation(steps));
            return Usage();
        }

        if (arguments.Count >= 2 &&
            string.Equals(arguments[0], "repair", StringComparison.OrdinalIgnoreCase) &&
            (arguments.Count - 1) % 3 == 0 &&
            TryCells(arguments, 1, out var cells))
            return FormatRepair(_session.RepairRails(cells));

        if (arguments.Count >= 1 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase))
            return FormatQuery(arguments.Skip(1).ToArray());

        if (arguments.Count >= 1 && string.Equals(arguments[0], "follow", StringComparison.OrdinalIgnoreCase))
            return FormatFollow(arguments.Skip(1).ToArray());

        return Usage();
    }

    private static string Usage() =>
        string.Join('\n',
            "Usage: rail construct | rail assemble | rail start [assembly <id>] [reverse] [step] | rail stop [assembly <id>] [step] | rail pause | rail resume | rail step [n]",
            "rail query [occupancy|intervals|connections|graph|rebuild|assembly|motion|display] | rail query <x> <y> <z>",
            "rail follow [vehicle <id>] | rail repair <x> <y> <z> [<x> <y> <z> ...].");

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

        if (arguments.Count == 1 && string.Equals(arguments[0], "assembly", StringComparison.OrdinalIgnoreCase))
            return FormatAssemblies(_session.QueryTrainAssemblies());

        if (arguments.Count == 1 && string.Equals(arguments[0], "motion", StringComparison.OrdinalIgnoreCase))
            return FormatMotionQuery();

        if (arguments.Count == 1 && string.Equals(arguments[0], "display", StringComparison.OrdinalIgnoreCase))
            return FormatDisplayQuery();

        return "Usage: rail query [occupancy|intervals|connections|graph|rebuild|assembly|motion|display] | rail query <x> <y> <z>";
    }

    private static string FormatMotion(TrainMotionResult result)
    {
        if (!result.Succeeded)
            return result.AdvancedSteps == 0
                ? $"Rail {result.Status}."
                : $"Rail {result.Status} step {result.SimulationStep} advanced {result.AdvancedSteps}.";
        if (result.Status == TrainMotionStatus.Advanced)
            return $"Rail {result.Status} step {result.SimulationStep} advanced {result.AdvancedSteps}.";
        if (result.Status is TrainMotionStatus.Started or TrainMotionStatus.Stopped)
            return $"Rail {result.Status} step {result.SimulationStep} at {result.AtStep}.";
        return $"Rail {result.Status} step {result.SimulationStep}.";
    }

    private string FormatMotionQuery()
    {
        var assemblies = _session.QueryTrainAssemblies();
        var lines = new List<string>
        {
            $"Rail motion step {_session.SimulationStep} paused {_session.SimulationPaused.ToString().ToLowerInvariant()}"
        };
        foreach (var command in _session.QueryScheduledTrainCommands())
        {
            lines.Add(
                $"pending {(command.Start ? "start" : "stop")} {command.AtStep.ToString(CultureInfo.InvariantCulture)} assembly {(command.AssemblyId?.ToString() ?? "none")}");
        }
        if (assemblies.Count == 0)
        {
            lines.Add("assembly none");
            return string.Join('\n', lines);
        }

        foreach (var assembly in assemblies)
        {
            lines.Add(
                $"assembly {assembly.Id} speed {FormatNumber(assembly.Speed)} running {assembly.Running.ToString().ToLowerInvariant()} heading {assembly.Reference.Heading} halt {FormatHalt(assembly.HaltReason)}");
        }

        return string.Join('\n', lines);
    }

    private string FormatDisplayQuery()
    {
        var sample = _session.QueryDisplayPoses();
        var lines = new List<string>
        {
            $"Rail display alpha {FormatNumber(sample.Alpha)} ended {sample.IntervalEnded.ToString().ToLowerInvariant()} follow {_session.FollowedVehicleId?.Value.ToString(CultureInfo.InvariantCulture) ?? "none"}"
        };
        foreach (var assembly in sample.Assemblies)
        {
            lines.Add(
                $"assembly {assembly.Id} distance {FormatNumber(assembly.Reference.Distance)} heading {assembly.Reference.Heading}");
        }

        foreach (var vehicle in sample.Vehicles)
        {
            lines.Add(
                $"vehicle {vehicle.VehicleId} front {FormatNumber(vehicle.FrontContact.X)},{FormatNumber(vehicle.FrontContact.Y)},{FormatNumber(vehicle.FrontContact.Z)} origin {FormatNumber(vehicle.Transform.OriginX)},{FormatNumber(vehicle.Transform.OriginY)},{FormatNumber(vehicle.Transform.OriginZ)}");
        }

        return string.Join('\n', lines);
    }

    private string FormatFollow(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2 &&
            string.Equals(arguments[0], "vehicle", StringComparison.OrdinalIgnoreCase) &&
            TryEntityId(arguments[1], out var vehicleId))
        {
            if (!_session.TryFollowVehicle(vehicleId))
                return $"Rail follow unknown {vehicleId.Value.ToString(CultureInfo.InvariantCulture)}.";
        }
        else if (arguments.Count != 0)
            return Usage();

        var followed = _session.FollowedVehicleId;
        var camera = _session.QueryFollowedCamera();
        if (followed is null || camera is null)
            return "Rail follow none.";
        return string.Join('\n',
            $"Rail follow {followed.Value.Value.ToString(CultureInfo.InvariantCulture)}",
            $"focus {FormatNumber(camera.FocusX)},{FormatNumber(camera.FocusY)},{FormatNumber(camera.FocusZ)}",
            $"heading {FormatNumber(camera.HeadingX)},{FormatNumber(camera.HeadingY)},{FormatNumber(camera.HeadingZ)}");
    }

    private static string FormatAssemble(TrainAssemblyResult result)
    {
        if (!result.Succeeded)
            return $"Rail assemble {result.Status}.";
        return $"Rail assemble {result.Status} assembly {result.AssemblyId}.";
    }

    private string FormatAssemblies(IReadOnlyList<TrainAssemblyQuery> assemblies)
    {
        if (assemblies.Count == 0)
            return "Rail assembly none.";
        return string.Join('\n', assemblies.Select(FormatAssembly));
    }

    private string FormatAssembly(TrainAssemblyQuery assembly)
    {
        var lines = new List<string>
        {
            $"Rail assembly {assembly.Id} active {assembly.ActiveLocomotiveId?.ToString() ?? "none"} heading {assembly.Reference.Heading} distance {FormatNumber(assembly.Reference.Distance)}",
            $"speed {FormatNumber(assembly.Speed)} running {assembly.Running.ToString().ToLowerInvariant()}",
            $"halt {FormatHalt(assembly.HaltReason)}",
            $"graph {_session.AssemblyGraphGeneration}",
            $"step {_session.SimulationStep} paused {_session.SimulationPaused.ToString().ToLowerInvariant()}",
            $"vehicles {string.Join(',', assembly.VehicleIds.Select(id => id.Value.ToString(CultureInfo.InvariantCulture)))}"
        };
        foreach (var vehicle in assembly.Vehicles)
        {
            var query = _session.QueryComposite(vehicle.VehicleId);
            var operational = query?.OperationalCondition.ToString() ?? "Unknown";
            lines.Add(
                $"vehicle {vehicle.VehicleId} front {FormatNumber(vehicle.FrontContact.X)},{FormatNumber(vehicle.FrontContact.Y)},{FormatNumber(vehicle.FrontContact.Z)} {operational}");
        }

        foreach (var connection in assembly.Connections)
        {
            lines.Add(
                $"coupler {connection.FromVehicleId}:{connection.FromPartId} ({connection.FromCell.X},{connection.FromCell.Y},{connection.FromCell.Z}) -> {connection.ToVehicleId}:{connection.ToPartId} ({connection.ToCell.X},{connection.ToCell.Y},{connection.ToCell.Z})");
        }

        return string.Join('\n', lines);
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

    private static string FormatHalt(TrainHaltReason reason) => reason switch
    {
        TrainHaltReason.SupportLost => "support-lost",
        TrainHaltReason.ForwardGap => "forward-gap",
        TrainHaltReason.EndOfLine => "end-of-line",
        _ => "none"
    };

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

    private static bool TryStartArguments(
        IReadOnlyList<string> arguments,
        out RailHeading heading,
        out ulong? atStep,
        out EntityId? assemblyId)
    {
        heading = RailHeading.AlongAlignment;
        atStep = null;
        assemblyId = null;
        var index = 1;
        if (index < arguments.Count && string.Equals(arguments[index], "assembly", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= arguments.Count || !TryEntityId(arguments[index + 1], out var id))
                return false;
            assemblyId = id;
            index += 2;
        }

        if (index < arguments.Count && string.Equals(arguments[index], "reverse", StringComparison.OrdinalIgnoreCase))
        {
            heading = RailHeading.AgainstAlignment;
            index++;
        }

        if (index < arguments.Count)
        {
            if (!TryStep(arguments[index], out var step))
                return false;
            atStep = step;
            index++;
        }

        return index == arguments.Count;
    }

    private static bool TryStopArguments(
        IReadOnlyList<string> arguments,
        out ulong? atStep,
        out EntityId? assemblyId)
    {
        atStep = null;
        assemblyId = null;
        var index = 1;
        if (index < arguments.Count && string.Equals(arguments[index], "assembly", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= arguments.Count || !TryEntityId(arguments[index + 1], out var id))
                return false;
            assemblyId = id;
            index += 2;
        }

        if (index < arguments.Count)
        {
            if (!TryStep(arguments[index], out var step))
                return false;
            atStep = step;
            index++;
        }

        return index == arguments.Count;
    }

    private static bool TryStep(string text, out ulong step) =>
        ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out step);

    private static bool TryEntityId(string text, out EntityId id)
    {
        id = default;
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
            return false;
        id = new EntityId(value);
        return true;
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

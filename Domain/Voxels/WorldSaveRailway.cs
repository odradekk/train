using System.Text.Json;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

public sealed class WorldSaveRailway
{
    public static WorldSaveRailway Empty { get; } = new(
        0,
        Array.Empty<PhysicalRailSegment>(),
        Array.Empty<RailConnection>(),
        Array.Empty<RailOccupancyCell>());

    public int SleeperCount { get; }
    public IReadOnlyList<PhysicalRailSegment> Segments { get; }
    public IReadOnlyList<RailConnection> Connections { get; }
    public IReadOnlyList<RailOccupancyCell> Occupancy { get; }

    public WorldSaveRailway(
        int sleeperCount,
        IReadOnlyList<PhysicalRailSegment> segments,
        IReadOnlyList<RailConnection> connections,
        IReadOnlyList<RailOccupancyCell> occupancy)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(occupancy);
        SleeperCount = sleeperCount;
        Segments = segments;
        Connections = connections;
        Occupancy = occupancy;
    }

    public bool HasContent =>
        SleeperCount != 0 || Segments.Count > 0 || Connections.Count > 0 || Occupancy.Count > 0;

    public static WorldSaveRailway Capture(PhysicalRailState rails)
    {
        ArgumentNullException.ThrowIfNull(rails);
        return new WorldSaveRailway(
            rails.SleeperCount,
            rails.Segments,
            rails.Connections,
            rails.OccupancyCells);
    }

    public IEnumerable<ulong> CollectIds()
    {
        foreach (var segment in Segments)
            yield return segment.Id.Value;
    }
}

internal static class WorldSaveRailwayCodec
{
    public static IReadOnlyList<TrainAssembly> CaptureAssemblies(IEnumerable<TrainAssembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return assemblies.OrderBy(item => item.Id.Value).ToArray();
    }

    public static void WriteRailway(Utf8JsonWriter writer, WorldSaveRailway railway)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(railway);
        writer.WritePropertyName("railway");
        writer.WriteStartObject();
        writer.WriteNumber("sleeper_count", railway.SleeperCount);
        writer.WritePropertyName("segments");
        writer.WriteStartArray();
        foreach (var segment in railway.Segments.OrderBy(item => item.Id.Value))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", segment.Id.Value);
            WriteAlignment(writer, segment.Alignment);
            WriteVolumes(writer, "rail_volumes", segment.RailVolumes);
            WriteVolumes(writer, "sleeper_volumes", segment.SleeperVolumes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("connections");
        writer.WriteStartArray();
        foreach (var connection in railway.Connections
                     .OrderBy(item => item.FromSegment.Value)
                     .ThenBy(item => item.ToSegment.Value)
                     .ThenBy(item => item.FromEnd)
                     .ThenBy(item => item.ToEnd))
        {
            writer.WriteStartObject();
            writer.WriteNumber("from_segment_id", connection.FromSegment.Value);
            writer.WriteString("from_end", FormatEnd(connection.FromEnd));
            writer.WriteNumber("to_segment_id", connection.ToSegment.Value);
            writer.WriteString("to_end", FormatEnd(connection.ToEnd));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("occupancy");
        writer.WriteStartArray();
        foreach (var cell in railway.Occupancy
                     .OrderBy(item => item.Cell.X)
                     .ThenBy(item => item.Cell.Y)
                     .ThenBy(item => item.Cell.Z))
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", cell.Cell.X);
            writer.WriteNumber("y", cell.Cell.Y);
            writer.WriteNumber("z", cell.Cell.Z);
            writer.WritePropertyName("supports");
            writer.WriteStartArray();
            foreach (var support in cell.Supports
                         .OrderBy(item => item.SegmentId.Value)
                         .ThenBy(item => item.WorldX.Start)
                         .ThenBy(item => item.AlongTrack.Start))
            {
                writer.WriteStartObject();
                writer.WriteNumber("segment_id", support.SegmentId.Value);
                WorldSaveVehicleCodec.WriteDouble(writer, "world_x_start", support.WorldX.Start);
                WorldSaveVehicleCodec.WriteDouble(writer, "world_x_end", support.WorldX.End);
                WorldSaveVehicleCodec.WriteDouble(writer, "along_start", support.AlongTrack.Start);
                WorldSaveVehicleCodec.WriteDouble(writer, "along_end", support.AlongTrack.End);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteAssemblies(Utf8JsonWriter writer, IReadOnlyList<TrainAssembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(assemblies);
        writer.WritePropertyName("assemblies");
        writer.WriteStartArray();
        foreach (var assembly in assemblies.OrderBy(item => item.Id.Value))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", assembly.Id.Value);
            writer.WriteNumber("active_locomotive_id", assembly.ActiveLocomotiveId.Value);
            writer.WritePropertyName("vehicle_ids");
            writer.WriteStartArray();
            foreach (var id in assembly.VehicleIds)
                writer.WriteNumberValue(id.Value);
            writer.WriteEndArray();
            writer.WritePropertyName("connections");
            writer.WriteStartArray();
            foreach (var connection in assembly.Connections)
            {
                writer.WriteStartObject();
                writer.WriteNumber("from_vehicle_id", connection.FromVehicleId.Value);
                writer.WriteNumber("from_part_id", connection.FromPartId.Value);
                writer.WriteNumber("from_x", connection.FromCell.X);
                writer.WriteNumber("from_y", connection.FromCell.Y);
                writer.WriteNumber("from_z", connection.FromCell.Z);
                writer.WriteNumber("to_vehicle_id", connection.ToVehicleId.Value);
                writer.WriteNumber("to_part_id", connection.ToPartId.Value);
                writer.WriteNumber("to_x", connection.ToCell.X);
                writer.WriteNumber("to_y", connection.ToCell.Y);
                writer.WriteNumber("to_z", connection.ToCell.Z);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("reference_segment_id", assembly.Reference.SegmentId.Value);
            WorldSaveVehicleCodec.WriteDouble(writer, "distance", assembly.Reference.Distance);
            writer.WriteString("heading", FormatHeading(assembly.Reference.Heading));
            WorldSaveVehicleCodec.WriteDouble(writer, "speed", assembly.Speed);
            writer.WriteBoolean("running", assembly.Running);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static void WriteCommands(Utf8JsonWriter writer, IReadOnlyList<ScheduledTrainCommandQuery> commands)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(commands);
        writer.WritePropertyName("scheduled_commands");
        writer.WriteStartArray();
        foreach (var command in commands.OrderBy(item => item.Sequence))
        {
            writer.WriteStartObject();
            writer.WriteNumber("at_step", command.AtStep);
            writer.WriteBoolean("start", command.Start);
            writer.WriteNumber("sequence", command.Sequence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static bool TryReadRailway(JsonElement element, out WorldSaveRailway railway, out WorldSaveStatus status)
    {
        railway = WorldSaveRailway.Empty;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sleeperCount = 0;
        IReadOnlyList<PhysicalRailSegment>? segments = null;
        IReadOnlyList<RailConnection>? connections = null;
        IReadOnlyList<RailOccupancyCell>? occupancy = null;
        var hasSleepers = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "sleeper_count":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out sleeperCount) ||
                        sleeperCount < 0)
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasSleepers = true;
                    break;
                case "segments":
                    if (!TryReadSegments(property.Value, out segments, out status))
                        return false;
                    break;
                case "connections":
                    if (!TryReadConnections(property.Value, out connections, out status))
                        return false;
                    break;
                case "occupancy":
                    if (!TryReadOccupancy(property.Value, out occupancy, out status))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        if (!hasSleepers || segments is null || connections is null || occupancy is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        railway = new WorldSaveRailway(sleeperCount, segments, connections, occupancy);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool TryReadAssemblies(
        JsonElement element,
        out IReadOnlyList<TrainAssembly> assemblies,
        out WorldSaveStatus status)
    {
        assemblies = Array.Empty<TrainAssembly>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<TrainAssembly>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadAssembly(item, out var assembly, out status))
                return false;
            items.Add(assembly);
        }

        assemblies = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool TryReadCommands(
        JsonElement element,
        out IReadOnlyList<ScheduledTrainCommandQuery> commands,
        out WorldSaveStatus status)
    {
        commands = Array.Empty<ScheduledTrainCommandQuery>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<ScheduledTrainCommandQuery>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadCommand(item, out var command, out status))
                return false;
            items.Add(command);
        }

        commands = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool TryRestore(
        WorldSaveDocument document,
        VoxelWorld world,
        IReadOnlyList<CompositeInstance> vehicles,
        VoxelCatalogSnapshot catalog,
        out PhysicalRailState rails,
        out IReadOnlyList<TrainAssembly> assemblies,
        out WorldSaveStatus status)
    {
        rails = new PhysicalRailState();
        assemblies = Array.Empty<TrainAssembly>();
        status = WorldSaveStatus.InvalidDelta;
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!TryValidateRailway(document.Railway, out status))
            return false;
        if (!TryValidateAssemblies(document.Assemblies, document.Railway, vehicles, catalog, out status))
            return false;
        if (!TryValidateCommands(
                document.SimulationStep,
                document.ScheduledCommands,
                document.ScheduledSequence,
                document.TransactionCount,
                out status))
            return false;

        var extraIds = document.Railway.CollectIds()
            .Concat(document.Assemblies.Select(item => item.Id.Value))
            .ToArray();
        if (!WorldSaveVehicleCodec.TryValidateIdentities(
                world,
                vehicles,
                document.NextEntityId,
                document.TransactionCount,
                out status,
                extraIds))
            return false;

        rails.RestoreFrom(
            document.Railway.Segments,
            document.Railway.Connections,
            document.Railway.Occupancy,
            document.Railway.SleeperCount,
            world);
        assemblies = document.Assemblies.ToArray();
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryValidateRailway(WorldSaveRailway railway, out WorldSaveStatus status)
    {
        status = WorldSaveStatus.InvalidDelta;
        if (!RailEditTransaction.TryValidatePersisted(
                railway.SleeperCount,
                railway.Segments,
                railway.Connections,
                railway.Occupancy))
            return false;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryValidateAssemblies(
        IReadOnlyList<TrainAssembly> assemblies,
        WorldSaveRailway railway,
        IReadOnlyList<CompositeInstance> vehicles,
        VoxelCatalogSnapshot catalog,
        out WorldSaveStatus status)
    {
        status = WorldSaveStatus.InvalidDelta;
        if (assemblies.Count > 1)
            return false;
        var assemblyIds = new HashSet<ulong>();
        var claimedVehicles = new HashSet<EntityId>();
        var vehicleById = vehicles.ToDictionary(item => item.Id);
        var segments = railway.Segments.ToDictionary(item => item.Id);
        foreach (var assembly in assemblies)
        {
            if (assembly.Id.Value == 0 || !assemblyIds.Add(assembly.Id.Value))
                return false;
            if (assembly.VehicleIds.Count == 0 ||
                assembly.VehicleIds.Any(id => !vehicleById.ContainsKey(id) || !claimedVehicles.Add(id)))
                return false;
            if (!assembly.VehicleIds.Contains(assembly.ActiveLocomotiveId))
                return false;
            if (!segments.TryGetValue(assembly.Reference.SegmentId, out var segment) ||
                !assembly.Reference.IsFinite ||
                assembly.Reference.Distance < 0 ||
                assembly.Reference.Distance - segment.Alignment.Length > StaticTrainFixture.ConstraintTolerance ||
                assembly.Reference.Heading != RailHeading.AlongAlignment)
                return false;
            if (!double.IsFinite(assembly.Speed) || assembly.Speed < 0 ||
                (!assembly.Running && assembly.Speed > 0))
                return false;
            if (assembly.VehicleIds.Count != 2 || assembly.Connections.Count != 1)
                return false;
            if (!TryValidateAssemblyMembers(assembly, vehicleById, segment, catalog))
                return false;
        }

        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryValidateAssemblyMembers(
        TrainAssembly assembly,
        IReadOnlyDictionary<EntityId, CompositeInstance> vehicleById,
        PhysicalRailSegment segment,
        VoxelCatalogSnapshot catalog)
    {
        var locomotive = vehicleById[assembly.ActiveLocomotiveId];
        if (!StraightTrainSolver.TryToWorldContact(locomotive, 0, out var front))
            return false;
        if (Math.Abs(StaticTrainFixture.AlongTrackDistance(front.X, segment.Alignment) - assembly.Reference.Distance) >
            StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(front.Y - segment.Alignment.StartY) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(front.Z - segment.Alignment.StartZ) > StaticTrainFixture.ConstraintTolerance)
            return false;

        var carriageId = assembly.VehicleIds.FirstOrDefault(id => id != assembly.ActiveLocomotiveId);
        if (carriageId.Value == 0 || !vehicleById.TryGetValue(carriageId, out var carriage))
            return false;
        if (!StraightTrainSolver.TryValidatePoses(locomotive, carriage, out _))
            return false;

        foreach (var connection in assembly.Connections)
        {
            if (!assembly.VehicleIds.Contains(connection.FromVehicleId) ||
                !assembly.VehicleIds.Contains(connection.ToVehicleId) ||
                connection.FromVehicleId == connection.ToVehicleId)
                return false;
            var from = vehicleById[connection.FromVehicleId];
            var to = vehicleById[connection.ToVehicleId];
            var fromPart = from.PartAt(connection.FromCell);
            var toPart = to.PartAt(connection.ToCell);
            if (fromPart is null || fromPart.Id != connection.FromPartId ||
                toPart is null || toPart.Id != connection.ToPartId)
                return false;
            if (!StraightTrainSolver.TryValidateCouplerConnection(
                    from,
                    to,
                    connection.FromCell,
                    connection.ToCell,
                    out _))
                return false;
        }

        return (!assembly.Running && assembly.Speed == 0) ||
               StraightTrainMotion.TryResolvePropulsion(locomotive, catalog, out _);
    }

    private static bool TryValidateCommands(
        ulong simulationStep,
        IReadOnlyList<ScheduledTrainCommandQuery> commands,
        ulong scheduledSequence,
        ulong transactionCount,
        out WorldSaveStatus status)
    {
        status = WorldSaveStatus.InvalidDelta;
        if (scheduledSequence > transactionCount)
            return false;
        var sequences = new HashSet<ulong>();
        foreach (var command in commands)
        {
            if (command.AtStep < simulationStep ||
                command.Sequence >= scheduledSequence ||
                !sequences.Add(command.Sequence))
                return false;
        }

        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadSegments(
        JsonElement element,
        out IReadOnlyList<PhysicalRailSegment> segments,
        out WorldSaveStatus status)
    {
        segments = Array.Empty<PhysicalRailSegment>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<PhysicalRailSegment>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadSegment(item, out var segment, out status))
                return false;
            items.Add(segment);
        }

        segments = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadSegment(JsonElement element, out PhysicalRailSegment segment, out WorldSaveStatus status)
    {
        segment = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong id = 0;
        var hasId = false;
        var hasStartX = false;
        var hasStartY = false;
        var hasStartZ = false;
        var hasEndX = false;
        var hasEndY = false;
        var hasEndZ = false;
        double startX = 0, startY = 0, startZ = 0, endX = 0, endY = 0, endZ = 0;
        IReadOnlyList<RailVolumeBox>? rails = null;
        IReadOnlyList<RailVolumeBox>? sleepers = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out id))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasId = true;
                    break;
                case "start_x":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out startX, out status))
                        return false;
                    hasStartX = true;
                    break;
                case "start_y":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out startY, out status))
                        return false;
                    hasStartY = true;
                    break;
                case "start_z":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out startZ, out status))
                        return false;
                    hasStartZ = true;
                    break;
                case "end_x":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out endX, out status))
                        return false;
                    hasEndX = true;
                    break;
                case "end_y":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out endY, out status))
                        return false;
                    hasEndY = true;
                    break;
                case "end_z":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out endZ, out status))
                        return false;
                    hasEndZ = true;
                    break;
                case "rail_volumes":
                    if (!TryReadVolumes(property.Value, out rails, out status))
                        return false;
                    break;
                case "sleeper_volumes":
                    if (!TryReadVolumes(property.Value, out sleepers, out status))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        if (!hasId || !hasStartX || !hasStartY || !hasStartZ || !hasEndX || !hasEndY || !hasEndZ ||
            rails is null || sleepers is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        segment = new PhysicalRailSegment(
            new EntityId(id),
            new HorizontalStraightAlignment(startX, startY, startZ, endX, endY, endZ),
            rails,
            sleepers);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadConnections(
        JsonElement element,
        out IReadOnlyList<RailConnection> connections,
        out WorldSaveStatus status)
    {
        connections = Array.Empty<RailConnection>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<RailConnection>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadConnection(item, out var connection, out status))
                return false;
            items.Add(connection);
        }

        connections = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadConnection(JsonElement element, out RailConnection connection, out WorldSaveStatus status)
    {
        connection = new RailConnection(new EntityId(1), RailEnd.Start, new EntityId(1), RailEnd.Start);
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong fromId = 0, toId = 0;
        RailEnd fromEnd = default, toEnd = default;
        var hasFrom = false;
        var hasTo = false;
        var hasFromEnd = false;
        var hasToEnd = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "from_segment_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out fromId))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasFrom = true;
                    break;
                case "to_segment_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out toId))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasTo = true;
                    break;
                case "from_end":
                    if (!TryReadEnd(property.Value, out fromEnd))
                        return false;
                    hasFromEnd = true;
                    break;
                case "to_end":
                    if (!TryReadEnd(property.Value, out toEnd))
                        return false;
                    hasToEnd = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasFrom || !hasTo || !hasFromEnd || !hasToEnd)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        connection = new RailConnection(new EntityId(fromId), fromEnd, new EntityId(toId), toEnd);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadOccupancy(
        JsonElement element,
        out IReadOnlyList<RailOccupancyCell> occupancy,
        out WorldSaveStatus status)
    {
        occupancy = Array.Empty<RailOccupancyCell>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<RailOccupancyCell>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadOccupancyCell(item, out var cell, out status))
                return false;
            items.Add(cell);
        }

        occupancy = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadOccupancyCell(JsonElement element, out RailOccupancyCell occupancy, out WorldSaveStatus status)
    {
        occupancy = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var x = 0;
        var y = 0;
        var z = 0;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        IReadOnlyList<RailCellSupport>? supports = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "x":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out x))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasX = true;
                    break;
                case "y":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out y))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasY = true;
                    break;
                case "z":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out z))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasZ = true;
                    break;
                case "supports":
                    if (!TryReadSupports(property.Value, out supports, out status))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        if (!hasX || !hasY || !hasZ || supports is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        occupancy = new RailOccupancyCell(new WorldVoxelCoord(x, y, z), supports);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadSupports(
        JsonElement element,
        out IReadOnlyList<RailCellSupport> supports,
        out WorldSaveStatus status)
    {
        supports = Array.Empty<RailCellSupport>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<RailCellSupport>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadSupport(item, out var support, out status))
                return false;
            items.Add(support);
        }

        supports = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadSupport(JsonElement element, out RailCellSupport support, out WorldSaveStatus status)
    {
        support = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong segmentId = 0;
        var hasId = false;
        var hasWorldStart = false;
        var hasWorldEnd = false;
        var hasAlongStart = false;
        var hasAlongEnd = false;
        double worldStart = 0, worldEnd = 0, alongStart = 0, alongEnd = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "segment_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out segmentId))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasId = true;
                    break;
                case "world_x_start":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out worldStart, out status))
                        return false;
                    hasWorldStart = true;
                    break;
                case "world_x_end":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out worldEnd, out status))
                        return false;
                    hasWorldEnd = true;
                    break;
                case "along_start":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out alongStart, out status))
                        return false;
                    hasAlongStart = true;
                    break;
                case "along_end":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out alongEnd, out status))
                        return false;
                    hasAlongEnd = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasId || !hasWorldStart || !hasWorldEnd || !hasAlongStart || !hasAlongEnd)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        support = new RailCellSupport(
            new EntityId(segmentId),
            new RailInterval(worldStart, worldEnd),
            new RailInterval(alongStart, alongEnd));
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadAssembly(JsonElement element, out TrainAssembly assembly, out WorldSaveStatus status)
    {
        assembly = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong id = 0, locoId = 0, segmentId = 0;
        var hasId = false;
        var hasLoco = false;
        var hasSegment = false;
        var hasDistance = false;
        var hasHeading = false;
        var hasSpeed = false;
        var hasRunning = false;
        IReadOnlyList<EntityId>? vehicleIds = null;
        IReadOnlyList<CouplerConnectionQuery>? connections = null;
        double distance = 0, speed = 0;
        var running = false;
        RailHeading heading = default;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out id))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasId = true;
                    break;
                case "active_locomotive_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out locoId))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasLoco = true;
                    break;
                case "vehicle_ids":
                    if (!TryReadEntityIds(property.Value, out vehicleIds, out status))
                        return false;
                    break;
                case "connections":
                    if (!TryReadCouplers(property.Value, out connections, out status))
                        return false;
                    break;
                case "reference_segment_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out segmentId))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasSegment = true;
                    break;
                case "distance":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out distance, out status))
                        return false;
                    hasDistance = true;
                    break;
                case "heading":
                    if (!TryReadHeading(property.Value, out heading))
                        return false;
                    hasHeading = true;
                    break;
                case "speed":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out speed, out status))
                        return false;
                    hasSpeed = true;
                    break;
                case "running":
                    if (property.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                        return false;
                    running = property.Value.GetBoolean();
                    hasRunning = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasId || !hasLoco || vehicleIds is null || connections is null || !hasSegment ||
            !hasDistance || !hasHeading || !hasSpeed || !hasRunning)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        if (speed < 0)
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }

        assembly = new TrainAssembly(
            new EntityId(id),
            new EntityId(locoId),
            new RailPosition(new EntityId(segmentId), distance, heading),
            vehicleIds,
            connections,
            speed,
            running);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadCommand(
        JsonElement element,
        out ScheduledTrainCommandQuery command,
        out WorldSaveStatus status)
    {
        command = new ScheduledTrainCommandQuery(0, false, 0);
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong atStep = 0, sequence = 0;
        var start = false;
        var hasAt = false;
        var hasStart = false;
        var hasSequence = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "at_step":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetUInt64(out atStep))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasAt = true;
                    break;
                case "start":
                    if (property.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                        return false;
                    start = property.Value.GetBoolean();
                    hasStart = true;
                    break;
                case "sequence":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetUInt64(out sequence))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasSequence = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasAt || !hasStart || !hasSequence)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        command = new ScheduledTrainCommandQuery(atStep, start, sequence);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadCouplers(
        JsonElement element,
        out IReadOnlyList<CouplerConnectionQuery> connections,
        out WorldSaveStatus status)
    {
        connections = Array.Empty<CouplerConnectionQuery>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<CouplerConnectionQuery>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadCoupler(item, out var connection, out status))
                return false;
            items.Add(connection);
        }

        connections = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadCoupler(
        JsonElement element,
        out CouplerConnectionQuery connection,
        out WorldSaveStatus status)
    {
        connection = new CouplerConnectionQuery(
            new EntityId(1),
            new EntityId(1),
            default,
            new EntityId(1),
            new EntityId(1),
            default);
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong fromVehicle = 0, fromPart = 0, toVehicle = 0, toPart = 0;
        var fromX = 0;
        var fromY = 0;
        var fromZ = 0;
        var toX = 0;
        var toY = 0;
        var toZ = 0;
        var hasFromVehicle = false;
        var hasFromPart = false;
        var hasToVehicle = false;
        var hasToPart = false;
        var hasFromX = false;
        var hasFromY = false;
        var hasFromZ = false;
        var hasToX = false;
        var hasToY = false;
        var hasToZ = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            if (property.Value.ValueKind != JsonValueKind.Number)
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            switch (property.Name)
            {
                case "from_vehicle_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out fromVehicle))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasFromVehicle = true;
                    break;
                case "from_part_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out fromPart))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasFromPart = true;
                    break;
                case "to_vehicle_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out toVehicle))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasToVehicle = true;
                    break;
                case "to_part_id":
                    if (!WorldSaveVehicleCodec.TryReadEntityId(property.Value, out toPart))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasToPart = true;
                    break;
                case "from_x":
                    if (!property.Value.TryGetInt32(out fromX))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasFromX = true;
                    break;
                case "from_y":
                    if (!property.Value.TryGetInt32(out fromY))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasFromY = true;
                    break;
                case "from_z":
                    if (!property.Value.TryGetInt32(out fromZ))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasFromZ = true;
                    break;
                case "to_x":
                    if (!property.Value.TryGetInt32(out toX))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasToX = true;
                    break;
                case "to_y":
                    if (!property.Value.TryGetInt32(out toY))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasToY = true;
                    break;
                case "to_z":
                    if (!property.Value.TryGetInt32(out toZ))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasToZ = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasFromVehicle || !hasFromPart || !hasToVehicle || !hasToPart ||
            !hasFromX || !hasFromY || !hasFromZ || !hasToX || !hasToY || !hasToZ)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        connection = new CouplerConnectionQuery(
            new EntityId(fromVehicle),
            new EntityId(fromPart),
            new LocalVoxelCoord(fromX, fromY, fromZ),
            new EntityId(toVehicle),
            new EntityId(toPart),
            new LocalVoxelCoord(toX, toY, toZ));
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadEntityIds(
        JsonElement element,
        out IReadOnlyList<EntityId> ids,
        out WorldSaveStatus status)
    {
        ids = Array.Empty<EntityId>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<EntityId>();
        foreach (var item in element.EnumerateArray())
        {
            if (!WorldSaveVehicleCodec.TryReadEntityId(item, out var id))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            items.Add(new EntityId(id));
        }

        ids = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadVolumes(
        JsonElement element,
        out IReadOnlyList<RailVolumeBox> volumes,
        out WorldSaveStatus status)
    {
        volumes = Array.Empty<RailVolumeBox>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<RailVolumeBox>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadVolume(item, out var volume, out status))
                return false;
            items.Add(volume);
        }

        volumes = items;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadVolume(JsonElement element, out RailVolumeBox volume, out WorldSaveStatus status)
    {
        volume = default;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        var hasMinX = false;
        var hasMinY = false;
        var hasMinZ = false;
        var hasMaxX = false;
        var hasMaxY = false;
        var hasMaxZ = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "min_x":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out minX, out status))
                        return false;
                    hasMinX = true;
                    break;
                case "min_y":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out minY, out status))
                        return false;
                    hasMinY = true;
                    break;
                case "min_z":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out minZ, out status))
                        return false;
                    hasMinZ = true;
                    break;
                case "max_x":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out maxX, out status))
                        return false;
                    hasMaxX = true;
                    break;
                case "max_y":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out maxY, out status))
                        return false;
                    hasMaxY = true;
                    break;
                case "max_z":
                    if (!WorldSaveVehicleCodec.TryReadFiniteDouble(property.Value, out maxZ, out status))
                        return false;
                    hasMaxZ = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasMinX || !hasMinY || !hasMinZ || !hasMaxX || !hasMaxY || !hasMaxZ)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        volume = new RailVolumeBox(minX, minY, minZ, maxX, maxY, maxZ);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static void WriteAlignment(Utf8JsonWriter writer, HorizontalStraightAlignment alignment)
    {
        WorldSaveVehicleCodec.WriteDouble(writer, "start_x", alignment.StartX);
        WorldSaveVehicleCodec.WriteDouble(writer, "start_y", alignment.StartY);
        WorldSaveVehicleCodec.WriteDouble(writer, "start_z", alignment.StartZ);
        WorldSaveVehicleCodec.WriteDouble(writer, "end_x", alignment.EndX);
        WorldSaveVehicleCodec.WriteDouble(writer, "end_y", alignment.EndY);
        WorldSaveVehicleCodec.WriteDouble(writer, "end_z", alignment.EndZ);
    }

    private static void WriteVolumes(Utf8JsonWriter writer, string name, IReadOnlyList<RailVolumeBox> volumes)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var volume in volumes)
        {
            writer.WriteStartObject();
            WorldSaveVehicleCodec.WriteDouble(writer, "min_x", volume.MinX);
            WorldSaveVehicleCodec.WriteDouble(writer, "min_y", volume.MinY);
            WorldSaveVehicleCodec.WriteDouble(writer, "min_z", volume.MinZ);
            WorldSaveVehicleCodec.WriteDouble(writer, "max_x", volume.MaxX);
            WorldSaveVehicleCodec.WriteDouble(writer, "max_y", volume.MaxY);
            WorldSaveVehicleCodec.WriteDouble(writer, "max_z", volume.MaxZ);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string FormatEnd(RailEnd end) => end == RailEnd.Start ? "start" : "end";

    private static string FormatHeading(RailHeading heading) =>
        heading == RailHeading.AgainstAlignment ? "against" : "along";

    private static bool TryReadEnd(JsonElement element, out RailEnd end)
    {
        end = default;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        switch (element.GetString())
        {
            case "start":
                end = RailEnd.Start;
                return true;
            case "end":
                end = RailEnd.End;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadHeading(JsonElement element, out RailHeading heading)
    {
        heading = default;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        switch (element.GetString())
        {
            case "along":
                heading = RailHeading.AlongAlignment;
                return true;
            case "against":
                heading = RailHeading.AgainstAlignment;
                return true;
            default:
                return false;
        }
    }

}

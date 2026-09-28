using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

public static class WorldSaveAuthority
{
    public static string Fingerprint(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("next_entity_id", session.NextEntityId);
            writer.WriteNumber("transaction_count", session.TransactionCount);
            writer.WriteNumber("simulation_step", session.SimulationStep);
            writer.WriteNumber("scheduled_sequence", session.ScheduledCommandSequence);
            WorldSaveRailwayCodec.WriteCommands(writer, session.QueryScheduledTrainCommands());
            writer.WritePropertyName("world");
            writer.WriteStartArray();
            foreach (var (position, voxel) in session.World.OccupiedVoxels()
                         .OrderBy(item => item.Key.X)
                         .ThenBy(item => item.Key.Y)
                         .ThenBy(item => item.Key.Z))
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", position.X);
                writer.WriteNumber("y", position.Y);
                writer.WriteNumber("z", position.Z);
                writer.WriteString("definition_id", voxel.DefinitionId.Value);
                writer.WriteString("orientation", voxel.Orientation.ToString());
                writer.WriteNumber("durability", voxel.CurrentDurability);
                if (voxel.RuntimeId is { } runtime)
                    writer.WriteNumber("runtime_id", runtime.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("vehicles");
            writer.WriteStartArray();
            foreach (var vehicle in session.Vehicles.OrderBy(item => item.Id.Value))
                WriteVehicle(writer, vehicle);
            writer.WriteEndArray();
            WorldSaveRailwayCodec.WriteRailway(writer, WorldSaveRailway.Capture(session.Rails));
            WorldSaveRailwayCodec.WriteAssemblies(writer, session.QueryTrainAssemblies()
                .Select(query => new TrainAssembly(
                    query.Id,
                    query.ActiveLocomotiveId,
                    query.Reference,
                    query.VehicleIds,
                    query.Connections,
                    query.Speed,
                    query.Running))
                .ToArray());
            WorldEditCodec.WriteQueue(writer, session.CaptureEditQueue());
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteVehicle(Utf8JsonWriter writer, CompositeInstance vehicle)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", vehicle.Id.Value);
        writer.WriteString("definition_id", vehicle.DefinitionId.Value);
        writer.WriteString("last_used_revision", vehicle.LastUsedRevision);
        writer.WriteString("archetype_id", vehicle.ArchetypeId.Value);
        writer.WriteString("last_used_archetype_revision", vehicle.LastUsedArchetypeRevision);
        writer.WriteString("size_class", vehicle.SizeClass.ToString());
        WorldSaveVehicleCodec.WriteDouble(writer, "origin_x", vehicle.Transform.OriginX);
        WorldSaveVehicleCodec.WriteDouble(writer, "origin_y", vehicle.Transform.OriginY);
        WorldSaveVehicleCodec.WriteDouble(writer, "origin_z", vehicle.Transform.OriginZ);
        WorldSaveVehicleCodec.WriteDouble(writer, "right_x", vehicle.Transform.RightX);
        WorldSaveVehicleCodec.WriteDouble(writer, "right_y", vehicle.Transform.RightY);
        WorldSaveVehicleCodec.WriteDouble(writer, "right_z", vehicle.Transform.RightZ);
        WorldSaveVehicleCodec.WriteDouble(writer, "up_x", vehicle.Transform.UpX);
        WorldSaveVehicleCodec.WriteDouble(writer, "up_y", vehicle.Transform.UpY);
        WorldSaveVehicleCodec.WriteDouble(writer, "up_z", vehicle.Transform.UpZ);
        writer.WritePropertyName("parts");
        writer.WriteStartArray();
        foreach (var part in vehicle.Parts.OrderBy(item => item.Id.Value))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", part.Id.Value);
            writer.WriteNumber("x", part.Cell.X);
            writer.WriteNumber("y", part.Cell.Y);
            writer.WriteNumber("z", part.Cell.Z);
            writer.WriteString("definition_id", part.DefinitionId.Value);
            writer.WriteString("orientation", part.Orientation.ToString());
            writer.WriteNumber("durability", part.CurrentDurability);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("contacts");
        writer.WriteStartArray();
        foreach (var contact in vehicle.Contacts)
        {
            writer.WriteStartObject();
            WorldSaveVehicleCodec.WriteDouble(writer, "x", contact.X);
            WorldSaveVehicleCodec.WriteDouble(writer, "y", contact.Y);
            WorldSaveVehicleCodec.WriteDouble(writer, "z", contact.Z);
            writer.WriteNumber("bound_x", contact.BoundCell.X);
            writer.WriteNumber("bound_y", contact.BoundCell.Y);
            writer.WriteNumber("bound_z", contact.BoundCell.Z);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

/// <summary>
/// Canonical fingerprint of a source's established base. It is not the voxel occupancy hash and it is
/// not <see cref="WorldSaveAuthority"/>'s live authority fingerprint.
/// </summary>
internal static class WorldSourceBase
{
    public static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                return false;
        }

        return true;
    }

    public static string Fingerprint(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.WorldSource is null)
            throw new InvalidOperationException("A configuration base fingerprint requires a world source.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            Write(writer, session);
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, VoxelDevelopmentSession session)
    {
        var source = session.WorldSource!;
        var identity = session.Identity;
        writer.WriteStartObject();
        if (source.Kind == WorldSource.BlankKind)
        {
            writer.WriteString("source_kind", source.Kind);
            writer.WriteNumber("source_version", source.ConfigurationVersion);
        }
        else
        {
            writer.WriteString("configuration_id", source.ConfigurationId);
            writer.WriteNumber("configuration_version", source.ConfigurationVersion);
        }
        writer.WriteNumber("seed", identity.Seed);
        writer.WriteString("generator_version", identity.GeneratorVersion);
        writer.WriteNumber("bounds_x", identity.Bounds.SizeX);
        writer.WriteNumber("bounds_y", identity.Bounds.SizeY);
        writer.WriteNumber("bounds_z", identity.Bounds.SizeZ);
        writer.WriteNumber("chunk_size", identity.ChunkSize);
        writer.WriteNumber("next_entity_id", session.NextEntityId);
        writer.WriteNumber("transaction_count", session.TransactionCount);
        WriteVoxels(writer, session);
        WriteRails(writer, session);
        WriteVehicles(writer, session);
        WriteAssemblies(writer, session);
        writer.WriteEndObject();
    }

    private static void WriteVoxels(Utf8JsonWriter writer, VoxelDevelopmentSession session)
    {
        writer.WritePropertyName("voxels");
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
            if (voxel.RuntimeId is { } runtime)
                writer.WriteNumber("runtime_id", runtime.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRails(Utf8JsonWriter writer, VoxelDevelopmentSession session)
    {
        writer.WritePropertyName("rails");
        writer.WriteStartObject();
        writer.WritePropertyName("segments");
        writer.WriteStartArray();
        foreach (var segment in session.Rails.Segments.OrderBy(item => item.Id.Value))
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
        foreach (var connection in session.Rails.Connections
                     .OrderBy(item => item.FromSegment.Value)
                     .ThenBy(item => item.FromEnd)
                     .ThenBy(item => item.ToSegment.Value)
                     .ThenBy(item => item.ToEnd))
        {
            writer.WriteStartObject();
            writer.WriteNumber("from_segment", connection.FromSegment.Value);
            writer.WriteString("from_end", connection.FromEnd.ToString());
            writer.WriteNumber("to_segment", connection.ToSegment.Value);
            writer.WriteString("to_end", connection.ToEnd.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("occupancy");
        writer.WriteStartArray();
        foreach (var cell in session.Rails.OccupancyCells
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
                WriteDouble(writer, "world_x_start", support.WorldX.Start);
                WriteDouble(writer, "world_x_end", support.WorldX.End);
                WriteDouble(writer, "along_start", support.AlongTrack.Start);
                WriteDouble(writer, "along_end", support.AlongTrack.End);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVehicles(Utf8JsonWriter writer, VoxelDevelopmentSession session)
    {
        writer.WritePropertyName("vehicles");
        writer.WriteStartArray();
        foreach (var vehicle in session.Vehicles.OrderBy(item => item.Id.Value))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", vehicle.Id.Value);
            writer.WriteString("definition_id", vehicle.DefinitionId.Value);
            writer.WriteString("archetype_id", vehicle.ArchetypeId.Value);
            writer.WriteString("size_class", VehicleSize.ToWire(vehicle.SizeClass));
            WriteDouble(writer, "origin_x", vehicle.Transform.OriginX);
            WriteDouble(writer, "origin_y", vehicle.Transform.OriginY);
            WriteDouble(writer, "origin_z", vehicle.Transform.OriginZ);
            WriteDouble(writer, "right_x", vehicle.Transform.RightX);
            WriteDouble(writer, "right_y", vehicle.Transform.RightY);
            WriteDouble(writer, "right_z", vehicle.Transform.RightZ);
            WriteDouble(writer, "up_x", vehicle.Transform.UpX);
            WriteDouble(writer, "up_y", vehicle.Transform.UpY);
            WriteDouble(writer, "up_z", vehicle.Transform.UpZ);
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
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("contacts");
            writer.WriteStartArray();
            foreach (var contact in vehicle.Contacts)
            {
                writer.WriteStartObject();
                WriteDouble(writer, "x", contact.X);
                WriteDouble(writer, "y", contact.Y);
                WriteDouble(writer, "z", contact.Z);
                writer.WriteNumber("bound_x", contact.BoundCell.X);
                writer.WriteNumber("bound_y", contact.BoundCell.Y);
                writer.WriteNumber("bound_z", contact.BoundCell.Z);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAssemblies(Utf8JsonWriter writer, VoxelDevelopmentSession session)
    {
        writer.WritePropertyName("assemblies");
        writer.WriteStartArray();
        foreach (var assembly in session.QueryTrainAssemblies())
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", assembly.Id.Value);
            if (assembly.ActiveLocomotiveId is { } locomotive)
                writer.WriteNumber("active_locomotive", locomotive.Value);
            else
                writer.WriteNull("active_locomotive");
            writer.WriteNumber("segment_id", assembly.Reference.SegmentId.Value);
            WriteDouble(writer, "distance", assembly.Reference.Distance);
            writer.WriteString("heading", assembly.Reference.Heading.ToString());
            writer.WritePropertyName("vehicle_ids");
            writer.WriteStartArray();
            foreach (var id in assembly.VehicleIds)
                writer.WriteNumberValue(id.Value);
            writer.WriteEndArray();
            writer.WritePropertyName("connections");
            writer.WriteStartArray();
            foreach (var connection in assembly.Connections
                         .OrderBy(item => item.FromVehicleId.Value)
                         .ThenBy(item => item.FromPartId.Value)
                         .ThenBy(item => item.ToVehicleId.Value)
                         .ThenBy(item => item.ToPartId.Value))
            {
                writer.WriteStartObject();
                writer.WriteNumber("from_vehicle", connection.FromVehicleId.Value);
                writer.WriteNumber("from_part", connection.FromPartId.Value);
                writer.WriteNumber("to_vehicle", connection.ToVehicleId.Value);
                writer.WriteNumber("to_part", connection.ToPartId.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAlignment(Utf8JsonWriter writer, HorizontalStraightAlignment alignment)
    {
        writer.WritePropertyName("alignment");
        writer.WriteStartObject();
        WriteDouble(writer, "start_x", alignment.StartX);
        WriteDouble(writer, "start_y", alignment.StartY);
        WriteDouble(writer, "start_z", alignment.StartZ);
        WriteDouble(writer, "end_x", alignment.EndX);
        WriteDouble(writer, "end_y", alignment.EndY);
        WriteDouble(writer, "end_z", alignment.EndZ);
        writer.WriteEndObject();
    }

    private static void WriteVolumes(Utf8JsonWriter writer, string name, IReadOnlyList<RailVolumeBox> volumes)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var volume in volumes)
        {
            writer.WriteStartObject();
            WriteDouble(writer, "min_x", volume.MinX);
            WriteDouble(writer, "min_y", volume.MinY);
            WriteDouble(writer, "min_z", volume.MinZ);
            WriteDouble(writer, "max_x", volume.MaxX);
            WriteDouble(writer, "max_y", volume.MaxY);
            WriteDouble(writer, "max_z", volume.MaxZ);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(VoxelDefinition.FormatMass(value));
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.Playground;

public static class AuthorityCodec
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    public static string Encode(AuthorityStateModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Encoding.UTF8.GetString(EncodeUtf8(model));
    }

    public static string Digest(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static AuthorityDocument Document(AuthorityStateModel model)
    {
        var canonical = Encode(model);
        return new AuthorityDocument(canonical, Digest(canonical));
    }

    public static bool TryDecode(string? json, out AuthorityStateModel model, out AuthorityDecodeStatus status)
    {
        model = null!;
        status = AuthorityDecodeStatus.Invalid;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            return TryReadModel(document.RootElement, out model, out status);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static AuthorityStateModel Capture(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var identity = session.Identity;
        var queue = session.CaptureEditQueue();
        var fault = session.Fault;
        return new AuthorityStateModel(
            identity.Seed,
            identity.GeneratorVersion,
            identity.Bounds.SizeX,
            identity.Bounds.SizeY,
            identity.Bounds.SizeZ,
            identity.ChunkSize,
            session.NextEntityId,
            session.TransactionCount,
            session.SimulationStep,
            queue.NextEditId,
            queue.BoundarySequence,
            fault is null
                ? null
                : new AuthorityFault(
                    fault.EditId,
                    fault.Kind is { } kind ? WorldEditCodec.WireKind(kind) : null,
                    fault.Position.Step,
                    fault.Position.Sequence,
                    fault.ExceptionType,
                    fault.Message),
            session.World.OccupiedVoxels().Select(pair => new AuthorityVoxel(
                pair.Key.X,
                pair.Key.Y,
                pair.Key.Z,
                pair.Value.DefinitionId.Value,
                pair.Value.Orientation.ToString(),
                pair.Value.CurrentDurability,
                pair.Value.RuntimeId?.Value)).ToArray(),
            WorldSaveVehicleCodec.CaptureLocals(session.Vehicles).Select(ToLocal).ToArray(),
            session.Vehicles.Select(ToVehicle).ToArray(),
            ToRailway(session.Rails),
            session.QueryTrainAssemblies().Select(ToAssembly).ToArray(),
            queue.Pending.Select(record => new AuthorityPending(
                record.Id,
                record.AcceptedAt.Step,
                record.AcceptedAt.Sequence,
                record.TargetStep,
                record.Edit.Canonical)).ToArray());
    }

    public static AuthorityContext Context(VoxelDevelopmentSession session, string? buildId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var voxels = session.CurrentVoxels ?? throw new InvalidOperationException("Voxel content must be loaded.");
        var composites = session.CurrentComposites;
        var source = session.WorldSource;
        return new AuthorityContext(
            AuthorityFieldCatalog.FormatVersion,
            string.IsNullOrWhiteSpace(buildId) ? AuthorityEnvironment.CurrentBuildId : buildId,
            ContentPublishEdit.For(voxels, composites).ContentDigest,
            SemanticLines(voxels, composites),
            source?.Kind ?? "unspecified",
            source?.ConfigurationId,
            source?.ConfigurationVersion,
            session.Identity.Seed,
            session.Identity.GeneratorVersion,
            AuthorityEnvironment.OperatingSystem,
            AuthorityEnvironment.Architecture,
            AuthorityEnvironment.Framework,
            AuthorityFieldCatalog.Guarantee,
            session.SaveId?.ToString("D"));
    }

    public static AuthorityObservation Observe(VoxelDevelopmentSession session, string? buildId = null, string input = "boundary")
    {
        ArgumentNullException.ThrowIfNull(session);
        return new AuthorityObservation(Context(session, buildId), new[] { Checkpoint(session, 0, input) });
    }

    public static AuthorityCheckpoint Checkpoint(VoxelDevelopmentSession session, int index, string input)
    {
        var document = Document(Capture(session));
        return new AuthorityCheckpoint(index, input, session.EditBoundary, document.Canonical, document.Digest);
    }

    public static string EncodeEvidence(AuthorityContext context, AuthorityCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checkpoint);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("evidence_format_version", 1);
            writer.WriteNumber("format_version", context.FormatVersion);
            writer.WriteString("build", context.BuildId ?? "");
            writer.WriteString("content_digest", Required(context.ContentDigest, nameof(context.ContentDigest)));
            writer.WriteStartArray("content_dependencies");
            foreach (var line in context.ContentDependencies)
                writer.WriteStringValue(line);
            writer.WriteEndArray();
            writer.WriteString("source_kind", Required(context.SourceKind, nameof(context.SourceKind)));
            if (context.ConfigurationId is null)
                writer.WriteNull("configuration_id");
            else
                writer.WriteString("configuration_id", context.ConfigurationId);
            if (context.ConfigurationVersion is { } version)
                writer.WriteNumber("configuration_version", version);
            else
                writer.WriteNull("configuration_version");
            writer.WriteNumber("seed", context.Seed);
            writer.WriteString("generator_version", Required(context.GeneratorVersion, nameof(context.GeneratorVersion)));
            writer.WriteString("operating_system", context.OperatingSystem);
            writer.WriteString("architecture", context.Architecture);
            writer.WriteString("framework", context.Framework);
            writer.WriteString("guarantee", context.Guarantee);
            if (context.SaveId is null)
                writer.WriteNull("save_id");
            else
                writer.WriteString("save_id", context.SaveId);
            writer.WriteNumber("index", checkpoint.Index);
            writer.WriteString("input", checkpoint.Input);
            writer.WriteNumber("boundary_step", checkpoint.Boundary.Step);
            writer.WriteNumber("boundary_sequence", checkpoint.Boundary.Sequence);
            writer.WriteString("canonical", checkpoint.Canonical);
            writer.WriteString("digest", checkpoint.Digest);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryReadEvidence(string? json, out AuthorityContext context, out AuthorityCheckpoint checkpoint)
    {
        context = null!;
        checkpoint = null!;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value))
                    return false;
            }

            if (!Take(fields, "evidence_format_version", out var evidenceVersion) ||
                !TryReadInt32(evidenceVersion, out var evidenceFormat) ||
                evidenceFormat != 1)
                return false;
            if (!Take(fields, "format_version", out var format) || !TryReadInt32(format, out var formatVersion))
                return false;
            if (!Take(fields, "build", out var build) || build.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "content_digest", out var content) || content.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "content_dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
                return false;
            var lines = new List<string>();
            foreach (var line in dependencies.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String)
                    return false;
                lines.Add(line.GetString()!);
            }

            if (!Take(fields, "source_kind", out var sourceKind) || sourceKind.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "configuration_id", out var configurationId) ||
                configurationId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return false;
            if (!Take(fields, "configuration_version", out var configurationVersion) ||
                configurationVersion.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                return false;
            int? configurationVersionValue = null;
            if (configurationVersion.ValueKind == JsonValueKind.Number)
            {
                if (!TryReadInt32(configurationVersion, out var parsedConfigurationVersion))
                    return false;
                configurationVersionValue = parsedConfigurationVersion;
            }
            if (!Take(fields, "seed", out var seed) || !seed.TryGetUInt64(out var seedValue))
                return false;
            if (!Take(fields, "generator_version", out var generator) || generator.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "operating_system", out var operatingSystem) || operatingSystem.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "architecture", out var architecture) || architecture.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "framework", out var framework) || framework.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "guarantee", out var guarantee) || guarantee.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "save_id", out var saveId) || saveId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return false;
            if (!Take(fields, "index", out var index) || !index.TryGetInt32(out var indexValue))
                return false;
            if (!Take(fields, "input", out var input) || input.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "boundary_step", out var boundaryStep) || !boundaryStep.TryGetUInt64(out var step))
                return false;
            if (!Take(fields, "boundary_sequence", out var boundarySequence) || !boundarySequence.TryGetUInt64(out var sequence))
                return false;
            if (!Take(fields, "canonical", out var canonical) || canonical.ValueKind != JsonValueKind.String)
                return false;
            if (!Take(fields, "digest", out var digest) || digest.ValueKind != JsonValueKind.String)
                return false;
            if (fields.Count != 0)
                return false;

            var buildId = build.GetString();
            context = new AuthorityContext(
                formatVersion,
                string.IsNullOrEmpty(buildId) ? null : buildId,
                content.GetString()!,
                lines,
                sourceKind.GetString()!,
                configurationId.ValueKind == JsonValueKind.Null ? null : configurationId.GetString(),
                configurationVersionValue,
                seedValue,
                generator.GetString()!,
                operatingSystem.GetString()!,
                architecture.GetString()!,
                framework.GetString()!,
                guarantee.GetString()!,
                saveId.ValueKind == JsonValueKind.Null ? null : saveId.GetString());
            checkpoint = new AuthorityCheckpoint(
                indexValue,
                input.GetString()!,
                new SimulationPosition(step, sequence),
                canonical.GetString()!,
                digest.GetString()!);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Take(Dictionary<string, JsonElement> fields, string name, out JsonElement value) =>
        fields.Remove(name, out value);

    /// <summary>True only for an integer inside <see cref="int"/> range. Fractional and overflowing JSON numbers are false.</summary>
    private static bool TryReadInt32(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    internal static IReadOnlyList<string> SemanticLines(VoxelCatalogSnapshot voxels, CompositeCatalogSnapshot composites)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(composites);
        return voxels.Entries.Select(pair => $"voxel {pair.Key.Value} {pair.Value.SemanticRevision}")
            .Concat(composites.Archetypes.Select(pair => $"archetype {pair.Key.Value} {pair.Value.SemanticRevision}"))
            .Concat(composites.Composites.Select(pair => $"composite {pair.Key.Value} {pair.Value.SemanticRevision}"))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }

    private static byte[] EncodeUtf8(AuthorityStateModel model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("authority_format_version", AuthorityFieldCatalog.FormatVersion);
            writer.WriteNumber("seed", model.Seed);
            writer.WriteString("generator_version", Required(model.GeneratorVersion, nameof(model.GeneratorVersion)));
            writer.WriteStartObject("bounds");
            writer.WriteNumber("x", model.BoundsX);
            writer.WriteNumber("y", model.BoundsY);
            writer.WriteNumber("z", model.BoundsZ);
            writer.WriteEndObject();
            writer.WriteNumber("chunk_size", model.ChunkSize);
            writer.WriteNumber("next_entity_id", model.NextEntityId);
            writer.WriteNumber("transaction_count", model.TransactionCount);
            writer.WriteNumber("simulation_step", model.SimulationStep);
            writer.WriteNumber("next_edit_id", model.NextEditId);
            writer.WriteNumber("boundary_sequence", model.BoundarySequence);
            writer.WritePropertyName("simulation_fault");
            if (model.Fault is null)
                writer.WriteNullValue();
            else
                WriteFault(writer, model.Fault);
            writer.WritePropertyName("world");
            writer.WriteStartArray();
            foreach (var voxel in model.World.OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Z))
                WriteVoxel(writer, voxel);
            writer.WriteEndArray();
            writer.WritePropertyName("save_local_definitions");
            writer.WriteStartArray();
            foreach (var local in model.SaveLocalDefinitions.OrderBy(item => item.Revision, StringComparer.Ordinal))
                WriteLocal(writer, local);
            writer.WriteEndArray();
            writer.WritePropertyName("vehicles");
            writer.WriteStartArray();
            foreach (var vehicle in model.Vehicles.OrderBy(item => item.Id))
                WriteVehicle(writer, vehicle);
            writer.WriteEndArray();
            WriteRailway(writer, model.Railway);
            writer.WritePropertyName("assemblies");
            writer.WriteStartArray();
            foreach (var assembly in model.Assemblies.OrderBy(item => item.Id))
                WriteAssembly(writer, assembly);
            writer.WriteEndArray();
            writer.WritePropertyName("pending");
            writer.WriteStartArray();
            foreach (var pending in model.Pending.OrderBy(item => item.EditId))
                WritePending(writer, pending);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static void WriteFault(Utf8JsonWriter writer, AuthorityFault fault)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("edit_id");
        if (fault.EditId is { } editId)
            writer.WriteNumberValue(editId);
        else
            writer.WriteNullValue();
        writer.WritePropertyName("kind");
        if (fault.Kind is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(fault.Kind);
        writer.WriteNumber("step", fault.Step);
        writer.WriteNumber("sequence", fault.Sequence);
        writer.WriteString("exception_type", Required(fault.ExceptionType, nameof(fault.ExceptionType)));
        writer.WriteString("message", fault.Message ?? "");
        writer.WriteEndObject();
    }

    private static void WriteVoxel(Utf8JsonWriter writer, AuthorityVoxel voxel)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", voxel.X);
        writer.WriteNumber("y", voxel.Y);
        writer.WriteNumber("z", voxel.Z);
        writer.WriteString("definition_id", Required(voxel.DefinitionId, nameof(voxel.DefinitionId)));
        writer.WriteString("orientation", Required(voxel.Orientation, nameof(voxel.Orientation)));
        writer.WriteNumber("durability", voxel.Durability);
        if (voxel.RuntimeId is { } runtime)
            writer.WriteNumber("runtime_id", runtime);
        writer.WriteEndObject();
    }

    private static void WriteLocal(Utf8JsonWriter writer, AuthorityLocalDefinition local)
    {
        writer.WriteStartObject();
        writer.WriteString("revision", Required(local.Revision, nameof(local.Revision)));
        writer.WriteString("id", Required(local.Id, nameof(local.Id)));
        writer.WriteString("archetype_id", Required(local.ArchetypeId, nameof(local.ArchetypeId)));
        writer.WriteString("size_class", Required(local.SizeClass, nameof(local.SizeClass)));
        writer.WritePropertyName("voxels");
        writer.WriteStartArray();
        foreach (var voxel in local.Voxels)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", voxel.X);
            writer.WriteNumber("y", voxel.Y);
            writer.WriteNumber("z", voxel.Z);
            writer.WriteString("definition_id", voxel.DefinitionId);
            writer.WriteString("orientation", voxel.Orientation);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteContacts(writer, local.Contacts);
        writer.WriteEndObject();
    }

    private static void WriteVehicle(Utf8JsonWriter writer, AuthorityVehicle vehicle)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", vehicle.Id);
        writer.WriteString("definition_id", Required(vehicle.DefinitionId, nameof(vehicle.DefinitionId)));
        writer.WriteString("last_used_revision", Required(vehicle.LastUsedRevision, nameof(vehicle.LastUsedRevision)));
        writer.WriteString("archetype_id", Required(vehicle.ArchetypeId, nameof(vehicle.ArchetypeId)));
        writer.WriteString("last_used_archetype_revision", Required(vehicle.LastUsedArchetypeRevision, nameof(vehicle.LastUsedArchetypeRevision)));
        writer.WriteString("size_class", Required(vehicle.SizeClass, nameof(vehicle.SizeClass)));
        if (vehicle.SaveLocalRevision is not null)
            writer.WriteString("save_local_revision", vehicle.SaveLocalRevision);
        writer.WritePropertyName("transform");
        writer.WriteStartObject();
        WriteDouble(writer, "origin_x", vehicle.Transform.OriginX);
        WriteDouble(writer, "origin_y", vehicle.Transform.OriginY);
        WriteDouble(writer, "origin_z", vehicle.Transform.OriginZ);
        WriteDouble(writer, "right_x", vehicle.Transform.RightX);
        WriteDouble(writer, "right_y", vehicle.Transform.RightY);
        WriteDouble(writer, "right_z", vehicle.Transform.RightZ);
        WriteDouble(writer, "up_x", vehicle.Transform.UpX);
        WriteDouble(writer, "up_y", vehicle.Transform.UpY);
        WriteDouble(writer, "up_z", vehicle.Transform.UpZ);
        writer.WriteEndObject();
        writer.WritePropertyName("parts");
        writer.WriteStartArray();
        foreach (var part in vehicle.Parts.OrderBy(item => item.Id))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", part.Id);
            writer.WriteNumber("x", part.X);
            writer.WriteNumber("y", part.Y);
            writer.WriteNumber("z", part.Z);
            writer.WriteString("definition_id", part.DefinitionId);
            writer.WriteString("orientation", part.Orientation);
            writer.WriteNumber("durability", part.Durability);
            writer.WriteString("last_used_revision", part.LastUsedRevision);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteContacts(writer, vehicle.Contacts);
        writer.WriteEndObject();
    }

    private static void WriteContacts(Utf8JsonWriter writer, IReadOnlyList<AuthorityContact> contacts)
    {
        writer.WritePropertyName("contacts");
        writer.WriteStartArray();
        foreach (var contact in contacts)
        {
            writer.WriteStartObject();
            WriteDouble(writer, "x", contact.X);
            WriteDouble(writer, "y", contact.Y);
            WriteDouble(writer, "z", contact.Z);
            writer.WriteNumber("bound_x", contact.BoundX);
            writer.WriteNumber("bound_y", contact.BoundY);
            writer.WriteNumber("bound_z", contact.BoundZ);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRailway(Utf8JsonWriter writer, AuthorityRailway railway)
    {
        ArgumentNullException.ThrowIfNull(railway);
        writer.WritePropertyName("railway");
        writer.WriteStartObject();
        writer.WriteNumber("sleeper_count", railway.SleeperCount);
        writer.WritePropertyName("segments");
        writer.WriteStartArray();
        foreach (var segment in railway.Segments.OrderBy(item => item.Id))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", segment.Id);
            writer.WritePropertyName("alignment");
            writer.WriteStartObject();
            WriteDouble(writer, "start_x", segment.Alignment.StartX);
            WriteDouble(writer, "start_y", segment.Alignment.StartY);
            WriteDouble(writer, "start_z", segment.Alignment.StartZ);
            WriteDouble(writer, "end_x", segment.Alignment.EndX);
            WriteDouble(writer, "end_y", segment.Alignment.EndY);
            WriteDouble(writer, "end_z", segment.Alignment.EndZ);
            writer.WriteEndObject();
            WriteVolumes(writer, "rail_volumes", segment.RailVolumes);
            WriteVolumes(writer, "sleeper_volumes", segment.SleeperVolumes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("connections");
        writer.WriteStartArray();
        foreach (var link in railway.Connections
                     .OrderBy(item => item.FromSegment)
                     .ThenBy(item => item.ToSegment)
                     .ThenBy(item => item.FromEnd, StringComparer.Ordinal)
                     .ThenBy(item => item.ToEnd, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteNumber("from_segment_id", link.FromSegment);
            writer.WriteString("from_end", link.FromEnd);
            writer.WriteNumber("to_segment_id", link.ToSegment);
            writer.WriteString("to_end", link.ToEnd);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("occupancy");
        writer.WriteStartArray();
        foreach (var cell in railway.Occupancy.OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Z))
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", cell.X);
            writer.WriteNumber("y", cell.Y);
            writer.WriteNumber("z", cell.Z);
            writer.WritePropertyName("supports");
            writer.WriteStartArray();
            foreach (var support in cell.Supports
                         .OrderBy(item => item.SegmentId)
                         .ThenBy(item => item.WorldXStart)
                         .ThenBy(item => item.AlongStart)
                         .ThenBy(item => item.WorldXEnd)
                         .ThenBy(item => item.AlongEnd))
            {
                writer.WriteStartObject();
                writer.WriteNumber("segment_id", support.SegmentId);
                WriteDouble(writer, "world_x_start", support.WorldXStart);
                WriteDouble(writer, "world_x_end", support.WorldXEnd);
                WriteDouble(writer, "along_start", support.AlongStart);
                WriteDouble(writer, "along_end", support.AlongEnd);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("destroyed");
        writer.WriteStartArray();
        foreach (var cell in railway.Destroyed.OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Z))
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", cell.X);
            writer.WriteNumber("y", cell.Y);
            writer.WriteNumber("z", cell.Z);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVolumes(Utf8JsonWriter writer, string name, IReadOnlyList<AuthorityVolume> volumes)
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

    private static void WriteAssembly(Utf8JsonWriter writer, AuthorityAssembly assembly)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", assembly.Id);
        writer.WritePropertyName("active_locomotive_id");
        if (assembly.ActiveLocomotiveId is { } locomotive)
            writer.WriteNumberValue(locomotive);
        else
            writer.WriteNullValue();
        writer.WritePropertyName("vehicle_ids");
        writer.WriteStartArray();
        foreach (var id in assembly.VehicleIds)
            writer.WriteNumberValue(id);
        writer.WriteEndArray();
        writer.WritePropertyName("connections");
        writer.WriteStartArray();
        foreach (var coupler in assembly.Connections)
        {
            writer.WriteStartObject();
            writer.WriteNumber("from_vehicle_id", coupler.FromVehicleId);
            writer.WriteNumber("from_part_id", coupler.FromPartId);
            writer.WriteNumber("from_x", coupler.FromX);
            writer.WriteNumber("from_y", coupler.FromY);
            writer.WriteNumber("from_z", coupler.FromZ);
            writer.WriteNumber("to_vehicle_id", coupler.ToVehicleId);
            writer.WriteNumber("to_part_id", coupler.ToPartId);
            writer.WriteNumber("to_x", coupler.ToX);
            writer.WriteNumber("to_y", coupler.ToY);
            writer.WriteNumber("to_z", coupler.ToZ);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("reference_segment_id", assembly.ReferenceSegmentId);
        WriteDouble(writer, "distance", assembly.Distance);
        writer.WriteString("heading", Required(assembly.Heading, nameof(assembly.Heading)));
        WriteDouble(writer, "speed", assembly.Speed);
        writer.WriteBoolean("running", assembly.Running);
        writer.WriteString("halt_reason", Required(assembly.HaltReason, nameof(assembly.HaltReason)));
        writer.WriteEndObject();
    }

    private static void WritePending(Utf8JsonWriter writer, AuthorityPending pending)
    {
        writer.WriteStartObject();
        writer.WriteNumber("edit_id", pending.EditId);
        writer.WriteNumber("accepted_step", pending.AcceptedStep);
        writer.WriteNumber("accepted_sequence", pending.AcceptedSequence);
        writer.WriteNumber("target_step", pending.TargetStep);
        writer.WritePropertyName("edit");
        writer.WriteRawValue(Required(pending.EditCanonical, nameof(pending.EditCanonical)));
        writer.WriteEndObject();
    }

    private static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(WorldEditCodec.FormatDouble(value));
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException("Authority text is required.", name) : value;

    private static AuthorityLocalDefinition ToLocal(WorldSaveLocalDefinition local) =>
        new(
            local.Revision,
            local.Definition.Id.Value,
            local.Definition.ArchetypeId.Value,
            VehicleSize.ToWire(local.Definition.SizeClass),
            local.Definition.Voxels.Select(voxel => new AuthorityLocalVoxel(
                voxel.Cell.X,
                voxel.Cell.Y,
                voxel.Cell.Z,
                voxel.DefinitionId.Value,
                voxel.Orientation.ToString())).ToArray(),
            local.Definition.Contacts.Select(contact => new AuthorityContact(
                contact.X,
                contact.Y,
                contact.Z,
                contact.BoundCell.X,
                contact.BoundCell.Y,
                contact.BoundCell.Z)).ToArray());

    private static AuthorityVehicle ToVehicle(CompositeInstance vehicle) =>
        new(
            vehicle.Id.Value,
            vehicle.DefinitionId.Value,
            vehicle.LastUsedRevision,
            vehicle.ArchetypeId.Value,
            vehicle.LastUsedArchetypeRevision,
            VehicleSize.ToWire(vehicle.SizeClass),
            vehicle.SaveLocalRevision,
            new AuthorityTransform(
                vehicle.Transform.OriginX,
                vehicle.Transform.OriginY,
                vehicle.Transform.OriginZ,
                vehicle.Transform.RightX,
                vehicle.Transform.RightY,
                vehicle.Transform.RightZ,
                vehicle.Transform.UpX,
                vehicle.Transform.UpY,
                vehicle.Transform.UpZ),
            vehicle.Parts.Select(part => new AuthorityPart(
                part.Id.Value,
                part.Cell.X,
                part.Cell.Y,
                part.Cell.Z,
                part.DefinitionId.Value,
                part.Orientation.ToString(),
                part.CurrentDurability,
                part.LastUsedRevision)).ToArray(),
            vehicle.Contacts.Select(contact => new AuthorityContact(
                contact.X,
                contact.Y,
                contact.Z,
                contact.BoundCell.X,
                contact.BoundCell.Y,
                contact.BoundCell.Z)).ToArray());

    private static AuthorityRailway ToRailway(PhysicalRailState rails) =>
        new(
            rails.SleeperCount,
            rails.Segments.Select(segment => new AuthoritySegment(
                segment.Id.Value,
                new AuthorityAlignment(
                    segment.Alignment.StartX,
                    segment.Alignment.StartY,
                    segment.Alignment.StartZ,
                    segment.Alignment.EndX,
                    segment.Alignment.EndY,
                    segment.Alignment.EndZ),
                segment.RailVolumes.Select(ToVolume).ToArray(),
                segment.SleeperVolumes.Select(ToVolume).ToArray())).ToArray(),
            rails.Connections.Select(link => new AuthorityLink(
                link.FromSegment.Value,
                EndWire(link.FromEnd),
                link.ToSegment.Value,
                EndWire(link.ToEnd))).ToArray(),
            rails.OccupancyCells.Select(cell => new AuthorityOccupancy(
                cell.Cell.X,
                cell.Cell.Y,
                cell.Cell.Z,
                cell.Supports.Select(support => new AuthoritySupport(
                    support.SegmentId.Value,
                    support.WorldX.Start,
                    support.WorldX.End,
                    support.AlongTrack.Start,
                    support.AlongTrack.End)).ToArray())).ToArray(),
            rails.DestroyedCells.Select(cell => new AuthorityCell(cell.X, cell.Y, cell.Z)).ToArray());

    private static AuthorityVolume ToVolume(RailVolumeBox box) =>
        new(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);

    private static AuthorityAssembly ToAssembly(TrainAssemblyQuery assembly) =>
        new(
            assembly.Id.Value,
            assembly.ActiveLocomotiveId?.Value,
            assembly.VehicleIds.Select(id => id.Value).ToArray(),
            assembly.Connections.Select(link => new AuthorityCoupler(
                link.FromVehicleId.Value,
                link.FromPartId.Value,
                link.FromCell.X,
                link.FromCell.Y,
                link.FromCell.Z,
                link.ToVehicleId.Value,
                link.ToPartId.Value,
                link.ToCell.X,
                link.ToCell.Y,
                link.ToCell.Z)).ToArray(),
            assembly.Reference.SegmentId.Value,
            assembly.Reference.Distance,
            assembly.Reference.Heading == RailHeading.AlongAlignment ? "along" : "against",
            assembly.Speed,
            assembly.Running,
            assembly.HaltReason switch
            {
                TrainHaltReason.None => "none",
                TrainHaltReason.SupportLost => "support_lost",
                TrainHaltReason.ForwardGap => "forward_gap",
                TrainHaltReason.EndOfLine => "end_of_line",
                _ => throw new ArgumentOutOfRangeException(nameof(assembly), assembly.HaltReason, null)
            });

    private static string EndWire(RailEnd end) => end switch
    {
        RailEnd.Start => "start",
        RailEnd.End => "end",
        _ => throw new ArgumentOutOfRangeException(nameof(end), end, null)
    };

    private static bool TryReadModel(JsonElement root, out AuthorityStateModel model, out AuthorityDecodeStatus status)
    {
        model = null!;
        if (!Fields.TryOpen(root, out var fields, out status) ||
            !fields.TryInt32("authority_format_version", out var version, out status))
            return false;
        if (version != AuthorityFieldCatalog.FormatVersion)
            return Fail(out model, out status, AuthorityDecodeStatus.UnknownFormat);
        if (!fields.TryUInt64("seed", out var seed, out status) ||
            !fields.TryString("generator_version", out var generator, out status) ||
            !fields.TryObject("bounds", out var bounds, out status) ||
            !bounds.TryInt32("x", out var boundsX, out status) ||
            !bounds.TryInt32("y", out var boundsY, out status) ||
            !bounds.TryInt32("z", out var boundsZ, out status) ||
            !bounds.TryFinish(out status) ||
            !fields.TryInt32("chunk_size", out var chunk, out status) ||
            !fields.TryUInt64("next_entity_id", out var nextEntity, out status) ||
            !fields.TryUInt64("transaction_count", out var transactions, out status) ||
            !fields.TryUInt64("simulation_step", out var step, out status) ||
            !fields.TryUInt64("next_edit_id", out var nextEdit, out status) ||
            !fields.TryUInt64("boundary_sequence", out var boundary, out status) ||
            !fields.TryFault(out var fault, out status) ||
            !fields.TryArray("world", out var worldElement, out status) ||
            !ReadVoxels(worldElement, out var world, out status) ||
            !fields.TryArray("save_local_definitions", out var localsElement, out status) ||
            !ReadLocals(localsElement, out var locals, out status) ||
            !fields.TryArray("vehicles", out var vehiclesElement, out status) ||
            !ReadVehicles(vehiclesElement, out var vehicles, out status) ||
            !fields.TryObject("railway", out var railwayFields, out status) ||
            !ReadRailway(railwayFields, out var railway, out status) ||
            !fields.TryArray("assemblies", out var assembliesElement, out status) ||
            !ReadAssemblies(assembliesElement, out var assemblies, out status) ||
            !fields.TryArray("pending", out var pendingElement, out status) ||
            !ReadPending(pendingElement, out var pending, out status) ||
            !fields.TryFinish(out status))
            return false;

        model = new AuthorityStateModel(
            seed,
            generator,
            boundsX,
            boundsY,
            boundsZ,
            chunk,
            nextEntity,
            transactions,
            step,
            nextEdit,
            boundary,
            fault,
            world,
            locals,
            vehicles,
            railway,
            assemblies,
            pending);
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadVoxels(JsonElement array, out IReadOnlyList<AuthorityVoxel> voxels, out AuthorityDecodeStatus status)
    {
        voxels = Array.Empty<AuthorityVoxel>();
        var list = new List<AuthorityVoxel>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryInt32("x", out var x, out status) ||
                !fields.TryInt32("y", out var y, out status) ||
                !fields.TryInt32("z", out var z, out status) ||
                !fields.TryString("definition_id", out var definition, out status) ||
                !fields.TryString("orientation", out var orientation, out status) ||
                !fields.TryInt32("durability", out var durability, out status))
                return false;
            ulong? runtime = null;
            if (fields.Contains("runtime_id"))
            {
                if (!fields.TryUInt64("runtime_id", out var parsedRuntime, out status) || parsedRuntime == 0)
                    return Fail(out voxels, out status, status == AuthorityDecodeStatus.Decoded ? AuthorityDecodeStatus.Invalid : status);
                runtime = parsedRuntime;
            }

            if (!fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityVoxel(x, y, z, definition, orientation, durability, runtime));
        }

        voxels = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadLocals(JsonElement array, out IReadOnlyList<AuthorityLocalDefinition> locals, out AuthorityDecodeStatus status)
    {
        locals = Array.Empty<AuthorityLocalDefinition>();
        var list = new List<AuthorityLocalDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryString("revision", out var revision, out status) ||
                !fields.TryString("id", out var id, out status) ||
                !fields.TryString("archetype_id", out var archetype, out status) ||
                !fields.TryString("size_class", out var size, out status) ||
                !fields.TryArray("voxels", out var voxelsElement, out status) ||
                !ReadLocalVoxels(voxelsElement, out var voxels, out status) ||
                !fields.TryArray("contacts", out var contactsElement, out status) ||
                !ReadContacts(contactsElement, out var contacts, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityLocalDefinition(revision, id, archetype, size, voxels, contacts));
        }

        locals = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadLocalVoxels(JsonElement array, out IReadOnlyList<AuthorityLocalVoxel> voxels, out AuthorityDecodeStatus status)
    {
        voxels = Array.Empty<AuthorityLocalVoxel>();
        var list = new List<AuthorityLocalVoxel>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryInt32("x", out var x, out status) ||
                !fields.TryInt32("y", out var y, out status) ||
                !fields.TryInt32("z", out var z, out status) ||
                !fields.TryString("definition_id", out var definition, out status) ||
                !fields.TryString("orientation", out var orientation, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityLocalVoxel(x, y, z, definition, orientation));
        }

        voxels = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadContacts(JsonElement array, out IReadOnlyList<AuthorityContact> contacts, out AuthorityDecodeStatus status)
    {
        contacts = Array.Empty<AuthorityContact>();
        var list = new List<AuthorityContact>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryDouble("x", out var x, out status) ||
                !fields.TryDouble("y", out var y, out status) ||
                !fields.TryDouble("z", out var z, out status) ||
                !fields.TryInt32("bound_x", out var boundX, out status) ||
                !fields.TryInt32("bound_y", out var boundY, out status) ||
                !fields.TryInt32("bound_z", out var boundZ, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityContact(x, y, z, boundX, boundY, boundZ));
        }

        contacts = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadVehicles(JsonElement array, out IReadOnlyList<AuthorityVehicle> vehicles, out AuthorityDecodeStatus status)
    {
        vehicles = Array.Empty<AuthorityVehicle>();
        var list = new List<AuthorityVehicle>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("id", out var id, out status) ||
                id == 0 ||
                !fields.TryString("definition_id", out var definition, out status) ||
                !fields.TryString("last_used_revision", out var revision, out status) ||
                !fields.TryString("archetype_id", out var archetype, out status) ||
                !fields.TryString("last_used_archetype_revision", out var archetypeRevision, out status) ||
                !fields.TryString("size_class", out var size, out status))
                return Fail(out vehicles, out status, status == AuthorityDecodeStatus.Decoded ? AuthorityDecodeStatus.Invalid : status);
            string? saveLocal = null;
            if (fields.Contains("save_local_revision") &&
                !fields.TryString("save_local_revision", out saveLocal, out status))
                return false;
            if (!fields.TryObject("transform", out var transformFields, out status) ||
                !ReadTransform(transformFields, out var transform, out status) ||
                !fields.TryArray("parts", out var partsElement, out status) ||
                !ReadParts(partsElement, out var parts, out status) ||
                !fields.TryArray("contacts", out var contactsElement, out status) ||
                !ReadContacts(contactsElement, out var contacts, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityVehicle(
                id, definition, revision, archetype, archetypeRevision, size, saveLocal, transform, parts, contacts));
        }

        vehicles = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadTransform(Fields fields, out AuthorityTransform transform, out AuthorityDecodeStatus status)
    {
        transform = null!;
        if (!fields.TryDouble("origin_x", out var originX, out status) ||
            !fields.TryDouble("origin_y", out var originY, out status) ||
            !fields.TryDouble("origin_z", out var originZ, out status) ||
            !fields.TryDouble("right_x", out var rightX, out status) ||
            !fields.TryDouble("right_y", out var rightY, out status) ||
            !fields.TryDouble("right_z", out var rightZ, out status) ||
            !fields.TryDouble("up_x", out var upX, out status) ||
            !fields.TryDouble("up_y", out var upY, out status) ||
            !fields.TryDouble("up_z", out var upZ, out status) ||
            !fields.TryFinish(out status))
            return false;
        transform = new AuthorityTransform(originX, originY, originZ, rightX, rightY, rightZ, upX, upY, upZ);
        return true;
    }

    private static bool ReadParts(JsonElement array, out IReadOnlyList<AuthorityPart> parts, out AuthorityDecodeStatus status)
    {
        parts = Array.Empty<AuthorityPart>();
        var list = new List<AuthorityPart>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("id", out var id, out status) ||
                id == 0 ||
                !fields.TryInt32("x", out var x, out status) ||
                !fields.TryInt32("y", out var y, out status) ||
                !fields.TryInt32("z", out var z, out status) ||
                !fields.TryString("definition_id", out var definition, out status) ||
                !fields.TryString("orientation", out var orientation, out status) ||
                !fields.TryInt32("durability", out var durability, out status) ||
                !fields.TryString("last_used_revision", out var revision, out status) ||
                !fields.TryFinish(out status))
                return Fail(out parts, out status, status == AuthorityDecodeStatus.Decoded ? AuthorityDecodeStatus.Invalid : status);
            list.Add(new AuthorityPart(id, x, y, z, definition, orientation, durability, revision));
        }

        parts = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadRailway(Fields fields, out AuthorityRailway railway, out AuthorityDecodeStatus status)
    {
        railway = null!;
        if (!fields.TryInt32("sleeper_count", out var sleepers, out status) ||
            !fields.TryArray("segments", out var segmentsElement, out status) ||
            !ReadSegments(segmentsElement, out var segments, out status) ||
            !fields.TryArray("connections", out var linksElement, out status) ||
            !ReadLinks(linksElement, out var links, out status) ||
            !fields.TryArray("occupancy", out var occupancyElement, out status) ||
            !ReadOccupancy(occupancyElement, out var occupancy, out status) ||
            !fields.TryArray("destroyed", out var destroyedElement, out status) ||
            !ReadCells(destroyedElement, out var destroyed, out status) ||
            !fields.TryFinish(out status))
            return false;
        railway = new AuthorityRailway(sleepers, segments, links, occupancy, destroyed);
        return true;
    }

    private static bool ReadSegments(JsonElement array, out IReadOnlyList<AuthoritySegment> segments, out AuthorityDecodeStatus status)
    {
        segments = Array.Empty<AuthoritySegment>();
        var list = new List<AuthoritySegment>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("id", out var id, out status) ||
                id == 0 ||
                !fields.TryObject("alignment", out var alignment, out status) ||
                !ReadAlignment(alignment, out var line, out status) ||
                !fields.TryArray("rail_volumes", out var railsElement, out status) ||
                !ReadVolumes(railsElement, out var rails, out status) ||
                !fields.TryArray("sleeper_volumes", out var sleepersElement, out status) ||
                !ReadVolumes(sleepersElement, out var sleeperVolumes, out status) ||
                !fields.TryFinish(out status))
                return Fail(out segments, out status, status == AuthorityDecodeStatus.Decoded ? AuthorityDecodeStatus.Invalid : status);
            list.Add(new AuthoritySegment(id, line, rails, sleeperVolumes));
        }

        segments = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadAlignment(Fields fields, out AuthorityAlignment alignment, out AuthorityDecodeStatus status)
    {
        alignment = null!;
        if (!fields.TryDouble("start_x", out var startX, out status) ||
            !fields.TryDouble("start_y", out var startY, out status) ||
            !fields.TryDouble("start_z", out var startZ, out status) ||
            !fields.TryDouble("end_x", out var endX, out status) ||
            !fields.TryDouble("end_y", out var endY, out status) ||
            !fields.TryDouble("end_z", out var endZ, out status) ||
            !fields.TryFinish(out status))
            return false;
        alignment = new AuthorityAlignment(startX, startY, startZ, endX, endY, endZ);
        return true;
    }

    private static bool ReadVolumes(JsonElement array, out IReadOnlyList<AuthorityVolume> volumes, out AuthorityDecodeStatus status)
    {
        volumes = Array.Empty<AuthorityVolume>();
        var list = new List<AuthorityVolume>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryDouble("min_x", out var minX, out status) ||
                !fields.TryDouble("min_y", out var minY, out status) ||
                !fields.TryDouble("min_z", out var minZ, out status) ||
                !fields.TryDouble("max_x", out var maxX, out status) ||
                !fields.TryDouble("max_y", out var maxY, out status) ||
                !fields.TryDouble("max_z", out var maxZ, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityVolume(minX, minY, minZ, maxX, maxY, maxZ));
        }

        volumes = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadLinks(JsonElement array, out IReadOnlyList<AuthorityLink> links, out AuthorityDecodeStatus status)
    {
        links = Array.Empty<AuthorityLink>();
        var list = new List<AuthorityLink>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("from_segment_id", out var from, out status) ||
                !fields.TryString("from_end", out var fromEnd, out status) ||
                !KnownEnd(fromEnd) ||
                !fields.TryUInt64("to_segment_id", out var to, out status) ||
                !fields.TryString("to_end", out var toEnd, out status) ||
                !KnownEnd(toEnd) ||
                !fields.TryFinish(out status))
                return Fail(out links, out status, status == AuthorityDecodeStatus.Decoded ? AuthorityDecodeStatus.UnknownFormat : status);
            list.Add(new AuthorityLink(from, fromEnd, to, toEnd));
        }

        links = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadOccupancy(JsonElement array, out IReadOnlyList<AuthorityOccupancy> occupancy, out AuthorityDecodeStatus status)
    {
        occupancy = Array.Empty<AuthorityOccupancy>();
        var list = new List<AuthorityOccupancy>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryInt32("x", out var x, out status) ||
                !fields.TryInt32("y", out var y, out status) ||
                !fields.TryInt32("z", out var z, out status) ||
                !fields.TryArray("supports", out var supportsElement, out status) ||
                !ReadSupports(supportsElement, out var supports, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityOccupancy(x, y, z, supports));
        }

        occupancy = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadSupports(JsonElement array, out IReadOnlyList<AuthoritySupport> supports, out AuthorityDecodeStatus status)
    {
        supports = Array.Empty<AuthoritySupport>();
        var list = new List<AuthoritySupport>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("segment_id", out var segment, out status) ||
                !fields.TryDouble("world_x_start", out var worldStart, out status) ||
                !fields.TryDouble("world_x_end", out var worldEnd, out status) ||
                !fields.TryDouble("along_start", out var alongStart, out status) ||
                !fields.TryDouble("along_end", out var alongEnd, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthoritySupport(segment, worldStart, worldEnd, alongStart, alongEnd));
        }

        supports = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadCells(JsonElement array, out IReadOnlyList<AuthorityCell> cells, out AuthorityDecodeStatus status)
    {
        cells = Array.Empty<AuthorityCell>();
        var list = new List<AuthorityCell>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryInt32("x", out var x, out status) ||
                !fields.TryInt32("y", out var y, out status) ||
                !fields.TryInt32("z", out var z, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityCell(x, y, z));
        }

        cells = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadAssemblies(JsonElement array, out IReadOnlyList<AuthorityAssembly> assemblies, out AuthorityDecodeStatus status)
    {
        assemblies = Array.Empty<AuthorityAssembly>();
        var list = new List<AuthorityAssembly>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("id", out var id, out status))
                return false;
            if (id == 0)
                return Fail(out assemblies, out status, AuthorityDecodeStatus.Invalid);
            if (!fields.TryNullableUInt64("active_locomotive_id", out var locomotive, out status) ||
                !fields.TryArray("vehicle_ids", out var idsElement, out status) ||
                !ReadIds(idsElement, out var ids, out status) ||
                !fields.TryArray("connections", out var couplersElement, out status) ||
                !ReadCouplers(couplersElement, out var couplers, out status) ||
                !fields.TryUInt64("reference_segment_id", out var segment, out status) ||
                !fields.TryDouble("distance", out var distance, out status) ||
                !fields.TryString("heading", out var heading, out status))
                return false;
            if (heading is not ("along" or "against"))
                return Fail(out assemblies, out status, AuthorityDecodeStatus.UnknownFormat);
            if (!fields.TryDouble("speed", out var speed, out status) ||
                !fields.TryBool("running", out var running, out status) ||
                !fields.TryString("halt_reason", out var halt, out status))
                return false;
            if (halt is not ("none" or "support_lost" or "forward_gap" or "end_of_line"))
                return Fail(out assemblies, out status, AuthorityDecodeStatus.UnknownFormat);
            if (!fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityAssembly(id, locomotive, ids, couplers, segment, distance, heading, speed, running, halt));
        }

        assemblies = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadIds(JsonElement array, out IReadOnlyList<ulong> ids, out AuthorityDecodeStatus status)
    {
        ids = Array.Empty<ulong>();
        var list = new List<ulong>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetUInt64(out var id) || id == 0)
                return Fail(out ids, out status, AuthorityDecodeStatus.Invalid);
            list.Add(id);
        }

        ids = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadCouplers(JsonElement array, out IReadOnlyList<AuthorityCoupler> couplers, out AuthorityDecodeStatus status)
    {
        couplers = Array.Empty<AuthorityCoupler>();
        var list = new List<AuthorityCoupler>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("from_vehicle_id", out var fromVehicle, out status) ||
                !fields.TryUInt64("from_part_id", out var fromPart, out status) ||
                !fields.TryInt32("from_x", out var fromX, out status) ||
                !fields.TryInt32("from_y", out var fromY, out status) ||
                !fields.TryInt32("from_z", out var fromZ, out status) ||
                !fields.TryUInt64("to_vehicle_id", out var toVehicle, out status) ||
                !fields.TryUInt64("to_part_id", out var toPart, out status) ||
                !fields.TryInt32("to_x", out var toX, out status) ||
                !fields.TryInt32("to_y", out var toY, out status) ||
                !fields.TryInt32("to_z", out var toZ, out status) ||
                !fields.TryFinish(out status))
                return false;
            list.Add(new AuthorityCoupler(fromVehicle, fromPart, fromX, fromY, fromZ, toVehicle, toPart, toX, toY, toZ));
        }

        couplers = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool ReadPending(JsonElement array, out IReadOnlyList<AuthorityPending> pending, out AuthorityDecodeStatus status)
    {
        pending = Array.Empty<AuthorityPending>();
        var list = new List<AuthorityPending>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var fields, out status) ||
                !fields.TryUInt64("edit_id", out var id, out status))
                return false;
            if (id == 0)
                return Fail(out pending, out status, AuthorityDecodeStatus.Invalid);
            if (!fields.TryUInt64("accepted_step", out var acceptedStep, out status) ||
                !fields.TryUInt64("accepted_sequence", out var acceptedSequence, out status) ||
                !fields.TryUInt64("target_step", out var target, out status) ||
                !fields.TryTake("edit", JsonValueKind.Object, out var editElement, out status) ||
                !fields.TryFinish(out status))
                return false;
            if (!WorldEditCodec.TryDecode(editElement.GetRawText(), out var edit, out var editStatus))
                return Fail(
                    out pending,
                    out status,
                    editStatus == WorldEditDecodeStatus.UnknownFormat
                        ? AuthorityDecodeStatus.UnknownFormat
                        : AuthorityDecodeStatus.Invalid);

            list.Add(new AuthorityPending(id, acceptedStep, acceptedSequence, target, edit.Canonical));
        }

        pending = list;
        status = AuthorityDecodeStatus.Decoded;
        return true;
    }

    private static bool KnownEnd(string end) => end is "start" or "end";

    private static bool Fail<T>(out T value, out AuthorityDecodeStatus status, AuthorityDecodeStatus code)
    {
        value = default!;
        status = code;
        return false;
    }

    private sealed class Fields
    {
        private readonly Dictionary<string, JsonElement> _remaining;

        private Fields(Dictionary<string, JsonElement> remaining) => _remaining = remaining;

        public static bool TryOpen(JsonElement element, out Fields fields, out AuthorityDecodeStatus status)
        {
            fields = null!;
            if (element.ValueKind != JsonValueKind.Object)
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            var remaining = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!remaining.TryAdd(property.Name, property.Value))
                {
                    status = AuthorityDecodeStatus.UnknownFormat;
                    return false;
                }
            }

            fields = new Fields(remaining);
            status = AuthorityDecodeStatus.Decoded;
            return true;
        }

        public bool Contains(string name) => _remaining.ContainsKey(name);

        public bool TryFinish(out AuthorityDecodeStatus status)
        {
            status = _remaining.Count == 0 ? AuthorityDecodeStatus.Decoded : AuthorityDecodeStatus.UnknownFormat;
            return _remaining.Count == 0;
        }

        public bool TryTake(string name, JsonValueKind kind, out JsonElement value, out AuthorityDecodeStatus status)
        {
            if (!_remaining.Remove(name, out value) || value.ValueKind != kind)
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            status = AuthorityDecodeStatus.Decoded;
            return true;
        }

        public bool TryObject(string name, out Fields fields, out AuthorityDecodeStatus status)
        {
            fields = null!;
            if (!TryTake(name, JsonValueKind.Object, out var element, out status))
                return false;
            return TryOpen(element, out fields, out status);
        }

        public bool TryArray(string name, out JsonElement value, out AuthorityDecodeStatus status) =>
            TryTake(name, JsonValueKind.Array, out value, out status);

        public bool TryString(string name, out string value, out AuthorityDecodeStatus status)
        {
            value = "";
            if (!TryTake(name, JsonValueKind.String, out var element, out status))
                return false;
            value = element.GetString() ?? "";
            if (value.Length == 0)
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            return true;
        }

        public bool TryInt32(string name, out int value, out AuthorityDecodeStatus status)
        {
            value = 0;
            if (!TryTake(name, JsonValueKind.Number, out var element, out status))
                return false;
            if (element.TryGetInt32(out value))
                return true;
            status = AuthorityDecodeStatus.Invalid;
            return false;
        }

        public bool TryUInt64(string name, out ulong value, out AuthorityDecodeStatus status)
        {
            value = 0;
            if (!TryTake(name, JsonValueKind.Number, out var element, out status))
                return false;
            if (element.TryGetUInt64(out value))
                return true;
            status = AuthorityDecodeStatus.Invalid;
            return false;
        }

        public bool TryNullableUInt64(string name, out ulong? value, out AuthorityDecodeStatus status)
        {
            value = null;
            if (!_remaining.Remove(name, out var element))
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                status = AuthorityDecodeStatus.Decoded;
                return true;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var parsed) && parsed != 0)
            {
                value = parsed;
                status = AuthorityDecodeStatus.Decoded;
                return true;
            }

            status = AuthorityDecodeStatus.Invalid;
            return false;
        }

        public bool TryBool(string name, out bool value, out AuthorityDecodeStatus status)
        {
            value = false;
            if (!_remaining.Remove(name, out var element))
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = element.GetBoolean();
                status = AuthorityDecodeStatus.Decoded;
                return true;
            }

            status = AuthorityDecodeStatus.Invalid;
            return false;
        }

        public bool TryDouble(string name, out double value, out AuthorityDecodeStatus status)
        {
            value = 0;
            if (!TryTake(name, JsonValueKind.Number, out var element, out status))
                return false;
            if (!element.TryGetDouble(out value) || !double.IsFinite(value))
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            return true;
        }

        public bool TryFault(out AuthorityFault? fault, out AuthorityDecodeStatus status)
        {
            fault = null;
            if (!_remaining.Remove("simulation_fault", out var element))
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                status = AuthorityDecodeStatus.Decoded;
                return true;
            }

            if (!TryOpen(element, out var fields, out status) ||
                !fields.TryNullableUInt64("edit_id", out var editId, out status))
                return false;
            if (!TryNullableKind(fields, out var kind, out status))
                return false;
            if (kind is not null && !WorldEditCodec.TryParseWireKind(kind, out _))
                return Fail(out fault, out status, AuthorityDecodeStatus.UnknownFormat);
            if (!fields.TryUInt64("step", out var step, out status) ||
                !fields.TryUInt64("sequence", out var sequence, out status) ||
                !fields.TryString("exception_type", out var type, out status) ||
                !fields.TryStringAllowEmpty("message", out var message, out status) ||
                !fields.TryFinish(out status))
                return false;
            fault = new AuthorityFault(editId, kind, step, sequence, type, message);
            return true;
        }

        public bool TryStringAllowEmpty(string name, out string value, out AuthorityDecodeStatus status)
        {
            value = "";
            if (!TryTake(name, JsonValueKind.String, out var element, out status))
                return false;
            value = element.GetString() ?? "";
            return true;
        }

        private static bool TryNullableKind(Fields fields, out string? kind, out AuthorityDecodeStatus status)
        {
            kind = null;
            if (!fields._remaining.Remove("kind", out var element))
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                status = AuthorityDecodeStatus.Decoded;
                return true;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            kind = element.GetString();
            if (string.IsNullOrEmpty(kind))
            {
                status = AuthorityDecodeStatus.Invalid;
                return false;
            }

            status = AuthorityDecodeStatus.Decoded;
            return true;
        }
    }
}

public sealed record AuthorityDocument(string Canonical, string Digest);

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Commands;

public enum WorldEditDecodeStatus
{
    Decoded = 0,
    UnknownFormat = 1,
    Invalid = 2
}

/// <summary>
/// Canonical encoding of parsed world edits (input format version 1). See docs/design/04 for the contract:
/// compact UTF-8 JSON, fixed field order, ordinal ASCII identifiers, sorted unordered target sets, preserved
/// ordered operation lists, i32 coordinates and amounts, non-zero u64 runtime ids, and finite doubles written
/// as the shortest culture-invariant round-trip text.
/// </summary>
public static class WorldEditCodec
{
    public const int InputFormatVersion = 1;
    public const string EmptyLogDigest = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    public static string WireKind(WorldEditKind kind) => kind switch
    {
        WorldEditKind.VoxelAdd => "voxel.add",
        WorldEditKind.VoxelRemove => "voxel.remove",
        WorldEditKind.VoxelDamage => "voxel.damage",
        WorldEditKind.VehicleManufacture => "vehicle.manufacture",
        WorldEditKind.VehicleDamage => "vehicle.damage",
        WorldEditKind.VehicleRefit => "vehicle.refit",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParseWireKind(string? text, out WorldEditKind kind)
    {
        foreach (var candidate in Enum.GetValues<WorldEditKind>())
        {
            if (string.Equals(WireKind(candidate), text, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static string WireState(WorldEditState state) => state switch
    {
        WorldEditState.Pending => "pending",
        WorldEditState.Executed => "executed",
        WorldEditState.Rejected => "rejected",
        WorldEditState.Faulted => "faulted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static string Encode(WorldEdit edit) => Encoding.UTF8.GetString(EncodeUtf8(edit));

    public static byte[] EncodeUtf8(WorldEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Write(writer => WriteEdit(writer, edit));
    }

    public static string EncodeRecord(WorldEditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Encoding.UTF8.GetString(Write(writer => WriteRecord(writer, record)));
    }

    public static string Digest(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Chains every executed, rejected, or faulted record so equal histories share one digest.</summary>
    public static string ChainLogDigest(string previous, WorldEditRecord record) =>
        Digest(previous + "\n" + EncodeRecord(record));

    public static bool TryDecode(string? json, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        status = WorldEditDecodeStatus.Invalid;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            return TryReadEdit(document.RootElement, out edit, out status);
        }
        catch (JsonException)
        {
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }
    }

    internal static void WriteQueue(Utf8JsonWriter writer, WorldEditQueueState state)
    {
        writer.WriteStartObject("world_edits");
        writer.WriteNumber("next_edit_id", state.NextEditId);
        writer.WriteNumber("boundary_sequence", state.BoundarySequence);
        writer.WriteStartArray("pending");
        foreach (var record in state.Pending.OrderBy(item => item.Id))
        {
            writer.WriteStartObject();
            WritePosition(writer, record);
            writer.WritePropertyName("edit");
            WriteEdit(writer, record.Edit);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static bool TryReadQueue(JsonElement element, out WorldEditQueueState state, out WorldEditDecodeStatus status)
    {
        state = WorldEditQueueState.Initial;
        if (!Fields.TryOpen(element, out var fields, out status))
            return false;
        if (!fields.TryUInt64("next_edit_id", out var nextId, out status) ||
            !fields.TryUInt64("boundary_sequence", out var boundary, out status) ||
            !fields.TryTake("pending", JsonValueKind.Array, out var pendingElement, out status) ||
            !fields.TryFinish(out status))
            return false;

        var pending = new List<WorldEditRecord>();
        foreach (var item in pendingElement.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var entry, out status) ||
                !entry.TryUInt64("edit_id", out var id, out status) ||
                !entry.TryUInt64("accepted_step", out var acceptedStep, out status) ||
                !entry.TryUInt64("accepted_sequence", out var acceptedSequence, out status) ||
                !entry.TryUInt64("target_step", out var targetStep, out status) ||
                !entry.TryTake("edit", JsonValueKind.Object, out var editElement, out status) ||
                !entry.TryFinish(out status) ||
                !TryReadEdit(editElement, out var edit, out status))
                return false;
            pending.Add(new WorldEditRecord(id, edit, new SimulationPosition(acceptedStep, acceptedSequence), targetStep));
        }

        state = new WorldEditQueueState(nextId, boundary, pending);
        status = WorldEditDecodeStatus.Decoded;
        return true;
    }

    internal static bool TryReadEdit(JsonElement element, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!Fields.TryOpen(element, out var fields, out status))
            return false;
        if (!fields.Contains("input_format_version") || !fields.Contains("kind"))
            return Fail(out edit, out status, WorldEditDecodeStatus.UnknownFormat);
        if (!fields.TryInt32("input_format_version", out var version, out status))
            return false;
        if (version != InputFormatVersion)
            return Fail(out edit, out status, WorldEditDecodeStatus.UnknownFormat);
        if (!fields.TryTake("kind", JsonValueKind.String, out var kindElement, out status))
            return false;
        if (!TryParseWireKind(kindElement.GetString(), out var kind))
            return Fail(out edit, out status, WorldEditDecodeStatus.UnknownFormat);

        var read = kind switch
        {
            WorldEditKind.VoxelAdd => TryReadVoxelAdd(fields, out edit, out status),
            WorldEditKind.VoxelRemove => TryReadVoxelRemove(fields, out edit, out status),
            WorldEditKind.VoxelDamage => TryReadVoxelDamage(fields, out edit, out status),
            WorldEditKind.VehicleManufacture => TryReadManufacture(fields, out edit, out status),
            WorldEditKind.VehicleDamage => TryReadVehicleDamage(fields, out edit, out status),
            WorldEditKind.VehicleRefit => TryReadRefit(fields, out edit, out status),
            _ => Fail(out edit, out status, WorldEditDecodeStatus.UnknownFormat)
        };
        return read && fields.TryFinish(out status);
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static void WriteRecord(Utf8JsonWriter writer, WorldEditRecord record)
    {
        writer.WriteStartObject();
        WritePosition(writer, record);
        writer.WriteString("state", WireState(record.State));
        if (record.ExecutedAt is { } executed)
        {
            writer.WriteNumber("executed_step", executed.Step);
            writer.WriteNumber("executed_sequence", executed.Sequence);
        }

        if (record.Status is { } status)
            writer.WriteString("status", status);
        writer.WritePropertyName("edit");
        WriteEdit(writer, record.Edit);
        writer.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter writer, WorldEditRecord record)
    {
        writer.WriteNumber("edit_id", record.Id);
        writer.WriteNumber("accepted_step", record.AcceptedAt.Step);
        writer.WriteNumber("accepted_sequence", record.AcceptedAt.Sequence);
        writer.WriteNumber("target_step", record.TargetStep);
    }

    private static void WriteEdit(Utf8JsonWriter writer, WorldEdit edit)
    {
        writer.WriteStartObject();
        writer.WriteNumber("input_format_version", InputFormatVersion);
        writer.WriteString("kind", WireKind(edit.Kind));
        switch (edit)
        {
            case VoxelAddEdit add:
                WriteCoord(writer, "position", add.Position.X, add.Position.Y, add.Position.Z);
                writer.WriteString("definition_id", add.DefinitionId.Value);
                writer.WriteString("orientation", add.Orientation.ToString());
                break;
            case VoxelRemoveEdit remove:
                WriteCoord(writer, "position", remove.Position.X, remove.Position.Y, remove.Position.Z);
                break;
            case VoxelDamageEdit damage:
                writer.WriteStartArray("targets");
                foreach (var target in damage.Targets)
                {
                    writer.WriteStartObject();
                    WriteCoord(writer, "position", target.Position.X, target.Position.Y, target.Position.Z);
                    writer.WriteNumber("amount", target.Amount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case VehicleManufactureEdit manufacture:
                writer.WriteString("definition_id", manufacture.DefinitionId.Value);
                WriteTransform(writer, manufacture.Transform);
                break;
            case VehicleDamageEdit damage:
                writer.WriteStartArray("targets");
                foreach (var target in damage.Targets)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("vehicle_id", target.VehicleId.Value);
                    WriteCoord(writer, "cell", target.Cell.X, target.Cell.Y, target.Cell.Z);
                    writer.WriteNumber("amount", target.Amount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case VehicleRefitEdit refit:
                writer.WriteNumber("vehicle_id", refit.VehicleId.Value);
                writer.WriteStartArray("operations");
                foreach (var operation in refit.Operations)
                    WriteOperation(writer, operation);
                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edit), edit.Kind, "Unknown world edit.");
        }

        writer.WriteEndObject();
    }

    private static void WriteOperation(Utf8JsonWriter writer, CompositeRefitOperation operation)
    {
        writer.WriteStartObject();
        writer.WriteString("op", OperationWire(operation.Kind));
        switch (operation.Kind)
        {
            case CompositeRefitKind.Add:
            case CompositeRefitKind.Replace:
                var cell = operation.Cell!.Value;
                WriteCoord(writer, "cell", cell.X, cell.Y, cell.Z);
                writer.WriteString("definition_id", operation.DefinitionId!.Value.Value);
                writer.WriteString("orientation", operation.Orientation!.Value.ToString());
                break;
            case CompositeRefitKind.Remove:
                writer.WriteNumber("part_id", operation.PartId!.Value.Value);
                break;
            case CompositeRefitKind.Move:
                writer.WriteNumber("part_id", operation.PartId!.Value.Value);
                var destination = operation.Cell!.Value;
                WriteCoord(writer, "cell", destination.X, destination.Y, destination.Z);
                break;
            case CompositeRefitKind.Rotate:
                writer.WriteNumber("part_id", operation.PartId!.Value.Value);
                writer.WriteString("orientation", operation.Orientation!.Value.ToString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Unknown refit kind.");
        }

        writer.WriteEndObject();
    }

    private static string OperationWire(CompositeRefitKind kind) => kind switch
    {
        CompositeRefitKind.Add => "add",
        CompositeRefitKind.Remove => "remove",
        CompositeRefitKind.Move => "move",
        CompositeRefitKind.Rotate => "rotate",
        CompositeRefitKind.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static void WriteCoord(Utf8JsonWriter writer, string name, int x, int y, int z)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("x", x);
        writer.WriteNumber("y", y);
        writer.WriteNumber("z", z);
        writer.WriteEndObject();
    }

    private static void WriteTransform(Utf8JsonWriter writer, CompositeTransform transform)
    {
        writer.WriteStartObject("transform");
        WriteDouble(writer, "origin_x", transform.OriginX);
        WriteDouble(writer, "origin_y", transform.OriginY);
        WriteDouble(writer, "origin_z", transform.OriginZ);
        WriteDouble(writer, "right_x", transform.RightX);
        WriteDouble(writer, "right_y", transform.RightY);
        WriteDouble(writer, "right_z", transform.RightZ);
        WriteDouble(writer, "up_x", transform.UpX);
        WriteDouble(writer, "up_y", transform.UpY);
        WriteDouble(writer, "up_z", transform.UpZ);
        writer.WriteEndObject();
    }

    internal static string FormatDouble(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Canonical doubles must be finite.");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(FormatDouble(value));
    }

    private static bool TryReadVoxelAdd(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryCoord("position", out var x, out var y, out var z, out status) ||
            !fields.TryVoxelDefinition("definition_id", out var definition, out status) ||
            !fields.TryOrientation("orientation", out var orientation, out status))
            return false;
        edit = new VoxelAddEdit(new WorldVoxelCoord(x, y, z), definition, orientation);
        return true;
    }

    private static bool TryReadVoxelRemove(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryCoord("position", out var x, out var y, out var z, out status))
            return false;
        edit = new VoxelRemoveEdit(new WorldVoxelCoord(x, y, z));
        return true;
    }

    private static bool TryReadVoxelDamage(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("targets", JsonValueKind.Array, out var array, out status))
            return false;
        var targets = new List<VoxelDamageTarget>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var target, out status) ||
                !target.TryCoord("position", out var x, out var y, out var z, out status) ||
                !target.TryInt32("amount", out var amount, out status) ||
                !target.TryFinish(out status))
                return false;
            targets.Add(new VoxelDamageTarget(new WorldVoxelCoord(x, y, z), amount));
        }

        edit = new VoxelDamageEdit(targets);
        return true;
    }

    private static bool TryReadManufacture(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("definition_id", JsonValueKind.String, out var idElement, out status))
            return false;
        var text = idElement.GetString()!;
        CompositeDefinitionId definition;
        try
        {
            definition = CompositeDefinitionId.Parse(text);
        }
        catch (ArgumentException)
        {
            return Fail(out edit, out status, WorldEditDecodeStatus.Invalid);
        }

        if (!string.Equals(definition.Value, text, StringComparison.Ordinal))
            return Fail(out edit, out status, WorldEditDecodeStatus.Invalid);
        if (!fields.TryTake("transform", JsonValueKind.Object, out var transformElement, out status) ||
            !Fields.TryOpen(transformElement, out var transform, out status))
            return false;
        var values = new double[9];
        var names = new[] { "origin_x", "origin_y", "origin_z", "right_x", "right_y", "right_z", "up_x", "up_y", "up_z" };
        for (var index = 0; index < names.Length; index++)
        {
            if (!transform.TryDouble(names[index], out values[index], out status))
                return false;
        }

        if (!transform.TryFinish(out status))
            return false;
        if (!CompositeTransform.TryRestore(
                values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8],
                out var pose))
            return Fail(out edit, out status, WorldEditDecodeStatus.Invalid);
        edit = new VehicleManufactureEdit(definition, pose);
        return true;
    }

    private static bool TryReadVehicleDamage(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("targets", JsonValueKind.Array, out var array, out status))
            return false;
        var targets = new List<CompositeDamageTarget>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var target, out status) ||
                !target.TryEntityId("vehicle_id", out var vehicle, out status) ||
                !target.TryCoord("cell", out var x, out var y, out var z, out status) ||
                !target.TryInt32("amount", out var amount, out status) ||
                !target.TryFinish(out status))
                return false;
            targets.Add(new CompositeDamageTarget(vehicle, new LocalVoxelCoord(x, y, z), amount));
        }

        edit = new VehicleDamageEdit(targets);
        return true;
    }

    private static bool TryReadRefit(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryEntityId("vehicle_id", out var vehicle, out status) ||
            !fields.TryTake("operations", JsonValueKind.Array, out var array, out status))
            return false;
        var operations = new List<CompositeRefitOperation>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var entry, out status) ||
                !entry.TryTake("op", JsonValueKind.String, out var opElement, out status))
                return false;
            CompositeRefitOperation operation;
            switch (opElement.GetString())
            {
                case "add":
                case "replace":
                    if (!entry.TryCoord("cell", out var x, out var y, out var z, out status) ||
                        !entry.TryVoxelDefinition("definition_id", out var definition, out status) ||
                        !entry.TryOrientation("orientation", out var orientation, out status))
                        return false;
                    operation = opElement.GetString() == "add"
                        ? CompositeRefitOperation.Add(new LocalVoxelCoord(x, y, z), definition, orientation)
                        : CompositeRefitOperation.Replace(new LocalVoxelCoord(x, y, z), definition, orientation);
                    break;
                case "remove":
                    if (!entry.TryEntityId("part_id", out var removed, out status))
                        return false;
                    operation = CompositeRefitOperation.Remove(removed);
                    break;
                case "move":
                    if (!entry.TryEntityId("part_id", out var moved, out status) ||
                        !entry.TryCoord("cell", out var mx, out var my, out var mz, out status))
                        return false;
                    operation = CompositeRefitOperation.Move(moved, new LocalVoxelCoord(mx, my, mz));
                    break;
                case "rotate":
                    if (!entry.TryEntityId("part_id", out var rotated, out status) ||
                        !entry.TryOrientation("orientation", out var rotation, out status))
                        return false;
                    operation = CompositeRefitOperation.Rotate(rotated, rotation);
                    break;
                default:
                    return Fail(out edit, out status, WorldEditDecodeStatus.UnknownFormat);
            }

            if (!entry.TryFinish(out status))
                return false;
            operations.Add(operation);
        }

        edit = new VehicleRefitEdit(vehicle, operations);
        return true;
    }

    private static bool Fail(out WorldEdit edit, out WorldEditDecodeStatus status, WorldEditDecodeStatus value)
    {
        edit = null!;
        status = value;
        return false;
    }

    /// <summary>Strict object reader: duplicate or unknown fields are an unknown format; malformed values are invalid.</summary>
    private sealed class Fields
    {
        private readonly Dictionary<string, JsonElement> _remaining;

        private Fields(Dictionary<string, JsonElement> remaining)
        {
            _remaining = remaining;
        }

        public static bool TryOpen(JsonElement element, out Fields fields, out WorldEditDecodeStatus status)
        {
            fields = null!;
            if (element.ValueKind != JsonValueKind.Object)
            {
                status = WorldEditDecodeStatus.Invalid;
                return false;
            }

            var remaining = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!remaining.TryAdd(property.Name, property.Value))
                {
                    status = WorldEditDecodeStatus.UnknownFormat;
                    return false;
                }
            }

            fields = new Fields(remaining);
            status = WorldEditDecodeStatus.Decoded;
            return true;
        }

        public bool Contains(string name) => _remaining.ContainsKey(name);

        public bool TryTake(string name, JsonValueKind kind, out JsonElement value, out WorldEditDecodeStatus status)
        {
            if (!_remaining.Remove(name, out value) || value.ValueKind != kind)
            {
                status = WorldEditDecodeStatus.Invalid;
                return false;
            }

            status = WorldEditDecodeStatus.Decoded;
            return true;
        }

        public bool TryFinish(out WorldEditDecodeStatus status)
        {
            status = _remaining.Count == 0 ? WorldEditDecodeStatus.Decoded : WorldEditDecodeStatus.UnknownFormat;
            return _remaining.Count == 0;
        }

        public bool TryInt32(string name, out int value, out WorldEditDecodeStatus status)
        {
            value = 0;
            if (!TryTake(name, JsonValueKind.Number, out var element, out status))
                return false;
            if (element.TryGetInt32(out value))
                return true;
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }

        public bool TryUInt64(string name, out ulong value, out WorldEditDecodeStatus status)
        {
            value = 0;
            if (!TryTake(name, JsonValueKind.Number, out var element, out status))
                return false;
            if (element.TryGetUInt64(out value))
                return true;
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }

        public bool TryEntityId(string name, out EntityId id, out WorldEditDecodeStatus status)
        {
            id = default;
            if (!TryUInt64(name, out var raw, out status))
                return false;
            if (raw == 0)
            {
                status = WorldEditDecodeStatus.Invalid;
                return false;
            }

            id = new EntityId(raw);
            return true;
        }

        public bool TryDouble(string name, out double value, out WorldEditDecodeStatus status)
        {
            value = 0;
            if (!TryTake(name, JsonValueKind.Number, out var element, out status))
                return false;
            if (element.TryGetDouble(out value) && double.IsFinite(value))
                return true;
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }

        public bool TryCoord(string name, out int x, out int y, out int z, out WorldEditDecodeStatus status)
        {
            x = y = z = 0;
            return TryTake(name, JsonValueKind.Object, out var element, out status) &&
                   TryOpen(element, out var coord, out status) &&
                   coord.TryInt32("x", out x, out status) &&
                   coord.TryInt32("y", out y, out status) &&
                   coord.TryInt32("z", out z, out status) &&
                   coord.TryFinish(out status);
        }

        public bool TryVoxelDefinition(string name, out VoxelDefinitionId id, out WorldEditDecodeStatus status)
        {
            id = default;
            if (!TryTake(name, JsonValueKind.String, out var element, out status))
                return false;
            var text = element.GetString()!;
            try
            {
                id = VoxelDefinitionId.Parse(text);
            }
            catch (ArgumentException)
            {
                status = WorldEditDecodeStatus.Invalid;
                return false;
            }

            if (string.Equals(id.Value, text, StringComparison.Ordinal))
                return true;
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }

        public bool TryOrientation(string name, out VoxelOrientation orientation, out WorldEditDecodeStatus status)
        {
            orientation = default;
            if (!TryTake(name, JsonValueKind.String, out var element, out status))
                return false;
            var text = element.GetString();
            if (VoxelOrientation.TryParse(text, out orientation) &&
                string.Equals(orientation.ToString(), text, StringComparison.Ordinal))
                return true;
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }
    }
}

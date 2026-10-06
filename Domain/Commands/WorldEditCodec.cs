using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.Commands;

public enum WorldEditDecodeStatus
{
    Decoded = 0,
    UnknownFormat = 1,
    Invalid = 2
}

/// <summary>
/// Canonical encoding of parsed world edits. See docs/design/04 for the contract:
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
        WorldEditKind.RailConstruct => "rail.construct",
        WorldEditKind.RailRepair => "rail.repair",
        WorldEditKind.TrainAssemble => "train.assemble",
        WorldEditKind.TrainStart => "train.start",
        WorldEditKind.TrainStop => "train.stop",
        WorldEditKind.ContentPublish => "content.publish",
        WorldEditKind.VoxelCellSet => "voxel.cell-set",
        WorldEditKind.VoxelCellRestore => "voxel.cell-restore",
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
            WorldEditKind.RailConstruct => TryReadRailConstruct(fields, out edit, out status),
            WorldEditKind.RailRepair => TryReadRailRepair(fields, out edit, out status),
            WorldEditKind.TrainAssemble => TryReadTrainAssemble(fields, out edit, out status),
            WorldEditKind.TrainStart => TryReadTrainCommand(fields, start: true, out edit, out status),
            WorldEditKind.TrainStop => TryReadTrainCommand(fields, start: false, out edit, out status),
            WorldEditKind.ContentPublish => TryReadContentPublish(fields, out edit, out status),
            WorldEditKind.VoxelCellSet => TryReadVoxelCellSet(fields, out edit, out status),
            WorldEditKind.VoxelCellRestore => TryReadVoxelCellRestore(fields, out edit, out status),
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
            case RailConstructEdit construct:
                WriteRailPlan(writer, construct.Plan);
                break;
            case RailRepairEdit repair:
                writer.WriteStartArray("cells");
                foreach (var cell in repair.Cells)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("x", cell.X);
                    writer.WriteNumber("y", cell.Y);
                    writer.WriteNumber("z", cell.Z);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case TrainAssembleEdit assemble:
                WriteAssemblyMember(writer, "locomotive", assemble.LocomotiveId, assemble.LocomotiveTransform, assemble.LocomotiveCouplerCell);
                WriteAssemblyMember(writer, "carriage", assemble.CarriageId, assemble.CarriageTransform, assemble.CarriageCouplerCell);
                break;
            case TrainStartEdit start:
                writer.WriteNumber("assembly_id", start.AssemblyId.Value);
                break;
            case TrainStopEdit stop:
                writer.WriteNumber("assembly_id", stop.AssemblyId.Value);
                break;
            case ContentPublishEdit publish:
                writer.WriteString("content_digest", publish.ContentDigest);
                break;
            case VoxelCellSetEdit cells:
                writer.WriteStartArray("targets");
                foreach (var target in cells.Targets)
                {
                    writer.WriteStartObject();
                    WriteCoord(writer, "position", target.Position.X, target.Position.Y, target.Position.Z);
                    writer.WriteString("operation", CellOperationWire(target.Operation));
                    if (target.Content is { } content)
                    {
                        writer.WriteStartObject("content");
                        writer.WriteString("definition_id", content.DefinitionId.Value);
                        writer.WriteString("orientation", content.Orientation.ToString());
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteNull("content");
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case VoxelCellRestoreEdit restore:
                writer.WriteStartArray("targets");
                foreach (var target in restore.Targets)
                {
                    writer.WriteStartObject();
                    WriteCoord(writer, "position", target.Position.X, target.Position.Y, target.Position.Z);
                    WriteCellState(writer, "expected", target.Expected);
                    WriteCellState(writer, "content", target.Content);
                    writer.WriteEndObject();
                }
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

    private static string CellOperationWire(VoxelCellOperation operation) => operation switch
    {
        VoxelCellOperation.Add => "add",
        VoxelCellOperation.Remove => "remove",
        VoxelCellOperation.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static void WriteCellState(Utf8JsonWriter writer, string name, VoxelCellState? state)
    {
        if (state is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("definition_id", state.DefinitionId.Value);
        writer.WriteString("orientation", state.Orientation.ToString());
        writer.WriteNumber("current_durability", state.CurrentDurability);
        writer.WriteEndObject();
    }

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

    private static void WriteRailPlan(Utf8JsonWriter writer, RailConstructionPlan plan)
    {
        writer.WriteStartArray("segments");
        foreach (var segment in plan.Segments)
        {
            writer.WriteStartObject();
            var alignment = segment.Alignment;
            writer.WriteStartObject("alignment");
            WriteDouble(writer, "start_x", alignment.StartX);
            WriteDouble(writer, "start_y", alignment.StartY);
            WriteDouble(writer, "start_z", alignment.StartZ);
            WriteDouble(writer, "end_x", alignment.EndX);
            WriteDouble(writer, "end_y", alignment.EndY);
            WriteDouble(writer, "end_z", alignment.EndZ);
            writer.WriteEndObject();
            WriteBoxes(writer, "rails", segment.RailVolumes);
            WriteBoxes(writer, "sleepers", segment.SleeperVolumes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("connections");
        foreach (var connection in plan.Connections)
        {
            writer.WriteStartObject();
            writer.WriteNumber("from_segment", connection.FromSegment);
            writer.WriteString("from_end", RailEndWire(connection.FromEnd));
            writer.WriteNumber("to_segment", connection.ToSegment);
            writer.WriteString("to_end", RailEndWire(connection.ToEnd));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteBoxes(Utf8JsonWriter writer, string name, IReadOnlyList<RailVolumeBox> boxes)
    {
        writer.WriteStartArray(name);
        foreach (var box in boxes)
        {
            writer.WriteStartObject();
            WriteDouble(writer, "min_x", box.MinX);
            WriteDouble(writer, "min_y", box.MinY);
            WriteDouble(writer, "min_z", box.MinZ);
            WriteDouble(writer, "max_x", box.MaxX);
            WriteDouble(writer, "max_y", box.MaxY);
            WriteDouble(writer, "max_z", box.MaxZ);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string RailEndWire(RailEnd end) => end switch
    {
        RailEnd.Start => "start",
        RailEnd.End => "end",
        _ => throw new ArgumentOutOfRangeException(nameof(end), end, null)
    };

    private static void WriteAssemblyMember(
        Utf8JsonWriter writer,
        string name,
        CompositeDefinitionId definitionId,
        CompositeTransform transform,
        LocalVoxelCoord couplerCell)
    {
        writer.WriteStartObject(name);
        writer.WriteString("definition_id", definitionId.Value);
        WriteTransform(writer, transform);
        WriteCoord(writer, "coupler_cell", couplerCell.X, couplerCell.Y, couplerCell.Z);
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

    private static bool TryReadVoxelCellSet(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("targets", JsonValueKind.Array, out var array, out status))
            return false;
        var targets = new List<VoxelCellTarget>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var target, out status) ||
                !target.TryCoord("position", out var x, out var y, out var z, out status) ||
                !target.TryTake("operation", JsonValueKind.String, out var operationElement, out status) ||
                !target.TryTakeAny("content", out var contentElement, out status))
                return false;
            if (!TryCellOperation(operationElement.GetString(), out var operation))
                return Fail(out edit, out status, WorldEditDecodeStatus.UnknownFormat);
            VoxelCellContent? content = null;
            if (contentElement.ValueKind != JsonValueKind.Null)
            {
                if (!Fields.TryOpen(contentElement, out var contentFields, out status) ||
                    !contentFields.TryVoxelDefinition("definition_id", out var definition, out status) ||
                    !contentFields.TryOrientation("orientation", out var orientation, out status) ||
                    !contentFields.TryFinish(out status))
                    return false;
                content = new VoxelCellContent(definition, orientation);
            }
            if (!target.TryFinish(out status))
                return false;
            targets.Add(new VoxelCellTarget(new WorldVoxelCoord(x, y, z), operation, content));
        }
        edit = new VoxelCellSetEdit(targets);
        return true;
    }

    private static bool TryReadVoxelCellRestore(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("targets", JsonValueKind.Array, out var array, out status))
            return false;
        var targets = new List<VoxelCellRestoreTarget>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var target, out status) ||
                !target.TryCoord("position", out var x, out var y, out var z, out status) ||
                !TryReadCellState(target, "expected", out var expected, out status) ||
                !TryReadCellState(target, "content", out var content, out status) ||
                !target.TryFinish(out status))
                return false;
            targets.Add(new VoxelCellRestoreTarget(new WorldVoxelCoord(x, y, z), expected, content));
        }

        edit = new VoxelCellRestoreEdit(targets);
        return true;
    }

    private static bool TryReadCellState(
        Fields fields,
        string name,
        out VoxelCellState? state,
        out WorldEditDecodeStatus status)
    {
        state = null;
        if (!fields.TryTakeAny(name, out var element, out status))
            return false;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (!Fields.TryOpen(element, out var content, out status) ||
            !content.TryVoxelDefinition("definition_id", out var definition, out status) ||
            !content.TryOrientation("orientation", out var orientation, out status) ||
            !content.TryInt32("current_durability", out var durability, out status) ||
            !content.TryFinish(out status))
            return false;
        state = new VoxelCellState(definition, orientation, durability);
        return true;
    }

    private static bool TryCellOperation(string? text, out VoxelCellOperation operation)
    {
        foreach (var candidate in Enum.GetValues<VoxelCellOperation>())
        {
            if (string.Equals(CellOperationWire(candidate), text, StringComparison.Ordinal))
            {
                operation = candidate;
                return true;
            }
        }
        operation = default;
        return false;
    }

    private static bool TryReadManufacture(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryCompositeDefinition("definition_id", out var definition, out status) ||
            !fields.TryTransform("transform", out var pose, out status))
            return false;
        edit = new VehicleManufactureEdit(definition, pose);
        return true;
    }

    private static bool TryReadRailConstruct(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("segments", JsonValueKind.Array, out var segmentArray, out status))
            return false;
        var segments = new List<PlannedRailSegment>();
        foreach (var item in segmentArray.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var segment, out status) ||
                !segment.TryTake("alignment", JsonValueKind.Object, out var alignmentElement, out status) ||
                !Fields.TryOpen(alignmentElement, out var alignment, out status) ||
                !alignment.TryDouble("start_x", out var startX, out status) ||
                !alignment.TryDouble("start_y", out var startY, out status) ||
                !alignment.TryDouble("start_z", out var startZ, out status) ||
                !alignment.TryDouble("end_x", out var endX, out status) ||
                !alignment.TryDouble("end_y", out var endY, out status) ||
                !alignment.TryDouble("end_z", out var endZ, out status) ||
                !alignment.TryFinish(out status) ||
                !segment.TryBoxes("rails", out var rails, out status) ||
                !segment.TryBoxes("sleepers", out var sleepers, out status) ||
                !segment.TryFinish(out status))
                return false;
            segments.Add(new PlannedRailSegment(new HorizontalStraightAlignment(startX, startY, startZ, endX, endY, endZ), rails, sleepers));
        }

        if (!fields.TryTake("connections", JsonValueKind.Array, out var connectionArray, out status))
            return false;
        var connections = new List<PlannedRailConnection>();
        foreach (var item in connectionArray.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var connection, out status) ||
                !connection.TryInt32("from_segment", out var fromSegment, out status) ||
                !connection.TryRailEnd("from_end", out var fromEnd, out status) ||
                !connection.TryInt32("to_segment", out var toSegment, out status) ||
                !connection.TryRailEnd("to_end", out var toEnd, out status) ||
                !connection.TryFinish(out status))
                return false;
            connections.Add(new PlannedRailConnection(fromSegment, fromEnd, toSegment, toEnd));
        }

        edit = new RailConstructEdit(new RailConstructionPlan(segments, connections));
        return true;
    }

    private static bool TryReadRailRepair(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("cells", JsonValueKind.Array, out var array, out status))
            return false;
        var cells = new List<WorldVoxelCoord>();
        foreach (var item in array.EnumerateArray())
        {
            if (!Fields.TryOpen(item, out var cell, out status) ||
                !cell.TryInt32("x", out var x, out status) ||
                !cell.TryInt32("y", out var y, out status) ||
                !cell.TryInt32("z", out var z, out status) ||
                !cell.TryFinish(out status))
                return false;
            cells.Add(new WorldVoxelCoord(x, y, z));
        }

        edit = new RailRepairEdit(cells);
        return true;
    }

    private static bool TryReadTrainAssemble(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!TryReadAssemblyMember(fields, "locomotive", out var locomotive, out var locomotivePose, out var locomotiveCoupler, out status) ||
            !TryReadAssemblyMember(fields, "carriage", out var carriage, out var carriagePose, out var carriageCoupler, out status))
            return false;
        edit = new TrainAssembleEdit(locomotive, locomotivePose, locomotiveCoupler, carriage, carriagePose, carriageCoupler);
        return true;
    }

    private static bool TryReadAssemblyMember(
        Fields fields,
        string name,
        out CompositeDefinitionId definition,
        out CompositeTransform pose,
        out LocalVoxelCoord coupler,
        out WorldEditDecodeStatus status)
    {
        definition = default;
        pose = default;
        coupler = default;
        if (!fields.TryTake(name, JsonValueKind.Object, out var element, out status) ||
            !Fields.TryOpen(element, out var member, out status) ||
            !member.TryCompositeDefinition("definition_id", out definition, out status) ||
            !member.TryTransform("transform", out pose, out status) ||
            !member.TryCoord("coupler_cell", out var x, out var y, out var z, out status) ||
            !member.TryFinish(out status))
            return false;
        coupler = new LocalVoxelCoord(x, y, z);
        return true;
    }

    private static bool TryReadTrainCommand(Fields fields, bool start, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryEntityId("assembly_id", out var assembly, out status))
            return false;
        edit = start ? new TrainStartEdit(assembly) : new TrainStopEdit(assembly);
        return true;
    }

    private static bool TryReadContentPublish(Fields fields, out WorldEdit edit, out WorldEditDecodeStatus status)
    {
        edit = null!;
        if (!fields.TryTake("content_digest", JsonValueKind.String, out var element, out status))
            return false;
        var digest = element.GetString();
        if (!ContentPublishEdit.IsDigest(digest))
            return Fail(out edit, out status, WorldEditDecodeStatus.Invalid);
        edit = new ContentPublishEdit(digest!);
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

        public bool TryTakeAny(string name, out JsonElement value, out WorldEditDecodeStatus status)
        {
            if (!_remaining.Remove(name, out value))
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

        public bool TryCompositeDefinition(string name, out CompositeDefinitionId id, out WorldEditDecodeStatus status)
        {
            id = default;
            if (!TryTake(name, JsonValueKind.String, out var element, out status))
                return false;
            var text = element.GetString()!;
            try
            {
                id = CompositeDefinitionId.Parse(text);
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

        public bool TryTransform(string name, out CompositeTransform pose, out WorldEditDecodeStatus status)
        {
            pose = default;
            if (!TryTake(name, JsonValueKind.Object, out var element, out status) ||
                !TryOpen(element, out var transform, out status))
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
            if (CompositeTransform.TryRestore(
                    values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8],
                    out pose))
                return true;
            status = WorldEditDecodeStatus.Invalid;
            return false;
        }

        public bool TryBoxes(string name, out IReadOnlyList<RailVolumeBox> boxes, out WorldEditDecodeStatus status)
        {
            boxes = Array.Empty<RailVolumeBox>();
            if (!TryTake(name, JsonValueKind.Array, out var array, out status))
                return false;
            var items = new List<RailVolumeBox>();
            foreach (var item in array.EnumerateArray())
            {
                if (!TryOpen(item, out var box, out status) ||
                    !box.TryDouble("min_x", out var minX, out status) ||
                    !box.TryDouble("min_y", out var minY, out status) ||
                    !box.TryDouble("min_z", out var minZ, out status) ||
                    !box.TryDouble("max_x", out var maxX, out status) ||
                    !box.TryDouble("max_y", out var maxY, out status) ||
                    !box.TryDouble("max_z", out var maxZ, out status) ||
                    !box.TryFinish(out status))
                    return false;
                items.Add(new RailVolumeBox(minX, minY, minZ, maxX, maxY, maxZ));
            }

            boxes = items;
            return true;
        }

        public bool TryRailEnd(string name, out RailEnd end, out WorldEditDecodeStatus status)
        {
            end = default;
            if (!TryTake(name, JsonValueKind.String, out var element, out status))
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
                    status = WorldEditDecodeStatus.Invalid;
                    return false;
            }
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

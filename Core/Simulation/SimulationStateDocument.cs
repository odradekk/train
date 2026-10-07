// Train.Core.Simulation 读写核心状态的共享基础字段。
// 恢复载荷与检查点通过同一实现保持字段、枚举和值域一致。

using System.Text.Json;

namespace Train.Core.Simulation;

/// <summary>读写完整模拟边界的基础权威字段。</summary>
internal static class SimulationStateDocument
{
    private static readonly string[] FieldNames =
    [
        "world_seed", "generator_version", "completed_steps", "boundary_sequence",
        "next_command_id", "next_entity_id", "transaction_count", "fault"
    ];

    /// <summary>按固定顺序写入完整 simulation 对象。</summary>
    internal static void Write(Utf8JsonWriter writer, SimulationState state)
    {
        writer.WriteStartObject();
        writer.WriteNumber("world_seed", state.WorldSeed);
        writer.WriteString("generator_version", state.GeneratorVersion);
        writer.WriteNumber("completed_steps", state.CompletedSteps);
        writer.WriteNumber("boundary_sequence", state.BoundarySequence);
        writer.WriteNumber("next_command_id", state.NextCommandId);
        writer.WriteNumber("next_entity_id", state.NextEntityId);
        writer.WriteNumber("transaction_count", state.TransactionCount);
        writer.WritePropertyName("fault");
        WriteFault(writer, state.Fault);
        writer.WriteEndObject();
    }

    /// <summary>严格读取完整 simulation 对象。</summary>
    internal static SimulationState Read(JsonElement fields, string path)
    {
        CheckKnownFields(fields, path, FieldNames);
        return new SimulationState(
            SimulationJson.UInt64(Property(fields, "world_seed", path), path + "/world_seed"),
            SimulationJson.String(Property(fields, "generator_version", path), path + "/generator_version", true))
        {
            CompletedSteps = SimulationJson.UInt64(Property(fields, "completed_steps", path),
                path + "/completed_steps"),
            BoundarySequence = SimulationJson.UInt64(Property(fields, "boundary_sequence", path),
                path + "/boundary_sequence"),
            NextCommandId = ReadNonZero(Property(fields, "next_command_id", path), path + "/next_command_id",
                "invalid_command_id"),
            NextEntityId = ReadNonZero(Property(fields, "next_entity_id", path), path + "/next_entity_id",
                "invalid_entity_id"),
            TransactionCount = SimulationJson.UInt64(Property(fields, "transaction_count", path),
                path + "/transaction_count"),
            Fault = ReadFault(Property(fields, "fault", path), path + "/fault")
        };
    }

    /// <summary>拒绝对象中的未知和重复字段，缺失字段留给固定读取顺序定位。</summary>
    private static void CheckKnownFields(JsonElement value, string path, params string[] names)
    {
        SimulationJson.RequireObject(value, path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var fieldPath = path + "/" + SimulationJson.Escape(property.Name);
            if (!seen.Add(property.Name)) throw SimulationJson.Invalid("duplicate_field", fieldPath);
            if (!names.Contains(property.Name, StringComparer.Ordinal))
                throw SimulationJson.Invalid("unknown_field", fieldPath);
        }
    }

    /// <summary>按固定读取顺序取得字段并定位缺失值。</summary>
    private static JsonElement Property(JsonElement value, string name, string path) =>
        value.TryGetProperty(name, out var property)
            ? property
            : throw SimulationJson.Invalid("missing_field", path + "/" + SimulationJson.Escape(name));

    /// <summary>按固定顺序写入可选故障及其事务上下文。</summary>
    private static void WriteFault(Utf8JsonWriter writer, SimulationFault? fault)
    {
        if (fault is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("code", fault.Code);
        writer.WriteString("phase", PhaseText(fault.Phase));
        writer.WritePropertyName("context");
        WriteContext(writer, fault.Context);
        writer.WritePropertyName("entity_id");
        if (fault.EntityId is ulong entityId) writer.WriteNumberValue(entityId); else writer.WriteNullValue();
        writer.WriteEndObject();
    }

    /// <summary>按固定顺序写入可选事务上下文。</summary>
    private static void WriteContext(Utf8JsonWriter writer, TransactionContext? context)
    {
        if (context is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("transaction_id", context.Value.TransactionId);
        writer.WriteString("operation", OperationText(context.Value.Operation));
        writer.WritePropertyName("position");
        writer.WriteStartObject();
        writer.WriteNumber("completed_steps", context.Value.Position.CompletedSteps);
        writer.WriteNumber("sequence", context.Value.Position.Sequence);
        writer.WriteEndObject();
        writer.WritePropertyName("command_id");
        if (context.Value.CommandId is ulong commandId) writer.WriteNumberValue(commandId); else writer.WriteNullValue();
        writer.WriteEndObject();
    }

    /// <summary>读取 null 或严格故障对象。</summary>
    private static SimulationFault? ReadFault(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        CheckKnownFields(value, path, "code", "phase", "context", "entity_id");
        return new SimulationFault(
            SimulationJson.String(Property(value, "code", path), path + "/code", true),
            ReadPhase(Property(value, "phase", path), path + "/phase"),
            ReadContext(Property(value, "context", path), path + "/context"),
            ReadNullableNonZero(Property(value, "entity_id", path), path + "/entity_id", "invalid_entity_id"));
    }

    /// <summary>读取 null 或严格事务上下文对象。</summary>
    private static TransactionContext? ReadContext(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        CheckKnownFields(value, path, "transaction_id", "operation", "position", "command_id");
        var position = Property(value, "position", path);
        CheckKnownFields(position, path + "/position", "completed_steps", "sequence");
        return new TransactionContext(
            ReadNonZero(Property(value, "transaction_id", path), path + "/transaction_id", "invalid_transaction_id"),
            ReadOperation(Property(value, "operation", path), path + "/operation"),
            new SimulationPosition(
                SimulationJson.UInt64(Property(position, "completed_steps", path + "/position"),
                    path + "/position/completed_steps"),
                SimulationJson.UInt64(Property(position, "sequence", path + "/position"),
                    path + "/position/sequence")),
            ReadNullableNonZero(Property(value, "command_id", path), path + "/command_id", "invalid_command_id"));
    }

    /// <summary>读取非零 UInt64 身份。</summary>
    private static ulong ReadNonZero(JsonElement value, string path, string code)
    {
        var result = SimulationJson.UInt64(value, path);
        return result != 0 ? result : throw SimulationJson.Invalid(code, path);
    }

    /// <summary>读取显式 null 或非零 UInt64 身份。</summary>
    private static ulong? ReadNullableNonZero(JsonElement value, string path, string code) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadNonZero(value, path, code);

    /// <summary>读取稳定的小写阶段名。</summary>
    private static SimulationPhase ReadPhase(JsonElement value, string path) =>
        SimulationJson.String(value, path) switch
        {
            "boundary" => SimulationPhase.Boundary,
            "command" => SimulationPhase.Command,
            "evaluate" => SimulationPhase.Evaluate,
            "resolve" => SimulationPhase.Resolve,
            "commit" => SimulationPhase.Commit,
            "validation" => SimulationPhase.Validation,
            _ => throw SimulationJson.Invalid("invalid_phase", path)
        };

    /// <summary>读取稳定的小写事务种类。</summary>
    private static SimulationOperation ReadOperation(JsonElement value, string path) =>
        SimulationJson.String(value, path) switch
        {
            "command" => SimulationOperation.Command,
            "step" => SimulationOperation.Step,
            _ => throw SimulationJson.Invalid("invalid_operation", path)
        };

    /// <summary>把阶段映射为稳定的小写文本。</summary>
    private static string PhaseText(SimulationPhase phase) => phase switch
    {
        SimulationPhase.Boundary => "boundary",
        SimulationPhase.Command => "command",
        SimulationPhase.Evaluate => "evaluate",
        SimulationPhase.Resolve => "resolve",
        SimulationPhase.Commit => "commit",
        SimulationPhase.Validation => "validation",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    /// <summary>把事务种类映射为稳定的小写文本。</summary>
    private static string OperationText(SimulationOperation operation) => operation switch
    {
        SimulationOperation.Command => "command",
        SimulationOperation.Step => "step",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };
}

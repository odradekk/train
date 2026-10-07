// Train.Tests.Core.Simulation 提供命令规范编码的真实测试领域适配器。
// 夹具只定义固定种类及字段，不承担公共包络、重复字段或摘要计算。

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

/// <summary>覆盖命令编码中各类标量、无序身份集合和有序身份序列的测试命令。</summary>
internal sealed record EncodingProbeCommand(
    long Signed,
    double Ratio,
    string Label,
    bool Enabled,
    ImmutableHashSet<ulong> EntityIds,
    ImmutableArray<ulong> Route) : CoreCommand;

/// <summary>把资源测试命令映射到版本 2 的固定种类名、字段顺序和字段约束。</summary>
internal sealed class ResourceCommandCodec : SimulationCommandCodec
{
    /// <summary>在领域字段写入时观察会话公开查询；未设置时不产生附加效果。</summary>
    internal Action? WritingFields { get; init; }

    protected override string GetKind(CoreCommand command) => command switch
    {
        NoChangeCommand => "fixture.no_change",
        CreateEntityCommand => "fixture.create_entity",
        DeleteEntityCommand => "fixture.delete_entity",
        CreateThenRejectCommand => "fixture.create_then_reject",
        InvalidCandidateCommand => "fixture.invalid_candidate",
        EncodingProbeCommand => "fixture.encoding_probe",
        _ => throw new ArgumentException("命令不属于资源测试格式。", nameof(command))
    };

    protected override void WriteFields(Utf8JsonWriter writer, CoreCommand command)
    {
        WritingFields?.Invoke();
        switch (command)
        {
            case DeleteEntityCommand delete:
                writer.WriteNumber("entity_id", delete.EntityId);
                break;
            case EncodingProbeCommand probe:
                writer.WriteNumber("signed", probe.Signed);
                writer.WritePropertyName("ratio");
                writer.WriteRawValue(probe.Ratio.ToString("R", CultureInfo.InvariantCulture));
                writer.WriteString("label", probe.Label);
                writer.WriteBoolean("enabled", probe.Enabled);
                writer.WritePropertyName("entity_ids");
                writer.WriteStartArray();
                foreach (var id in probe.EntityIds.Order())
                    writer.WriteNumberValue(id);
                writer.WriteEndArray();
                writer.WritePropertyName("route");
                writer.WriteStartArray();
                foreach (var id in probe.Route)
                    writer.WriteNumberValue(id);
                writer.WriteEndArray();
                break;
        }
    }

    protected override DeterminismResult<CoreCommand> ReadFields(string kind, JsonElement fields) => kind switch
    {
        "fixture.no_change" => ReadFieldless(fields, new NoChangeCommand()),
        "fixture.create_entity" => ReadFieldless(fields, new CreateEntityCommand()),
        "fixture.delete_entity" => ReadDelete(fields),
        "fixture.create_then_reject" => ReadFieldless(fields, new CreateThenRejectCommand()),
        "fixture.invalid_candidate" => ReadFieldless(fields, new InvalidCandidateCommand()),
        "fixture.encoding_probe" => ReadProbe(fields),
        _ => Failure(DeterminismErrorKind.UnknownFormat, "unknown_command_kind", "/kind")
    };

    private static DeterminismResult<CoreCommand> ReadFieldless(JsonElement fields, CoreCommand command)
    {
        foreach (var property in fields.EnumerateObject())
            return Failure(DeterminismErrorKind.InvalidData, "unknown_field", $"/{Escape(property.Name)}");
        return new DeterminismResult<CoreCommand>.Success(command);
    }

    private static DeterminismResult<CoreCommand> ReadDelete(JsonElement fields)
    {
        foreach (var property in fields.EnumerateObject())
            if (property.Name != "entity_id")
                return Failure(DeterminismErrorKind.InvalidData, "unknown_field", $"/{Escape(property.Name)}");
        if (!fields.TryGetProperty("entity_id", out var entity))
            return Failure(DeterminismErrorKind.InvalidData, "missing_field", "/entity_id");
        if (entity.ValueKind != JsonValueKind.Number || !entity.TryGetUInt64(out var id))
            return Failure(DeterminismErrorKind.InvalidData, "invalid_type", "/entity_id");
        return id == 0
            ? Failure(DeterminismErrorKind.InvalidData, "out_of_range", "/entity_id")
            : new DeterminismResult<CoreCommand>.Success(new DeleteEntityCommand(id));
    }

    private static DeterminismResult<CoreCommand> ReadProbe(JsonElement fields)
    {
        var properties = fields.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var allowed = new[] { "signed", "ratio", "label", "enabled", "entity_ids", "route" };
        var unknown = properties.Keys.FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
        if (unknown is not null)
            return Failure(DeterminismErrorKind.InvalidData, "unknown_field", $"/{Escape(unknown)}");
        foreach (var name in allowed)
            if (!properties.ContainsKey(name))
                return Failure(DeterminismErrorKind.InvalidData, "missing_field", $"/{name}");

        if (properties["signed"].ValueKind != JsonValueKind.Number || !properties["signed"].TryGetInt64(out var signed))
            return Failure(DeterminismErrorKind.InvalidData, "invalid_type", "/signed");
        if (properties["ratio"].ValueKind != JsonValueKind.Number || !properties["ratio"].TryGetDouble(out var ratio) || !double.IsFinite(ratio))
            return Failure(DeterminismErrorKind.InvalidData, "invalid_number", "/ratio");
        if (properties["label"].ValueKind != JsonValueKind.String)
            return Failure(DeterminismErrorKind.InvalidData, "invalid_type", "/label");
        if (properties["enabled"].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failure(DeterminismErrorKind.InvalidData, "invalid_type", "/enabled");
        var ids = ReadIds(properties["entity_ids"], "/entity_ids", unordered: true);
        if (ids.Error is not null)
            return new DeterminismResult<CoreCommand>.Failure(ids.Error);
        var route = ReadIds(properties["route"], "/route", unordered: false);
        if (route.Error is not null)
            return new DeterminismResult<CoreCommand>.Failure(route.Error);

        return new DeterminismResult<CoreCommand>.Success(new EncodingProbeCommand(
            signed, ratio, properties["label"].GetString()!, properties["enabled"].GetBoolean(),
            ids.Values.ToImmutableHashSet(), route.Values.ToImmutableArray()));
    }

    private static (IReadOnlyList<ulong> Values, DeterminismError? Error) ReadIds(
        JsonElement element, string path, bool unordered)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return ([], new DeterminismError(DeterminismErrorKind.InvalidData, "invalid_type", path));
        var values = new List<ulong>();
        var seen = new HashSet<ulong>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{path}/{index}";
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetUInt64(out var id))
                return ([], new DeterminismError(DeterminismErrorKind.InvalidData, "invalid_type", itemPath));
            if (id == 0)
                return ([], new DeterminismError(DeterminismErrorKind.InvalidData, "out_of_range", itemPath));
            if (unordered && !seen.Add(id))
                return ([], new DeterminismError(DeterminismErrorKind.InvalidData, "duplicate_id", itemPath));
            values.Add(id);
            index++;
        }
        return (values, null);
    }

    private static DeterminismResult<CoreCommand> Failure(
        DeterminismErrorKind kind, string code, string path) =>
        new DeterminismResult<CoreCommand>.Failure(new DeterminismError(kind, code, path));

    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");
}

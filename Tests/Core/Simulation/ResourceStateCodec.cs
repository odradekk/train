// Train.Core.Tests.Simulation 提供资源状态格式的真实测试领域适配器。
// 夹具负责领域字段、关系和投影，不承担公共包络或规范摘要计算。

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

/// <summary>资源测试领域的版本 1 状态格式。</summary>
internal sealed class ResourceStateCodec : SimulationStateCodec
{
    public override string StateKind => "fixture.resources";
    public override uint StateVersion => 1;

    protected override void WriteDomain(Utf8JsonWriter writer, SimulationState state)
    {
        var resources = (ResourceState)state;
        writer.WriteNumber("stock", resources.Stock);
        writer.WritePropertyName("holdings");
        writer.WriteStartArray();
        foreach (var holding in resources.Holdings.OrderBy(pair => pair.Key))
        {
            writer.WriteStartObject();
            writer.WriteNumber("entity_id", holding.Key);
            writer.WriteNumber("amount", holding.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("entity_ids");
        writer.WriteStartArray();
        foreach (var entityId in resources.EntityIds)
            writer.WriteNumberValue(entityId);
        writer.WriteEndArray();
        writer.WritePropertyName("demands");
        writer.WriteStartArray();
        foreach (var demand in resources.Demands.OrderBy(value => value.EntityId).ThenBy(value => value.RequestId))
        {
            writer.WriteStartObject();
            writer.WriteNumber("entity_id", demand.EntityId);
            writer.WriteNumber("request_id", demand.RequestId);
            writer.WriteNumber("priority", demand.Priority);
            writer.WriteNumber("amount", demand.Amount);
            writer.WriteBoolean("allow_partial", demand.AllowPartial);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    protected override DeterminismResult<SimulationState> ReadDomain(SimulationState boundary, JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object)
            return Failure("invalid_type", "");
        var allowed = new[] { "stock", "holdings", "entity_ids", "demands" };
        foreach (var property in fields.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                return Failure("unknown_field", "/" + Escape(property.Name));
        foreach (var name in allowed)
            if (fields.EnumerateObject().Count(property => property.NameEquals(name)) != 1)
                return Failure(fields.TryGetProperty(name, out _) ? "duplicate_field" : "missing_field", "/" + name);

        if (!TryUInt64(fields.GetProperty("stock"), out var stock))
            return Failure("invalid_uint64", "/stock");

        var holdingsResult = ReadHoldings(fields.GetProperty("holdings"));
        if (holdingsResult.Error is not null) return new DeterminismResult<SimulationState>.Failure(holdingsResult.Error);
        var idsResult = ReadEntityIds(fields.GetProperty("entity_ids"));
        if (idsResult.Error is not null) return new DeterminismResult<SimulationState>.Failure(idsResult.Error);
        var demandsResult = ReadDemands(fields.GetProperty("demands"));
        if (demandsResult.Error is not null) return new DeterminismResult<SimulationState>.Failure(demandsResult.Error);

        return new DeterminismResult<SimulationState>.Success(new ResourceState
        {
            WorldSeed = boundary.WorldSeed,
            GeneratorVersion = boundary.GeneratorVersion,
            CompletedSteps = boundary.CompletedSteps,
            BoundarySequence = boundary.BoundarySequence,
            NextCommandId = boundary.NextCommandId,
            NextEntityId = boundary.NextEntityId,
            TransactionCount = boundary.TransactionCount,
            Fault = boundary.Fault,
            Stock = stock,
            Holdings = holdingsResult.Value!,
            EntityIds = idsResult.Value!,
            Demands = demandsResult.Value!.Value
        });
    }

    protected override ImmutableArray<CheckpointObject> ProjectDomain(SimulationState state)
    {
        var resources = (ResourceState)state;
        var objects = ImmutableArray.CreateBuilder<CheckpointObject>();
        var demands = string.Join(',', resources.Demands
            .OrderBy(x => x.EntityId)
            .ThenBy(x => x.RequestId)
            .Select(x => string.Create(CultureInfo.InvariantCulture,
                $"{{\"allow_partial\":{x.AllowPartial.ToString().ToLowerInvariant()},\"amount\":{x.Amount},\"entity_id\":{x.EntityId},\"priority\":{x.Priority},\"request_id\":{x.RequestId}}}")));
        objects.Add(Object("fixture.resources", 0, string.Create(CultureInfo.InvariantCulture,
            $"{{\"demands\":[{demands}],\"stock\":{resources.Stock}}}")));
        foreach (var entityId in resources.EntityIds)
        {
            var holding = resources.Holdings.TryGetValue(entityId, out var amount)
                ? amount.ToString(CultureInfo.InvariantCulture)
                : "null";
            objects.Add(Object("fixture.entity", entityId, string.Create(CultureInfo.InvariantCulture,
                $"{{\"holding\":{holding}}}")));
        }
        return objects.ToImmutable();
    }

    internal string WriteDomainText(ResourceState state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteDomain(writer, state);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    internal DeterminismResult<SimulationState> ReadDomainForTest(SimulationState boundary, string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadDomain(boundary, document.RootElement);
    }
    internal ImmutableArray<CheckpointObject> ProjectDomainForTest(ResourceState state) => ProjectDomain(state);

    private static (ImmutableDictionary<ulong, ulong>? Value, DeterminismError? Error) ReadHoldings(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return (null, Error("invalid_type", "/holdings"));
        var result = ImmutableDictionary.CreateBuilder<ulong, ulong>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"/holdings/{index++}";
            var shape = ReadObject(item, path, "entity_id", "amount");
            if (shape is not null)
                return (null, shape);
            if (!TryUInt64(item.GetProperty("entity_id"), out var id) || id == 0) return (null, Error("invalid_entity_id", path + "/entity_id"));
            if (!TryUInt64(item.GetProperty("amount"), out var amount)) return (null, Error("invalid_uint64", path + "/amount"));
            if (!result.TryAdd(id, amount)) return (null, Error("duplicate_identity", path + "/entity_id"));
        }
        return (result.ToImmutable(), null);
    }

    private static (ImmutableSortedSet<ulong>? Value, DeterminismError? Error) ReadEntityIds(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return (null, Error("invalid_type", "/entity_ids"));
        var result = ImmutableSortedSet.CreateBuilder<ulong>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryUInt64(item, out var id) || id == 0) return (null, Error("invalid_entity_id", $"/entity_ids/{index}"));
            if (!result.Add(id))
                return (null, Error("duplicate_identity", $"/entity_ids/{index}"));
            index++;
        }
        return (result.ToImmutable(), null);
    }

    private static (ImmutableArray<ResourceDemand>? Value, DeterminismError? Error) ReadDemands(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return (null, Error("invalid_type", "/demands"));
        var result = ImmutableArray.CreateBuilder<ResourceDemand>();
        var identities = new HashSet<(ulong, ulong)>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"/demands/{index++}";
            var shape = ReadObject(item, path, "entity_id", "request_id", "priority", "amount", "allow_partial");
            if (shape is not null)
                return (null, shape);
            if (!TryUInt64(item.GetProperty("entity_id"), out var entityId) || entityId == 0) return (null, Error("invalid_entity_id", path + "/entity_id"));
            if (!TryUInt64(item.GetProperty("request_id"), out var requestId)) return (null, Error("invalid_uint64", path + "/request_id"));
            if (!TryInt32(item.GetProperty("priority"), out var priority)) return (null, Error("invalid_int32", path + "/priority"));
            if (!TryUInt64(item.GetProperty("amount"), out var amount)) return (null, Error("invalid_uint64", path + "/amount"));
            if (item.GetProperty("allow_partial").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, Error("invalid_type", path + "/allow_partial"));
            if (!identities.Add((entityId, requestId))) return (null, Error("duplicate_identity", path));
            result.Add(new ResourceDemand(entityId, requestId, priority, amount, item.GetProperty("allow_partial").GetBoolean()));
        }
        return (result.ToImmutable(), null);
    }

    private static DeterminismError? ReadObject(JsonElement element, string path, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) return Error("invalid_type", path);
        foreach (var property in element.EnumerateObject()) if (!fields.Contains(property.Name, StringComparer.Ordinal)) return Error("unknown_field", path + "/" + Escape(property.Name));
        foreach (var field in fields)
        {
            var count = element.EnumerateObject().Count(x => x.NameEquals(field));
            if (count != 1)
                return Error(count == 0 ? "missing_field" : "duplicate_field", path + "/" + field);
        }
        return null;
    }
    private static CheckpointObject Object(string kind, ulong id, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(new(kind, id), document.RootElement);
    }
    private static DeterminismResult<SimulationState>.Failure Failure(string code, string path) => new(Error(code, path));
    private static DeterminismError Error(string code, string path) => new(DeterminismErrorKind.InvalidData, code, path);
    private static bool TryUInt64(JsonElement value, out ulong result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out result);
    }
    private static bool TryInt32(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }
    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

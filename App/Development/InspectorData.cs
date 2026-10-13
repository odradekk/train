using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Train.Core.Contracts;

namespace Train.App.Development;

internal enum InspectionStatus { Found, Missing, Unavailable }
internal sealed record ObjectInspection(WorldObjectId Id, InspectionStatus Status, string? Json, string? Explanation);
internal sealed record CommandInspection(RecordedCommand Record, string Kind, string Result,
    ImmutableArray<WorldObjectId> Subjects, string? Explanation);

internal static class InspectorData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new RuntimeRecordConverterFactory() }
    };

    internal static ObjectInspection Inspect(ICoreReadView view, WorldObjectId id)
    {
        try
        {
            var value = FindObject(view, id);
            return value is not null
                ? new(id, InspectionStatus.Found, JsonSerializer.Serialize(value, value.GetType(), JsonOptions), null)
                : new(id, InspectionStatus.Missing, null, "对象已失效；保留原身份。");
        }
        catch (Exception error)
        {
            return new(id, InspectionStatus.Unavailable, null, $"查询或显示失败：{error.Message}");
        }
    }

    internal static bool Exists(ICoreReadView view, WorldObjectId id)
    {
        try
        {
            return FindObject(view, id) is not null;
        }
        catch (Exception) { return false; }
    }

    private static object? FindObject(ICoreReadView view, WorldObjectId id) => id switch
    {
        WorldObjectId.Node x => FoundValue(view.FindNode(x.Id)),
        WorldObjectId.Edge x => FoundValue(view.FindEdge(x.Id)),
        WorldObjectId.Train x => FoundValue(view.FindTrain(x.Id)),
        WorldObjectId.Instance x => FoundValue(view.FindInstance(x.Id)),
        WorldObjectId.Settlement x => FoundValue(view.FindSettlement(x.Id)),
        WorldObjectId.Inventory x => FoundValue(view.FindInventory(x.Id)),
        WorldObjectId.Job x => FoundValue(view.FindJob(x.Id)),
        WorldObjectId.Combat x => FoundValue(view.FindCombat(x.Id)),
        WorldObjectId.Creature x => FoundValue(view.FindCreature(x.Id)),
        WorldObjectId.CreatureGroup x => FoundValue(view.FindCreatureGroup(x.Id)),
        WorldObjectId.Lair x => FoundValue(view.FindLair(x.Id)),
        _ => throw new NotSupportedException("未支持的公开对象身份。")
    };

    private static object? FoundValue<T>(Lookup<T> lookup) where T : notnull
        => lookup is Lookup<T>.Found found ? found.Value : null;

    internal static ImmutableArray<CommandInspection> ReadCommands(ImmutableArray<RecordedCommand> commands,
        string? kind = null, string? result = null, WorldObjectId? subject = null)
        => commands.Select(ReadCommand).Where(x => (kind is null || x.Kind == kind)
            && (result is null || x.Result == result)
            && (subject is null || x.Subjects.Contains(subject))).ToImmutableArray();

    private static CommandInspection ReadCommand(RecordedCommand record)
    {
        var subjects = new HashSet<WorldObjectId>();
        string kind = "<无法读取 kind>";
        string? explanation = null;
        try
        {
            using var document = JsonDocument.Parse(record.CanonicalPayload);
            var payload = document.RootElement;
            kind = payload.GetProperty("kind").GetString() ?? kind;
            switch (kind)
            {
                case "content.publish": break;
                case "train.form":
                    foreach (var vehicle in payload.GetProperty("ordered_vehicles").EnumerateArray())
                        subjects.Add(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle.GetUInt64()))));
                    break;
                case "train.disband": case "train.start": case "train.stop": case "train.replace_run_plan":
                    subjects.Add(new WorldObjectId.Train(new(payload.GetProperty("train").GetUInt64())));
                    if (kind == "train.replace_run_plan")
                        foreach (var stop in payload.GetProperty("plan").GetProperty("stops").EnumerateArray())
                            subjects.Add(new WorldObjectId.Node(new(stop.GetProperty("node").GetUInt64())));
                    break;
                case "rail.debug_add_break":
                    subjects.Add(new WorldObjectId.Edge(new(payload.GetProperty("edge").GetUInt64())));
                    break;
                default: explanation = "尚无此 kind 的公开规范身份字段；原始参数已保留。"; break;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            explanation = $"规范负载读取失败；原文已保留：{error.Message}";
        }
        var result = record.Result.Decision switch
        {
            CommandDecision.Accepted accepted => AddSubjects("accepted", accepted.Created),
            CommandDecision.Rejected rejected => AddSubjects("rejected", rejected.Problems
                .Where(x => x.Subject is not null).Select(x => x.Subject!)),
            CommandDecision.Faulted faulted => AddSubjects("faulted", faulted.Fault.Subject is { } id ? [id] : []),
            _ => "unknown"
        };
        return new(record, kind, result, subjects.ToImmutableArray(), explanation);

        string AddSubjects(string decision, IEnumerable<WorldObjectId> ids)
        {
            subjects.UnionWith(ids);
            return decision;
        }
    }

    // 公开查询中的抽象记录属性也须保留具体变体字段。
    private sealed class RuntimeRecordConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsAbstract && !type.IsInterface
            && type.Assembly == typeof(WorldObjectId).Assembly;
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(RuntimeRecordConverter<>).MakeGenericType(type))!;
    }

    private sealed class RuntimeRecordConverter<T> : JsonConverter<T>
    {
        public RuntimeRecordConverter() { }

        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, value!.GetType(), options);
    }
}

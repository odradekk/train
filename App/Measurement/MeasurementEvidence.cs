using System.Text.Json;
using static Train.App.Measurement.MeasurementJson;

namespace Train.App.Measurement;

internal sealed record SteadyMemory(double? Private, double? Working, double? Buffer, double? Texture, double? Render)
{
    internal static SteadyMemory Read(JsonElement value, bool frame = false) =>
        !frame && Number(At(value, "samples")) is not > 0 ? new(null, null, null, null, null) :
        new(Counter(value, "private_bytes"), Counter(value, "working_set_bytes"), Counter(value, "buffer_bytes"),
            Counter(value, "texture_bytes"), Counter(value, "video_bytes"));

    private static double? Counter(JsonElement value, string name) =>
        Number(At(value, name)) is { } number && number >= 0 ? number : null;
}

internal sealed record MeasuredOpening(string Type, bool Node, bool Valid, double? Click, double? End,
    double? Latency, double? Ready, SteadyMemory Steady);

/// <summary>只在计时结束后流式读取报告所需的旁车；各文件的缺失和截断分别保留为未知原因。</summary>
internal sealed class MeasurementEvidence
{
    private readonly List<(double At, SteadyMemory Memory)> _maps = [];
    internal List<MeasuredOpening> Openings { get; } = [];
    internal string? OpeningProblem { get; private set; }
    internal string? MapProblem { get; private set; }
    internal double? InitialHeap { get; private set; }
    internal List<double?> RetiredHeaps { get; } = [];
    internal string? HeapProblem { get; private set; }
    internal double? PreparationMaximum { get; private set; }
    internal int PreparationFrames { get; private set; }
    internal int PreparationSamples { get; private set; }
    internal string? PreparationProblem { get; private set; }

    internal static bool UsesPreparation(string scenario) => (scenario is
        "scene-node" or "scene-node-first" or "scene-edge" or "scene-follow" ||
        scenario.StartsWith("scene-route-", StringComparison.Ordinal)) && !scenario.Contains("capture", StringComparison.Ordinal);

    internal static bool UsesSidecars(string scenario) => scenario is
        "scene-node" or "scene-node-first" or "scene-edge" or "scene-follow" or "scene-follow-8x" or "scene-memory" or "scene-soak";

    internal SteadyMemory? MapBefore(double? click) => _maps
        .Where(map => map.At < click).OrderByDescending(map => map.At)
        .Select(map => map.Memory).FirstOrDefault();

    internal static MeasurementEvidence Read(string scenario, string directory)
    {
        var result = new MeasurementEvidence();
        if (UsesPreparation(scenario)) result.ReadPreparation(directory);
        result.OpeningProblem = ReadRecords(Path.Combine(directory, "scene-operations.jsonl"), record =>
        {
            if (scenario == "scene-memory" && At(record, "kind").ToString() == "natural-map")
            {
                if (Number(At(record, "at")) is not { } at) throw new JsonException("地图稳态记录缺少时间");
                result._maps.Add((at, SteadyMemory.Read(At(record, "memory"))));
                return;
            }
            if (At(record, "kind").ToString() != "open") return;
            var name = At(record, "name").ToString();
            result.Openings.Add(new(name, At(record, "target").ToString().StartsWith("Node", StringComparison.Ordinal),
                name.Length > 0 && At(record, "sample_valid").ValueKind == JsonValueKind.True,
                Number(At(record, "click")), Number(At(record, "observation_ended")),
                Number(At(record, "click_to_operable_seconds")), Number(At(record, "operable_to_ready_seconds")),
                SteadyMemory.Read(At(record, "steady"))));
        });
        if (scenario != "scene-memory") return result;

        Queue<(double At, SteadyMemory Memory)> map = [];
        var ended = false;
        result.MapProblem = ReadRecords(Path.Combine(directory, "scene-frames.jsonl"), record =>
        {
            if (At(record, "phase").ToString() != "initial-map")
            { if (map.Count > 0) ended = true; return; }
            if (ended) return;
            if (Number(At(record, "at")) is not { } at) throw new JsonException("地图帧缺少时间");
            map.Enqueue((at, SteadyMemory.Read(record, frame: true)));
            while (map.TryPeek(out var first) && first.At < at - 1) map.Dequeue();
        });
        double? Median(Func<SteadyMemory, double?> counter)
        {
            var values = map.Select(sample => counter(sample.Memory)).ToArray();
            return values.Length == 0 || values.Any(value => value is null) ? null :
                FrameMeasurements.Distribution(values.Select(value => value!.Value)).P50;
        }
        if (map.Count > 0)
            result._maps.Add((map.Last().At, new(Median(value => value.Private), Median(value => value.Working),
                Median(value => value.Buffer), Median(value => value.Texture), Median(value => value.Render))));
        var firstNode = result.Openings.Where(open => open.Node && open.Click.HasValue).MinBy(open => open.Click);
        result.HeapProblem = ReadRecords(Path.Combine(directory, "scene-collections.jsonl"), record =>
        {
            var phase = At(record, "phase").ToString();
            var at = Number(At(record, "at"));
            var live = Number(At(record, "managed_live_bytes"));
            if (phase == "initial-map" && Number(At(record, "cycle")) == 0 && at < firstNode?.Click)
                result.InitialHeap ??= live;
            else if (phase is "retired-map" or "final-map" && at.HasValue &&
                result.Openings.Any(open => open.Node && open.End <= at)) result.RetiredHeaps.Add(live);
        });
        return result;
    }

    private void ReadPreparation(string directory)
    {
        PreparationProblem = ReadRecords(Path.Combine(directory, "scene-resource-frames.jsonl"), record =>
        {
            PreparationFrames++;
            if (At(record, "after_operable").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("缺少场景准备窗口标记");
            if (At(record, "after_operable").ValueKind == JsonValueKind.False) return;
            var active = Number(At(record, "preparation_seconds"));
            var candidate = Number(At(record, "candidate_preparation_seconds"));
            var total = active + candidate;
            if (total is null || !double.IsFinite(total.Value) || active < 0 || candidate < 0)
                throw new JsonException("场景准备耗时缺失或无效");
            PreparationSamples++;
            PreparationMaximum = Math.Max(PreparationMaximum ?? 0, total.Value);
        });
    }

    private static string? ReadRecords(string path, Action<JsonElement> read)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("旁车记录必须是对象");
                read(document.RootElement);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return "旁车缺失、损坏或截断：" + Path.GetFileName(path); }
    }
}

internal static class MeasurementJson
{
    internal static JsonElement At(JsonElement value, params string[] path)
    {
        foreach (var part in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return default;
        return value;
    }

    internal static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    internal static IEnumerable<JsonElement> Array(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}

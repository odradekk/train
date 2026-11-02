using System.Text.Json;
using static Train.App.Measurement.MeasurementJson;

namespace Train.App.Measurement;

internal static partial class MeasurementAssessment
{
    private static void AddOpenings(List<MeasurementAssessmentItem> items, string scenario, MeasurementEvidence? evidence)
    {
        var groups = evidence?.Openings.GroupBy(open => open.Type).ToArray() ?? [];
        if (groups.Length == 0)
        {
            foreach (var metric in new[] { "opening_p95", "opening_maximum", "far_ready" })
                items.Add(Check(metric, "场景打开：" + scenario, null, evidence?.OpeningProblem ?? "缺少有效打开样本"));
            return;
        }
        foreach (var group in groups)
        {
            var valid = group.Where(open => open.Valid).ToArray();
            var latencies = valid.Select(open => open.Latency).ToArray();
            var allTimed = latencies.Length > 0 && latencies.All(value => value.HasValue);
            var reason = evidence!.OpeningProblem ?? (allTimed ? null : "缺少有效打开的可操作时间");
            var p95 = allTimed ? FrameMeasurements.Distribution(latencies.Select(value => value!.Value)).P95 : (double?)null;
            var window = "有效打开操作：" + group.Key;
            items.Add(Check("opening_p95", window, p95, reason ?? (valid.Length < MinimumOpeningSamples ? $"同类有效打开仅 {valid.Length} 次，不足 {MinimumOpeningSamples} 次" : null)));
            items.Add(Check("opening_maximum", window, Maximum(latencies), reason));
            items.Add(Check("far_ready", window, Maximum(valid.Select(open => open.Ready)), evidence.OpeningProblem));
        }
    }

    private static void AddMemory(List<MeasurementAssessmentItem> items, string scenario, JsonElement budget, MeasurementEvidence? evidence)
    {
        List<SteadyMemory> steady = evidence?.Openings.Select(open => open.Steady).ToList() ?? [];
        if (scenario == "scene-soak" && evidence is not null) steady.AddRange(evidence.MapMemory);
        foreach (var field in new[] { "initial_map", "editor_steady", "after_editor_map", "final_map" })
            if (At(budget, field).ValueKind == JsonValueKind.Object) steady.Add(SteadyMemory.Read(At(budget, field)));
        if (At(budget, "editor_sequence", "after").ValueKind == JsonValueKind.Object)
            steady.Add(SteadyMemory.Read(At(budget, "editor_sequence", "after")));
        const string window = "整个游戏：已记录稳态中位数中的最大值（不含峰值）";
        items.Add(Check("game_private", window, Maximum(steady.Select(value => value.Private)), evidence?.OpeningProblem));
        var working = Check("game_working_set", window, Maximum(steady.Select(value => value.Working)), evidence?.OpeningProblem);
        items.Add(OperatingSystem.IsLinux() ? working with
        {
            Threshold = null, Status = working.Status == "unknown" ? "unknown" : "recorded",
            Rule = "Linux 工作集只记录；3 GiB 上限留到目标配置 Windows 判定"
        } : working);
        items.Add(Check("game_render", window, Maximum(steady.Select(value => value.Render)), evidence?.OpeningProblem));
        if (scenario is not ("scene-node" or "scene-node-first" or "scene-memory" or "scene-soak")) return;

        var noBaseline = scenario != "scene-memory" ? "场景没有进入前的地图稳态读数或强制 GC 配对" : null;
        var nodes = evidence?.Openings.Where(open => open.Node && open.Valid).ToArray() ?? [];
        var pairs = nodes.Select(open => (Node: open.Steady, Map: evidence?.MapBefore(open.Click))).ToArray();
        var reason = noBaseline ?? evidence?.OpeningProblem ?? evidence?.MapProblem;
        const string paired = "同进程节点稳态 − 点击前最近一次地图稳态中位数";
        items.Add(Check("node_private_delta", paired,
            Maximum(pairs.Select(pair => pair.Node.Private - pair.Map?.Private)), reason));
        items.Add(Check("node_render_delta", paired,
            Maximum(pairs.Select(pair => pair.Node.Buffer + pair.Node.Texture - pair.Map?.Buffer - pair.Map?.Texture)), reason));
        if (scenario == "scene-soak") AddSoakHeap(items, At(budget, "soak"));
        else items.Add(Check("retired_heap_delta", "同进程返回地图后强制 GC − cycle 0 初始地图强制 GC",
            Maximum(evidence?.RetiredHeaps.Select(heap => heap - evidence.InitialHeap) ?? []),
            noBaseline ?? evidence?.OpeningProblem ?? evidence?.HeapProblem));
    }

    private static void AddSoakHeap(List<MeasurementAssessmentItem> items, JsonElement soak)
    {
        var rows = Array(At(soak, "memory", "reset_heaps")).ToArray();
        var heaps = rows.Where(heap => At(heap, "phase").ToString() == "reset").ToArray();
        var valid = rows.All(heap => At(heap, "phase").ToString() is "reset" or "final") &&
            heaps.Length >= 2 && Number(At(soak, "segments")) == heaps.Length &&
            heaps.Select((heap, index) => Number(At(heap, "segment")) == index + 1 &&
                Number(At(heap, "live_bytes")) is >= 0).All(value => value);
        var seconds = Number(At(soak, "seconds"));
        var item = Check("retired_heap_delta", "长测：每段重读同一存档、暂停地图后强制 GC − 第 1 段；各段最大值，不含 final",
            valid ? heaps.Max(heap => Number(At(heap, "live_bytes"))!.Value) - Number(At(heaps[0], "live_bytes")) : null,
            seconds is null or < GrowthObservationSeconds ? "观测时长不足两小时" :
            valid ? null : "缺少完整分段的同状态强制 GC 读数");
        items.Add(item with { ObservedSeconds = seconds, Excess = item.Status == "unknown" ? null :
            Math.Max(0, item.Measured!.Value - item.Threshold!.Value) });
    }

    private static double? Maximum(IEnumerable<double?> values)
    {
        var samples = values.ToArray();
        return samples.Length == 0 || samples.Any(value => value is null) ? null : samples.Max();
    }
}

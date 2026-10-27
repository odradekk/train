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
        foreach (var field in new[] { "initial_map", "editor_steady", "after_editor_map", "final_map" })
            if (At(budget, field).ValueKind == JsonValueKind.Object) steady.Add(SteadyMemory.Read(At(budget, field)));
        if (At(budget, "editor_sequence", "after").ValueKind == JsonValueKind.Object)
            steady.Add(SteadyMemory.Read(At(budget, "editor_sequence", "after")));
        const string window = "整个游戏：已记录稳态中位数中的最大值（不含峰值）";
        items.Add(Check("game_private", window, Maximum(steady.Select(value => value.Private)), evidence?.OpeningProblem));
        items.Add(Check("game_working_set", window, Maximum(steady.Select(value => value.Working)), evidence?.OpeningProblem));
        items.Add(Check("game_render", window, Maximum(steady.Select(value => value.Render)), evidence?.OpeningProblem));
        if (scenario is not ("scene-node" or "scene-node-first" or "scene-memory")) return;

        var noBaseline = scenario != "scene-memory" ? "场景没有进入前的地图稳态读数或强制 GC 配对" : null;
        var nodes = evidence?.Openings.Where(open => open.Node && open.Valid).ToArray() ?? [];
        var map = evidence?.InitialMap;
        var reason = noBaseline ?? evidence?.OpeningProblem ?? evidence?.MapProblem;
        const string paired = "同进程节点稳态 − 首次进入前地图末 1 秒中位数";
        items.Add(Check("node_private_delta", paired,
            Maximum(nodes.Select(open => open.Steady.Private - map?.Private)), reason));
        items.Add(Check("node_render_delta", paired,
            Maximum(nodes.Select(open => open.Steady.Buffer + open.Steady.Texture - map?.Buffer - map?.Texture)), reason));
        items.Add(Check("retired_heap_delta", "同进程返回地图后强制 GC − cycle 0 初始地图强制 GC",
            Maximum(evidence?.RetiredHeaps.Select(heap => heap - evidence.InitialHeap) ?? []),
            noBaseline ?? evidence?.OpeningProblem ?? evidence?.HeapProblem));
        if (scenario == "scene-memory") AddGrowth(items, default, Number(At(budget, "memory_loop_seconds")));
    }

    private static double? Maximum(IEnumerable<double?> values)
    {
        var samples = values.ToArray();
        return samples.Length == 0 || samples.Any(value => value is null) ? null : samples.Max();
    }
}

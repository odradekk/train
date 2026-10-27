using System.Text.Json;
using Train.Core.Contracts;
using static Train.App.Measurement.MeasurementJson;

namespace Train.App.Measurement;

internal sealed record MeasurementAssessmentItem(string Metric, string Window, string Unit, string Rule,
    double? Threshold, double? Measured, string Status, string? Reason, double? ObservedSeconds = null);

/// <summary>报告逐项对照已确认目标；不代表目标配置或完整负载验收。
/// Decision note: 2026-10-26-performance-measurement-system
/// </summary>
internal static partial class MeasurementAssessment
{
    internal const string Source = "docs/design/00-product-and-core-loop.md#本阶段性能目标";
    private const int MinimumOpeningSamples = 30;
    private const double GrowthObservationSeconds = 7200;
    // 设计 00 的秒、字节和倍速阈值只在这里维护；补齐范围按设计 01。
    private static readonly Dictionary<string, (double? Value, string Unit, string Rule)> Targets = new()
    {
        ["frame_p95"] = (0.0167, "seconds", "≤"),
        ["frame_p99"] = (0.025, "seconds", "≤"),
        ["frame_maximum"] = (0.1, "seconds", "≤"),
        ["actual_speed"] = (8, "multiplier", "标准混合负载，整个窗口请求 8× 且未暂停；≥"),
        ["opening_p95"] = (0.5, "seconds", $"同类至少 {MinimumOpeningSamples} 次有效打开；≤"),
        ["opening_maximum"] = (1, "seconds", "所有有效打开；≤"),
        ["far_ready"] = (5, "seconds", "首个可操作后，64 米完整显示范围；≤"),
        ["node_private_delta"] = (1073741824, "bytes", "节点稳态私有字节 − 进入前地图稳态私有字节；≤"),
        ["node_render_delta"] = (536870912, "bytes", "节点稳态缓冲区加纹理 − 进入前地图稳态缓冲区加纹理；≤"),
        ["game_private"] = (4294967296, "bytes", "整个游戏稳态私有字节；≤"),
        ["game_working_set"] = (3221225472, "bytes", "整个游戏稳态工作集；≤"),
        ["game_render"] = (2147483648, "bytes", "整个游戏稳态渲染分配（video_bytes）；≤"),
        ["retired_heap_delta"] = (104857600, "bytes", "返回地图后强制 GC 存活堆 − 首次进入前地图强制 GC 存活堆；≤"),
        ["private_growth"] = (null, "bytes/hour", "两小时反复打开不得持续增长"),
        ["working_set_growth"] = (null, "bytes/hour", "两小时反复打开不得持续增长"),
        ["render_growth"] = (null, "bytes/hour", "两小时反复打开不得持续增长")
    };

    internal static List<MeasurementAssessmentItem> Evaluate(string scenario, JsonElement identity,
        MeasurementTiming? timing, string? failure, MeasurementEvidence? evidence)
    {
        List<MeasurementAssessmentItem> items = [];
        if (scenario.Contains("capture", StringComparison.Ordinal) || scenario == "scene-reference" ||
            At(identity, "scene_budget", "capture_only").ValueKind == JsonValueKind.True ||
            At(identity, "scene_budget", "reference_only").ValueKind == JsonValueKind.True) return items;
        var route = scenario.StartsWith("scene-route-", StringComparison.Ordinal);
        var budget = At(identity, "scene_budget");
        var window = route
            ? "路线 timing：从点击起，包含打开过程的整条路线"
            : scenario == "scene-follow-8x" ? "跟随 8× timing：预热后的采样窗口"
            : scenario == "scene-memory" ? "场景 timing：初始 5 秒地图保持"
            : scenario == "scene-editor" ? "编辑器现有 timing 采样窗口"
            : "场景 timing：末尾 5 秒地图保持";
        if (scenario == "scene-soak") AddSoak(items, identity, budget);
        else
        {
            AddFrames(items, window, timing?.FrameSeconds);
            if (scenario is "scene-follow-8x" or "scene-route-8x")
                items.Add(Check("actual_speed", window, timing?.ActualSpeed,
                    timing is null || timing.Frames.IsEmpty ? "缺少窗口帧" :
                    timing.Frames.Any(frame => frame.RequestedSpeed != SimulationSpeed.Eight || frame.Paused)
                        ? "窗口含非 8× 或暂停帧" : null));
            if (route)
            {
                var startup = At(budget, "route", "startup");
                var item = Check("far_ready", "路线初始场景：沿用 route.startup",
                    Number(At(startup, "first_ready_at")) - Number(At(startup, "first_operable_at")));
                var status = At(startup, "status").ToString();
                var known = item.Measured.HasValue && status is "passed" or "failed";
                items.Add(item with { Status = known ? status : "unknown",
                    Reason = known ? null : "路线补齐未知或缺少时间：" + At(startup, "reason") });
            }
        }
        if (MeasurementEvidence.UsesSidecars(scenario) && scenario != "scene-memory") AddOpenings(items, scenario, evidence);
        AddMemory(items, scenario, budget, evidence);
        var problem = failure is not null ? "运行失败：" + failure :
            route && At(budget, "route", "route_completed").ValueKind != JsonValueKind.True ? "路线未完成或缺少完成标记" : null;
        return problem is null ? items : items.Select(item => item with { Status = "unknown", Reason = problem }).ToList();
    }

    private static void AddSoak(List<MeasurementAssessmentItem> items, JsonElement identity, JsonElement budget)
    {
        var soak = At(budget, "soak");
        var views = Array(At(soak, "views")).ToArray();
        if (views.Length == 0) AddFrames(items, "长测视图：缺少内嵌采样窗口", null);
        foreach (var view in views)
        {
            var name = At(view, "view").ToString();
            var window = "长测 soak.views：" + name;
            var frames = At(view, "frame_seconds");
            var frameProblem = Number(At(view, "frames")) is > 0 ? null : "缺少该视图的真实绘制帧";
            items.Add(Check("frame_p95", window, Number(At(frames, "p95")), frameProblem));
            items.Add(Check("frame_p99", window, Number(At(frames, "p99")), frameProblem));
            items.Add(Check("frame_maximum", window, Number(At(frames, "maximum")), frameProblem));
            if (!name.EndsWith("/8x", StringComparison.Ordinal)) continue;
            var samples = Array(At(soak, "samples")).Where(sample =>
                At(sample, "view").ToString() == name[..^3] && Number(At(sample, "speed")) == 8).ToArray();
            var reason = At(identity, "encounter_action", "value").ToString() != "None"
                ? "长测未确认关闭遇敌自动降速" : samples.Length == 0 ? "缺少该视图的 8× 样本" :
                samples.Any(sample => Number(At(sample, "paused_frames")) != 0 || Number(At(sample, "frames")) is not > 0)
                    ? "样本含暂停帧或缺少帧计数" : frameProblem;
            items.Add(Check("actual_speed", window,
                Number(At(view, "steps_per_second")) / SimulationTime.StepsPerSecond, reason));
        }
        AddGrowth(items, At(soak, "memory"), Number(At(soak, "seconds")));
    }

    private static void AddGrowth(List<MeasurementAssessmentItem> items, JsonElement memory, double? seconds)
    {
        foreach (var (metric, counter) in new[] { ("private_growth", "private_bytes_per_hour"),
            ("working_set_growth", "working_set_bytes_per_hour"), ("render_growth", "godot_video_bytes_per_hour") })
            items.Add(Check(metric, "反复打开的地图内存增长", Number(At(memory, counter)),
                "两小时持续增长的判定规则未定义" + (seconds is null || seconds < GrowthObservationSeconds ? "；观测时长不足两小时" : ""))
                with { ObservedSeconds = seconds });
    }

    private static void AddFrames(List<MeasurementAssessmentItem> items, string window, TimingDistribution? frames)
    {
        items.Add(Check("frame_p95", window, frames?.P95));
        items.Add(Check("frame_p99", window, frames?.P99));
        items.Add(Check("frame_maximum", window, frames?.Maximum));
    }

    private static MeasurementAssessmentItem Check(string metric, string window, double? measured, string? reason = null)
    {
        var target = Targets[metric];
        var status = reason is not null || measured is null ? "unknown" :
            (metric == "actual_speed" ? measured >= target.Value : measured <= target.Value) ? "passed" : "failed";
        return new(metric, window, target.Unit, target.Rule, target.Value, measured, status,
            status == "unknown" ? reason ?? "缺少测量数据" : null);
    }

}

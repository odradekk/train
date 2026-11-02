using System.Text.Json;

namespace Train.App.Measurement;

internal static class MeasurementReport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    internal static string Serialize(string scenario, IReadOnlyDictionary<string, object?> identity,
        MeasurementTiming? timing,
        string? failure = null, MeasurementEvidence? evidence = null)
    {
        if (timing is null && failure is null) throw new InvalidOperationException("成功报告必须包含真实绘制帧。");
        // 测量入口写入的身份标明是否在用户指定的测试机器上运行；缺省按开发机处理。
        var testMachine = identity.TryGetValue("machine", out var machine) && machine?.ToString() == "test";
        var nested = identity.TryGetValue("system", out var system) && system is JsonElement { ValueKind: JsonValueKind.Object } host &&
            host.TryGetProperty("display_wrapper", out var wrapper) && wrapper.ValueKind == JsonValueKind.Object;
        var world = scenario == "scene-soak" ? identity.GetValueOrDefault("soak_profile")?.ToString() ?? "standard-world" : "current-stage";
        return JsonSerializer.Serialize(new
        {
            outcome = failure is null ? "completed" : "failed",
            problem = failure,
            scenario,
            scope = new { stage = scenario == "scene-soak" ? 6 : 2,
                world,
                machine = testMachine ? "test" : "development",
                content = "formal-assets-at-tested-commit",
                art_review = "美术 Issue #358–#363、#393–#396 的 PR 已合并，正式外观仍待用户目视确认",
                composition = nested ? "游戏在 gamescope 嵌套合成器中以 1920×1080 输出运行，画面先经 gamescope 合成再交给主机合成器；参数见 identity.system.display_wrapper" : null,
                final_performance = false,
                unmeasured = Unmeasured(scenario, testMachine, world) },
            identity,
            timing,
            assessment = new
            {
                conclusion = "待验收",
                reason = "尚未提供目标配置实机的完整性能验收证据；逐项结果只说明本次窗口。",
                source = MeasurementAssessment.Source,
                items = MeasurementAssessment.Evaluate(scenario, JsonSerializer.SerializeToElement(identity, Json), timing, failure, evidence)
            }
        }, Json);
    }

    private static string[] Unmeasured(string scenario, bool testMachine, string world)
    {
        // 窗口长测仍未覆盖核心长测和新进程首开；只有标准世界的完整配方负载很快下降。
        string[] gaps = scenario == "scene-soak"
            ? ["core_24h_long_run_#367", "process_first_open_node_line_follow"]
            : ["full_demo_load", "long_runs"];
        if (world == "standard-world") gaps = [.. gaps, "full_recipe_load_only_briefly_after_each_reset"];
        return testMachine ? gaps : [.. gaps, "target_hardware"];
    }
}

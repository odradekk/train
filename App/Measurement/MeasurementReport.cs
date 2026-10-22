using System.Text.Json;

namespace Train.App.Measurement;

internal sealed record MemoryMeasurement(double ElapsedSeconds, long WorkingSetBytes, long PrivateBytes, long PeakWorkingSetBytes,
    double? GodotStaticBytes, double? VideoMemoryBytes);

internal sealed record MeasuredOperation(string Name, double ElapsedSeconds, double DurationSeconds,
    bool Succeeded, string? Problem = null);

internal static class MeasurementReport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    internal static string Serialize(string scenario, IReadOnlyDictionary<string, object?> identity,
        MeasurementTiming? timing, IEnumerable<MemoryMeasurement> memory, IEnumerable<MeasuredOperation> operations,
        string? failure = null)
    {
        if (timing is null && failure is null) throw new InvalidOperationException("成功报告必须包含真实绘制帧。");
        var scene = scenario.StartsWith("scene-", StringComparison.Ordinal);
        // 测量入口写入的身份标明是否在用户指定的测试机器上运行；缺省按开发机处理。
        var testMachine = identity.TryGetValue("machine", out var machine) && machine?.ToString() == "test";
        return JsonSerializer.Serialize(new
        {
            outcome = failure is null ? "completed" : "failed",
            problem = failure,
            scenario,
            scope = new { stage = scenario == "scene-soak" ? 6 : scene ? 2 : 1,
                world = scenario == "scene-soak" ? "standard-world" : scene ? "current-stage" : "small",
                machine = testMachine ? "test" : "development",
                content = scene ? "formal-assets-at-tested-commit" : "placeholder",
                art_review = scene ? "美术 Issue #358–#363、#393–#396 的 PR 已合并，正式外观仍待用户目视确认" : null,
                thresholds = "帧耗时尾部、加载等待等阈值待用户确认，本报告不判定通过",
                final_performance = false,
                unmeasured = testMachine ? new[] { "full_demo_load", "long_runs" } : new[] { "full_demo_load", "target_hardware", "long_runs" } },
            identity,
            timing,
            memory = memory.Select(sample => new
            {
                sample.ElapsedSeconds, sample.WorkingSetBytes, sample.PrivateBytes, sample.PeakWorkingSetBytes,
                GodotStaticBytes = sample.GodotStaticBytes is { } engine ? (object)engine : "unavailable",
                VideoMemoryBytes = sample.VideoMemoryBytes is { } video ? (object)video : "unavailable"
            }),
            operations
        }, Json);
    }
}

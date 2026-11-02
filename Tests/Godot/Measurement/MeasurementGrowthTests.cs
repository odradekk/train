using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementGrowthTests
{
    [TestMethod]
    [DataRow(104857599L, "passed", 0d)]
    [DataRow(104857600L, "passed", 0d)]
    [DataRow(104857601L, "failed", 1d)]
    public void ResetHeapUsesTheSameMapLimitAndExcludesFinal(long delta, string status, double excess)
    {
        using var report = SoakReport(new object[]
        {
            new { segment = 1, phase = "reset", live_bytes = 1000L },
            new { segment = 2, phase = "reset", live_bytes = 1000 + delta },
            new { segment = 2, phase = "final", live_bytes = 9999999999L }
        });
        var item = Item(report, "retired_heap_delta");
        Assert.AreEqual(status, item.GetProperty("status").GetString());
        Assert.AreEqual((double)delta, item.GetProperty("measured").GetDouble());
        Assert.AreEqual(104857600d, item.GetProperty("threshold").GetDouble());
        Assert.AreEqual(excess, item.GetProperty("excess").GetDouble());
    }

    [TestMethod]
    public void HeapChecksEveryResetAgainstTheFirstRatherThanTheLastOrCachedDelta()
    {
        using var report = SoakReport(new object[]
        {
            new { segment = 1, phase = "reset", live_bytes = 1000L, over_first_bytes = 0 },
            new { segment = 2, phase = "reset", live_bytes = 104858601L, over_first_bytes = 0 },
            new { segment = 3, phase = "reset", live_bytes = 1000L, over_first_bytes = 0 }
        }, segments: 3);
        var item = Item(report, "retired_heap_delta");
        Assert.AreEqual("failed", item.GetProperty("status").GetString());
        Assert.AreEqual(104857601d, item.GetProperty("measured").GetDouble());
    }

    [TestMethod]
    [TestCategory("integration")]
    public void SecondHourMatchesOfflineOlsWithJitteredBoundarySamples() => InDirectory(directory =>
    {
        // 同 test_soak_memory_assessment.py；四个等距边界样本的每步斜率为 2.8 MiB，每步 1801 秒。
        WriteMaps(directory, [(100, 0), (1901, 3), (3702, 3), (5503, 7), (7304, 11)]);
        using var report = SoakReport([], evidence: MeasurementEvidence.Read("scene-soak", directory));
        var item = Item(report, "private_growth");
        Assert.AreEqual("passed", item.GetProperty("status").GetString());
        Assert.AreEqual(10080d * 1048576 / 1801, item.GetProperty("measured").GetDouble(), 1e-6);
        Assert.AreEqual(5403d, item.GetProperty("observed_seconds").GetDouble());
        Assert.AreEqual(3700d, item.GetProperty("window_start_seconds").GetDouble());
    });

    [TestMethod]
    [TestCategory("integration")]
    [DataRow(209715199d, "passed", 0d)]
    [DataRow(209715200d, "passed", 0d)]
    [DataRow(209715201d, "failed", 1d)]
    public void PrivateGrowthExcludesWarmupAndUsesTheInclusiveSingleRunLimit(double slope, string status, double excess) =>
        InDirectory(directory =>
        {
            WriteMaps(directory, [(100, 0), (3700, 4096),
                (5500, 4096 + slope / 2 / 1048576), (7300, 4096 + slope / 1048576)]);
            using var report = SoakReport([], evidence: MeasurementEvidence.Read("scene-soak", directory));
            var item = Item(report, "private_growth");
            Assert.AreEqual(status, item.GetProperty("status").GetString());
            Assert.AreEqual(slope, item.GetProperty("measured").GetDouble(), 1e-6);
            Assert.AreEqual(excess, item.GetProperty("excess").GetDouble(), 1e-6);
        });

    [TestMethod]
    [TestCategory("integration")]
    public void WholeGameBudgetsIncludeNaturalMapReadingsAcrossTheRun() => InDirectory(directory =>
    {
        File.WriteAllLines(Path.Combine(directory, "scene-operations.jsonl"), new[]
        {
            JsonSerializer.Serialize(new { kind = "natural-map", at = 100, memory = new
                { samples = 3, private_bytes = 4294967297L, working_set_bytes = 3221225473L, video_bytes = 2147483649L } }),
            JsonSerializer.Serialize(new { kind = "natural-map", at = 7300, memory = new
                { samples = 3, private_bytes = 1L, working_set_bytes = 1L, video_bytes = 1L } })
        });
        using var report = SoakReport([], evidence: MeasurementEvidence.Read("scene-soak", directory));
        foreach (var (metric, expected) in new[] { ("game_private", 4294967297d),
            ("game_working_set", 3221225473d), ("game_render", 2147483649d) })
        {
            var item = Item(report, metric);
            Assert.AreEqual(metric == "game_working_set" && OperatingSystem.IsLinux() ? "recorded" : "failed",
                item.GetProperty("status").GetString());
            Assert.AreEqual(expected, item.GetProperty("measured").GetDouble());
        }
    });

    [TestMethod]
    [DataRow("scene-map")]
    [DataRow("scene-memory")]
    [DataRow("scene-soak")]
    [DataRow("scene-editor")]
    public void WorkingSetUsesTheMeasuringPlatformForEveryScenario(string scenario)
    {
        using var report = Report(scenario, Timing(), new()
        {
            ["os"] = OperatingSystem.IsLinux() ? "Microsoft Windows" : "Arch Linux",
            ["scene_budget"] = new { final_map = new
                { samples = 1, private_bytes = 1, working_set_bytes = 3221225473L, video_bytes = 1 } }
        });
        var item = Item(report, "game_working_set");
        Assert.AreEqual(OperatingSystem.IsLinux() ? "recorded" : "failed", item.GetProperty("status").GetString());
        Assert.AreEqual(3221225473d, item.GetProperty("measured").GetDouble());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("excess").ValueKind);
        if (OperatingSystem.IsLinux()) Assert.AreEqual(JsonValueKind.Null, item.GetProperty("threshold").ValueKind);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("bad-live")]
    [DataRow("extra-phase")]
    [DataRow("short")]
    public void HeapNeedsCompleteSegmentsAndTwoHours(string problem)
    {
        List<object> heaps =
        [new { segment = 1, phase = "reset", live_bytes = 1000L },
         new { segment = problem == "duplicate" ? 1 : 2, phase = "reset", live_bytes = problem == "bad-live" ? -1L : 1000L }];
        if (problem == "missing") heaps.RemoveAt(1);
        if (problem == "extra-phase") heaps.Add(new { segment = 3, live_bytes = 1000 });
        using var report = SoakReport(heaps.ToArray(), seconds: problem == "short" ? 7199 : 7200);
        var item = Item(report, "retired_heap_delta");
        Assert.AreEqual("unknown", item.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("excess").ValueKind);
    }

    [TestMethod]
    [TestCategory("integration")]
    [DataRow("partial")]
    [DataRow("truncated")]
    [DataRow("counter")]
    [DataRow("samples")]
    [DataRow("missing")]
    [DataRow("declared-short")]
    public void PrivateGrowthNeedsACompleteSecondHourAndValidEvidence(string problem) => InDirectory(directory =>
    {
        WriteMaps(directory, [(100, 0), (3700, 10), (5500, 10), (problem == "partial" ? 7299 : 7300, 10)]);
        var path = Path.Combine(directory, "scene-operations.jsonl");
        if (problem == "missing") File.Delete(path);
        else if (problem == "truncated") File.AppendAllText(path, "{\"kind\":");
        else if (problem is "counter" or "samples") File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            kind = "natural-map", at = problem == "samples" ? 100 : 7200, memory = new
            { samples = problem == "samples" ? 0 : 1, private_bytes = problem == "samples" ? 10485760d : (double?)null }
        }) + "\n");
        using var report = SoakReport([], seconds: problem == "declared-short" ? 7199 : 7200,
            evidence: MeasurementEvidence.Read("scene-soak", directory));
        var item = Item(report, "private_growth");
        Assert.AreEqual("unknown", item.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("excess").ValueKind);
    });

    [TestMethod]
    [DataRow("scene-memory")]
    [DataRow("scene-soak")]
    public void GrowthItemsOnlyContainTheLongSoakPrivateCriterion(string scenario)
    {
        using var report = Report(scenario, Timing());
        var names = report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("metric").GetString()).ToArray();
        Assert.IsFalse(names.Contains("working_set_growth"));
        Assert.IsFalse(names.Contains("render_growth"));
        Assert.AreEqual(scenario == "scene-soak", names.Contains("private_growth"));
    }

    private static void WriteMaps(string directory, (double At, double Mebibytes)[] points) =>
        File.WriteAllLines(Path.Combine(directory, "scene-operations.jsonl"), points.Select(point =>
            JsonSerializer.Serialize(new { kind = "natural-map", at = point.At, memory = new
            { samples = 3, private_bytes = point.Mebibytes * 1048576, working_set_bytes = 1, video_bytes = 1 } })));

    private static void InDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-growth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static JsonDocument SoakReport(object[] heaps, double seconds = 7200, int segments = 2,
        MeasurementEvidence? evidence = null) => JsonDocument.Parse(MeasurementReport.Serialize("scene-soak",
        new Dictionary<string, object?> { ["scene_budget"] = new { soak = new
        { seconds, segments, memory = new { reset_heaps = heaps } } } }, Timing(), evidence: evidence));
}

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
public sealed class MeasurementSoakEvidenceTests
{
    [TestMethod]
    [DataRow(.5, 5, "passed")]
    [DataRow(1.001, 5.001, "failed")]
    public void ThirtyValidSoakOpeningsAssessLatencyAndFarReady(double latency, double far, string expected) =>
        InDirectory(directory =>
        {
            Write(directory, Enumerable.Range(0, 30).Select(i => Open("soak-node", i, latency, far)));
            using var report = ReadReport(directory);
            foreach (var (metric, measured) in new[] { ("opening_p95", latency),
                         ("opening_maximum", latency), ("far_ready", far) })
                AssertMeasured(Opening(report, metric, "soak-node"), measured, expected);
        });

    [TestMethod]
    public void InvalidAndOtherTypesCannotCompleteTwentyNineSoakOpenings() => InDirectory(directory =>
    {
        var opens = Enumerable.Range(0, 29).Select(i => Open("soak-node", i, .5, 5)).ToList();
        opens.AddRange(Enumerable.Range(0, 30).Select(i => Open("soak-reopen", 100 + i, .01, .01)));
        opens.AddRange(Enumerable.Range(0, 30).Select(i => Open("soak-node", 200 + i, 100, 100, false)));
        Write(directory, opens);
        using var report = ReadReport(directory);
        var p95 = Opening(report, "opening_p95", "soak-node");
        Assert.AreEqual("unknown", p95.GetProperty("status").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(p95.GetProperty("reason").GetString()));
        AssertMeasured(Opening(report, "opening_p95", "soak-reopen"), .01, "passed");
        AssertMeasured(Opening(report, "opening_maximum", "soak-node"), .5, "passed");
        AssertMeasured(Opening(report, "far_ready", "soak-node"), 5, "passed");
    });

    [TestMethod]
    public void SoakUsesOpenSteadyAbsoluteMemoryAndIgnoresPeaksAndOtherSidecars() => InDirectory(directory =>
    {
        Write(directory, new[]
        {
            Open("soak-node", 0, .2, 1, steady: Steady(4294967296L, 10, 10)),
            Open("soak-node", 1, .2, 1, steady: Steady(10, 3221225473L, 10)),
            Open("soak-node", 2, .2, 1, steady: Steady(10, 10, 2147483648L))
        });
        File.WriteAllText(Path.Combine(directory, "scene-frames.jsonl"), "{\"at\":");
        File.WriteAllText(Path.Combine(directory, "scene-collections.jsonl"), "{\"at\":");
        var evidence = MeasurementEvidence.Read("scene-soak", directory);
        using var report = ReadReport(directory, evidence);
        AssertMeasured(Opening(report, "opening_maximum", "soak-node"), .2, "passed");
        AssertMeasured(Opening(report, "far_ready", "soak-node"), 1, "passed");
        AssertMeasured(Item(report, "game_private"), 4294967296d, "passed");
        AssertMeasured(Item(report, "game_working_set"), 3221225473d, "failed");
        AssertMeasured(Item(report, "game_render"), 2147483648d, "passed");
        foreach (var metric in new[] { "node_private_delta", "node_render_delta", "retired_heap_delta" })
        {
            var item = Item(report, metric);
            Assert.AreEqual("unknown", item.GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, item.GetProperty("measured").ValueKind);
        }
    });

    private static JsonDocument ReadReport(string directory, MeasurementEvidence? evidence = null) =>
        JsonDocument.Parse(MeasurementReport.Serialize("scene-soak", new Dictionary<string, object?>()
        {
            ["scene_budget"] = new { soak = new { operations = new[]
            {
                new { type = "soak-node", ready_seconds = new { p95 = .01 } },
                new { type = "soak-reopen", ready_seconds = new { p95 = .01 } }
            } } }
        }, Timing(), evidence: evidence ?? MeasurementEvidence.Read("scene-soak", directory)));

    private static JsonElement Opening(JsonDocument report, string metric, string type) =>
        report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("metric").GetString() == metric &&
                item.GetProperty("window").GetString()!.Contains(type, StringComparison.Ordinal));

    private static void AssertMeasured(JsonElement item, double measured, string status)
    {
        Assert.AreEqual(status, item.GetProperty("status").GetString());
        Assert.AreEqual(measured, item.GetProperty("measured").GetDouble());
    }

    private static object Open(string name, int index, double latency, double far, bool valid = true,
        object? steady = null) => new
    {
        kind = "open", name, target = "Node { Id = 2 }", click = index * 20d,
        observation_ended = index * 20d + latency + far + 5,
        click_to_operable_seconds = latency, operable_to_ready_seconds = far, sample_valid = valid,
        steady = steady ?? Steady(10, 10, 10)
    };

    private static object Steady(long privateBytes, long working, long render) => new
    {
        samples = 3, private_bytes = privateBytes, working_set_bytes = working, video_bytes = render,
        buffer_bytes = 1, texture_bytes = 1,
        peak_private_bytes = 9999999999L, peak_working_set_bytes = 9999999999L, peak_video_bytes = 9999999999L
    };

    private static void Write(string directory, IEnumerable<object> records) =>
        File.WriteAllLines(Path.Combine(directory, "scene-operations.jsonl"),
            records.Select(record => JsonSerializer.Serialize(record)));

    private static void InDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-soak-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

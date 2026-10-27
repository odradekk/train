using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
public sealed class MeasurementEvidenceTests
{
    [TestMethod]
    [DataRow(29, "unknown")]
    [DataRow(30, "passed")]
    public void OpeningP95RequiresThirtyValidOperationsOfTheSameType(int count, string expected) =>
        InDirectory(directory =>
        {
            var opens = Enumerable.Range(0, count).Select(i => Open("map-node", i * 10, .5, 5)).ToList();
            opens.AddRange(Enumerable.Range(0, 40).Select(i => Open("node-reopen", 1000 + i * 10, .01, .01)));
            opens.AddRange(Enumerable.Range(0, 40).Select(i => Open("map-node", 2000 + i * 10, .01, .01, valid: false)));
            Write(directory, "scene-operations.jsonl", opens);
            using var report = ReadReport("scene-node", directory);
            Assert.AreEqual(expected, Opening(report, "opening_p95", "map-node").GetProperty("status").GetString());
        });

    [TestMethod]
    [DataRow(.5, "passed")]
    [DataRow(.5001, "failed")]
    public void OpeningP95IncludesItsLimit(double seconds, string expected) => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", Enumerable.Range(0, 30).Select(i => Open("map-node", i * 10, seconds, 5)));
        using var report = ReadReport("scene-node", directory);
        Assert.AreEqual(expected, Opening(report, "opening_p95", "map-node").GetProperty("status").GetString());
    });

    [TestMethod]
    [DataRow(1, 5, "passed", "passed")]
    [DataRow(1.001, 5.001, "failed", "failed")]
    public void OpeningMaximumAndFarReadyUseValidOperationsIncludingEquality(
        double seconds, double far, string opening, string ready) => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("map-node", 0, seconds, far),
            Open("map-node", 20, 100, 100, valid: false)]);
        using var report = ReadReport("scene-node", directory);
        Assert.AreEqual(opening, Opening(report, "opening_maximum", "map-node").GetProperty("status").GetString());
        Assert.AreEqual(ready, Opening(report, "far_ready", "map-node").GetProperty("status").GetString());
    });

    [TestMethod]
    public void NodeWithoutPreMapCannotReportPairedMemoryDeltas() => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("map-node", 0, .2, 1)]);
        using var report = ReadReport("scene-node", directory);
        foreach (var metric in new[] { "node_private_delta", "node_render_delta", "retired_heap_delta" })
            Unknown(Item(report, metric));
    });

    [TestMethod]
    public void WholeGameMemoryUsesLargestSteadyValuesAndIgnoresPeaks() => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("map-node", 0, .2, 1,
            steady: Steady(7, 5000000000, 2000000000, 20, 30, 2100000000))]);
        var identity = new Dictionary<string, object?>
        {
            ["scene_budget"] = new { final_map = Steady(20, 1000000000, 3300000000, 40, 50, 2200000000) }
        };
        using var report = ReadReport("scene-node", directory, identity);
        Measured(report, "game_private", 5000000000, "failed");
        Measured(report, "game_working_set", 3300000000, "failed");
        Measured(report, "game_render", 2200000000, "failed");
    });

    [TestMethod]
    public void MemoryPairsNodeSteadyWithThePreviousMapLastSecondMedian() => InDirectory(directory =>
    {
        Write(directory, "scene-frames.jsonl", new[]
        {
            Frame(0, "initial-map", 900, 900, 900), Frame(3.9, "initial-map", 900, 900, 900),
            Frame(4, "initial-map", 100, 10, 40), Frame(4.5, "initial-map", 300, 30, 60),
            Frame(5, "initial-map", 200, 20, 50),
            Frame(20, "retired-map", 800, 80, 90), Frame(25, "retired-map", 800, 80, 90)
        });
        Write(directory, "scene-operations.jsonl", [Open("memory-node", 6, .2, 1,
            steady: Steady(10, 1000, 2000, 120, 250, 3000))]);
        using var report = ReadReport("scene-memory", directory);
        // Initial-map P50: private=200, buffer=20, texture=50. Later map is not this open's baseline.
        Measured(report, "node_private_delta", 800, "passed");
        Measured(report, "node_render_delta", 300, "passed");
    });

    [TestMethod]
    public void RetiredHeapUsesPostCollectionLiveBytesAgainstTheFirstMapCollection() => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("memory-node", 6, .2, 1)]);
        Write(directory, "scene-collections.jsonl", new object[]
        {
            new { cycle = 0, phase = "initial-map", at = 5.5, managed_live_bytes = 100,
                natural = new { private_bytes = 9999999999L }, heap_bytes = 9999999999L },
            new { cycle = 10, phase = "retired-map", at = 30, managed_live_bytes = 250,
                natural = new { private_bytes = 9999999999L }, heap_bytes = 9999999999L },
            new { cycle = 10, phase = "final-map", at = 31, managed_live_bytes = 300,
                natural = new { private_bytes = 9999999999L }, heap_bytes = 9999999999L }
        });
        using var report = ReadReport("scene-memory", directory);
        Measured(report, "retired_heap_delta", 200, "passed");
    });

    [TestMethod]
    public void MissingEvidenceCannotBecomeZeroMemory() => InDirectory(directory =>
    {
        using var report = ReadReport("scene-memory", directory);
        foreach (var metric in new[] { "node_private_delta",
            "node_render_delta", "retired_heap_delta", "game_private", "game_working_set", "game_render" })
            Unknown(Item(report, metric));
    });

    [TestMethod]
    [DataRow("{\"kind\":\"open\",\"name\":\"map-node\",\"sample_valid\":true}")]
    [DataRow("{\"kind\":\"open\",\"name\":\"map-node\",\"sample_valid\":false,\"click_to_operable_seconds\":0.1,\"operable_to_ready_seconds\":1}")]
    [DataRow("{\"kind\":\"open\",")]
    public void MissingInvalidOrTruncatedOpenEvidenceIsUnknown(string line) => InDirectory(directory =>
    {
        File.WriteAllText(Path.Combine(directory, "scene-operations.jsonl"), line + "\n");
        using var report = ReadReport("scene-node", directory);
        Unknown(Item(report, "opening_maximum"));
        Unknown(Item(report, "far_ready"));
    });

    [TestMethod]
    [DataRow("scene-frames.jsonl", "node_private_delta")]
    [DataRow("scene-collections.jsonl", "retired_heap_delta")]
    public void DamagedPairingEvidenceDoesNotInvalidateWholeGameSteadyMemory(string file, string metric) => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("memory-node", 6, 1, 5)]);
        File.WriteAllText(Path.Combine(directory, file), "{\"at\":");
        using var report = ReadReport("scene-memory", directory);
        Unknown(Item(report, metric));
        Measured(report, "game_private", 1000, "passed");
    });

    [TestMethod]
    public void ATruncatedTailCannotPassFromEarlierCompleteOpenRecords() => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("map-node", 6, .2, 1)]);
        File.AppendAllText(Path.Combine(directory, "scene-operations.jsonl"), "{\"kind\":");
        using var report = ReadReport("scene-node", directory);
        Assert.AreEqual("unknown", Item(report, "opening_maximum").GetProperty("status").GetString());
    });

    [TestMethod]
    public void MemoryKeepsSteadyReadingsWithoutJudgingDiagnosticOpeningTimes() => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("memory-node", 6, 100, 100)]);
        using var report = ReadReport("scene-memory", directory);
        var metrics = report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("metric").GetString()).ToArray();
        Assert.IsFalse(metrics.Intersect(new[] { "opening_p95", "opening_maximum", "far_ready" }).Any());
        Measured(report, "game_private", 1000, "passed");
    });

    [TestMethod]
    public void ZeroSteadySamplesCannotPassTheWholeGameBudget() => InDirectory(directory =>
    {
        Write(directory, "scene-operations.jsonl", [Open("map-node", 0, .2, 1,
            steady: new { samples = 0, private_bytes = 1, working_set_bytes = 1, video_bytes = 1 })]);
        using var report = ReadReport("scene-node", directory);
        Unknown(Item(report, "game_private"));
    });

    [TestMethod]
    [DataRow("node_private_delta", 1073741824L)]
    [DataRow("node_render_delta", 536870912L)]
    [DataRow("retired_heap_delta", 104857600L)]
    public void PairedMemoryLimitsIncludeEquality(string metric, long limit) => InDirectory(directory =>
    {
        foreach (var (offset, expected) in new[] { (-1, "passed"), (0, "passed"), (1, "failed") })
        {
            Write(directory, "scene-frames.jsonl", new[]
                { Frame(0, "initial-map", 100, 20, 0), Frame(4, "initial-map", 100, 20, 0), Frame(5, "initial-map", 100, 20, 0) });
            Write(directory, "scene-operations.jsonl", [Open("memory-node", 6, .2, 1,
                steady: Steady(10, 100 + limit + offset, 1, 20 + limit + offset, 0, 1))]);
            Write(directory, "scene-collections.jsonl", new object[]
            {
                new { cycle = 0, phase = "initial-map", at = 5.5, managed_live_bytes = 100L },
                new { cycle = 1, phase = "final-map", at = 30d, managed_live_bytes = 100 + limit + offset }
            });
            using var report = ReadReport("scene-memory", directory);
            Measured(report, metric, limit + offset, expected);
        }
    });

    private static object Open(string name, double click, double seconds, double far, bool valid = true, object? steady = null) =>
        new { kind = "open", name, target = "Node { Id = 2 }", click, observation_ended = click + seconds + far + 5,
            click_to_operable_seconds = seconds, operable_to_ready_seconds = far, sample_valid = valid,
            steady = steady ?? Steady(click + seconds + far + 5, 1000, 2000, 100, 200, 3000) };

    private static object Steady(double at, long privateBytes, long working, long buffer, long texture, long video) =>
        new { at, samples = 3, private_bytes = privateBytes, working_set_bytes = working, buffer_bytes = buffer,
            texture_bytes = texture, video_bytes = video, peak_private_bytes = 9999999999L, peak_working_set_bytes = 9999999999L };

    private static object Frame(double at, string phase, long privateBytes, long buffer, long texture) =>
        new { at, phase, private_bytes = privateBytes, working_set_bytes = privateBytes + 100,
            buffer_bytes = buffer, texture_bytes = texture, video_bytes = buffer + texture + 100 };

    private static JsonDocument ReadReport(string scenario, string directory, Dictionary<string, object?>? identity = null) =>
        JsonDocument.Parse(MeasurementReport.Serialize(scenario, identity ?? new(), Timing(),
            evidence: MeasurementEvidence.Read(scenario, directory)));

    private static JsonElement Opening(JsonDocument report, string metric, string name) =>
        report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("metric").GetString() == metric &&
                item.GetProperty("window").GetString()!.Contains(name, StringComparison.Ordinal));

    private static void Measured(JsonDocument report, string metric, double value, string status)
    {
        var item = Item(report, metric);
        Assert.AreEqual(value, item.GetProperty("measured").GetDouble());
        Assert.AreEqual(status, item.GetProperty("status").GetString());
    }

    private static void Unknown(JsonElement item)
    {
        Assert.AreEqual("unknown", item.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("measured").ValueKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));
    }

    private static void Write(string directory, string file, IEnumerable<object> values) =>
        File.WriteAllLines(Path.Combine(directory, file), values.Select(value => JsonSerializer.Serialize(value)));

    private static void InDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-measurement-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

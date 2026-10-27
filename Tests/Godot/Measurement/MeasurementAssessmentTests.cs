using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementAssessmentTests
{
    [TestMethod]
    [DataRow("frame_p95", 0.0166, "passed")]
    [DataRow("frame_p95", 0.0167, "passed")]
    [DataRow("frame_p95", 0.0168, "failed")]
    [DataRow("frame_p99", 0.0249, "passed")]
    [DataRow("frame_p99", 0.025, "passed")]
    [DataRow("frame_p99", 0.0251, "failed")]
    [DataRow("frame_maximum", 0.099, "passed")]
    [DataRow("frame_maximum", 0.1, "passed")]
    [DataRow("frame_maximum", 0.101, "failed")]
    public void FrameVerdictsUseTheDesignLimitIncludingEquality(string metric, double seconds, string expected)
    {
        using var report = Report("scene-map", Timing(seconds));
        var item = Item(report, metric);
        Assert.AreEqual(expected, item.GetProperty("status").GetString());
        Assert.AreEqual(seconds, item.GetProperty("measured").GetDouble());
        Assert.AreEqual("seconds", item.GetProperty("unit").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(item.GetProperty("window").GetString()));
    }

    [TestMethod]
    public void AFailedRunCannotPassAnyCriterionFromRetainedSamples()
    {
        using var report = Report("scene-map", Timing(0.01), failure: "window lost");
        var items = report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray().ToArray();
        Assert.IsTrue(items.Length > 0);
        Assert.IsTrue(items.All(item => item.GetProperty("status").GetString() == "unknown"));
        Assert.IsTrue(items.All(item => !string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString())));
    }

    [TestMethod]
    [DataRow("scene-capture")]
    [DataRow("scene-reference")]
    [DataRow("scene-route-capture-8x")]
    public void CaptureAndReferenceRunsHaveNoThresholdVerdicts(string scenario)
    {
        using var report = Report(scenario, Timing(0.01));
        Assert.AreEqual(0, report.RootElement.GetProperty("assessment").GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    [DataRow("test")]
    [DataRow("development")]
    public void AReportAlwaysKeepsOverallAcceptancePending(string machine)
    {
        using var report = Report("scene-map", Timing(0.01), new() { ["machine"] = machine });
        var assessment = report.RootElement.GetProperty("assessment");
        Assert.AreEqual("待验收", assessment.GetProperty("conclusion").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(assessment.GetProperty("reason").GetString()));
        Assert.IsFalse(report.RootElement.GetProperty("scope").GetProperty("final_performance").GetBoolean());
    }

    [TestMethod]
    [DataRow("game_private", 4294967296d)]
    [DataRow("game_working_set", 3221225472d)]
    [DataRow("game_render", 2147483648d)]
    public void WholeGameSteadyLimitsIncludeEquality(string metric, double limit)
    {
        foreach (var (offset, expected) in new[] { (-1, "passed"), (0, "passed"), (1, "failed") })
        {
            using var report = Report("scene-map", Timing(), new()
            { ["scene_budget"] = new { final_map = new { samples = 3,
                private_bytes = limit + offset, working_set_bytes = limit + offset, video_bytes = limit + offset } } });
            Assert.AreEqual(expected, Item(report, metric).GetProperty("status").GetString());
        }
    }

    internal static MeasurementTiming Timing(double seconds = 0.01, double actualSpeed = 8) =>
        new(0, 480, 1, actualSpeed, new(seconds, seconds, seconds, seconds), new(0, 0, 0, 0),
            0.005, 0, ImmutableArray.Create(new MeasuredFrame(1, seconds, 480, 0, SimulationSpeed.Eight, false)));

    internal static JsonDocument Report(string scenario, MeasurementTiming? timing,
        Dictionary<string, object?>? identity = null, string? failure = null) =>
        JsonDocument.Parse(MeasurementReport.Serialize(scenario, identity ?? new(), timing, failure));

    internal static JsonElement Item(JsonDocument report, string metric) =>
        report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("metric").GetString() == metric);
}

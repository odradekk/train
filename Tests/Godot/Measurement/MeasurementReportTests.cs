using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementReportTests
{
    [TestMethod]
    public void MissingSamplesCanOnlyBeReportedAsFailure()
    {
        Assert.Throws<InvalidOperationException>(() => MeasurementReport.Serialize("map-1x",
            new Dictionary<string, object?>(), null, [], []));
        using var report = JsonDocument.Parse(MeasurementReport.Serialize("map-1x",
            new Dictionary<string, object?>(), null, [], [], "no rendered frames"));
        Assert.AreEqual("failed", report.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, report.RootElement.GetProperty("timing").ValueKind);
    }

    [TestMethod]
    public void ReportKeepsRawEvidenceUnavailableMetricsAndFailedOperations()
    {
        var measurements = new FrameMeasurements(10, 20);
        measurements.AddFrame(10.1, 26, 0.007, SimulationSpeed.Eight, false, new(1, "Edge", 16, 2));
        var json = MeasurementReport.Serialize("follow-8x", new Dictionary<string, object?>
        {
            ["commit"] = "tested-build", ["dirty"] = false, ["input_sha256"] = "input-identity"
        }, measurements.Summarize(), [new(0.1, 1000, 2000, 1500, null, 3000)],
            [new("first-load", 0, 0.2, true), new("save", 0.5, 0.1, false, "publication_failed")]);
        using var document = JsonDocument.Parse(json);
        var report = document.RootElement;
        Assert.AreEqual("input-identity", report.GetProperty("identity").GetProperty("input_sha256").GetString());
        Assert.IsFalse(report.GetProperty("scope").GetProperty("final_performance").GetBoolean());
        var memory = report.GetProperty("memory")[0];
        Assert.AreEqual("unavailable", memory.GetProperty("godot_static_bytes").GetString());
        Assert.AreEqual(3000d, memory.GetProperty("video_memory_bytes").GetDouble());
        Assert.AreEqual(1500L, memory.GetProperty("peak_working_set_bytes").GetInt64());
        Assert.AreEqual(26ul, report.GetProperty("timing").GetProperty("frames")[0]
            .GetProperty("completed_steps").GetUInt64());
        var follow = report.GetProperty("timing").GetProperty("frames")[0].GetProperty("follow");
        Assert.AreEqual(1ul, follow.GetProperty("train_id").GetUInt64());
        Assert.AreEqual("Edge", follow.GetProperty("position_kind").GetString());
        Assert.AreEqual(16ul, follow.GetProperty("position_id").GetUInt64());
        Assert.AreEqual(2ul, follow.GetProperty("display_space").GetUInt64());
        Assert.AreEqual(JsonValueKind.Null, report.GetProperty("timing").GetProperty("window_median_actual_speed").ValueKind);
        var failedSave = report.GetProperty("operations")[1];
        Assert.IsFalse(failedSave.GetProperty("succeeded").GetBoolean());
        Assert.AreEqual("publication_failed", failedSave.GetProperty("problem").GetString());
        Assert.AreEqual(0.1, failedSave.GetProperty("duration_seconds").GetDouble());
    }
}

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
        Assert.Throws<InvalidOperationException>(() => MeasurementReport.Serialize("scene-map",
            new Dictionary<string, object?>(), null));
        using var report = JsonDocument.Parse(MeasurementReport.Serialize("scene-map",
            new Dictionary<string, object?>(), null, "no rendered frames"));
        Assert.AreEqual("failed", report.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, report.RootElement.GetProperty("timing").ValueKind);
    }

    [TestMethod]
    public void ReportKeepsRawTimingAndIdentity()
    {
        var measurements = new FrameMeasurements(10, 20);
        measurements.AddFrame(10.1, 26, 0.007, SimulationSpeed.Eight, false);
        var json = MeasurementReport.Serialize("scene-route-8x", new Dictionary<string, object?>
        {
            ["commit"] = "tested-build", ["dirty"] = false, ["input_sha256"] = "input-identity"
        }, measurements.Summarize());
        using var document = JsonDocument.Parse(json);
        var report = document.RootElement;
        Assert.AreEqual("input-identity", report.GetProperty("identity").GetProperty("input_sha256").GetString());
        Assert.IsFalse(report.GetProperty("scope").GetProperty("final_performance").GetBoolean());
        var timing = report.GetProperty("timing");
        Assert.AreEqual(26ul, timing.GetProperty("frames")[0].GetProperty("completed_steps").GetUInt64());
        Assert.AreEqual(0.007, timing.GetProperty("simulation_seconds").GetProperty("maximum").GetDouble());
        Assert.AreEqual(0.1, timing.GetProperty("elapsed_seconds").GetDouble(), 1e-12);
        Assert.AreEqual(1d, timing.GetProperty("actual_speed").GetDouble(), 1e-12);
    }
}

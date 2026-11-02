using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementReportTests
{
    [TestMethod]
    [DataRow("scene-soak", null, "standard-world", true)]
    [DataRow("scene-soak", "standard-world", "standard-world", true)]
    [DataRow("scene-soak", "crowded-station", "crowded-station", false)]
    [DataRow("scene-soak", "busy-combat", "busy-combat", false)]
    [DataRow("scene-soak", "long-consist", "long-consist", false)]
    [DataRow("scene-map", null, "current-stage", false)]
    public void ReportScopeDescribesSelectedSoakWorld(string scenario, string? profile,
        string expectedWorld, bool expectedRecipeGap)
    {
        var identity = new Dictionary<string, object?>();
        if (profile is not null) identity["soak_profile"] = profile;
        using var report = JsonDocument.Parse(MeasurementReport.Serialize(scenario, identity,
            null, "no rendered frames"));
        var scope = report.RootElement.GetProperty("scope");
        var recipeGap = scope.GetProperty("unmeasured").EnumerateArray().Any(gap =>
            gap.GetString() == "full_recipe_load_only_briefly_after_each_reset");

        Assert.AreEqual((expectedWorld, expectedRecipeGap),
            (scope.GetProperty("world").GetString(), recipeGap));
    }

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

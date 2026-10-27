using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class SoakSamplingTests
{
    [TestMethod]
    [DataRow("map", false)]
    [DataRow("editor", true)]
    public void ViewRecordKeepsGcAndFrameEvidenceInTheSameWindow(string view, bool paused)
    {
        var sample = new MeasurementRun.SoakSample(view, SimulationSpeed.Eight, 100, 1000,
            TimeSpan.FromSeconds(2), [10, 4, 1]);
        sample.Frames.AddFrame(100.5, paused ? 1000UL : 1240UL, .001, SimulationSpeed.Eight, paused);
        sample.Frames.AddFrame(101, paused ? 1000UL : 1480UL, .001, SimulationSpeed.Eight, paused);
        sample.Classes["all-but-line-battle"] = 2;

        var (timing, record) = sample.Finish(3, TimeSpan.FromSeconds(2.125), [13, 6, 2]);
        using var report = Report(timing, record);
        var result = report.RootElement.GetProperty("identity").GetProperty("scene_budget")
            .GetProperty("soak").GetProperty("samples")[0];

        Assert.AreEqual(3, result.GetProperty("segment").GetInt32());
        Assert.AreEqual(view, result.GetProperty("view").GetString());
        Assert.AreEqual(8, result.GetProperty("speed").GetInt32());
        Assert.AreEqual(100d, result.GetProperty("started").GetDouble());
        Assert.AreEqual(1d, result.GetProperty("seconds").GetDouble());
        Assert.AreEqual(2, result.GetProperty("frames").GetInt32());
        Assert.AreEqual(paused ? 2 : 0, result.GetProperty("paused_frames").GetInt32());
        Assert.AreEqual(paused ? 0d : 480d, result.GetProperty("steps_per_second").GetDouble());
        Assert.AreEqual(2, result.GetProperty("load_classes").GetProperty("all-but-line-battle").GetInt32());
        Assert.AreEqual(.125, result.GetProperty("pause_seconds").GetDouble());
        CollectionAssert.AreEqual(new[] { 3, 2, 1 },
            result.GetProperty("collections_by_generation").EnumerateArray().Select(item => item.GetInt32()).ToArray());
    }

    private static JsonDocument Report(MeasurementTiming timing, params object[] samples) =>
        JsonDocument.Parse(MeasurementReport.Serialize("scene-soak", new Dictionary<string, object?>
        {
            ["scene_budget"] = new { soak = new { samples } }
        }, timing));
}

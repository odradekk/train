using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementWindowTests
{
    [TestMethod]
    [DataRow(7.99, "failed")]
    [DataRow(8d, "passed")]
    [DataRow(8.01, "passed")]
    public void EightTimesHasNoUndocumentedTolerance(double actualSpeed, string expected)
    {
        using var report = Report("scene-follow-8x", Timing(actualSpeed: actualSpeed));
        Assert.AreEqual(expected, Item(report, "actual_speed").GetProperty("status").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(Item(report, "actual_speed").GetProperty("rule").GetString()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnyDowngradedOrPausedFrameMakesTheSpeedUnknown(bool paused)
    {
        var timing = Timing();
        timing = timing with { Frames = timing.Frames.Add(new(2, 1, 960, 0,
            paused ? SimulationSpeed.Eight : SimulationSpeed.One, paused)) };
        using var report = Report("scene-follow-8x", timing);
        Assert.AreEqual("unknown", Item(report, "actual_speed").GetProperty("status").GetString());
    }

    [TestMethod]
    [DataRow("scene-route-1x")]
    [DataRow("scene-route-2x")]
    [DataRow("scene-route-4x")]
    public void LowerRequestedSpeedsDoNotClaimTheEightTimesGoal(string scenario)
    {
        using var report = Report(scenario, Timing());
        Assert.IsFalse(report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("metric").GetString() == "actual_speed"));
    }

    [TestMethod]
    public void AnUnfinishedRouteCannotPassFromItsFastPreviewFrames()
    {
        using var report = Report("scene-route-8x", Timing(), new()
        { ["scene_budget"] = new { route = new { route_completed = false, preview_seconds = 15 } } });
        Assert.AreEqual("unknown", Item(report, "frame_p95").GetProperty("status").GetString());
        Assert.AreEqual("unknown", Item(report, "actual_speed").GetProperty("status").GetString());
    }

    [TestMethod]
    [DataRow("passed", 3d)]
    [DataRow("failed", 6d)]
    [DataRow("unknown", 2d)]
    public void RouteFillInKeepsTheExistingAnalysisVerdict(string status, double readyAt)
    {
        using var report = Report("scene-route-8x", Timing(), new()
        { ["scene_budget"] = new { route = new { route_completed = true,
            startup = new { first_operable_at = 1, first_ready_at = readyAt, status, reason = "analysis-reason" } } } });
        var item = Item(report, "far_ready");
        Assert.AreEqual(status, item.GetProperty("status").GetString());
        Assert.AreEqual(readyAt - 1, item.GetProperty("measured").GetDouble());
    }

    [TestMethod]
    [DataRow("None", 0, 480d, "passed")]
    [DataRow("None", 0, 479.9, "failed")]
    [DataRow("None", 1, 480d, "unknown")]
    [DataRow("SlowDown", 0, 480d, "unknown")]
    public void SoakSpeedRequiresDisabledEncounterSlowdownAndUnpausedSamples(string action, int pausedFrames,
        double stepsPerSecond, string expected)
    {
        using var report = Report("scene-soak", Timing(), Soak(action, pausedFrames, stepsPerSecond));
        var speed = Item(report, "actual_speed");
        Assert.AreEqual(expected, speed.GetProperty("status").GetString());
        Assert.AreEqual(stepsPerSecond / 60, speed.GetProperty("measured").GetDouble());
        Assert.AreEqual("failed", Item(report, "frame_p99").GetProperty("status").GetString());
        StringAssert.Contains(speed.GetProperty("window").GetString()!, "map/8x");
    }

    [TestMethod]
    public void SoakDoesNotSubstitutePeaksOrResetHeapsForMissingSteadyPairs()
    {
        using var report = Report("scene-soak", Timing(), Soak("None", 0, 480));
        foreach (var metric in new[] { "node_private_delta", "node_render_delta", "retired_heap_delta", "game_private" })
            Assert.AreEqual("unknown", Item(report, metric).GetProperty("status").GetString(), metric);
        var growth = Item(report, "private_growth");
        Assert.AreEqual("unknown", growth.GetProperty("status").GetString());
        Assert.AreEqual(123, growth.GetProperty("measured").GetDouble());
        Assert.AreEqual(7200, growth.GetProperty("observed_seconds").GetDouble());
        Assert.IsFalse(string.IsNullOrWhiteSpace(growth.GetProperty("reason").GetString()));
    }

    [TestMethod]
    public void ASoakViewWithoutFramesCannotPassFromLeftoverDistributions()
    {
        using var report = Report("scene-soak", Timing(), new()
        { ["scene_budget"] = new { soak = new { views = new[] { new { view = "map/8x", frames = 0,
            frame_seconds = new { p95 = 0.01, p99 = 0.01, maximum = 0.01 } } } } } });
        Assert.AreEqual("unknown", Item(report, "frame_p95").GetProperty("status").GetString());
    }

    [TestMethod]
    public void RouteFillInNeedsItsMeasuredTimesEvenWhenTheStatusSaysPassed()
    {
        using var report = Report("scene-route-8x", Timing(), new()
        { ["scene_budget"] = new { route = new { route_completed = true, startup = new { status = "passed" } } } });
        Assert.AreEqual("unknown", Item(report, "far_ready").GetProperty("status").GetString());
    }

    private static Dictionary<string, object?> Soak(string action, int pausedFrames, double stepsPerSecond) => new()
    {
        ["encounter_action"] = new { value = action },
        ["scene_budget"] = new { soak = new
        {
            seconds = 7200,
            views = new[] { new { view = "map/8x", frames = 30, frame_seconds = new { p95 = 0.01, p99 = 0.03, maximum = 0.08 },
                steps_per_second = stepsPerSecond } },
            samples = new[] { new { view = "map", speed = 8, frames = 30, paused_frames = pausedFrames } },
            memory = new { private_bytes_per_hour = 123, working_set_bytes_per_hour = "unavailable",
                godot_video_bytes_per_hour = 0, reset_heaps = new[] { new { live_bytes = 1, over_first_bytes = 0 } } },
            operations = new[] { new { type = "node-first", peak_over_map = new { private_bytes = 1 },
                latency_seconds = new { p95 = 0.1 }, ready_seconds = new { maximum = 1 } } }
        } }
    };
}

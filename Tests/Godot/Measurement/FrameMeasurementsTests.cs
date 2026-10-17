using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class FrameMeasurementsTests
{
    [TestMethod]
    public void SpeedWindowsUseConsecutiveDrawBoundariesAndKeepWholeSampleTail()
    {
        var measurements = new FrameMeasurements(10, 100);
        measurements.AddFrame(10.5, 130, 0, SimulationSpeed.Eight, false);
        measurements.AddFrame(11.25, 250, 0, SimulationSpeed.Eight, false);
        measurements.AddFrame(11.75, 280, 0, SimulationSpeed.Eight, false);
        measurements.AddFrame(12, 430, 0, SimulationSpeed.Eight, false);
        measurements.AddFrame(12.5, 460, 0, SimulationSpeed.Eight, false);

        var result = measurements.Summarize(includeSpeedWindows: true);
        Assert.AreEqual(2, result.SpeedWindows.Length);
        Assert.AreEqual(new SpeedWindow(1, 0, 1.25, 150, 2), result.SpeedWindows[0]);
        Assert.AreEqual(new SpeedWindow(2, 1.25, 2, 180, 4), result.SpeedWindows[1]);
        Assert.AreEqual(3d, result.WindowMedianActualSpeed);
        Assert.AreEqual(2d, result.WindowMinimumActualSpeed);
        Assert.AreEqual(360ul, result.CompletedSteps);
        Assert.AreEqual(2.4d, result.ActualSpeed);
        Assert.AreEqual(5, result.Frames.Length);
    }

    [TestMethod]
    public void FullSampleKeepsAll120WindowsAndAveragesTheMiddlePair()
    {
        var measurements = new FrameMeasurements(0, 0);
        var steps = 0ul;
        for (var second = 1; second <= 120; second++)
        {
            steps += second <= 60 ? 60ul : 180ul;
            measurements.AddFrame(second, steps, 0, SimulationSpeed.Eight, false);
        }
        var result = measurements.Summarize(includeSpeedWindows: true);
        Assert.AreEqual(120, result.SpeedWindows.Length);
        Assert.AreEqual(2d, result.WindowMedianActualSpeed);
        Assert.AreEqual(1d, result.WindowMinimumActualSpeed);
        Assert.AreEqual(120, result.SpeedWindows[^1].TargetSecond);

        measurements.AddFrame(122.1, steps, 0, SimulationSpeed.Eight, false);
        Assert.Throws<InvalidOperationException>(() => measurements.Summarize(includeSpeedWindows: true));
        var legacy = measurements.Summarize();
        Assert.AreEqual(121, legacy.Frames.Length);
        Assert.AreEqual(0, legacy.SpeedWindows.Length);
        Assert.IsNull(legacy.WindowMedianActualSpeed);
        Assert.IsNull(legacy.WindowMinimumActualSpeed);
    }

    [TestMethod]
    public void PercentilesUseNearestRankAndKeepTheLargestStall()
    {
        var measurements = new FrameMeasurements(0, 0);
        var seconds = 0d;
        foreach (var milliseconds in Enumerable.Range(1, 100).Reverse())
        {
            seconds += milliseconds / 1000d;
            measurements.AddFrame(seconds, 0, 0, SimulationSpeed.Eight, true);
        }
        var result = measurements.Summarize();
        Assert.AreEqual(0.050, result.FrameSeconds.P50, 1e-12);
        Assert.AreEqual(0.095, result.FrameSeconds.P95, 1e-12);
        Assert.AreEqual(0.099, result.FrameSeconds.P99, 1e-12);
        Assert.AreEqual(0.100, result.FrameSeconds.Maximum, 1e-12);
        Assert.AreEqual(0d, result.ActualSpeed);
        Assert.AreEqual(100, result.Frames.Length);
    }

    [TestMethod]
    public void ActualSpeedUsesCompletedStepsAndWallTimeIncludingPausedFrames()
    {
        var measurements = new FrameMeasurements(10, 100);
        measurements.AddFrame(10.01, 101, 0.001, SimulationSpeed.One, false);
        measurements.AddFrame(10.04, 103, 0.006, SimulationSpeed.Eight, false);
        measurements.AddFrame(10.06, 103, 0, SimulationSpeed.Eight, true);
        measurements.AddFrame(10.10, 106, 0.005, SimulationSpeed.Four, false);
        var result = measurements.Summarize();
        Assert.AreEqual(6ul, result.CompletedSteps);
        Assert.AreEqual(0.1, result.ElapsedSeconds, 1e-12);
        Assert.AreEqual(1d, result.ActualSpeed, 1e-12);
        Assert.AreEqual(0.006, result.SimulationSeconds.Maximum);
        Assert.AreEqual(2, result.SimulationBudgetReachedFrames);
        Assert.IsTrue(result.Frames[2].Paused);
        Assert.AreEqual(SimulationSpeed.Four, result.Frames[3].RequestedSpeed);

        measurements.AddFrame(10.12, 107, 0.001, SimulationSpeed.One, false);
        Assert.AreEqual(4, result.Frames.Length);
    }

    [TestMethod]
    public void CheckCostsAreClippedToEachMeasuredIntervalWithoutDoubleCounting()
    {
        (double Started, double Ended)[] checks = [(0.9, 1.2), (1.8, 2.1), (2.8, 3.2), (3.9, 4.1)];

        var beforeOperable = FrameMeasurements.SumOverlap(checks, 1, 2);
        var afterOperable = FrameMeasurements.SumOverlap(checks, 2, 3);

        Assert.AreEqual(0.4, beforeOperable, 1e-12);
        Assert.AreEqual(0.3, afterOperable, 1e-12);
        Assert.AreEqual(FrameMeasurements.SumOverlap(checks, 1, 3), beforeOperable + afterOperable, 1e-12);
        Assert.AreEqual(0d, FrameMeasurements.SumOverlap(checks, 2, 2));
        Assert.AreEqual(0d, FrameMeasurements.SumOverlap(checks, 3, 2));
        Assert.AreEqual(0d, FrameMeasurements.SumOverlap([], 1, 3));
    }

    [TestMethod]
    public void MissingFramesAndDecreasingStepCountsCannotProduceSuccessfulMeasurements()
    {
        var measurements = new FrameMeasurements(10, 100);
        Assert.Throws<InvalidOperationException>(() => measurements.Summarize());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            measurements.AddFrame(10, 100, 0, SimulationSpeed.One, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            measurements.AddFrame(10.01, 99, 0, SimulationSpeed.One, false));
        measurements.AddFrame(10.02, 101, 0.001, SimulationSpeed.One, false);
        Assert.AreEqual(1ul, measurements.Summarize().CompletedSteps);
    }
}

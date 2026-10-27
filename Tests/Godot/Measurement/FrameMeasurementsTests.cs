using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class FrameMeasurementsTests
{
    [TestMethod]
    [DataRow(new double[] { 3 }, 3d, 3d)]
    [DataRow(new double[] { 3, 1 }, 1d, 3d)]
    [DataRow(new double[] { 3, 1, 2 }, 2d, 3d)]
    public void NearestRankKeepsSmallSamplesWithoutInterpolation(double[] samples, double p50, double tail)
    {
        var distribution = FrameMeasurements.Distribution(samples);
        Assert.AreEqual(p50, distribution.P50);
        Assert.AreEqual(tail, distribution.P95);
        Assert.AreEqual(tail, distribution.P99);
        Assert.AreEqual(tail, distribution.Maximum);
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

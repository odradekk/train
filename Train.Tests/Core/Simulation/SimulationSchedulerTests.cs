// Train.Tests.Core.Simulation 验证定步长调度器的纯 C# 时间契约。
// 测试仅累计宿主帧间隔，不构造列车、世界或 Godot 场景。

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationSchedulerTests
{
    [TestMethod]
    public void Accumulator_preserves_fractional_steps_and_display_alpha()
    {
        var scheduler = new SimulationScheduler();
        Assert.AreEqual(0.0, scheduler.Remainder);
        Assert.AreEqual(0.0, scheduler.DisplayAlpha);

        Assert.AreEqual(0UL, scheduler.CollectSteps(SimulationClock.StepDuration * 0.4, paused: false));
        Assert.AreEqual(SimulationClock.StepDuration * 0.4, scheduler.Remainder, 1e-9);
        Assert.AreEqual(0.4, scheduler.DisplayAlpha, 1e-9);

        Assert.AreEqual(1UL, scheduler.CollectSteps(SimulationClock.StepDuration * 0.7, paused: false));
        Assert.AreEqual(SimulationClock.StepDuration * 0.1, scheduler.Remainder, 1e-9);
        Assert.AreEqual(0.1, scheduler.DisplayAlpha, 1e-9);
    }

    [TestMethod]
    public void Pause_and_invalid_intervals_preserve_remainder()
    {
        var scheduler = new SimulationScheduler();
        scheduler.CollectSteps(SimulationClock.StepDuration * 0.4, paused: false);
        var remainder = scheduler.Remainder;
        var alpha = scheduler.DisplayAlpha;

        Assert.AreEqual(0UL, scheduler.CollectSteps(1.0, paused: true));
        Assert.AreEqual(remainder, scheduler.Remainder);
        Assert.AreEqual(alpha, scheduler.DisplayAlpha);

        foreach (var interval in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.AreEqual(0UL, scheduler.CollectSteps(interval, paused: false));
            Assert.AreEqual(remainder, scheduler.Remainder);
            Assert.AreEqual(alpha, scheduler.DisplayAlpha);
        }

        Assert.AreEqual(1UL, scheduler.CollectSteps(SimulationClock.StepDuration * 0.7, paused: false));
        Assert.AreEqual(0.1, scheduler.DisplayAlpha, 1e-9);
    }

    [TestMethod]
    public void Reset_discards_remainder()
    {
        var scheduler = new SimulationScheduler();
        scheduler.CollectSteps(SimulationClock.StepDuration * 0.4, paused: false);
        scheduler.Reset();

        Assert.AreEqual(0.0, scheduler.Remainder);
        Assert.AreEqual(0.0, scheduler.DisplayAlpha);
        Assert.AreEqual(0UL, scheduler.CollectSteps(SimulationClock.StepDuration * 0.7, paused: false));
        Assert.AreEqual(0.7, scheduler.DisplayAlpha, 1e-9);
    }

    [TestMethod]
    public void Display_rates_collect_the_same_step_count()
    {
        foreach (var (rate, frames) in new[] { (30, 150), (60, 300), (144, 720) })
        {
            var scheduler = new SimulationScheduler();
            ulong steps = 0;
            for (var frame = 0; frame < frames; frame++)
                steps += scheduler.CollectSteps(1.0 / rate, paused: false);

            Assert.AreEqual(300UL, steps);
            Assert.IsTrue(scheduler.DisplayAlpha is >= 0 and <= 1);
        }
    }
}

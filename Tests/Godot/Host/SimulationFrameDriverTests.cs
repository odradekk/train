using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class SimulationFrameDriverTests
{
    [TestMethod]
    public void Fractional_step_survives_pause_and_speed_change()
    {
        var driver = new SimulationFrameDriver(() => 0);
        driver.SetPlayerPaused(false);
        var calls = 0;
        (uint, bool) Advance() { calls++; return (1u, false); }
        Assert.AreEqual(0UL, driver.AdvanceFrame(SimulationTime.StepSeconds * 0.5, Advance));
        var retained = driver.Remainder;
        driver.SetPlayerPaused(true);
        Assert.AreEqual(0UL, driver.AdvanceFrame(10, Advance));
        driver.SetSpeed(SimulationSpeed.Two);
        Assert.AreEqual(retained, driver.Remainder);
        driver.SetPlayerPaused(false);
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds * 0.25, Advance));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, driver.Remainder, 1e-12);
    }

    [TestMethod]
    public void Frame_budget_checks_after_complete_step_and_discards_unused_allowance()
    {
        var now = 0.0;
        var driver = new SimulationFrameDriver(() => now);
        driver.SetPlayerPaused(false);
        var calls = 0;
        (uint, bool) SlowAdvance() { calls++; now += 0.006; return (1u, false); }
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds * 10, SlowAdvance));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0UL, driver.AdvanceFrame(SimulationTime.StepSeconds * 0.5, SlowAdvance));
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds * 0.5, SlowAdvance));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void Callback_stop_on_speed_reduction_discards_old_allowance()
    {
        var driver = new SimulationFrameDriver(() => 0);
        driver.SetPlayerPaused(false);
        driver.SetSpeed(SimulationSpeed.Eight);
        var calls = 0;
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds, () =>
        {
            calls++;
            driver.SetSpeed(SimulationSpeed.One);
            return (1u, true);
        }));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds, () =>
        {
            calls++;
            return (1u, false);
        }));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Player_resume_preserves_other_pause_sources(int source)
    {
        var driver = new SimulationFrameDriver(() => 0);
        if (source == 0) driver.SetFocusPaused(true);
        else if (source == 1) driver.SetOperationLocked(true);
        else if (source == 2) driver.Freeze();
        else driver.SetNotificationPaused(true);
        driver.SetSpeed(SimulationSpeed.Two);
        driver.SetPlayerPaused(false);
        Assert.IsFalse(driver.PlayerPaused);
        Assert.IsTrue(driver.IsPaused);
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, () => throw new AssertFailedException("暂停来源仍存在。")));
        Assert.AreEqual(0.0, driver.ActualSpeed);
    }

    [TestMethod]
    public void Selecting_speed_preserves_player_pause()
    {
        var driver = new SimulationFrameDriver(() => 0);
        driver.SetSpeed(SimulationSpeed.Four);
        Assert.IsTrue(driver.PlayerPaused);
        Assert.IsTrue(driver.IsPaused);
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, () => throw new AssertFailedException("切倍速不应恢复。")));
    }

    [TestMethod]
    public void Actual_speed_uses_completed_steps_and_monotonic_elapsed_time()
    {
        var now = 100.0;
        var driver = new SimulationFrameDriver(() => now);
        driver.SetPlayerPaused(false);
        driver.SetSpeed(SimulationSpeed.Eight);
        var completed = driver.AdvanceFrame(1, () =>
        {
            now += 2;
            return (1u, true);
        });
        Assert.AreEqual(1UL, completed);
        Assert.AreEqual(SimulationTime.StepSeconds / 2, driver.ActualSpeed, 1e-12);
        driver.SetPlayerPaused(true);
        Assert.AreEqual(0.0, driver.ActualSpeed);
        driver.SetPlayerPaused(false);
        now += 2;
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, () => (0u, false)));
        Assert.AreEqual(0.0, driver.ActualSpeed);
    }

    [TestMethod]
    public void Manual_steps_ignore_frame_budget_and_end_at_callback_stop()
    {
        var now = 0.0;
        var driver = new SimulationFrameDriver(() => now);
        var calls = 0;
        Assert.AreEqual(3UL, driver.Step(10, () =>
        {
            calls++;
            now += 1;
            return (1u, calls == 3);
        }));
        Assert.AreEqual(3, calls);
        Assert.IsTrue(driver.PlayerPaused);
    }

    [TestMethod]
    public void Zero_completion_ends_automatic_and_manual_loops()
    {
        var driver = new SimulationFrameDriver(() => 0);
        driver.SetPlayerPaused(false);
        var calls = 0;
        (uint, bool) NoProgress() { calls++; return (0u, false); }
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, NoProgress));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0UL, driver.Step(10, NoProgress));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void Notification_pause_stops_before_next_step_without_time_debt()
    {
        var driver = new SimulationFrameDriver(() => 0);
        driver.SetPlayerPaused(false);
        driver.SetSpeed(SimulationSpeed.Eight);
        var calls = 0;
        var completed = driver.AdvanceFrame(1.0 / 60, () =>
        {
            calls++;
            driver.SetNotificationPaused(true);
            return (1u, false);
        });
        Assert.AreEqual(1UL, completed);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(driver.NotificationPaused);
        Assert.IsFalse(driver.PlayerPaused);
        driver.SetNotificationPaused(false);
        Assert.AreEqual(0UL, driver.AdvanceFrame(SimulationTime.StepSeconds / 16,
            () => throw new AssertFailedException("产生了追赶步骤。")));
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds / 16, () => (1u, false)));
    }

    [TestMethod]
    public void Speed_reduction_excludes_steps_completed_before_the_sample_reset()
    {
        var now = 0.0;
        var driver = new SimulationFrameDriver(() => now);
        driver.SetSpeed(SimulationSpeed.Eight);
        driver.SetPlayerPaused(false);
        var calls = 0;
        Assert.AreEqual(3UL, driver.AdvanceFrame(SimulationTime.StepSeconds, () =>
        {
            now += 0.0001;
            calls++;
            if (calls == 3) { driver.SetSpeed(SimulationSpeed.One); now += 0.000001; return (1u, true); }
            return (1u, false);
        }));
        Assert.AreEqual(0.0, driver.ActualSpeed);
        now += SimulationTime.StepSeconds - 0.000001;
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds, () => (1u, false)));
        Assert.AreEqual(1.0, driver.ActualSpeed, 1e-12);
        driver.SetSpeed(SimulationSpeed.One);
        Assert.AreEqual(1.0, driver.ActualSpeed, 1e-12);
        now += SimulationTime.StepSeconds;
        Assert.AreEqual(1UL, driver.AdvanceFrame(SimulationTime.StepSeconds, () => (1u, false)));
        Assert.AreEqual(1.0, driver.ActualSpeed, 1e-12);
    }
}

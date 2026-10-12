
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

[TestClass]
public sealed class SimulationDriverTests
{
    private const double Budget = 0.005;

    [TestMethod]
    [DataRow(1, 2UL)]
    [DataRow(2, 4UL)]
    [DataRow(4, 8UL)]
    [DataRow(8, 16UL)]
    public void A_frame_uses_target_speed_and_fixed_complete_steps(int speed, ulong expected)
    {
        var clock = new TestClock();
        var driver = Create(clock);
        var original = driver.GetStatus();
        driver.SetSpeed((SimulationSpeed)speed);
        clock.Seconds = 1.0 / 30;
        var result = driver.AdvanceFrame(1.0 / 30, Budget);

        Assert.AreEqual(expected, result.StepsAdvanced);
        Assert.AreEqual(expected, result.StateAfter.CompletedSteps);
        Assert.AreEqual(expected, result.StateAfter.TransactionCount);
        Assert.AreEqual(0UL, result.StateAfter.BoundarySequence);
        Assert.AreEqual(expected / 60.0, driver.GetStatus().SimulationSeconds, 1e-12);
        Assert.AreEqual(0UL, original.State.CompletedSteps);
    }

    [TestMethod]
    public void Budget_keeps_complete_events_and_only_carries_fractional_time()
    {
        var clock = new TestClock();
        var driver = Create(clock, TimedSteps(clock, 0.003));
        clock.Seconds = 4.5 / 60;
        var first = driver.AdvanceFrame(4.5 / 60, Budget);
        Assert.AreEqual(2UL, first.StepsAdvanced);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(0.5 / 60, driver.GetStatus().Remainder, 1e-12);
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(1), new MarkerEvent(2) }, first.Events.ToArray());

        clock.Seconds += 0.5 / 60;
        var second = driver.AdvanceFrame(0.5 / 60, Budget);
        Assert.AreEqual(1UL, second.StepsAdvanced);
        Assert.AreEqual(3UL, second.StateAfter.CompletedSteps);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(0.0, driver.GetStatus().Remainder, 1e-12);
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(3) }, second.Events.ToArray());
    }

    [TestMethod]
    public void An_expensive_complete_step_is_finished_before_the_budget_stops_the_frame()
    {
        var clock = new TestClock();
        var driver = Create(clock, TimedSteps(clock, 0.006));
        driver.SetSpeed(SimulationSpeed.Eight);
        clock.Seconds = 1;
        var result = driver.AdvanceFrame(1, Budget);
        Assert.AreEqual(1UL, result.StepsAdvanced);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(1.0 / 60 / 1.006, driver.GetStatus().ActualSpeed, 1e-12);
        Assert.IsTrue(driver.GetStatus().ActualSpeed < 8);
    }

    [TestMethod]
    public void Changing_from_overloaded_eight_to_one_does_not_replay_old_steps()
    {
        var clock = new TestClock();
        var cost = 0.006;
        var driver = Create(clock, new ScriptedCalculations
        {
            Step = (before, context) =>
            {
                clock.Seconds += cost;
                return ScriptedCalculations.StepResult(before, context);
            }
        });
        driver.SetSpeed(SimulationSpeed.Eight);
        clock.Seconds = 1;
        Assert.AreEqual(1UL, driver.AdvanceFrame(1, Budget).StepsAdvanced);

        cost = 0;
        driver.SetSpeed(SimulationSpeed.One);
        for (var frame = 0; frame < 60; frame++)
        {
            clock.Seconds += 1.0 / 60;
            Assert.AreEqual(1UL, driver.AdvanceFrame(1.0 / 60, Budget).StepsAdvanced);
            Assert.IsTrue(driver.GetStatus().ActualSpeed <= 1 + 1e-12);
        }
        Assert.AreEqual(61UL, driver.GetStatus().State.CompletedSteps);
    }

    [TestMethod]
    public void A_long_frame_does_not_make_later_frames_catch_up()
    {
        var clock = new TestClock();
        var cost = 0.003;
        var driver = Create(clock, new ScriptedCalculations
        {
            Step = (before, context) =>
            {
                clock.Seconds += cost;
                return ScriptedCalculations.StepResult(before, context);
            }
        });
        clock.Seconds = 1;
        Assert.AreEqual(2UL, driver.AdvanceFrame(1, Budget).StepsAdvanced);

        cost = 0;
        for (var frame = 0; frame < 3; frame++)
        {
            clock.Seconds += 1.0 / 60;
            Assert.AreEqual(1UL, driver.AdvanceFrame(1.0 / 60, Budget).StepsAdvanced);
        }
        Assert.AreEqual(5UL, driver.GetStatus().State.CompletedSteps);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(8)]
    public void Actual_speed_measures_completed_work_over_real_time(int speed)
    {
        var clock = new TestClock();
        var driver = Create(clock);
        driver.SetSpeed((SimulationSpeed)speed);
        clock.Seconds = 1;
        driver.AdvanceFrame(1, Budget);
        Assert.AreEqual((double)speed, driver.GetStatus().ActualSpeed, 1e-12);
        driver.SetPaused(true);
        Assert.AreEqual(0.0, driver.GetStatus().ActualSpeed);
        driver.SetPaused(false);
        clock.Seconds = 2;
        driver.AdvanceFrame(1, Budget);
        Assert.AreEqual((double)speed, driver.GetStatus().ActualSpeed, 1e-12);
    }

    [TestMethod]
    public void Pause_and_speed_changes_keep_fractional_time_and_do_not_change_authority()
    {
        var clock = new TestClock();
        var driver = Create(clock);
        clock.Seconds = 0.5 / 60;
        driver.AdvanceFrame(0.5 / 60, Budget);
        var checkpoint = driver.CreateCheckpoint();
        driver.SetPaused(true);
        clock.Seconds += 10;
        Assert.AreEqual(0UL, driver.AdvanceFrame(10, Budget).StepsAdvanced);
        driver.SetSpeed(SimulationSpeed.Two);
        Assert.AreEqual(0.5 / 60, driver.GetStatus().Remainder, 1e-12);
        Assert.AreEqual(checkpoint.CanonicalText, driver.CreateCheckpoint().CanonicalText);
        driver.SetPaused(false);
        clock.Seconds += 0.25 / 60;
        Assert.AreEqual(1UL, driver.AdvanceFrame(0.25 / 60, Budget).StepsAdvanced);
        Assert.AreEqual(0.0, driver.GetStatus().Remainder, 1e-12);
    }

    [TestMethod]
    public void Manual_steps_require_pause_and_leave_fractional_time_and_speed_untouched()
    {
        var clock = new TestClock();
        var driver = Create(clock, TimedSteps(clock, 0.006));
        Assert.ThrowsExactly<InvalidOperationException>(() => driver.Step(1));
        Assert.IsFalse(driver.GetStatus().IsPaused);
        clock.Seconds = 2.5 / 60;
        driver.AdvanceFrame(2.5 / 60, Budget);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(0.5 / 60, driver.GetStatus().Remainder, 1e-12);
        driver.SetPaused(true);
        var result = driver.Step(2);
        Assert.AreEqual(2UL, result.StepsAdvanced);
        Assert.AreEqual(3UL, result.StateAfter.CompletedSteps);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(0.5 / 60, driver.GetStatus().Remainder, 1e-12);
        Assert.AreEqual(0.0, driver.GetStatus().ActualSpeed);
        clock.Seconds += 1;
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, Budget).StepsAdvanced);
        Assert.AreEqual(0.5 / 60, driver.GetStatus().Remainder, 1e-12);
    }

    [TestMethod]
    public void Paused_commands_use_the_same_session_and_keep_rejected_positions()
    {
        var clock = new TestClock();
        var session = new SimulationSession(new ResourceState(), new ResourceCalculations(), new ResourceCommandCodec());
        var driver = new SimulationDriver(session, new ResourceStateCodec(), () => clock.Seconds);
        driver.SetPaused(true);
        driver.SubmitCommand(new CreateEntityCommand(), CommandSource.Console);
        var rejected = (SubmissionResult.Completed)driver.SubmitCommand(new DeleteEntityCommand(9), CommandSource.Console);
        driver.SubmitCommand(new NoChangeCommand(), CommandSource.Automation);

        Assert.AreEqual(CommandOutcome.Rejected, rejected.Execution.Outcome);
        Assert.AreEqual(new SimulationPosition(0, 2), rejected.Execution.Position);
        Assert.AreEqual(3UL, driver.GetStatus().State.BoundarySequence);
        Assert.AreEqual(4UL, driver.GetStatus().State.NextCommandId);
        Assert.AreEqual(3UL, driver.GetStatus().State.TransactionCount);
        Assert.AreEqual(2UL, driver.GetStatus().State.NextEntityId);
        Assert.AreEqual(3, session.GetCommandLog().Entries.Length);
        Assert.AreEqual(new SimulationPosition(0, 3), driver.CreateCheckpoint().Position);
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, Budget).StepsAdvanced);
    }

    [TestMethod]
    public void A_fault_returns_prior_complete_events_and_prevents_more_accumulation()
    {
        var clock = new TestClock();
        var driver = Create(clock, new ScriptedCalculations
        {
            Step = (before, context) => context.Position.CompletedSteps == 3
                ? throw new SimulationCalculationException("test_stop", SimulationPhase.Commit)
                : ScriptedCalculations.StepResult(before, context) with
                { Events = ImmutableArray.Create<DomainEvent>(new MarkerEvent(context.Position.CompletedSteps)) }
        });
        clock.Seconds = 4.0 / 60;
        var result = driver.AdvanceFrame(4.0 / 60, Budget);
        Assert.AreEqual(2UL, result.StepsAdvanced);
        Assert.AreEqual(2UL, result.StateAfter.CompletedSteps);
        Assert.AreEqual("test_stop", result.StateAfter.Fault!.Code);
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(1), new MarkerEvent(2) }, result.Events.ToArray());
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual(result.StateAfter.Fault, result.Diagnostic.Fault);
        clock.Seconds += 10;
        var frozen = driver.AdvanceFrame(10, Budget);
        Assert.AreEqual(0UL, frozen.StepsAdvanced);
        Assert.IsNull(frozen.Diagnostic);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(0.0, driver.GetStatus().ActualSpeed);
        driver.SetPaused(true);
        Assert.AreEqual(0UL, driver.Step(1).StepsAdvanced);
    }

    [TestMethod]
    public void Exhausted_boundaries_do_not_accumulate_unusable_frame_time()
    {
        var state = new ResourceState { CompletedSteps = ulong.MaxValue, TransactionCount = ulong.MaxValue };
        var session = new SimulationSession(state, new ScriptedCalculations(), new ResourceCommandCodec());
        var driver = new SimulationDriver(session, new ResourceStateCodec(), () => 0);
        Assert.AreEqual(0UL, driver.AdvanceFrame(1, Budget).StepsAdvanced);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(state, driver.GetStatus().State);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void Invalid_budget_is_rejected_before_modifying_the_host(double budget)
    {
        var driver = Create(new TestClock());
        var before = driver.GetStatus();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => driver.AdvanceFrame(1, budget));
        Assert.AreEqual(before, driver.GetStatus());
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Invalid_frame_intervals_keep_the_existing_remainder(double delta)
    {
        var driver = Create(new TestClock());
        driver.AdvanceFrame(0.5 / 60, Budget);
        var before = driver.GetStatus();
        Assert.AreEqual(0UL, driver.AdvanceFrame(delta, Budget).StepsAdvanced);
        Assert.AreEqual(before, driver.GetStatus());
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Invalid_frame_intervals_do_not_replay_discarded_steps(double delta)
    {
        var clock = new TestClock();
        var driver = Create(clock, TimedSteps(clock, 0.006));
        driver.AdvanceFrame(2.5 / 60, Budget);
        var before = driver.GetStatus();
        Assert.AreEqual(0UL, before.PendingSteps);

        var result = driver.AdvanceFrame(delta, Budget);

        Assert.AreEqual(0UL, result.StepsAdvanced);
        Assert.AreEqual(before.State, result.StateAfter);
        Assert.AreEqual(0UL, driver.GetStatus().PendingSteps);
        Assert.AreEqual(before.Remainder, driver.GetStatus().Remainder);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void Unsupported_speed_does_not_modify_target_or_authority()
    {
        var driver = Create(new TestClock());
        var before = driver.GetStatus();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => driver.SetSpeed((SimulationSpeed)3));
        Assert.AreEqual(before, driver.GetStatus());
    }

    [TestMethod]
    public void A_driver_cannot_be_modified_or_queried_from_another_thread()
    {
        var driver = Create(new TestClock());
        Exception? mutationError = null;
        Exception? queryError = null;
        var thread = new Thread(() =>
        {
            try { driver.SetPaused(true); } catch (Exception error) { mutationError = error; }
            try { driver.GetStatus(); } catch (Exception error) { queryError = error; }
        });
        thread.Start();
        thread.Join();
        Assert.IsInstanceOfType(mutationError, typeof(InvalidOperationException));
        Assert.IsInstanceOfType(queryError, typeof(InvalidOperationException));
        Assert.IsFalse(driver.GetStatus().IsPaused);
    }

    [TestMethod]
    public void Reentrant_host_mutation_freezes_the_attempt_without_changing_settings()
    {
        SimulationDriver? driver = null;
        driver = Create(new TestClock(), new ScriptedCalculations
        {
            Step = (before, context) =>
            {
                driver!.SetSpeed(SimulationSpeed.Eight);
                return ScriptedCalculations.StepResult(before, context);
            }
        });
        var result = driver.AdvanceFrame(1.0 / 60, Budget);
        Assert.AreEqual(0UL, result.StepsAdvanced);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual(SimulationSpeed.One, driver.GetStatus().Speed);
    }

    private static SimulationDriver Create(TestClock clock, SimulationCalculations? calculations = null) =>
        new(new SimulationSession(new ResourceState(), calculations ?? new ScriptedCalculations(), new ResourceCommandCodec()),
            new ResourceStateCodec(), () => clock.Seconds);

    private static ScriptedCalculations TimedSteps(TestClock clock, double cost) => new()
    {
        Step = (before, context) =>
        {
            clock.Seconds += cost;
            return ScriptedCalculations.StepResult(before, context) with
            { Events = ImmutableArray.Create<DomainEvent>(new MarkerEvent(context.Position.CompletedSteps)) };
        }
    };

    private sealed class TestClock { public double Seconds { get; set; } }
}

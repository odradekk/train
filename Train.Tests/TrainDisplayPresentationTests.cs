using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Simulation;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:railway")]
public sealed class TrainDisplayPresentationTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void Adjacent_completed_states_interpolate_the_reference_then_solve_rigid_poses()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(1).Succeeded);
        var from = harness.Session.QueryDisplayPoses(0);
        var to = harness.Session.QueryDisplayPoses(1);
        var mid = harness.Session.QueryDisplayPoses(0.5);
        AssertDisplayConstraints(harness, from);
        AssertDisplayConstraints(harness, mid);
        AssertDisplayConstraints(harness, to);
        Assert.AreEqual(80.0, from.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(to.Vehicles[0].FrontContact.X, harness.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(
            (from.Vehicles[0].FrontContact.X + to.Vehicles[0].FrontContact.X) * 0.5,
            mid.Vehicles[0].FrontContact.X,
            Tolerance);
        Assert.AreEqual(
            mid.Vehicles[0].FrontContact.X - 9.0,
            mid.Vehicles[1].FrontContact.X,
            Tolerance);
        Assert.IsFalse(from.IntervalEnded);
        Assert.IsFalse(mid.IntervalEnded);
    }

    [TestMethod]
    public void Cross_seam_interpolation_follows_world_path_not_mixed_segment_distances()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(1020).Succeeded);
        var before = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(harness.Session.Rails.Segments[0].Id, before.Reference.SegmentId);
        Assert.IsTrue(before.Vehicles[0].FrontContact.X < StraightRailFixture.SeamX);
        Assert.IsTrue(harness.Session.AdvanceSimulation(1).Succeeded);
        var after = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(harness.Session.Rails.Segments[1].Id, after.Reference.SegmentId);
        Assert.IsTrue(after.Vehicles[0].FrontContact.X > StraightRailFixture.SeamX);

        var mixedDistance = (before.Reference.Distance + after.Reference.Distance) * 0.5;
        foreach (var alpha in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var sample = harness.Session.QueryDisplayPoses(alpha);
            AssertDisplayConstraints(harness, sample);
            var expectedX = before.Vehicles[0].FrontContact.X +
                            alpha * (after.Vehicles[0].FrontContact.X - before.Vehicles[0].FrontContact.X);
            Assert.AreEqual(expectedX, sample.Vehicles[0].FrontContact.X, Tolerance);
            Assert.AreNotEqual(mixedDistance, sample.Assemblies[0].Reference.Distance, 1.0);
            Assert.AreEqual(1.0, sample.Vehicles[0].Transform.UpY, Tolerance);
            Assert.AreEqual(1.0, Math.Abs(sample.Vehicles[0].Transform.RightX), Tolerance);
        }
    }

    [TestMethod]
    public void Coupler_split_and_support_loss_end_the_old_interval_from_committed_state()
    {
        using var splitHarness = RunningAt300();
        var beforeSplit = splitHarness.Session.QueryDisplayPoses(0.5);
        Assert.AreEqual(1, beforeSplit.Assemblies.Count);
        var carriageId = splitHarness.Session.QueryTrainAssemblies().Single().VehicleIds[1];
        Assert.IsTrue(splitHarness.Session.TryFollowVehicle(carriageId));
        var authorityBefore = splitHarness.Session.CaptureTrainAuthority();
        var hashBefore = WorldSaveAuthority.Fingerprint(splitHarness.Session);
        BreakCoupler(splitHarness.Session);
        var afterSplit = splitHarness.Session.QueryDisplayPoses(0.5);
        Assert.IsTrue(afterSplit.IntervalEnded);
        Assert.AreEqual(2, afterSplit.Assemblies.Count);
        Assert.AreEqual(96.0, afterSplit.Vehicles.Single(item => item.VehicleId == afterSplit.Assemblies[0].VehicleIds[0]).FrontContact.X, Tolerance);
        Assert.AreEqual(87.0, afterSplit.Vehicles.Single(item => item.VehicleId == afterSplit.Assemblies[1].VehicleIds[0]).FrontContact.X, Tolerance);
        Assert.AreEqual(carriageId, splitHarness.Session.FollowedVehicleId);
        Assert.AreEqual(authorityBefore.SimulationStep, splitHarness.Session.SimulationStep);
        Assert.AreNotEqual(authorityBefore, splitHarness.Session.CaptureTrainAuthority());
        Assert.AreNotEqual(hashBefore, WorldSaveAuthority.Fingerprint(splitHarness.Session));

        using var support = RunningAt300();
        BreakCoupler(support.Session);
        Assert.IsTrue(support.Session.AdvanceSimulation(30).Succeeded);
        var beforeLoss = support.Session.QueryDisplayPoses(0.4);
        Assert.IsFalse(beforeLoss.IntervalEnded);
        Assert.AreEqual(WorldCommandStatus.Removed, support.Session.Damage(StraightRailFixture.SupportLossCell, 100).Status);
        var afterLoss = support.Session.QueryDisplayPoses(0.4);
        Assert.IsTrue(afterLoss.IntervalEnded);
        var carriage = support.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is null);
        Assert.AreEqual(88.875, afterLoss.Vehicles.Single(item => item.VehicleId == carriage.VehicleIds[0]).FrontContact.X, Tolerance);
        Assert.AreEqual(carriage.Vehicles[0].FrontContact.X, afterLoss.Vehicles.Single(item => item.VehicleId == carriage.VehicleIds[0]).FrontContact.X, Tolerance);

        var loco = support.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is not null);
        var locoBefore = loco.Vehicles[0].FrontContact.X;
        Assert.IsTrue(loco.Running);
        Assert.IsTrue(support.Session.AdvanceSimulation(1).Succeeded);
        var locoAfter = support.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is not null);
        var rebuilt = support.Session.QueryDisplayPoses(0.5);
        Assert.IsFalse(rebuilt.IntervalEnded);
        Assert.AreEqual(
            (locoBefore + locoAfter.Vehicles[0].FrontContact.X) * 0.5,
            rebuilt.Vehicles.Single(item => item.VehicleId == locoAfter.VehicleIds[0]).FrontContact.X,
            Tolerance);
        Assert.AreEqual(
            carriage.Vehicles[0].FrontContact.X,
            rebuilt.Vehicles.Single(item => item.VehicleId == carriage.VehicleIds[0]).FrontContact.X,
            Tolerance);
    }

    [TestMethod]
    public void Forward_gap_final_step_interpolates_and_still_notifies_the_graph()
    {
        using var harness = RunningAt300();
        var graph = harness.Session.AssemblyGraphGeneration;
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            harness.Session.Damage(StraightRailFixture.BetweenContactGapCell, 100).Status);
        AssertLastLimitStepInterpolates(harness, TrainHaltReason.ForwardGap);
        Assert.IsTrue(harness.Session.AssemblyGraphGeneration > graph);
    }

    [TestMethod]
    public void End_of_line_final_step_interpolates_without_snapping_the_interval()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(2700).Succeeded);
        Assert.AreEqual(TrainHaltReason.None, harness.Session.QueryTrainAssemblies().Single().HaltReason);
        AssertLastLimitStepInterpolates(harness, TrainHaltReason.EndOfLine);
    }

    [TestMethod]
    public void Display_sampling_does_not_write_back_or_enter_the_authority_hash()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        var before = harness.Session.CaptureTrainAuthority();
        var hash = WorldSaveAuthority.Fingerprint(harness.Session);
        var followed = harness.Session.FollowedVehicleId;
        foreach (var alpha in new[] { 0.0, 0.3, 0.7, 1.0 })
            _ = harness.Session.QueryDisplayPoses(alpha);
        _ = harness.Session.QueryFollowedCamera(0.5);
        Assert.IsTrue(harness.Session.TryFollowVehicle(harness.Session.QueryTrainAssemblies().Single().VehicleIds[1]));
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(hash, WorldSaveAuthority.Fingerprint(harness.Session));
        Assert.AreNotEqual(followed, harness.Session.FollowedVehicleId);
    }

    [TestMethod]
    public void Host_schedules_and_pause_keep_authority_while_alpha_tracks_remainder()
    {
        using var first = Parked();
        using var fps30 = Parked();
        using var fps144 = Parked();
        using var paused = Parked();
        RunFixture300(first.Session);
        var expected = first.Session.CaptureTrainAuthority();
        DriveDisplay(fps30.Session, 1.0 / 30.0, 300);
        DriveDisplay(fps144.Session, 1.0 / 144.0, 300);
        DriveDisplay(paused.Session, 1.0 / 60.0, 300, pauseAfterFrames: 20, pauseFrames: 8);
        Assert.AreEqual(expected, fps30.Session.CaptureTrainAuthority());
        Assert.AreEqual(expected, fps144.Session.CaptureTrainAuthority());
        Assert.AreEqual(expected, paused.Session.CaptureTrainAuthority());

        var scheduler = new SimulationScheduler();
        Assert.AreEqual(0.0, scheduler.DisplayAlpha, Tolerance);
        Assert.AreEqual(0UL, scheduler.CollectSteps(SimulationClock.StepDuration * 0.4, paused: false));
        Assert.AreEqual(0.4, scheduler.DisplayAlpha, 1e-9);
        Assert.AreEqual(1UL, scheduler.CollectSteps(SimulationClock.StepDuration * 0.7, paused: false));
        Assert.IsTrue(scheduler.DisplayAlpha is >= 0 and < 1);
        Assert.AreEqual(0UL, scheduler.CollectSteps(1.0, paused: true));
    }

    [TestMethod]
    public void Followed_carriage_framing_stays_continuous_when_it_becomes_assembly_lead()
    {
        using var harness = RunningAt300();
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        var carriageId = assembly.VehicleIds[1];
        Assert.AreNotEqual(carriageId, assembly.VehicleIds[0]);
        Assert.IsTrue(harness.Session.TryFollowVehicle(carriageId));
        var definition = harness.Session.QueryComposite(carriageId)!.DefinitionId;
        Assert.AreEqual(StaticTrainFixture.CarriageId, definition);
        var beforeFollow = harness.Session.QueryFollowedCamera(1)!;
        var before = AlongRailDisplay.FrameFollowCamera(beforeFollow, definition);
        Assert.AreEqual(4.0, before.LookOffset, Tolerance);
        Assert.AreEqual(beforeFollow.FocusX + 4.0, before.LookX, Tolerance);

        BreakCoupler(harness.Session);
        Assert.AreEqual(300UL, harness.Session.SimulationStep);
        Assert.AreEqual(carriageId, harness.Session.FollowedVehicleId);
        var split = harness.Session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(carriageId));
        Assert.AreEqual(carriageId, split.VehicleIds[0]);
        Assert.AreEqual(4.0, split.Speed, Tolerance);
        Assert.IsFalse(split.Running);

        var afterFollow = harness.Session.QueryFollowedCamera(1)!;
        var after = AlongRailDisplay.FrameFollowCamera(afterFollow, harness.Session.QueryComposite(carriageId)!.DefinitionId);
        Assert.AreEqual(carriageId, afterFollow.VehicleId);
        Assert.AreEqual(4.0, after.LookOffset, Tolerance);
        Assert.AreEqual(before.LookX, after.LookX, Tolerance);
        Assert.AreEqual(before.LookY, after.LookY, Tolerance);
        Assert.AreEqual(before.LookZ, after.LookZ, Tolerance);
        Assert.AreEqual(before.PositionX, after.PositionX, Tolerance);
        Assert.AreEqual(before.PositionY, after.PositionY, Tolerance);
        Assert.AreEqual(before.PositionZ, after.PositionZ, Tolerance);
        Assert.AreEqual(-5.0, AlongRailDisplay.LookOffsetAlongHeading(StaticTrainFixture.LocomotiveId), Tolerance);
        Assert.AreNotEqual(before.LookX - 9.0, after.LookX, 1.0);
    }

    [TestMethod]
    public void Motion_status_text_distinguishes_running_coast_and_stop()
    {
        using var harness = RunningAt300();
        var running = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual("运行", AlongRailDisplay.MotionStatusText(running, paused: false));
        Assert.AreEqual("运行 暂停", AlongRailDisplay.MotionStatusText(running, paused: true));

        var carriageId = running.VehicleIds[1];
        BreakCoupler(harness.Session);
        var coasting = harness.Session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(carriageId));
        Assert.IsFalse(coasting.Running);
        Assert.AreEqual(4.0, coasting.Speed, Tolerance);
        Assert.AreEqual(TrainHaltReason.None, coasting.HaltReason);
        Assert.AreEqual("滑行", AlongRailDisplay.MotionStatusText(coasting, paused: false));
        Assert.AreEqual("滑行 暂停", AlongRailDisplay.MotionStatusText(coasting, paused: true));

        Assert.IsTrue(harness.Session.AdvanceSimulation(240).Succeeded);
        var stopped = harness.Session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(carriageId));
        Assert.IsFalse(stopped.Running);
        Assert.AreEqual(0.0, stopped.Speed, Tolerance);
        Assert.AreEqual("停止", AlongRailDisplay.MotionStatusText(stopped, paused: false));
        Assert.AreEqual("支撑失效", AlongRailDisplay.MotionStatusText(
            new TrainAssemblyQuery(
                stopped.Id,
                stopped.ActiveLocomotiveId,
                stopped.Reference,
                stopped.VehicleIds,
                stopped.Connections,
                stopped.Vehicles,
                0,
                false,
                TrainHaltReason.SupportLost),
            paused: false));
        Assert.AreEqual("前方缺口", AlongRailDisplay.MotionStatusText(
            stopped with { HaltReason = TrainHaltReason.ForwardGap },
            paused: false));
        Assert.AreEqual("线路末端", AlongRailDisplay.MotionStatusText(
            stopped with { HaltReason = TrainHaltReason.EndOfLine },
            paused: false));
    }

    [TestMethod]
    public void Followed_camera_uses_display_body_heading_and_survives_disconnect()
    {
        using var harness = Parked();
        var loco = harness.Session.QueryTrainAssemblies().Single().VehicleIds[0];
        var carriage = harness.Session.QueryTrainAssemblies().Single().VehicleIds[1];
        Assert.AreEqual(loco, harness.Session.FollowedVehicleId);
        Assert.IsTrue(harness.Session.TryFollowVehicle(carriage));
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(1).Succeeded);
        var moving = harness.Session.QueryFollowedCamera(0.5);
        Assert.IsNotNull(moving);
        Assert.AreEqual(carriage, moving!.VehicleId);
        Assert.AreEqual(1.0, moving.HeadingX, Tolerance);
        Assert.AreEqual(0.0, moving.HeadingY, Tolerance);
        Assert.AreEqual(0.0, moving.HeadingZ, Tolerance);
        Assert.AreEqual(moving.FocusX, harness.Session.QueryDisplayPoses(0.5).Vehicles.Single(item => item.VehicleId == carriage).Transform.OriginX, Tolerance);

        Assert.IsTrue(harness.Session.AdvanceSimulation(299).Succeeded);
        BreakCoupler(harness.Session);
        Assert.AreEqual(carriage, harness.Session.FollowedVehicleId);
        var after = harness.Session.QueryFollowedCamera(0.5);
        Assert.AreEqual(carriage, after!.VehicleId);
        Assert.AreEqual(1.0, after.HeadingX, Tolerance);
        Assert.IsFalse(harness.Session.TryFollowVehicle(new EntityId(ulong.MaxValue)));
        Assert.AreEqual(carriage, harness.Session.FollowedVehicleId);
    }

    [TestMethod]
    public void Save_load_rebuilds_display_from_committed_state_without_extrapolation()
    {
        using var live = Parked();
        Assert.IsTrue(live.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(live.Session.AdvanceSimulation(900).Succeeded);
        var saved = live.Session.Save("Display 900");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var liveHash = WorldSaveAuthority.Fingerprint(live.Session);
        using var reader = StaticTrainHarness.Create(live.UserData, live.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(liveHash, WorldSaveAuthority.Fingerprint(reader.Session));
        var restored = reader.Session.QueryDisplayPoses(0.5);
        Assert.IsTrue(restored.IntervalEnded);
        Assert.AreEqual(136.0, restored.Vehicles[0].FrontContact.X, Tolerance);
        _ = reader.Session.QueryDisplayPoses(1.5);
        Assert.AreEqual(136.0, reader.Session.QueryDisplayPoses(1.5).Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(live.Session.CaptureTrainAuthority(), reader.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void Console_display_and_follow_reuse_the_session_entry()
    {
        using var harness = Parked();
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session),
            new CompositeConsoleCommands(harness.Session),
            new RailConsoleCommands(harness.Session));
        dispatcher.Execute("rail start");
        dispatcher.Execute("rail step 1");
        dispatcher.Execute("rail query display");
        var carriage = harness.Session.QueryTrainAssemblies().Single().VehicleIds[1];
        dispatcher.Execute($"rail follow vehicle {carriage.Value.ToString(CultureInfo.InvariantCulture)}");
        dispatcher.Execute("rail follow");
        var log = string.Join('\n', harness.Memory.Snapshot().Where(item => item.EventId == ConsoleCommandDispatcher.RailEventId).Select(item => item.Message));
        StringAssert.Contains(log, "Rail display");
        StringAssert.Contains(log, "alpha");
        StringAssert.Contains(log, "follow");
        StringAssert.Contains(log, carriage.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AssertLastLimitStepInterpolates(StaticTrainHarness harness, TrainHaltReason expected)
    {
        for (var i = 0; i < 400; i++)
        {
            var before = harness.Session.QueryTrainAssemblies().Single();
            Assert.IsTrue(harness.Session.AdvanceSimulation(1).Succeeded);
            var after = harness.Session.QueryTrainAssemblies().Single();
            if (after.HaltReason != expected)
                continue;
            AssertInterpolatedFinalStep(harness, before.Vehicles[0].FrontContact.X, after.Vehicles[0].FrontContact.X);
            return;
        }

        Assert.Fail($"Did not reach {expected} within 400 steps.");
    }

    private static void AssertInterpolatedFinalStep(StaticTrainHarness harness, double fromX, double toX)
    {
        var zero = harness.Session.QueryDisplayPoses(0);
        var mid = harness.Session.QueryDisplayPoses(0.5);
        var one = harness.Session.QueryDisplayPoses(1);
        Assert.IsFalse(zero.IntervalEnded);
        Assert.IsFalse(mid.IntervalEnded);
        Assert.IsFalse(one.IntervalEnded);
        Assert.AreEqual(fromX, zero.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual((fromX + toX) * 0.5, mid.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(toX, one.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreNotEqual(toX, fromX, 1e-6);
        _ = harness;
    }

    private static void AssertDisplayConstraints(StaticTrainHarness harness, TrainDisplaySample sample)
    {
        Assert.AreEqual(2, sample.Vehicles.Count);
        Assert.AreEqual(6.0, Math.Abs(sample.Vehicles[0].FrontContact.X - sample.Vehicles[0].RearContact.X), Tolerance);
        Assert.AreEqual(6.0, Math.Abs(sample.Vehicles[1].FrontContact.X - sample.Vehicles[1].RearContact.X), Tolerance);
        Assert.AreEqual(1.0, sample.Vehicles[0].Transform.OriginX - 8 - sample.Vehicles[1].Transform.OriginX, Tolerance);
        Assert.IsFalse(StraightTrainSolver.BodiesOverlap(sample.Vehicles[0].Transform, sample.Vehicles[1].Transform));
        Assert.IsTrue(sample.Vehicles[0].Transform.IsValid);
        Assert.IsTrue(sample.Vehicles[1].Transform.IsValid);
        Assert.AreEqual(2.0, sample.Vehicles[0].FrontContact.Y, Tolerance);
        Assert.AreEqual(16.0, sample.Vehicles[0].FrontContact.Z, Tolerance);
        _ = harness;
    }

    private static StaticTrainHarness Parked()
    {
        var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        return harness;
    }

    private static StaticTrainHarness RunningAt300()
    {
        var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        return harness;
    }

    private static void BreakCoupler(VoxelDevelopmentSession session)
    {
        var assembly = session.QueryTrainAssemblies().Single();
        Assert.IsTrue(session.Damage(assembly.ActiveLocomotiveId!.Value, assembly.Connections[0].FromCell, 100).Succeeded);
    }

    private static void RunFixture300(VoxelDevelopmentSession session)
    {
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
    }

    private static void DriveDisplay(
        VoxelDevelopmentSession session,
        double delta,
        ulong targetStep,
        int pauseAfterFrames = -1,
        int pauseFrames = 0)
    {
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        var frames = 0;
        while (session.SimulationStep < targetStep)
        {
            if (pauseAfterFrames >= 0 && frames == pauseAfterFrames)
                Assert.IsTrue(session.PauseSimulation().Succeeded);
            if (pauseAfterFrames >= 0 && frames == pauseAfterFrames + pauseFrames)
                Assert.IsTrue(session.ResumeSimulation().Succeeded);
            var steps = session.CollectHostSteps(delta);
            if (steps > 0)
                Assert.IsTrue(session.AdvanceSimulation(steps).Succeeded);
            _ = session.QueryDisplayPoses();
            frames++;
            if (frames > 20_000)
                Assert.Fail($"Display drive did not reach step {targetStep}.");
        }
    }
}

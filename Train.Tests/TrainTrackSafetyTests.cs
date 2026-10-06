using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:railway")]
public sealed class TrainTrackSafetyTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void Fixture_300_then_first_segment_gap_stops_every_contact_at_the_incoming_boundary()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        AssertMotion(harness.Session.QueryTrainAssemblies().Single(), 96, 87, 4, running: true);
        Assert.AreEqual(TrainHaltReason.None, harness.Session.QueryTrainAssemblies().Single().HaltReason);

        Assert.AreEqual(
            WorldCommandStatus.Removed,
            harness.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        AssertGap(harness, harness.Session.Rails.Segments[0].Id, 110, 111);
        Assert.IsFalse(harness.Session.QueryDerivedRailGraph().Current);
        harness.Session.RebuildDerivedRailGraph();
        Assert.IsFalse(harness.Session.QueryDerivedRailGraph().TreatsIntervalAsTraversable(
            harness.Session.Rails.Segments[0].Id,
            new RailInterval(110.4, 110.6)));

        using var stale = Parked();
        Assert.IsTrue(stale.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(stale.Session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            stale.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        Assert.IsFalse(stale.Session.QueryDerivedRailGraph().Current);
        Assert.IsTrue(stale.Session.AdvanceSimulation(2000).Succeeded);
        AssertStoppedAtIncomingBoundary(stale, 110, 111, TrainHaltReason.ForwardGap);

        Assert.IsTrue(harness.Session.AdvanceSimulation(2000).Succeeded);
        AssertStoppedAtIncomingBoundary(harness, 110, 111, TrainHaltReason.ForwardGap);
        Assert.AreEqual(
            stale.Session.QueryTrainAssemblies().Single(),
            harness.Session.QueryTrainAssemblies().Single());

        Assert.IsTrue(harness.Session.AdvanceSimulation(1).Succeeded);
        var still = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(110.0, still.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, still.Speed, Tolerance);
        Assert.IsFalse(ContactsEnter(still, 110, 111));
        Assert.IsFalse(
            StraightTrainSolver.TryContainingInterval(
                new[] { new RailInterval(111, 144) },
                111 - Tolerance,
                out _));
    }

    [TestMethod]
    public void High_speed_restore_stops_inside_one_step_and_cannot_use_the_far_interval()
    {
        using var catalog = HighSpeed();
        var saveId = TrainPersistenceFixtures.WriteHighSpeed(
            catalog.Session,
            catalog.Store,
            StaticTrainFixture.LocomotiveFrontContactX);
        using var loaded = StaticTrainHarness.Create(catalog.UserData, catalog.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Session.Load(saveId).Status);
        var restored = loaded.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(80.0, restored.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(600.0, restored.Speed, Tolerance);
        Assert.AreEqual(600.0, CurrentTarget(loaded), Tolerance);
        Assert.IsTrue(restored.Running);

        Assert.AreEqual(
            WorldCommandStatus.Removed,
            loaded.Session.Damage(StraightRailFixture.HighSpeedGapCell, 100).Status);
        Assert.IsTrue(loaded.Session.AdvanceSimulation(1).Succeeded);
        AssertStoppedAtIncomingBoundary(loaded, 84, 85, TrainHaltReason.ForwardGap);
        Assert.IsTrue(loaded.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X <= 84 + Tolerance);
        Assert.IsTrue(loaded.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X < 90 - Tolerance);
    }

    [TestMethod]
    public void Cross_segment_high_speed_restore_uses_the_explicit_connection_then_stops()
    {
        using var catalog = HighSpeed();
        var saveId = TrainPersistenceFixtures.WriteHighSpeed(catalog.Session, catalog.Store, 140);
        using var loaded = StaticTrainHarness.Create(catalog.UserData, catalog.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Session.Load(saveId).Status);
        var restored = loaded.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(140.0, restored.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(600.0, restored.Speed, Tolerance);
        Assert.AreEqual(600.0, CurrentTarget(loaded), Tolerance);

        Assert.AreEqual(
            WorldCommandStatus.Removed,
            loaded.Session.Damage(StraightRailFixture.CrossSegmentGapCell, 100).Status);
        Assert.IsTrue(loaded.Session.AdvanceSimulation(1).Succeeded);
        var stopped = AssertStoppedAtIncomingBoundary(loaded, 147, 148, TrainHaltReason.ForwardGap);
        Assert.IsTrue(stopped.Vehicles[0].FrontContact.X > 144);
        Assert.IsTrue(stopped.Vehicles[0].FrontContact.X <= 147 + Tolerance);
        Assert.IsTrue(stopped.Vehicles[0].FrontContact.X < 150 - Tolerance);
        Assert.AreEqual(loaded.Session.Rails.Segments[1].Id, stopped.Reference.SegmentId);
    }

    [TestMethod]
    public void Gap_between_contacts_limits_travel_and_round_trips_without_a_support_loss()
    {
        using var harness = RunningAt300();
        var before = harness.Session.QueryTrainAssemblies().Single();
        AssertMotion(before, 96, 87, 4, running: true);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            harness.Session.Damage(StraightRailFixture.BetweenContactGapCell, 100).Status);
        var afterDamage = harness.Session.QueryTrainAssemblies().Single();
        AssertMotion(afterDamage, 96, 87, 4, running: true);
        Assert.AreEqual(TrainHaltReason.None, afterDamage.HaltReason);
        Assert.IsFalse(ContactsEnter(afterDamage, 93, 94));
        AssertRoundTripUninterrupted(harness, "Between Contact Gap");

        using var live = RunningAt300();
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            live.Session.Damage(StraightRailFixture.BetweenContactGapCell, 100).Status);
        Assert.IsTrue(live.Session.AdvanceSimulation(200).Succeeded);
        var stopped = live.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(0.0, stopped.Speed, Tolerance);
        Assert.IsFalse(stopped.Running);
        Assert.AreEqual(TrainHaltReason.ForwardGap, stopped.HaltReason);
        Assert.AreEqual(93.0, stopped.Vehicles[0].RearContact.X, Tolerance);
        Assert.IsTrue(stopped.Vehicles[0].FrontContact.X > 94);
        Assert.IsFalse(ContactsEnter(stopped, 93, 94));
        Assert.AreEqual(TrainMotionStatus.DisconnectedPath, live.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.AreEqual(TrainHaltReason.ForwardGap, live.Session.QueryTrainAssemblies().Single().HaltReason);
    }

    [TestMethod]
    public void Exact_gap_boundary_step_zeros_speed_and_restores_the_halt()
    {
        using var catalog = HighSpeed();
        var saveId = TrainPersistenceFixtures.WriteHighSpeed(catalog.Session, catalog.Store, 80);
        using var loaded = StaticTrainHarness.Create(catalog.UserData, catalog.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Session.Load(saveId).Status);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            loaded.Session.Damage(StraightRailFixture.ExactBoundaryGapCell, 100).Status);
        Assert.IsTrue(loaded.Session.AdvanceSimulation(1).Succeeded);
        var stopped = AssertStoppedAtIncomingBoundary(loaded, 90, 91, TrainHaltReason.ForwardGap);
        Assert.AreEqual(90.0, stopped.Vehicles[0].FrontContact.X, Tolerance);
        AssertRoundTripUninterrupted(loaded, "Exact Boundary Halt");
        Assert.AreEqual(TrainMotionStatus.DisconnectedPath, loaded.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.AreEqual(0.0, loaded.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.AreEqual(TrainHaltReason.ForwardGap, loaded.Session.QueryTrainAssemblies().Single().HaltReason);
    }

    [TestMethod]
    public void Exact_end_of_line_step_zeros_speed_and_restores_the_halt()
    {
        using var catalog = HighSpeed();
        var saveId = TrainPersistenceFixtures.WriteHighSpeed(catalog.Session, catalog.Store, 262);
        using var loaded = StaticTrainHarness.Create(catalog.UserData, catalog.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Session.Load(saveId).Status);
        Assert.IsTrue(loaded.Session.AdvanceSimulation(1).Succeeded);
        var stopped = AssertStoppedAtIncomingBoundary(loaded, 272, 273, TrainHaltReason.EndOfLine);
        Assert.AreEqual(272.0, stopped.Vehicles[0].FrontContact.X, Tolerance);
        AssertRoundTripUninterrupted(loaded, "Exact End Halt");
    }

    [TestMethod]
    public void Unrepaired_forward_gap_refuses_start_and_keeps_the_halt()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            harness.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        Assert.IsTrue(harness.Session.AdvanceSimulation(2000).Succeeded);
        AssertStoppedAtIncomingBoundary(harness, 110, 111, TrainHaltReason.ForwardGap);

        Assert.AreEqual(TrainMotionStatus.DisconnectedPath, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);
        var blocked = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(0.0, blocked.Speed, Tolerance);
        Assert.IsFalse(blocked.Running);
        Assert.AreEqual(TrainHaltReason.ForwardGap, blocked.HaltReason);
        AssertRoundTripUninterrupted(harness, "Unrepaired Gap Halt");
        Assert.AreEqual(TrainMotionStatus.DisconnectedPath, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.AreEqual(TrainHaltReason.ForwardGap, harness.Session.QueryTrainAssemblies().Single().HaltReason);

        Assert.IsTrue(harness.Session.RepairRails(new[] { StraightRailFixture.FirstSegmentDamageCell }).Succeeded);
        Assert.AreEqual(TrainHaltReason.None, harness.Session.QueryTrainAssemblies().Single().HaltReason);
        Assert.AreEqual(TrainMotionStatus.Started, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);
    }

    [TestMethod]
    public void Support_loss_freezes_only_the_affected_split_assembly()
    {
        using var harness = RunningAt300();
        BreakCoupler(harness.Session);
        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        var before = AssertSplit(harness, 98, 88.875, 4, 3.5);
        var beforeGraph = harness.Session.AssemblyGraphGeneration;
        var rearPose = before.Carriage.Vehicles[0];

        var notifications = 0;
        TrainAuthoritySnapshot? observed = null;
        void Observe()
        {
            notifications++;
            observed = harness.Session.CaptureTrainAuthority();
        }

        harness.Session.Changed += Observe;
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            harness.Session.Damage(StraightRailFixture.SupportLossCell, 100).Status);
        harness.Session.Changed -= Observe;
        Assert.AreEqual(1, notifications);
        Assert.IsNotNull(observed);

        var after = AssertSplit(harness, 98, 88.875, 4, 0);
        Assert.AreEqual(observed, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(before.Loco.Id, after.Loco.Id);
        Assert.AreEqual(before.Carriage.Id, after.Carriage.Id);
        Assert.AreEqual(before.Carriage.Reference, after.Carriage.Reference);
        Assert.AreEqual(rearPose.Transform, after.Carriage.Vehicles[0].Transform);
        Assert.AreEqual(rearPose.FrontContact, after.Carriage.Vehicles[0].FrontContact);
        Assert.AreEqual(rearPose.RearContact, after.Carriage.Vehicles[0].RearContact);
        Assert.IsFalse(after.Carriage.Running);
        Assert.AreEqual(TrainHaltReason.SupportLost, after.Carriage.HaltReason);
        Assert.AreEqual(TrainHaltReason.None, after.Loco.HaltReason);
        Assert.IsTrue(after.Loco.Running);
        Assert.IsTrue(harness.Session.AssemblyGraphGeneration > beforeGraph);
        Assert.IsTrue(harness.Session.BuildTrackRenderModel().HasGapNear(StraightRailFixture.SupportLossCell));

        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        var continued = harness.Session.QueryTrainAssemblies();
        var loco = continued.Single(item => item.Id == after.Loco.Id);
        var carriage = continued.Single(item => item.Id == after.Carriage.Id);
        Assert.AreEqual(100.0, loco.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(4.0, loco.Speed, Tolerance);
        Assert.AreEqual(88.875, carriage.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, carriage.Speed, Tolerance);
        Assert.AreEqual(TrainHaltReason.SupportLost, carriage.HaltReason);
    }

    [TestMethod]
    public void Repair_clears_halt_but_requires_an_explicit_powered_start_from_zero()
    {
        using var gap = Parked();
        Assert.IsTrue(gap.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(gap.Session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            gap.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        Assert.IsTrue(gap.Session.AdvanceSimulation(2000).Succeeded);
        var stopped = gap.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(0.0, stopped.Speed, Tolerance);
        Assert.AreEqual(TrainHaltReason.ForwardGap, stopped.HaltReason);

        Assert.IsTrue(gap.Session.RepairRails(new[] { StraightRailFixture.FirstSegmentDamageCell }).Succeeded);
        var repaired = gap.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(0.0, repaired.Speed, Tolerance);
        Assert.IsFalse(repaired.Running);
        Assert.AreEqual(TrainHaltReason.None, repaired.HaltReason);
        Assert.AreEqual(110.0, repaired.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(TrainMotionStatus.Started, gap.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.AreEqual(0.0, gap.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.IsTrue(gap.Session.AdvanceSimulation(1).Succeeded);
        var moving = gap.Session.QueryTrainAssemblies().Single();
        Assert.IsTrue(moving.Running);
        Assert.IsTrue(moving.Speed > 0);
        Assert.IsTrue(moving.Vehicles[0].FrontContact.X > 110 + Tolerance);

        using var split = RunningAt300();
        BreakCoupler(split.Session);
        Assert.IsTrue(split.Session.AdvanceSimulation(30).Succeeded);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            split.Session.Damage(StraightRailFixture.SupportLossCell, 100).Status);
        Assert.IsTrue(split.Session.RepairRails(new[] { StraightRailFixture.SupportLossCell }).Succeeded);
        var rear = split.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is null);
        Assert.AreEqual(0.0, rear.Speed, Tolerance);
        Assert.AreEqual(TrainMotionStatus.MissingPropulsion, split.Session.StartTrain(RailHeading.AlongAlignment, assemblyId: rear.Id).Status);
        Assert.AreEqual(0.0, split.Session.QueryTrainAssemblies().Single(item => item.Id == rear.Id).Speed, Tolerance);
    }

    [TestMethod]
    public void Full_destroy_keeps_the_referenced_identity_and_does_not_hand_off_a_new_segment()
    {
        using var harness = Parked();
        var first = harness.Session.Rails.Segments[0];
        var second = harness.Session.Rails.Segments[1];
        var cells = FirstSegmentOccupiedCells(harness.Session, first.Id);
        Assert.IsTrue(cells.Contains(StraightRailFixture.SharedSeamCell));
        var beforeReference = harness.Session.QueryTrainAssemblies().Single().Reference;
        var beforeAlignment = first.Alignment;

        Assert.IsTrue(harness.Session.Damage(cells.Select(cell => new VoxelDamageTarget(cell, 100)).ToArray()).Succeeded);
        var parked = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(beforeReference.SegmentId, parked.Reference.SegmentId);
        Assert.AreEqual(beforeReference.Distance, parked.Reference.Distance, Tolerance);
        Assert.AreEqual(beforeAlignment, harness.Session.Rails.Segments.Single(item => item.Id == first.Id).Alignment);
        Assert.AreEqual(0, harness.Session.Rails.TraversableWorldX(first.Id, harness.Session.World).Count);
        Assert.IsTrue(harness.Session.Rails.TraversableWorldX(second.Id, harness.Session.World).Count > 0);
        Assert.IsTrue(
            harness.Session.Rails.TraversableWorldX(second.Id, harness.Session.World)
                .Any(interval => interval.Start <= 200 && 200 <= interval.End));
        Assert.AreEqual(80.0, parked.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, parked.Speed, Tolerance);
        Assert.AreEqual(TrainHaltReason.SupportLost, parked.HaltReason);
        Assert.AreEqual(TrainMotionStatus.InsufficientSupport, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);

        var samePlace = harness.Session.ConstructRails(StraightRailFixture.CreateFixturePlan());
        Assert.AreEqual(RailEditStatus.IndependentLineConflict, samePlace.Status);
        Assert.AreEqual(first.Id, harness.Session.QueryTrainAssemblies().Single().Reference.SegmentId);

        Assert.IsTrue(harness.Session.ConstructRails(ShiftZ(StraightRailFixture.CreateFixturePlan(), 8)).Succeeded);
        var afterNew = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(first.Id, afterNew.Reference.SegmentId);
        Assert.AreEqual(TrainHaltReason.SupportLost, afterNew.HaltReason);
        Assert.AreEqual(TrainMotionStatus.InsufficientSupport, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);

        Assert.IsTrue(harness.Session.RepairRails(cells).Succeeded);
        var restored = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(first.Id, restored.Reference.SegmentId);
        Assert.AreEqual(TrainHaltReason.None, restored.HaltReason);
        Assert.AreEqual(TrainMotionStatus.Started, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.AreEqual(first.Id, harness.Session.QueryTrainAssemblies().Single().Reference.SegmentId);
    }

    [TestMethod]
    public void Formal_files_restore_halts_and_reject_missing_geometry_without_replacing_the_session()
    {
        using var gap = Parked();
        Assert.IsTrue(gap.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(gap.Session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            gap.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        Assert.IsTrue(gap.Session.AdvanceSimulation(2000).Succeeded);
        AssertRoundTripUninterrupted(gap, "Gap Halt");

        using var support = RunningAt300();
        BreakCoupler(support.Session);
        Assert.IsTrue(support.Session.AdvanceSimulation(30).Succeeded);
        Assert.AreEqual(
            WorldCommandStatus.Removed,
            support.Session.Damage(StraightRailFixture.SupportLossCell, 100).Status);
        AssertRoundTripUninterrupted(support, "Support Loss Halt");

        using var destroyed = Parked();
        var firstId = destroyed.Session.Rails.Segments[0].Id;
        var cells = FirstSegmentOccupiedCells(destroyed.Session, firstId);
        Assert.IsTrue(destroyed.Session.Damage(cells.Select(cell => new VoxelDamageTarget(cell, 100)).ToArray()).Succeeded);
        AssertRoundTripUninterrupted(destroyed, "Full Destroy Halt");
        using var destroyedReader = StaticTrainHarness.Create(destroyed.UserData, destroyed.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, destroyedReader.Session.Load(destroyed.Session.SaveId!.Value).Status);
        var loadedDestroyed = destroyedReader.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(firstId, loadedDestroyed.Reference.SegmentId);
        Assert.AreEqual(0, destroyedReader.Session.Rails.TraversableWorldX(firstId, destroyedReader.Session.World).Count);
        Assert.AreEqual(TrainHaltReason.SupportLost, loadedDestroyed.HaltReason);
        Assert.AreEqual(TrainMotionStatus.InsufficientSupport, destroyedReader.Session.StartTrain(RailHeading.AlongAlignment).Status);

        Assert.AreEqual(WorldSaveStatus.Saved, destroyed.Session.Save("Full Destroy Current").Status);
        var path = destroyed.Store.PathFor(destroyed.Session.SaveId!.Value);
        var previous = File.ReadAllText(path);
        var backup = path + ".valid";
        File.WriteAllText(backup, previous);
        var before = destroyed.Session.CaptureTrainAuthority();
        var referenced = destroyed.Session.QueryTrainAssemblies().Single().Reference.SegmentId.Value;
        var missingGeometry = Mutate(previous, root => RemoveReferencedSegment(root, referenced));
        File.WriteAllText(path, missingGeometry);
        Assert.AreEqual(WorldSaveStatus.InvalidDelta, destroyed.Session.Load(destroyed.Session.SaveId.Value).Status);
        Assert.AreEqual(before, destroyed.Session.CaptureTrainAuthority());
        Assert.AreEqual(missingGeometry, File.ReadAllText(path));
        Assert.IsTrue(File.Exists(backup));
        Assert.AreEqual(previous, File.ReadAllText(backup));
        File.WriteAllText(path, previous);
        Assert.AreEqual(WorldSaveStatus.Loaded, destroyed.Session.Load(destroyed.Session.SaveId.Value).Status);
        Assert.AreEqual(before, destroyed.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void Console_reproduces_gap_support_loss_repair_and_start_cases()
    {
        using var harness = StaticTrainHarness.Create();
        var dispatcher = Dispatcher(harness);
        dispatcher.Execute("voxel reset");
        dispatcher.Execute("rail construct");
        dispatcher.Execute("rail assemble");
        dispatcher.Execute("rail start");
        dispatcher.Execute("rail step 300");
        dispatcher.Execute("voxel damage 110 1 15 100");
        dispatcher.Execute("rail step 2000");
        dispatcher.Execute("rail query assembly");
        dispatcher.Execute("rail query motion");
        dispatcher.Execute("voxel save Gap Halt");
        Assert.IsTrue(harness.Session.SaveId.HasValue);
        dispatcher.Execute($"voxel load {harness.Session.SaveId.Value:D}");
        dispatcher.Execute("rail start");
        dispatcher.Execute("rail repair 110 1 15");
        dispatcher.Execute("rail query assembly");
        dispatcher.Execute("rail start");
        var gapLog = RailLog(harness);
        StringAssert.Contains(gapLog, "halt forward-gap");
        StringAssert.Contains(gapLog, "DisconnectedPath");
        StringAssert.Contains(gapLog, "halt none");
        StringAssert.Contains(gapLog, "Started");
        var voxelLog = VoxelLog(harness);
        StringAssert.Contains(voxelLog, "Saved");
        StringAssert.Contains(voxelLog, "Loaded");

        using var split = StaticTrainHarness.Create();
        var splitDispatcher = Dispatcher(split);
        splitDispatcher.Execute("voxel reset");
        splitDispatcher.Execute("rail construct");
        splitDispatcher.Execute("rail assemble");
        splitDispatcher.Execute("rail start");
        splitDispatcher.Execute("rail step 300");
        var locoId = split.Session.QueryTrainAssemblies().Single().ActiveLocomotiveId!.Value;
        var coupler = split.Session.QueryTrainAssemblies().Single().Connections[0].FromCell;
        splitDispatcher.Execute($"composite damage {locoId.Value} {coupler.X} {coupler.Y} {coupler.Z} 100");
        splitDispatcher.Execute("rail step 30");
        splitDispatcher.Execute("voxel damage 88 1 15 100");
        splitDispatcher.Execute("rail query assembly");
        splitDispatcher.Execute("rail query motion");
        splitDispatcher.Execute("voxel save Support Loss Halt");
        splitDispatcher.Execute("rail repair 88 1 15");
        var rear = split.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is null);
        splitDispatcher.Execute($"rail start assembly {rear.Id.Value}");
        var supportLog = RailLog(split);
        StringAssert.Contains(supportLog, "halt support-lost");
        StringAssert.Contains(supportLog, "MissingPropulsion");
        StringAssert.Contains(VoxelLog(split), "Saved");
    }

    private static TrainAssemblyQuery AssertStoppedAtIncomingBoundary(
        StaticTrainHarness harness,
        double start,
        double end,
        TrainHaltReason halt)
    {
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        Assert.IsTrue(assembly.Vehicles[0].FrontContact.X <= start + Tolerance, assembly.Vehicles[0].FrontContact.X.ToString("G17", CultureInfo.InvariantCulture));
        Assert.AreEqual(0.0, assembly.Speed, Tolerance);
        Assert.IsFalse(assembly.Running);
        Assert.AreEqual(halt, assembly.HaltReason);
        Assert.IsFalse(ContactsEnter(assembly, start, end));
        return assembly;
    }

    private static void AssertGap(StaticTrainHarness harness, EntityId segmentId, double start, double end)
    {
        var intervals = harness.Session.Rails.TraversableWorldX(segmentId, harness.Session.World);
        Assert.IsFalse(intervals.Any(interval => interval.Start <= start && interval.End >= end));
        Assert.IsTrue(harness.Session.BuildTrackRenderModel().HasGapNear(StraightRailFixture.FirstSegmentDamageCell));
    }

    private static bool ContactsEnter(TrainAssemblyQuery assembly, double start, double end) =>
        assembly.Vehicles.Any(vehicle =>
            IsInsideGap(vehicle.FrontContact.X, start, end) ||
            IsInsideGap(vehicle.RearContact.X, start, end));

    private static bool IsInsideGap(double x, double start, double end) =>
        x > start + Tolerance && x < end - Tolerance;

    private static void AssertMotion(TrainAssemblyQuery assembly, double locoFront, double carriageFront, double speed, bool running)
    {
        Assert.AreEqual(locoFront, assembly.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(carriageFront, assembly.Vehicles[1].FrontContact.X, Tolerance);
        Assert.AreEqual(speed, assembly.Speed, Tolerance);
        Assert.AreEqual(running, assembly.Running);
    }

    private static (TrainAssemblyQuery Loco, TrainAssemblyQuery Carriage) AssertSplit(
        StaticTrainHarness harness,
        double locoFront,
        double carriageFront,
        double locoSpeed,
        double carriageSpeed)
    {
        var assemblies = harness.Session.QueryTrainAssemblies();
        Assert.AreEqual(2, assemblies.Count);
        var loco = assemblies.Single(item => item.ActiveLocomotiveId is not null);
        var carriage = assemblies.Single(item => item.ActiveLocomotiveId is null);
        Assert.AreEqual(locoFront, loco.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(carriageFront, carriage.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(locoSpeed, loco.Speed, Tolerance);
        Assert.AreEqual(carriageSpeed, carriage.Speed, Tolerance);
        return (loco, carriage);
    }

    private static void AssertRoundTripUninterrupted(StaticTrainHarness live, string name)
    {
        live.Session.RebuildDerivedRailGraph();
        var liveHash = WorldSaveAuthority.Fingerprint(live.Session);
        var liveAuthority = live.Session.CaptureTrainAuthority();
        var saved = live.Session.Save(name);
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        using var reader = StaticTrainHarness.Create(live.UserData, live.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(liveAuthority, reader.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHash, WorldSaveAuthority.Fingerprint(reader.Session));
        Assert.IsTrue(live.Session.AdvanceSimulation(30).Succeeded);
        Assert.IsTrue(reader.Session.AdvanceSimulation(30).Succeeded);
        Assert.AreEqual(live.Session.CaptureTrainAuthority(), reader.Session.CaptureTrainAuthority());
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(live.Session), WorldSaveAuthority.Fingerprint(reader.Session));
    }

    private static IReadOnlyList<WorldVoxelCoord> FirstSegmentOccupiedCells(VoxelDevelopmentSession session, EntityId firstId) =>
        session.QueryRails().Occupancy
            .Where(item =>
                item.Supports.Any(support => support.SegmentId == firstId) &&
                session.Query(item.Cell).IsOccupied)
            .Select(item => item.Cell)
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ThenBy(item => item.Z)
            .ToArray();

    private static void BreakCoupler(VoxelDevelopmentSession session)
    {
        var assembly = session.QueryTrainAssemblies().Single();
        Assert.IsTrue(session.Damage(assembly.ActiveLocomotiveId!.Value, assembly.Connections[0].FromCell, 100).Succeeded);
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

    private static StaticTrainHarness HighSpeed()
    {
        var harness = StaticTrainHarness.Create();
        harness.Source.AddVoxelFile("core", "voxels/propulsion.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "propulsion",
                "开发动力件",
                "切片 4.2 最小动力行为零件。能力由已注册 propulsion 行为推导，不表示燃料或通用资源。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: "[{\"type\":\"propulsion\",\"target_speed\":600,\"acceleration\":2}]")));
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        return harness;
    }

    private static double CurrentTarget(StaticTrainHarness harness)
    {
        Assert.IsTrue(harness.Reloader.Catalog.Current!.TryGet(StaticTrainFixture.PropulsionId, out var entry));
        return entry.Definition.Propulsion!.TargetSpeed;
    }

    private static ConsoleCommandDispatcher Dispatcher(StaticTrainHarness harness) =>
        new(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session),
            new CompositeConsoleCommands(harness.Session),
            new RailConsoleCommands(harness.Session));

    private static string RailLog(StaticTrainHarness harness) =>
        string.Join('\n', harness.Memory.Snapshot()
            .Where(item => item.EventId == ConsoleCommandDispatcher.RailEventId)
            .Select(item => item.Message));

    private static string VoxelLog(StaticTrainHarness harness) =>
        string.Join('\n', harness.Memory.Snapshot()
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .Select(item => item.Message));

    private static RailConstructionPlan ShiftZ(RailConstructionPlan plan, double deltaZ) =>
        new(
            plan.Segments.Select(segment => segment with
            {
                Alignment = segment.Alignment with
                {
                    StartZ = segment.Alignment.StartZ + deltaZ,
                    EndZ = segment.Alignment.EndZ + deltaZ
                },
                RailVolumes = segment.RailVolumes
                    .Select(volume => volume with { MinZ = volume.MinZ + deltaZ, MaxZ = volume.MaxZ + deltaZ })
                    .ToArray(),
                SleeperVolumes = segment.SleeperVolumes
                    .Select(volume => volume with { MinZ = volume.MinZ + deltaZ, MaxZ = volume.MaxZ + deltaZ })
                    .ToArray()
            }).ToArray(),
            plan.Connections.ToArray());

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static void RemoveReferencedSegment(JsonObject root, ulong referenced)
    {
        var segments = root["railway"]!["segments"]!.AsArray();
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (segments[index]!["id"]!.GetValue<ulong>() == referenced)
                segments.RemoveAt(index);
        }
    }
}

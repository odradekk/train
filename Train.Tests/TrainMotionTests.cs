using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Behaviors;
using Train.Domain.Rails;
using Train.Domain.Simulation;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class TrainMotionTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void Start_at_step_zero_then_300_steps_reaches_the_fixture_pose()
    {
        using var harness = Parked();
        Assert.AreEqual(0UL, harness.Session.SimulationStep);
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        var started = harness.Session.QueryTrainAssemblies().Single();
        Assert.IsTrue(started.Running);
        Assert.AreEqual(0.0, started.Speed, Tolerance);
        AssertWorldContact(started.Vehicles[0].FrontContact, 80, 2, 16);
        Assert.AreEqual(0UL, harness.Session.SimulationStep);

        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        var moved = AssertMotion(harness, 96, 87, 4, running: true);
        Assert.AreEqual(300UL, harness.Session.SimulationStep);
        AssertConstraints(harness, moved);
    }

    [TestMethod]
    public void Reverse_start_is_rejected_without_changing_authority()
    {
        using var harness = Parked();
        AssertRejectedMotion(harness, TrainMotionStatus.ReverseUnsupported, () =>
            harness.Session.StartTrain(RailHeading.AgainstAlignment));
        AssertWorldContact(harness.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact, 80, 2, 16);
    }

    [TestMethod]
    public void Stop_zeros_speed_and_does_not_keep_a_previous_value()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        Assert.IsTrue(harness.Session.StopTrain().Succeeded);
        var stopped = AssertMotion(harness, 96, 87, 0, running: false);
        Assert.IsTrue(harness.Session.AdvanceSimulation(60).Succeeded);
        var after = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(stopped.Vehicles[0].FrontContact.X, after.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, after.Speed, Tolerance);
        Assert.IsFalse(after.Running);
    }

    [TestMethod]
    public void Eighteen_hundred_steps_then_stop_matches_the_fixture()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(1800).Succeeded);
        var moved = AssertMotion(harness, 196, 187, 4, running: true);
        Assert.AreEqual(harness.Session.Rails.Segments[1].Id, moved.Reference.SegmentId);
        Assert.AreEqual(52.0, moved.Reference.Distance, Tolerance);
        Assert.IsTrue(harness.Session.StopTrain().Succeeded);
        AssertMotion(harness, 196, 187, 0, running: false);
    }

    [TestMethod]
    public void End_of_line_stops_on_the_last_legal_contact_without_reversing()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(4000).Succeeded);
        var ended = AssertMotion(harness, 272, 263, 0, running: false);
        Assert.IsTrue(ended.Vehicles[0].FrontContact.X <= 272 + Tolerance);
        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        var still = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(ended.Vehicles[0].FrontContact.X, still.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, still.Speed, Tolerance);
        Assert.IsFalse(still.Running);
    }

    [TestMethod]
    public void Connected_seam_updates_segment_identity_and_unconnected_adjacent_rail_is_closed()
    {
        using var connected = Parked();
        Assert.IsTrue(connected.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(connected.Session.AdvanceSimulation(1800).Succeeded);
        var crossed = connected.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(connected.Session.Rails.Segments[1].Id, crossed.Reference.SegmentId);
        AssertWorldContact(crossed.Vehicles[0].FrontContact, 196, 2, 16);

        using var blocked = Parked(StraightRailFixture.CreateUnconnectedFixturePlan());
        var firstEnd = blocked.Session.Rails.Segments[0].Alignment.EndX;
        Assert.IsTrue(blocked.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(blocked.Session.AdvanceSimulation(4000).Succeeded);
        var stopped = blocked.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(blocked.Session.Rails.Segments[0].Id, stopped.Reference.SegmentId);
        Assert.AreEqual(firstEnd, stopped.Vehicles[0].FrontContact.X, Tolerance);
        Assert.IsTrue(stopped.Vehicles[0].FrontContact.X <= 144 + Tolerance);
        Assert.AreEqual(0.0, stopped.Speed, Tolerance);
    }

    [TestMethod]
    public void Whole_path_is_limited_by_a_gap_and_rebuild_does_not_move_the_consist()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(120).Succeeded);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        Assert.IsTrue(harness.Session.AdvanceSimulation(2000).Succeeded);
        var limited = harness.Session.QueryTrainAssemblies().Single();
        Assert.IsTrue(limited.Vehicles[0].FrontContact.X <= 110 + Tolerance);
        Assert.IsFalse(limited.Vehicles.Any(vehicle => vehicle.FrontContact.X > 110 + Tolerance && vehicle.FrontContact.X < 111 - Tolerance));
        Assert.AreEqual(0.0, limited.Speed, Tolerance);

        using var rebuild = Parked();
        Assert.IsTrue(rebuild.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(rebuild.Session.AdvanceSimulation(300).Succeeded);
        var before = rebuild.Session.CaptureTrainAuthority();
        rebuild.Session.RebuildDerivedRailGraph();
        Assert.AreEqual(before, rebuild.Session.CaptureTrainAuthority());
        AssertWorldContact(rebuild.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact, 96, 2, 16);
    }

    [TestMethod]
    public void Step_overflow_and_identity_exhaustion_fail_atomically()
    {
        using var overflow = Parked();
        Assert.IsTrue(overflow.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        SetCursor(overflow.Session, nameof(VoxelDevelopmentSession.SimulationStep), ulong.MaxValue);
        AssertRejectedMotion(overflow, TrainMotionStatus.StepOverflow, () => overflow.Session.AdvanceSimulation(1));
        Assert.AreEqual(ulong.MaxValue, overflow.Session.SimulationStep);
        AssertWorldContact(overflow.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact, 80, 2, 16);

        using var ids = Parked();
        SetCursor(ids.Session, nameof(VoxelDevelopmentSession.NextEntityId), ulong.MaxValue);
        var before = ids.Session.CaptureTrainAuthority();
        Assert.IsTrue(ids.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.AreEqual(ulong.MaxValue, ids.Session.NextEntityId);
        Assert.AreNotEqual(before, ids.Session.CaptureTrainAuthority());
        Assert.IsTrue(ids.Session.QueryTrainAssemblies().Single().Running);
    }

    [TestMethod]
    public void Repeated_runs_and_display_schedules_share_the_same_authority()
    {
        using var first = Parked();
        using var second = Parked();
        RunFixture300(first.Session);
        RunFixture300(second.Session);
        Assert.AreEqual(first.Session.CaptureTrainAuthority(), second.Session.CaptureTrainAuthority());

        using var scheduled = Parked();
        Assert.IsTrue(scheduled.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        var collected = CollectDisplay(1.0 / 30.0, 150);
        Assert.AreEqual(300UL, collected);
        Assert.AreEqual(300UL, CollectDisplay(1.0 / 60.0, 300));
        Assert.AreEqual(300UL, CollectDisplay(1.0 / 144.0, 720));
        Assert.IsTrue(scheduled.Session.AdvanceSimulation(collected).Succeeded);
        Assert.AreEqual(first.Session.CaptureTrainAuthority(), scheduled.Session.CaptureTrainAuthority());

        using var paused = Parked();
        Assert.IsTrue(paused.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(paused.Session.AdvanceSimulation(100).Succeeded);
        Assert.IsTrue(paused.Session.PauseSimulation().Succeeded);
        var pauseStep = paused.Session.SimulationStep;
        Assert.AreEqual(0UL, paused.Session.CollectHostSteps(1.0 / 30.0));
        Assert.AreEqual(pauseStep, paused.Session.SimulationStep);
        Assert.IsTrue(paused.Session.ResumeSimulation().Succeeded);
        Assert.IsTrue(paused.Session.AdvanceSimulation(200).Succeeded);
        Assert.AreEqual(first.Session.CaptureTrainAuthority(), paused.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void Propulsion_reload_keeps_absolute_state_and_rejects_illegal_candidates()
    {
        using var harness = Parked();
        harness.Reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(harness.Session));
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        var before = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(4.0, before.Speed, Tolerance);

        var defaultRevision = Revision(harness, StaticTrainFixture.PropulsionId);
        WritePropulsion(harness, targetSpeed: 4, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreEqual(defaultRevision, Revision(harness, StaticTrainFixture.PropulsionId));

        WritePropulsion(harness, targetSpeed: 2, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreNotEqual(defaultRevision, Revision(harness, StaticTrainFixture.PropulsionId));
        var adapted = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(1, harness.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(before.Vehicles[0].FrontContact.X, adapted.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(2.0, adapted.Speed, Tolerance);
        Assert.IsTrue(adapted.Running);

        var afterClamp = harness.Session.CaptureTrainAuthority();
        WritePropulsion(harness, targetSpeed: -1, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Rejected, harness.Reloader.Reload().Status);
        Assert.AreEqual(afterClamp, harness.Session.CaptureTrainAuthority());

        WritePropulsion(harness, behaviorsJson: "[]");
        Assert.AreEqual(ContentReloadStatus.Rejected, harness.Reloader.Reload().Status);
        Assert.AreEqual(afterClamp, harness.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void Save_rejects_railway_motion_and_keeps_the_previous_file()
    {
        using var harness = Parked();
        harness.Session.Reset();
        Assert.AreEqual(WorldSaveStatus.Saved, harness.Session.Save("Empty").Status);
        var path = harness.Store.PathFor(harness.Session.SaveId!.Value);
        var previous = File.ReadAllText(path);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(WorldSaveStatus.UnsupportedRailway, harness.Session.Save("Motion").Status);
        Assert.AreEqual(previous, File.ReadAllText(path));
    }

    [TestMethod]
    public void Console_uses_the_same_start_step_stop_and_query_entry()
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

        dispatcher.Execute("rail start reverse");
        dispatcher.Execute("rail start");
        dispatcher.Execute("rail step 300");
        dispatcher.Execute("rail query assembly");
        dispatcher.Execute("rail query motion");
        dispatcher.Execute("rail stop");
        dispatcher.Execute("rail pause");
        dispatcher.Execute("rail resume");
        var log = string.Join('\n', harness.Memory.Snapshot().Where(item => item.EventId == ConsoleCommandDispatcher.RailEventId).Select(item => item.Message));
        StringAssert.Contains(log, "ReverseUnsupported");
        StringAssert.Contains(log, "Started");
        StringAssert.Contains(log, "speed 4");
        StringAssert.Contains(log, "running true");
        StringAssert.Contains(log, "Stopped");
        StringAssert.Contains(log, "Paused");
        StringAssert.Contains(log, "Resumed");
    }

    [TestMethod]
    public void Default_and_explicit_fixture_propulsion_share_a_semantic_revision()
    {
        using var omitted = LoggerSession.Create();
        using var explicitDefaults = LoggerSession.Create();
        var left = ContentFixtures.Create(StaticTrainHarness.Package(), omitted.Logger);
        var rightSource = StaticTrainHarness.Package();
        rightSource.AddVoxelFile("core", "voxels/propulsion.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "propulsion",
                "开发动力件",
                "切片 4.2 最小动力行为零件。能力由已注册 propulsion 行为推导，不表示燃料或通用资源。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: "[{\"type\":\"propulsion\",\"target_speed\":4,\"acceleration\":2}]")));
        var right = ContentFixtures.Create(rightSource, explicitDefaults.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, left.Reload().Status);
        Assert.AreEqual(ContentReloadStatus.Committed, right.Reload().Status);
        Assert.IsTrue(left.Catalog.Current!.TryGet(StaticTrainFixture.PropulsionId, out var leftEntry));
        Assert.IsTrue(right.Catalog.Current!.TryGet(StaticTrainFixture.PropulsionId, out var rightEntry));
        Assert.AreEqual(leftEntry.SemanticRevision, rightEntry.SemanticRevision);
        Assert.AreEqual(4.0, leftEntry.Definition.Propulsion!.TargetSpeed, Tolerance);
        Assert.AreEqual(2.0, leftEntry.Definition.Propulsion.Acceleration, Tolerance);
    }

    private static StaticTrainHarness Parked(RailConstructionPlan? plan = null)
    {
        var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(plan).Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        return harness;
    }

    private static void RunFixture300(VoxelDevelopmentSession session)
    {
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
    }

    private static ulong CollectDisplay(double delta, int frames)
    {
        var scheduler = new SimulationScheduler();
        ulong steps = 0;
        for (var frame = 0; frame < frames; frame++)
            steps += scheduler.CollectSteps(delta, paused: false);
        return steps;
    }

    private static TrainAssemblyQuery AssertMotion(
        StaticTrainHarness harness,
        double locoFront,
        double carriageFront,
        double speed,
        bool running)
    {
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        AssertWorldContact(assembly.Vehicles[0].FrontContact, locoFront, 2, 16);
        AssertWorldContact(assembly.Vehicles[1].FrontContact, carriageFront, 2, 16);
        Assert.AreEqual(speed, assembly.Speed, Tolerance);
        Assert.AreEqual(running, assembly.Running);
        return assembly;
    }

    private static void AssertConstraints(StaticTrainHarness harness, TrainAssemblyQuery assembly)
    {
        Assert.AreEqual(6.0, Math.Abs(assembly.Vehicles[0].FrontContact.X - assembly.Vehicles[0].RearContact.X), Tolerance);
        Assert.AreEqual(6.0, Math.Abs(assembly.Vehicles[1].FrontContact.X - assembly.Vehicles[1].RearContact.X), Tolerance);
        Assert.AreEqual(1.0, assembly.Vehicles[0].Transform.OriginX - 8 - assembly.Vehicles[1].Transform.OriginX, Tolerance);
        Assert.IsFalse(StraightTrainSolver.BodiesOverlap(assembly.Vehicles[0].Transform, assembly.Vehicles[1].Transform));
        var loco = harness.Session.Vehicles.Single(item => item.Id == assembly.VehicleIds[0]);
        var carriage = harness.Session.Vehicles.Single(item => item.Id == assembly.VehicleIds[1]);
        Assert.IsTrue(StraightTrainSolver.TryValidatePoses(loco, carriage, out var status));
        Assert.AreEqual(TrainAssemblyStatus.Formed, status);
    }

    private static void AssertWorldContact(WorldContactQuery contact, double x, double y, double z)
    {
        Assert.AreEqual(x, contact.X, Tolerance, "x");
        Assert.AreEqual(y, contact.Y, Tolerance, "y");
        Assert.AreEqual(z, contact.Z, Tolerance, "z");
    }

    private static void AssertRejectedMotion(
        StaticTrainHarness harness,
        TrainMotionStatus expected,
        Func<TrainMotionResult> act)
    {
        var before = harness.Session.CaptureTrainAuthority();
        var notifications = 0;
        harness.Session.Changed += Count;
        try
        {
            Assert.AreEqual(expected, act().Status);
            Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
            Assert.AreEqual(0, notifications);
        }
        finally
        {
            harness.Session.Changed -= Count;
        }

        void Count() => notifications++;
    }

    private static void SetCursor(VoxelDevelopmentSession session, string name, ulong value) =>
        typeof(VoxelDevelopmentSession).GetProperty(name)!.SetValue(session, value);

    private static string Revision(StaticTrainHarness harness, VoxelDefinitionId id)
    {
        Assert.IsTrue(harness.Reloader.Catalog.Current!.TryGet(id, out var entry));
        return entry.SemanticRevision;
    }

    private static void WritePropulsion(
        StaticTrainHarness harness,
        double targetSpeed = 4,
        double acceleration = 2,
        string? behaviorsJson = null)
    {
        behaviorsJson ??=
            $"[{{ \"type\":\"propulsion\",\"target_speed\":{targetSpeed.ToString(CultureInfo.InvariantCulture)},\"acceleration\":{acceleration.ToString(CultureInfo.InvariantCulture)} }}]";
        harness.Source.AddVoxelFile("core", "voxels/propulsion.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "propulsion",
                "开发动力件",
                "切片 4.2 最小动力行为零件。能力由已注册 propulsion 行为推导，不表示燃料或通用资源。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: behaviorsJson)));
    }
}

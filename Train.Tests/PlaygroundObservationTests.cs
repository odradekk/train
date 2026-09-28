using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class PlaygroundObservationTests
{
    private const double Frame = 1.0 / 60.0;
    private static readonly ObservationVector Overview = new(76, 16, -8);
    private static readonly ObservationVector OverviewLook = new(76, 2, 16);

    [TestMethod]
    public void Free_observation_needs_no_vehicle_and_selection_never_moves_the_camera()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        var start = observer.Present(Frame, session.QueryDisplayPoses());

        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        var list = PlaygroundInspector.List(session);
        Assert.AreEqual(2, list.Count);
        Assert.IsTrue(list.All(entry => entry.Object.Kind == ObservedObjectKind.RailSegment));

        Assert.AreEqual(PlaygroundObservationStatus.NoSelection, observer.FocusSelection());
        Assert.AreEqual(PlaygroundObservationStatus.NoSelection, observer.FollowSelection());
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(list[0].Object));
        Assert.AreEqual(start, observer.Present(Frame, session.QueryDisplayPoses()));
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.WorldCell(StraightRailFixture.LeftRailSample)));
        Assert.AreEqual(start, observer.Present(Frame, session.QueryDisplayPoses()));
        Assert.AreEqual(PlaygroundObservationStatus.NotVehicle, observer.FollowSelection());
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        Assert.AreEqual(PlaygroundObservationStatus.UnknownObject, observer.Select(ObservedObject.Vehicle(new EntityId(999))));
        Assert.AreEqual(PlaygroundObservationStatus.UnknownObject, observer.Select(ObservedObject.WorldCell(PlaygroundScenePoints.EmptyProbe)));
        Assert.AreEqual(ObservedObjectKind.WorldCell, observer.Selection!.Value.Kind);

        Assert.IsTrue(observer.Fly(1, 2, 3));
        observer.Look(0.2, -0.1);
        observer.Zoom(4);
        var moved = observer.Present(Frame, session.QueryDisplayPoses());
        Assert.AreNotEqual(start.Position, moved.Position);
        Assert.AreEqual(ObservedObjectKind.WorldCell, observer.Selection!.Value.Kind);
    }

    [TestMethod]
    public void Focus_frames_the_selection_once_from_the_current_view_and_free_returns_in_place()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        var cell = StraightRailFixture.LeftRailSample;
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.WorldCell(cell)));
        Assert.AreEqual(PlaygroundObservationStatus.Focused, observer.FocusSelection());
        Assert.AreEqual(PlaygroundCameraMode.Focus, observer.Mode);

        var poses = PresentFrames(observer, session, 60);
        var goal = new ObservationVector(cell.X + 0.5, cell.Y + 0.5, cell.Z + 0.5);
        Assert.IsFalse(observer.Transitioning);
        AssertClose(goal, poses[^1].Look);
        var direction = Overview - goal;
        var framed = poses[^1].Position - goal;
        Assert.AreEqual(Math.Atan2(direction.Z, direction.X), Math.Atan2(framed.Z, framed.X), 1e-9);

        observer.Look(0.3, 0);
        var orbited = observer.Present(Frame, session.QueryDisplayPoses());
        AssertClose(goal, orbited.Look);
        Assert.AreEqual((poses[^1].Position - goal).Length, (orbited.Position - goal).Length, 1e-9);

        Assert.AreEqual(PlaygroundObservationStatus.Free, observer.Free());
        Assert.IsNull(observer.Target);
        var free = observer.Present(Frame, session.QueryDisplayPoses());
        AssertClose(orbited.Position, free.Position);
        AssertClose(Unit(orbited.Look - orbited.Position), Unit(free.Look - free.Position));
        Assert.AreEqual(ObservedObjectKind.WorldCell, observer.Selection!.Value.Kind);
    }

    [TestMethod]
    public void Transitions_are_smooth_and_settle_exactly_on_the_goal()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var observer = new PlaygroundObserver(session, new ObservationVector(20, 30, -40), new ObservationVector(20, 2, 16));
        var before = observer.Present(Frame, session.QueryDisplayPoses());
        var loco = session.QueryTrainAssemblies().Single().VehicleIds[0];
        observer.Select(ObservedObject.Vehicle(loco));
        Assert.AreEqual(PlaygroundObservationStatus.Following, observer.FollowSelection());

        var poses = PresentFrames(observer, session, 60);
        var total = (poses[^1].Look - before.Look).Length;
        Assert.IsTrue(total > 10, $"look transition distance {total}");
        var steps = new[] { before }.Concat(poses).Zip(poses, (a, b) => (b.Look - a.Look).Length).ToArray();
        Assert.IsTrue(steps[0] < total * 0.05, $"first frame jumped {steps[0]} of {total}");
        Assert.IsTrue(steps.Max() < total * 0.1, $"largest frame step {steps.Max()} of {total}");
        Assert.IsFalse(observer.Transitioning);
        var frame = PlaygroundObserver.VehicleFrame(session, session.QueryDisplayPoses(), loco)!;
        AssertClose(frame.Center, poses[^1].Look);
    }

    [TestMethod]
    public void Follow_reads_the_displayed_body_frame_and_keeps_its_relative_offset_between_steps()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var carriage = session.QueryTrainAssemblies().Single().VehicleIds[1];
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        observer.Select(ObservedObject.Vehicle(carriage));
        Assert.AreEqual(PlaygroundObservationStatus.Following, observer.FollowSelection());
        PresentFrames(observer, session, 60);
        var settled = observer.Pose.Position - observer.Pose.Look;

        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        var sawFraction = false;
        for (var index = 0; index < 90; index++)
        {
            var delta = index % 3 == 0 ? 1.0 / 144.0 : 1.0 / 50.0;
            session.AdvanceSimulation(session.CollectHostSteps(delta));
            var sample = session.QueryDisplayPoses();
            var pose = observer.Present(delta, sample);
            var displayed = sample.Vehicles.Single(item => item.VehicleId == carriage).Transform;
            var local = PlaygroundObserver.LocalCenter(session.Vehicles.Single(item => item.Id == carriage));
            var body = displayed.TransformPoint(local.X, local.Y, local.Z);
            AssertClose(new ObservationVector(body.X, body.Y, body.Z), pose.Look);
            AssertClose(settled, pose.Position - pose.Look);
            var authoritative = session.Vehicles.Single(item => item.Id == carriage).Transform.OriginX;
            if (sample.Alpha > 0.05 && sample.Alpha < 0.95 && Math.Abs(displayed.OriginX - authoritative) > 1e-6)
                sawFraction = true;
        }

        Assert.IsTrue(sawFraction, "follow never sampled an interpolated display pose");
        var heading = PlaygroundObserver.VehicleFrame(session, session.QueryDisplayPoses(), carriage)!.Heading;
        AssertClose(new ObservationVector(1, 0, 0), heading);

        Assert.IsTrue(session.StopTrain().Succeeded);
        session.AdvanceSimulation(session.CollectHostSteps(0.2));
        Assert.AreEqual(0.0, session.QueryTrainAssemblies().Single().Speed);
        var stopped = PlaygroundObserver.VehicleFrame(session, session.QueryDisplayPoses(), carriage)!;
        AssertClose(new ObservationVector(1, 0, 0), stopped.Heading);
        AssertClose(settled, observer.Present(Frame, session.QueryDisplayPoses()).Position - observer.Pose.Look);
    }

    [TestMethod]
    public void Follow_offset_turns_with_the_displayed_body_heading()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        Assert.IsTrue(session.Manufacture(HollowShellFixture.DefinitionId, CompositeTransform.Translation(60, 6, 24)).Succeeded);
        Assert.IsTrue(CompositeTransform.TryCreate(100, 6, 24, new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY), out var turned));
        Assert.IsTrue(session.Manufacture(HollowShellFixture.DefinitionId, turned).Succeeded);
        var ids = session.Vehicles.Select(item => item.Id).OrderBy(id => id.Value).ToArray();
        var sample = session.QueryDisplayPoses();

        var straight = PlaygroundObserver.VehicleFrame(session, sample, ids[0])!;
        var rotated = PlaygroundObserver.VehicleFrame(session, sample, ids[1])!;
        AssertClose(new ObservationVector(-1, 0, 0), straight.Heading);
        AssertClose(new ObservationVector(0, 0, -1), rotated.Heading);

        var observer = new PlaygroundObserver(session, straight.Center + new ObservationVector(3, 8, -20), straight.Center);
        observer.Select(ObservedObject.Vehicle(ids[0]));
        observer.FollowSelection(sample);
        for (var index = 0; index < 60; index++)
            observer.Present(Frame, sample);
        var offset = observer.Pose.Position - observer.Pose.Look;

        var shell = session.Vehicles.Single(item => item.Id == ids[0]);
        var displayedTurned = new TrainDisplaySample(0, true, Array.Empty<TrainDisplayAssemblyQuery>(), new[]
        {
            new TrainDisplayVehicleQuery(ids[0], null, turned, default!, default!)
        });
        var pose = observer.Present(Frame, displayedTurned);
        var local = PlaygroundObserver.LocalCenter(shell);
        var body = turned.TransformPoint(local.X, local.Y, local.Z);
        AssertClose(new ObservationVector(body.X, body.Y, body.Z), pose.Look);
        AssertClose(new ObservationVector(-offset.Z, offset.Y, offset.X), pose.Position - pose.Look);
    }

    [TestMethod]
    public void Split_and_active_locomotive_change_keep_the_followed_vehicle()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var assembly = session.QueryTrainAssemblies().Single();
        var (loco, carriage) = (assembly.VehicleIds[0], assembly.VehicleIds[1]);
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        observer.Select(ObservedObject.Vehicle(carriage));
        observer.FollowSelection();
        var before = PresentFrames(observer, session, 60)[^1];

        var split = session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(loco, StaticTrainFixture.LocomotiveRearCouplerCell, 100) }));
        Assert.AreEqual("Damaged", split.Status);
        Assert.AreEqual(2, session.QueryTrainAssemblies().Count);
        var after = observer.Present(Frame, session.QueryDisplayPoses());
        Assert.AreEqual(PlaygroundCameraMode.Follow, observer.Mode);
        Assert.AreEqual(ObservedObject.Vehicle(carriage), observer.Target);
        Assert.AreEqual(ObservedObject.Vehicle(carriage), observer.Selection);
        Assert.AreEqual(0, observer.Notices.Count);
        AssertClose(before.Position, after.Position);
        var carriageAssembly = session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(carriage));
        Assert.IsNull(carriageAssembly.ActiveLocomotiveId);

        observer.Select(ObservedObject.Vehicle(loco));
        observer.FollowSelection();
        PresentFrames(observer, session, 60);
        Assert.AreEqual(ObservedObject.Vehicle(loco), observer.Target);
        var control = session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(loco, new LocalVoxelCoord(3, 0, 1), 100) }));
        Assert.AreEqual("Damaged", control.Status);
        var locoAssembly = session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(loco));
        Assert.IsNull(locoAssembly.ActiveLocomotiveId);
        observer.Present(Frame, session.QueryDisplayPoses());
        Assert.AreEqual(ObservedObject.Vehicle(loco), observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Follow, observer.Mode);
        Assert.AreEqual(0, observer.Notices.Count);
    }

    [TestMethod]
    public void Destroyed_target_clears_references_reports_and_keeps_the_camera_in_place()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var assembly = session.QueryTrainAssemblies().Single();
        var (loco, carriage) = (assembly.VehicleIds[0], assembly.VehicleIds[1]);
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        observer.Select(ObservedObject.Vehicle(carriage));
        observer.FollowSelection();
        var held = PresentFrames(observer, session, 60)[^1];

        var destroyed = session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(carriage, HollowShellFixture.CoreCell, 100) }));
        Assert.AreEqual("Removed", destroyed.Status);
        Assert.IsFalse(session.Vehicles.Any(item => item.Id == carriage));
        Assert.IsTrue(session.Vehicles.Any(item => item.Id == loco));

        var after = observer.Present(Frame, session.QueryDisplayPoses());
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        Assert.IsNull(observer.Target);
        Assert.IsNull(observer.Selection);
        CollectionAssert.AreEquivalent(
            new[] { PlaygroundNoticeKind.SelectionLost, PlaygroundNoticeKind.TargetLost },
            observer.Notices.Select(item => item.Kind).ToArray());
        var lost = observer.Notices.Single(item => item.Kind == PlaygroundNoticeKind.TargetLost);
        Assert.AreEqual(ObservedObject.Vehicle(carriage), lost.Object);
        Assert.AreEqual(PlaygroundCameraMode.Follow, lost.Mode);
        StringAssert.Contains(lost.DescribeZh(), "已彻底销毁");
        AssertClose(held.Position, after.Position);
        AssertClose(Unit(held.Look - held.Position), Unit(after.Look - after.Position));

        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment, assemblyId: session.QueryTrainAssemblies().Single().Id).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(120).Succeeded);
        var later = observer.Present(Frame, session.QueryDisplayPoses());
        AssertClose(after.Position, later.Position);
        AssertClose(after.Look, later.Look);
        Assert.IsNull(observer.Target);
        Assert.AreEqual(2, observer.Notices.Count);
    }

    [TestMethod]
    public void Target_destroyed_mid_transition_stops_where_the_camera_already_is()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var carriage = session.QueryTrainAssemblies().Single().VehicleIds[1];
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        observer.Select(ObservedObject.Vehicle(carriage));
        observer.FollowSelection();
        var midway = PresentFrames(observer, session, 10)[^1];
        Assert.IsTrue(observer.Transitioning);

        session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(carriage, HollowShellFixture.CoreCell, 100) }));
        var stopped = observer.Present(Frame, session.QueryDisplayPoses());
        Assert.IsFalse(observer.Transitioning);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        AssertClose(midway.Position, stopped.Position);
        AssertClose(Unit(midway.Look - midway.Position), Unit(stopped.Look - stopped.Position));
    }

    [TestMethod]
    public void World_cell_selection_reads_the_live_voxel_and_is_lost_when_it_is_removed()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        Assert.AreEqual("Added", session.SubmitEdit(AddProbe()).Status);
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.WorldCell(probe)));
        observer.FocusSelection();
        StringAssert.Contains(PlaygroundInspector.Inspect(session, observer.Selection!.Value)!.Durability, "100/100");

        session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(probe, 25) }));
        var damaged = PlaygroundInspector.Inspect(session, observer.Selection!.Value)!;
        StringAssert.Contains(damaged.Durability, "75/100");
        StringAssert.Contains(damaged.Eligibility, "Hit");
        Assert.AreEqual("world", damaged.Owner);

        var rail = PlaygroundInspector.Inspect(session, ObservedObject.WorldCell(StraightRailFixture.LeftRailSample))!;
        StringAssert.StartsWith(rail.Owner, "rail segment ");

        session.SubmitEdit(new VoxelRemoveEdit(probe));
        observer.Present(Frame, session.QueryDisplayPoses());
        Assert.IsNull(observer.Selection);
        Assert.IsNull(observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        Assert.AreEqual(2, observer.Notices.Count);
    }

    [TestMethod]
    public void World_cell_selection_is_lost_when_a_different_voxel_replaces_it_between_frames()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        Assert.AreEqual("Added", session.SubmitEdit(AddProbe()).Status);
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        observer.Select(ObservedObject.WorldCell(probe));

        session.SubmitEdit(new VoxelRemoveEdit(probe));
        Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(probe, new VoxelDefinitionId("core", "fixed"), VoxelOrientation.Identity)).Status);
        observer.Reconcile();

        Assert.IsNull(observer.Selection);
        Assert.AreEqual(PlaygroundNoticeKind.SelectionLost, observer.LastNotice!.Kind);
    }

    [TestMethod]
    public void Same_definition_rebuild_between_frames_drops_the_cell_selection_and_focus_in_place()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        using var observer = FocusedOnProbe(session, out var held);

        Assert.AreEqual("Removed", session.SubmitEdit(new VoxelRemoveEdit(probe)).Status);
        Assert.AreEqual("Added", session.SubmitEdit(AddProbe()).Status);

        AssertRebuildDroppedReferences(observer, session, held);
    }

    [TestMethod]
    public void Same_definition_rebuild_scheduled_for_one_step_drops_the_cell_selection_and_focus_in_place()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        using var observer = FocusedOnProbe(session, out var held);
        var step = session.SimulationStep + 1;

        Assert.AreEqual(WorldEditRefusal.None, session.SubmitEdit(new VoxelRemoveEdit(probe), step).Refusal);
        Assert.AreEqual(WorldEditRefusal.None, session.SubmitEdit(AddProbe(), step).Refusal);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        CollectionAssert.AreEqual(
            new[] { "Removed", "Added" },
            session.QueryEditLog().TakeLast(2).Select(record => record.Status).ToArray());

        AssertRebuildDroppedReferences(observer, session, held);
    }

    [TestMethod]
    public void Faulted_removal_rolls_back_and_keeps_the_cell_selection_and_focus()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        using var observer = FocusedOnProbe(session, out _);
        var selected = observer.Selection;
        session.EditFaultProbe = record =>
        {
            if (record.Edit is VoxelRemoveEdit)
                throw new InvalidOperationException("injected invariant failure");
        };

        Assert.AreEqual(WorldEditState.Faulted, session.SubmitEdit(new VoxelRemoveEdit(probe)).Record!.State);
        observer.Present(Frame, session.QueryDisplayPoses());

        Assert.AreEqual(selected, observer.Selection);
        Assert.AreEqual(selected, observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Focus, observer.Mode);
        Assert.AreEqual(0, observer.Notices.Count);
    }

    [TestMethod]
    public void Removal_executed_before_a_faulted_add_in_the_same_step_still_drops_the_cell_focus()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        using var observer = FocusedOnProbe(session, out var held);
        var step = session.SimulationStep + 1;
        session.SubmitEdit(new VoxelRemoveEdit(probe), step);
        session.SubmitEdit(AddProbe(), step);
        session.EditFaultProbe = record =>
        {
            if (record.Edit is VoxelAddEdit)
                throw new InvalidOperationException("injected invariant failure");
        };

        Assert.IsFalse(session.AdvanceSimulation(1).Succeeded);
        CollectionAssert.AreEqual(
            new[] { WorldEditState.Executed, WorldEditState.Faulted },
            session.QueryEditLog().TakeLast(2).Select(record => record.State).ToArray());
        Assert.IsNull(session.Query(probe).Voxel);
        var after = observer.Present(Frame, session.QueryDisplayPoses());

        Assert.IsNull(observer.Selection);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        Assert.AreEqual(PlaygroundNoticeKind.TargetLost, observer.LastNotice!.Kind);
        AssertClose(held.Position, after.Position);
    }

    [TestMethod]
    public void Disposed_observer_stops_listening_for_executed_edits()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var probe = PlaygroundSequences.EditProbe;
        var observer = FocusedOnProbe(session, out _);
        var selected = observer.Selection;

        observer.Dispose();
        session.SubmitEdit(new VoxelRemoveEdit(probe));
        session.SubmitEdit(AddProbe());

        Assert.AreEqual(selected, observer.Selection);
        Assert.AreEqual(0, observer.Notices.Count);
    }

    [TestMethod]
    public void Composite_reset_that_reuses_the_vehicle_id_drops_the_follow_target_in_place()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var composites = new CompositeConsoleCommands(session);
        composites.Execute(new[] { "reset", "hollow" });
        var id = session.Vehicles.Single().Id;
        using var observer = FollowingVehicle(session, id, out var held);

        composites.Execute(new[] { "reset", "hollow" });

        Assert.AreEqual(id, session.Vehicles.Single().Id, "the reset should reuse the id for a new vehicle");
        AssertReplacementDropped(observer, session, held, ObservedObject.Vehicle(id), PlaygroundCameraMode.Follow, WorldReplacement.Reset);
    }

    [TestMethod]
    public void Successful_load_that_reuses_the_vehicle_id_drops_the_follow_target_in_place()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var composites = new CompositeConsoleCommands(session);
        var voxels = new VoxelConsoleCommands(session);
        composites.Execute(new[] { "reset", "hollow" });
        voxels.Execute(new[] { "save", "Hollow" });
        var saveId = session.SaveId!.Value;
        composites.Execute(new[] { "reset", "bridge" });
        var id = session.Vehicles.Single().Id;
        var before = session.QueryComposite(id)!.DefinitionId;
        using var observer = FollowingVehicle(session, id, out var held);

        StringAssert.StartsWith(voxels.Execute(new[] { "load", saveId.ToString("D") }), "Voxel Loaded.");

        Assert.AreNotEqual(before, session.QueryComposite(id)!.DefinitionId, "the load should put a different vehicle under the same id");
        AssertReplacementDropped(observer, session, held, ObservedObject.Vehicle(id), PlaygroundCameraMode.Follow, WorldReplacement.Load);
    }

    [TestMethod]
    public void Failed_loads_keep_the_selection_target_and_mode()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var composites = new CompositeConsoleCommands(session);
        composites.Execute(new[] { "reset", "hollow" });
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save("Hollow").Status);
        var saved = session.SaveId!.Value;
        var json = File.ReadAllText(playground.Store.PathFor(saved));
        var corrupt = WriteSave(playground, Guid.NewGuid(), _ => "{");
        var mismatched = WriteSave(playground, Guid.NewGuid(), _ => json);
        var unbuildable = Guid.NewGuid();
        WriteSave(playground, unbuildable, id => json
            .Replace(saved.ToString("D"), id.ToString("D"), StringComparison.Ordinal)
            .Replace(HollowShellFixture.DefinitionId.Value, "core:dev-missing-shell", StringComparison.Ordinal));
        var vehicle = session.Vehicles.Single().Id;
        using var observer = FollowingVehicle(session, vehicle, out _);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);

        var statuses = new[] { Guid.NewGuid(), corrupt, mismatched, unbuildable }
            .Select(saveId =>
            {
                var status = session.Load(saveId).Status;
                observer.Present(Frame, session.QueryDisplayPoses());
                return status;
            })
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { WorldSaveStatus.NotFound, WorldSaveStatus.Truncated, WorldSaveStatus.MismatchedId, WorldSaveStatus.MissingDefinition },
            statuses);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.AreEqual(ObservedObject.Vehicle(vehicle), observer.Selection);
        Assert.AreEqual(ObservedObject.Vehicle(vehicle), observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Follow, observer.Mode);
        Assert.AreEqual(0, observer.Notices.Count);
    }

    [TestMethod]
    public void Failed_load_keeps_a_focused_world_cell()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        using var observer = FocusedOnProbe(session, out _);
        var reference = observer.Target;

        Assert.AreEqual(WorldSaveStatus.NotFound, session.Load(Guid.NewGuid()).Status);
        observer.Present(Frame, session.QueryDisplayPoses());

        Assert.AreEqual(reference, observer.Selection);
        Assert.AreEqual(reference, observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Focus, observer.Mode);
        Assert.AreEqual(0, observer.Notices.Count);
    }

    [TestMethod]
    public void Voxel_reset_drops_a_selected_rail_segment_without_moving_the_free_camera()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        using var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        var segment = ObservedObject.Segment(session.Rails.Segments[0].Id);
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(segment));
        var before = observer.Present(Frame, session.QueryDisplayPoses());

        new VoxelConsoleCommands(session).Execute(new[] { "reset" });

        Assert.IsNull(observer.Selection);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        var lost = observer.Notices.Single();
        Assert.AreEqual(new PlaygroundObservationNotice(PlaygroundNoticeKind.SelectionLost, segment, PlaygroundCameraMode.Free, WorldReplacement.Reset), lost);
        Assert.AreEqual($"selection-lost segment {segment.Id.Value} world-replaced reset", lost.Describe());
        Assert.AreEqual(before, observer.Present(Frame, session.QueryDisplayPoses()));
    }

    [TestMethod]
    public void Voxel_reset_drops_a_focused_world_cell()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        using var observer = FocusedOnProbe(session, out var held);
        var reference = observer.Target!.Value;

        new VoxelConsoleCommands(session).Execute(new[] { "reset", "discard" });

        AssertReplacementDropped(observer, session, held, reference, PlaygroundCameraMode.Focus, WorldReplacement.Reset);
    }

    [TestMethod]
    public void Loading_a_save_with_the_same_voxel_still_drops_the_focused_world_cell()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        using var observer = FocusedOnProbe(session, out var held);
        var reference = observer.Target!.Value;
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save("Probe").Status);

        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(session.SaveId!.Value).Status);

        Assert.AreEqual(PlaygroundSequences.EditVoxelId, session.Query(PlaygroundSequences.EditProbe).Voxel!.DefinitionId);
        AssertReplacementDropped(observer, session, held, reference, PlaygroundCameraMode.Focus, WorldReplacement.Load);
    }

    [TestMethod]
    public void Disposed_observer_stops_listening_for_world_replacement()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var composites = new CompositeConsoleCommands(session);
        composites.Execute(new[] { "reset", "hollow" });
        var id = session.Vehicles.Single().Id;
        var observer = FollowingVehicle(session, id, out _);

        observer.Dispose();
        composites.Execute(new[] { "reset", "hollow" });

        Assert.AreEqual(ObservedObject.Vehicle(id), observer.Target);
        Assert.AreEqual(0, observer.Notices.Count);
    }

    private static Guid WriteSave(PlaygroundHarness playground, Guid id, Func<Guid, string> contents)
    {
        var path = playground.Store.PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents(id));
        return id;
    }

    private static PlaygroundObserver FollowingVehicle(VoxelDevelopmentSession session, EntityId id, out ObservationCameraPose held)
    {
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.Vehicle(id)));
        Assert.AreEqual(PlaygroundObservationStatus.Following, observer.FollowSelection());
        held = PresentFrames(observer, session, 60)[^1];
        Assert.IsFalse(observer.Transitioning);
        return observer;
    }

    private static void AssertReplacementDropped(
        PlaygroundObserver observer,
        VoxelDevelopmentSession session,
        ObservationCameraPose held,
        ObservedObject reference,
        PlaygroundCameraMode mode,
        WorldReplacement replacement)
    {
        var after = observer.Present(Frame, session.QueryDisplayPoses());

        Assert.IsNull(observer.Selection);
        Assert.IsNull(observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        CollectionAssert.AreEqual(
            new[] { PlaygroundNoticeKind.SelectionLost, PlaygroundNoticeKind.TargetLost },
            observer.Notices.Select(item => item.Kind).ToArray());
        Assert.IsTrue(observer.Notices.All(item => item.Object == reference && item.Replacement == replacement));
        var lost = observer.LastNotice!;
        Assert.AreEqual(mode, lost.Mode);
        StringAssert.Contains(lost.Describe(), "world-replaced " + replacement.ToString().ToLowerInvariant());
        AssertClose(held.Position, after.Position);
        AssertClose(Unit(held.Look - held.Position), Unit(after.Look - after.Position));
    }

    private static PlaygroundObserver FocusedOnProbe(VoxelDevelopmentSession session, out ObservationCameraPose held)
    {
        var probe = PlaygroundSequences.EditProbe;
        Assert.AreEqual("Added", session.SubmitEdit(AddProbe()).Status);
        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.WorldCell(probe)));
        Assert.AreEqual(PlaygroundObservationStatus.Focused, observer.FocusSelection());
        held = PresentFrames(observer, session, 60)[^1];
        Assert.IsFalse(observer.Transitioning);
        return observer;
    }

    private static VoxelAddEdit AddProbe() =>
        new(PlaygroundSequences.EditProbe, PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity);

    private static void AssertRebuildDroppedReferences(PlaygroundObserver observer, VoxelDevelopmentSession session, ObservationCameraPose held)
    {
        var probe = PlaygroundSequences.EditProbe;
        Assert.AreEqual(PlaygroundSequences.EditVoxelId, session.Query(probe).Voxel!.DefinitionId);

        var after = observer.Present(Frame, session.QueryDisplayPoses());

        Assert.IsNull(observer.Selection);
        Assert.IsNull(observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        var lost = observer.Notices.Single(item => item.Kind == PlaygroundNoticeKind.TargetLost);
        Assert.AreEqual(ObservedObjectKind.WorldCell, lost.Object.Kind);
        Assert.AreEqual(probe, lost.Object.Cell);
        Assert.AreEqual(PlaygroundCameraMode.Focus, lost.Mode);
        Assert.IsTrue(observer.Notices.Any(item => item.Kind == PlaygroundNoticeKind.SelectionLost));
        AssertClose(held.Position, after.Position);
        AssertClose(Unit(held.Look - held.Position), Unit(after.Look - after.Position));
    }

    [TestMethod]
    public void Vehicle_ray_hits_the_displayed_body_and_misses_empty_space()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var assembly = session.QueryTrainAssemblies().Single();
        var sample = session.QueryDisplayPoses();
        var loco = PlaygroundObserver.VehicleFrame(session, sample, assembly.VehicleIds[0])!;
        var carriage = PlaygroundObserver.VehicleFrame(session, sample, assembly.VehicleIds[1])!;

        var down = new ObservationVector(0, -1, 0);
        var locoHit = PlaygroundInspector.RaycastVehicles(session, sample, loco.Center + new ObservationVector(0, 20, 0), down, 100);
        Assert.AreEqual(assembly.VehicleIds[0], locoHit!.VehicleId);
        var carriageHit = PlaygroundInspector.RaycastVehicles(session, sample, carriage.Center + new ObservationVector(0, 20, 0), down, 100);
        Assert.AreEqual(assembly.VehicleIds[1], carriageHit!.VehicleId);
        Assert.IsTrue(carriageHit.Distance > 10 && carriageHit.Distance < 20);

        var along = PlaygroundInspector.RaycastVehicles(session, sample, carriage.Center - new ObservationVector(40, 0, 0), new ObservationVector(1, 0, 0), 200);
        Assert.AreEqual(assembly.VehicleIds[1], along!.VehicleId);
        Assert.IsNull(PlaygroundInspector.RaycastVehicles(session, sample, loco.Center + new ObservationVector(0, 20, 0), new ObservationVector(0, 1, 0), 100));
        Assert.IsNull(PlaygroundInspector.RaycastVehicles(session, sample, loco.Center + new ObservationVector(0, 20, 0), down, 5));
    }

    [TestMethod]
    public void Inspection_reports_ownership_durability_eligibility_and_existing_failures()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var assembly = session.QueryTrainAssemblies().Single();
        var (loco, carriage) = (assembly.VehicleIds[0], assembly.VehicleIds[1]);

        var fresh = PlaygroundInspector.Inspect(session, ObservedObject.Vehicle(loco))!;
        StringAssert.Contains(fresh.Identity, "core:dev-locomotive");
        Assert.AreEqual($"assembly {assembly.Id.Value} position 1/2 active-locomotive", fresh.Owner);
        StringAssert.Contains(fresh.Eligibility, "Operational · start Started");
        StringAssert.Contains(fresh.Durability, "damaged 0");
        Assert.AreEqual(0, fresh.Failures.Count);

        session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(carriage, new LocalVoxelCoord(3, 0, 1), 40) }));
        var dented = PlaygroundInspector.Inspect(session, ObservedObject.Vehicle(carriage))!;
        StringAssert.Contains(dented.Durability, "damaged 1");
        Assert.AreEqual($"assembly {assembly.Id.Value} position 2/2", dented.Owner);

        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        StringAssert.Contains(PlaygroundInspector.Inspect(session, ObservedObject.Vehicle(loco))!.Eligibility, "running");
        session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(StraightRailFixture.FirstSegmentDamageCell, 100) }));
        Assert.IsTrue(session.AdvanceSimulation(2000).Succeeded);
        var halted = PlaygroundInspector.Inspect(session, ObservedObject.Vehicle(loco))!;
        CollectionAssert.Contains(halted.Failures.ToArray(), "halt ForwardGap");
        CollectionAssert.Contains(halted.Failures.ToArray(), "start DisconnectedPath");

        var segment = session.Rails.Segments.OrderBy(item => item.Id.Value).First();
        var rail = PlaygroundInspector.Inspect(session, ObservedObject.Segment(segment.Id))!;
        Assert.IsTrue(rail.Failures.Any(item => item.StartsWith("missing cells ", StringComparison.Ordinal)), string.Join("; ", rail.Failures));
        StringAssert.Contains(rail.Owner, "connections");

        session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(loco, StaticTrainFixture.LocomotiveRearCouplerCell, 100) }));
        var coasting = PlaygroundInspector.Inspect(session, ObservedObject.Vehicle(carriage))!;
        CollectionAssert.Contains(coasting.Failures.ToArray(), "assembly has no active locomotive");
        CollectionAssert.Contains(coasting.Failures.ToArray(), "start MissingPropulsion");
        Assert.IsNull(PlaygroundInspector.Inspect(session, ObservedObject.Vehicle(new EntityId(999_999))));
    }

    [TestMethod]
    public void Observation_never_changes_authority_clock_input_order_or_saved_state()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelAddEdit(PlaygroundSequences.EditProbe, PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity), 305).Status);
        var before = Capture(session);

        var observer = new PlaygroundObserver(session, Overview, OverviewLook);
        var assembly = session.QueryTrainAssemblies().Single();
        foreach (var item in PlaygroundInspector.List(session))
        {
            observer.Select(item.Object);
            PlaygroundInspector.Inspect(session, item.Object);
        }

        observer.Select(ObservedObject.Vehicle(assembly.VehicleIds[1]));
        observer.FocusSelection();
        PresentFrames(observer, session, 20);
        observer.FollowSelection();
        observer.Look(0.4, -0.2);
        observer.Zoom(3);
        PresentFrames(observer, session, 40);
        PlaygroundInspector.RaycastVehicles(session, session.QueryDisplayPoses(), Overview, OverviewLook - Overview, 200);
        session.QueryStartEligibility(assembly.Id);
        observer.Free();
        observer.Fly(3, 1, 2);
        PresentFrames(observer, session, 5);

        Assert.AreEqual(before, Capture(session));
        Assert.AreEqual(0UL, session.CollectHostSteps(1.0));
        var saved = session.Save("Checkpoint");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var json = File.ReadAllText(playground.Store.PathFor(saved.SaveId!.Value));
        foreach (var key in new[] { "observ", "camera", "select", "follow", "focus" })
            Assert.IsFalse(json.Contains(key, StringComparison.OrdinalIgnoreCase), $"save mentions {key}");
    }

    [TestMethod]
    public void Same_inputs_reach_the_same_state_with_or_without_an_observer_following()
    {
        using var content = PlaygroundHarness.Content();
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.StraightRailRunAndHaltId)!;
        var plain = PlaygroundValidation.Run(sequence, content.Catalog, content.Composites);
        Assert.IsTrue(plain.Passed);

        Assert.AreEqual(
            PlaygroundEstablishStatus.Established,
            PlaygroundSession.TryEstablish(sequence.ConfigurationId, content.Catalog, content.Composites, null, out var observed));
        content.Attach(observed!);
        var observer = new PlaygroundObserver(observed!, Overview, OverviewLook);
        observer.Select(ObservedObject.Vehicle(observed!.QueryTrainAssemblies().Single().VehicleIds[0]));
        observer.FollowSelection();
        foreach (var step in sequence.Steps)
        {
            PresentFrames(observer, observed, 3);
            Assert.AreEqual(step.ExpectedStatus, step.Operation.Apply(observed));
            PresentFrames(observer, observed, 3);
        }

        Assert.AreEqual(plain.FinalFingerprint, WorldSaveAuthority.Fingerprint(observed));
    }

    private static string Capture(VoxelDevelopmentSession session) => string.Join('|',
        WorldSaveAuthority.Fingerprint(session),
        session.SimulationStep,
        session.SimulationPaused,
        session.TransactionCount,
        session.NextEntityId,
        session.NextEditId,
        session.EditBoundary,
        session.EditLogDigest,
        session.AssemblyGraphGeneration,
        session.DisplayInterpolationAlpha,
        session.FollowedVehicleId?.Value);

    private static ObservationCameraPose[] PresentFrames(PlaygroundObserver observer, VoxelDevelopmentSession session, int frames) =>
        Enumerable.Range(0, frames).Select(_ => observer.Present(Frame, session.QueryDisplayPoses())).ToArray();

    private static void AssertClose(ObservationVector expected, ObservationVector actual, double tolerance = 1e-6)
    {
        Assert.AreEqual(expected.X, actual.X, tolerance, $"x expected {expected} actual {actual}");
        Assert.AreEqual(expected.Y, actual.Y, tolerance, $"y expected {expected} actual {actual}");
        Assert.AreEqual(expected.Z, actual.Z, tolerance, $"z expected {expected} actual {actual}");
    }

    private static ObservationVector Unit(ObservationVector value) => value * (1 / value.Length);
}

internal static class PlaygroundScenePoints
{
    public static readonly WorldVoxelCoord EmptyProbe = new(100, 10, 15);
}

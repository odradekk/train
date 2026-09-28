using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class RailCommandTests
{
    private const double Tolerance = 1e-9;
    private static readonly WorldVoxelCoord Probe = new(60, 3, 20);
    private static readonly VoxelDefinitionId Ore = new("core", "ferrous-ore");

    [TestMethod]
    public void Paused_rail_removal_repair_and_explicit_start_take_consecutive_positions_at_one_boundary()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var cells = FirstSegmentCells(session);
        var front = session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X;

        var removed = session.SubmitEdit(DestroyAll(cells));
        var refused = session.StartTrain(RailHeading.AlongAlignment);
        var repaired = session.RepairRails(cells);
        var started = session.StartTrain(RailHeading.AlongAlignment);

        Assert.AreEqual(WorldEditState.Executed, removed.Record!.State);
        Assert.AreEqual(TrainMotionStatus.InsufficientSupport, refused.Status);
        Assert.AreEqual(RailRepairStatus.Repaired, repaired.Status);
        Assert.AreEqual(TrainMotionStatus.Started, started.Status);
        var log = session.QueryEditLog();
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4 }, log.Select(record => record.Id).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 4).Select(index => (SimulationPosition?)new SimulationPosition(0, (ulong)index)).ToArray(),
            log.Select(record => record.ExecutedAt).ToArray());
        CollectionAssert.AreEqual(
            new[] { "voxel.damage", "train.start", "rail.repair", "train.start" },
            log.Select(record => record.Edit.WireKind).ToArray());
        CollectionAssert.AreEqual(
            new[] { WorldEditState.Executed, WorldEditState.Rejected, WorldEditState.Executed, WorldEditState.Executed },
            log.Select(record => record.State).ToArray());
        Assert.AreEqual(nameof(TrainMotionStatus.InsufficientSupport), log[1].Status);
        Assert.AreEqual(nameof(RailRepairStatus.Repaired), log[2].Status);

        Assert.AreEqual(0UL, session.SimulationStep);
        Assert.IsTrue(session.SimulationPaused);
        var parked = session.QueryTrainAssemblies().Single();
        Assert.IsTrue(parked.Running);
        Assert.AreEqual(0.0, parked.Speed, Tolerance);
        Assert.AreEqual(front, parked.Vehicles[0].FrontContact.X);
        Assert.AreEqual(TrainHaltReason.None, parked.HaltReason);

        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.IsTrue(session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X > front + Tolerance);
    }

    [TestMethod]
    public void Paused_structure_and_train_edits_queued_for_a_boundary_match_the_same_edits_submitted_there()
    {
        using var queued = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var cells = FirstSegmentCells(queued.Session);
        Assert.AreEqual("Pending", queued.Session.SubmitEdit(DestroyAll(cells), 3).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, queued.Session.StartTrain(RailHeading.AlongAlignment, 3).Status);
        Assert.AreEqual("Pending", queued.Session.SubmitEdit(new RailRepairEdit(cells), 3).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, queued.Session.StartTrain(RailHeading.AlongAlignment, 3).Status);
        Assert.AreEqual(4, queued.Session.QueryPendingEdits().Count);
        Assert.AreEqual(2, queued.Session.QueryScheduledTrainCommands().Count);
        Assert.IsTrue(queued.Session.AdvanceSimulation(3).Succeeded);

        using var immediate = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        Assert.IsTrue(immediate.Session.AdvanceSimulation(3).Succeeded);
        immediate.Session.SubmitEdit(DestroyAll(cells));
        Assert.AreEqual(TrainMotionStatus.InsufficientSupport, immediate.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.IsTrue(immediate.Session.RepairRails(cells).Succeeded);
        Assert.AreEqual(TrainMotionStatus.Started, immediate.Session.StartTrain(RailHeading.AlongAlignment).Status);

        var log = queued.Session.QueryEditLog();
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 4).Select(index => (SimulationPosition?)new SimulationPosition(3, (ulong)index)).ToArray(),
            log.Select(record => record.ExecutedAt).ToArray());
        CollectionAssert.AreEqual(
            immediate.Session.QueryEditLog().Select(record => (record.Edit, record.State, record.ExecutedAt, record.Status)).ToArray(),
            log.Select(record => (record.Edit, record.State, record.ExecutedAt, record.Status)).ToArray());
        Assert.AreEqual(immediate.Session.CaptureTrainAuthority(), queued.Session.CaptureTrainAuthority());
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(immediate.Session), WorldSaveAuthority.Fingerprint(queued.Session));
        Assert.AreEqual(0, queued.Session.QueryScheduledTrainCommands().Count);
    }

    [TestMethod]
    public void Running_same_boundary_train_and_rail_edits_are_independent_of_advance_splits_and_display_rate()
    {
        var splits = new Func<VoxelDevelopmentSession, bool>[]
        {
            session => session.AdvanceSimulation(100).Succeeded,
            session => Enumerable.Range(0, 100).All(_ => session.AdvanceSimulation(1).Succeeded),
            session => session.AdvanceSimulation(30).Succeeded && session.AdvanceSimulation(20).Succeeded && session.AdvanceSimulation(50).Succeeded,
            session => DriveHost(session, 1.0 / 144.0, 100),
            session => DriveHost(session, 1.0 / 30.0, 100)
        };
        var runs = new List<(string Digest, string Fingerprint, TrainAuthoritySnapshot Train, IReadOnlyList<WorldEditRecord> Log)>();
        foreach (var split in splits)
        {
            using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
            var session = playground.Session;
            Assert.AreEqual(TrainMotionStatus.Started, session.StartTrain(RailHeading.AlongAlignment).Status);
            Assert.AreEqual(TrainMotionStatus.Pending, session.StopTrain(50).Status);
            Assert.AreEqual("Pending", session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(StraightRailFixture.FirstSegmentDamageCell, 100) }), 50).Status);
            Assert.AreEqual("Pending", session.SubmitEdit(new RailRepairEdit(new[] { StraightRailFixture.FirstSegmentDamageCell }), 50).Status);
            Assert.AreEqual(TrainMotionStatus.Pending, session.StartTrain(RailHeading.AlongAlignment, 50).Status);
            Assert.AreEqual(TrainMotionStatus.Pending, session.StopTrain(60).Status);
            Assert.IsTrue(split(session));
            Assert.AreEqual(100UL, session.SimulationStep);
            runs.Add((session.EditLogDigest, WorldSaveAuthority.Fingerprint(session), session.CaptureTrainAuthority(), session.QueryEditLog()));
        }

        var log = runs[0].Log;
        CollectionAssert.AreEqual(
            new SimulationPosition?[] { new(0, 0), new(50, 0), new(50, 1), new(50, 2), new(50, 3), new(60, 0) },
            log.Select(record => record.ExecutedAt).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Started", "Stopped", "Removed", "Repaired", "Started", "Stopped" },
            log.Select(record => record.Status).ToArray());
        foreach (var run in runs.Skip(1))
        {
            Assert.AreEqual(runs[0].Digest, run.Digest);
            Assert.AreEqual(runs[0].Fingerprint, run.Fingerprint);
            Assert.AreEqual(runs[0].Train, run.Train);
        }

        var halted = runs[0].Train.Assemblies.Single();
        Assert.IsFalse(halted.Running);
        Assert.AreEqual(0.0, halted.Speed, Tolerance);
    }

    [TestMethod]
    public void Pause_only_stops_automatic_advance_while_stop_changes_the_train()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(120).Succeeded);
        Assert.AreEqual(TrainMotionStatus.Pending, session.StopTrain(400).Status);
        var running = session.QueryTrainAssemblies().Single();
        var authority = session.CaptureTrainAuthority();
        var queue = session.CaptureEditQueue();

        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(0UL, session.CollectHostSteps(1.0));
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        Assert.IsTrue(session.PauseSimulation().Succeeded);
        Assert.AreEqual(0UL, session.CollectHostSteps(1.0));
        Assert.AreEqual(authority, session.CaptureTrainAuthority());
        Assert.AreEqual(queue.NextEditId, session.CaptureEditQueue().NextEditId);
        Assert.AreEqual(queue.Pending.Single(), session.CaptureEditQueue().Pending.Single());
        Assert.IsTrue(running.Running);
        Assert.IsTrue(running.Speed > 0);

        using var stepped = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        Assert.IsTrue(stepped.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(Enumerable.Range(0, 120).All(_ => stepped.Session.AdvanceSimulation(1).Succeeded));
        Assert.AreEqual(TrainMotionStatus.Pending, stepped.Session.StopTrain(400).Status);
        Assert.AreEqual(authority, stepped.Session.CaptureTrainAuthority());

        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.IsTrue(session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X > running.Vehicles[0].FrontContact.X + Tolerance);
        Assert.IsTrue(session.SimulationPaused);

        var stop = session.StopTrain();
        Assert.AreEqual(TrainMotionStatus.Stopped, stop.Status);
        var stopped = session.QueryTrainAssemblies().Single();
        Assert.IsFalse(stopped.Running);
        Assert.AreEqual(0.0, stopped.Speed, Tolerance);
        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(1, session.QueryPendingEdits().Count);
    }

    [TestMethod]
    public void Motion_invariant_failure_halts_at_the_last_complete_boundary_without_an_edit_id()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        var carriage = session.QueryTrainAssemblies().Single().VehicleIds[1];
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity), 5).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, session.StopTrain(20).Status);
        session.EditFaultProbe = record =>
        {
            if (record.Edit is VoxelAddEdit)
                Vehicles(session).RemoveAll(vehicle => vehicle.Id == carriage);
        };

        var advanced = session.AdvanceSimulation(30);

        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, advanced.Status);
        Assert.AreEqual(5UL, advanced.AdvancedSteps);
        Assert.AreEqual(5UL, session.SimulationStep);
        var fault = session.Fault!;
        Assert.IsNull(fault.EditId);
        Assert.IsNull(fault.Kind);
        Assert.AreEqual(new SimulationPosition(5, 1), fault.Position);
        Assert.AreEqual("MotionInvariant", fault.ExceptionType);
        StringAssert.Contains(fault.Message, nameof(TrainMotionStatus.NoAssembly));
        Assert.AreEqual(new SimulationPosition(5, 1), session.EditBoundary);
        Assert.AreEqual(WorldEditState.Executed, session.QueryEditLog()[^1].State);
        Assert.AreEqual(1, session.QueryScheduledTrainCommands().Count);
        Assert.IsTrue(session.Query(Probe).IsOccupied);

        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, session.AdvanceSimulation(1).Status);
        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, session.StopTrain().Status);
        Assert.AreEqual(RailRepairStatus.SimulationFaulted, session.RepairRails(new[] { StraightRailFixture.FirstSegmentDamageCell }).Status);
        Assert.AreEqual(RailEditStatus.SimulationFaulted, session.ConstructRails().Status);
        Assert.AreEqual(TrainAssemblyStatus.SimulationFaulted, session.AssembleStaticTrain().Status);
        Assert.AreEqual(WorldSaveStatus.SimulationFaulted, session.Save("Motion fault").Status);
        WriteOre(playground, maxDurability: 150);
        Assert.AreEqual(ContentReloadStatus.Rejected, playground.Reloader.Reload().Status);
        Assert.AreEqual(5UL, session.SimulationStep);
    }

    [TestMethod]
    public void Faulted_train_command_rolls_back_its_transaction_and_motion_flags()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var before = session.CaptureTrainAuthority();
        var transactions = session.TransactionCount;
        session.EditFaultProbe = record =>
        {
            if (record.Edit is TrainStartEdit)
                throw new InvalidOperationException("injected");
        };

        var start = session.StartTrain(RailHeading.AlongAlignment);

        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, start.Status);
        Assert.AreEqual(1UL, start.EditId);
        var after = session.CaptureTrainAuthority();
        Assert.AreEqual(before.Composites, after.Composites);
        Assert.AreEqual(before.Rails, after.Rails);
        CollectionAssert.AreEqual(before.Assemblies.ToArray(), after.Assemblies.ToArray());
        Assert.AreEqual(1UL, session.QueryPendingEdits().Single().Id);
        Assert.AreEqual(transactions, session.TransactionCount);
        Assert.IsFalse(session.QueryTrainAssemblies().Single().Running);
        Assert.AreEqual(1UL, session.Fault!.EditId);
        Assert.AreEqual(WorldEditKind.TrainStart, session.Fault.Kind);
        Assert.AreEqual(WorldEditState.Faulted, session.QueryEditLog().Single().State);
    }

    [TestMethod]
    public void Mixed_rail_and_train_queue_restores_in_a_new_host_with_a_fully_destroyed_referenced_segment()
    {
        using var live = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = live.Session;
        var cells = FirstSegmentCells(session);
        var firstSegment = session.Rails.Segments[0].Id;
        var assembly = session.QueryTrainAssemblies().Single().Id;
        Assert.AreEqual("Pending", session.SubmitEdit(DestroyAll(cells), 10).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, session.StartTrain(RailHeading.AlongAlignment, 10).Status);
        Assert.AreEqual("Pending", session.SubmitEdit(new RailRepairEdit(cells), 20).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, session.StartTrain(RailHeading.AlongAlignment, 20).Status);
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity), 20).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, session.StopTrain(200).Status);
        Assert.IsTrue(session.AdvanceSimulation(12).Succeeded);

        var destroyed = session.QueryTrainAssemblies().Single();
        Assert.AreEqual(firstSegment, destroyed.Reference.SegmentId);
        Assert.AreEqual(TrainHaltReason.SupportLost, destroyed.HaltReason);
        Assert.AreEqual(0, session.Rails.TraversableWorldX(firstSegment, session.World).Count);
        Assert.AreEqual(nameof(TrainMotionStatus.InsufficientSupport), session.QueryEditLog()[^1].Status);
        Assert.AreEqual(4, session.QueryPendingEdits().Count);

        var saved = session.Save("Mixed rail queue");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var path = live.Store.PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        CollectionAssert.AreEqual(
            new[] { "rail.repair", "train.start", "voxel.add", "train.stop" },
            JsonNode.Parse(json)!["world_edits"]!["pending"]!.AsArray().Select(item => item!["edit"]!["kind"]!.GetValue<string>()).ToArray());

        using var host = PlaygroundHarness.Content(userData: live.UserData);
        var restored = new VoxelDevelopmentSession(host.Catalog, VoxelWorldIdentity.Slice4, host.Store, host.Composites);
        host.Attach(restored);
        var mutated = JsonNode.Parse(json)!.AsObject();
        var start = mutated["world_edits"]!["pending"]![1]!["edit"]!.AsObject();
        start["assembly_id"] = mutated["next_entity_id"]!.GetValue<ulong>();
        File.WriteAllText(path, mutated.ToJsonString());
        Assert.AreEqual(WorldSaveStatus.InvalidDelta, restored.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(0, restored.QueryTrainAssemblies().Count);
        File.WriteAllText(path, json);

        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(restored));
        Assert.AreEqual(firstSegment, restored.QueryTrainAssemblies().Single().Reference.SegmentId);
        Assert.AreEqual(assembly, restored.QueryScheduledTrainCommands()[0].AssemblyId);
        Assert.AreEqual(session.EditBoundary, restored.EditBoundary);

        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        Assert.IsTrue(restored.AdvanceSimulation(150).Succeeded && restored.AdvanceSimulation(150).Succeeded);

        Assert.AreEqual(WorldSaveAuthority.Fingerprint(session), WorldSaveAuthority.Fingerprint(restored));
        Assert.AreEqual(session.CaptureTrainAuthority(), restored.CaptureTrainAuthority());
        var liveTail = session.QueryEditLog().Skip(session.QueryEditLog().Count - 4).Select(record => record.Canonical).ToArray();
        CollectionAssert.AreEqual(liveTail, restored.QueryEditLog().Select(record => record.Canonical).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Repaired", "Started", "Added", "Stopped" },
            restored.QueryEditLog().Select(record => record.Status).ToArray());
        CollectionAssert.AreEqual(
            new SimulationPosition?[] { new(20, 0), new(20, 1), new(20, 2), new(200, 0) },
            restored.QueryEditLog().Select(record => record.ExecutedAt).ToArray());
        Assert.AreEqual(0, restored.QueryPendingEdits().Count);
        Assert.AreEqual(firstSegment, restored.QueryTrainAssemblies().Single().Reference.SegmentId);

        Assert.IsTrue(restored.AdvanceSimulation(50).Succeeded);
        Assert.AreEqual(4, restored.QueryEditLog().Count);
    }

    [TestMethod]
    public void World_changing_content_publication_is_recorded_at_its_boundary_and_refused_on_a_faulted_session()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(3).Succeeded);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(Probe, Ore, VoxelOrientation.Identity).Status);

        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);
        Assert.AreEqual(2, session.QueryEditLog().Count);

        WriteOre(playground, maxDurability: 150);
        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);
        var published = session.QueryEditLog()[^1];
        Assert.IsInstanceOfType<ContentPublishEdit>(published.Edit);
        Assert.AreEqual(ContentPublishEdit.For(playground.Catalog.Current!, playground.Composites.Current), published.Edit);
        Assert.AreEqual(new SimulationPosition(3, 1), published.AcceptedAt);
        Assert.AreEqual(published.AcceptedAt, published.ExecutedAt);
        Assert.AreEqual(WorldEditState.Executed, published.State);
        Assert.AreEqual("Published", published.Status);
        Assert.AreEqual(150, session.Query(Probe).MaxDurability);
        Assert.AreEqual(new SimulationPosition(3, 2), session.EditBoundary);

        var beforeDisplayChange = playground.Catalog.Current;
        WriteOre(playground, maxDurability: 150, displayName: "Iron Ore");
        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);
        Assert.AreNotSame(beforeDisplayChange, playground.Catalog.Current);
        Assert.AreSame(published, session.QueryEditLog()[^1]);
        Assert.AreEqual(new SimulationPosition(3, 2), session.EditBoundary);
        Assert.AreEqual(WorldEditRefusal.None, session.SubmitEdit(new VoxelRemoveEdit(Probe)).Refusal);

        var catalog = playground.Catalog.Current;
        session.EditFaultProbe = record =>
        {
            if (record.Edit is ContentPublishEdit)
                throw new InvalidOperationException("injected publication failure");
        };
        WriteOre(playground, maxDurability: 60);
        Assert.ThrowsException<InvalidOperationException>(() => playground.Reloader.Reload());
        Assert.AreSame(catalog, playground.Catalog.Current);
        Assert.AreEqual(WorldEditKind.ContentPublish, session.Fault!.Kind);
        Assert.AreEqual(WorldEditState.Faulted, session.QueryEditLog()[^1].State);
        Assert.AreEqual(new SimulationPosition(3, 3), session.QueryEditLog()[^1].ExecutedAt);
        Assert.IsFalse(session.Query(Probe).IsOccupied);

        session.EditFaultProbe = null;
        var rejected = playground.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.SimulationUnavailable));
        Assert.AreSame(catalog, playground.Catalog.Current);
    }

    [TestMethod]
    public void Running_train_takes_the_candidate_speed_limit_inside_the_publication_before_it_commits()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(4d, Speed(session));

        var logged = session.QueryEditLog().Count;
        double? dispatched = null;
        int? loggedAtDispatch = null;
        session.EditFaultProbe = record =>
        {
            if (record.Edit is not ContentPublishEdit)
                return;
            dispatched = Speed(session);
            loggedAtDispatch = session.QueryEditLog().Count;
        };
        WritePropulsion(playground, targetSpeed: 2);

        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);
        Assert.AreEqual(2d, dispatched);
        Assert.AreEqual(logged, loggedAtDispatch);
        Assert.AreEqual(2d, Speed(session));
        Assert.IsTrue(session.QueryTrainAssemblies().Single().Running);
        var published = session.QueryEditLog()[^1];
        Assert.IsInstanceOfType<ContentPublishEdit>(published.Edit);
        Assert.AreEqual("Published", published.Status);
        Assert.AreEqual(new SimulationPosition(300, 0), published.ExecutedAt);
    }

    [TestMethod]
    public void Publication_fault_after_candidate_adaptation_keeps_the_old_catalog_world_and_speed()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(Probe, Ore, VoxelOrientation.Identity).Status);
        var catalog = playground.Catalog.Current;
        var composites = playground.Composites.Current;

        double? adaptedSpeed = null;
        int? adaptedDurability = null;
        session.EditFaultProbe = record =>
        {
            if (record.Edit is not ContentPublishEdit)
                return;
            adaptedSpeed = Speed(session);
            adaptedDurability = session.Query(Probe).Voxel!.CurrentDurability;
            throw new InvalidOperationException("injected publication failure");
        };
        WritePropulsion(playground, targetSpeed: 2);
        WriteOre(playground, maxDurability: 60);

        Assert.ThrowsException<InvalidOperationException>(() => playground.Reloader.Reload());
        Assert.AreEqual(2d, adaptedSpeed);
        Assert.AreEqual(60, adaptedDurability);
        Assert.AreSame(catalog, playground.Catalog.Current);
        Assert.AreSame(composites, playground.Composites.Current);
        Assert.AreEqual(4d, Speed(session));
        Assert.IsTrue(session.QueryTrainAssemblies().Single().Running);
        Assert.AreEqual(100, session.Query(Probe).Voxel!.CurrentDurability);
        Assert.AreEqual(WorldEditKind.ContentPublish, session.Fault!.Kind);
        Assert.AreEqual(WorldEditState.Faulted, session.QueryEditLog()[^1].State);
    }

    [TestMethod]
    public void Content_publication_outside_the_edit_order_is_refused_by_the_generic_entry()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var edit = ContentPublishEdit.For(playground.Catalog.Current!, playground.Composites.Current);
        Assert.ThrowsException<ArgumentException>(() => playground.Session.SubmitEdit(edit));
        Assert.AreEqual(1UL, playground.Session.NextEditId);
    }

    [TestMethod]
    public void Console_schedules_rail_edits_and_reports_train_edit_ids()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        using var logger = LoggerSession.Create();
        var session = playground.Session;
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, session);
        var cell = StraightRailFixture.FirstSegmentDamageCell;
        var text = $"{cell.X} {cell.Y} {cell.Z}";

        dispatcher.Execute("rail start at 4");
        dispatcher.Execute($"voxel damage {text} 100 at 2");
        dispatcher.Execute($"rail repair {text} at 3");
        dispatcher.Execute("rail stop 6");
        dispatcher.Execute("rail construct at 1");
        dispatcher.Execute("rail assemble at 1");
        dispatcher.Execute("rail query motion");
        dispatcher.Execute("rail step 7");
        dispatcher.Execute("rail start");
        dispatcher.Execute("rail start at 3");
        dispatcher.Execute("voxel edits log 8");

        var log = session.QueryEditLog();
        CollectionAssert.AreEqual(
            new[] { "rail.construct", "train.assemble", "voxel.damage", "rail.repair", "train.start", "train.stop", "train.start" },
            log.Select(record => record.Edit.WireKind).ToArray());
        CollectionAssert.AreEqual(
            new[] { "IndependentLineConflict", "AlreadyAssembled", "Removed", "Repaired", "Started", "Stopped", "Started" },
            log.Select(record => record.Status).ToArray());
        CollectionAssert.AreEqual(
            new SimulationPosition?[] { new(1, 0), new(1, 1), new(2, 0), new(3, 0), new(4, 0), new(6, 0), new(7, 0) },
            log.Select(record => record.ExecutedAt).ToArray());
        var messages = WorldEditEntryTests.Messages(logger);
        StringAssert.Contains(messages, "Rail Pending edit 1 step 0 at 4.");
        StringAssert.Contains(messages, "Rail Pending edit 4 step 0 at 6.");
        StringAssert.Contains(messages, "Edit 5 rail.construct pending");
        StringAssert.Contains(messages, "pending start 4 assembly");
        StringAssert.Contains(messages, " edit 1");
        StringAssert.Contains(messages, "Rail Started edit 7 step 7 at 7.");
        StringAssert.Contains(messages, "Rail PastStep.");
        Assert.AreEqual(8UL, session.NextEditId);
    }

    private static VoxelDamageEdit DestroyAll(IEnumerable<WorldVoxelCoord> cells) =>
        new(cells.Select(cell => new VoxelDamageTarget(cell, 100)).ToArray());

    private static IReadOnlyList<WorldVoxelCoord> FirstSegmentCells(VoxelDevelopmentSession session)
    {
        var first = session.Rails.Segments[0].Id;
        return session.QueryRails().Occupancy
            .Where(item => item.Supports.Any(support => support.SegmentId == first) && session.Query(item.Cell).IsOccupied)
            .Select(item => item.Cell)
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ThenBy(item => item.Z)
            .ToArray();
    }

    private static bool DriveHost(VoxelDevelopmentSession session, double delta, ulong target)
    {
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        while (session.SimulationStep < target)
        {
            var steps = Math.Min(target - session.SimulationStep, session.CollectHostSteps(delta));
            if (!session.AdvanceSimulation(steps).Succeeded)
                return false;
        }

        return session.PauseSimulation().Succeeded;
    }

    private static List<CompositeInstance> Vehicles(VoxelDevelopmentSession session) =>
        (List<CompositeInstance>)typeof(VoxelDevelopmentSession)
            .GetField("_vehicles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;

    private static double Speed(VoxelDevelopmentSession session) =>
        session.QueryTrainAssemblies().Single().Speed;

    private static void WritePropulsion(PlaygroundHarness harness, double targetSpeed) =>
        harness.Source.AddVoxelFile("core", "voxels/propulsion.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "propulsion",
                "开发动力件",
                "切片 4.2 最小动力行为零件。能力由已注册 propulsion 行为推导，不表示燃料或通用资源。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: $"[{{\"type\":\"propulsion\",\"target_speed\":{targetSpeed.ToString(CultureInfo.InvariantCulture)},\"acceleration\":2}}]")));

    private static void WriteOre(PlaygroundHarness harness, int maxDurability, string displayName = "Ferrous Ore") =>
        harness.Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", displayName, "说明", value: 250, mass: 2, maxDurability: maxDurability)));
}

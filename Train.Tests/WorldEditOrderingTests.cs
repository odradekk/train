using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
public sealed class WorldEditOrderingTests
{
    internal static readonly WorldVoxelCoord Probe = new(60, 3, 20);
    internal static readonly VoxelDefinitionId Ore = new("core", "ferrous-ore");
    internal static readonly CompositeTransform ShellPose = CompositeTransform.Translation(60, 6, 24);

    [TestMethod]
    public void Paused_edits_take_consecutive_in_step_sequences_without_moving_clock_or_train()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = worldEditor.Session;
        var start = session.StartTrain(RailHeading.AlongAlignment);
        Assert.AreEqual(TrainMotionStatus.Started, start.Status);
        Assert.AreEqual(1UL, start.EditId);
        var front = session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X;

        var submissions = new[]
        {
            session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity)),
            session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(Probe, 25) }), confirmEndHistory: true),
            session.SubmitEdit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, ShellPose), confirmEndHistory: true)
        };
        var shell = ((CompositeManufactureResult)submissions[2].Result!).VehicleId!.Value;
        submissions = submissions.Concat(new[]
        {
            session.SubmitEdit(new VehicleRefitEdit(shell, new[] { CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity) }), confirmEndHistory: true),
            session.SubmitEdit(new VehicleDamageEdit(new[] { new CompositeDamageTarget(shell, HollowShellRefit.FirstAdd, 25) }), confirmEndHistory: true),
            session.SubmitEdit(new VoxelRemoveEdit(Probe))
        }).ToArray();

        CollectionAssert.AreEqual(
            new[] { "Added", "Damaged", "Valid", "Valid", "Damaged", "Removed" },
            submissions.Select(item => item.Status).ToArray());
        for (var index = 0; index < submissions.Length; index++)
        {
            var record = submissions[index].Record!;
            Assert.AreEqual((ulong)index + 2, record.Id);
            Assert.AreEqual(new SimulationPosition(0, (ulong)index + 1), record.AcceptedAt);
            Assert.AreEqual(record.AcceptedAt, record.ExecutedAt);
            Assert.AreEqual(WorldEditState.Executed, record.State);
        }

        Assert.AreEqual(0UL, session.SimulationStep);
        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(front, session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X);
        Assert.AreEqual(new SimulationPosition(0, 7), session.EditBoundary);
        Assert.AreEqual(8UL, session.NextEditId);
        var log = session.QueryEditLog();
        Assert.IsInstanceOfType<TrainStartEdit>(log[0].Edit);
        Assert.AreEqual(new SimulationPosition(0, 0), log[0].ExecutedAt);
        CollectionAssert.AreEqual(submissions.Select(item => item.Record).ToArray(), log.Skip(1).ToArray());

        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.AreEqual(new SimulationPosition(1, 0), session.EditBoundary);
    }

    [TestMethod]
    public void Scheduled_edits_run_at_their_boundary_in_acceptance_order_however_the_advance_is_split()
    {
        var runs = new List<(string Digest, string Fingerprint, IReadOnlyList<WorldEditRecord> Log)>();
        foreach (var split in new Func<VoxelDevelopmentSession, bool>[]
                 {
                     session => session.AdvanceSimulation(10).Succeeded,
                     session => Enumerable.Range(0, 10).All(_ => session.AdvanceSimulation(1).Succeeded),
                     session => session.AdvanceSimulation(3).Succeeded && session.AdvanceSimulation(2).Succeeded && session.AdvanceSimulation(5).Succeeded
                 })
        {
            using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
            var session = worldEditor.Session;
            Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
            Assert.AreEqual("Pending", session.SubmitEdit(new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity), 5).Status);
            Assert.IsTrue(session.AdvanceSimulation(2).Succeeded);
            Assert.AreEqual("Pending", session.SubmitEdit(new VoxelRemoveEdit(Probe), 5).Status);
            Assert.AreEqual("Pending", session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(Probe, 25) }), 5).Status);
            Assert.AreEqual(3, session.QueryPendingEdits().Count);
            Assert.IsFalse(session.Query(Probe).IsOccupied);
            Assert.IsTrue(split(session));
            Assert.AreEqual(12UL, session.SimulationStep);
            Assert.AreEqual(0, session.QueryPendingEdits().Count);
            runs.Add((session.EditLogDigest, WorldSaveAuthority.Fingerprint(session), session.QueryEditLog()));
        }

        Assert.IsInstanceOfType<TrainStartEdit>(runs[0].Log[0].Edit);
        var log = runs[0].Log.Skip(1).ToArray();
        CollectionAssert.AreEqual(new ulong[] { 2, 3, 4 }, log.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { new SimulationPosition(0, 1), new SimulationPosition(2, 0), new SimulationPosition(2, 1) },
            log.Select(item => item.AcceptedAt).ToArray());
        CollectionAssert.AreEqual(
            new SimulationPosition?[] { new(5, 0), new(5, 1), new(5, 2) },
            log.Select(item => item.ExecutedAt).ToArray());
        CollectionAssert.AreEqual(new[] { "Added", "Removed", "Empty" }, log.Select(item => item.Status).ToArray());
        CollectionAssert.AreEqual(
            new[] { WorldEditState.Executed, WorldEditState.Executed, WorldEditState.Rejected },
            log.Select(item => item.State).ToArray());
        foreach (var run in runs.Skip(1))
        {
            Assert.AreEqual(runs[0].Digest, run.Digest);
            Assert.AreEqual(runs[0].Fingerprint, run.Fingerprint);
        }
    }

    [TestMethod]
    public void Running_modification_at_an_explicit_boundary_matches_the_same_edit_submitted_there_while_paused()
    {
        using var content = WorldEditorHarness.Content();
        var reference = WorldEditorValidation.Run(
            WorldEditorSequences.Find(WorldEditorSequences.StraightRailRunAndHaltId)!,
            content.Catalog,
            content.Composites);
        Assert.IsTrue(reference.Passed, string.Join(Environment.NewLine, reference.Lines()));

        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = worldEditor.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        var scheduled = session.SubmitEdit(
            new VoxelDamageEdit(new[] { new VoxelDamageTarget(StraightRailFixture.FirstSegmentDamageCell, 100) }),
            300);
        Assert.AreEqual(WorldEditState.Pending, scheduled.Record!.State);
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        var remaining = 2300UL;
        while (remaining > 0)
        {
            var steps = Math.Min(remaining, session.CollectHostSteps(0.7));
            Assert.IsTrue(session.AdvanceSimulation(steps).Succeeded);
            remaining -= steps;
        }

        Assert.IsTrue(session.PauseSimulation().Succeeded);
        var executed = session.QueryEditLog().Single(record => record.Edit is VoxelDamageEdit);
        Assert.AreEqual(new SimulationPosition(300, 0), executed.ExecutedAt);
        Assert.AreEqual("Removed", executed.Status);
        Assert.AreEqual(TrainHaltReason.ForwardGap, session.QueryTrainAssemblies().Single().HaltReason);
        Assert.AreEqual(reference.FinalFingerprint, WorldSaveAuthority.Fingerprint(session));
    }

    [TestMethod]
    public void Expected_rejections_dequeue_in_place_later_edits_continue_and_nothing_is_retried()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = worldEditor.Session;
        var bridge = session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var shell = session.Manufacture(HollowShellFixture.DefinitionId, ShellPose).VehicleId!.Value;
        var shellBefore = session.QueryComposite(shell);

        var other = new WorldVoxelCoord(61, 3, 20);
        session.SubmitEdit(new VehicleDamageEdit(new[]
        {
            new CompositeDamageTarget(bridge, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(bridge, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(bridge, BridgeFixture.RightCoreCell, 100)
        }), 2);
        var edits = new WorldEdit[]
        {
            new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity),
            new VoxelAddEdit(Probe, Ore, VoxelOrientation.Identity),
            new VehicleRefitEdit(bridge, new[] { CompositeRefitOperation.Add(new LocalVoxelCoord(3, 2, 1), HollowShellRefit.MarkerId, VoxelOrientation.Identity) }),
            new VehicleRefitEdit(shell, new[]
            {
                CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity),
                CompositeRefitOperation.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId, VoxelOrientation.Identity)
            }),
            new VoxelDamageEdit(new[] { new VoxelDamageTarget(Probe, 25) }),
            new VoxelRemoveEdit(other),
            new VoxelAddEdit(other, Ore, VoxelOrientation.Identity)
        };
        foreach (var edit in edits)
            Assert.AreEqual("Pending", session.SubmitEdit(edit, 4).Status);

        Assert.IsTrue(session.AdvanceSimulation(6).Succeeded);

        var log = session.QueryEditLog();
        CollectionAssert.AreEqual(
            new SimulationPosition?[] { new(0, 0), new(0, 1), new(2, 0) },
            log.Take(3).Select(item => item.ExecutedAt).ToArray());
        Assert.AreEqual("Removed", log[2].Status);
        var atFour = log.Skip(3).ToArray();
        CollectionAssert.AreEqual(
            new[] { "Added", "Occupied", "UnknownVehicle", "EnvelopeExceeded", "Damaged", "Empty", "Added" },
            atFour.Select(item => item.Status).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 7).Select(index => (SimulationPosition?)new SimulationPosition(4, (ulong)index)).ToArray(),
            atFour.Select(item => item.ExecutedAt).ToArray());
        Assert.AreEqual(0, session.QueryPendingEdits().Count);
        Assert.IsNull(session.QueryComposite(bridge));
        Assert.AreEqual(shellBefore!.Parts.Count, session.QueryComposite(shell)!.Parts.Count);
        Assert.IsFalse(session.QueryComposite(shell)!.Parts.Any(part => part.Cell == HollowShellRefit.FirstAdd));
        Assert.AreEqual(session.Query(Probe).MaxDurability - 25, session.Query(Probe).Voxel!.CurrentDurability);
        Assert.IsTrue(session.Query(other).IsOccupied);

        Assert.IsTrue(session.AdvanceSimulation(10).Succeeded);
        Assert.AreEqual(log.Count, session.QueryEditLog().Count);
    }

    [TestMethod]
    public void Internal_fault_restores_the_last_complete_boundary_and_halts_the_session()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = worldEditor.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(Probe, Ore, VoxelOrientation.Identity).Status);
        session.EditFaultProbe = record =>
        {
            if (record.Edit is VoxelRemoveEdit)
                throw new InvalidOperationException("injected invariant failure");
        };
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelRemoveEdit(Probe), 3, confirmEndHistory: true).Status);
        var before = session.CaptureAuthority();

        var advanced = session.AdvanceSimulation(10);

        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, advanced.Status);
        Assert.AreEqual(3UL, advanced.AdvancedSteps);
        Assert.AreEqual(3UL, session.SimulationStep);
        Assert.IsTrue(session.Query(Probe).IsOccupied);
        CollectionAssert.AreEqual(before.WorldVoxels.ToArray(), session.CaptureAuthority().WorldVoxels.ToArray());
        var fault = session.Fault!;
        Assert.AreEqual(3UL, fault.EditId);
        Assert.AreEqual(new SimulationPosition(3, 0), fault.Position);
        Assert.AreEqual(nameof(InvalidOperationException), fault.ExceptionType);
        Assert.AreEqual(WorldEditState.Faulted, session.QueryEditLog()[^1].State);
        Assert.AreEqual(3UL, session.QueryPendingEdits().Single().Id);
        var faultedFingerprint = WorldSaveAuthority.Fingerprint(session);

        Assert.AreEqual(WorldEditRefusal.SimulationFaulted, session.SubmitEdit(new VoxelRemoveEdit(Probe)).Refusal);
        Assert.AreEqual(WorldCommandStatus.SimulationFaulted, session.Remove(Probe).Status);
        Assert.AreEqual(ConstructionStatus.SimulationFaulted, session.Manufacture(HollowShellFixture.DefinitionId, ShellPose).Status);
        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, session.AdvanceSimulation(1).Status);
        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, session.StopTrain().Status);
        Assert.AreEqual(WorldSaveStatus.SimulationFaulted, session.Save("Faulted").Status);
        Assert.AreEqual(faultedFingerprint, WorldSaveAuthority.Fingerprint(session));

        session.EditFaultProbe = null;
        session.Reset();
        Assert.IsNull(session.Fault);
        Assert.AreEqual(1UL, session.NextEditId);
    }

    [TestMethod]
    public void Immediate_fault_rolls_back_a_composite_transaction_including_ids_and_transactions()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = worldEditor.Session;
        var before = session.CaptureAuthority();
        session.EditFaultProbe = _ => throw new ArgumentException("injected");

        var submission = session.SubmitEdit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, ShellPose));

        Assert.AreEqual(WorldEditState.Faulted, submission.Record!.State);
        Assert.AreEqual(nameof(ArgumentException), submission.Status);
        Assert.IsNull(submission.Result);
        Assert.AreEqual(before, session.CaptureAuthority());
        Assert.AreEqual(0, session.QueryComposites().Count);
    }

    [TestMethod]
    public void Past_steps_are_refused_without_consuming_ids_or_sequences()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = worldEditor.Session;
        Assert.IsTrue(session.AdvanceSimulation(5).Succeeded);

        var refused = session.SubmitEdit(new VoxelRemoveEdit(Probe), 4);

        Assert.AreEqual(WorldEditRefusal.PastStep, refused.Refusal);
        Assert.IsNull(refused.Record);
        Assert.AreEqual(1UL, session.NextEditId);
        Assert.AreEqual(new SimulationPosition(5, 0), session.EditBoundary);
        Assert.AreEqual(0UL, session.EditLogCount);
    }

    [TestMethod]
    public void Typed_entries_keep_business_statuses_for_unencodable_payloads_without_taking_input()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = worldEditor.Session;
        var vehicle = session.QueryComposites()[0].Id;
        var part = session.QueryComposites()[0].Parts[0].Id;
        var fingerprint = WorldSaveAuthority.Fingerprint(session);

        Assert.AreEqual(WorldCommandStatus.UnknownDefinition, session.Add(Probe, default, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.DisallowedOrientation, session.Add(Probe, Ore, default).Status);
        Assert.AreEqual(ConstructionStatus.UnknownDefinition, session.Manufacture(default, ShellPose).Status);
        Assert.AreEqual(CompositeDamageStatus.UnknownVehicle, session.Damage(default(EntityId), HollowShellRefit.FirstAdd, 25).Status);
        Assert.AreEqual(ConstructionStatus.UnknownVehicle, session.Refit(default, CompositeRefitOperation.Remove(part)).Status);
        Assert.AreEqual(ConstructionStatus.UnknownPart, session.Refit(vehicle, CompositeRefitOperation.Remove(default)).Status);
        Assert.AreEqual(ConstructionStatus.UnknownDefinition, session.Refit(vehicle, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, default, VoxelOrientation.Identity)).Status);
        Assert.AreEqual(ConstructionStatus.DisallowedOrientation, session.Refit(vehicle, CompositeRefitOperation.Rotate(part, default)).Status);

        Assert.AreEqual(new SimulationPosition(0, 0), session.EditBoundary);
        Assert.AreEqual(1UL, session.NextEditId);
        Assert.AreEqual(0UL, session.EditLogCount);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
    }

    [TestMethod]
    public void Start_whose_condition_failed_at_an_earlier_boundary_is_rejected_in_place_and_the_advance_continues()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = worldEditor.Session;
        var assembly = session.QueryTrainAssemblies().Single();
        var start = session.StartTrain(RailHeading.AlongAlignment, atStep: 4);
        Assert.AreEqual(TrainMotionStatus.Pending, start.Status);
        var locomotive = session.QueryComposites().Single(vehicle => vehicle.Id == assembly.ActiveLocomotiveId);
        var propulsion = locomotive.Parts.Single(part => part.DefinitionId == StaticTrainFixture.PropulsionId).Id;
        Assert.AreEqual("Pending", session.SubmitEdit(new VehicleRefitEdit(locomotive.Id, new[] { CompositeRefitOperation.Remove(propulsion) }), 2).Status);
        var transactions = session.TransactionCount;

        var advanced = session.AdvanceSimulation(10);

        Assert.AreEqual(TrainMotionStatus.Advanced, advanced.Status);
        Assert.AreEqual(10UL, advanced.AdvancedSteps);
        Assert.AreEqual(new SimulationPosition(10, 0), session.EditBoundary);
        Assert.IsNull(session.Fault);
        var log = session.QueryEditLog();
        CollectionAssert.AreEqual(new[] { start.EditId!.Value + 1, start.EditId.Value }, log.Select(record => record.Id).ToArray());
        CollectionAssert.AreEqual(new SimulationPosition?[] { new(2, 0), new(4, 0) }, log.Select(record => record.ExecutedAt).ToArray());
        Assert.AreEqual(WorldEditState.Executed, log[0].State);
        Assert.AreEqual(WorldEditState.Rejected, log[1].State);
        Assert.AreEqual(nameof(TrainMotionStatus.MissingPropulsion), log[1].Status);
        Assert.AreEqual(transactions + 1, session.TransactionCount);
        Assert.AreEqual(0, session.QueryPendingEdits().Count);
        Assert.IsFalse(session.QueryTrainAssemblies().Single().Running);
    }

    [TestMethod]
    public void Queries_camera_and_host_collection_do_not_take_positions()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = worldEditor.Session;
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(Probe, Ore, VoxelOrientation.Identity).Status);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var boundary = session.EditBoundary;

        session.QueryFollowedCamera();
        session.QueryDisplayPoses(0.5);
        session.QueryCollision(Probe);
        session.Query(Probe);
        session.QueryComposites();
        session.QueryRails();
        session.QueryEditLog();
        session.QueryPendingEdits();
        session.CollectHostSteps(1.0);

        Assert.AreEqual(boundary, session.EditBoundary);
        Assert.AreEqual(2UL, session.NextEditId);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
    }
}

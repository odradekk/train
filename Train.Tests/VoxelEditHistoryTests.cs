using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
[TestCategory("scope:world-editor")]
public sealed class VoxelEditHistoryTests
{
    private static readonly VoxelDefinitionId Ore = new("core", "ferrous-ore");
    private static readonly WorldVoxelCoord First = new(2, 2, 2);
    private static readonly WorldVoxelCoord Second = new(3, 2, 2);

    [TestMethod]
    public void Immediate_voxel_edits_undo_and_redo_as_new_formal_edits_without_rewinding_cursors()
    {
        using var harness = Blank();
        var session = harness.Session;
        Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity)).Status);
        var editAfterAdd = session.NextEditId;
        var entityAfterAdd = session.NextEntityId;
        var step = session.SimulationStep;

        AssertHistory(session, undo: 1, redo: 0);
        Assert.AreEqual("Applied", session.Undo().Status);
        var undoRecord = session.QueryEditLog()[^1];
        Assert.AreEqual(new SimulationPosition(step, 1), undoRecord.AcceptedAt);
        Assert.AreEqual(undoRecord.AcceptedAt, undoRecord.ExecutedAt);
        Assert.IsFalse(session.Query(First).IsOccupied);
        Assert.AreEqual(editAfterAdd + 1, session.NextEditId);
        Assert.AreEqual(entityAfterAdd, session.NextEntityId);
        Assert.AreEqual(step, session.SimulationStep);
        AssertHistory(session, undo: 0, redo: 1);

        var editAfterUndo = session.NextEditId;
        Assert.AreEqual("Applied", session.Redo().Status);
        var redoRecord = session.QueryEditLog()[^1];
        Assert.AreEqual(new SimulationPosition(step, 2), redoRecord.AcceptedAt);
        Assert.AreEqual(redoRecord.AcceptedAt, redoRecord.ExecutedAt);
        Assert.IsTrue(session.Query(First).IsOccupied);
        Assert.AreEqual(editAfterUndo + 1, session.NextEditId);
        Assert.AreEqual(entityAfterAdd, session.NextEntityId);
        Assert.AreEqual(step, session.SimulationStep);
        AssertHistory(session, undo: 1, redo: 0);
    }

    [TestMethod]
    public void Undo_restores_exact_durability_but_does_not_restore_a_debris_runtime_identity()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var manufactured = session.Manufacture(BridgeFixture.DefinitionId, CompositeTransform.Translation(8, 8, 8));
        var vehicle = session.QueryComposite(manufactured.VehicleId!.Value)!;
        var part = vehicle.Parts.Single(item => item.Cell == new LocalVoxelCoord(0, 0, 0));
        var damagedVehicle = session.SubmitEdit(new VehicleDamageEdit(new[]
        {
            new CompositeDamageTarget(vehicle.Id, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(vehicle.Id, BridgeFixture.LeftCoreCell, 100)
        }));
        Assert.AreEqual(WorldEditState.Executed, damagedVehicle.Record!.State);
        var dropped = session.World.OccupiedVoxels().Single(item => item.Value.RuntimeId == part.Id);
        Assert.IsNotNull(dropped.Value.RuntimeId);
        var entityCursor = session.NextEntityId;
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Remove(dropped.Key))).Status);

        Assert.AreEqual("Applied", session.Undo().Status);
        var restoredDebris = session.Query(dropped.Key).Voxel!;
        Assert.AreEqual(dropped.Value.DefinitionId, restoredDebris.DefinitionId);
        Assert.AreEqual(dropped.Value.Orientation, restoredDebris.Orientation);
        Assert.AreEqual(dropped.Value.CurrentDurability, restoredDebris.CurrentDurability);
        Assert.IsNull(restoredDebris.RuntimeId);
        Assert.AreEqual(entityCursor, session.NextEntityId);
        Assert.AreEqual("Applied", session.Redo().Status);
        Assert.IsFalse(session.Query(dropped.Key).IsOccupied);
        Assert.AreEqual(entityCursor, session.NextEntityId);
        Assert.AreEqual("Applied", session.Undo().Status);
        Assert.IsNull(session.Query(dropped.Key).Voxel!.RuntimeId);
        Assert.AreEqual(entityCursor, session.NextEntityId);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(new VoxelCellTarget(dropped.Key,
            VoxelCellOperation.Replace, new VoxelCellContent(Ore, VoxelOrientation.Yaw90)))).Status);
        Assert.IsNull(session.Query(dropped.Key).Voxel!.RuntimeId);
        Assert.AreEqual(entityCursor, session.NextEntityId);
        Assert.AreEqual("Applied", session.Undo().Status);
        Assert.IsNull(session.Query(dropped.Key).Voxel!.RuntimeId);
        Assert.AreEqual(entityCursor, session.NextEntityId);

        var damaged = new WorldVoxelCoord(5, 2, 2);
        Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(damaged, Ore, VoxelOrientation.Identity)).Status);
        Assert.AreEqual("Damaged", session.SubmitEdit(
            new VoxelDamageEdit(new[] { new VoxelDamageTarget(damaged, 25) }), confirmEndHistory: true).Status);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Remove(damaged))).Status);
        Assert.AreEqual("Applied", session.Undo().Status);
        Assert.AreEqual(75, session.Query(damaged).Voxel!.CurrentDurability);
    }

    [TestMethod]
    public void Cell_set_replace_and_legacy_remove_restore_exact_cell_state_in_both_directions()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(First, 25) }), confirmEndHistory: true);
        var replacement = new VoxelCellContent(HollowShellRefit.MarkerId, VoxelOrientation.Yaw90);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(
            new VoxelCellTarget(First, VoxelCellOperation.Replace, replacement))).Status);
        AssertCell(session, First, replacement.DefinitionId, replacement.Orientation, 100);
        Assert.AreEqual("Applied", session.Undo().Status);
        AssertCell(session, First, Ore, VoxelOrientation.Identity, 75);
        Assert.AreEqual("Applied", session.Redo().Status);
        AssertCell(session, First, replacement.DefinitionId, replacement.Orientation, 100);

        Assert.AreEqual("Removed", session.SubmitEdit(new VoxelRemoveEdit(First)).Status);
        Assert.IsFalse(session.Query(First).IsOccupied);
        Assert.AreEqual("Applied", session.Undo().Status);
        AssertCell(session, First, replacement.DefinitionId, replacement.Orientation, 100);
        Assert.AreEqual("Applied", session.Redo().Status);
        Assert.IsFalse(session.Query(First).IsOccupied);
    }

    [TestMethod]
    public void Restore_preconditions_reject_the_whole_multi_cell_edit_and_keep_history()
    {
        using var harness = Blank();
        var session = harness.Session;
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Add(First), Add(Second))).Status);
        var undo = session.Undo();
        Assert.AreEqual("Applied", undo.Status);
        Assert.IsTrue(session.World.TryAdd(First, new WorldVoxel(Ore, 100, VoxelOrientation.Yaw90)));
        var firstBefore = session.Query(First).Voxel;
        var secondBefore = session.Query(Second).Voxel;
        var nextEdit = session.NextEditId;

        var refused = session.Redo();
        Assert.AreEqual(VoxelCellSetStatus.ExpectedContentMismatch, ((VoxelCellSetResult)refused.Result!).Status);
        Assert.AreEqual(firstBefore, session.Query(First).Voxel);
        Assert.AreEqual(secondBefore, session.Query(Second).Voxel);
        Assert.AreEqual(nextEdit + 1, session.NextEditId);
        Assert.IsTrue(session.Query(First).IsOccupied);
        Assert.IsFalse(session.Query(Second).IsOccupied);
        AssertHistory(session, undo: 0, redo: 1);
    }

    [DataTestMethod]
    [DataRow("definition", false)]
    [DataRow("orientation", false)]
    [DataRow("durability", false)]
    [DataRow("definition", true)]
    [DataRow("orientation", true)]
    [DataRow("durability", true)]
    public void Reverse_preconditions_compare_each_occupied_cell_field_before_writing_any_cell(string field, bool redo)
    {
        using var harness = Blank();
        var session = harness.Session;
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Add(First), Add(Second))).Status);
        if (redo)
        {
            Assert.AreEqual("Applied", session.SubmitEdit(Set(Remove(First), Remove(Second))).Status);
            Assert.AreEqual("Applied", session.Undo().Status);
        }
        var changed = field switch
        {
            "definition" => new WorldVoxel(HollowShellRefit.MarkerId, 100, VoxelOrientation.Identity),
            "orientation" => new WorldVoxel(Ore, 100, VoxelOrientation.Yaw90),
            "durability" => new WorldVoxel(Ore, 75, VoxelOrientation.Identity),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        // Simulate drift at the world boundary without ending this session's history.
        Assert.IsTrue(session.World.TryReplace(Second, changed));
        var result = redo ? session.Redo() : session.Undo();
        Assert.AreEqual(WorldEditState.Rejected, result.Record!.State);
        Assert.AreEqual(VoxelCellSetStatus.ExpectedContentMismatch, ((VoxelCellSetResult)result.Result!).Status);
        Assert.AreEqual(new WorldVoxel(Ore, 100, VoxelOrientation.Identity), session.Query(First).Voxel);
        Assert.AreEqual(changed, session.Query(Second).Voxel);
        AssertHistory(session, undo: 1, redo: redo ? 1 : 0);
    }

    [TestMethod]
    public void Failed_edits_and_failed_reverse_keep_branches_but_successful_new_edit_clears_redo()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        session.Undo();
        AssertHistory(session, undo: 0, redo: 1);

        Assert.AreEqual(VoxelCellSetStatus.Empty,
            ((VoxelCellSetResult)session.SubmitEdit(Set(Remove(Second))).Result!).Status);
        AssertHistory(session, undo: 0, redo: 1);
        Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(Second, Ore, VoxelOrientation.Identity)).Status);
        AssertHistory(session, undo: 1, redo: 0);
        Assert.AreEqual(WorldEditRefusal.NoHistory, session.Redo().Refusal);
    }

    [TestMethod]
    public void Capacity_evicts_oldest_operations_and_reports_each_eviction()
    {
        using var harness = Blank(sizeX: VoxelDevelopmentSession.VoxelHistoryCapacity + 2);
        var session = harness.Session;
        for (var x = 0; x < VoxelDevelopmentSession.VoxelHistoryCapacity + 2; x++)
            Assert.AreEqual("Added", session.SubmitEdit(new VoxelAddEdit(new(x, 2, 2), Ore, VoxelOrientation.Identity)).Status);

        Assert.AreEqual(VoxelDevelopmentSession.VoxelHistoryCapacity, session.UndoCount);
        Assert.AreEqual(2UL, session.HistoryEvictionCount);
        for (var i = 0; i < VoxelDevelopmentSession.VoxelHistoryCapacity; i++)
            Assert.AreEqual("Applied", session.Undo().Status);
        Assert.IsTrue(session.Query(new(0, 2, 2)).IsOccupied);
        Assert.IsTrue(session.Query(new(1, 2, 2)).IsOccupied);
        Assert.AreEqual(WorldEditRefusal.NoHistory, session.Undo().Refusal);
    }

    [TestMethod]
    public void Running_pending_and_non_voxel_edits_obey_history_boundaries_and_confirmation()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        Assert.AreEqual(WorldEditRefusal.HistoryConfirmationRequired,
            session.SubmitEdit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(First, 1) })).Refusal);
        AssertHistory(session, undo: 1, redo: 0);
        Assert.AreEqual("Damaged", session.SubmitEdit(
            new VoxelDamageEdit(new[] { new VoxelDamageTarget(First, 1) }), confirmEndHistory: true).Status);
        AssertHistory(session, undo: 0, redo: 0);

        session.SubmitEdit(new VoxelAddEdit(Second, Ore, VoxelOrientation.Identity));
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelRemoveEdit(Second), session.SimulationStep + 1,
            confirmEndHistory: true).Status);
        AssertHistory(session, undo: 0, redo: 0);
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        Assert.AreEqual(WorldEditRefusal.SimulationRunning, session.Undo().Refusal);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        session.PauseSimulation();
        AssertHistory(session, undo: 0, redo: 0);
    }

    [TestMethod]
    public void Confirmed_non_voxel_failure_keeps_history_but_success_ends_it()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        var failed = session.SubmitEdit(new VehicleManufactureEdit(
            new CompositeDefinitionId("core", "missing"), CompositeTransform.Translation(4, 4, 4)),
            confirmEndHistory: true);
        Assert.AreEqual(WorldEditState.Rejected, failed.Record!.State);
        Assert.AreEqual("UnknownDefinition", failed.Status);
        AssertHistory(session, undo: 1, redo: 0);

        var succeeded = session.SubmitEdit(new VehicleManufactureEdit(
            HollowShellFixture.DefinitionId, CompositeTransform.Translation(4, 4, 4)),
            confirmEndHistory: true);
        Assert.AreEqual(WorldEditState.Executed, succeeded.Record!.State);
        Assert.AreEqual("Valid", succeeded.Status);
        AssertHistory(session, undo: 0, redo: 0);
    }

    [TestMethod]
    public void Failed_and_successful_simulation_world_and_publish_boundaries_keep_or_end_history()
    {
        using var resume = Blank();
        resume.Session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        resume.Session.BeginWorldChange(new(WorldChangeKind.Quit, null));
        Assert.AreEqual(TrainMotionStatus.WorldChanging, resume.Session.ResumeSimulation().Status);
        Assert.AreEqual(1, resume.Session.UndoCount);
        resume.Session.ChooseWorldChange(WorldChangeChoice.Cancel);
        Assert.IsTrue(resume.Session.ResumeSimulation().Succeeded);
        AssertHistory(resume.Session, 0, 0);

        using var advance = Blank();
        Assert.IsTrue(advance.Session.AdvanceSimulation(1).Succeeded);
        advance.Session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        Assert.AreEqual(TrainMotionStatus.StepOverflow, advance.Session.AdvanceSimulation(ulong.MaxValue).Status);
        AssertHistory(advance.Session, 1, 0);
        Assert.IsTrue(advance.Session.AdvanceSimulation(1).Succeeded);
        AssertHistory(advance.Session, 0, 0);

        using var change = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var changeCell = new WorldVoxelCoord(5, 15, 4);
        Assert.AreEqual("Added", change.Session.SubmitEdit(new VoxelAddEdit(changeCell, Ore, VoxelOrientation.Identity)).Status);
        Assert.AreEqual(WorldChangeStatus.CandidateFailed, change.Session.TryChangeWorld(
            new(WorldChangeKind.NewBlank, WorldChangeChoice.Discard, BlankIdentity: default)).Status);
        AssertHistory(change.Session, 1, 0);
        BlankWorldSource.TryIdentity(12, 8, 12, 22, out var replacement);
        Assert.AreEqual(WorldChangeStatus.Completed, change.Session.TryChangeWorld(
            new(WorldChangeKind.NewBlank, WorldChangeChoice.Discard, BlankIdentity: replacement)).Status);
        AssertHistory(change.Session, 0, 0);

        using var publishFailure = Blank();
        publishFailure.Session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        Assert.ThrowsException<InvalidOperationException>(() => publishFailure.Session.PublishContent(
            ContentPublishEdit.For(publishFailure.Catalog.Current!, publishFailure.Composites.Current),
            () => throw new InvalidOperationException("publish probe")));
        Assert.AreEqual(1, publishFailure.Session.UndoCount);

        using var publish = Blank();
        publish.Session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        publish.Session.PublishContent(ContentPublishEdit.For(publish.Catalog.Current!, publish.Composites.Current), () => { });
        AssertHistory(publish.Session, 0, 0);
    }

    [TestMethod]
    public void Save_preserves_session_history_but_loading_the_save_starts_without_history()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        var saved = session.Save();
        Assert.IsTrue(saved.Succeeded);
        AssertHistory(session, undo: 1, redo: 0);

        using var reader = Blank(harness);
        Assert.IsTrue(reader.Session.Load(saved.SaveId!.Value).Succeeded);
        AssertHistory(reader.Session, undo: 0, redo: 0);
        Assert.AreEqual(WorldEditRefusal.NoHistory, reader.Session.Undo().Refusal);
    }

    [TestMethod]
    public void Save_as_export_and_failed_save_preserve_undo_and_redo_stacks()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        session.Undo();
        AssertHistory(session, 0, 1);
        Assert.IsTrue(session.SaveAs("history copy").Succeeded);
        AssertHistory(session, 0, 1);

        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        var exportPath = Path.Combine(harness.UserData, "history-export.json");
        Assert.IsTrue(workspace.Export(session, exportPath).Succeeded);
        AssertHistory(session, 0, 1);

        Assert.AreEqual(WorldChangeStatus.Switching,
            session.BeginWorldChange(new(WorldChangeKind.Quit, null)).Status);
        Assert.AreEqual(WorldSaveStatus.WorldChanging, session.Save().Status);
        Assert.AreEqual(1, session.RedoCount);
        session.ChooseWorldChange(WorldChangeChoice.Cancel);
        AssertHistory(session, 0, 1);
    }

    [TestMethod]
    public void World_change_and_fault_disable_history_without_discarding_it()
    {
        using var harness = Blank();
        var session = harness.Session;
        session.SubmitEdit(new VoxelAddEdit(First, Ore, VoxelOrientation.Identity));
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new(WorldChangeKind.Quit, null)).Status);
        Assert.IsFalse(session.CanUndo);
        Assert.AreEqual(WorldEditRefusal.WorldChanging, session.Undo().Refusal);
        Assert.AreEqual(WorldChangeStatus.Cancelled, session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        AssertHistory(session, undo: 1, redo: 0);

        session.EditFaultProbe = _ => throw new InvalidOperationException("history boundary probe");
        var faulted = session.SubmitEdit(new VoxelAddEdit(Second, Ore, VoxelOrientation.Identity));
        Assert.AreEqual(WorldEditState.Faulted, faulted.Record!.State);
        Assert.AreEqual(1, session.UndoCount);
        Assert.IsFalse(session.CanUndo);
        Assert.AreEqual(WorldEditRefusal.SimulationFaulted, session.Undo().Refusal);
        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, session.ResumeSimulation().Status);
        Assert.AreEqual(1, session.UndoCount);
    }

    [TestMethod]
    public void Draft_history_changes_only_the_draft_and_commit_discard_or_replacement_end_it()
    {
        using var harness = Blank();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        var before = WorldSaveAuthority.Fingerprint(session);
        tool.SetExact(First, Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        tool.SetExact(Second, Ore, VoxelOrientation.Yaw90, VoxelCellOperation.Replace);
        var hollow = new VoxelDefinitionId("core", "hollow");
        tool.SetSettings(hollow, VoxelOrientation.Yaw90, VoxelCellOperation.Remove);
        Assert.IsTrue(tool.CanUndoDraft);
        Assert.IsTrue(tool.UndoDraft());
        Assert.AreEqual(Second, tool.Position);
        Assert.AreEqual(Ore, tool.DefinitionId);
        Assert.AreEqual(VoxelOrientation.Yaw90, tool.Orientation);
        Assert.AreEqual(VoxelCellOperation.Replace, tool.Operation);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));
        Assert.IsTrue(tool.CanRedoDraft);
        Assert.IsFalse(session.CanUndo);
        Assert.AreEqual(WorldEditRefusal.VoxelDraftExists, session.Undo().Refusal);
        Assert.IsTrue(tool.RedoDraft());
        Assert.AreEqual(Second, tool.Position);
        Assert.AreEqual(hollow, tool.DefinitionId);
        Assert.AreEqual(VoxelCellOperation.Remove, tool.Operation);
        tool.Discard();
        Assert.IsFalse(tool.CanUndoDraft);
        Assert.IsFalse(tool.CanRedoDraft);

        tool.SetExact(First, Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        var committed = tool.Commit();
        Assert.AreEqual(WorldEditState.Executed, committed.Record!.State);
        Assert.AreEqual("Applied", committed.Status);
        Assert.IsFalse(tool.CanUndoDraft);
        tool.SetExact(Second, Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        tool.SetExact(First, Ore, VoxelOrientation.Yaw90, VoxelCellOperation.Replace);
        Assert.IsTrue(tool.CanUndoDraft);
        tool.SetExact(Second, Ore, VoxelOrientation.Identity, VoxelCellOperation.Remove);
        Assert.IsTrue(tool.CanUndoDraft);

        session.Reset();
        Assert.IsFalse(tool.HasDraft);
        Assert.IsFalse(tool.CanUndoDraft);
        Assert.IsFalse(tool.CanRedoDraft);
    }

    private static VoxelCellSetEdit Set(params VoxelCellTarget[] targets) => new(targets);
    private static VoxelCellTarget Add(WorldVoxelCoord position) =>
        new(position, VoxelCellOperation.Add, new VoxelCellContent(Ore, VoxelOrientation.Identity));
    private static VoxelCellTarget Remove(WorldVoxelCoord position) =>
        new(position, VoxelCellOperation.Remove, null);

    private static void AssertHistory(VoxelDevelopmentSession session, int undo, int redo)
    {
        Assert.AreEqual(undo, session.UndoCount);
        Assert.AreEqual(redo, session.RedoCount);
        Assert.AreEqual(undo != 0, session.CanUndo);
        Assert.AreEqual(redo != 0, session.CanRedo);
    }

    private static void AssertCell(VoxelDevelopmentSession session, WorldVoxelCoord position,
        VoxelDefinitionId definition, VoxelOrientation orientation, int durability)
    {
        var voxel = session.Query(position).Voxel!;
        Assert.AreEqual(definition, voxel.DefinitionId);
        Assert.AreEqual(orientation, voxel.Orientation);
        Assert.AreEqual(durability, voxel.CurrentDurability);
    }

    private static WorldEditorHarness Blank(int sizeX = 16)
    {
        var harness = WorldEditorHarness.Content();
        BlankWorldSource.TryIdentity(sizeX, 8, 16, 12, out var identity);
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        return harness;
    }

    private static WorldEditorHarness Blank(WorldEditorHarness source)
    {
        var harness = WorldEditorHarness.Content(source.Source, source.UserData);
        BlankWorldSource.TryIdentity(16, 8, 16, 12, out var identity);
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        return harness;
    }
}

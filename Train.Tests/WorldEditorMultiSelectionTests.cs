using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
public sealed class WorldEditorMultiSelectionTests
{
    private static readonly WorldVoxelCoord First = new(2, 2, 2);
    private static readonly WorldVoxelCoord Second = new(3, 2, 2);
    private static readonly VoxelDefinitionId Ore = WorldEditorSequences.EditVoxelId;
    private static readonly VoxelDefinitionId Marker = new("core", "refit-marker");
    private static readonly ObservationVector View = new(8, 8, 8);

    [TestMethod]
    public void Selection_normalizes_world_identity_and_only_world_cell_sets_share_batch_operations()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        SeedCells(session);
        using var observer = new WorldEditorObserver(session, View, default);

        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.SelectMany(new[]
        {
            ObservedObject.WorldCell(First),
            ObservedObject.WorldCell(Second),
            ObservedObject.WorldCell(First)
        }));
        Assert.AreEqual(2, observer.SelectedObjects.Count);
        Assert.IsNull(observer.Selection);
        Assert.IsTrue(observer.SelectedObjects.All(item => item.Definition == Ore));
        Assert.AreEqual(WorldEditorSelectionOperations.Remove | WorldEditorSelectionOperations.Replace,
            observer.CommonOperations);

        var segment = session.Rails.Segments.First();
        var vehicles = session.Vehicles.Select(vehicle => ObservedObject.Vehicle(vehicle.Id)).ToArray();
        Assert.IsTrue(vehicles.Length > 1);
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.SelectMany(vehicles));
        Assert.AreEqual(WorldEditorSelectionOperations.None, observer.CommonOperations);
        Assert.AreEqual(WorldEditorObservationStatus.Selected,
            observer.SelectMany(new[] { ObservedObject.WorldCell(First), ObservedObject.Segment(segment.Id) }));
        Assert.AreEqual(WorldEditorSelectionOperations.None, observer.CommonOperations);
        Assert.AreEqual(WorldEditorObservationStatus.Selected,
            observer.SelectMany(new[] { vehicles[0], ObservedObject.Segment(segment.Id) }));
        Assert.AreEqual(WorldEditorSelectionOperations.None, observer.CommonOperations);
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.Select(ObservedObject.Segment(segment.Id)));
        Assert.AreEqual(ObservedObject.Segment(segment.Id), observer.Selection);
        Assert.AreEqual(WorldEditorSelectionOperations.None, observer.CommonOperations);
        Assert.AreEqual(WorldEditorObservationStatus.Cleared, observer.ClearSelection());
        Assert.AreEqual(0, observer.SelectedObjects.Count);
        Assert.IsNull(observer.Selection);
        Assert.AreEqual(WorldEditorSelectionOperations.None, observer.CommonOperations);
    }

    [TestMethod]
    public void Ordinary_drafts_guard_selection_but_batch_drafts_keep_captured_targets_when_selection_changes()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var observer = new WorldEditorObserver(session, View, default);
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.Select(ObservedObject.WorldCell(First)));

        tool.SetExact(new(4, 2, 2), Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(WorldEditorObservationStatus.DraftRequired,
            observer.Select(ObservedObject.WorldCell(Second)));
        Assert.AreEqual(WorldEditorObservationStatus.DraftRequired, observer.ClearSelection());
        tool.Discard();

        Assert.AreEqual(WorldEditorObservationStatus.Selected,
            observer.SelectMany(new[] { ObservedObject.WorldCell(First), ObservedObject.WorldCell(Second) }));
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(observer.SelectedObjects, VoxelCellOperation.Remove).Status);
        CollectionAssert.AreEquivalent(new[] { First, Second }, tool.BatchTargets.ToArray());
        Assert.IsTrue(tool.HasBatchDraft);
        Assert.AreEqual(WorldEditorObservationStatus.Cleared, observer.ClearSelection());
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.Select(ObservedObject.WorldCell(Second)));
        CollectionAssert.AreEquivalent(new[] { First, Second }, tool.CurrentEdit.Targets.Select(item => item.Position).ToArray());
        tool.SetExact(new(5, 2, 2), Marker, VoxelOrientation.Yaw90, VoxelCellOperation.Add);
        CollectionAssert.AreEquivalent(new[] { First, Second }, tool.BatchTargets.ToArray());
        Assert.AreEqual(VoxelCellOperation.Remove, tool.Operation);

        Assert.AreEqual(VoxelCellSetStatus.InvalidTarget,
            tool.StartBatch(new[] { ObservedObject.WorldCell(Second) }, VoxelCellOperation.Remove).Status);
        CollectionAssert.AreEquivalent(new[] { First, Second }, tool.BatchTargets.ToArray());
    }

    [TestMethod]
    public void Batch_remove_commits_one_cell_set_and_one_history_entry_then_undo_and_redo_restore_the_group()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        var beforeHistory = session.UndoCount;
        var beforeLog = session.QueryEditLog().Count;

        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove).Status);
        var committed = tool.Commit();

        Assert.AreEqual(WorldEditState.Executed, committed.Record!.State);
        Assert.IsInstanceOfType<VoxelCellSetEdit>(committed.Record.Edit);
        Assert.AreEqual(2, ((VoxelCellSetEdit)committed.Record.Edit).Targets.Count);
        Assert.AreEqual(beforeHistory + 1, session.UndoCount);
        Assert.AreEqual(beforeLog + 1, session.QueryEditLog().Count);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsFalse(session.Query(First).IsOccupied);
        Assert.IsFalse(session.Query(Second).IsOccupied);

        Assert.AreEqual("Applied", session.Undo().Status);
        AssertVoxel(session, First, Ore, VoxelOrientation.Identity);
        AssertVoxel(session, Second, Ore, VoxelOrientation.Yaw90);
        Assert.AreEqual("Applied", session.Redo().Status);
        Assert.IsFalse(session.Query(First).IsOccupied);
        Assert.IsFalse(session.Query(Second).IsOccupied);
    }

    [TestMethod]
    public void Batch_replace_applies_one_setting_to_mixed_originals_and_undo_restores_each_original()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Replace).Status);
        tool.SetSettings(Marker, VoxelOrientation.Yaw90, VoxelCellOperation.Remove);

        Assert.AreEqual(VoxelCellOperation.Replace, tool.Operation);
        Assert.IsTrue(tool.CurrentEdit.Targets.All(target => target.Content == new VoxelCellContent(Marker, VoxelOrientation.Yaw90)));
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        AssertVoxel(session, First, Marker, VoxelOrientation.Yaw90);
        AssertVoxel(session, Second, Marker, VoxelOrientation.Yaw90);
        Assert.AreEqual("Applied", session.Undo().Status);
        AssertVoxel(session, First, Ore, VoxelOrientation.Identity);
        AssertVoxel(session, Second, Ore, VoxelOrientation.Yaw90);
    }

    [TestMethod]
    public void Batch_replace_is_rejected_as_a_whole_when_one_target_already_has_the_requested_content()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Replace).Status);
        tool.SetSettings(Ore, VoxelOrientation.Identity, VoxelCellOperation.Remove);
        var beforeHistory = session.UndoCount;
        var beforeLog = session.QueryEditLog().Count;
        var nextEdit = session.NextEditId;

        Assert.AreEqual(new VoxelCellSetResult(VoxelCellSetStatus.NoChange, First), tool.Preview);
        var rejected = tool.Commit();

        Assert.AreEqual(WorldEditRefusal.None, rejected.Refusal);
        Assert.AreEqual(WorldEditState.Rejected, rejected.Record!.State);
        Assert.AreEqual(new VoxelCellSetResult(VoxelCellSetStatus.NoChange, First), rejected.Result);
        Assert.AreEqual(nextEdit + 1, session.NextEditId);
        Assert.AreEqual(beforeLog + 1, session.QueryEditLog().Count);
        Assert.AreEqual(beforeHistory, session.UndoCount);
        AssertVoxel(session, First, Ore, VoxelOrientation.Identity);
        AssertVoxel(session, Second, Ore, VoxelOrientation.Yaw90);
        Assert.IsTrue(tool.HasBatchDraft);
    }

    [TestMethod]
    public void Any_identity_drift_rejects_the_whole_batch_without_allocating_an_edit_and_keeps_the_draft()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove).Status);
        var nextEdit = session.NextEditId;
        var firstBefore = session.Query(First).Voxel;
        Assert.IsTrue(session.World.TryReplace(Second, new WorldVoxel(Ore, 100, VoxelOrientation.Identity)));

        Assert.AreEqual(VoxelCellSetStatus.ExpectedContentMismatch, tool.Preview.Status);
        Assert.AreEqual(Second, tool.Preview.Position);
        var refused = tool.Commit();
        Assert.AreEqual(WorldEditRefusal.DraftTargetsChanged, refused.Refusal);
        Assert.AreEqual(new VoxelCellSetResult(VoxelCellSetStatus.ExpectedContentMismatch, Second), refused.Result);
        Assert.AreEqual(nextEdit, session.NextEditId);
        Assert.IsTrue(tool.HasBatchDraft);
        Assert.AreEqual(firstBefore, session.Query(First).Voxel);
        Assert.IsTrue(session.Query(Second).IsOccupied);
    }

    [TestMethod]
    public void Removed_then_identically_rebuilt_target_stays_invalid_until_the_batch_is_discarded()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove).Status);

        Assert.AreEqual(WorldCommandStatus.Removed, session.Remove(First).Status);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(First, Ore, VoxelOrientation.Identity).Status);
        var nextBatchEdit = session.NextEditId;
        var refused = tool.Commit();
        Assert.AreEqual(WorldEditRefusal.DraftTargetsChanged, refused.Refusal);
        Assert.AreEqual(new VoxelCellSetResult(VoxelCellSetStatus.ExpectedContentMismatch, First), refused.Result);
        Assert.AreEqual(nextBatchEdit, session.NextEditId);
        Assert.IsTrue(tool.HasBatchDraft);

        tool.Discard();
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove).Status);
    }

    [TestMethod]
    public void Definition_change_by_an_executed_cell_edit_refuses_the_batch_without_allocating_its_edit()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove).Status);
        var changed = session.SubmitEdit(new VoxelCellSetEdit(new[]
        {
            new VoxelCellTarget(First, VoxelCellOperation.Replace,
                new VoxelCellContent(Marker, VoxelOrientation.Identity))
        }));
        Assert.AreEqual(WorldEditState.Executed, changed.Record!.State);
        var nextBatchEdit = session.NextEditId;

        var refused = tool.Commit();

        Assert.AreEqual(WorldEditRefusal.DraftTargetsChanged, refused.Refusal);
        Assert.AreEqual(new VoxelCellSetResult(VoxelCellSetStatus.ExpectedContentMismatch, First), refused.Result);
        Assert.AreEqual(nextBatchEdit, session.NextEditId);
        AssertVoxel(session, First, Marker, VoxelOrientation.Identity);
        AssertVoxel(session, Second, Ore, VoxelOrientation.Yaw90);
        Assert.IsTrue(tool.HasBatchDraft);
    }

    [TestMethod]
    public void Runtime_identity_change_rejects_a_batch_even_when_definition_and_orientation_are_unchanged()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        Assert.IsTrue(session.World.TryReplace(First,
            new WorldVoxel(Ore, 100, VoxelOrientation.Identity, new Train.Domain.Composites.EntityId(41))));
        using var tool = new WorldEditorVoxelTool(session);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove).Status);

        Assert.IsTrue(session.World.TryReplace(First,
            new WorldVoxel(Ore, 100, VoxelOrientation.Identity, new Train.Domain.Composites.EntityId(42))));
        Assert.AreEqual(new VoxelCellSetResult(VoxelCellSetStatus.ExpectedContentMismatch, First), tool.Preview);
        Assert.AreEqual(WorldEditRefusal.DraftTargetsChanged, tool.Commit().Refusal);
        Assert.IsTrue(session.Query(Second).IsOccupied);
    }

    [TestMethod]
    public void Batch_drafts_trigger_departure_protection_and_are_not_written_by_save()
    {
        using var harness = BlankWithCells();
        var session = harness.Session;
        var saveId = session.Save().SaveId!.Value;
        using var tool = new WorldEditorVoxelTool(session);
        tool.StartBatch(Observe(session, First, Second), VoxelCellOperation.Remove);

        Assert.IsTrue(session.Save().Succeeded);
        Assert.IsTrue(tool.HasBatchDraft);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new(WorldChangeKind.Quit, null)).Status);
        Assert.AreEqual(WorldChangeStatus.Cancelled,
            session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);

        var restored = new VoxelDevelopmentSession(harness.Catalog, session.Identity, harness.Store, harness.Composites);
        try
        {
            Assert.IsTrue(restored.Load(saveId).Succeeded);
            AssertVoxel(restored, First, Ore, VoxelOrientation.Identity);
            AssertVoxel(restored, Second, Ore, VoxelOrientation.Yaw90);
            Assert.IsFalse(restored.HasEditorDraft);
        }
        finally
        {
            restored.Detach();
        }
    }

    private static WorldEditorHarness BlankWithCells()
    {
        var harness = WorldEditorHarness.Content();
        BlankWorldSource.TryIdentity(16, 8, 16, 12, out var identity);
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        SeedCells(harness.Session);
        return harness;
    }

    private static void SeedCells(VoxelDevelopmentSession session)
    {
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(First, Ore, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(Second, Ore, VoxelOrientation.Yaw90).Status);
    }

    private static ObservedObject[] Observe(VoxelDevelopmentSession session, params WorldVoxelCoord[] cells) =>
        cells.Select(cell =>
        {
            var voxel = session.Query(cell).Voxel!;
            return ObservedObject.WorldCell(cell, voxel.RuntimeId, voxel.DefinitionId);
        }).ToArray();

    private static void AssertVoxel(VoxelDevelopmentSession session, WorldVoxelCoord position,
        VoxelDefinitionId definition, VoxelOrientation orientation)
    {
        var voxel = session.Query(position).Voxel;
        Assert.IsNotNull(voxel);
        Assert.AreEqual(definition, voxel.DefinitionId);
        Assert.AreEqual(orientation, voxel.Orientation);
    }
}

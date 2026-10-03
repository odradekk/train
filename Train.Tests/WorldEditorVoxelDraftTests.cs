using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Voxels;
using Train.Domain.Rails;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
[TestCategory("scope:persistence")]
public sealed class WorldEditorVoxelDraftTests
{
    private static readonly WorldVoxelCoord Cell = new(1, 2, 1);

    [DataTestMethod]
    [DataRow(WorldEditorVoxelMode.Brush, VoxelWorkPlane.VerticalX)]
    [DataRow(WorldEditorVoxelMode.Box, VoxelWorkPlane.Surface)]
    public void Region_modes_keep_horizontal_targets_and_cannot_redirect_an_existing_draft(
        WorldEditorVoxelMode mode, VoxelWorkPlane plane)
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetPlane(plane, 12);
        Assert.IsTrue(tool.SetMode(mode));
        Assert.AreEqual(VoxelWorkPlane.Horizontal, tool.Plane);
        Assert.AreEqual(7, tool.Layer);
        Assert.IsFalse(tool.SetPlane(VoxelWorkPlane.VerticalZ, 3));
        Assert.IsTrue(tool.TryPlaneTarget(new(1.5, 10, 2.5), new(0, -1, 0), out var target));
        Assert.AreEqual(new WorldVoxelCoord(1, 7, 2), target);
        tool.SetExact(target, tool.DefinitionId, tool.Orientation, VoxelCellOperation.Add);
        var boundTargets = tool.CurrentEdit.Targets.Select(item => item.Position).ToArray();
        Assert.IsFalse(tool.SetMode(WorldEditorVoxelMode.Cell));
        Assert.IsFalse(tool.SetLayer(1));
        CollectionAssert.AreEqual(boundTargets, tool.CurrentEdit.Targets.Select(item => item.Position).ToArray());
        tool.Discard();
        Assert.IsTrue(tool.SetMode(WorldEditorVoxelMode.Cell));
        Assert.IsTrue(tool.SetPlane(VoxelWorkPlane.VerticalZ, 3));
    }

    [DataTestMethod]
    [DataRow(VoxelWorkPlane.Horizontal, 2, 6, 6, 9, -2, -2, -4, 2, 2, 1)]
    [DataRow(VoxelWorkPlane.VerticalX, 2, 6, 2, 9, -2, -0.1, -4, 2, 1, 1)]
    [DataRow(VoxelWorkPlane.VerticalZ, 1, 6, 2, 9, -2, -0.1, -4, 2, 1, 1)]
    public void Plane_intersections_target_the_integer_layer_and_floor_the_other_axes(
        VoxelWorkPlane plane, int layer, int ox, int oy, int oz, double dx, double dy, double dz, int x, int y, int z)
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        Assert.IsTrue(tool.SetPlane(plane, layer));
        Assert.IsTrue(tool.TryPlaneTarget(new(ox, oy, oz), new(dx, dy, dz), out var target));
        Assert.AreEqual(new WorldVoxelCoord(x, y, z), target);
        tool.Hover(target);
        Assert.AreEqual(target, tool.CurrentEdit.Targets.Single().Position);
        Assert.IsFalse(tool.HasDraft);
    }

    [TestMethod]
    public void Plane_misses_and_outside_targets_never_bypass_world_validation_or_redirect_a_draft()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetPlane(VoxelWorkPlane.VerticalX, 0);
        Assert.IsFalse(tool.TryPlaneTarget(new(2, 2, 2), new(0, 1, 0), out _));
        Assert.IsFalse(tool.TryPlaneTarget(new(2, 2, 2), new(1, 0, 0), out _));
        Assert.IsTrue(tool.TryPlaneTarget(new(2, 2, 2), new(-1, -2, 0), out var outside));
        Assert.AreEqual(new WorldVoxelCoord(0, -2, 2), outside);
        tool.Hover(outside);
        Assert.AreEqual(VoxelCellSetStatus.OutOfBounds, tool.Preview.Status);
        Assert.AreEqual(VoxelCellSetStatus.OutOfBounds, ((VoxelCellSetResult)tool.Commit().Result!).Status);
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        Assert.IsFalse(tool.SetPlane(VoxelWorkPlane.VerticalZ, 3));
        Assert.IsFalse(tool.SetPlane(VoxelWorkPlane.Surface, 0));
        tool.SetExact(Cell, tool.DefinitionId, tool.Orientation, VoxelCellOperation.Add);
        tool.Hover(default);
        Assert.AreEqual(Cell, tool.Position);
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        Assert.IsTrue(tool.SetPlane(VoxelWorkPlane.VerticalZ, 3));
    }

    [TestMethod]
    public void Surface_add_targets_the_outside_neighbor_and_remove_replace_target_the_hit_cell()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetPlane(VoxelWorkPlane.Surface, 0);
        var hit = new WorldVoxelCoord(0, 2, 1);
        var adjacent = new WorldVoxelCoord(-1, 2, 1);
        tool.Hover(tool.SurfaceTarget(hit, adjacent));
        Assert.AreEqual(adjacent, tool.Position);
        Assert.AreEqual(VoxelCellSetStatus.OutOfBounds, tool.Preview.Status);
        tool.SetSettings(tool.DefinitionId, tool.Orientation, VoxelCellOperation.Remove);
        Assert.AreEqual(hit, tool.SurfaceTarget(hit, adjacent));
        tool.SetSettings(tool.DefinitionId, tool.Orientation, VoxelCellOperation.Replace);
        Assert.AreEqual(hit, tool.SurfaceTarget(hit, adjacent));
    }

    [TestMethod]
    public void Definition_orientation_and_operation_settings_keep_hover_transient_and_following_the_mouse()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetSettings(WorldEditorSequences.EditVoxelId,
            new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY), VoxelCellOperation.Add);
        tool.Hover(Cell);
        Assert.IsFalse(tool.HasDraft);
        Assert.AreEqual(Cell, tool.Position);
        Assert.AreEqual(VoxelCellSetStatus.Valid, tool.Preview.Status);
        tool.SetSettings(tool.DefinitionId, tool.Orientation, VoxelCellOperation.Remove);
        var next = new WorldVoxelCoord(3, 2, 1);
        tool.Hover(next);
        Assert.AreEqual(next, tool.Position);
        Assert.IsFalse(tool.HasDraft);
        Assert.AreEqual(VoxelCellSetStatus.Empty, tool.Preview.Status);
        tool.SetSettings(tool.DefinitionId, tool.Orientation, VoxelCellOperation.Replace);
        tool.Hover(Cell);
        Assert.AreEqual(Cell, tool.Position);
        Assert.IsFalse(tool.HasDraft);
        tool.SetExact(Cell, tool.DefinitionId, tool.Orientation, VoxelCellOperation.Add);
        tool.SetSettings(tool.DefinitionId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        tool.Hover(next);
        Assert.AreEqual(Cell, tool.Position);
        Assert.IsTrue(tool.HasDraft);
    }

    [TestMethod]
    public void Hover_is_transient_but_precise_input_and_rejected_commits_pin_the_same_preview()
    {
        using var harness = Blank();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetActive(true);
        var authority = WorldSaveAuthority.Fingerprint(session);
        tool.SetLayer(2);
        tool.Hover(Cell);
        Assert.AreEqual(Cell, tool.CurrentEdit.Targets[0].Position);
        Assert.IsFalse(tool.HasDraft);
        Assert.AreEqual(authority, WorldSaveAuthority.Fingerprint(session));
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Remove);
        tool.Hover(new(3, 2, 1));
        Assert.AreEqual(Cell, tool.Position);
        Assert.IsFalse(tool.SetLayer(3));
        Assert.IsFalse(tool.SetActive(false));
        Assert.AreEqual(VoxelCellSetStatus.Empty, ((VoxelCellSetResult)tool.Commit().Result!).Status);
        Assert.IsTrue(tool.HasDraft);
        Assert.IsFalse(session.Query(Cell).IsOccupied);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsTrue(session.Query(Cell).IsOccupied);
        Assert.IsTrue(tool.SetLayer(3));
        Assert.IsTrue(tool.SetActive(false));
    }

    [DataTestMethod]
    [DataRow(WorldChangeKind.NewBlank)]
    [DataRow(WorldChangeKind.Load)]
    [DataRow(WorldChangeKind.Reset)]
    [DataRow(WorldChangeKind.SwitchConfiguration)]
    [DataRow(WorldChangeKind.ReturnToWorkspace)]
    [DataRow(WorldChangeKind.ReturnToMainMenu)]
    [DataRow(WorldChangeKind.Quit)]
    [DataRow(WorldChangeKind.LeaveForScene)]
    [DataRow(WorldChangeKind.ReloadSaved)]
    public void Every_departure_protects_a_draft_even_when_the_saved_world_is_clean(WorldChangeKind kind)
    {
        using var harness = Blank();
        var session = harness.Session;
        var saveId = session.Save().SaveId!.Value;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        var original = session.World;
        var request = new WorldChangeRequest(kind, WorldChangeChoice.Discard,
            WorldEditorConfigurations.StraightRailEmptyId, saveId, BlankIdentity: session.Identity);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.BeginWorldChange(request).Status);
        Assert.IsTrue(session.IsChangingWorld);
        Assert.AreEqual(WorldEditRefusal.WorldChanging, session.SubmitEdit(tool.CurrentEdit).Refusal);
        Assert.AreEqual(WorldEditRefusal.WorldChanging, tool.Commit().Refusal);
        Assert.IsFalse(session.Query(Cell).IsOccupied);
        Assert.AreEqual(0, session.QueryEditLog().Count);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.ChooseWorldChange(WorldChangeChoice.Discard).Status);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.AdvanceWorldChange().Status);
        Assert.AreSame(original, session.World);
        Assert.IsTrue(tool.HasDraft);
        Assert.AreEqual(WorldChangeStatus.Cancelled, session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.IsTrue(tool.HasDraft);
        Assert.IsFalse(session.IsChangingWorld);
    }

    [TestMethod]
    public void Saving_before_departure_keeps_the_draft_out_of_the_save_until_explicitly_committed()
    {
        using var harness = Blank();
        var session = harness.Session;
        var saveId = session.Save().SaveId!.Value;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.IsTrue(session.Save().Succeeded);
        Assert.IsTrue(tool.HasDraft);
        session.BeginWorldChange(new(WorldChangeKind.Quit, null));
        session.ChooseWorldChange(WorldChangeChoice.SaveThenSwitch);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.AdvanceWorldChange().Status);
        Assert.IsTrue(tool.HasDraft);
        var restored = new VoxelDevelopmentSession(harness.Catalog, session.Identity, harness.Store, harness.Composites);
        try
        {
            Assert.IsTrue(restored.Load(saveId).Succeeded);
            Assert.IsFalse(restored.Query(Cell).IsOccupied);
            Assert.IsFalse(restored.HasEditorDraft);
        }
        finally { restored.Detach(); }
        Assert.AreEqual(WorldEditRefusal.WorldChanging, tool.Commit().Refusal);
        Assert.IsTrue(tool.HasDraft);
        Assert.IsFalse(session.Query(Cell).IsOccupied);
        Assert.AreEqual(WorldChangeStatus.Cancelled, session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsTrue(session.Query(Cell).IsOccupied);
        Assert.AreEqual(WorldChangeStatus.Switching,
            session.BeginWorldChange(new(WorldChangeKind.Quit, WorldChangeChoice.SaveThenSwitch)).Status);
        Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        Assert.AreEqual(WorldChangeStatus.Completed, session.AdvanceWorldChange().Status);
    }

    [TestMethod]
    public void Reload_after_saving_and_discarding_a_draft_keeps_the_original_reload_target()
    {
        using var harness = Blank();
        var session = harness.Session;
        var original = session.Save().SaveId!.Value;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        session.BeginWorldChange(new(WorldChangeKind.ReloadSaved, null));
        session.ChooseWorldChange(WorldChangeChoice.SaveAsThenReload);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.AdvanceWorldChange().Status);
        var copy = session.SaveId;
        Assert.AreNotEqual(original, copy);
        Assert.IsTrue(tool.HasDraft);
        tool.Discard();
        WorldChangeResult result = new(WorldChangeStatus.Switching);
        Assert.IsTrue(SpinWait.SpinUntil(() => (result = session.AdvanceWorldChange()).Status != WorldChangeStatus.Switching,
            TimeSpan.FromSeconds(10)), "Reload did not finish within the bounded wait.");
        Assert.AreEqual(WorldChangeStatus.Completed, result.Status);
        Assert.AreEqual(original, session.SaveId);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsFalse(session.Query(Cell).IsOccupied);
    }

    [TestMethod]
    public void Selection_and_vehicle_level_cannot_redirect_an_existing_draft()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        using var observer = new WorldEditorObserver(session, new(76, 16, -8), new(76, 2, 16));
        var vehicle = ObservedObject.Vehicle(session.Vehicles[0].Id);
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.Select(vehicle));
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(WorldEditorObservationStatus.DraftRequired, observer.EnterVehicleParts());
        Assert.AreEqual(WorldEditorObservationStatus.DraftRequired, observer.Select(ObservedObject.WorldCell(StraightRailFixture.LeftRailSample)));
        Assert.AreEqual(WorldEditorObservationStatus.DraftRequired, observer.ClearSelection());
        Assert.AreEqual(vehicle, observer.Selection);
        Assert.AreEqual(Cell, tool.Position);
        tool.Discard();
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.EnterVehicleParts());
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(WorldEditorObservationStatus.DraftRequired, observer.ReturnToWhole());
        tool.Discard();
        Assert.AreEqual(WorldEditorObservationStatus.Selected, observer.ReturnToWhole());
    }

    [TestMethod]
    public void Draft_commit_is_suspended_until_departure_is_cancelled()
    {
        using var harness = Blank();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        session.BeginWorldChange(new(WorldChangeKind.Quit, null));
        var nextEdit = session.NextEditId;
        Assert.AreEqual(WorldEditRefusal.WorldChanging, tool.Commit().Refusal);
        Assert.AreEqual(nextEdit, session.NextEditId);
        Assert.AreEqual(0, session.QueryEditLog().Count);
        Assert.IsTrue(tool.HasDraft);
        Assert.IsFalse(session.Query(Cell).IsOccupied);
        Assert.AreEqual(WorldChangeStatus.Cancelled, session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsTrue(session.Query(Cell).IsOccupied);
    }

    private static WorldEditorHarness Blank()
    {
        var harness = WorldEditorHarness.Content();
        BlankWorldSource.TryIdentity(16, 8, 16, 12, out var identity);
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        return harness;
    }
}

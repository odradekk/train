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

    [TestMethod]
    public void Hover_is_transient_but_precise_input_and_rejected_commits_pin_the_same_preview()
    {
        using var harness = Blank();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetActive(true);
        var authority = WorldSaveAuthority.Fingerprint(session);
        tool.SetHeight(2);
        tool.Hover(Cell);
        Assert.AreEqual(Cell, tool.CurrentEdit.Targets[0].Position);
        Assert.IsFalse(tool.HasDraft);
        Assert.AreEqual(authority, WorldSaveAuthority.Fingerprint(session));
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Remove);
        tool.Hover(new(3, 2, 1));
        Assert.AreEqual(Cell, tool.Position);
        Assert.IsFalse(tool.SetHeight(3));
        Assert.IsFalse(tool.SetActive(false));
        Assert.AreEqual(VoxelCellSetStatus.Empty, ((VoxelCellSetResult)tool.Commit().Result!).Status);
        Assert.IsTrue(tool.HasDraft);
        Assert.IsFalse(session.Query(Cell).IsOccupied);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsTrue(session.Query(Cell).IsOccupied);
        Assert.IsTrue(tool.SetHeight(3));
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
        Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State);
        Assert.IsFalse(tool.HasDraft);
        Assert.IsTrue(session.Query(Cell).IsOccupied);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.AdvanceWorldChange().Status);
        session.ChooseWorldChange(WorldChangeChoice.SaveThenSwitch);
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
        while (session.AdvanceWorldChange().Status == WorldChangeStatus.Switching) { }
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
    public void Explicit_draft_commit_during_departure_does_not_reopen_other_world_input()
    {
        using var harness = Blank();
        var session = harness.Session;
        using var tool = new WorldEditorVoxelTool(session);
        tool.SetExact(Cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        session.BeginWorldChange(new(WorldChangeKind.Quit, null));
        WorldEditSubmission? other = null;
        WorldEditSubmission? nestedDraft = null;
        void TryOtherInput() => other = session.SubmitEdit(new VoxelAddEdit(new(2, 2, 1),
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity));
        void TryNestedDraft() => nestedDraft = tool.Commit();
        session.Changed += TryOtherInput;
        session.Changed += TryNestedDraft;
        try { Assert.AreEqual(WorldEditState.Executed, tool.Commit().Record!.State); }
        finally { session.Changed -= TryOtherInput; session.Changed -= TryNestedDraft; }
        Assert.AreEqual(WorldEditRefusal.WorldChanging, other!.Refusal);
        Assert.AreEqual(WorldEditRefusal.DraftCommitInProgress, nestedDraft!.Refusal);
        Assert.AreEqual(1, session.QueryEditLog().Count);
        Assert.IsTrue(session.Query(Cell).IsOccupied);
        Assert.IsFalse(session.Query(new(2, 2, 1)).IsOccupied);
        session.ChooseWorldChange(WorldChangeChoice.Cancel);
    }

    private static WorldEditorHarness Blank()
    {
        var harness = WorldEditorHarness.Content();
        BlankWorldSource.TryIdentity(16, 8, 16, 12, out var identity);
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        return harness;
    }
}

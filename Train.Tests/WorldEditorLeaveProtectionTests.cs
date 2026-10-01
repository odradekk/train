using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
[TestCategory("scope:persistence")]
public sealed class WorldEditorLeaveProtectionTests
{
    private static readonly WorldVoxelCoord FirstEdit = new(1, 2, 1);
    private static readonly WorldVoxelCoord LaterEdit = new(2, 2, 1);

    [DataTestMethod]
    [DataRow(WorldChangeKind.NewBlank)]
    [DataRow(WorldChangeKind.Load)]
    [DataRow(WorldChangeKind.Reset)]
    [DataRow(WorldChangeKind.SwitchConfiguration)]
    [DataRow(WorldChangeKind.ReturnToWorkspace)]
    [DataRow(WorldChangeKind.ReturnToMainMenu)]
    [DataRow(WorldChangeKind.Quit)]
    [DataRow(WorldChangeKind.ReloadSaved)]
    public void Every_leave_kind_requires_a_choice_for_unsaved_worlds(WorldChangeKind kind)
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var request = Request(kind, session.Save("Target").SaveId);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(LaterEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);

        var result = session.BeginWorldChange(request);

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, result.Status);
        Assert.IsTrue(session.IsChangingWorld);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsTrue(session.Query(LaterEdit).IsOccupied);
    }

    [TestMethod]
    public void Never_saved_blank_world_requires_a_choice_before_leaving()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        Assert.IsTrue(BlankWorldSource.TryIdentity(9, 8, 7, 6, out var identity));
        var session = workspace.NewBlank(identity);
        harness.Attach(session);

        Assert.IsNull(session.SaveId);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.Quit, null)).Status);
    }

    [TestMethod]
    public void Clean_leave_starts_switching_without_a_choice()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);

        var result = harness.Session.BeginWorldChange(
            new WorldChangeRequest(WorldChangeKind.ReturnToWorkspace, null));

        Assert.AreEqual(WorldChangeStatus.Switching, result.Status);
        Assert.IsTrue(harness.Session.IsChangingWorld);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Pending_choice_blocks_time_and_world_input_and_cancel_restores_the_original_run_state(bool initiallyPaused)
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        if (!initiallyPaused)
            Assert.IsTrue(session.ResumeSimulation().Succeeded);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var step = session.SimulationStep;
        var nextEdit = session.NextEditId;

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.ReturnToMainMenu, null)).Status);
        Assert.AreEqual(0UL, session.CollectHostSteps(10));
        Assert.IsFalse(session.SubmitEdit(new VoxelRemoveEdit(FirstEdit)).Accepted);
        Assert.IsFalse(session.AdvanceSimulation(1).Succeeded);
        Assert.IsFalse(session.PauseSimulation().Succeeded);
        Assert.IsFalse(session.ResumeSimulation().Succeeded);
        Assert.AreEqual(step, session.SimulationStep);
        Assert.AreEqual(nextEdit, session.NextEditId);

        Assert.AreEqual(WorldChangeStatus.Cancelled, session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.AreEqual(initiallyPaused, session.SimulationPaused);
        Assert.AreEqual(0UL, session.CollectHostSteps(10));
        Assert.AreEqual(initiallyPaused ? 0UL : 1UL, session.CollectHostSteps(1.0 / 60.0));
        Assert.AreEqual(step, session.SimulationStep);
    }

    [TestMethod]
    public void Saved_leave_can_fail_after_publication_without_losing_the_saved_file_or_current_world()
    {
        using var harness = WorldEditorHarness.Content();
        var store = new CallbackStore(harness.Store);
        Assert.AreEqual(WorldEditorEstablishStatus.Established,
            WorldEditorSession.TryEstablish(WorldEditorConfigurations.StraightRailTrainId,
                harness.Catalog, harness.Composites, store, out var established));
        harness.Attach(established!);
        var session = established!;
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var world = session.World;
        var step = session.SimulationStep;
        var missing = Guid.NewGuid();

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.Load, null, SaveId: missing, SaveDisplayName: "Recovery")).Status);
        Assert.AreEqual(WorldChangeStatus.Switching, session.ChooseWorldChange(WorldChangeChoice.SaveThenSwitch).Status);
        store.AfterCapture = () => AssertWorldChangeLocked(session);
        var saved = session.AdvanceWorldChange();
        Assert.AreEqual(WorldChangeStatus.Switching, saved.Status);
        var recoveryId = session.SaveId;
        Assert.IsNotNull(recoveryId);
        Assert.IsTrue(File.Exists(harness.Store.PathFor(recoveryId.Value)));

        var failed = session.AdvanceWorldChange();
        Assert.AreEqual(WorldChangeStatus.CandidateFailed, failed.Status);
        Assert.AreSame(world, session.World);
        Assert.AreEqual(step, session.SimulationStep);
        Assert.IsFalse(session.SimulationPaused);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.IsTrue(File.Exists(harness.Store.PathFor(recoveryId.Value)));
        Assert.AreEqual(0UL, session.CollectHostSteps(0));
    }

    [TestMethod]
    public void Leave_can_be_cancelled_after_its_save_stage_without_invalidating_the_file()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var world = session.World;

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.Quit, null, SaveDisplayName: "Recovery")).Status);
        Assert.AreEqual(WorldChangeStatus.Switching,
            session.ChooseWorldChange(WorldChangeChoice.SaveThenSwitch).Status);
        Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        var saved = session.SaveId;
        Assert.IsNotNull(saved);

        Assert.AreEqual(WorldChangeStatus.Cancelled,
            session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.AreSame(world, session.World);
        Assert.IsTrue(File.Exists(harness.Store.PathFor(saved.Value)));
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Reload_saved_keeps_the_original_target_after_saving_changes_as_a_copy()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        var original = session.Save("Original").SaveId!.Value;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.ReloadSaved, null)).Status);
        Assert.AreEqual(WorldChangeStatus.Switching,
            session.ChooseWorldChange(WorldChangeChoice.SaveAsThenReload).Status);
        Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        var copy = session.SaveId;
        Assert.IsNotNull(copy);
        Assert.AreNotEqual(original, copy.Value);
        Assert.IsTrue(File.Exists(harness.Store.PathFor(original)));
        Assert.IsTrue(File.Exists(harness.Store.PathFor(copy.Value)));

        Assert.AreEqual(WorldChangeStatus.Completed, session.AdvanceWorldChange().Status);
        Assert.AreEqual(original, session.SaveId);
        Assert.IsFalse(session.Query(FirstEdit).IsOccupied);
        Assert.IsTrue(session.SimulationPaused);
    }

    [TestMethod]
    public void Reload_failure_after_save_as_keeps_the_current_world_and_both_files()
    {
        using var harness = WorldEditorHarness.Content();
        var store = new ReadFailingStore(harness.Store);
        Assert.AreEqual(WorldEditorEstablishStatus.Established,
            WorldEditorSession.TryEstablish(WorldEditorConfigurations.StraightRailTrainId,
                harness.Catalog, harness.Composites, store, out var session));
        harness.Attach(session!);
        var original = session!.Save("Original").SaveId!.Value;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var world = session.World;

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.ReloadSaved, null)).Status);
        Assert.AreEqual(WorldChangeStatus.Switching,
            session.ChooseWorldChange(WorldChangeChoice.SaveAsThenReload).Status);
        Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        var copy = session.SaveId!.Value;
        store.FailingRead = original;
        store.BeforeRead = () => AssertWorldChangeLocked(session);

        Assert.AreEqual(WorldChangeStatus.CandidateFailed, session.AdvanceWorldChange().Status);
        Assert.AreSame(world, session.World);
        Assert.AreEqual(copy, session.SaveId);
        Assert.IsTrue(session.Query(FirstEdit).IsOccupied);
        Assert.IsTrue(File.Exists(harness.Store.PathFor(original)));
        Assert.IsTrue(File.Exists(harness.Store.PathFor(copy)));
    }

    [TestMethod]
    public void Saving_a_captured_state_does_not_mark_a_later_edit_clean_or_pause_the_session()
    {
        using var harness = WorldEditorHarness.Content();
        var store = new CallbackStore(harness.Store);
        Assert.AreEqual(WorldEditorEstablishStatus.Established,
            WorldEditorSession.TryEstablish(WorldEditorConfigurations.StraightRailTrainId,
                harness.Catalog, harness.Composites, store, out var session));
        harness.Attach(session!);
        Assert.IsTrue(session!.ResumeSimulation().Succeeded);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        store.AfterCapture = () =>
        {
            Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(LaterEdit, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
            store.AfterCapture = null;
        };

        var saved = session.Save("Captured");

        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.IsFalse(session.SimulationPaused);
        Assert.IsTrue(session.HasUnsavedChanges);
        var reader = new VoxelDevelopmentSession(harness.Catalog, session.Identity, harness.Store, harness.Composites);
        try
        {
            Assert.AreEqual(WorldSaveStatus.Loaded, reader.Load(saved.SaveId!.Value).Status);
            Assert.IsTrue(reader.Query(FirstEdit).IsOccupied);
            Assert.IsFalse(reader.Query(LaterEdit).IsOccupied);
        }
        finally { reader.Detach(); }

        var copy = session.SaveAs("Copy");
        Assert.AreEqual(WorldSaveStatus.Saved, copy.Status);
        Assert.IsFalse(session.SimulationPaused);
    }

    [TestMethod]
    public void Reload_without_a_saved_identity_fails_without_starting_a_transition()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        Assert.IsTrue(BlankWorldSource.TryIdentity(9, 8, 7, 6, out var identity));
        var session = workspace.NewBlank(identity);
        harness.Attach(session);

        var result = session.BeginWorldChange(new WorldChangeRequest(
            WorldChangeKind.ReloadSaved, WorldChangeChoice.Discard));

        Assert.AreEqual(WorldChangeStatus.CandidateFailed, result.Status);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.IsNull(session.SaveId);
        Assert.IsTrue(session.HasUnsavedChanges);
    }

    private static WorldChangeRequest Request(WorldChangeKind kind, Guid? saveId) => kind switch
    {
        WorldChangeKind.NewBlank => BlankRequest(),
        WorldChangeKind.Load => new WorldChangeRequest(kind, null, SaveId: saveId),
        WorldChangeKind.SwitchConfiguration => new WorldChangeRequest(kind, null,
            ConfigurationId: WorldEditorConfigurations.StraightRailEmptyId),
        _ => new WorldChangeRequest(kind, null)
    };

    private static void AssertWorldChangeLocked(VoxelDevelopmentSession session)
    {
        Assert.IsTrue(session.IsChangingWorld);
        Assert.IsFalse(session.SubmitEdit(new VoxelRemoveEdit(FirstEdit)).Accepted);
        Assert.IsFalse(session.AdvanceSimulation(1).Succeeded);
    }

    private static WorldChangeRequest BlankRequest()
    {
        Assert.IsTrue(BlankWorldSource.TryIdentity(9, 8, 7, 42, out var identity));
        return new WorldChangeRequest(WorldChangeKind.NewBlank, null, BlankIdentity: identity);
    }

    private sealed class ReadFailingStore(FileWorldSaveStore files) : IWorldSaveStore
    {
        public Guid? FailingRead { get; set; }
        public Action? BeforeRead { get; set; }

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status)
        {
            BeforeRead?.Invoke();
            if (id == FailingRead)
            {
                json = "";
                status = WorldSaveStatus.ReadFailed;
                return false;
            }
            return files.TryRead(id, out json, out status);
        }

        public bool TryWrite(Guid id, string json, out WorldSaveStatus status) => files.TryWrite(id, json, out status);
    }

    private sealed class CallbackStore(FileWorldSaveStore files) : IWorldSaveStore
    {
        public Action? AfterCapture { get; set; }

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status) => files.TryRead(id, out json, out status);

        public bool TryWrite(Guid id, string json, out WorldSaveStatus status)
        {
            AfterCapture?.Invoke();
            return files.TryWrite(id, json, out status);
        }
    }
}

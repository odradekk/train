using Train.Core.Content.Voxels;
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
    [DataRow(WorldChangeKind.Reset)]
    [DataRow(WorldChangeKind.Load)]
    public void Old_slice_synchronous_choice_does_not_leave_an_invisible_transition(WorldChangeKind kind)
    {
        using var harness = WorldEditorHarness.Content();
        var session = new VoxelDevelopmentSession(harness.Catalog, VoxelWorldIdentity.Slice1,
            harness.Store, harness.Composites);
        harness.Attach(session);
        var saved = session.Save("Original").SaveId!.Value;
        var cell = new WorldVoxelCoord(4, 4, 4);
        Assert.IsTrue(session.SubmitEdit(new VoxelRemoveEdit(cell)).Accepted);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        var world = session.World;
        var step = session.SimulationStep;

        var result = session.TryChangeWorld(new WorldChangeRequest(kind, null, SaveId: kind == WorldChangeKind.Load ? saved : null));

        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, result.Status);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.AreSame(world, session.World);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.AreEqual(step + 1, session.SimulationStep);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(cell,
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            session.BeginWorldChange(new WorldChangeRequest(WorldChangeKind.Quit, null)).Status);
        Assert.AreEqual(WorldChangeStatus.Cancelled,
            session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.IsFalse(session.IsChangingWorld);
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

        var failed = AdvanceUntilSettled(session);
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

        Assert.AreEqual(WorldChangeStatus.Completed, AdvanceUntilSettled(session).Status);
        Assert.AreEqual(original, session.SaveId);
        Assert.IsFalse(session.Query(FirstEdit).IsOccupied);
        Assert.IsTrue(session.SimulationPaused);
    }

    [TestMethod]
    public void Console_reload_save_alias_preserves_the_original_and_saves_changes_as_a_copy()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        var original = session.Save("Original").SaveId!.Value;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit,
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        new VoxelConsoleCommands(session).Execute(["reload", "save"]);

        Assert.AreEqual(original, session.SaveId);
        Assert.IsFalse(session.Query(FirstEdit).IsOccupied);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.IsTrue(session.SimulationPaused);
        var copies = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store)
            .ListSaves().Where(entry => entry.Id != original).ToArray();
        Assert.AreEqual(1, copies.Length);
        Assert.AreEqual(WorldChangeStatus.Completed, session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.Load, WorldChangeChoice.Discard, SaveId: copies[0].Id)).Status);
        Assert.IsTrue(session.Query(FirstEdit).IsOccupied);
    }

    [TestMethod]
    public void Reload_failure_after_save_as_keeps_the_current_world_and_both_files()
    {
        using var harness = WorldEditorHarness.Content();
        var failingStore = new ReadFailingStore(harness.Store);
        using var store = new BlockingReadStore(failingStore);
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
        failingStore.FailingRead = original;
        store.BlockedId = original;
        Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        Assert.IsTrue(store.ReadStarted.Wait(TimeSpan.FromSeconds(5)), "The candidate read did not start.");
        AssertWorldChangeLocked(session);
        store.ReleaseRead.Set();

        Assert.AreEqual(WorldChangeStatus.CandidateFailed, AdvanceUntilSettled(session).Status);
        Assert.AreSame(world, session.World);
        Assert.AreEqual(copy, session.SaveId);
        Assert.IsTrue(session.Query(FirstEdit).IsOccupied);
        Assert.IsTrue(File.Exists(harness.Store.PathFor(original)));
        Assert.IsTrue(File.Exists(harness.Store.PathFor(copy)));
    }

    [TestMethod]
    public void Reload_candidate_can_be_cancelled_while_its_read_is_blocked()
    {
        using var harness = WorldEditorHarness.Content();
        using var store = new BlockingReadStore(harness.Store);
        Assert.AreEqual(WorldEditorEstablishStatus.Established,
            WorldEditorSession.TryEstablish(WorldEditorConfigurations.StraightRailTrainId,
                harness.Catalog, harness.Composites, store, out var session));
        harness.Attach(session!);
        var original = session!.Save("Original").SaveId!.Value;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit,
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var second = session.SaveAs("Second").SaveId!.Value;
        Assert.AreEqual(WorldChangeStatus.Completed, session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.Load, WorldChangeChoice.Discard, SaveId: original)).Status);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(LaterEdit,
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var world = session.World;
        var paused = session.SimulationPaused;
        store.BlockedId = original;
        var readsBeforeCancellation = store.ReadCount;

        Assert.AreEqual(WorldChangeStatus.Switching,
            session.BeginWorldChange(new WorldChangeRequest(
                WorldChangeKind.ReloadSaved, WorldChangeChoice.Discard)).Status);
        Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        Assert.IsTrue(store.ReadStarted.Wait(TimeSpan.FromSeconds(5)), "The candidate read did not start.");
        Assert.AreEqual(readsBeforeCancellation + 1, store.ReadCount);

        Assert.AreEqual(WorldChangeStatus.Cancelled,
            session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.AreSame(world, session.World);
        Assert.AreEqual(paused, session.SimulationPaused);
        Assert.IsTrue(session.Query(LaterEdit).IsOccupied);

        Assert.AreEqual(WorldChangeStatus.Switching,
            session.BeginWorldChange(new WorldChangeRequest(
                WorldChangeKind.Load, WorldChangeChoice.Discard, SaveId: second)).Status);
        for (var attempt = 0; attempt < 8; attempt++)
            Assert.AreEqual(WorldChangeStatus.Switching, session.AdvanceWorldChange().Status);
        Assert.AreEqual(readsBeforeCancellation + 1, store.ReadCount,
            "A second candidate read started before the abandoned read completed.");
        Assert.AreSame(world, session.World);
        Assert.IsTrue(session.Query(LaterEdit).IsOccupied);

        store.ReleaseRead.Set();
        Assert.AreEqual(WorldChangeStatus.Completed,
            AdvanceUntilSettled(session).Status);
        Assert.AreEqual(readsBeforeCancellation + 2, store.ReadCount);
        Assert.IsTrue(session.Query(FirstEdit).IsOccupied);
        Assert.IsFalse(session.Query(LaterEdit).IsOccupied);
        Assert.AreEqual(second, session.SaveId);
    }

    [TestMethod]
    public void Changed_handler_can_cancel_initial_confirmation_without_publishing_a_stale_request()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(FirstEdit,
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Accepted);
        var notifications = new List<(WorldChangeRequest? Request, WorldChangeResult Result)>();
        var cancelled = false;
        session.WorldChangeChanged += (request, result) => notifications.Add((request, result));
        session.Changed += CancelOnFirstConfirmation;

        var started = session.BeginWorldChange(
            new WorldChangeRequest(WorldChangeKind.ReturnToWorkspace, null));

        session.Changed -= CancelOnFirstConfirmation;
        Assert.AreEqual(WorldChangeStatus.Cancelled, started.Status);
        Assert.IsFalse(session.IsChangingWorld);
        Assert.AreEqual(1, notifications.Count);
        Assert.IsNotNull(notifications[0].Request);
        Assert.AreEqual(WorldChangeKind.ReturnToWorkspace, notifications[0].Request!.Kind);
        Assert.AreEqual(WorldChangeStatus.Cancelled, notifications[0].Result.Status);
        Assert.IsFalse(notifications.Any(item => item.Result.Status == WorldChangeStatus.ChoiceRequired));

        void CancelOnFirstConfirmation()
        {
            if (cancelled)
                return;
            cancelled = true;
            Assert.AreEqual(WorldChangeStatus.Cancelled,
                session.ChooseWorldChange(WorldChangeChoice.Cancel).Status);
        }
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

    private static WorldChangeResult AdvanceUntilSettled(VoxelDevelopmentSession session)
    {
        var result = session.AdvanceWorldChange();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (result.Status == WorldChangeStatus.Switching && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Yield();
            result = session.AdvanceWorldChange();
        }
        Assert.AreNotEqual(WorldChangeStatus.Switching, result.Status, "World change did not settle within five seconds.");
        return result;
    }

    private sealed class ReadFailingStore(FileWorldSaveStore files) : IWorldSaveStore
    {
        public Guid? FailingRead { get; set; }

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status)
        {
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

    private sealed class BlockingReadStore(IWorldSaveStore files) : IWorldSaveStore, IDisposable
    {
        private int _readCount;
        public Guid? BlockedId { get; set; }
        public int ReadCount => Volatile.Read(ref _readCount);
        public ManualResetEventSlim ReadStarted { get; } = new();
        public ManualResetEventSlim ReleaseRead { get; } = new();

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status)
        {
            Interlocked.Increment(ref _readCount);
            if (id == BlockedId)
            {
                ReadStarted.Set();
                ReleaseRead.Wait();
            }
            return files.TryRead(id, out json, out status);
        }

        public bool TryWrite(Guid id, string json, out WorldSaveStatus status) =>
            files.TryWrite(id, json, out status);

        public void Dispose()
        {
            ReleaseRead.Set();
            ReadStarted.Dispose();
            ReleaseRead.Dispose();
        }
    }
}

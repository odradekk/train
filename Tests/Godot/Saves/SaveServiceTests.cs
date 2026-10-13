using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Saves;

[TestClass]
[TestCategory("integration")]
public sealed class SaveServiceTests
{
    [TestMethod]
    public void Automatic_interval_uses_real_time_rotates_three_slots_and_can_be_disabled()
    {
        using var run = new Run();
        run.Service.SetAutoSaveInterval(TimeSpan.FromSeconds(10));
        run.Process();
        run.Now = 9;
        run.Process();
        Assert.IsFalse(run.Service.HasPendingWork);
        for (var i = 1; i <= 4; i++)
        {
            run.Now = i * 10;
            run.Process();
            run.Drain();
        }
        var entries = run.Service.List();
        Assert.AreEqual(3, entries.Count);
        Assert.IsTrue(entries.All(entry => entry.Automatic));
        Assert.AreEqual(40L, run.Store.ReadHeader(run.Store.AutoPathFor(run.Id, 0)).SavedAt.ToUnixTimeSeconds());
        Assert.AreEqual(20L, run.Store.ReadHeader(run.Store.AutoPathFor(run.Id, 1)).SavedAt.ToUnixTimeSeconds());
        Assert.AreEqual(30L, run.Store.ReadHeader(run.Store.AutoPathFor(run.Id, 2)).SavedAt.ToUnixTimeSeconds());
        Assert.IsTrue(run.Service.Status!.Unsaved);
        run.Service.SetAutoSaveInterval(null);
        run.Now = 1000;
        run.Process();
        Assert.IsFalse(run.Service.HasPendingWork);
        Assert.AreEqual(4, run.Host.Factory.CaptureCount);
    }

    [TestMethod]
    public void Manual_save_conflict_is_busy_and_one_pending_automatic_capture_uses_latest_content()
    {
        using var run = new Run(blockFirstWrite: true);
        run.Service.SetAutoSaveInterval(TimeSpan.FromSeconds(10));
        run.Process();
        Assert.IsNull(run.Service.Save());
        run.WaitWriter();
        Assert.AreEqual("busy", run.Service.SaveAs("conflict")?.Code);
        for (var i = 1; i <= 3; i++) { run.Now = i * 10; run.Process(); }
        Assert.AreEqual(1, run.Host.Factory.CaptureCount);
        run.PublishContent(7);
        run.Host.CaptureContent = (view, _) => new ContentResult<IContentCapture>.Success(
            new FixedContentCapture($"latest-{view.PersistentRevision.Value}", view.PersistentRevision));
        run.Release();
        run.Drain();
        Assert.AreEqual(2, run.Host.Factory.CaptureCount);
        Assert.AreEqual("latest-7", run.Store.Read(run.Store.AutoPathFor(run.Id, 0)).ContentPayload);
        Assert.IsTrue(run.Service.Status!.Unsaved);
    }

    [TestMethod]
    public void Pending_automatic_capture_is_discarded_when_binding_changes()
    {
        using var run = new Run(blockFirstWrite: true);
        run.Service.SetAutoSaveInterval(TimeSpan.FromSeconds(10));
        run.Process();
        run.Service.Save();
        run.WaitWriter();
        run.Now = 10;
        run.Process();
        var oldBinding = run.Service.Status!.Binding;
        run.ConfigureRestore();
        run.Host.Host.BeginRestore("core-state", "content-state", HostFixture.Options, new(run.Id, "new run"));
        run.Host.PumpUntil(() => !run.Host.Host.IsBusy);
        run.Process();
        Assert.AreNotEqual(oldBinding, run.Service.Status!.Binding);
        run.Release();
        run.Drain();
        Assert.AreEqual(1, run.Host.Factory.CaptureCount);
        Assert.IsTrue(run.Service.Status.Unsaved);
        Assert.IsFalse(File.Exists(run.Store.AutoPathFor(run.Id, 0)));
    }

    [TestMethod]
    public void Save_save_as_and_export_publish_files_and_only_manual_success_confirms_identity()
    {
        using var run = new Run();
        var export = Path.Combine(run.Root, "exports", "save.json");
        Assert.IsNull(run.Service.Export(export));
        run.Drain();
        Assert.IsTrue(File.Exists(export));
        Assert.AreEqual(run.Id, run.Store.ReadHeader(export).Id);
        Assert.AreEqual(run.Id, run.Service.Status!.Identity.Id);
        Assert.IsTrue(run.Service.Status.Unsaved);
        File.WriteAllText(export, "existing external file");
        Assert.AreEqual("overwrite-confirmation", run.Service.Export(export)?.Code);
        Assert.AreEqual("existing external file", File.ReadAllText(export));
        Assert.IsNull(run.Service.Export(export, true));
        run.Drain();
        Assert.AreEqual(run.Id, run.Store.ReadHeader(export).Id);
        Assert.IsTrue(run.Service.Status.Unsaved);
        Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.IsFalse(run.Service.Status.Unsaved);
        var old = File.ReadAllBytes(run.Store.PathFor(run.Id));
        Assert.IsNull(run.Service.SaveAs("second"));
        run.Drain();
        var second = run.Service.Status.Identity;
        Assert.AreNotEqual(run.Id, second.Id);
        Assert.AreEqual("second", second.Name);
        Assert.IsFalse(run.Service.Status.Unsaved);
        Assert.AreEqual(second.Id, run.Store.ReadHeader(run.Store.PathFor(second.Id)).Id);
        CollectionAssert.AreEqual(old, File.ReadAllBytes(run.Store.PathFor(run.Id)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Save_as_failure_or_cancellation_preserves_identity_and_unsaved_state(bool cancel)
    {
        using var run = new Run(blockFirstWrite: true, failPublish: !cancel);
        var before = run.Service.Status!;
        Assert.IsNull(run.Service.SaveAs("failed replacement"));
        run.WaitWriter();
        if (cancel) run.Service.Cancel();
        run.Release();
        run.Drain();
        Assert.AreEqual(before.Identity, run.Service.Status!.Identity);
        Assert.IsTrue(run.Service.Status.Unsaved);
        Assert.AreEqual(0, run.Service.List().Count);
    }

    [TestMethod]
    public void Recreated_service_replaces_oldest_automatic_slot_and_preserves_other_files()
    {
        using var run = new Run();
        foreach (var (slot, seconds) in new[] { (0, 30), (1, 10), (2, 20) })
        {
            var document = SaveContainerTests.Document();
            document = document with { Header = document.Header with { Id = run.Id, SavedAt = DateTimeOffset.FromUnixTimeSeconds(seconds) } };
            Directory.CreateDirectory(run.Store.DirectoryFor(run.Id));
            File.WriteAllText(run.Store.AutoPathFor(run.Id, slot), SaveContainer.Encode(document));
        }
        var first = File.ReadAllBytes(run.Store.AutoPathFor(run.Id, 0));
        var third = File.ReadAllBytes(run.Store.AutoPathFor(run.Id, 2));
        run.RecreateService();
        run.Service.SetAutoSaveInterval(TimeSpan.FromSeconds(10));
        run.Process();
        run.Now = 40;
        run.Process();
        run.Drain();
        Assert.AreEqual(40L, run.Store.ReadHeader(run.Store.AutoPathFor(run.Id, 1)).SavedAt.ToUnixTimeSeconds());
        CollectionAssert.AreEqual(first, File.ReadAllBytes(run.Store.AutoPathFor(run.Id, 0)));
        CollectionAssert.AreEqual(third, File.ReadAllBytes(run.Store.AutoPathFor(run.Id, 2)));
    }

    [TestMethod]
    public void Import_validates_without_activation_preserves_payload_bytes_and_creates_new_identity()
    {
        using var run = new Run();
        const string core = " \r\n核心 opaque 🚂 \t";
        const string content = "content-state";
        var source = run.Source(core, content);
        var sourceBefore = File.ReadAllBytes(source);
        var original = run.Store.PathFor(run.Id);
        File.Copy(source, original);
        var originalBefore = File.ReadAllBytes(original);
        var (coreCandidate, contentCandidate) = run.ConfigureRestore(core, content);
        var status = run.Service.Status;
        Assert.IsNull(run.Service.Import(source));
        run.Drain();
        Assert.AreEqual(0, coreCandidate.ActivationCount);
        Assert.AreEqual(0, contentCandidate.Activations);
        Assert.AreEqual(1, coreCandidate.DisposeCount);
        Assert.AreEqual(1, contentCandidate.Disposals);
        Assert.AreEqual(status, run.Service.Status);
        var imported = run.Service.List().Single(entry => entry.Header.Id != run.Id);
        var document = run.Store.Read(imported.Path);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(core), Encoding.UTF8.GetBytes(document.CorePayload));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(content), Encoding.UTF8.GetBytes(document.ContentPayload));
        CollectionAssert.AreEqual(sourceBefore, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(originalBefore, File.ReadAllBytes(original));
    }

    [TestMethod]
    public void Import_missing_confirmation_cancel_and_validation_failure_leave_no_entry()
    {
        using var run = new Run();
        var source = run.Source();
        var missing = new MissingCoreReference(new CoreDefinitionReference.Voxel(new("fixture:missing")), "voxel");
        var (core, content) = run.ConfigureRestore(missing: missing);
        run.Service.Import(source);
        run.PumpUntil(() => run.Service.Missing is not null);
        Assert.AreEqual(0, run.Service.List().Count);
        run.Service.Cancel();
        run.Drain();
        Assert.AreEqual(1, core.DisposeCount);
        Assert.AreEqual(1, content.Disposals);
        Assert.AreEqual(0, run.Service.List().Count);
        run.Host.Core.DecodeStateCall = _ => new CoreResult<ICoreCapture>.Failure([new("invalid-core", "state", null, [])]);
        run.Service.Import(source);
        run.Drain();
        Assert.AreEqual(0, run.Service.List().Count);
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public void Missing_import_confirmation_publishes_without_activating_candidates()
    {
        using var run = new Run();
        var source = run.Source();
        var (core, content) = run.ConfigureRestore(missing: new(
            new CoreDefinitionReference.Voxel(new("fixture:missing")), "voxel"));
        run.Service.Import(source);
        run.PumpUntil(() => run.Service.Missing is not null);
        Assert.IsTrue(run.Service.ConfirmMissing());
        run.Drain();
        Assert.AreEqual(1, run.Service.List().Count);
        Assert.AreEqual(0, core.ActivationCount);
        Assert.AreEqual(0, content.Activations);
        Assert.AreEqual(1, core.DisposeCount);
        Assert.AreEqual(1, content.Disposals);
    }

    [TestMethod]
    public void Example_opens_with_unique_unsaved_identities_and_saves_only_the_copy()
    {
        using var run = new Run();
        var source = run.Source();
        var original = File.ReadAllBytes(source);
        var document = run.Store.Read(source);
        run.Service.ReturnToMenu();
        run.Service.ChooseLeave(LeaveChoice.Discard);
        run.ConfigureRestore();
        Assert.IsNull(run.Service.StartExample(document));
        run.Drain();
        var first = run.Service.Status!.Identity;
        Assert.AreNotEqual(document.Header.Id, first.Id);
        Assert.AreEqual(document.Header.Name, first.Name);
        Assert.IsTrue(run.Service.Status.Unsaved);
        Assert.IsFalse(File.Exists(run.Store.PathFor(first.Id)));
        Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.AreEqual(first.Id, run.Store.ReadHeader(run.Store.PathFor(first.Id)).Id);
        Assert.IsFalse(File.Exists(run.Store.PathFor(document.Header.Id)));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));

        run.ConfigureRestore();
        Assert.IsNull(run.Service.StartExample(document));
        run.Drain();
        var second = run.Service.Status!.Identity;
        Assert.AreNotEqual(document.Header.Id, second.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(document.Header.Name, second.Name);
        Assert.IsTrue(run.Service.Status.Unsaved);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
    }

    [TestMethod]
    public void Example_start_waits_for_unsaved_session_leave_choice()
    {
        using var run = new Run();
        var before = run.Service.Status!;
        var document = run.Store.Read(run.Source());
        var (core, _) = run.ConfigureRestore();
        Assert.IsNull(run.Service.StartExample(document));
        Assert.IsNotNull(run.Service.Leaving);
        Assert.AreEqual(before, run.Service.Status);
        Assert.AreEqual(0, core.ActivationCount);
        run.Service.ChooseLeave(LeaveChoice.Discard);
        run.Drain();
        Assert.AreNotEqual(before.Binding, run.Service.Status!.Binding);
        Assert.AreNotEqual(document.Header.Id, run.Service.Status.Identity.Id);
        Assert.IsTrue(run.Service.Status.Unsaved);
    }

    [TestMethod]
    public void Valid_load_activates_new_candidates_and_establishes_clean_manual_save_identity()
    {
        using var run = new Run();
        var source = run.Source();
        var document = run.Store.Read(source);
        var before = run.Service.Status!.Binding;
        var (core, content) = run.ConfigureRestore();
        run.Service.Load(new(source, document.Header, false));
        run.Service.ChooseLeave(LeaveChoice.Discard);
        run.Drain();
        Assert.AreEqual(1, core.ActivationCount);
        Assert.AreEqual(1, content.Activations);
        Assert.AreNotEqual(before, run.Service.Status!.Binding);
        Assert.AreEqual(document.Header.Id, run.Service.Status.Identity.Id);
        Assert.IsFalse(run.Service.Status.Unsaved);
        Assert.IsTrue(run.Host.Host.GetStatus()!.PlayerPaused);
    }

    [TestMethod]
    public void Loading_automatic_entry_preserves_uuid_and_requires_manual_save()
    {
        using var run = new Run();
        var source = run.Source();
        var automatic = run.Store.AutoPathFor(run.Id, 0);
        File.Copy(source, automatic);
        var entry = run.Service.List().Single();
        Assert.IsTrue(entry.Automatic);
        run.ConfigureRestore();
        Assert.IsNull(run.Service.Load(entry));
        run.Service.ChooseLeave(LeaveChoice.Discard);
        run.Drain();
        Assert.AreEqual(run.Id, run.Service.Status!.Identity.Id);
        Assert.IsTrue(run.Service.Status.Unsaved);
        Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.IsFalse(run.Service.Status.Unsaved);
    }

    [TestMethod]
    public void Cancelled_missing_load_releases_service_for_save_load_and_leave()
    {
        using var run = new Run();
        var source = run.Source();
        var entry = new SaveEntry(source, run.Store.ReadHeader(source), false);
        var before = run.Service.Status!;
        run.ConfigureRestore(missing: new(new CoreDefinitionReference.Voxel(new("fixture:missing")), "voxel"));
        Assert.IsNull(run.Service.Load(entry));
        run.Service.ChooseLeave(LeaveChoice.Discard);
        run.PumpUntil(() => run.Service.Missing is not null);
        run.Service.Cancel();
        run.Process();
        Assert.IsFalse(run.Service.IsBusy);
        Assert.IsFalse(run.Service.HasPendingWork);
        Assert.AreEqual(before, run.Service.Status);
        Assert.IsNull(run.Service.Save());
        run.Drain();
        run.ConfigureRestore();
        Assert.IsNull(run.Service.Load(entry));
        run.Drain();
        Assert.IsFalse(run.Service.IsBusy);
        Assert.IsNull(run.Service.ReturnToMenu());
        Assert.IsNull(run.Service.Status);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(5)]
    public void Legacy_list_and_import_reject_without_mutating_session_or_file(int version)
    {
        using var run = new Run();
        var path = run.Store.PathFor(run.Id);
        Directory.CreateDirectory(run.Store.DirectoryFor(run.Id));
        File.WriteAllText(path, SaveContainerTests.Legacy(version));
        var bytes = File.ReadAllBytes(path);
        var before = run.Service.Status;
        Assert.AreEqual(0, run.Service.List().Count);
        StringAssert.Contains(run.Service.ListMessage!, "基于全局体素世界的旧存档已不支持");
        Assert.IsFalse(run.Service.ListMessage.Contains("损坏", StringComparison.Ordinal));
        Assert.ThrowsExactly<SaveFormatException>(() => run.Store.Read(path));
        Assert.IsNull(run.Service.Import(path));
        run.Drain();
        Assert.AreEqual(before, run.Service.Status);
        StringAssert.Contains(run.Service.LastMessage!, "基于全局体素世界的旧存档已不支持");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(5)]
    [DataRow(0)]
    public void Invalid_load_keeps_current_run_and_original_file(int version)
    {
        var text = version == 0 ? "broken JSON" : SaveContainerTests.Legacy(version);
        using var run = new Run();
        var source = run.Source();
        File.WriteAllText(source, text);
        var before = File.ReadAllBytes(source);
        var status = run.Service.Status;
        var entry = new SaveEntry(source, SaveContainerTests.Document().Header, false);
        run.Service.Load(entry);
        run.Service.ChooseLeave(LeaveChoice.Discard);
        run.Drain();
        Assert.AreEqual(status, run.Service.Status);
        Assert.IsFalse(run.Host.Host.GetStatus()!.OperationLocked);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(source));
        StringAssert.Contains(run.Service.LastMessage!, version == 0 ? "损坏" : "基于全局体素世界的旧存档已不支持");
        if (version != 0) Assert.IsFalse(run.Service.LastMessage.Contains("损坏", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Invalid_list_entry_cannot_replace_manual_or_leave_save_failure(bool leaving)
    {
        using var run = new Run(failPublish: true);
        var damaged = run.Store.PathFor(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(damaged)!);
        File.WriteAllText(damaged, SaveContainerTests.Legacy(5));
        var before = File.ReadAllBytes(damaged);
        var status = run.Service.Status;
        run.Service.Changed += () => run.Service.List();
        var continued = false;
        if (leaving)
        {
            Assert.IsNull(run.Service.RequestLeave(() => continued = true));
            run.Service.ChooseLeave(LeaveChoice.Save);
        }
        else Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.AreEqual("deterministic publish failure", run.Service.LastMessage);
        Assert.AreEqual(0, run.Service.List().Count);
        StringAssert.Contains(run.Service.ListMessage!, "基于全局体素世界的旧存档已不支持");
        Assert.AreEqual("deterministic publish failure", run.Service.LastMessage);
        Assert.AreEqual(status, run.Service.Status);
        Assert.IsFalse(continued);
        Assert.IsNull(run.Service.Leaving);
        Assert.IsFalse(run.Host.Host.GetStatus()!.OperationLocked);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(damaged));
        File.Delete(damaged);
        Assert.AreEqual(0, run.Service.List().Count);
        Assert.IsNull(run.Service.ListMessage);
        Assert.AreEqual("deterministic publish failure", run.Service.LastMessage);
    }

    [TestMethod]
    public void Leave_save_waits_for_real_file_publication_then_invokes_continuation()
    {
        using var run = new Run(blockFirstWrite: true);
        var continued = false;
        run.Service.RequestLeave(() => continued = true);
        Assert.IsTrue(run.Host.Host.GetStatus()!.OperationLocked);
        run.Service.ChooseLeave(LeaveChoice.Save);
        run.WaitWriter();
        run.Process();
        Assert.IsFalse(continued);
        Assert.IsFalse(File.Exists(run.Store.PathFor(run.Id)));
        Assert.IsTrue(run.Service.Leaving!.Waiting);
        run.Release();
        run.Drain();
        Assert.IsTrue(continued);
        Assert.IsTrue(File.Exists(run.Store.PathFor(run.Id)));
        Assert.IsFalse(run.Service.Status!.Unsaved);
        Assert.IsFalse(run.Host.Host.GetStatus()!.OperationLocked);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Leave_save_failure_or_cancellation_unlocks_and_restores_settings_without_catchup(bool cancel)
    {
        using var run = new Run(blockFirstWrite: true, failPublish: !cancel);
        run.Host.Host.SetSpeed(SimulationSpeed.Four);
        run.Host.Host.SetPaused(false);
        var continued = false;
        run.Service.RequestLeave(() => continued = true);
        run.Service.ChooseLeave(LeaveChoice.Save);
        run.WaitWriter();
        if (cancel) run.Service.Cancel();
        run.Release();
        run.Drain();
        Assert.IsFalse(continued);
        Assert.IsTrue(run.Service.Status!.Unsaved);
        Assert.IsFalse(File.Exists(run.Store.PathFor(run.Id)));
        var status = run.Host.Host.GetStatus()!;
        Assert.AreEqual(SimulationSpeed.Four, status.Speed);
        Assert.IsFalse(status.PlayerPaused);
        Assert.IsFalse(status.OperationLocked);
        run.Host.Host.ProcessFrame(30);
        Assert.AreEqual(0, run.Host.Session.AdvanceCount);
    }

    [TestMethod]
    public void In_progress_publication_is_awaited_asynchronously_before_discard_leaves()
    {
        using var run = new Run(blockFirstWrite: true);
        run.Service.Save();
        run.WaitWriter();
        Assert.IsNull(run.Service.ReturnToMenu());
        Assert.IsTrue(run.Service.Leaving!.Waiting);
        Assert.IsNotNull(run.Host.Host.GetStatus());
        run.Service.ChooseLeave(LeaveChoice.Discard);
        Assert.IsNotNull(run.Host.Host.GetStatus());
        run.Release();
        run.Drain();
        Assert.IsNull(run.Host.Host.GetStatus());
        Assert.IsTrue(File.Exists(run.Store.PathFor(run.Id)));
    }

    [TestMethod]
    public void Cancelling_leave_while_an_existing_write_runs_keeps_the_session_after_publication()
    {
        using var run = new Run(blockFirstWrite: true);
        var exits = 0;
        run.Service.Save();
        run.WaitWriter();
        Assert.IsNull(run.Service.RequestLeave(() => exits++));
        Assert.IsTrue(run.Service.Leaving!.Waiting);
        run.Service.ChooseLeave(LeaveChoice.Cancel);
        Assert.IsNull(run.Service.Leaving);
        Assert.IsFalse(run.Host.Host.GetStatus()!.OperationLocked);
        run.Release();
        run.Drain();
        Assert.AreEqual(0, exits);
        Assert.IsNotNull(run.Host.Host.GetStatus());
        Assert.IsTrue(File.Exists(run.Store.PathFor(run.Id)));
    }

    [TestMethod]
    public void Cancelling_leave_save_does_not_invoke_a_stale_continuation()
    {
        using var run = new Run(blockFirstWrite: true);
        var exits = 0;
        run.Service.RequestLeave(() => exits++);
        run.Service.ChooseLeave(LeaveChoice.Save);
        run.WaitWriter();
        run.Service.ChooseLeave(LeaveChoice.Cancel);
        Assert.IsFalse(run.Host.Host.GetStatus()!.OperationLocked);
        run.Release();
        run.Drain();
        Assert.AreEqual(0, exits);
        Assert.IsTrue(run.Service.Status!.Unsaved);
        Assert.IsFalse(File.Exists(run.Store.PathFor(run.Id)));
    }

    [TestMethod]
    public void Leaving_waits_for_import_publication_before_offering_the_current_sessions_choices()
    {
        using var run = new Run(blockFirstWrite: true);
        var source = run.Source();
        run.ConfigureRestore();
        run.Service.Import(source);
        run.PumpUntil(() => run.WriterEntered);
        var exits = 0;
        Assert.IsNull(run.Service.RequestLeave(() => exits++));
        Assert.IsTrue(run.Service.Leaving!.Waiting);
        run.Release();
        run.Drain();
        Assert.AreEqual(0, exits);
        Assert.AreEqual(run.Id, run.Service.Status!.Identity.Id);
        Assert.IsFalse(run.Service.Leaving!.Waiting);
        Assert.AreEqual(1, run.Service.List().Count);
        run.Service.ChooseLeave(LeaveChoice.Discard);
        Assert.AreEqual(1, exits);
    }

    [TestMethod]
    public void Frozen_leave_offers_only_discard_or_cancel()
    {
        using var run = new Run();
        var fault = new CoreFault("frozen", new(0, 0), "advance", "state", null);
        run.Host.Session.QueueAdvance(new(0, new(0, 0), [], fault, null), new FixedCoreReadView(new(0, 0), HostCoreScenario.Input, fault));
        run.Host.Host.Step(1);
        run.Service.ReturnToMenu();
        Assert.IsFalse(run.Service.Leaving!.CanSave);
        run.Service.ChooseLeave(LeaveChoice.Save);
        Assert.IsFalse(run.Service.HasPendingWork);
        run.Service.ChooseLeave(LeaveChoice.Cancel);
        Assert.IsNotNull(run.Host.Host.GetStatus());
        Assert.IsFalse(run.Host.Host.GetStatus()!.OperationLocked);
        run.Service.ReturnToMenu();
        run.Service.ChooseLeave(LeaveChoice.Discard);
        Assert.IsNull(run.Host.Host.GetStatus());
    }

    private sealed class Run : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("train-save-service-").FullName;
        internal HostFixture Host { get; } = new();
        internal FileSaveStore Store { get; }
        internal SaveService Service { get; private set; }
        internal Guid Id { get; }
        internal bool WriterEntered => entered.IsSet;
        internal double Now { get; set; }
        private readonly ManualResetEventSlim entered = new();
        private readonly ManualResetEventSlim release = new();
        private int writeCount;

        internal Run(bool blockFirstWrite = false, bool failPublish = false)
        {
            Host.ReadyHost();
            Host.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(CoreCapture("core-state")));
            Id = Host.Host.GetSaveStatus()!.Identity.Id;
            Store = new(Root, (path, json) =>
            {
                FileSaveStore.WriteCandidate(path, json);
                if (Interlocked.Increment(ref writeCount) == 1 && blockFirstWrite)
                {
                    entered.Set();
                    HostFixture.WaitForSignal(release, "release file writer");
                }
            }, (candidate, target, overwrite) =>
            {
                if (failPublish) throw new IOException("deterministic publish failure");
                File.Move(candidate, target, overwrite);
            });
            Service = new(Host.Host, Store, () => Now, () => DateTimeOffset.FromUnixTimeSeconds((long)Now));
            Service.SetAutoSaveInterval(null);
        }

        internal void RecreateService()
        {
            Service.Dispose();
            Service = new(Host.Host, Store, () => Now, () => DateTimeOffset.FromUnixTimeSeconds((long)Now));
        }

        internal void Process() { Host.Host.ProcessFrame(0); Service.Process(); }
        internal void PumpUntil(Func<bool> complete) => Assert.IsTrue(SpinWait.SpinUntil(() =>
            { Process(); return complete(); }, HostFixture.HangTimeout), "save service did not settle");
        internal void Drain() => PumpUntil(() => !Service.HasPendingWork);
        internal void WaitWriter() => HostFixture.WaitForSignal(entered, "writer started");
        internal void Release() => release.Set();
        internal string Source(string core = "core-state", string content = "content-state")
        {
            var path = Path.Combine(Root, "source.json");
            var document = SaveContainerTests.Document(core) with { ContentPayload = content };
            document = document with { Header = document.Header with { Id = Id } };
            Directory.CreateDirectory(Store.DirectoryFor(Id));
            File.WriteAllText(path, SaveContainer.Encode(document));
            return path;
        }
        internal (ScriptedCoreCandidate Core, ScriptedContentCandidate Content) ConfigureRestore(
            string core = "core-state", string content = "content-state", MissingCoreReference? missing = null)
        {
            var session = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
            session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(CoreCapture(core)));
            var coreCandidate = new ScriptedCoreCandidate(session, HostCoreScenario.References, missing is null ? [] : [missing]);
            var contentCandidate = new ScriptedContentCandidate(Host.Content);
            Host.Core.DecodeStateCall = value => value == core ? new CoreResult<ICoreCapture>.Success(CoreCapture(value))
                : throw new AssertFailedException("wrong opaque core payload");
            Host.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(coreCandidate);
            Host.Load = _ => new ContentResult<IContentCandidate>.Success(contentCandidate);
            return (coreCandidate, contentCandidate);
        }
        private static FixedCoreCapture CoreCapture(string payload) => new(new(new(0, 0), "canonical", "hash"),
            HostCoreScenario.References, payload, HostCoreScenario.Input, new(0));
        internal void PublishContent(ulong revision)
        {
            var basis = Host.Host.Content!;
            Host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(basis.Core, new(basis.Generation.Value + 1), new(revision)), basis, ContentChange.Persistent));
            Host.Host.BeginReload(CommandSource.Console);
            Host.PumpUntil(() => !Host.Host.IsBusy);
        }
        public void Dispose()
        {
            release.Set();
            try { Drain(); }
            finally
            {
                Service.Dispose();
                Host.Dispose();
                entered.Dispose();
                release.Dispose();
                Directory.Delete(Root, true);
            }
        }
    }
}

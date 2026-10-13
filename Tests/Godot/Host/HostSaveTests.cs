using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostSaveTests
{
    private HostFixture? fixtureUnderTest;
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void VerifyHostLogs()
    {
        if (TestContext.CurrentTestOutcome == UnitTestOutcome.Passed)
            fixtureUnderTest?.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Default_restoration_is_unsaved_until_first_successful_save()
    {
        using var fixture = new HostFixture();
        fixtureUnderTest = fixture;
        fixture.ReadyHost();
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
    }

    [TestMethod]
    public void Save_captures_core_then_paired_content_synchronously()
    {
        using var fixture = ReadyClean();
        var references = HostCoreScenario.References with { Designs = [new("saved-design")] };
        var core = new FixedCoreCapture(new(new(0, 0), "canonical", "hash"), references, "core", HostCoreScenario.Input, new(0));
        fixture.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(core));
        fixture.CaptureContent = (view, actualReferences) =>
        {
            Assert.AreSame(fixture.Host.Content, view);
            Assert.AreSame(references, actualReferences);
            Assert.AreEqual(1, fixture.Session.CaptureCount);
            Assert.AreEqual("busy", fixture.Host.RenameSave("during capture")?.Code);
            return new ContentResult<IContentCapture>.Success(new FixedContentCapture("content-state", view.PersistentRevision));
        };
        var (capture, failure) = fixture.Host.CaptureSave();
        Assert.IsNull(failure);
        Assert.AreSame(core, capture!.Core);
        Assert.AreEqual(1, fixture.Factory.CaptureCount);
        Assert.AreEqual("content-state", capture.Content.Encode());
    }

    [TestMethod]
    public void Paused_persistent_content_and_save_name_changes_mark_session_unsaved()
    {
        using var fixture = ReadyClean();
        Assert.IsFalse(fixture.Host.GetSaveStatus()!.Unsaved);
        fixture.Host.SetPaused(true);
        PublishPersistentContent(fixture, 1);
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
        var capture = fixture.Host.CaptureSave().Capture!;
        Assert.IsTrue(fixture.Host.CompleteSave(capture, capture.Identity));
        Assert.IsFalse(fixture.Host.GetSaveStatus()!.Unsaved);
        Assert.IsNull(fixture.Host.RenameSave("changed name"));
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
    }

    [TestMethod]
    public void Completion_confirms_only_captured_core_content_and_name_revisions()
    {
        using var fixture = ReadyClean();
        var capture = fixture.Host.CaptureSave().Capture!;
        PublishPersistentContent(fixture, 2);
        fixture.Host.RenameSave("after capture");
        var command = new CoreCommand.StopTrain(new(1));
        fixture.Session.QueueSubmit(command, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Accepted([], [])), [], null),
            new FixedCoreReadView(new(0, 1), HostCoreScenario.Input, revision: new(1)));
        fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.UserInterface);
        Assert.IsTrue(fixture.Host.CompleteSave(capture, capture.Identity));
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
        Assert.AreEqual("after capture", fixture.Host.GetSaveStatus()!.Identity.Name);
    }

    [TestMethod]
    public void Core_revision_alone_marks_session_unsaved_even_after_old_capture_completes()
    {
        using var fixture = ReadyClean();
        var capture = fixture.Host.CaptureSave().Capture!;
        var command = new CoreCommand.StopTrain(new(1));
        fixture.Session.QueueSubmit(command, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Accepted([], [])), [], null),
            new FixedCoreReadView(new(0, 1), HostCoreScenario.Input, revision: new(1)));
        fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.UserInterface);
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
        Assert.IsTrue(fixture.Host.CompleteSave(capture, capture.Identity));
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
        Assert.AreEqual(capture.Identity, fixture.Host.GetSaveStatus()!.Identity);
    }

    [TestMethod]
    public void New_game_is_unsaved_before_its_first_save()
    {
        using var fixture = new HostFixture();
        fixtureUnderTest = fixture;
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(fixture.ContentCandidate);
        fixture.World = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World);
        fixture.Copies = (view, _) => view;
        fixture.Core.PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(fixture.CoreCandidate);
        Assert.IsNull(fixture.Host.BeginNewGame(new("fixture:profile"), 1, "fixed", HostFixture.Options).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
    }

    [TestMethod]
    public void Save_as_requires_matching_binding_and_expected_identity()
    {
        using var fixture = ReadyClean();
        var capture = fixture.Host.CaptureSave().Capture!;
        var target = new SaveIdentity(Guid.NewGuid(), "save as");
        Assert.IsFalse(fixture.Host.CompleteSave(capture with { Binding = new HostBinding(Guid.NewGuid()) }, target));
        Assert.IsFalse(fixture.Host.CompleteSave(capture with { Identity = new(Guid.NewGuid(), capture.Identity.Name) }, target));
        Assert.AreEqual(capture.Identity, fixture.Host.GetSaveStatus()!.Identity);
        Assert.IsFalse(fixture.Host.GetSaveStatus()!.Unsaved);
        Assert.IsTrue(fixture.Host.CompleteSave(capture, target));
        Assert.AreEqual(target, fixture.Host.GetSaveStatus()!.Identity);
        Assert.IsFalse(fixture.Host.GetSaveStatus()!.Unsaved);
    }

    [TestMethod]
    public void Late_completion_from_old_run_cannot_update_new_run_with_same_save_id()
    {
        using var fixture = ReadyClean();
        var capture = fixture.Host.CaptureSave().Capture!;
        var nextSession = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var nextCore = new ScriptedCoreCandidate(nextSession, HostCoreScenario.References);
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(nextCore);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options, capture.Identity);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        var before = fixture.Host.GetSaveStatus()!;
        Assert.AreNotEqual(capture.Binding, before.Binding);
        Assert.IsTrue(before.Unsaved);
        Assert.IsFalse(fixture.Host.CompleteSave(capture, new(Guid.NewGuid(), "late save as")));
        Assert.AreEqual(before, fixture.Host.GetSaveStatus());
    }

    [TestMethod]
    public void Frozen_core_rejects_normal_save_before_capture()
    {
        using var fixture = ReadyClean();
        var fault = new CoreFault("fixed_fault", new(0, 0), "advance", "state", null);
        fixture.Session.QueueAdvance(new(0, new(0, 0), [], fault, null),
            new FixedCoreReadView(new(0, 0), HostCoreScenario.Input, fault));
        fixture.Host.Step(1);
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Frozen);
        var result = fixture.Host.CaptureSave();
        Assert.IsNull(result.Capture);
        Assert.AreEqual("frozen", result.Failure?.Code);
        Assert.AreEqual(0, fixture.Session.CaptureCount);
        Assert.AreEqual(0, fixture.Factory.CaptureCount);
    }

    [TestMethod]
    public void Leave_lock_blocks_mutation_and_unlock_restores_settings_without_catching_up()
    {
        using var fixture = ReadyClean();
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        fixture.Host.SetPaused(false);
        var before = fixture.Host.GetStatus()!;
        Assert.IsNull(fixture.Host.LockLeaving());
        Assert.AreEqual("busy", fixture.Host.RenameSave("blocked")?.Code);
        Assert.AreEqual("busy", fixture.Host.SetPaused(true)?.Code);
        Assert.AreEqual("busy", fixture.Host.SetSpeed(SimulationSpeed.One)?.Code);
        fixture.Host.ProcessFrame(30);
        Assert.AreEqual(0, fixture.Session.AdvanceCount);
        fixture.Host.UnlockLeaving();
        fixture.Host.ProcessFrame(30);
        var after = fixture.Host.GetStatus()!;
        Assert.AreEqual(before.Speed, after.Speed);
        Assert.AreEqual(before.PlayerPaused, after.PlayerPaused);
        Assert.IsFalse(after.OperationLocked);
        Assert.AreEqual(0, fixture.Session.AdvanceCount);
        Assert.AreEqual(0.0, after.Remainder);
    }

    private HostFixture ReadyClean()
    {
        var fixture = new HostFixture();
        fixtureUnderTest = fixture;
        var core = new FixedCoreCapture(new(new(0, 0), "canonical", "hash"), HostCoreScenario.References,
            "core-state", HostCoreScenario.Input, new(0));
        fixture.Core.DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(core);
        fixture.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(core));
        fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options,
            new(Guid.NewGuid(), "original"), unsaved: false);
        fixture.PumpUntil(() => fixture.Host.GetStatus() is not null);
        fixture.Host.ProcessFrame(0);
        return fixture;
    }

    private static void PublishPersistentContent(HostFixture fixture, ulong revision)
    {
        var basis = fixture.Host.Content!;
        var target = new FixedContentView(basis.Core, new(basis.Generation.Value + 1), new(revision));
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(
            new ScriptedContentCandidate(target, basis, ContentChange.Persistent));
        Assert.IsNull(fixture.Host.BeginReload(CommandSource.Console).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreSame(target, fixture.Host.Content);
    }
}

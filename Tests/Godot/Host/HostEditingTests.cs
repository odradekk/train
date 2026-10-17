using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostEditingTests
{
    [TestMethod]
    public void Editing_blocks_world_changes_but_allows_save_capture_and_preserves_pause_sources()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        Assert.IsNull(fixture.Host.SetPaused(false));
        fixture.Host.AutoPauseOnFocusLoss = true;
        fixture.Host.SetFocused(false);
        Assert.IsNull(fixture.Host.BeginEditing());
        var status = fixture.Host.GetStatus()!;
        Assert.IsTrue(status.EditingPaused);
        Assert.IsTrue(status.IsPaused);
        Assert.IsFalse(status.PlayerPaused);
        Assert.AreEqual("editing_active", fixture.Host.SetPaused(true)?.Code);
        Assert.AreEqual("editing_active", fixture.Host.SetSpeed(SimulationSpeed.Two)?.Code);
        Assert.AreEqual("editing_active", fixture.Host.Step(1).Problem?.Code);
        var submitted = fixture.Host.Submit(status.Binding, new CoreCommand.StopTrain(new(1)), CommandSource.Console);
        Assert.AreEqual("editing_active", ((SubmissionResult.NotAccepted)submitted).Reason.Code);
        Assert.AreEqual("editing_active", fixture.Host.BeginReload(CommandSource.Console).Problem?.Code);
        Assert.AreEqual("editing_active", fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options).Problem?.Code);
        Assert.AreEqual("editing_active", fixture.Host.BeginReplay("unused").Problem?.Code);
        Assert.AreEqual("editing_active", fixture.Host.BeginContentEdit(new ContentEdit.DeleteLocalDraft(new(Guid.NewGuid()))).Problem?.Code);
        Assert.IsNotNull(fixture.Host.CaptureSave().Capture);
        fixture.Host.ProcessFrame(10);
        Assert.AreEqual(0, fixture.Session.AdvanceCount);
        fixture.Host.EndEditing();
        status = fixture.Host.GetStatus()!;
        Assert.IsFalse(status.EditingPaused);
        Assert.IsTrue(status.FocusPaused);
        Assert.IsFalse(status.PlayerPaused);
        fixture.Host.SetFocused(true);
        fixture.Host.ProcessFrame(10);
        Assert.AreEqual(0, fixture.Session.AdvanceCount);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Editing_requires_an_active_normal_session_and_repeated_entry_is_rejected()
    {
        using var fixture = new HostFixture();
        Assert.AreEqual("simulation_session_unavailable", fixture.Host.BeginEditing()?.Code);
        fixture.ReadyHost();
        Assert.IsNull(fixture.Host.BeginEditing());
        Assert.AreEqual("editing_active", fixture.Host.BeginEditing()?.Code);
        fixture.Host.EndEditing();
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        fixture.AssertNoUnexpectedFailures();
    }
    [TestMethod]
    public void Saving_a_local_draft_activates_content_without_ending_editing_or_submitting_a_core_command()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var draft = Draft();
        var target = new FixedContentView(fixture.Content.Core, fixture.Content.Generation, new(1));
        var candidate = new ScriptedContentCandidate(target, fixture.Content, ContentChange.Persistent);
        fixture.Factory.PrepareEditCall = (basis, edit) =>
        {
            Assert.AreSame(fixture.Content, basis);
            Assert.AreEqual(new ContentEdit.SaveLocalDraft(draft), edit);
            return new ContentResult<IContentCandidate>.Success(candidate);
        };
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        Assert.IsNull(fixture.Host.BeginEditing());
        Assert.IsNull(fixture.Host.BeginContentEdit(new ContentEdit.SaveLocalDraft(draft)).Problem);
        fixture.PumpUntil(() => ended is not null);
        Assert.IsTrue(ended!.Succeeded);
        Assert.AreEqual(OperationKind.Edit, ended.Kind);
        Assert.AreSame(target, fixture.Host.Content);
        Assert.IsTrue(fixture.Host.IsEditing);
        Assert.IsTrue(fixture.Host.GetSaveStatus()!.Unsaved);
        Assert.AreEqual(1, candidate.Activations);
        Assert.AreEqual(1, candidate.Disposals);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Content_write_exception_discards_candidate_and_exposes_reason_without_changing_content()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        fixture.AllowHostFailure("host_content_edit_failed");
        var candidate = new ScriptedContentCandidate(fixture.Content, fixture.Content, ContentChange.Library);
        fixture.Factory.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        Assert.IsNull(fixture.Host.BeginContentEdit(new ContentEdit.DeleteLocalDraft(new(Guid.NewGuid())),
            _ => throw new IOException("固定写入失败原因")).Problem);
        fixture.PumpUntil(() => ended is not null);
        Assert.AreEqual(HostOperationStatus.Faulted, ended!.Status);
        Assert.AreEqual("固定写入失败原因", ended.Message);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
        Assert.AreEqual(0, candidate.Activations);
        Assert.AreEqual(1, candidate.Disposals);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Editing_allows_recording_maintenance_without_releasing_the_edit_pause()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var binding = fixture.Host.GetStatus()!.Binding;
        var start = new RecordingStart(new(1), fixture.Capture(new(0, 0)));
        fixture.Session.CaptureRecordingStartCall = () => new CoreResult<RecordingStart>.Success(start);
        var confirmed = false;
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        {
            Assert.AreEqual(start.Id, point);
            Assert.AreEqual(RecordingWriteOutcome.PublishedWithDependencies, outcome);
            confirmed = true;
            return new([], []);
        };
        Assert.IsNull(fixture.Host.BeginEditing());
        Assert.AreEqual(start, ((CoreResult<RecordingStart>.Success)fixture.Host.CaptureRecordingStart(binding)).Value);
        fixture.Host.ConfirmRecordingStart(binding, start.Id, RecordingWriteOutcome.PublishedWithDependencies);
        Assert.IsTrue(confirmed);
        Assert.IsTrue(fixture.Host.IsEditing);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Content_edit_cannot_be_cancelled_during_writing_and_activates_once_after_completion()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        using var writing = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var target = new FixedContentView(fixture.Content.Core, fixture.Content.Generation, new(1));
        var candidate = new ScriptedContentCandidate(target, fixture.Content, ContentChange.Library);
        fixture.Factory.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        var begun = fixture.Host.BeginContentEdit(new ContentEdit.DeleteLocalDraft(new(Guid.NewGuid())), _ =>
        {
            writing.Set();
            HostFixture.WaitForSignal(finish, "允许内容写入完成");
            return new Train.App.Saves.FileWriteResult(true);
        });
        Assert.IsNull(begun.Problem);
        try
        {
            HostFixture.WaitForSignal(writing, "内容进入写入阶段");
            Assert.IsFalse(fixture.Host.Cancel(begun.Operation));
            Assert.IsTrue(fixture.Host.IsBusy);
            Assert.IsNull(ended);
            Assert.AreEqual(0, candidate.Activations);
        }
        finally { finish.Set(); }
        fixture.PumpUntil(() => ended is not null);
        fixture.Host.ProcessFrame(0);
        Assert.IsTrue(ended!.Succeeded);
        Assert.AreSame(target, fixture.Host.Content);
        Assert.AreEqual(1, candidate.Activations);
        Assert.AreEqual(1, candidate.Disposals);
        Assert.IsTrue(fixture.Logs.Any(log => log.EventId == "content_edit_summary"));
        Assert.IsFalse(fixture.Logs.Any(log => log.EventId == "content_reload_summary"));
        fixture.AssertNoUnexpectedFailures();
    }

    private static SavedContentDraft Draft() => new(new(Guid.NewGuid()), new(1),
        new DraftTarget.LocalBlueprint(null), new DraftPayload.Design(new(new("测试设计", ""),
            new DesignSpecification.Assembly(null), new([], [], []))));

}

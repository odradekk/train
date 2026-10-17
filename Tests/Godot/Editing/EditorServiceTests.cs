using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Editing;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Godot.Tests.Host;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Editing;

[TestClass]
public sealed class EditorServiceTests
{
    private static DesignDraft Draft(string name) => new(new(name, ""),
        new DesignSpecification.SceneModel(null), new([], [], []));
    private static DesignEvaluation Evaluation(DesignDraft draft) => new DesignEvaluation.Complete(
        new(draft.Display.Name), new("composition"), draft, null,
        new(null, null, null, true, null, null, [], [], [], [], []), true, []);

    [TestMethod]
    public void Cancel_keeps_dirty_session_and_discard_runs_pending_action_once()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var session = fixture.Session;
        session.Change(Draft("edited"));
        fixture.Editor.Process();
        var continued = 0;
        fixture.Editor.RequestClose(() => continued++);
        Assert.IsTrue(fixture.Editor.Leaving);
        fixture.Editor.ResolveClose(LeaveChoice.Cancel);
        Assert.AreSame(session, fixture.Editor.Session);
        Assert.IsFalse(fixture.Editor.Leaving);
        Assert.AreEqual(0, continued);
        fixture.Editor.RequestClose(() => continued++);
        fixture.Editor.ResolveClose(LeaveChoice.Discard);
        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.AreEqual(1, session.Disposals);
        Assert.AreEqual(1, continued);
        Assert.AreEqual(0, session.Saved.Count);
    }

    [TestMethod]
    public void Scene_failure_keeps_same_session_and_recovery_does_not_replace_it()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var session = fixture.Session;
        session.Change(Draft("recoverable"));
        fixture.Editor.SceneFailed(new("scene-unavailable", SceneStage.Present, null, null, null, null, "preview failed"));
        Assert.IsTrue(fixture.Editor.SceneUnavailable);
        Assert.AreSame(session, fixture.Editor.Session);
        Assert.AreEqual("recoverable", session.Capture().Draft.Display.Name);
        fixture.Editor.SceneRecovered();
        Assert.IsFalse(fixture.Editor.SceneUnavailable);
        Assert.AreSame(session, fixture.Editor.Session);
        Assert.AreEqual(0, session.Disposals);
    }

    [TestMethod]
    public void Failed_close_save_keeps_session_dirty_and_does_not_run_pending_action()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var session = fixture.Session;
        session.Change(Draft("unsaved"));
        fixture.Host.Factory.PrepareEditCall = (_, _) =>
            new ContentResult<IContentCandidate>.Faulted("save-rejected", "cannot save", null);
        var continued = false;
        fixture.Editor.RequestClose(() => continued = true);
        fixture.Editor.ResolveClose(LeaveChoice.Save);
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        Assert.IsFalse(continued);
        Assert.AreSame(session, fixture.Editor.Session);
        Assert.IsTrue(session.Read().IsDirty);
        Assert.AreEqual(0, session.Saved.Count);
        Assert.AreEqual(0, session.Disposals);
        Assert.IsNotNull(fixture.Editor.Message);
    }

    [TestMethod]
    public void Failed_blueprint_prepare_reopens_same_snapshot_without_saving_a_draft()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var original = fixture.Session;
        var edited = Draft("blueprint edits");
        original.Change(edited);
        fixture.EvaluateCurrent();
        var target = new BlueprintSaveTarget.New(BlueprintLibrary.Local);
        var calls = new List<ContentEdit>();
        fixture.Host.Factory.PrepareEditCall = (_, edit) =>
        {
            calls.Add(edit);
            Assert.AreEqual(1, original.Disposals, "发布前必须结束原会话。");
            return new ContentResult<IContentCandidate>.Faulted("publish-rejected", "cannot publish", null);
        };
        fixture.Editor.SaveBlueprint(target);
        fixture.PumpUntil(() => fixture.Scenes.Sessions.Count == 2 && !fixture.Editor.IsBusy);
        Assert.AreEqual(1, calls.Count);
        Assert.IsInstanceOfType<ContentEdit.SaveBlueprint>(calls[0]);
        Assert.AreEqual(edited, fixture.Session.Capture().Draft);
        Assert.AreEqual(new DraftTarget.LocalBlueprint(null), fixture.Editor.Target);
        Assert.AreEqual(target, fixture.Editor.SaveTarget);
        Assert.IsTrue(fixture.Editor.IsDirty);
        Assert.AreEqual(0, original.Saved.Count);
    }
    [TestMethod]
    public void Saving_marks_only_captured_revision_and_later_edits_remain_dirty_even_after_scene_failure()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var session = fixture.Session;
        session.Change(Draft("saved snapshot"));
        var saved = session.Capture();
        using var prepared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        SavedContentDraft? requested = null;
        fixture.Host.Factory.PrepareEditCall = (basis, edit) =>
        {
            requested = ((ContentEdit.SaveLocalDraft)edit).Draft;
            prepared.Set();
            Assert.IsTrue(release.Wait(HostFixture.HangTimeout), "保存准备未获释放。");
            var view = new FixedContentView(basis.Core, basis.Generation, new(1));
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view, basis,
                ContentChange.Persistent, edit: new PreparedContentEdit.DraftSaved(requested.Id, requested.Revision)));
        };
        try
        {
            fixture.Editor.SceneFailed(new("scene-down", SceneStage.Present, null, null, null, null, "preview failed"));
            fixture.Editor.SaveDraft();
            HostFixture.WaitForSignal(prepared, "保存捕获已进入内容准备");
            session.Change(Draft("newer unsaved edits"));
        }
        finally { release.Set(); }
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        Assert.AreEqual(saved.Draft, ((DraftPayload.Design)requested!.Payload).Value);
        Assert.AreEqual(saved.Stamp.Revision, requested.Revision);
        Assert.AreEqual(1, session.Saved.Count);
        Assert.AreEqual(saved, session.Saved[0]);
        Assert.IsTrue(fixture.Editor.State!.IsDirty);
        Assert.AreSame(session, fixture.Editor.Session);
        Assert.IsTrue(fixture.Editor.SceneUnavailable);
    }
    [TestMethod]
    public void Late_evaluation_cannot_replace_newer_revision_evaluation()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        using var olderStarted = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        var session = fixture.Session;
        fixture.Host.Factory.EvaluateDesignCall = (_, draft) =>
        {
            if (draft.Display.Name == "older")
            {
                olderStarted.Set();
                Assert.IsTrue(releaseOlder.Wait(HostFixture.HangTimeout), "旧评估未获释放。");
            }
            return new ContentResult<DesignEvaluation>.Success(Evaluation(draft));
        };
        session.Change(Draft("older"));
        var olderStamp = session.Capture().Stamp;
        try
        {
            fixture.Editor.Process();
            HostFixture.WaitForSignal(olderStarted, "旧评估开始");
            session.Change(Draft("newer"));
            fixture.Editor.Process();
            fixture.PumpUntil(() => session.Read().CurrentEvaluation is DesignEvaluation.Complete
                { Definition.Display.Name: "newer" });
        }
        finally { releaseOlder.Set(); }
        fixture.PumpUntil(() => session.Evaluations.Contains(olderStamp));
        Assert.AreEqual("newer", ((DesignEvaluation.Complete)fixture.Editor.State!.CurrentEvaluation!).Definition.Display.Name);
        Assert.IsTrue(fixture.Editor.State.IsDirty);
    }
    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void Locked_user_draft_preserves_valid_saved_file_and_leaves_current_edit_dirty()
    {
        using var fixture = new EditingFixture();
        var encoded = new Dictionary<string, SavedContentDraft>();
        fixture.Host.Factory.EncodeDraftCall = draft =>
        {
            var payload = ((DraftPayload.Design)draft.Payload).Value.Display.Name;
            encoded[payload] = draft;
            return new ContentResult<string>.Success(payload);
        };
        fixture.Host.Factory.DecodeDraftCall = payload => new ContentResult<SavedContentDraft>.Success(encoded[payload]);
        fixture.Editor.OpenDraft(new(new(Guid.NewGuid()), new(0), new DraftTarget.UserBlueprint(null),
            new DraftPayload.Design(Draft("initial user draft"))));
        fixture.PumpUntil(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        fixture.Session.Change(Draft("valid saved draft"));
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        var target = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Single();
        Assert.AreEqual("authoring-drafts", Path.GetFileName(Path.GetDirectoryName(target)));
        var original = File.ReadAllBytes(target);
        Assert.IsFalse(fixture.Editor.State!.IsDirty);
        fixture.Session.Change(Draft("cannot replace locked file"));
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Editor.SaveDraft();
            fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        Assert.AreEqual("valid saved draft", ((DraftPayload.Design)encoded[File.ReadAllText(target)].Payload).Value.Display.Name);
        Assert.IsTrue(fixture.Editor.State!.IsDirty);
        Assert.AreEqual(1, fixture.Session.Saved.Count);
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }
    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void Locked_user_library_entry_preserves_old_file_and_reopens_unsaved_snapshot()
    {
        using var fixture = new EditingFixture(userLibrary: true);
        var address = new BlueprintAddress(BlueprintLibrary.User, new("fixture-blueprint"));
        var saveTarget = new BlueprintSaveTarget.Overwrite(address);
        var writes = 0;
        fixture.Host.Factory.PrepareEditCall = (basis, edit) =>
        {
            var save = (ContentEdit.SaveBlueprint)edit;
            writes++;
            var bytes = System.Text.Encoding.UTF8.GetBytes(save.Draft.Display.Name).ToImmutableArray();
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(basis.Core, basis.Generation, new((ulong)writes)), basis, ContentChange.Persistent,
                edit: new PreparedContentEdit.BlueprintSaved(address, new("fixture-hash")),
                requiredWrites: [new(address.Id, bytes)]));
        };
        fixture.Open();
        fixture.Session.Change(Draft("valid library entry"));
        fixture.EvaluateCurrent();
        fixture.Editor.SaveBlueprint(saveTarget);
        fixture.PumpUntil(() => !fixture.Editor.IsBusy && !fixture.Editor.IsOpen);
        var target = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Single();
        var original = File.ReadAllBytes(target);
        fixture.Open();
        var edited = Draft("unsaved library replacement");
        fixture.Session.Change(edited);
        fixture.EvaluateCurrent();
        var originalSession = fixture.Session;
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Editor.SaveBlueprint(saveTarget);
            fixture.PumpUntil(() => fixture.Scenes.Sessions.Count == 3 && !fixture.Editor.IsBusy);
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        Assert.AreEqual("valid library entry", File.ReadAllText(target));
        Assert.AreEqual(1, originalSession.Disposals);
        Assert.AreEqual(edited, fixture.Session.Capture().Draft);
        Assert.IsTrue(fixture.Editor.IsDirty);
        Assert.AreEqual(saveTarget, fixture.Editor.SaveTarget);
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }
    [TestMethod]
    public void Successful_close_save_marks_snapshot_before_disposing_and_continuing()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var session = fixture.Session;
        session.Change(Draft("saved before leaving"));
        fixture.Host.Factory.PrepareEditCall = (basis, edit) =>
        {
            var draft = ((ContentEdit.SaveLocalDraft)edit).Draft;
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(basis.Core, basis.Generation, new(1)), basis, ContentChange.Persistent,
                edit: new PreparedContentEdit.DraftSaved(draft.Id, draft.Revision)));
        };
        var continued = false;
        fixture.Editor.RequestClose(() =>
        {
            Assert.AreEqual(1, session.Saved.Count);
            Assert.AreEqual(1, session.Disposals);
            continued = true;
        });
        fixture.Editor.ResolveClose(LeaveChoice.Save);
        fixture.PumpUntil(() => continued);
        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.IsFalse(fixture.Coordinator.IsEditing);
    }
    [TestMethod]
    public void Open_editor_exception_releases_editing_pause_and_reports_original_reason()
    {
        using var fixture = new EditingFixture();
        fixture.Scenes.OpenEditorCall = (_, _) => throw new InvalidOperationException("fixture scene opening failed");
        fixture.Editor.Open(Draft("cannot open"), new DraftTarget.LocalBlueprint(null));
        fixture.PumpUntil(() => !fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        Assert.IsFalse(fixture.Coordinator.IsEditing);
        Assert.IsNull(fixture.Editor.Session);
        StringAssert.Contains(fixture.Editor.Message!, "fixture scene opening failed");
        Assert.AreEqual(0, fixture.Scenes.Sessions.Count);
    }

    [TestMethod]
    public void Cancel_editor_close_aborts_save_service_leave_before_game_leave_prompt_or_continuation()
    {
        using var fixture = new EditingFixture();
        using var saves = new SaveService(fixture.Coordinator, new FileSaveStore(fixture.Root));
        saves.CloseEditor = fixture.Editor.RequestClose;
        fixture.Open();
        fixture.Session.Change(Draft("unsaved editor work"));
        var continued = false;
        Assert.IsNull(saves.RequestLeave(() => continued = true));
        Assert.IsTrue(fixture.Editor.Leaving);
        Assert.IsNull(saves.Leaving);
        fixture.Editor.ResolveClose(LeaveChoice.Cancel);
        fixture.Editor.Process();
        saves.Process();
        fixture.Coordinator.ProcessFrame(0);
        Assert.IsFalse(continued);
        Assert.IsFalse(fixture.Editor.Leaving);
        Assert.IsNull(saves.Leaving);
        Assert.IsFalse(saves.HasPendingWork);
        Assert.IsTrue(fixture.Editor.IsOpen);
        Assert.IsTrue(fixture.Coordinator.IsEditing);
    }

    [TestMethod]
    [TestCategory("integration")]
    public void User_draft_saved_after_failed_blueprint_publish_updates_the_original_guid_file()
    {
        using var fixture = new EditingFixture();
        var encoded = new Dictionary<string, SavedContentDraft>();
        fixture.Host.Factory.EncodeDraftCall = draft =>
        {
            var payload = ((DraftPayload.Design)draft.Payload).Value.Display.Name;
            encoded[payload] = draft;
            return new ContentResult<string>.Success(payload);
        };
        fixture.Host.Factory.DecodeDraftCall = payload => new ContentResult<SavedContentDraft>.Success(encoded[payload]);
        var id = new DraftId(Guid.NewGuid());
        fixture.Editor.OpenDraft(new(id, new(0), new DraftTarget.UserBlueprint(null),
            new DraftPayload.Design(Draft("original user draft"))));
        fixture.PumpUntil(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        fixture.Session.Change(Draft("saved user draft"));
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        var path = Path.Combine(fixture.Root, "authoring-drafts", id.Value.ToString("D") + ".json");
        Assert.AreEqual("saved user draft", File.ReadAllText(path));
        fixture.Session.Change(Draft("recovered user draft"));
        fixture.EvaluateCurrent();
        fixture.Host.Factory.PrepareEditCall = (_, _) =>
            new ContentResult<IContentCandidate>.Faulted("publish-failed", "publish failed", null);
        fixture.Editor.SaveBlueprint(new BlueprintSaveTarget.New(BlueprintLibrary.Local));
        fixture.PumpUntil(() => fixture.Scenes.Sessions.Count == 2 && !fixture.Editor.IsBusy);
        Assert.IsTrue(fixture.Editor.IsDirty);
        Assert.AreEqual("saved user draft", File.ReadAllText(path));
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        Assert.AreEqual("recovered user draft", File.ReadAllText(path));
        Assert.AreEqual(id, encoded[File.ReadAllText(path)].Id);
        Assert.IsFalse(fixture.Editor.IsDirty);
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }
    private sealed class EditingFixture : IDisposable
    {
        internal HostFixture Host { get; } = new();
        internal EditorScenes Scenes { get; } = new();
        internal EditorService Editor { get; }
        internal HostCoordinator Coordinator { get; }
        internal EditorSession Session => Scenes.Sessions[^1];
        internal string Root => _root;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "train-editor-tests", Guid.NewGuid().ToString("N"));
        internal EditingFixture(bool userLibrary = false)
        {
            if (userLibrary)
            {
                Coordinator = new(Host.Core, Host.Factory, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                    new(new("builtin"), new("mods"), new("user-library"), "fixture-game", []), Host.Logger,
                    sessionOptions: HostFixture.Options);
                Assert.IsNull(Coordinator.BeginRestore("core-state", "content-state", HostFixture.Options).Problem);
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                {
                    Coordinator.ProcessFrame(0);
                    return Coordinator.GetStatus() is not null;
                }, HostFixture.HangTimeout));
            }
            else { Host.ReadyHost(); Coordinator = Host.Host; }
            Host.Factory.EvaluateDesignCall = (_, draft) => new ContentResult<DesignEvaluation>.Success(Evaluation(draft));
            Editor = new(Coordinator, Scenes, new FileSaveStore(_root), Host.Logger);
        }
        internal void Open()
        {
            Editor.Open(Draft("initial"), new DraftTarget.LocalBlueprint(null));
            PumpUntil(() => Editor.IsOpen && !Editor.IsBusy);
        }
        internal void EvaluateCurrent()
        {
            Editor.Process();
            PumpUntil(() => Session.Read().CurrentEvaluation is not null);
        }
        internal void PumpUntil(Func<bool> completed) => Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            Coordinator.ProcessFrame(0);
            Editor.Process();
            return completed();
        }, HostFixture.HangTimeout), "挂起保护超时：编辑协调未完成。");
        public void Dispose()
        {
            Editor.Dispose();
            if (!ReferenceEquals(Coordinator, Host.Host)) Coordinator.Dispose();
            Host.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}

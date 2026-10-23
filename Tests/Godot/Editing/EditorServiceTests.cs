using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Assets;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.ContractSupport;
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
    [DataRow(false)]
    [DataRow(true)]
    public void Unavailable_instance_design_shows_player_reason_and_keeps_hash_in_logs(bool missing)
    {
        var id = new InstanceId.Vehicle(new(41));
        var hash = new DesignHash("unavailable-instance-design-hash");
        using var fixture = new EditingFixture(configure: host =>
        {
            var content = new FixedContentView(host.Content.Core, host.Content.Generation)
            {
                LocalDrafts = [],
                Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                {
                    [hash] = missing ? new ContentLookup<DesignEvaluation>.Missing([])
                        : new ContentLookup<DesignEvaluation>.NotFound()
                }
            };
            host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(content));
            var view = new FixedCoreReadView(new(0, 0), content.Core.Identity)
            {
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [id] = new Lookup<InstanceView>.Found(new(id, hash, new("composition"),
                        new WorldLocation.Node(new(1)), SettlementRelation.PlayerOwned,
                        new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None, new(1, 1), [], []))
                }
            };
            var restore = host.Core.PrepareRestoreCall!;
            host.Core.PrepareRestoreCall = (saved, projection, options) =>
            {
                restore(saved, projection, options);
                return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(
                    new ScriptedCoreSession(view), HostCoreScenario.References));
            };
        });

        fixture.Editor.OpenInstance(id);

        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.IsTrue(fixture.Editor.HasError);
        StringAssert.Contains(fixture.Editor.Message!, "设计");
        StringAssert.Contains(fixture.Editor.Message!, "不可用");
        Assert.IsFalse(fixture.Editor.Message!.Contains(hash.Value, StringComparison.Ordinal));
        Assert.IsTrue(fixture.Host.Logs.Any(log => log.Message.Contains(hash.Value, StringComparison.Ordinal)
            || log.Attributes.Values.Any(value => value is string text && text.Contains(hash.Value, StringComparison.Ordinal))));
    }

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
    public void Failed_close_save_keeps_session_dirty_and_does_not_run_pending_action()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var session = fixture.Session;
        session.Change(Draft("unsaved"));
        fixture.Editor.Process();
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
        fixture.Editor.Process();
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
            session.ReadCall = () => throw new InvalidOperationException("read while saving failed");
        }
        finally { release.Set(); }
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        session.ReadCall = null;
        Assert.AreEqual(saved.Draft, ((DraftPayload.Design)requested!.Payload).Value);
        Assert.AreEqual(saved.Stamp.Revision, requested.Revision);
        Assert.AreEqual(1, session.Saved.Count);
        Assert.AreEqual(saved, session.Saved[0]);
        Assert.IsTrue(fixture.Editor.IsDirty);
        Assert.IsTrue(session.Read().IsDirty);
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
    [DataRow(false)]
    [DataRow(true)]
    public void Locked_external_draft_preserves_saved_file_and_leaves_current_edit_dirty(bool package)
    {
        using var fixture = new EditingFixture();
        var encoded = new Dictionary<string, SavedContentDraft>();
        fixture.Host.Factory.EncodeDraftCall = draft =>
        {
            var payload = draft.Payload switch
            {
                DraftPayload.Design value => value.Value.Display.Name,
                DraftPayload.Definition { Value: DefinitionDraft.Blueprint value } => value.Value.Display.Name,
                _ => throw new AssertFailedException("unexpected payload")
            };
            encoded[payload] = draft;
            return new ContentResult<string>.Success(payload);
        };
        fixture.Host.Factory.DecodeDraftCall = payload => new ContentResult<SavedContentDraft>.Success(encoded[payload]);
        DraftTarget targetDefinition = package ? new DraftTarget.PackageDefinition(new("core"), DefinitionKind.Blueprint, "test", new("source"))
            : new DraftTarget.UserBlueprint(null);
        DraftPayload initialPayload = package ? new DraftPayload.Definition(new DefinitionDraft.Blueprint("test", Draft("initial user draft")))
            : new DraftPayload.Design(Draft("initial user draft"));
        var savedEvents = 0;
        fixture.Editor.PackageDraftSaved += _ => savedEvents++;
        fixture.Editor.OpenDraft(new(new(Guid.NewGuid()), new(0), targetDefinition, initialPayload));
        fixture.PumpUntil(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        fixture.Session.Change(Draft("valid saved draft"));
        fixture.Editor.Process();
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        var target = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Single();
        Assert.AreEqual(package ? AuthoringPaths.PackageDraftsDirectory(fixture.Root, new("core"))
            : AuthoringPaths.UserDraftsDirectory(fixture.Root), Path.GetDirectoryName(target));
        var original = File.ReadAllBytes(target);
        Assert.IsFalse(fixture.Editor.State!.IsDirty);
        fixture.Session.Change(Draft("cannot replace locked file"));
        fixture.Editor.Process();
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Editor.SaveDraft();
            fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        Assert.AreEqual("valid saved draft", File.ReadAllText(target));
        Assert.AreEqual(package ? 1 : 0, savedEvents);
        Assert.IsTrue(fixture.Editor.State!.IsDirty);
        Assert.AreEqual(1, fixture.Session.Saved.Count);
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
        var savedDraft = package ? fixture.Editor.PackageDrafts(new("core")).Single() : fixture.Editor.UserDrafts().Single();
        fixture.Editor.RequestClose(() => { });
        fixture.Editor.ResolveClose(LeaveChoice.Discard);
        fixture.Editor.OpenDraft(savedDraft);
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        Assert.AreEqual("valid saved draft", fixture.Editor.State!.Snapshot.Draft.Display.Name);
        Assert.AreEqual(targetDefinition, fixture.Editor.Target);
        Assert.IsFalse(fixture.Editor.IsDirty);
    }
    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void Locked_user_library_entry_preserves_old_file_and_reopens_unsaved_snapshot()
    {
        using var fixture = new EditingFixture(userLibrary: true);
        var address = new BlueprintAddress(BlueprintLibrary.User, new(Guid.NewGuid().ToString("D")));
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
        Assert.AreEqual(Path.Combine(AuthoringPaths.UserLibraryDirectory(fixture.Root), address.Id.Value + ".blueprint.json"), target);
        var read = (ContentIoResult<ImmutableArray<byte>>.Success)fixture.Files!.Read(new(new("user-library"), Path.GetFileName(target)), 1024);
        CollectionAssert.AreEqual(File.ReadAllBytes(target), read.Value.ToArray());
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
        fixture.Editor.Process();
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
        using var saves = new SaveService(fixture.Coordinator, new FileSaveStore(fixture.Root), fixture.Editor);
        fixture.Open();
        fixture.Session.Change(Draft("unsaved editor work"));
        fixture.Editor.Process();
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
        fixture.Editor.Process();
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        var path = Path.Combine(AuthoringPaths.UserDraftsDirectory(fixture.Root), id.Value.ToString("D") + ".json");
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
    [TestMethod]
    public void Repeated_close_is_rejected_and_undo_to_clean_does_not_leak_the_old_request()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var old = 0;
        var current = 0;
        fixture.Session.Change(Draft("dirty"));
        fixture.Editor.Process();
        Assert.IsNull(fixture.Editor.RequestClose(() => old++));
        Assert.IsNotNull(fixture.Editor.RequestClose(() => Assert.Fail("不能替换第一个离开请求。")));
        fixture.Session.UndoCall = () =>
        {
            fixture.Session.Change(Draft("initial"), dirty: false);
            return new SceneResult<EditState>.Success(fixture.Session.Read());
        };
        fixture.Editor.Undo();
        fixture.Editor.RequestClose(() => current++);
        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.AreEqual(1, current);
        fixture.Open();
        Assert.IsFalse(fixture.Editor.Leaving);
        fixture.Editor.RequestClose(() => current++);
        Assert.AreEqual(2, current);
        Assert.AreEqual(0, old);
    }

    [TestMethod]
    public void Undo_and_redo_rejection_reports_a_chinese_message_without_scene_details()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var problems = ImmutableArray.Create(new SceneProblem("invalid_operation", SceneStage.Edit, null, null, null, null,
            "There is no edit to restore."));
        fixture.Session.UndoCall = () => new SceneResult<EditState>.Failure(problems);
        fixture.Editor.Undo();
        AssertWithoutSceneDetails(fixture.Editor.Message);
        fixture.Session.RedoCall = () => new SceneResult<EditState>.Failure(problems);
        fixture.Editor.Redo();
        AssertWithoutSceneDetails(fixture.Editor.Message);

        static void AssertWithoutSceneDetails(string? message)
        {
            Assert.IsFalse(string.IsNullOrEmpty(message));
            Assert.IsFalse(message!.Contains("invalid_operation", StringComparison.Ordinal));
            Assert.IsFalse(message.Contains("There is no edit to restore.", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Closing_clears_old_error_and_new_open_failure_keeps_its_reason()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        fixture.Editor.Report("old failure");
        fixture.Editor.RequestClose(() => { });
        Assert.IsFalse(fixture.Editor.HasError);
        Assert.IsNull(fixture.Editor.Message);
        fixture.Scenes.OpenEditorCall = (_, _) => throw new InvalidOperationException("new opening failure");
        fixture.Editor.Open(Draft("failed"), new DraftTarget.LocalBlueprint(null));
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        Assert.IsTrue(fixture.Editor.HasError);
        StringAssert.Contains(fixture.Editor.Message!, "new opening failure");
    }

    [TestMethod]
    public void Clearing_feedback_removes_old_message_and_error_without_closing_the_editor()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        fixture.Editor.Report("old failure");
        fixture.Editor.ClearFeedback();
        Assert.IsNull(fixture.Editor.Message);
        Assert.IsFalse(fixture.Editor.HasError);
        Assert.IsTrue(fixture.Editor.IsOpen);
    }

    [TestMethod]
    public void Object_capture_reports_complete_problem_and_exception_and_respects_scene_guard()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var problem = new SceneProblem("object_missing", SceneStage.Edit, null,
            new ContentReference.Blueprint(new(BlueprintLibrary.Packages, new("missing-design"))), new("missing-package"), null, "受阻操作");
        fixture.Session.CaptureObjectCall = _ => new SceneResult<DesignDraft>.Failure([problem]);
        Assert.IsNull(fixture.Editor.CaptureObject(new(10)));
        StringAssert.Contains(fixture.Editor.Message!, "场景出现问题，详情见日志。");
        StringAssert.Contains(fixture.Editor.Message!, "来源内容包不可用。");
        foreach (var value in new[] { "object_missing", "missing-design", "missing-package", "受阻操作" })
        {
            Assert.IsFalse(fixture.Editor.Message!.Contains(value, StringComparison.Ordinal));
            Assert.IsTrue(fixture.Host.Logs.Any(log => log.Message.Contains(value, StringComparison.Ordinal)));
        }
        fixture.Session.CaptureObjectCall = _ => throw new InvalidOperationException("capture exception");
        Assert.IsNull(fixture.Editor.CaptureObject(new(10)));
        StringAssert.Contains(fixture.Editor.Message!, "capture exception");
        Assert.IsTrue(fixture.Host.Logs.Any(log => log.Message.Contains("capture exception", StringComparison.Ordinal)));
        fixture.Editor.SceneFailed(problem);
        fixture.Session.CaptureObjectCall = _ => { Assert.Fail("场景不可用时不能访问导出入口。"); return null!; };
        Assert.IsNull(fixture.Editor.CaptureObject(new(10)));
    }

    [TestMethod]
    [TestCategory("integration")]
    public void User_draft_listing_skips_other_targets_and_continues_after_individual_failures()
    {
        using var fixture = new EditingFixture();
        var directory = AuthoringPaths.UserDraftsDirectory(fixture.Root);
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "01-throws", "02-bad", "03-other", "04-valid" })
            File.WriteAllText(Path.Combine(directory, name + ".json"), name);
        var valid = new SavedContentDraft(new(Guid.NewGuid()), new(0), new DraftTarget.UserBlueprint(null), new DraftPayload.Design(Draft("valid")));
        fixture.Host.Factory.DecodeDraftCall = payload => payload switch
        {
            "01-throws" => throw new IOException("one file failed"),
            "02-bad" => new ContentResult<SavedContentDraft>.Faulted("bad_draft", "bad draft", null),
            "03-other" => new ContentResult<SavedContentDraft>.Success(valid with { Target = new DraftTarget.LocalBlueprint(null) }),
            "04-valid" => new ContentResult<SavedContentDraft>.Success(valid),
            _ => throw new InvalidOperationException("其他来源不应扫描。")
        };
        File.WriteAllText(Path.Combine(fixture.Root, "authoring-drafts", "other-source.json"), "outside");
        CollectionAssert.AreEqual(new[] { valid }, fixture.Editor.UserDrafts().ToArray());
        Assert.IsTrue(fixture.Host.Logs.Any(log => log.Message.Contains("one file failed", StringComparison.Ordinal)));
        Assert.IsTrue(fixture.Host.Logs.Any(log => log.Message.Contains("bad_draft", StringComparison.Ordinal)));
        Assert.IsFalse(fixture.Host.Logs.Any(log => log.Message.Contains("03-other", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Session_read_or_capture_exception_stops_scene_but_does_not_escape_processing(bool read)
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        fixture.Session.Change(Draft("dirty"));
        var last = fixture.Session.Read();
        if (read) fixture.Editor.Process();
        if (read) fixture.Session.ReadCall = () => throw new InvalidOperationException("read failed");
        else fixture.Session.CaptureCall = () => throw new InvalidOperationException("capture failed");
        if (read)
        {
            Assert.AreSame(last, fixture.Editor.State);
            Assert.IsTrue(fixture.Editor.IsDirty);
            Assert.IsFalse(fixture.Editor.SceneUnavailable);
        }
        fixture.Editor.Process();
        Assert.AreSame(last, fixture.Editor.State);
        Assert.IsTrue(fixture.Editor.IsDirty);
        Assert.IsTrue(fixture.Editor.SceneUnavailable);
        Assert.AreSame(fixture.Session, fixture.Editor.Session);
        var detail = read ? "read failed" : "capture failed";
        var sentence = read ? "无法读取当前设计，详情见日志。" : "无法捕获当前设计，详情见日志。";
        StringAssert.Contains(fixture.Editor.Message!, sentence);
        Assert.IsFalse(fixture.Editor.Message!.Contains(detail, StringComparison.Ordinal));
        Assert.IsTrue(fixture.Host.Logs.Any(log => log.Message.Contains(detail, StringComparison.Ordinal)
            && log.Message.Contains(read ? "editor_read_failed" : "editor_capture_failed", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Clean_editor_closes_and_returns_busy_while_save_import_is_pending()
    {
        using var fixture = new EditingFixture();
        using var saves = new SaveService(fixture.Coordinator, new FileSaveStore(fixture.Root), fixture.Editor);
        fixture.Open();
        Assert.IsFalse(fixture.Editor.IsDirty);
        Assert.IsNull(saves.Import(Path.Combine(fixture.Root, "missing-import.json")));
        Assert.IsTrue(saves.HasPendingWork);
        Assert.AreEqual("busy", saves.RequestLeave(() => Assert.Fail("导入未完成时不能继续离开。"))?.Code);
        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.AreEqual(1, fixture.Session.Disposals);
        Assert.IsTrue(saves.HasPendingWork);
        Assert.IsNull(saves.Leaving);
    }

    [TestMethod]
    public void Frozen_core_allows_reopening_and_draft_save_but_rejects_blueprint_publish()
    {
        using var fixture = new EditingFixture();
        var fault = new CoreFault("frozen_fixture", new(0, 0), "advance", "state", null);
        fixture.Host.Session.QueueAdvance(new(0, new(0, 0), [], fault, null),
            new FixedCoreReadView(new(0, 0), HostCoreScenario.Input, fault));
        fixture.Coordinator.Step(1);
        fixture.Open();
        fixture.Session.Change(Draft("frozen draft"));
        fixture.EvaluateCurrent();
        fixture.Host.Factory.PrepareEditCall = (basis, edit) =>
        {
            var draft = ((ContentEdit.SaveLocalDraft)edit).Draft;
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(basis.Core, basis.Generation, new(1)), basis, ContentChange.Persistent,
                edit: new PreparedContentEdit.DraftSaved(draft.Id, draft.Revision)));
        };
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        Assert.IsFalse(fixture.Editor.IsDirty);
        StringAssert.Contains(fixture.Editor.Message!, "无法写入存档");
        fixture.Session.Change(Draft("publish rejected"));
        fixture.EvaluateCurrent();
        var session = fixture.Session;
        session.SetState(state => state with { CanUndo = true, EnteredObject = new(10), Selection = [new(11)] });
        fixture.Editor.Process();
        var state = fixture.Editor.State;
        session.CaptureCall = () => throw new InvalidOperationException("冻结拒绝不应捕获或关闭会话。");
        Assert.IsFalse(fixture.Editor.SaveBlueprint(new BlueprintSaveTarget.New(BlueprintLibrary.Local)));
        Assert.AreSame(session, fixture.Editor.Session);
        Assert.AreEqual(0, session.Disposals);
        Assert.AreSame(state, fixture.Editor.State);
        Assert.IsTrue(fixture.Editor.State!.CanUndo);
        Assert.AreEqual(new EditElementId(10), fixture.Editor.State.EnteredObject);
        CollectionAssert.AreEqual(new[] { new EditElementId(11) }, fixture.Editor.State.Selection.ToArray());
        Assert.IsTrue(fixture.Editor.IsDirty);
        Assert.IsTrue(fixture.Coordinator.GetSaveStatus()!.Frozen);
        StringAssert.Contains(fixture.Editor.Message!, "frozen");
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Frozen_core_still_saves_user_draft_to_disk()
    {
        using var fixture = new EditingFixture();
        var fault = new CoreFault("frozen_fixture", new(0, 0), "advance", "state", null);
        fixture.Host.Session.QueueAdvance(new(0, new(0, 0), [], fault, null),
            new FixedCoreReadView(new(0, 0), HostCoreScenario.Input, fault));
        fixture.Coordinator.Step(1);
        var id = new DraftId(Guid.NewGuid());
        var draft = new SavedContentDraft(id, new(0), new DraftTarget.UserBlueprint(null),
            new DraftPayload.Design(Draft("user draft")));
        fixture.Host.Factory.EncodeDraftCall = _ => new ContentResult<string>.Success("user draft payload");
        fixture.Host.Factory.DecodeDraftCall = _ => new ContentResult<SavedContentDraft>.Success(draft);
        fixture.Editor.OpenDraft(draft);
        fixture.PumpUntil(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        fixture.Session.Change(Draft("saved while frozen"));
        fixture.Editor.Process();
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        var path = Path.Combine(AuthoringPaths.UserDraftsDirectory(fixture.Root), id.Value.ToString("D") + ".json");
        Assert.AreEqual("user draft payload", File.ReadAllText(path));
        Assert.IsFalse(fixture.Editor.IsDirty);
        Assert.IsTrue(fixture.Coordinator.GetSaveStatus()!.Frozen);
    }

    [TestMethod]
    public void Reading_cached_state_does_not_poll_session_or_publish_changes()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var cached = fixture.Editor.State;
        var reads = 0;
        var changes = 0;
        fixture.Editor.Changed += () => changes++;
        fixture.Session.Change(Draft("external change"));
        var updated = fixture.Session.Read();
        fixture.Session.ReadCall = () => { reads++; return updated; };
        for (var index = 0; index < 3; index++)
        {
            Assert.AreSame(cached, fixture.Editor.State);
            Assert.IsFalse(fixture.Editor.IsDirty);
        }
        Assert.AreEqual(0, reads);
        Assert.AreEqual(0, changes);
        fixture.Editor.Process();
        Assert.AreEqual(1, reads);
        Assert.AreSame(updated, fixture.Editor.State);
        Assert.IsTrue(fixture.Editor.IsDirty);
    }

    [TestMethod]
    public void Form_close_runs_after_grid_close_and_can_cancel_fail_or_continue()
    {
        using var fixture = new EditingFixture();
        var continued = 0;
        Action? pending = null;
        fixture.Editor.CloseForm = after => { pending = after; return null; };
        Assert.IsNull(fixture.Editor.RequestClose(() => continued++));
        Assert.AreEqual(0, continued);
        pending = null; // Form cancellation drops its continuation.
        fixture.Editor.CloseForm = _ => new("form_failed", "form save failed");
        Assert.AreEqual("form_failed", fixture.Editor.RequestClose(() => continued++)!.Code);
        fixture.Open();
        fixture.Session.Change(Draft("dirty"));
        fixture.Editor.Process();
        fixture.Editor.RequestClose(() => continued++);
        fixture.Editor.ResolveClose(LeaveChoice.Discard);
        Assert.IsFalse(fixture.Editor.IsOpen);
        StringAssert.Contains(fixture.Editor.Message!, "form save failed");
        Assert.AreEqual(0, continued);
        fixture.Editor.CloseForm = after => { after(); return null; };
        Assert.IsNull(fixture.Editor.RequestClose(() => continued++));
        Assert.AreEqual(1, continued);
    }

    [TestMethod]
    [TestCategory("integration")]
    [DataRow(false)]
    [DataRow(true)]
    public void Passive_draft_refresh_preserves_external_save_failure(bool package)
    {
        using var fixture = new EditingFixture();
        var packageId = new PackageId("core");
        DraftTarget target = package ? new DraftTarget.PackageDefinition(packageId, DefinitionKind.Blueprint, "test", new("source"))
            : new DraftTarget.UserBlueprint(null);
        fixture.Editor.Open(Draft("unsaved"), target, unsaved: true);
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        fixture.Host.Factory.EncodeDraftCall = _ => new ContentResult<string>.Faulted("save_failed", "草稿保存失败", null);
        var wasBusy = false;
        fixture.Editor.Changed += () =>
        {
            if (wasBusy && !fixture.Editor.IsBusy) fixture.Editor.PackageDrafts(packageId);
            wasBusy = fixture.Editor.IsBusy;
        };
        fixture.Editor.SaveDraft();
        fixture.PumpUntil(() => !fixture.Editor.IsBusy);
        StringAssert.Contains(fixture.Editor.Message!, "草稿保存失败");
        Assert.IsTrue(fixture.Editor.HasError);
        Assert.IsTrue(fixture.Editor.IsDirty);
        fixture.Editor.UserDrafts();
        StringAssert.Contains(fixture.Editor.Message!, "草稿保存失败");
    }

    private sealed class EditingFixture : IDisposable
    {
        internal HostFixture Host { get; } = new();
        internal EditorScenes Scenes { get; } = new();
        internal EditorService Editor { get; }
        internal HostCoordinator Coordinator { get; }
        internal EditorSession Session => Scenes.Sessions[^1];
        internal string Root => _root;
        internal ContentFiles? Files { get; }
        private readonly GodotMainThreadDispatcher _dispatcher = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "train-editor-tests", Guid.NewGuid().ToString("N"));
        internal EditingFixture(bool userLibrary = false, Action<HostFixture>? configure = null)
        {
            configure?.Invoke(Host);
            if (userLibrary)
            {
                Files = new(_dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
                {
                    [new("user-library")] = new ContentRootLocation.Disk(AuthoringPaths.UserLibraryDirectory(_root))
                });
                Coordinator = new(Host.Core, Host.Factory, Files, new UnconfiguredAssetInspector(),
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
            PumpUntil(() => Editor.State?.CurrentEvaluation is not null);
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
            _dispatcher.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}

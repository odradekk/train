using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Content.ContractSupport;

namespace Train.Godot.Tests.Editing;

public sealed partial class EditorServiceTests
{
    [TestMethod]
    [TestCategory("integration")]
    [DataRow(false)]
    [DataRow(true)]
    public void Delete_user_draft_uses_its_actual_file_and_keeps_it_on_failure(bool fail)
    {
        using var fixture = new EditingFixture();
        var draft = new SavedContentDraft(new(Guid.NewGuid()), new(1), new DraftTarget.UserBlueprint(null),
            new DraftPayload.Design(Draft("discard")));
        var directory = AuthoringPaths.UserDraftsDirectory(fixture.Root);
        Directory.CreateDirectory(directory);
        var actual = Path.Combine(directory, "imported-name.json");
        var unrelated = Path.Combine(directory, draft.Id.Value.ToString("D") + ".json");
        File.WriteAllText(actual, "selected");
        File.WriteAllText(unrelated, "other");
        var other = draft with { Id = new(Guid.NewGuid()) };
        fixture.Host.Factory.DecodeDraftCall = text => new ContentResult<SavedContentDraft>.Success(text == "selected" ? draft : other);
        var selected = fixture.Editor.UserDrafts().Single(item => item.Id == draft.Id);
        var changes = 0;
        fixture.Editor.Changed += () => changes++;
        if (fail) fixture.Editor.DeleteFile = path => throw new IOException(path);
        fixture.Editor.DeleteDraft(selected);
        Assert.AreEqual(fail, File.Exists(actual));
        Assert.AreEqual("other", File.ReadAllText(unrelated));
        Assert.IsTrue(changes > 0);
        Assert.AreEqual(fail, fixture.Editor.HasError);
        Assert.AreEqual(fail, fixture.Editor.UserDrafts().Any(item => item.Id == draft.Id));
        Assert.IsFalse(fixture.Editor.Message?.Contains(fixture.Root, StringComparison.Ordinal) == true);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Delete_local_draft_publishes_only_after_success_and_reports_failure(bool fail)
    {
        var draft = new SavedContentDraft(new(Guid.NewGuid()), new(1), new DraftTarget.LocalBlueprint(null),
            new DraftPayload.Design(Draft("local")));
        using var fixture = new EditingFixture(configure: host => host.Load = _ =>
            new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(host.Content.Core, host.Content.Generation) { LocalDrafts = [draft] })));
        fixture.Host.Factory.PrepareEditCall = (basis, edit) =>
        {
            Assert.AreEqual(draft.Id, ((ContentEdit.DeleteLocalDraft)edit).Draft);
            if (fail) return new ContentResult<IContentCandidate>.Faulted("delete_failed", "private failure", null);
            var view = new FixedContentView(basis.Core, basis.Generation, new(1)) { LocalDrafts = [] };
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view, basis,
                ContentChange.Persistent, edit: new PreparedContentEdit.DraftDeleted(draft.Id)));
        };
        var original = fixture.Coordinator.Content;
        var changes = 0;
        fixture.Editor.Changed += () => changes++;
        fixture.Editor.DeleteDraft(draft);
        fixture.PumpUntil(() => changes > 0);
        Assert.AreEqual(fail, ReferenceEquals(original, fixture.Coordinator.Content));
        Assert.AreEqual(fail, fixture.Coordinator.Content!.LocalDrafts.Contains(draft));
        Assert.AreEqual(fail, fixture.Editor.HasError);
        if (fail) Assert.IsFalse(fixture.Editor.Message!.Contains("private failure", StringComparison.Ordinal));
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
        Assert.IsFalse(fixture.Editor.Message!.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsFalse(fixture.Host.Logs.Any(log => log.Message.Contains("03-other", StringComparison.Ordinal)));
    }

}

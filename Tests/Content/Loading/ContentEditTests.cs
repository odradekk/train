using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentEditTests
{
    [TestMethod]
    public void Local_drafts_prepare_without_mutation_overwrite_noop_and_delete()
    {
        using var loaded = DesignFixtures.Content().Load();
        var basis = (ContentView)loaded.View;
        var draft = new C.SavedContentDraft(new(Guid.NewGuid()), new(72),
            new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(8))),
            new C.DraftPayload.Design(DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Vehicle(null) }));
        using var prepared = Success(basis, new C.ContentEdit.SaveLocalDraft(draft));
        Assert.AreEqual(0, basis.LocalDrafts.Length);
        Assert.AreEqual(C.ContentChange.Persistent, prepared.Changes);
        Assert.AreEqual(basis.Generation, prepared.View.Generation);
        Assert.AreEqual(basis.PersistentRevision.Value + 1, prepared.View.PersistentRevision.Value);
        Assert.AreEqual(basis.Core.Identity, prepared.View.Core.Identity);
        var saved = (ContentView)prepared.Activate();
        Assert.AreEqual(draft, saved.LocalDrafts.Single());
        using var noop = Success(saved, new C.ContentEdit.SaveLocalDraft(draft));
        Assert.AreEqual(C.ContentChange.None, noop.Changes);
        Assert.AreSame(saved, noop.View);
        Failure(saved, new C.ContentEdit.SaveLocalDraft(draft with { Id = new(Guid.NewGuid()) }), "duplicate_identity");
        var revised = draft with { Revision = new(9) };
        using var overwrite = Success(saved, new C.ContentEdit.SaveLocalDraft(revised));
        Assert.AreEqual(new C.EditRevision(9), overwrite.View.LocalDrafts.Single().Revision);
        using var deleted = Success((ContentView)overwrite.View, new C.ContentEdit.DeleteLocalDraft(draft.Id));
        Assert.AreEqual(0, deleted.View.LocalDrafts.Length);
        Failure(basis, new C.ContentEdit.DeleteLocalDraft(draft.Id), "reference_missing");
        Failure(basis, new C.ContentEdit.SaveLocalDraft(draft with { Target = new C.DraftTarget.UserBlueprint(null) }), "invalid_field");
    }

    [TestMethod]
    [DataRow(C.BlueprintLibrary.Local)]
    [DataRow(C.BlueprintLibrary.User)]
    public void Blueprint_save_overwrite_and_delete_follow_library_persistence(C.BlueprintLibrary library)
    {
        using var loaded = DesignFixtures.Content().Load();
        var basis = (ContentView)loaded.View;
        var draft = DesignFixtures.Vehicle();
        using var save = Success(basis, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(library), draft));
        var address = ((C.PreparedContentEdit.BlueprintSaved)save.Edit!).Blueprint;
        Assert.AreEqual(0, basis.Blueprints.Length);
        Assert.AreEqual(C.ContentChange.Library | (library == C.BlueprintLibrary.Local ? C.ContentChange.Persistent : C.ContentChange.None), save.Changes);
        Assert.AreEqual(basis.Generation.Value + 1, save.View.Generation.Value);
        Assert.AreEqual(basis.Core.Identity, save.View.Core.Identity);
        Assert.AreEqual(basis.Core.Designs.Length, save.View.Core.Designs.Length);
        Assert.AreEqual(library == C.BlueprintLibrary.User ? 1 : 0, save.RequiredWrites.Length);
        if (library == C.BlueprintLibrary.User) Assert.IsNotNull(save.RequiredWrites.Single().Bytes);
        var saved = (ContentView)save.Activate();
        using var noop = Success(saved, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.Overwrite(address), draft));
        Assert.AreEqual(C.ContentChange.None, noop.Changes);
        Assert.AreEqual(0, noop.RequiredWrites.Length);
        var renamed = draft with { Display = new("改名", "描述") };
        using var overwrite = Success(saved, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.Overwrite(address), renamed));
        Assert.AreEqual(address, ((C.PreparedContentEdit.BlueprintSaved)overwrite.Edit!).Blueprint);
        using var delete = Success((ContentView)overwrite.View, new C.ContentEdit.DeleteBlueprint(address));
        Assert.AreEqual(0, delete.View.Blueprints.Length);
        if (library == C.BlueprintLibrary.User) Assert.IsNull(delete.RequiredWrites.Single().Bytes);
        Failure(basis, new C.ContentEdit.DeleteBlueprint(address), "reference_missing");
    }

    [TestMethod]
    public void Targets_are_prepared_without_changing_basis_and_repeat_as_noop()
    {
        using var loaded = DesignFixtures.Content().Load();
        var basis = (ContentView)loaded.View;
        var draft = DesignFixtures.Vehicle();
        using var target = Success(basis, new C.ContentEdit.ConstructionTarget(draft));
        Assert.IsInstanceOfType<C.PreparedContentEdit.ConstructionTarget>(target.Edit);
        Assert.AreEqual(C.ContentChange.Presentation | C.ContentChange.Persistent, target.Changes);
        Assert.AreEqual(basis.Core.Identity, target.View.Core.Identity);
        var saved = (ContentView)target.Activate();
        var hash = ((C.PreparedContentEdit.ConstructionTarget)target.Edit!).Design.Design.Design;
        using var same = Success(saved, new C.ContentEdit.ConstructionTarget(draft));
        Assert.AreEqual(C.ContentChange.None, same.Changes);
        using var instance = Success(saved, new C.ContentEdit.InstanceTarget(hash, draft with { Display = new("新名", "说明") }));
        Assert.IsTrue(((C.PreparedContentEdit.InstanceTarget)instance.Edit!).Difference.CanApplyImmediately);
        Failure(basis, new C.ContentEdit.InstanceTarget(hash, draft), "reference_missing");
    }

    [TestMethod]
    public void Invalid_player_designs_and_package_writes_are_rejected()
    {
        using var loaded = DesignFixtures.Content().Load();
        var basis = (ContentView)loaded.View;
        var draft = DesignFixtures.Vehicle();
        Failure(basis, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(C.BlueprintLibrary.Packages), draft), "read_only_target");
        Failure(basis, new C.ContentEdit.DeleteBlueprint(new(C.BlueprintLibrary.Packages, new("core:test"))), "read_only_target");
        foreach (var invalid in new[]
        {
            draft with { Specification = new C.DesignSpecification.Vehicle(null) },
            draft with { Specification = new C.DesignSpecification.FunctionalComposite([new K.PartCapability.Control()], null) }
        })
        {
            Failure(basis, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(C.BlueprintLibrary.Local), invalid), "design_invalid");
            Failure(basis, new C.ContentEdit.ConstructionTarget(invalid), "design_invalid");
        }
    }

    private static C.IContentCandidate Success(ContentView basis, C.ContentEdit edit)
    {
        var result = ContentEditor.Prepare(basis, edit);
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        return ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
    }

    private static void Failure(ContentView basis, C.ContentEdit edit, string code)
    {
        var result = ContentEditor.Prepare(basis, edit);
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == code), result.ToString());
    }
}

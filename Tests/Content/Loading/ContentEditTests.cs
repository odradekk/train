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
        var fixture = DesignFixtures.Content();
        fixture.Installation = fixture.Installation with { UserLibrary = new("library") };
        fixture.AddDirectory(fixture.Installation.UserLibrary!.Value, "");
        using var loaded = fixture.Load();
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
    public void Local_draft_view_keeps_identity_order_after_save_and_overwrite()
    {
        using var initial = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var first = new C.SavedContentDraft(new(Guid.Parse("11111111-1111-4111-8111-111111111111")), new(1),
            new C.DraftTarget.LocalBlueprint(null), new C.DraftPayload.Design(DesignFixtures.Vehicle()));
        var second = first with { Id = new(Guid.Parse("22222222-2222-4222-8222-222222222222")) };
        using var later = ((C.ContentResult<C.IContentCandidate>.Success)factory.PrepareEdit(initial.View, new C.ContentEdit.SaveLocalDraft(second))).Value;
        using var earlier = ((C.ContentResult<C.IContentCandidate>.Success)factory.PrepareEdit(later.Activate(), new C.ContentEdit.SaveLocalDraft(first))).Value;
        var basis = earlier.Activate();
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, basis.LocalDrafts.Select(d => d.Id).ToArray());
        using var overwrite = ((C.ContentResult<C.IContentCandidate>.Success)factory.PrepareEdit(basis,
            new C.ContentEdit.SaveLocalDraft(first with { Revision = new(2) }))).Value;
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, overwrite.View.LocalDrafts.Select(d => d.Id).ToArray());
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
        var incomplete = draft with { Specification = new C.DesignSpecification.Vehicle(null) };
        Failure(basis, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(C.BlueprintLibrary.Local), incomplete), "design_invalid");
        Failure(basis, new C.ContentEdit.ConstructionTarget(incomplete), "design_invalid");
    }

    [TestMethod]
    public void Unconfigured_user_library_rejects_save_overwrite_and_delete_without_writes()
    {
        using var loaded = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var address = new C.BlueprintAddress(C.BlueprintLibrary.User, new("11111111-1111-4111-8111-111111111111"));
        foreach (var edit in new C.ContentEdit[]
        {
            new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(C.BlueprintLibrary.User), DesignFixtures.Vehicle()),
            new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.Overwrite(address), DesignFixtures.Vehicle()),
            new C.ContentEdit.DeleteBlueprint(address)
        })
        {
            var result = factory.PrepareEdit(loaded.View, edit);
            Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
            var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single();
            Assert.AreEqual("invalid_field", diagnostic.Code);
            Assert.AreEqual("/target", diagnostic.Field);
        }
        Assert.AreEqual(0, loaded.View.Blueprints.Length);
        Assert.AreEqual(new C.ContentRevision(0), loaded.View.PersistentRevision);
    }

    [TestMethod]
    [DataRow("default", "/id")]
    [DataRow("package", "/target")]
    [DataRow("definition", "/payload")]
    public void Public_local_draft_save_rejects_invalid_identity_target_and_payload(string mutation, string field)
    {
        using var loaded = DesignFixtures.Content().Load();
        var draft = new C.SavedContentDraft(new(Guid.Parse("11111111-1111-4111-8111-111111111111")), new(1),
            new C.DraftTarget.LocalBlueprint(null), new C.DraftPayload.Design(DesignFixtures.Vehicle()));
        draft = mutation switch
        {
            "default" => draft with { Id = default },
            "package" => draft with { Target = new C.DraftTarget.PackageDefinition(new("core"), C.DefinitionKind.Blueprint, "test", new("version")) },
            _ => draft with { Payload = new C.DraftPayload.Definition(new C.DefinitionDraft.Blueprint("test", DesignFixtures.Vehicle())) }
        };
        var result = ContentFactory.Create().PrepareEdit(loaded.View, new C.ContentEdit.SaveLocalDraft(draft));
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single();
        Assert.AreEqual("invalid_field", diagnostic.Code);
        Assert.AreEqual(field, diagnostic.Field);
        Assert.AreEqual(0, loaded.View.LocalDrafts.Length);
    }

    private static C.IContentCandidate Success(ContentView basis, C.ContentEdit edit)
    {
        var result = ContentFactory.Create().PrepareEdit(basis, edit);
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        return ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
    }

    private static void Failure(ContentView basis, C.ContentEdit edit, string code)
    {
        var result = ContentFactory.Create().PrepareEdit(basis, edit);
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == code), result.ToString());
    }
}

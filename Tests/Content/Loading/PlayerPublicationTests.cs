using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class PlayerPublicationTests
{
    [TestMethod]
    public void Current_user_library_is_read_by_all_load_purposes_and_only_library_changes_are_published()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        using var saved = Success(factory.PrepareEdit(initial.View, new C.ContentEdit.SaveBlueprint(
            new C.BlueprintSaveTarget.New(C.BlueprintLibrary.User), DesignFixtures.Vehicle())));
        var write = saved.RequiredWrites.Single();
        var root = new C.ContentRoot("library");
        fixture.Installation = fixture.Installation with { UserLibrary = root };
        fixture.AddFile(root, write.Blueprint.Value + ".blueprint.json", write.Bytes!.Value.ToArray());
        fixture.AddFile(root, "22222222-2222-4222-8222-222222222222.blueprint.json", "{");
        fixture.AddFile(root, "wrong.blueprint.json", "{}");
        using var loaded = fixture.Load();
        Assert.AreEqual(2, loaded.View.Blueprints.Length);
        Assert.AreEqual(1, loaded.View.Blueprints.Count(b => b.Usable));
        Assert.IsTrue(loaded.Report.Diagnostics.Any(d => d.Code == "invalid_json"));
        var basis = loaded.Activate();
        var capture = Success(factory.Capture(basis, References(basis)));
        var json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(write.Bytes.Value.AsSpan()))!;
        json["display"]!["name"] = "磁盘新版本";
        fixture.AddFile(root, write.Blueprint.Value + ".blueprint.json", json.ToJsonString());
        using var reload = Success(factory.PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(basis, References(basis))), fixture, fixture));
        Assert.AreEqual(C.ContentChange.Library, reload.Changes);
        Assert.AreEqual(basis.Generation.Value + 1, reload.View.Generation.Value);
        Assert.AreEqual(basis.PersistentRevision, reload.View.PersistentRevision);
        Assert.AreEqual(basis.Core.Identity, reload.View.Core.Identity);
        using var restore = Success(factory.PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Restore(capture, References(basis))), fixture, fixture));
        var address = new C.BlueprintAddress(C.BlueprintLibrary.User, write.Blueprint);
        Assert.AreEqual("磁盘新版本", ((C.ContentLookup<C.BlueprintRecord>.Found)restore.View.FindBlueprint(address)).Value.Definition.Display.Name);
        Assert.AreEqual(0, JsonNode.Parse(capture.Encode())!["local_blueprints"]!.AsArray().Count);
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("local-draft")]
    [DataRow("local-blueprint")]
    [DataRow("user-blueprint")]
    public void Only_instance_drafts_block_new_missing_references_on_reload_and_restore_reports_all(string kind)
    {
        var fixture = DesignFixtures.Content();
        DesignFixtures.AddVoxel(fixture, "only");
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var design = Only();
        C.ContentEdit edit = kind.EndsWith("blueprint", StringComparison.Ordinal)
            ? new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(kind == "local-blueprint" ? C.BlueprintLibrary.Local : C.BlueprintLibrary.User), design)
            : new C.ContentEdit.SaveLocalDraft(new(new(Guid.NewGuid()), new(1), kind == "instance"
                ? new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(7))) : new C.DraftTarget.LocalBlueprint(null), new C.DraftPayload.Design(design)));
        using var saved = Success(factory.PrepareEdit(initial.View, edit));
        var basis = saved.Activate();
        var references = References(basis);
        var capture = Success(factory.Capture(basis, references));
        if (kind == "user-blueprint")
        {
            var write = saved.RequiredWrites.Single();
            fixture.Installation = fixture.Installation with { UserLibrary = new("library") };
            fixture.AddFile(new("library"), write.Blueprint.Value + ".blueprint.json", write.Bytes!.Value.ToArray());
        }
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/voxels/only.voxel.json");
        var result = factory.PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(basis, references)), fixture, fixture);
        if (kind == "instance") Failure(result, "reference_missing");
        else
        {
            using var reload = Success(result);
            Assert.AreEqual(0, reload.Report.RuntimeMissing.Length);
            Assert.IsTrue(reload.Report.AuthoringMissing.Any(m => m.Diagnostics.Any(d => d.Code == "reference_missing")));
        }
        using var restored = Success(factory.PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Restore(Success(factory.DecodeCapture(capture.Encode())), references)), fixture, fixture));
        Assert.IsTrue(restored.Report.AuthoringMissing.Length > 0);
        Assert.AreEqual(0, restored.Report.RuntimeMissing.Length);
        Assert.AreEqual(0, restored.View.Core.Designs.Length);
    }

    [TestMethod]
    public void Existing_missing_instance_draft_can_remain_or_recover_but_cannot_lose_the_recovered_reference()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var draft = new C.SavedContentDraft(new(Guid.NewGuid()), new(1),
            new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(7))), new C.DraftPayload.Design(Only()));
        using var saved = Success(factory.PrepareEdit(initial.View, new C.ContentEdit.SaveLocalDraft(draft)));
        var basis = saved.Activate();
        using var unchanged = Success(factory.PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(basis, References(basis))), fixture, fixture));
        Assert.IsTrue(unchanged.Report.AuthoringMissing.Length > 0);
        DesignFixtures.AddVoxel(fixture, "only");
        using var recovered = Success(factory.PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(basis, References(basis))), fixture, fixture));
        Assert.AreEqual(0, recovered.Report.AuthoringMissing.Length);
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/voxels/only.voxel.json");
        Failure(factory.PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(recovered.View, References(recovered.View))), fixture, fixture), "reference_missing");
    }

    [TestMethod]
    public void Abandoned_user_save_does_not_choose_first_display_and_user_edits_replay_without_external_files()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var assembly = Only() with { Specification = new C.DesignSpecification.Assembly(null),
            Layout = new([DesignFixtures.Entry()], [new(new(0, 0, 0), 0)], []) };
        using (var abandoned = Success(factory.PrepareEdit(initial.View, new C.ContentEdit.SaveBlueprint(
            new C.BlueprintSaveTarget.New(C.BlueprintLibrary.User), assembly with { Display = new("放弃", "描述") })))) { }
        using var accepted = Success(factory.PrepareEdit(initial.View, new C.ContentEdit.SaveBlueprint(
            new C.BlueprintSaveTarget.New(C.BlueprintLibrary.User), assembly with { Display = new("首次成功", "描述") })));
        var published = accepted.Activate();
        var address = ((C.PreparedContentEdit.BlueprintSaved)accepted.Edit!).Blueprint;
        var complete = (C.DesignEvaluation.Complete)((C.ContentLookup<C.BlueprintRecord>.Found)published.FindBlueprint(address)).Value.Evaluation;
        var good = new K.GoodId.Assembly(complete.Composition);
        Assert.AreEqual(C.ContentChange.Library | C.ContentChange.Persistent, accepted.Changes);
        Assert.AreEqual(initial.View.PersistentRevision.Value + 1, published.PersistentRevision.Value);
        Assert.IsInstanceOfType<C.ContentLookup<C.GoodPresentation>.NotFound>(initial.View.FindGood(good));
        Assert.AreEqual(0, published.Core.DesignGoods.Length);
        using var renamed = Success(factory.PrepareEdit(published, new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.Overwrite(address),
            assembly with { Display = new("后来名称", "描述") })));
        var latest = renamed.Activate();
        Assert.AreEqual(C.ContentChange.Library, renamed.Changes);
        Assert.AreEqual(published.PersistentRevision, latest.PersistentRevision);
        Assert.AreEqual("首次成功", ((C.ContentLookup<C.GoodPresentation>.Found)latest.FindGood(good)).Value.Display.Text.Name);
        var capture = Success(factory.Capture(latest, References(latest)));
        var json = JsonNode.Parse(capture.Encode())!;
        Assert.AreEqual(0, json["local_blueprints"]!.AsArray().Count);
        var history = Success(factory.DecodeReplay(Success(factory.CaptureReplay([initial.View, published, latest], References(latest))).Encode())).History;
        var replayed = history[^1];
        Assert.AreEqual("后来名称", ((C.ContentLookup<C.BlueprintRecord>.Found)replayed.FindBlueprint(address)).Value.Definition.Display.Name);
        Assert.AreEqual("首次成功", ((C.ContentLookup<C.GoodPresentation>.Found)replayed.FindGood(good)).Value.Display.Text.Name);
        Assert.AreEqual(0, replayed.Core.DesignGoods.Length);
    }

    [TestMethod]
    public void Library_design_is_transferred_by_target_input_without_adding_a_dynamic_core_catalog()
    {
        using var initial = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var draft = DesignFixtures.Vehicle();
        using var library = Success(factory.PrepareEdit(initial.View,
            new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(C.BlueprintLibrary.Local), draft)));
        var basis = library.Activate();
        using var target = Success(factory.PrepareEdit(basis, new C.ContentEdit.ConstructionTarget(draft)));
        var input = ((C.PreparedContentEdit.ConstructionTarget)target.Edit!).Design;
        Assert.AreEqual(basis.Core.Identity, input.InputIdentity);
        Assert.AreEqual(0, target.View.Core.Designs.Length);
        Assert.AreEqual(C.ContentChange.Presentation | C.ContentChange.Persistent, target.Changes);
        var capture = Success(factory.Capture(target.View, new([input.Design.Design], [], [], [basis.Core.Identity])));
        Assert.IsTrue(capture.Designs.Contains(input.Design.Design));
        using var instance = Success(factory.PrepareEdit(target.View, new C.ContentEdit.InstanceTarget(input.Design.Design,
            draft with { Display = new("新名", "说明") })));
        Assert.IsTrue(((C.PreparedContentEdit.InstanceTarget)instance.Edit!).Difference.CanApplyImmediately);
    }

    [TestMethod]
    public void Root_composite_is_encodable_but_all_player_publication_and_comparison_paths_reject_it()
    {
        using var initial = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var draft = Only() with { Specification = new C.DesignSpecification.FunctionalComposite([new K.PartCapability.Weapon(10, 1)], null) };
        var saved = new C.SavedContentDraft(new(Guid.NewGuid()), new(1), new C.DraftTarget.LocalBlueprint(null), new C.DraftPayload.Design(draft));
        Assert.IsInstanceOfType<C.ContentResult<C.SavedContentDraft>.Success>(factory.DecodeDraft(Success(factory.EncodeDraft(saved))));
        using var local = Success(factory.PrepareEdit(initial.View, new C.ContentEdit.SaveLocalDraft(saved)));
        foreach (var edit in new C.ContentEdit[]
        {
            new C.ContentEdit.SaveBlueprint(new C.BlueprintSaveTarget.New(C.BlueprintLibrary.Local), draft),
            new C.ContentEdit.ConstructionTarget(draft), new C.ContentEdit.InstanceTarget(new("absent"), draft)
        }) Failure(factory.PrepareEdit(initial.View, edit), "design_invalid");
        Failure(factory.CompareDesigns(initial.View, new("absent"), draft), "design_invalid");
        Assert.ThrowsExactly<NotImplementedException>(() => factory.EvaluateDesign(initial.View, draft));
    }

    private static C.DesignDraft Only() => new(new("独立设计", "仅引用一个定义"), new C.DesignSpecification.Vehicle(K.SizeClass.Small),
        new([DesignFixtures.Entry("only")], [new(new(0, 0, 0), 0)], []));
}

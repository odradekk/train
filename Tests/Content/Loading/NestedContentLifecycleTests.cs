using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using static Train.Content.Tests.Loading.ContentRestoreTests;
using static Train.Content.Tests.Loading.NestedDesignTests;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class NestedContentLifecycleTests
{
    [TestMethod]
    public void Inventory_only_composite_reference_blocks_reload_but_restores_as_missing()
    {
        var fixture = CompositeContent();
        using var initial = fixture.Load();
        var good = new K.GoodId.Composite(new("core:engine"));
        Assert.IsInstanceOfType<C.ContentLookup<C.GoodPresentation>.Found>(initial.View.FindGood(good));
        Assert.IsTrue(initial.View.Core.Goods.Any(g => g.Good == good));
        var references = References(initial.View) with { Definitions = [new(new K.CoreDefinitionReference.Composite(new("core:engine")), null, "inventory.engine", false)] };
        var saved = Success(ContentFactory.Create().Capture(initial.View, references));
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        Failure(PrepareReload(fixture, initial.View, references), "reference_missing");
        using var restored = Restore(fixture, saved, references);
        Assert.IsTrue(restored.Report.RuntimeMissing.Any(m => m.Field == "inventory.engine"));
        Assert.IsInstanceOfType<C.ContentLookup<C.GoodPresentation>.Missing>(restored.View.FindGood(good));
    }

    [TestMethod]
    public void Missing_composite_is_one_placeholder_and_only_its_ancestor_sweep_loses_known_range()
    {
        var fixture = CompositeContent();
        AddHost(fixture, "host", true);
        var prepared = fixture.Prepare();
        if (prepared is C.ContentResult<C.IContentCandidate>.Failure failure)
            Assert.Fail(string.Join("\n", failure.Diagnostics.Select(d => $"{d.Code} {d.Field} {d.Message}")));
        using var initial = Success(prepared);
        var original = Blueprint(initial.View, "host");
        var references = References(initial.View, "host");
        var saved = Success(ContentFactory.Create().Capture(initial.View, references));
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/host.blueprint.json");
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        using var restored = Restore(fixture, saved, references);
        var partial = (C.DesignEvaluation.Partial)Found(restored.View.FindDesign(original.Design));
        Assert.IsFalse(partial.Scene.OccupancyComplete);
        Assert.AreEqual(1, partial.Scene.NestedObjects.Count(n => n.Details is C.NestedObjectDetails.MissingFunctionalComposite));
        Assert.AreEqual(1, partial.Scene.EditLocks.Length);
        var missing = partial.CorePlaceholder!.Parts.Single(p => p.Missing);
        Assert.AreEqual(original.Core!.Design.Parts.Single(p => p.Definition is K.CoreDefinitionReference.Composite).Id, missing.Id);
        Assert.IsFalse(partial.CorePlaceholder.CanConstruct);
        var missingSweep = partial.Scene.RotationSweeps.Single(s => s.NestedItemIndices.SequenceEqual(new[] { 0, 0 }));
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown>(missingSweep.SweptCells);
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown>(missingSweep.ConflictCells);
        var parent = partial.Scene.RotationSweeps.Single(s => s.NestedItemIndices.SequenceEqual(new[] { 0 }));
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown>(parent.SweptCells);
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown>(parent.ConflictCells);
        var knownChild = partial.Scene.RotationSweeps.Single(s => s.NestedItemIndices.SequenceEqual(new[] { 0, 1 }));
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Known>(knownChild.SweptCells);
        Assert.IsInstanceOfType<C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown>(knownChild.ConflictCells);
    }

    [TestMethod]
    public void Changed_composite_footprint_keeps_saved_body_and_hash_but_disables_construction()
    {
        var fixture = CompositeContent();
        AddHost(fixture, "host");
        using var initial = fixture.Load();
        var before = Blueprint(initial.View, "host");
        var references = References(initial.View, "host");
        var factory = ContentFactory.Create();
        var saved = Success(factory.Capture(initial.View, references));
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/host.blueprint.json");
        WorldContentFixtures.Change(fixture, "resources/blueprints/engine.blueprint.json", json =>
            json["layout"]!["cells"] = JsonNode.Parse("""[[0,0,-1,0],[0,0,0,0],[0,0,1,0]]"""));
        using var reloaded = Success(PrepareReload(fixture, initial.View, references));
        using var restored = Restore(fixture, saved, references);
        foreach (var view in new[] { reloaded.View, restored.View })
        {
            var after = (C.DesignEvaluation.Complete)Found(view.FindDesign(before.Design));
            Assert.AreEqual(before.Design, after.Design);
            Assert.AreEqual(before.Composition, after.Composition);
            Assert.AreEqual(before.Definition.Display, after.Definition.Display);
            CollectionAssert.AreEqual(before.Definition.Layout.NestedItems.ToArray(), after.Definition.Layout.NestedItems.ToArray());
            Assert.IsFalse(after.IsValid);
            Assert.IsFalse(after.Core!.Design.CanConstruct);
            Assert.IsFalse(DesignFixtures.Evaluate(view, before.Definition).Core!.Design.CanConstruct);
        }
    }

    [TestMethod]
    public void Moving_composite_cells_changes_input_identity_but_presentation_does_not()
    {
        var fixture = CompositeContent();
        using var initial = fixture.Load();
        var draft = Host(new C.NestedDesignItem.FunctionalCompositeReference(new(0, 0, 1), C.VoxelOrientation.PxPy, new("core:engine"), null));
        var before = DesignFixtures.Evaluate(initial.View, draft);
        Assert.IsTrue(before.IsValid);
        Change(fixture, "resources/blueprints/engine.blueprint.json", json =>
            json["layout"]!["cells"] = JsonNode.Parse("""[[0,0,-1,0],[0,0,0,0]]"""));
        using var moved = fixture.Load();
        var after = DesignFixtures.Evaluate(moved.View, draft);
        Assert.AreEqual(before.Design, after.Design);
        Assert.IsFalse(after.IsValid);
        var good = new K.GoodId.Composite(new("core:engine"));
        Assert.AreNotEqual(initial.View.Core.Goods.Single(g => g.Good == good).Revision, moved.View.Core.Goods.Single(g => g.Good == good).Revision);
        Assert.AreNotEqual(initial.View.Core.Identity, moved.View.Core.Identity);
        Change(fixture, "resources/blueprints/engine.blueprint.json", json =>
        {
            json["display"]!["name"] = "只改显示";
            json["layout"]!["palette"]![0]!["shape"] = "wedge";
            json["layout"]!["palette"]![0]!["paint"] = "#abcdef";
        });
        using var repainted = fixture.Load();
        Assert.AreEqual(moved.View.Core.Identity, repainted.View.Core.Identity);
        Assert.AreEqual(moved.View.Core.Goods.Single(g => g.Good == good).Revision, repainted.View.Core.Goods.Single(g => g.Good == good).Revision);
    }

    [TestMethod]
    public void First_display_is_deterministic_and_survives_source_deletion_without_manifest_designs()
    {
        var fixture = DesignFixtures.Content();
        AddAssembly(fixture, "z-last", "后引入", "#123456");
        AddAssembly(fixture, "a-first", "先引入", "#abcdef");
        using var initial = fixture.Load();
        fixture.ReverseEnumeration = true;
        using var reversed = fixture.Load();
        var chosen = Blueprint(initial.View, "a-first");
        var other = Blueprint(initial.View, "z-last");
        Assert.AreEqual(chosen.Composition, other.Composition);
        Assert.AreNotEqual(chosen.Design, other.Design);
        var good = new K.GoodId.Assembly(chosen.Composition);
        Assert.AreEqual(Found(initial.View.FindGood(good)), Found(reversed.View.FindGood(good)));
        Assert.AreEqual("先引入", Found(initial.View.FindGood(good)).Display.Text.Name);
        var references = References(initial.View);
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/a-first.blueprint.json");
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/z-last.blueprint.json");
        using var reloaded = Success(PrepareReload(fixture, initial.View, references));
        Assert.AreEqual("先引入", Found(reloaded.View.FindGood(good)).Display.Text.Name);
        Assert.IsInstanceOfType<C.ContentLookup<C.DesignEvaluation>.Found>(reloaded.View.FindDesign(chosen.Design));
        var saved = Success(ContentFactory.Create().Capture(reloaded.View, references));
        Assert.IsTrue(saved.Designs.Contains(chosen.Design));
        AddAssembly(fixture, "new-source", "新来源", "#654321");
        using var restored = Restore(fixture, saved, references);
        Assert.AreEqual("先引入", Found(restored.View.FindGood(good)).Display.Text.Name);
        var newDesign = Blueprint(restored.View, "new-source");
        Assert.IsFalse(Success(ContentFactory.Create().Capture(restored.View, references)).Designs.Contains(newDesign.Design));
    }

    [TestMethod]
    public void Preview_and_discarded_candidate_do_not_register_display_in_basis()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var references = References(initial.View);
        var preview = DesignFixtures.Evaluate(initial.View, Assembly());
        var good = new K.GoodId.Assembly(preview.Composition);
        Assert.IsFalse(initial.View.Core.DesignGoods.Any(g => g.Good == good));
        var before = Success(ContentFactory.Create().Capture(initial.View, references)).Encode();
        AddAssembly(fixture, "introduced", "候选", "#abcdef");
        using (var candidate = Success(PrepareReload(fixture, initial.View, references)))
            Assert.IsTrue(candidate.View.Core.DesignGoods.Any(g => g.Good == good));
        Assert.IsFalse(initial.View.Core.DesignGoods.Any(g => g.Good == good));
        Assert.AreEqual(before, Success(ContentFactory.Create().Capture(initial.View, references)).Encode());
    }

    [TestMethod]
    public void Reload_registering_only_restored_library_displays_advances_generation_and_persistent_revision()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var references = References(initial.View);
        var saved = Success(ContentFactory.Create().Capture(initial.View, references));
        AddAssembly(fixture, "introduced", "新增构成", "#abcdef");
        using var restored = Restore(fixture, saved, references);
        Assert.AreEqual(0, restored.View.Core.DesignGoods.Length);
        using var reloaded = Success(PrepareReload(fixture, restored.View, references));
        Assert.AreEqual(C.ContentChange.Persistent, reloaded.Changes);
        Assert.AreEqual(restored.View.Generation.Value + 1, reloaded.View.Generation.Value);
        Assert.AreEqual(restored.View.PersistentRevision.Value + 1, reloaded.View.PersistentRevision.Value);
        Assert.AreEqual(1, reloaded.View.Core.DesignGoods.Length);
    }

    private static C.DesignEvaluation.Complete Blueprint(C.IContentView view, string id) =>
        (C.DesignEvaluation.Complete)Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation;

    [TestMethod]
    public void Replay_rebuilds_nested_inputs_and_first_display_from_each_fixed_history()
    {
        var fixture = CompositeContent();
        var assembly = DesignFixtures.Blueprint("assembly");
        assembly["category"] = "large_equipment";
        assembly.Remove("simulation");
        assembly["layout"]!["composites"] = JsonNode.Parse("""[{"at":[0,0,1],"blueprint":"engine"}]""");
        AddBlueprint(fixture, assembly);
        using var initial = fixture.Load();
        var before = Blueprint(initial.View, "assembly");
        var good = new K.GoodId.Assembly(before.Composition);
        var factory = ContentFactory.Create();
        Change(fixture, "resources/blueprints/engine.blueprint.json", json =>
            json["simulation"]!["behaviors"] = JsonNode.Parse("""{"platform":{}}"""));
        using var reloaded = Success(PrepareReload(fixture, initial.View, References(initial.View)));
        Assert.AreNotEqual(initial.View.Core.Identity, reloaded.View.Core.Identity);
        var references = References(reloaded.View) with { Inputs = [initial.View.Core.Identity, reloaded.View.Core.Identity] };
        var replay = Success(factory.CaptureReplay([initial.View, reloaded.View], references));
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/assembly.blueprint.json");
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        var decoded = Success(factory.DecodeReplay(replay.Encode()));
        for (var index = 0; index < 2; index++)
        {
            var view = decoded.History[index];
            Assert.AreEqual(index == 0 ? initial.View.Core.Identity : reloaded.View.Core.Identity, view.Core.Identity);
            Assert.AreEqual(before.Design, Found(view.FindGood(good)).DisplayDesign);
            var projection = ((C.DesignEvaluation.Complete)Found(view.FindDesign(before.Design))).Core!.Design;
            Assert.IsInstanceOfType(projection.Parts.Single().Capabilities.Single(), index == 0 ? typeof(K.PartCapability.Control) : typeof(K.PartCapability.Platform));
        }
    }

    private static void AddAssembly(ContentFixture fixture, string id, string name, string paint)
    {
        var json = DesignFixtures.Blueprint(id);
        json["category"] = "large_equipment";
        json.Remove("simulation");
        json["display"]!["name"] = name;
        json["layout"]!["palette"]![0]!["paint"] = paint;
        WorldContentFixtures.AddBlueprint(fixture, json);
    }

    private static void AddHost(ContentFixture fixture, string id, bool nestedRotation = false)
    {
        var json = DesignFixtures.Blueprint(id);
        json["layout"]!["composites"] = nestedRotation
            ? JsonNode.Parse("""[{"at":[0,1,0],"design":{"category":"large_equipment","display":{"name":"父","description":"测试"},"appearance":{"behaviors":{"rotation":{"axis":"y","pivot":[0,0,0],"range":[0,15],"speed":20}}},"layout":{"palette":[],"cells":[],"composites":[{"at":[0,0,0],"blueprint":"engine"},{"at":[0,1,0],"design":{"category":"large_equipment","display":{"name":"子","description":"测试"},"appearance":{"behaviors":{"rotation":{"axis":"y","pivot":[0,0,0],"range":[0,15],"speed":20}}},"layout":{"palette":[{"voxel":"block"}],"cells":[[0,0,0,0]]}}}]}}}]""")
            : JsonNode.Parse("""[{"at":[0,0,1],"blueprint":"engine"}]""");
        WorldContentFixtures.AddBlueprint(fixture, json);
    }

    private static C.ContentResult<C.IContentCandidate> PrepareReload(ContentFixture fixture, C.IContentView basis, K.CoreReferenceManifest references) =>
        ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(basis, references)), fixture, fixture);
}

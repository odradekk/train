using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentRestoreTests
{
    [TestMethod]
    public void Missing_mod_survives_capture_restore_and_reinstallation_with_original_design_values()
    {
        var fixture = ModContent();
        using var original = fixture.Load();
        var record = ((C.ContentLookup<C.BlueprintRecord>.Found)original.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(ModId + ":engine")))).Value;
        var before = (C.DesignEvaluation.Complete)record.Evaluation;
        var references = new K.CoreReferenceManifest([before.Design], [],
            [new(new K.CoreDefinitionReference.Voxel(new(ModId + ":motor")), null, "instance.part", false),
             new(new K.CoreDefinitionReference.Terrain(new("core:absent")), null, "world.terrain", false)], [original.View.Core.Identity]);
        var factory = ContentFactory.Create();
        var saved = Success(factory.Capture(original.View, references));
        fixture.RemoveEntry(ContentFixture.Mods, "equipment");
        using var restored = Restore(fixture, saved, references);
        Assert.AreEqual(saved.Revision, restored.View.PersistentRevision);
        Assert.IsTrue(restored.Report.AuthoringMissing.IsEmpty);
        Assert.IsTrue(restored.Report.RuntimeMissing.Any(m => m.Package?.Value == ModId && m.Field == "instance.part"));
        Assert.IsTrue(restored.Report.RuntimeMissing.Any(m => m.Field.Contains("/layout/palette/", StringComparison.Ordinal)));
        Assert.IsTrue(restored.Report.RuntimeMissing.Any(m => m.Field == "world.terrain"));
        Assert.IsInstanceOfType<C.ContentLookup<C.TerrainAppearanceProjection>.Missing>(restored.View.FindTerrain(new("core:absent")));
        var partial = (C.DesignEvaluation.Partial)((C.ContentLookup<C.DesignEvaluation>.Found)restored.View.FindDesign(before.Design)).Value;
        Assert.AreEqual(before.Design, partial.CorePlaceholder!.Design);
        var part = partial.CorePlaceholder.Parts.Single();
        Assert.AreEqual(before.Core!.Design.Parts.Single().Id, part.Id);
        Assert.IsTrue(part.Missing);
        Assert.AreEqual(0L, part.MaxDurability);
        Assert.AreEqual(0L, part.HitWeight);
        Assert.AreEqual(new K.Money(0), part.BaseValue);
        Assert.IsTrue(part.Capabilities.IsEmpty);
        Assert.AreEqual(0d, partial.CorePlaceholder.MassTons);
        Assert.AreEqual(0L, partial.CorePlaceholder.MaxStructureDurability);
        Assert.IsFalse(partial.CorePlaceholder.CanConstruct);
        Assert.AreEqual(1, partial.CorePlaceholder.MissingReferences.Length);
        Assert.AreEqual(1, partial.Scene.EditLocks.Length);
        Assert.IsTrue(partial.Scene.Voxels.Single().Missing);
        Assert.IsTrue(restored.View.Core.Designs.Any(d => d.Design == before.Design));
        Assert.IsInstanceOfType<C.ContentLookup<C.VoxelAppearanceProjection>.Missing>(restored.View.FindVoxelAppearance(new(ModId + ":motor")));
        var resaved = Success(factory.Capture(restored.View, references));
        var installed = ModContent();
        using var recovered = Restore(installed, Success(factory.DecodeCapture(resaved.Encode())), references);
        var complete = (C.DesignEvaluation.Complete)((C.ContentLookup<C.DesignEvaluation>.Found)recovered.View.FindDesign(before.Design)).Value;
        Assert.AreEqual(before.Design, complete.Design);
        Assert.AreEqual(before.Composition, complete.Composition);
        Assert.AreEqual(before.Core.Design.MaxStructureDurability, complete.Core!.Design.MaxStructureDurability);
        Assert.AreEqual(before.Core.Design.MassTons, complete.Core.Design.MassTons);
        Assert.AreEqual(before.Core.Design.Parts.Single().MaxDurability, complete.Core.Design.Parts.Single().MaxDurability);
        Assert.AreEqual(before.Core.Design.Parts.Single().Id, complete.Core.Design.Parts.Single().Id);
        Assert.AreEqual(saved.Encode(), resaved.Encode());
        var newDraft = Success(factory.EvaluateDesign(restored.View,
            record.Definition with { Specification = new C.DesignSpecification.Vehicle(null) }));
        Assert.IsInstanceOfType<C.DesignEvaluation.Partial>(newDraft);
        Assert.IsNull(((C.DesignEvaluation.Partial)newDraft).CorePlaceholder);
    }

    [TestMethod]
    public void Prepared_restore_keeps_its_report_assets_and_layout_after_installation_changes_and_disposal()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var basis = factory.WithSettlementCopies(initial.View, [Template(41)]);
        var references = References(basis, "engine") with { Templates = [Template(41)] };
        var saved = Success(factory.Capture(basis, references));
        using var restored = Restore(fixture, saved, references);
        var view = restored.View;
        var report = restored.Report;
        var cube = (C.VoxelPresentation.Cube)WorldContentFixtures.Found(view.FindVoxelAppearance(new("core:machine"))).Presentation;
        var asset = WorldContentFixtures.Found(view.FindAsset(cube.Top));
        var expected = Success(factory.Capture(view, references)).Encode();
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/machine/tile.png", [21, 22], new C.AssetMetadata.Png(64, 64));
        fixture.RemoveEntry(ContentFixture.BuiltIn, WorldContentFixtures.TemplatePath);
        restored.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => restored.Activate());
        Assert.AreSame(report, restored.Report);
        CollectionAssert.AreEqual(asset.Bytes.ToArray(), WorldContentFixtures.Found(view.FindAsset(cube.Top)).Bytes.ToArray());
        Assert.AreEqual(expected, Success(factory.Capture(view, references)).Encode());
        Assert.AreEqual(saved.Encode(), Success(factory.Capture(basis, references)).Encode());
    }

    [TestMethod]
    public void Restore_rejects_missing_copy_and_plot_mismatch_and_preserves_captured_revision()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var basis = factory.WithSettlementCopies(initial.View, [Template(41)]);
        var references = References(basis, "engine") with { Templates = [Template(41)] };
        var saved = Success(factory.Capture(basis, references));
        var json = JsonNode.Parse(saved.Encode())!;
        json["templates"] = new JsonArray();
        var withoutCopy = Success(factory.DecodeCapture(json.ToJsonString()));
        Failure(fixtureRestore(withoutCopy, references), "inconsistent_capture");
        Failure(fixtureRestore(saved, references with { Templates = [Template(41) with { Plots = [new(7, K.PlotSize.Small)] }] }), "inconsistent_capture");
        var withoutDesignJson = JsonNode.Parse(saved.Encode())!;
        var designs = withoutDesignJson["designs"]!.AsArray();
        designs.Remove(designs.Single(d => d!["hash"]!.GetValue<string>() == references.Designs.Single().Value));
        var withoutDesign = Success(factory.DecodeCapture(withoutDesignJson.ToJsonString()));
        Failure(fixtureRestore(withoutDesign, references), "inconsistent_capture");
        using var restored = Restore(fixture, saved, references);
        Assert.AreEqual(basis.PersistentRevision, restored.View.PersistentRevision);

        C.ContentResult<C.IContentCandidate> fixtureRestore(C.IContentCapture capture, K.CoreReferenceManifest manifest) =>
            factory.PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Restore(capture, manifest)), fixture, fixture);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Restore_and_reload_recalculate_durability_without_changing_design_or_part_identity(bool reload)
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var references = References(initial.View, "engine");
        var before = initial.View.Core.Designs.Single(d => d.Design == references.Designs.Single());
        var saved = Success(ContentFactory.Create().Capture(initial.View, references));
        WorldContentFixtures.Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 2);
        using var lower = Prepare(initial.View);
        var reduced = lower.View.Core.Designs.Single(d => d.Design == before.Design);
        Assert.AreEqual(before.Parts.Single().Id, reduced.Parts.Single().Id);
        Assert.AreEqual(2L, reduced.Parts.Single().MaxDurability);
        Assert.IsTrue(reduced.MaxStructureDurability < before.MaxStructureDurability);
        WorldContentFixtures.Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 5);
        using var recovered = Prepare(lower.View);
        var after = recovered.View.Core.Designs.Single(d => d.Design == before.Design);
        Assert.AreEqual(before.Parts.Single().MaxDurability, after.Parts.Single().MaxDurability);
        Assert.AreEqual(before.MaxStructureDurability, after.MaxStructureDurability);
        Assert.AreEqual(before.Parts.Single().Id, after.Parts.Single().Id);

        C.IContentCandidate Prepare(C.IContentView basis) => Success(ContentFactory.Create().PrepareLoad(
            new(fixture.Installation, reload ? new C.ContentLoadPurpose.Reload(basis, references) : new C.ContentLoadPurpose.Restore(saved, references)), fixture, fixture));
    }

    internal const string ModId = "11111111-1111-4111-8111-111111111111";
    internal static ContentFixture ModContent()
    {
        var fixture = DesignFixtures.Content();
        fixture.Installation = fixture.Installation with { EnabledMods = [new(ModId)] };
        fixture.AddFile(ContentFixture.Mods, "equipment/mod.json", $$$"""
            {"schema_version":2,"id":"{{{ModId}}}","name":"设备","version":"1.0.0","authors":["测试"]}
            """);
        var voxel = JsonNode.Parse(ContentFixture.VoxelJson)!;
        voxel["id"] = "motor";
        voxel["simulation"]!["behaviors"] = JsonNode.Parse("""{"control":{}}""");
        fixture.AddFile(ContentFixture.Mods, "equipment/resources/voxels/motor.voxel.json", voxel.ToJsonString());
        fixture.AddAsset(ContentFixture.Mods, "equipment/resources/voxels/motor/tile.png", [20, 21], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, "equipment/resources/voxels/motor/icon.png", [22, 23], new C.AssetMetadata.Png(128, 128));
        var blueprint = DesignFixtures.Blueprint("engine");
        blueprint["layout"]!["palette"]![0]!["voxel"] = "motor";
        fixture.AddFile(ContentFixture.Mods, "equipment/resources/blueprints/engine.blueprint.json", blueprint.ToJsonString());
        return fixture;
    }
    internal static C.IContentCandidate Restore(ContentFixture fixture, C.IContentCapture capture, K.CoreReferenceManifest references) =>
        Success(ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Restore(capture, references)), fixture, fixture));
}

using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using static Train.Content.Tests.Loading.ContentRestoreTests;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentReloadTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Reload_rejects_newly_missing_required_definition_or_design_dependency(bool designDependency)
    {
        var fixture = DesignFixtures.Content();
        AddBlueprint(fixture, DesignFixtures.Blueprint("engine"));
        using var initial = fixture.Load();
        var basis = initial.View;
        var references = References(basis, designDependency ? ["engine"] : []) with
        {
            Definitions = designDependency ? [] : [new(new K.CoreDefinitionReference.Voxel(new("core:block")), null, "instance.part", false)]
        };
        var saved = Success(ContentFactory.Create().Capture(basis, references)).Encode();
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json");
        var rejected = Prepare(fixture, basis, references);
        ContentCaptureTests.Failure(rejected, "reference_missing");
        var diagnostics = ((C.ContentResult<C.IContentCandidate>.Failure)rejected).Diagnostics;
        Assert.IsTrue(diagnostics.Any(d => d.Stage == "reload" &&
            d.Subject == new C.ContentReference.Core(new K.CoreDefinitionReference.Voxel(new("core:block"))) &&
            (designDependency ? d.Field.Contains("/layout/palette/", StringComparison.Ordinal) : d.Field == "instance.part")));
        Assert.AreEqual(saved, Success(ContentFactory.Create().Capture(basis, references)).Encode());
    }

    [TestMethod]
    public void Reload_retains_and_recovers_existing_mod_placeholder_but_rejects_new_core_missing()
    {
        var fixture = ModContent();
        using var initial = fixture.Load();
        var record = Found(initial.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(ModId + ":engine"))));
        var design = ((C.DesignEvaluation.Complete)record.Evaluation).Design;
        var references = new K.CoreReferenceManifest([design], [],
            [new(new K.CoreDefinitionReference.Voxel(new("core:block")), null, "instance.block", false)], [initial.View.Core.Identity]);
        var saved = Success(ContentFactory.Create().Capture(initial.View, references));
        fixture.RemoveEntry(ContentFixture.Mods, "equipment");
        using var restored = Restore(fixture, saved, references);
        using var unchanged = Reload(fixture, restored.View, references);
        Assert.IsInstanceOfType<C.DesignEvaluation.Partial>(Found(unchanged.View.FindDesign(design)));
        Assert.AreSame(restored.View, unchanged.View);
        var installed = ModContent();
        using var recovered = Reload(installed, restored.View, references);
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(Found(recovered.View.FindDesign(design)));
        Assert.IsTrue(recovered.Report.RuntimeMissing.IsEmpty);
        installed.RemoveEntry(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json");
        ContentCaptureTests.Failure(Prepare(installed, restored.View, references), "reference_missing");
        Assert.IsInstanceOfType<C.DesignEvaluation.Partial>(Found(restored.View.FindDesign(design)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Reload_preserves_referenced_design_and_owner_copy_after_sources_change_or_disappear(bool remove)
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var basis = factory.WithSettlementCopies(initial.View, [Template(41)]);
        var references = References(basis, "engine") with { Templates = [Template(41)] };
        var saved = Success(factory.Capture(basis, references)).Encode();
        var layout = Found(basis.FindSettlementLayout(new(41)));
        if (remove)
        {
            fixture.RemoveEntry(ContentFixture.BuiltIn, ProfilePath);
            fixture.RemoveEntry(ContentFixture.BuiltIn, TemplatePath);
            fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        }
        else
        {
            Change(fixture, TemplatePath, json => json["plots"]![1]!["at"]![0] = 40);
            Change(fixture, "resources/blueprints/engine.blueprint.json", json => json["layout"]!["cells"]![0]![0] = 1);
        }
        using var reloaded = Reload(fixture, basis, references);
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(Found(reloaded.View.FindDesign(references.Designs.Single())));
        var retained = Found(reloaded.View.FindSettlementLayout(new(41)));
        Assert.AreEqual(layout.SourceTemplate, retained.SourceTemplate);
        Assert.AreEqual(layout.SceneModel.Design, retained.SceneModel.Design);
        Assert.AreEqual(layout.Display, retained.Display);
        CollectionAssert.AreEqual(layout.Plots.ToArray(), retained.Plots.ToArray());
        Assert.AreEqual(saved, Success(factory.Capture(reloaded.View, references)).Encode());
        Assert.AreEqual(basis.PersistentRevision, reloaded.View.PersistentRevision);
    }

    [TestMethod]
    public void Reload_without_changes_reuses_basis_and_revisions()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        using var reloaded = Reload(fixture, initial.View, References(initial.View, "engine"));
        Assert.AreSame(initial.View, reloaded.Basis);
        Assert.AreSame(initial.View, reloaded.View);
        Assert.AreEqual(C.ContentChange.None, reloaded.Changes);
        Assert.AreEqual(initial.View.Generation, reloaded.View.Generation);
        Assert.AreEqual(initial.View.PersistentRevision, reloaded.View.PersistentRevision);
    }

    [TestMethod]
    [DataRow("semantic", C.ContentChange.CoreInput)]
    [DataRow("asset", C.ContentChange.Presentation)]
    [DataRow("add", C.ContentChange.Library)]
    [DataRow("remove", C.ContentChange.Library)]
    [DataRow("modify", C.ContentChange.Library)]
    [DataRow("combined", C.ContentChange.CoreInput | C.ContentChange.Presentation | C.ContentChange.Library)]
    public void Reload_classifies_effective_changes_and_advances_only_generation(string mutation, C.ContentChange expected)
    {
        var fixture = DesignFixtures.Content();
        AddBlueprint(fixture, DesignFixtures.Blueprint("engine"));
        using var initial = fixture.Load();
        var references = References(initial.View);
        if (mutation is "semantic" or "combined")
            Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 2);
        if (mutation is "asset" or "combined")
            fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/machine/tile.png", [21, 22], new C.AssetMetadata.Png(32, 32));
        if (mutation is "add" or "combined") AddBlueprint(fixture, DesignFixtures.Blueprint("extra"));
        if (mutation == "remove") fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        if (mutation == "modify")
            Change(fixture, "resources/blueprints/engine.blueprint.json", json => json["layout"]!["cells"]![0]![0] = 1);
        using var reloaded = Reload(fixture, initial.View, references);
        Assert.AreEqual(expected, reloaded.Changes);
        Assert.AreEqual(initial.View.Generation.Value + 1, reloaded.View.Generation.Value);
        Assert.AreEqual(initial.View.PersistentRevision, reloaded.View.PersistentRevision);
        Assert.AreEqual(expected.HasFlag(C.ContentChange.CoreInput), initial.View.Core.Identity != reloaded.View.Core.Identity);
    }

    [TestMethod]
    public void Disposing_reload_does_not_activate_or_mutate_basis()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var basis = initial.View;
        var references = References(basis, "engine");
        var saved = Success(ContentFactory.Create().Capture(basis, references)).Encode();
        var before = basis.Core.Designs.Single(d => d.Design == references.Designs.Single());
        Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 2);
        using var reloaded = Reload(fixture, basis, references);
        reloaded.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => reloaded.Activate());
        Assert.AreSame(before, basis.Core.Designs.Single(d => d.Design == references.Designs.Single()));
        Assert.AreEqual(saved, Success(ContentFactory.Create().Capture(basis, references)).Encode());
    }

    [TestMethod]
    public void Reload_updates_generation_catalog_and_railway_appearance_without_changing_current_core_identity()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var references = References(initial.View);
        var factory = ContentFactory.Create();
        Change(fixture, ProfilePath, json => json["configuration"]!["width_meters"] = 1200);
        Change(fixture, "resources/railway-rules/rail.railway-rule.json", json => json["appearance"]!["maximum_grade"] = 0.2);
        using var updated = Reload(fixture, initial.View, references);
        Assert.AreEqual(initial.View.Core.Identity, updated.View.Core.Identity);
        Assert.AreEqual(initial.View.Generation.Value + 1, updated.View.Generation.Value);
        Assert.IsTrue(updated.Changes.HasFlag(C.ContentChange.Presentation));
        Assert.AreEqual(0.2, Found(updated.View.FindRailwayAppearance(new("core:rail"))).MaximumGrade);
        Assert.AreEqual(1200d, Success(factory.CreateWorldInput(updated.View, new("core:world"), 1, "test")).Configuration.WidthMeters);
    }

    [TestMethod]
    [DataRow("missing", "file_missing")]
    [DataRow("decode", "asset_decode_failed")]
    [DataRow("unsupported", "asset_unsupported")]
    [DataRow("rule", "asset_rule_failed")]
    public void Reload_rejects_asset_failure_in_enabled_unreferenced_mod_and_retains_basis(string failure, string code)
    {
        var fixture = ModContent();
        using var initial = fixture.Load();
        var references = References(initial.View);
        var factory = ContentFactory.Create();
        var saved = Success(factory.Capture(initial.View, references)).Encode();
        const string path = "equipment/resources/voxels/motor/tile.png";
        if (failure == "missing") fixture.SetReadFailure(ContentFixture.Mods, path, C.ContentIoFailure.Missing);
        if (failure == "rule") fixture.AddAsset(ContentFixture.Mods, path, [20, 21], new C.AssetMetadata.Png(3, 3));
        var result = factory.PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(initial.View, references)),
            fixture, new FailedModInspector(fixture, failure));
        Failure(result, code);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d =>
            d.Code == code && d.Source == new C.ContentSourceLocation(new(ModId), path) &&
            d.Field == "/appearance/presentation/faces/top"));
        Assert.AreEqual(saved, Success(factory.Capture(initial.View, references)).Encode());
        Assert.IsInstanceOfType<C.ContentLookup<C.VoxelAppearanceProjection>.Found>(initial.View.FindVoxelAppearance(new(ModId + ":motor")));
    }

    private sealed class FailedModInspector(ContentFixture fixture, string failure) : C.IAssetInspector
    {
        public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes) =>
            bytes.SequenceEqual(new byte[] { 20, 21 }) && failure is "decode" or "unsupported"
                ? new C.AssetInspection.Failure(failure == "decode" ? C.AssetInspectionFailure.CannotDecode : C.AssetInspectionFailure.UnsupportedContent, "bad mod asset")
                : fixture.Inspect(kind, revision, bytes);
    }

    private static C.ContentResult<C.IContentCandidate> Prepare(ContentFixture fixture, C.IContentView basis, K.CoreReferenceManifest references) =>
        ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(basis, references)), fixture, fixture);

    private static C.IContentCandidate Reload(ContentFixture fixture, C.IContentView basis, K.CoreReferenceManifest references) =>
        Success(Prepare(fixture, basis, references));
}

using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class BlueprintLoadingTests
{
    [TestMethod]
    public void Building_and_scene_model_blueprints_bind_their_specification_and_flatten_region()
    {
        var fixture = DesignFixtures.Content();
        var building = DesignFixtures.Blueprint("building");
        building["category"] = "building";
        building["simulation"] = JsonNode.Parse("""{"plot_size":"medium","building_type":"production","facility_tags":["food_processing","assembly"]}""");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/building.blueprint.json", building.ToJsonString());
        var scene = DesignFixtures.Blueprint("scene");
        scene["category"] = "scene_model";
        scene.Remove("simulation");
        scene["layout"]!["palette"]![0]!["voxel"] = "scenery";
        scene["appearance"] = JsonNode.Parse("""{"flatten":{"from":[-1,-2],"to":[1,2],"height":3}}""");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/scene.blueprint.json", scene.ToJsonString());
        using var candidate = fixture.Load();
        var design = candidate.View.Core.Designs.Single();
        var kind = (K.DesignKind.Building)design.Kind;
        Assert.AreEqual(K.PlotSize.Medium, kind.PlotSize);
        Assert.AreEqual(K.BuildingKind.Production, kind.Type);
        CollectionAssert.AreEqual(new[] { K.FacilityTag.Assembly, K.FacilityTag.FoodProcessing }, kind.FacilityTags.ToArray());
        var record = ((C.ContentLookup<C.BlueprintRecord>.Found)candidate.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:scene")))).Value;
        var evaluation = (C.DesignEvaluation.Complete)record.Evaluation;
        Assert.IsNull(evaluation.Core);
        Assert.AreEqual(new C.FlattenRegion(new(-1, -2), new(1, 2), 3), evaluation.Scene.RootFlatten);
    }

    [TestMethod]
    public void Package_blueprints_publish_lookups_and_sorted_deduplicated_designs_without_changing_core_identity()
    {
        var fixture = DesignFixtures.Content();
        using var withoutBlueprints = fixture.Load();
        var first = DesignFixtures.Blueprint("first");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/first.blueprint.json", first.ToJsonString());
        var duplicate = (JsonObject)first.DeepClone();
        duplicate["id"] = "duplicate";
        duplicate["layout"]!["palette"]![0]!["orientation"] = "px_py";
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/duplicate.blueprint.json", duplicate.ToJsonString());
        var other = DesignFixtures.Blueprint("other");
        other["display"]!["name"] = "另一设计";
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/other.blueprint.json", other.ToJsonString());
        var scene = DesignFixtures.Blueprint("scene");
        scene["category"] = "scene_model";
        scene.Remove("simulation");
        scene["layout"]!["palette"]![0]!["voxel"] = "scenery";
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/scene.blueprint.json", scene.ToJsonString());
        using var normal = fixture.Load();
        fixture.ReverseEnumeration = true;
        using var reversed = fixture.Load();
        Assert.AreEqual(withoutBlueprints.View.Core.Identity, normal.View.Core.Identity);
        Assert.AreEqual(4, normal.View.Blueprints.Length);
        Assert.IsTrue(normal.View.Blueprints.All(b => b.Usable && b.Diagnostics.IsEmpty));
        Assert.AreEqual(2, normal.View.Core.Designs.Length);
        CollectionAssert.AreEqual(normal.View.Core.Designs.Select(d => d.Design.Value).Order(StringComparer.Ordinal).ToArray(), normal.View.Core.Designs.Select(d => d.Design.Value).ToArray());
        CollectionAssert.AreEqual(normal.View.Blueprints.Select(b => b.Address).ToArray(), reversed.View.Blueprints.Select(b => b.Address).ToArray());
        CollectionAssert.AreEqual(normal.View.Core.Designs.Select(d => d.Design).ToArray(), reversed.View.Core.Designs.Select(d => d.Design).ToArray());
        var record = ((C.ContentLookup<C.BlueprintRecord>.Found)normal.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:first")))).Value;
        Assert.AreEqual("core:block", record.Definition.Layout.Palette.Single().Voxel.Value);
        Assert.AreEqual(C.VoxelOrientation.PxPy, record.Definition.Layout.Palette.Single().Orientation);
        var evaluation = (C.DesignEvaluation.Complete)record.Evaluation;
        Assert.IsInstanceOfType<C.ContentLookup<C.DesignEvaluation>.Found>(normal.View.FindDesign(evaluation.Design));
        var sceneRecord = ((C.ContentLookup<C.BlueprintRecord>.Found)normal.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:scene")))).Value;
        Assert.IsNull(((C.DesignEvaluation.Complete)sceneRecord.Evaluation).Core);
    }

    [TestMethod]
    [DataRow("duplicate_json", "invalid_json")]
    [DataRow("missing_voxel", "reference_missing")]
    [DataRow("reference_blank", "invalid_field")]
    [DataRow("reference_leading_space", "invalid_field")]
    [DataRow("reference_trailing_space", "invalid_field")]
    [DataRow("reference_uppercase_namespace", "invalid_field")]
    [DataRow("reference_double_colon", "invalid_field")]
    [DataRow("reference_empty_namespace", "invalid_field")]
    [DataRow("unused_missing_voxel", "reference_missing")]
    [DataRow("flatten", "design_invalid")]
    [DataRow("empty", "design_invalid")]
    [DataRow("unknown_building", "invalid_field")]
    [DataRow("unknown_tag", "invalid_field")]
    [DataRow("duplicate_tags", "design_invalid")]
    [DataRow("paint", "design_invalid")]
    [DataRow("shape", "design_invalid")]
    [DataRow("unused_shape", "design_invalid")]
    [DataRow("unused_paint", "design_invalid")]
    [DataRow("unused_orientation", "design_invalid")]
    [DataRow("model_shape", "design_invalid")]
    [DataRow("wrong_kind", "reference_wrong_kind")]
    [DataRow("missing_size", "invalid_field")]
    [DataRow("unknown_size", "invalid_field")]
    [DataRow("unknown_field", "invalid_field")]
    [DataRow("old_schema", "unsupported_format")]
    [DataRow("filename", "invalid_field")]
    [DataRow("palette", "invalid_field")]
    [DataRow("duplicate_cell", "design_invalid")]
    [DataRow("unrepresentable_length", "design_invalid")]
    [DataRow("used_packages", "invalid_field")]
    [DataRow("nested", "reference_missing")]
    [DataRow("equipment", "invalid_field")]
    [DataRow("creature", "unsupported_format")]
    public void Invalid_builtin_blueprint_rejects_initial_load_with_source(string problem, string code)
    {
        var fixture = DesignFixtures.Content();
        var document = DesignFixtures.Blueprint();
        switch (problem)
        {
            case "unused_missing_voxel": document["layout"]!["palette"]!.AsArray().Add(JsonNode.Parse("""{"voxel":"absent"}""")); break;
            case "empty": document["layout"]!["cells"] = new JsonArray(); break;
            case "paint": document["layout"]!["palette"]![0]!["paint"] = "red"; break;
            case "shape": document["layout"]!["palette"]![0]!["shape"] = "unlisted"; break;
            case "unused_shape": document["layout"]!["palette"]!.AsArray().Add(JsonNode.Parse("""{"voxel":"block","shape":"unlisted"}""")); break;
            case "unused_paint": document["layout"]!["palette"]!.AsArray().Add(JsonNode.Parse("""{"voxel":"block","paint":"red"}""")); break;
            case "unused_orientation": document["layout"]!["palette"]!.AsArray().Add(JsonNode.Parse("""{"voxel":"block","orientation":"px_ny"}""")); break;
            case "model_shape":
                DesignFixtures.AddMeshVoxel(fixture);
                document["layout"]!["palette"]![0]!["voxel"] = "model";
                document["layout"]!["palette"]![0]!["shape"] = "cube";
                break;
            case "wrong_kind": document["layout"]!["palette"]![0]!["voxel"] = "test"; break;
            case "flatten":
                document["category"] = "scene_model";
                document.Remove("simulation");
                document["layout"]!["palette"]![0]!["voxel"] = "scenery";
                document["appearance"] = JsonNode.Parse("""{"flatten":{"from":[1,0],"to":[0,0],"height":0}}""");
                break;
            case "unknown_building":
            case "unknown_tag":
            case "duplicate_tags":
                document["category"] = "building";
                document["simulation"] = JsonNode.Parse("""{"plot_size":"small","building_type":"production","facility_tags":["assembly"]}""");
                if (problem == "unknown_building") document["simulation"]!["building_type"] = "unknown";
                else document["simulation"]!["facility_tags"] = problem == "unknown_tag" ? new JsonArray("unknown") : new JsonArray("assembly", "assembly");
                break;
            case "missing_voxel": document["layout"]!["palette"]![0]!["voxel"] = "absent"; break;
            case "reference_blank": document["layout"]!["palette"]![0]!["voxel"] = " "; break;
            case "reference_leading_space": document["layout"]!["palette"]![0]!["voxel"] = " block"; break;
            case "reference_trailing_space": document["layout"]!["palette"]![0]!["voxel"] = "block "; break;
            case "reference_uppercase_namespace": document["layout"]!["palette"]![0]!["voxel"] = "CORE:block"; break;
            case "reference_double_colon": document["layout"]!["palette"]![0]!["voxel"] = "core::block"; break;
            case "reference_empty_namespace": document["layout"]!["palette"]![0]!["voxel"] = ":block"; break;
            case "missing_size": document["simulation"]!.AsObject().Remove("size_class"); break;
            case "unknown_size": document["simulation"]!["size_class"] = "huge"; break;
            case "unknown_field": document["extra"] = true; break;
            case "old_schema": document["schema_version"] = 2; break;
            case "filename": document["id"] = "different"; break;
            case "palette": document["layout"]!["cells"]![0]![3] = 1; break;
            case "duplicate_cell": document["layout"]!["cells"]!.AsArray().Add(JsonNode.Parse("[0,0,0,0]")); break;
            case "unrepresentable_length": document["layout"]!["cells"] = JsonNode.Parse("[[0,0,-2147483648,0],[0,0,0,0]]"); break;
            case "used_packages": document["used_packages"] = new JsonArray(); break;
            case "nested": document["layout"]!["composites"] = JsonNode.Parse("""[{"at":[0,0,0],"blueprint":"absent"}]"""); break;
            case "equipment": document["category"] = "large_equipment"; break;
            case "creature": document["category"] = "creature"; break;
        }
        var text = document.ToJsonString();
        if (problem == "duplicate_json") text = text.Insert(1, "\"schema_version\":3,");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/test.blueprint.json", text);
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        var diagnostics = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics;
        if (problem == "unused_missing_voxel")
            Assert.IsTrue(diagnostics.Any(d => d.Code == "reference_missing" && d.Field == "/layout/palette/1/voxel"));
        if (problem.StartsWith("reference_", StringComparison.Ordinal))
            Assert.IsTrue(diagnostics.Any(d => d.Code == code && d.Field == "/layout/palette/0/voxel"));
        Assert.IsTrue(diagnostics.Any(d => d.Code == code && d.Source == new C.ContentSourceLocation(new("core"), "resources/blueprints/test.blueprint.json")), string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message)));
    }

    [TestMethod]
    [DataRow("missing_voxel", "reference_missing")]
    [DataRow("unrepresentable_length", "design_invalid")]
    public void Invalid_mod_blueprint_excludes_its_whole_package_and_transitive_dependents(string problem, string code)
    {
        const string source = "11111111-1111-4111-8111-111111111111";
        const string dependent = "22222222-2222-4222-8222-222222222222";
        const string last = "33333333-3333-4333-8333-333333333333";
        var fixture = DesignFixtures.Content();
        AddMod(fixture, "source", source, null);
        AddMod(fixture, "dependent", dependent, source);
        AddMod(fixture, "last", last, dependent);
        var bad = DesignFixtures.Blueprint();
        bad["layout"]!["palette"]![0]!["voxel"] = problem == "missing_voxel" ? "core:absent" : "core:block";
        if (problem == "unrepresentable_length") bad["layout"]!["cells"] = JsonNode.Parse("[[0,0,-2147483648,0],[0,0,0,0]]");
        fixture.AddFile(ContentFixture.Mods, "source/resources/blueprints/test.blueprint.json", bad.ToJsonString());
        var valid = DesignFixtures.Blueprint("valid");
        valid["layout"]!["palette"]![0]!["voxel"] = "core:block";
        fixture.AddFile(ContentFixture.Mods, "source/resources/blueprints/valid.blueprint.json", valid.ToJsonString());
        fixture.AddFile(ContentFixture.Mods, "dependent/resources/blueprints/valid.blueprint.json", valid.ToJsonString());
        using var candidate = fixture.Load();
        Assert.AreEqual(0, candidate.View.Blueprints.Length);
        Assert.AreEqual(0, candidate.View.Core.Designs.Length);
        var statuses = candidate.Report.Packages;
        Assert.AreEqual(C.PackageState.Invalid, statuses.Single(p => p.Metadata?.Id.Value == source).State);
        Assert.IsTrue(statuses.Single(p => p.Metadata?.Id.Value == source).Diagnostics.Any(d => d.Code == code));
        foreach (var id in new[] { dependent, last })
            Assert.AreEqual(C.PackageState.DependencyUnavailable, statuses.Single(p => p.Metadata?.Id.Value == id).State);
        Assert.AreEqual(3, candidate.View.Voxels.Length);
    }

    [TestMethod]
    public void Composite_reference_to_plain_assembly_excludes_its_whole_package_at_the_reference()
    {
        const string mod = "11111111-1111-4111-8111-111111111111";
        var fixture = DesignFixtures.Content();
        AddMod(fixture, "pack", mod, null);
        var assembly = DesignFixtures.Blueprint("assembly");
        assembly["category"] = "large_equipment";
        assembly.Remove("simulation");
        assembly["layout"]!["palette"]![0]!["voxel"] = "core:block";
        fixture.AddFile(ContentFixture.Mods, "pack/resources/blueprints/assembly.blueprint.json", assembly.ToJsonString());
        using (var valid = fixture.Load())
        {
            var record = ((C.ContentLookup<C.BlueprintRecord>.Found)valid.View.FindBlueprint(
                new(C.BlueprintLibrary.Packages, new(mod + ":assembly")))).Value;
            Assert.IsInstanceOfType<C.DesignSpecification.Assembly>(record.Definition.Specification);
        }

        var host = DesignFixtures.Blueprint("host");
        host["layout"]!["palette"]![0]!["voxel"] = "core:block";
        host["layout"]!["composites"] = JsonNode.Parse("""[{"at":[1,0,0],"blueprint":"assembly"}]""");
        fixture.AddFile(ContentFixture.Mods, "pack/resources/blueprints/host.blueprint.json", host.ToJsonString());
        using var candidate = fixture.Load();
        var package = candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == mod);
        Assert.AreEqual(C.PackageState.Invalid, package.State);
        var diagnostic = package.Diagnostics.Single(d => d.Code == "reference_wrong_kind");
        Assert.AreEqual("/layout/composites/0/blueprint", diagnostic.Field);
        Assert.AreEqual(new C.ContentSourceLocation(new(mod), "pack/resources/blueprints/host.blueprint.json"), diagnostic.Source);
        Assert.AreEqual(0, candidate.View.Blueprints.Length);
        Assert.AreEqual(0, candidate.View.Core.Designs.Length);
    }

    [TestMethod]
    public void Display_whitespace_normalizes_identically_for_drafts_and_package_blueprints()
    {
        var fixture = DesignFixtures.Content();
        var document = DesignFixtures.Blueprint();
        document["display"]!["name"] = " Test ";
        document["display"]!["description"] = " Design ";
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/test.blueprint.json", document.ToJsonString());
        using var candidate = fixture.Load();
        var record = ((C.ContentLookup<C.BlueprintRecord>.Found)candidate.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:test")))).Value;
        var loaded = (C.DesignEvaluation.Complete)record.Evaluation;
        var draft = record.Definition with { Display = new(" Test ", " Design ") };
        var evaluated = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.AreEqual(loaded.Design, evaluated.Design);
        Assert.AreEqual(record.Definition.Display with { Name = "Test", Description = "Design" }, evaluated.Definition.Display);
        Assert.AreEqual(evaluated.Definition.Display, record.Definition.Display);
    }

    [TestMethod]
    public void Mod_blueprint_local_voxel_reference_uses_its_package_despite_matching_core_definition()
    {
        const string mod = "11111111-1111-4111-8111-111111111111";
        var fixture = new ContentFixture();
        fixture.AddCube();
        AddMod(fixture, "pack", mod, null);
        var voxel = JsonNode.Parse(ContentFixture.VoxelJson)!;
        voxel["simulation"]!["mass"] = 3;
        fixture.AddFile(ContentFixture.Mods, "pack/resources/voxels/block.voxel.json", voxel.ToJsonString());
        fixture.AddAsset(ContentFixture.Mods, "pack/resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, "pack/resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
        fixture.AddFile(ContentFixture.Mods, "pack/resources/blueprints/test.blueprint.json", DesignFixtures.Blueprint().ToJsonString());

        using var candidate = fixture.Load();
        Assert.IsTrue(candidate.View.Voxels.Any(v => v.Definition.Value == "core:block"));
        var record = ((C.ContentLookup<C.BlueprintRecord>.Found)candidate.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(mod + ":test")))).Value;
        Assert.AreEqual(mod + ":block", record.Definition.Layout.Palette.Single().Voxel.Value);
        Assert.AreEqual(3d, ((C.DesignEvaluation.Complete)record.Evaluation).Core!.Design.MassTons);
    }

    [TestMethod]
    public void Retained_blueprint_uses_final_core_identity_after_another_voxel_package_is_excluded()
    {
        const string excluded = "11111111-1111-4111-8111-111111111111";
        const string retained = "33333333-3333-4333-8333-333333333333";
        var fixture = DesignFixtures.Content();
        AddMod(fixture, "excluded", excluded, null);
        AddMod(fixture, "retained", retained, null);
        fixture.AddFile(ContentFixture.Mods, "excluded/resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
        fixture.AddAsset(ContentFixture.Mods, "excluded/resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, "excluded/resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
        var bad = DesignFixtures.Blueprint();
        bad["layout"]!["palette"]![0]!["voxel"] = "core:absent";
        fixture.AddFile(ContentFixture.Mods, "excluded/resources/blueprints/test.blueprint.json", bad.ToJsonString());
        var good = DesignFixtures.Blueprint();
        good["layout"]!["palette"]![0]!["voxel"] = "core:block";
        fixture.AddFile(ContentFixture.Mods, "retained/resources/blueprints/test.blueprint.json", good.ToJsonString());
        using var candidate = fixture.Load();
        Assert.AreEqual(C.PackageState.Invalid, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == excluded).State);
        Assert.AreEqual(3, candidate.View.Voxels.Length);
        var record = ((C.ContentLookup<C.BlueprintRecord>.Found)candidate.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(retained + ":test")))).Value;
        var evaluation = (C.DesignEvaluation.Complete)record.Evaluation;
        Assert.AreEqual(candidate.View.Core.Identity, evaluation.Core!.InputIdentity);
        var found = ((C.ContentLookup<C.DesignEvaluation>.Found)candidate.View.FindDesign(evaluation.Design)).Value;
        Assert.AreEqual(candidate.View.Core.Identity, ((C.DesignEvaluation.Complete)found).Core!.InputIdentity);
    }

    private static void AddMod(ContentFixture fixture, string folder, string id, string? dependency)
    {
        var dependencies = dependency is null ? "[]" : $$"""[{"id":"{{dependency}}","min_version":"1.0.0"}]""";
        fixture.AddFile(ContentFixture.Mods, folder + "/mod.json", $$"""
            {"schema_version":2,"id":"{{id}}","name":"包","version":"1.0.0","authors":["作者"],"dependencies":{{dependencies}}}
            """);
        fixture.AddDirectory(ContentFixture.Mods, folder + "/resources");
        fixture.Installation = fixture.Installation with { EnabledMods = fixture.Installation.EnabledMods.Add(new(id)) };
    }
}

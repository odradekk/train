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
        CollectionAssert.AreEquivalent(new[] { K.FacilityTag.FoodProcessing, K.FacilityTag.Assembly }, kind.FacilityTags.ToArray());
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
    [DataRow("missing_size", "invalid_field")]
    [DataRow("filename", "invalid_field")]
    [DataRow("palette", "invalid_field")]
    [DataRow("duplicate_cell", "design_invalid")]
    [DataRow("unrepresentable_length", "design_invalid")]
    [DataRow("used_packages", "invalid_field")]
    [DataRow("nested", "unsupported_format")]
    [DataRow("equipment", "unsupported_format")]
    [DataRow("creature", "unsupported_format")]
    public void Invalid_builtin_blueprint_rejects_initial_load_with_source(string problem, string code)
    {
        var fixture = DesignFixtures.Content();
        var document = DesignFixtures.Blueprint();
        switch (problem)
        {
            case "missing_voxel": document["layout"]!["palette"]![0]!["voxel"] = "absent"; break;
            case "missing_size": document["simulation"]!.AsObject().Remove("size_class"); break;
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

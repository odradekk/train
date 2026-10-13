using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class WorldPackageTests
{
    [TestMethod]
    [DataRow("staple", 0)]
    [DataRow("prepared", 2)]
    public void Food_satisfying_goods_with_raw_good_tag_reject_the_population_rule(string good, int index)
    {
        var fixture = WorldContentFixtures.Content();
        WorldContentFixtures.Change(fixture, $"resources/voxels/{good}.voxel.json",
            j => j["simulation"]!["tags"] = new JsonArray("raw_good"));
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d =>
            d.Code == "invalid_field" && d.Source?.Path == "resources/rules/people.population-rule.json" &&
            d.Field == $"/simulation/consumption_goods/{index}/good"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Distinct_vehicle_blueprint_ids_with_the_same_hash_reject_the_profile_package(bool mod)
    {
        const string owner = "11111111-1111-4111-8111-111111111111";
        var fixture = WorldContentFixtures.Content();
        var alias = DesignFixtures.Blueprint("wagon");
        alias["id"] = "wagon-copy";
        WorldContentFixtures.AddBlueprint(fixture, alias);
        var profile = WorldContentFixtures.Profile();
        Qualify(profile);
        profile["configuration"]!["npc_initialization"]!["for_sale_vehicle_candidates"] = new JsonArray("core:wagon", "core:wagon-copy");
        var path = WorldContentFixtures.ProfilePath;
        if (mod)
        {
            fixture.Installation = fixture.Installation with { EnabledMods = [new(owner)] };
            fixture.AddFile(ContentFixture.Mods, "owner/mod.json", Manifest(owner).ToJsonString());
            path = "owner/" + path;
            fixture.AddFile(ContentFixture.Mods, path, profile.ToJsonString());
            using var candidate = fixture.Load();
            var rejected = candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == owner);
            Assert.AreEqual(C.PackageState.Invalid, rejected.State);
            Assert.IsTrue(rejected.Diagnostics.Any(d => d.Code == "invalid_field" &&
                d.Source?.Path == path && d.Field == "/configuration/npc_initialization/for_sale_vehicle_candidates"));
            Assert.AreEqual(C.PackageState.Available, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == "core").State);
            Assert.AreEqual(1, candidate.View.GenerationProfiles.Length);
        }
        else
        {
            fixture.AddFile(ContentFixture.BuiltIn, path, profile.ToJsonString());
            var result = fixture.Prepare();
            Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
            Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d =>
                d.Code == "invalid_field" && d.Source?.Path == path &&
                d.Field == "/configuration/npc_initialization/for_sale_vehicle_candidates"));
        }
    }

    [TestMethod]
    public void Cross_package_railway_coverage_rejects_the_profile_owner_and_its_dependents()
    {
        const string owner = "11111111-1111-4111-8111-111111111111";
        const string dependent = "22222222-2222-4222-8222-222222222222";
        var fixture = WorldContentFixtures.Content();
        fixture.Installation = fixture.Installation with { EnabledMods = [new(owner), new(dependent)] };
        fixture.AddFile(ContentFixture.Mods, "owner/mod.json", Manifest(owner).ToJsonString());
        var manifest = Manifest(dependent);
        manifest["dependencies"] = new JsonArray(new JsonObject { ["id"] = owner, ["min_version"] = "1.0.0" });
        fixture.AddFile(ContentFixture.Mods, "dependent/mod.json", manifest.ToJsonString());
        fixture.AddDirectory(ContentFixture.Mods, "dependent/resources");
        var profile = WorldContentFixtures.Profile();
        Qualify(profile);
        profile["configuration"]!["terrain_bands"]![0]!["maximum_tracks"] = 3;
        profile["configuration"]!["initial_railway"]!["maximum_tracks"] = 3;
        const string path = "owner/resources/generation-profiles/world.generation-profile.json";
        fixture.AddFile(ContentFixture.Mods, path, profile.ToJsonString());
        using var candidate = fixture.Load();
        var rejected = candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == owner);
        Assert.AreEqual(C.PackageState.Invalid, rejected.State);
        Assert.IsTrue(rejected.Diagnostics.Any(d => d.Code == "invalid_field" && d.Source?.Path == path
            && d.Field == "/configuration/terrain_bands"));
        Assert.AreEqual(C.PackageState.DependencyUnavailable, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == dependent).State);
        Assert.AreEqual(C.PackageState.Available, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == "core").State);
        Assert.AreEqual(2, WorldContentFixtures.Found(candidate.View.FindTerrain(new("core:plains"))).RailwayModels.Length);
        Assert.AreEqual(1, candidate.View.GenerationProfiles.Length);
    }

    [TestMethod]
    public void Built_in_profile_with_missing_railway_models_fails_loading()
    {
        var fixture = WorldContentFixtures.Content();
        WorldContentFixtures.Change(fixture, WorldContentFixtures.ProfilePath, j =>
        {
            j["configuration"]!["terrain_bands"]![0]!["maximum_tracks"] = int.MaxValue;
            j["configuration"]!["initial_railway"]!["maximum_tracks"] = int.MaxValue;
        });
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == "invalid_field"
            && d.Source?.Path == WorldContentFixtures.ProfilePath));
    }

    [TestMethod]
    [DataRow("wrong-rule", "/configuration/rules/population")]
    [DataRow("scene-good", "/rail_good")]
    [DataRow("duplicate-rule", "/id")]
    [DataRow("invalid-symbol", "/display/map_symbol")]
    [DataRow("wrong-terrain-voxel", "/surface_voxel")]
    public void Wrong_reference_kinds_duplicate_rule_ids_and_invalid_symbols_reject_the_package(string mutation, string expectedField)
    {
        var fixture = WorldContentFixtures.Content();
        switch (mutation)
        {
            case "wrong-rule":
                WorldContentFixtures.Change(fixture, WorldContentFixtures.ProfilePath, j => j["configuration"]!["rules"]!["population"] = "rail");
                break;
            case "scene-good":
                WorldContentFixtures.Change(fixture, "resources/railway-rules/rail.railway-rule.json", j => j["rail_good"] = "scenery");
                break;
            case "duplicate-rule":
                var rule = RuleParserTests.Combat(); rule["id"] = "rail";
                fixture.AddFile(ContentFixture.BuiltIn, "resources/combat-rules/rail.combat-rule.json", rule.ToJsonString());
                break;
            case "invalid-symbol":
                fixture.AddAsset(ContentFixture.BuiltIn, "resources/settlement-types/farm/symbol.png", [9], new C.AssetMetadata.Png(32, 32));
                break;
            case "wrong-terrain-voxel":
                WorldContentFixtures.Change(fixture, "resources/terrain-types/plains.terrain-type.json", j => j["surface_voxel"] = "rail");
                break;
        }
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        var code = mutation == "duplicate-rule" ? "duplicate_identity" : mutation == "invalid-symbol" ? "asset_rule_failed" : "reference_wrong_kind";
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == code && d.Field == expectedField));
    }

    [TestMethod]
    [DataRow("terrain-decoration", "/decorations")]
    [DataRow("template-scene", "/scene_model")]
    [DataRow("plot-building", "/plots")]
    [DataRow("for-sale-candidate", "/configuration/npc_initialization/for_sale_vehicle_candidates")]
    public void Blueprint_references_to_the_wrong_design_kind_report_their_field(string mutation, string expectedField)
    {
        var fixture = WorldContentFixtures.Content();
        const string terrainPath = "resources/terrain-types/plains.terrain-type.json";
        var path = mutation == "terrain-decoration" ? terrainPath
            : mutation == "for-sale-candidate" ? WorldContentFixtures.ProfilePath : WorldContentFixtures.TemplatePath;
        WorldContentFixtures.Change(fixture, path, json =>
        {
            switch (mutation)
            {
                case "terrain-decoration":
                    json["decorations"] = new JsonArray(new JsonObject { ["scene_model"] = "station", ["weight"] = 1 });
                    break;
                case "template-scene":
                    json["scene_model"] = "station";
                    break;
                case "plot-building":
                    json["plots"]![1]!["building"] = "wagon";
                    break;
                case "for-sale-candidate":
                    json["configuration"]!["npc_initialization"]!["for_sale_vehicle_candidates"] = new JsonArray("station");
                    break;
            }
        });
        var failure = WorldContentFixtures.Failure(fixture.Prepare());
        Assert.IsTrue(failure.Diagnostics.Any(d => d.Code == "reference_wrong_kind" &&
            d.Field == expectedField && d.Source?.Path == path));
    }

    private static JsonObject Manifest(string id) => new()
    {
        ["schema_version"] = 2, ["id"] = id, ["name"] = "世界配置扩展", ["version"] = "1.0.0", ["authors"] = new JsonArray("test")
    };

    private static void Qualify(JsonNode node)
    {
        var ids = new[] { "plains", "ore", "people", "market", "building", "rail", "battle", "wagon", "engine" };
        if (node is JsonObject obj)
            foreach (var property in obj.ToArray())
            {
                if (property.Key == "id" || property.Value is null) continue;
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && ids.Contains(text))
                    obj[property.Key] = "core:" + text;
                else Qualify(property.Value);
            }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && ids.Contains(text)) array[i] = "core:" + text;
                else if (array[i] is { } item) Qualify(item);
    }
}

using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class WorldPackageTests
{
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
        Assert.AreEqual(2, WorldContentTests.Found(candidate.View.FindTerrain(new("core:plains"))).RailwayModels.Length);
        Assert.AreEqual(1, candidate.View.GenerationProfiles.Length);
    }

    [TestMethod]
    public void Built_in_profile_with_missing_railway_models_fails_loading()
    {
        var fixture = WorldContentFixtures.Content();
        WorldIdentityTests.Change(fixture, WorldContentFixtures.ProfilePath, j =>
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
    [DataRow("wrong-rule")]
    [DataRow("scene-good")]
    [DataRow("duplicate-rule")]
    [DataRow("invalid-symbol")]
    [DataRow("wrong-terrain-voxel")]
    public void Wrong_reference_kinds_duplicate_rule_ids_and_invalid_symbols_reject_the_package(string mutation)
    {
        var fixture = WorldContentFixtures.Content();
        switch (mutation)
        {
            case "wrong-rule":
                WorldIdentityTests.Change(fixture, WorldContentFixtures.ProfilePath, j => j["configuration"]!["rules"]!["population"] = "rail");
                break;
            case "scene-good":
                WorldIdentityTests.Change(fixture, "resources/railway-rules/rail.railway-rule.json", j => j["rail_good"] = "scenery");
                break;
            case "duplicate-rule":
                var rule = RuleParserTests.Combat(); rule["id"] = "rail";
                fixture.AddFile(ContentFixture.BuiltIn, "resources/combat-rules/rail.combat-rule.json", rule.ToJsonString());
                break;
            case "invalid-symbol":
                fixture.AddAsset(ContentFixture.BuiltIn, "resources/settlement-types/farm/symbol.png", [9], new C.AssetMetadata.Png(32, 32));
                break;
            case "wrong-terrain-voxel":
                WorldIdentityTests.Change(fixture, "resources/terrain-types/plains.terrain-type.json", j => j["surface_voxel"] = "rail");
                break;
        }
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        var code = mutation == "duplicate-rule" ? "duplicate_identity" : mutation == "invalid-symbol" ? "asset_rule_failed" : "reference_wrong_kind";
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == code));
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

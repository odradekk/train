using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class LairLoadingTests
{
    [TestMethod]
    [DataRow("unknown", "invalid_field", "/extra")]
    [DataRow("empty-members", "invalid_field", "/members")]
    [DataRow("duplicate-members", "invalid_field", "/members")]
    [DataRow("not-positive", "invalid_field", "/spawn_interval_seconds")]
    [DataRow("schema", "unsupported_format", "/schema_version")]
    [DataRow("missing-maximum", "invalid_field", "/maximum_members")]
    [DataRow("missing-creature", "reference_missing", "/members/0/creature")]
    [DataRow("vehicle-member", "reference_wrong_kind", "/members/0/creature")]
    [DataRow("vehicle-scene", "reference_wrong_kind", "/scene_model")]
    [DataRow("missing-scene", "reference_missing", "/scene_model")]
    public void Lair_definition_failures_keep_their_field(string problem, string code, string field)
    {
        var fixture = DesignFixtures.Content();
        WorldContentFixtures.AddBlueprint(fixture, WorldContentFixtures.Scene());
        WorldContentFixtures.AddBlueprint(fixture, Scout());
        WorldContentFixtures.AddBlueprint(fixture, DesignFixtures.Blueprint("hauler"));
        var lair = Lair();
        switch (problem)
        {
            case "unknown": lair["extra"] = true; break;
            case "empty-members": lair["members"] = new JsonArray(); break;
            case "duplicate-members": lair["members"]!.AsArray().Add(lair["members"]![0]!.DeepClone()); break;
            case "not-positive": lair["spawn_interval_seconds"] = 0; break;
            case "schema": lair["schema_version"] = 2; break;
            case "missing-maximum": lair.Remove("maximum_members"); break;
            case "missing-creature": lair["members"]![0]!["creature"] = "absent"; break;
            case "vehicle-member": lair["members"]![0]!["creature"] = "hauler"; break;
            case "vehicle-scene": lair["scene_model"] = "hauler"; break;
            case "missing-scene": lair["scene_model"] = "absent"; break;
        }
        fixture.AddFile(ContentFixture.BuiltIn, "resources/lair-types/nest.lair-type.json", lair.ToJsonString());
        var failure = WorldContentFixtures.Failure(fixture.Prepare());
        Assert.AreEqual(1, failure.Diagnostics.Length,
            string.Join("; ", failure.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Field)));
        Assert.IsTrue(failure.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Field == field));
    }

    [TestMethod]
    public void Lair_weight_of_another_definition_is_the_wrong_kind()
    {
        var fixture = WorldContentFixtures.Content();
        var profile = WorldContentFixtures.Profile();
        profile["configuration"]!["lair_weights"] = new JsonArray(new JsonObject { ["type"] = "scene", ["weight"] = 1 });
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.ProfilePath, profile.ToJsonString());
        var failure = WorldContentFixtures.Failure(fixture.Prepare());
        Assert.IsTrue(failure.Diagnostics.Any(diagnostic => diagnostic.Code == "reference_wrong_kind" &&
            diagnostic.Field == "/configuration/lair_weights" && diagnostic.Source?.Path == WorldContentFixtures.ProfilePath));
    }

    [TestMethod]
    public void Invalid_lair_excludes_the_mod_and_its_valid_voxel()
    {
        const string mod = "11111111-1111-4111-8111-111111111111";
        var fixture = DesignFixtures.Content();
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json",
            "{\"schema_version\":2,\"id\":\"" + mod + "\",\"name\":\"测试包\",\"version\":\"1.0.0\",\"authors\":[\"作者\"],\"dependencies\":[]}");
        fixture.Installation = fixture.Installation with { EnabledMods = fixture.Installation.EnabledMods.Add(new(mod)) };
        var voxel = JsonNode.Parse(ContentFixture.VoxelJson)!;
        voxel["id"] = "food";
        fixture.AddFile(ContentFixture.Mods, "pack/resources/voxels/food.voxel.json", voxel.ToJsonString());
        fixture.AddAsset(ContentFixture.Mods, "pack/resources/voxels/food/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, "pack/resources/voxels/food/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
        fixture.AddFile(ContentFixture.Mods, "pack/resources/lair-types/nest.lair-type.json", Lair().ToJsonString());
        using var candidate = fixture.Load();
        var status = candidate.Report.Packages.Single(package => package.Metadata?.Id.Value == mod);
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(diagnostic => diagnostic.Code == "reference_missing"));
        Assert.IsFalse(candidate.View.Voxels.Any(entry => entry.Definition.Value == mod + ":food"));
        Assert.AreEqual(0, candidate.View.Core.LairTypes.Length);
    }

    private static JsonObject Scout()
    {
        var creature = DesignFixtures.Blueprint("scout");
        creature["category"] = "creature";
        creature["simulation"] = JsonNode.Parse("""{"behaviors":{"creature":{"hp":8,"speed":1.5,"retreat_threshold":0.25,"raid_duration":12}}}""");
        creature["layout"]!["palette"]![0]!["voxel"] = "scenery";
        return creature;
    }

    private static JsonObject Lair() => JsonNode.Parse("""
        {"schema_version":1,"id":"nest","display":{"name":"巢穴","description":"测试聚落"},
        "generation_weight":2,"members":[{"creature":"scout","weight":1}],"maximum_members":4,
        "spawn_interval_seconds":30,"raid_interval_seconds":60,"roaming_radius_hops":1,"raid_radius_hops":2,
        "rail_damage_work_seconds":15,"clearance_reward":10,"scene_model":"scene"}
        """)!.AsObject();
}

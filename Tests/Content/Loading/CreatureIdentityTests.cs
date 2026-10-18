using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class CreatureIdentityTests
{
    [TestMethod]
    [DataRow("creature-hp", true, false)]
    [DataRow("lair-weight", true, true)]
    [DataRow("unreferenced-lair", true, false)]
    [DataRow("creature-display", false, false)]
    [DataRow("lair-display", false, false)]
    [DataRow("lair-scene", false, false)]
    public void Gameplay_changes_mark_core_input_and_display_changes_mark_presentation(
        string change, bool gameplay, bool generation)
    {
        var fixture = WorldContentFixtures.Content();
        WorldContentFixtures.AddBlueprint(fixture, Creature("scout"));
        var grove = WorldContentFixtures.Scene();
        grove["id"] = "grove";
        grove["display"]!["name"] = "林地";
        WorldContentFixtures.AddBlueprint(fixture, grove);
        fixture.AddFile(ContentFixture.BuiltIn, "resources/lair-types/nest.lair-type.json", Lair("nest", "scene").ToJsonString());
        fixture.AddFile(ContentFixture.BuiltIn, "resources/lair-types/den.lair-type.json", Lair("den", "scene").ToJsonString());
        WorldContentFixtures.Change(fixture, WorldContentFixtures.ProfilePath, json =>
            json["configuration"]!["lair_weights"] = JsonNode.Parse("""[{"type":"nest","weight":1}]"""));
        using var first = fixture.Load();
        switch (change)
        {
            case "creature-hp":
                WorldContentFixtures.Change(fixture, "resources/blueprints/scout.blueprint.json", json =>
                    json["simulation"]!["behaviors"]!["creature"]!["hp"] = 9);
                break;
            case "lair-weight":
                WorldContentFixtures.Change(fixture, "resources/lair-types/nest.lair-type.json", json => json["generation_weight"] = 4);
                break;
            case "unreferenced-lair":
                WorldContentFixtures.Change(fixture, "resources/lair-types/den.lair-type.json", json => json["generation_weight"] = 4);
                break;
            case "creature-display":
                WorldContentFixtures.Change(fixture, "resources/blueprints/scout.blueprint.json", json => json["display"]!["name"] = "新侦察");
                break;
            case "lair-display":
                WorldContentFixtures.Change(fixture, "resources/lair-types/nest.lair-type.json", json => json["display"]!["name"] = "新巢穴");
                break;
            case "lair-scene":
                WorldContentFixtures.Change(fixture, "resources/lair-types/nest.lair-type.json", json => json["scene_model"] = "grove");
                break;
        }
        var references = ContentCaptureTests.References(first.View);
        using var reloaded = ContentCaptureTests.Success(ContentFactory.Create().PrepareLoad(
            new(fixture.Installation, new C.ContentLoadPurpose.Reload(first.View, references)), fixture, fixture));
        var beforeCreature = first.View.Core.Creatures.Single();
        var afterCreature = reloaded.View.Core.Creatures.Single();
        var beforeNest = first.View.Core.LairTypes.Single(lair => lair.Id.Value == "core:nest");
        var afterNest = reloaded.View.Core.LairTypes.Single(lair => lair.Id.Value == "core:nest");
        Assert.AreEqual(change == "creature-hp", beforeCreature.Revision != afterCreature.Revision);
        Assert.AreEqual(change is "lair-weight", beforeNest.Revision != afterNest.Revision);
        Assert.AreEqual(gameplay, first.View.Core.Identity != reloaded.View.Core.Identity);
        Assert.AreEqual(generation, WorldContentFixtures.World(first.View).GenerationContentRevision !=
            WorldContentFixtures.World(reloaded.View).GenerationContentRevision);
        Assert.AreEqual(gameplay, reloaded.Changes.HasFlag(C.ContentChange.CoreInput));
        Assert.AreEqual(!gameplay, reloaded.Changes.HasFlag(C.ContentChange.Presentation));
        if (change == "lair-display")
            Assert.AreEqual("新巢穴", ((C.ContentLookup<C.ContentDisplay>.Found)reloaded.View.FindDefinitionDisplay(
                new Train.Core.Contracts.CoreDefinitionReference.Lair(afterNest.Id))).Value.Text.Name);
    }

    private static JsonObject Creature(string id)
    {
        var creature = JsonNode.Parse("""
            {"schema_version":3,"id":"scout","category":"creature",
            "simulation":{"behaviors":{"creature":{"hp":8,"speed":1.5,"retreat_threshold":0.25,"raid_duration":12}}},
            "layout":{"palette":[{"voxel":"scenery"}],"cells":[[0,0,0,0]]},
            "display":{"name":"侦察","description":"测试生物"}}
            """)!.AsObject();
        creature["id"] = id;
        return creature;
    }

    private static JsonObject Lair(string id, string scene)
    {
        var lair = JsonNode.Parse("""
            {"schema_version":1,"id":"nest","display":{"name":"巢穴","description":"测试聚落"},
            "generation_weight":2,"members":[{"creature":"scout","weight":1}],"maximum_members":4,
            "spawn_interval_seconds":30,"raid_interval_seconds":60,"roaming_radius_hops":1,"raid_radius_hops":2,
            "rail_damage_work_seconds":15,"clearance_reward":10,"scene_model":"scene"}
            """)!.AsObject();
        lair["id"] = id;
        lair["scene_model"] = scene;
        return lair;
    }
}

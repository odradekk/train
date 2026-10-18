using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class CreatureLoadingTests
{
    [TestMethod]
    public void Creature_and_lair_project_current_appearance_without_a_core_design()
    {
        var fixture = DesignFixtures.Content();
        WorldContentFixtures.AddBlueprint(fixture, WorldContentFixtures.Scene());
        AddCreature(fixture, "scout");
        var wolf = Creature("wolf");
        wolf["simulation"]!["behaviors"]!.AsObject().Remove("weapon");
        wolf["layout"]!["cells"] = JsonNode.Parse("[[0,0,0,0],[2,0,0,0]]");
        AddCreature(fixture, wolf);
        fixture.AddFile(ContentFixture.BuiltIn, "resources/lair-types/nest.lair-type.json", Lair().ToJsonString());
        using var candidate = fixture.Load();
        var creatures = candidate.View.Core.Creatures;
        Assert.AreEqual(2, creatures.Length);
        Assert.AreEqual("core:scout", creatures[0].Id.Value);
        Assert.AreEqual(8, creatures[0].HitPoints);
        Assert.AreEqual(1.5, creatures[0].SpeedMetersPerSecond);
        Assert.AreEqual(0.25, creatures[0].RetreatLossRatio);
        Assert.AreEqual(12, creatures[0].RaidDurationSeconds);
        Assert.AreEqual(new K.PartCapability.Weapon(3, 0.5), creatures[0].Weapon);
        Assert.IsNull(creatures[1].Weapon);
        Assert.IsFalse(candidate.View.Core.Designs.Any(design => design.Design == Appearance(candidate.View, "scout").Scene.Design));
        var scene = Appearance(candidate.View, "wolf").Scene;
        Assert.AreEqual(2, scene.Voxels.Length);
        Assert.IsTrue(scene.OccupancyComplete);
        var lair = candidate.View.Core.LairTypes.Single();
        CollectionAssert.AreEqual(new[] { "core:scout", "core:wolf" }, lair.Members.Select(member => member.Creature.Value).ToArray());
        var lairAppearance = (C.ContentLookup<C.LairAppearanceProjection>.Found)candidate.View.FindLair(lair.Id);
        Assert.AreEqual(WorldContentFixtures.Design(candidate.View, "scene"), lairAppearance.Value.SceneModel.Design);
        Assert.IsNull(lairAppearance.Value.Display.Icon);
        Assert.IsNull(lairAppearance.Value.Display.MapSymbol);
        Assert.IsFalse(lairAppearance.Value.Display.Major);
        var display = (C.ContentLookup<C.ContentDisplay>.Found)candidate.View.FindDefinitionDisplay(new K.CoreDefinitionReference.Creature(new("core:scout")));
        Assert.AreEqual("侦察", display.Value.Text.Name);
        Assert.IsNull(display.Value.Icon);
        Assert.IsNull(display.Value.MapSymbol);
        Assert.IsFalse(display.Value.Major);
        var lairDisplay = (C.ContentLookup<C.ContentDisplay>.Found)candidate.View.FindDefinitionDisplay(new K.CoreDefinitionReference.Lair(lair.Id));
        Assert.IsFalse(lairDisplay.Value.Major);
        Assert.IsNull(lairDisplay.Value.Icon);
        Assert.IsNull(lairDisplay.Value.MapSymbol);
    }

    [TestMethod]
    public void Direct_creature_evaluation_keeps_scene_and_leaves_core_empty()
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = new C.DesignDraft(new("侦察", "生物"), new C.DesignSpecification.Creature(new(8, 1.5, 0.25, 12), new(3, 0.5)),
            new([DesignFixtures.Entry("scenery")], [new(new(0, 0, 0), 0)], []));
        var evaluation = DesignFixtures.Evaluate(candidate.View, draft);
        Assert.IsNull(evaluation.Core);
        Assert.IsTrue(evaluation.IsValid);
        Assert.AreEqual(1, evaluation.Scene.Voxels.Length);
        Assert.IsNotNull(evaluation.Scene.Design);
    }

    [TestMethod]
    [DataRow("goods", "design_invalid", "/layout/cells/0")]
    [DataRow("nested", "design_invalid", "/layout/composites")]
    [DataRow("hp", "invalid_field", "/simulation/behaviors/creature/hp")]
    [DataRow("retreat", "invalid_field", "/simulation/behaviors/creature/retreat_threshold")]
    [DataRow("damage", "invalid_field", "/simulation/behaviors/weapon/damage")]
    [DataRow("unknown", "invalid_field", "/simulation/behaviors/creature/extra")]
    [DataRow("missing-behavior", "invalid_field", "/simulation/behaviors/creature")]
    public void Creature_blueprint_rejections_keep_their_field(string problem, string code, string field)
    {
        var fixture = DesignFixtures.Content();
        var creature = Creature("scout");
        switch (problem)
        {
            case "goods": creature["layout"]!["palette"]![0]!["voxel"] = "block"; break;
            case "nested": creature["layout"]!["composites"] = JsonNode.Parse("""[{"at":[0,0,1],"blueprint":"absent"}]"""); break;
            case "hp": creature["simulation"]!["behaviors"]!["creature"]!["hp"] = 0; break;
            case "retreat": creature["simulation"]!["behaviors"]!["creature"]!["retreat_threshold"] = 1.5; break;
            case "damage": creature["simulation"]!["behaviors"]!["weapon"]!["damage"] = 0; break;
            case "unknown": creature["simulation"]!["behaviors"]!["creature"]!["extra"] = 1; break;
            case "missing-behavior": creature["simulation"]!["behaviors"]!.AsObject().Remove("creature"); break;
        }
        AddCreature(fixture, creature);
        var failure = WorldContentFixtures.Failure(fixture.Prepare());
        Assert.IsTrue(failure.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Field == field),
            string.Join("; ", failure.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Field)));
    }

    [TestMethod]
    public void Valid_creature_in_an_invalid_lair_package_stays_out_of_the_view()
    {
        const string mod = "11111111-1111-4111-8111-111111111111";
        var fixture = DesignFixtures.Content();
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json",
            "{\"schema_version\":2,\"id\":\"" + mod + "\",\"name\":\"测试包\",\"version\":\"1.0.0\",\"authors\":[\"作者\"],\"dependencies\":[]}");
        fixture.Installation = fixture.Installation with { EnabledMods = fixture.Installation.EnabledMods.Add(new(mod)) };
        var creature = Creature("beast");
        creature["layout"]!["palette"]![0]!["voxel"] = "core:scenery";
        fixture.AddFile(ContentFixture.Mods, "pack/resources/blueprints/beast.blueprint.json", creature.ToJsonString());
        var lair = Lair();
        lair["extra"] = true;
        fixture.AddFile(ContentFixture.Mods, "pack/resources/lair-types/nest.lair-type.json", lair.ToJsonString());
        using var candidate = fixture.Load();
        var status = candidate.Report.Packages.Single(package => package.Metadata?.Id.Value == mod);
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(diagnostic => diagnostic.Code == "invalid_field"));
        Assert.IsFalse(candidate.View.Core.Creatures.Any(creatureProjection => creatureProjection.Id.Value == mod + ":beast"));
        Assert.IsInstanceOfType<C.ContentLookup<C.CreatureAppearanceProjection>.NotFound>(candidate.View.FindCreature(new(mod + ":beast")));
    }

    private static C.CreatureAppearanceProjection Appearance(C.IContentView view, string id) =>
        ((C.ContentLookup<C.CreatureAppearanceProjection>.Found)view.FindCreature(new("core:" + id))).Value;

    private static void AddCreature(ContentFixture fixture, JsonObject creature) =>
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/" + creature["id"]!.GetValue<string>() + ".blueprint.json", creature.ToJsonString());

    private static void AddCreature(ContentFixture fixture, string id) => AddCreature(fixture, Creature(id));

    private static JsonObject Creature(string id)
    {
        var creature = JsonNode.Parse("""
            {"schema_version":3,"id":"scout","category":"creature",
            "simulation":{"behaviors":{"creature":{"hp":8,"speed":1.5,"retreat_threshold":0.25,"raid_duration":12},"weapon":{"damage":3,"interval":0.5}}},
            "layout":{"palette":[{"voxel":"scenery"}],"cells":[[0,0,0,0]]},
            "display":{"name":"侦察","description":"测试生物"}}
            """)!.AsObject();
        creature["id"] = id;
        return creature;
    }

    private static JsonObject Lair() => JsonNode.Parse("""
        {"schema_version":1,"id":"nest","display":{"name":"巢穴","description":"测试聚落"},
        "generation_weight":2,"members":[{"creature":"wolf","weight":1},{"creature":"scout","weight":3}],"maximum_members":4,
        "spawn_interval_seconds":30,"raid_interval_seconds":60,"roaming_radius_hops":1,"raid_radius_hops":2,
        "rail_damage_work_seconds":15,"clearance_reward":10,"scene_model":"scene"}
        """)!.AsObject();
}

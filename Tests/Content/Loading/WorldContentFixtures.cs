using System.Text.Json.Nodes;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

internal static class WorldContentFixtures
{
    internal const string TemplatePath = "resources/settlement-templates/farm-start.settlement-template.json";
    internal const string ProfilePath = "resources/generation-profiles/world.generation-profile.json";

    internal static ContentFixture Content()
    {
        var fixture = DesignFixtures.Content();
        foreach (var id in new[] { "staple", "clothing", "prepared", "consumer", "electronics", "medical", "rail", "ore" })
            DesignFixtures.AddVoxel(fixture, id);
        AddBlueprint(fixture, Scene());
        AddBlueprint(fixture, Building());
        AddBlueprint(fixture, Building("workshop", "small", "production"));
        AddBlueprint(fixture, DesignFixtures.Blueprint("wagon"));
        var engine = DesignFixtures.Blueprint("engine");
        engine["layout"]!["palette"]![0]!["voxel"] = "machine";
        AddBlueprint(fixture, engine);
        AddType(fixture, "farm");
        AddType(fixture, "unused");
        fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, Template().ToJsonString());
        fixture.AddFile(ContentFixture.BuiltIn, "resources/terrain-types/plains.terrain-type.json", Terrain().ToJsonString());
        AddRule(fixture, RuleParserTests.Population(), "people", "population", "rules");
        AddRule(fixture, RuleParserTests.Market(), "market", "market", "rules");
        AddRule(fixture, RuleParserTests.Construction(), "building", "construction", "rules");
        AddRule(fixture, RuleParserTests.Railway(), "rail", "railway", "railway-rules");
        AddRule(fixture, RuleParserTests.Combat(), "battle", "combat", "combat-rules");
        fixture.AddFile(ContentFixture.BuiltIn, ProfilePath, Profile().ToJsonString());
        return fixture;
    }

    internal static void AddBlueprint(ContentFixture fixture, JsonObject json) =>
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/" + json["id"]!.GetValue<string>() + ".blueprint.json", json.ToJsonString());

    private static void AddRule(ContentFixture fixture, JsonObject json, string id, string kind, string directory)
    {
        json["id"] = id;
        fixture.AddFile(ContentFixture.BuiltIn, $"resources/{directory}/{id}.{kind}-rule.json", json.ToJsonString());
    }

    private static void AddType(ContentFixture fixture, string id)
    {
        fixture.AddFile(ContentFixture.BuiltIn, $"resources/settlement-types/{id}.settlement-type.json", $$$"""
            {"schema_version":3,"id":"{{{id}}}","simulation":{"preferred_terrain":["plains"],"weight":3,"population_min":100,"population_max":200},
            "display":{"name":"聚居地","description":"测试类型","map_symbol":"symbol.png"}}
            """);
        fixture.AddAsset(ContentFixture.BuiltIn, $"resources/settlement-types/{id}/symbol.png", [10, 11, 12], new C.AssetMetadata.Png(24, 24));
    }

    internal static JsonObject Scene()
    {
        var json = DesignFixtures.Blueprint("scene");
        json["category"] = "scene_model";
        json.Remove("simulation");
        json["layout"]!["palette"]![0]!["voxel"] = "scenery";
        json["appearance"] = JsonNode.Parse("""{"flatten":{"from":[0,0],"to":[0,0],"height":0}}""");
        return json;
    }

    internal static JsonObject Building(string id = "station", string size = "station", string kind = "station")
    {
        var json = DesignFixtures.Blueprint(id);
        json["category"] = "building";
        json["simulation"] = new JsonObject { ["plot_size"] = size, ["building_type"] = kind, ["facility_tags"] = new JsonArray() };
        json["layout"]!["palette"]![0]!["voxel"] = "machine";
        return json;
    }

    internal static JsonObject Template() => JsonNode.Parse("""
        {"schema_version":2,"id":"farm-start","settlement_type":"farm","weight":2,"scene_model":"scene",
        "plots":[{"id":7,"size":"station","at":[0,0,0],"building":"station"},{"id":11,"size":"medium","at":[30,0,0],"orientation":"pz_py"}],
        "display":{"name":"农庄","description":"最小模板"}}
        """)!.AsObject();

    internal static JsonObject Terrain() => JsonNode.Parse("""
        {"schema_version":1,"id":"plains","display":{"name":"平原","description":"测试地貌"},
        "surface_voxel":"scenery","subsurface_voxel":"scenery","rock_voxel":"scenery","local_height_noise":[],
        "decorations":[],"decoration_target_per_square_meter":0,
        "railway_models":[{"tracks":1,"bridge":"scene","tunnel_portal":"scene"},{"tracks":2,"bridge":"scene","tunnel_portal":"scene"}]}
        """)!.AsObject();

    internal static JsonObject Profile()
    {
        var json = GenerationProfileParserTests.Fixture();
        json["configuration"]!["terrain_bands"]![0]!["maximum_tracks"] = 2;
        json["configuration"]!["initial_railway"]!["maximum_tracks"] = 2;
        return json;
    }
}

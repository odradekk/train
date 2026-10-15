using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

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
        foreach (var id in new[] { "staple", "prepared" })
            Change(fixture, $"resources/voxels/{id}.voxel.json", json => json["simulation"]!.AsObject().Remove("tags"));
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

    internal static ContentFixture InitializedTemplate(string kind = "production")
    {
        var fixture = Content();
        ProcessLoadingTests.Add(fixture, ProcessLoadingTests.Process(kind));
        Change(fixture, "resources/blueprints/workshop.blueprint.json", json =>
            json["simulation"]!["facility_tags"] = new JsonArray(kind == "extraction" ? "extraction" : "metallurgy"));
        var device = DesignFixtures.Blueprint("device");
        device["category"] = "large_equipment";
        device["simulation"] = JsonNode.Parse("""{"behaviors":{"storage":{"capacity":25}}}""");
        AddBlueprint(fixture, device);
        var frame = DesignFixtures.Blueprint("frame");
        frame["category"] = "large_equipment";
        frame.Remove("simulation");
        AddBlueprint(fixture, frame);
        Change(fixture, TemplatePath, json =>
        {
            json["plots"]![1]!["building"] = "workshop";
            json["founding"] = JsonNode.Parse("""[{"plot":11,"building":"workshop"},{"plot":7,"building":"station"}]""");
            json["inventory"] = JsonNode.Parse("""[{"voxel":"block","quantity":11},{"blueprint":"device","quantity":2},{"blueprint":"frame","quantity":3}]""");
            json["production_plans"] = new JsonArray(new JsonObject
            {
                ["plot"] = 11, ["capability"] = kind == "extraction" ? "extraction" : "production",
                ["process"] = "test", ["enabled"] = false, ["priority"] = -5
            });
        });
        return fixture;
    }

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
        json["appearance"] = JsonNode.Parse("""{"flatten":{"from":[-400,-400],"to":[400,400],"height":0}}""");
        return json;
    }

    internal static JsonObject Building(string id = "station", string size = "small_station", string kind = "station")
    {
        var json = DesignFixtures.Blueprint(id);
        json["category"] = "building";
        json["simulation"] = new JsonObject { ["plot_size"] = size, ["building_type"] = kind, ["facility_tags"] = new JsonArray() };
        json["layout"]!["palette"]![0]!["voxel"] = "machine";
        return json;
    }

    internal static JsonObject Template() => JsonNode.Parse("""
        {"schema_version":2,"id":"farm-start","settlement_type":"farm","weight":2,"scene_model":"scene",
        "plots":[{"id":7,"size":"small_station","at":[0,0,0],"building":"station"},{"id":11,"size":"medium","at":[160,0,0],"orientation":"pz_py"}],
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

    internal static void Change(ContentFixture fixture, string path, Action<JsonObject> change)
    {
        var bytes = ((C.ContentIoResult<System.Collections.Immutable.ImmutableArray<byte>>.Success)
            fixture.Read(new(ContentFixture.BuiltIn, path), 1024 * 1024)).Value;
        var json = JsonNode.Parse(bytes.AsSpan())!.AsObject();
        change(json);
        fixture.AddFile(ContentFixture.BuiltIn, path, json.ToJsonString());
    }

    internal static K.OwnedSettlementTemplateReference Reference(ulong owner) =>
        new(new(owner), new("core:farm-start"), [new(7, K.PlotSize.SmallStation), new(11, K.PlotSize.Medium)]);

    internal static K.DesignHash Design(C.IContentView view, string id) =>
        ((C.DesignEvaluation.Complete)Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation).Design;

    internal static K.WorldGenerationInput World(C.IContentView view, ulong seed = 1, string version = "test")
    {
        var result = ContentFactory.Create().CreateWorldInput(view, new("core:world"), seed, version);
        Assert.IsInstanceOfType<C.ContentResult<K.WorldGenerationInput>.Success>(result, result.ToString());
        return ((C.ContentResult<K.WorldGenerationInput>.Success)result).Value;
    }

    internal static T Found<T>(C.ContentLookup<T> lookup) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentLookup<T>.Found>(lookup, lookup.ToString());
        return ((C.ContentLookup<T>.Found)lookup).Value;
    }

    internal static C.ContentResult<C.IContentCandidate>.Failure Failure(C.ContentResult<C.IContentCandidate> result)
    {
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        return (C.ContentResult<C.IContentCandidate>.Failure)result;
    }
}

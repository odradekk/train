using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

internal static class DesignFixtures
{
    internal static ContentFixture Content()
    {
        var fixture = new ContentFixture();
        AddVoxel(fixture, "block");
        AddVoxel(fixture, "machine", behaviors: """
            {"propulsion":{"traction":25,"rated_speed":20},"control":{},"storage":{"capacity":100},
             "production":{"work_rate":2},"extraction":{"work_rate":3},
             "construction":{"work_rate":4,"max_size_class":"titan"},"power":{"output":5},
             "weapon":{"damage":6,"interval":0.5},"platform":{}}
            """);
        AddVoxel(fixture, "scenery", scene: true);
        return fixture;
    }

    internal static void AddVoxel(ContentFixture fixture, string id, string? behaviors = null, bool scene = false,
        int durability = 5, double mass = 3, int value = 7)
    {
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!.AsObject();
        json["id"] = id;
        if (scene) json.Remove("simulation");
        else
        {
            json["simulation"]!["max_durability"] = durability;
            json["simulation"]!["mass"] = mass;
            json["simulation"]!["value"] = value;
            if (behaviors is not null) json["simulation"]!["behaviors"] = JsonNode.Parse(behaviors);
        }
        json["appearance"]!["presentation"]!["shapes"] = JsonNode.Parse("""["cube","wedge"]""");
        fixture.AddFile(ContentFixture.BuiltIn, $"resources/voxels/{id}.voxel.json", json.ToJsonString());
        fixture.AddAsset(ContentFixture.BuiltIn, $"resources/voxels/{id}/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.BuiltIn, $"resources/voxels/{id}/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
    }

    internal static void AddMeshVoxel(ContentFixture fixture)
    {
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["id"] = "model";
        json["appearance"]!["presentation"] = JsonNode.Parse("""{"kind":"mesh","model":"model.glb","surface_materials":["bare_metal"]}""");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/model.voxel.json", json.ToJsonString());
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/model/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/model/model.glb", [7, 8, 9],
            new C.AssetMetadata.Glb(1, [C.AssetAlphaMode.Opaque], new(new(-0.25, 0, -0.25), new(0.25, 0.5, 0.25)), false, false, false, false, false, false));
    }

    internal static C.VoxelPaletteEntry Entry(string id = "block") => new(new("core:" + id), C.VoxelOrientation.PxPy, null, null);
    internal static C.DesignDraft Vehicle() => new(new("测试", "设计"), new C.DesignSpecification.Vehicle(K.SizeClass.Small),
        new([Entry("machine"), Entry()], [new(new(0, 0, 1), 0), new(new(0, 0, 0), 1), new(new(0, 0, -1), 1)], []));
    internal static C.DesignEvaluation.Complete Evaluate(C.IContentView view, C.DesignDraft draft)
    {
        var result = ContentFactory.Create().EvaluateDesign(view, draft);
        Assert.IsInstanceOfType<C.ContentResult<C.DesignEvaluation>.Success>(result, result.ToString());
        var evaluation = ((C.ContentResult<C.DesignEvaluation>.Success)result).Value;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation, evaluation.ToString());
        return (C.DesignEvaluation.Complete)evaluation;
    }

    internal static JsonObject Blueprint(string id = "test") => JsonNode.Parse($$$"""
        {"schema_version":3,"id":"{{{id}}}","category":"vehicle","simulation":{"size_class":"small"},
         "layout":{"palette":[{"voxel":"block"}],"cells":[[0,0,0,0]]},
         "display":{"name":"测试","description":"设计"}}
        """)!.AsObject();
}

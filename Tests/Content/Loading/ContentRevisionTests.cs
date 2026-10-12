using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentRevisionTests
{
    [TestMethod]
    public void Presentation_changes_keep_business_inputs_and_retained_bytes_unchanged()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        using var first = fixture.Load();
        var original = first.View;
        var good = original.Core.Goods.Single();
        var appearance = Found(original.FindVoxelAppearance(new("core:block")));
        var originalTop = ((C.VoxelPresentation.Cube)appearance.Presentation).Top;

        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["display"]!["name"] = "重涂方块";
        json["appearance"]!["collision"] = new JsonObject { ["kind"] = "none" };
        var presentation = json["appearance"]!["presentation"]!;
        presentation["shapes"] = new JsonArray("wedge", "cube");
        presentation["material"] = "concrete";
        presentation["tile_span"] = 4;
        json["appearance"]!["behaviors"] = new JsonObject { ["wheelset"] = new JsonObject(), ["light"] = new JsonObject() };
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json", json.ToJsonString());
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/tile.png", [8, 9, 10], new C.AssetMetadata.Png(32, 32));
        using var second = fixture.Load();
        var updated = second.View;
        Assert.AreEqual(original.Core.Identity, updated.Core.Identity);
        Assert.AreEqual(good, updated.Core.Goods.Single());
        Assert.AreEqual(1UL, updated.Generation.Value);
        Assert.AreEqual("方块", Found(original.FindVoxelDisplay(new("core:block"))).Display.Name);
        Assert.AreEqual("重涂方块", Found(updated.FindVoxelDisplay(new("core:block"))).Display.Name);
        var changedAppearance = Found(updated.FindVoxelAppearance(new("core:block")));
        Assert.IsInstanceOfType<C.VoxelCollision.None>(changedAppearance.Collision);
        Assert.IsTrue(changedAppearance.Behaviors.Wheelset);
        Assert.AreNotEqual(((C.VoxelPresentation.Cube)appearance.Presentation).Top, ((C.VoxelPresentation.Cube)changedAppearance.Presentation).Top);
        second.Dispose();
        first.Dispose();
        var retainedAppearance = Found(original.FindVoxelAppearance(new("core:block")));
        var retainedTop = ((C.VoxelPresentation.Cube)retainedAppearance.Presentation).Top;
        Assert.AreEqual(originalTop, retainedTop);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Found(original.FindAsset(retainedTop)).Bytes.ToArray());
        Assert.AreEqual(good, original.Voxels.Single().Good);
        Assert.IsInstanceOfType<C.VoxelCollision.FullBox>(retainedAppearance.Collision);
    }

    [TestMethod]
    [DataRow("propulsion", "traction")]
    [DataRow("storage", "capacity")]
    public void Changing_only_a_behavior_parameter_changes_business_identity(string behavior, string parameter)
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["simulation"]!["behaviors"] = JsonNode.Parse("""{"propulsion":{"traction":25,"rated_speed":20},"storage":{"capacity":25}}""");
        fixture.AddCube(json.ToJsonString());
        using var first = fixture.Load();
        var originalGood = first.View.Core.Goods.Single();
        json["simulation"]!["behaviors"]![behavior]![parameter] = 30;
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json", json.ToJsonString());
        using var second = fixture.Load();
        Assert.AreNotEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreNotEqual(originalGood.Revision, second.View.Core.Goods.Single().Revision);
        Assert.AreNotEqual(originalGood, second.View.Core.Goods.Single());
    }

    [TestMethod]
    [DataRow("mass")]
    [DataRow("durability")]
    [DataRow("behavior")]
    [DataRow("orientations")]
    public void Semantic_changes_change_revision_business_identity_and_good(string change)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        using var first = fixture.Load();
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        switch (change)
        {
            case "mass": json["simulation"]!["mass"] = 3; break;
            case "durability": json["simulation"]!["max_durability"] = 200; break;
            case "behavior": json["simulation"]!["behaviors"] = new JsonObject { ["propulsion"] = new JsonObject { ["traction"] = 25, ["rated_speed"] = 20 } }; break;
            case "orientations": json["appearance"]!["allowed_orientations"] = "all_24"; break;
        }
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json", json.ToJsonString());
        using var second = fixture.Load();
        Assert.AreNotEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreNotEqual(first.View.Core.Goods.Single().Revision, second.View.Core.Goods.Single().Revision);
        Assert.AreNotEqual(first.View.Core.Goods.Single(), second.View.Core.Goods.Single());
    }

    [TestMethod]
    public void Json_order_numeric_spelling_and_file_enumeration_do_not_change_identity()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["simulation"]!["behaviors"] = JsonNode.Parse("""{"storage":{"capacity":10},"propulsion":{"traction":25,"rated_speed":20}}""");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json", json.ToJsonString());
        using var first = fixture.Load();
        var reordered = """
            {"display":{"icon":"icon.png","description":"测试结构体素","name":"方块"},
             "appearance":{"presentation":{"faces":{"bottom":"tile.png","side":"tile.png","top":"tile.png"},"kind":"cube"},"allowed_orientations":"four_way"},
             "simulation":{"behaviors":{"propulsion":{"rated_speed":2e1,"traction":25.0},"storage":{"capacity":10}},"tags":["raw_good"],"max_durability":100,"mass":2.0,"value":20},"id":"block","schema_version":3}
            """;
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json", reordered);
        fixture.ReverseEnumeration = true;
        using var second = fixture.Load();
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreEqual(first.View.Core.Goods.Single(), second.View.Core.Goods.Single());
    }

    private static T Found<T>(C.ContentLookup<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentLookup<T>.Found>(result);
        return ((C.ContentLookup<T>.Found)result).Value;
    }
}

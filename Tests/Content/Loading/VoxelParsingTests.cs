using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class VoxelParsingTests
{
    [TestMethod]
    public void Scene_voxel_has_appearance_but_is_not_a_good()
    {
        var json = Document();
        json.Remove("simulation");
        using var candidate = Load(json);
        Assert.AreEqual(1, candidate.View.Voxels.Length);
        Assert.IsNull(candidate.View.Voxels[0].Good);
        Assert.AreEqual(0, candidate.View.Core.Goods.Length);
        Assert.IsInstanceOfType<C.ContentLookup<C.VoxelAppearanceProjection>.Found>(
            candidate.View.FindVoxelAppearance(candidate.View.Voxels[0].Definition));
    }

    [TestMethod]
    public void All_functional_behaviors_accept_their_parameters_and_affect_semantics()
    {
        var json = Document();
        json["simulation"]!["fuel_energy"] = 3.5;
        json["simulation"]!["behaviors"] = JsonNode.Parse("""
            {"propulsion":{"traction":25,"rated_speed":20},"control":{},"storage":{"capacity":100},
             "production":{"work_rate":2},"extraction":{"work_rate":3},
             "construction":{"work_rate":4,"max_size_class":"titan"},"power":{"output":5},
             "weapon":{"damage":6,"interval":0.5},"platform":{}}
            """);
        using var original = Load(json);
        var good = original.View.Core.Goods.Single();
        Assert.AreEqual(20L, good.BaseValue.Value);
        Assert.AreEqual(2d, good.MassTons);
        Assert.IsTrue(good.RawGood);
        Assert.AreEqual(3.5, good.FuelEnergyPerUnit);
        foreach (var behavior in json["simulation"]!["behaviors"]!.AsObject().Select(p => p.Key).ToArray())
        {
            var changed = (JsonObject)json.DeepClone();
            changed["simulation"]!["behaviors"]!.AsObject().Remove(behavior);
            using var removed = Load(changed);
            Assert.AreNotEqual(good.Revision, removed.View.Core.Goods.Single().Revision, behavior);
        }
    }

    [TestMethod]
    public void Cube_and_light_defaults_are_visible_to_callers()
    {
        var json = Document();
        json["appearance"]!.AsObject().Remove("allowed_orientations");
        json["appearance"]!["behaviors"] = JsonNode.Parse("""{"light":{}}""");
        using var candidate = Load(json);
        var appearance = Appearance(candidate);
        Assert.AreEqual(C.AllowedVoxelOrientations.None, appearance.AllowedOrientations);
        Assert.IsInstanceOfType<C.VoxelCollision.FullBox>(appearance.Collision);
        var cube = (C.VoxelPresentation.Cube)appearance.Presentation;
        CollectionAssert.AreEqual(new[] { new C.VoxelShape("cube") }, cube.Shapes.ToArray());
        Assert.AreEqual(C.MaterialCategory.PaintedMetal, cube.Material);
        Assert.AreEqual(1, cube.TileSpan);
        Assert.AreEqual(cube.Side, cube.Front);
        Assert.AreEqual(new C.LightAppearance(new("#ffd9a0"), 4), appearance.Behaviors.Light);
        Assert.IsFalse(appearance.Behaviors.Wheelset);
    }

    [TestMethod]
    public void Explicit_cube_collision_and_visual_behaviors_are_projected()
    {
        var json = Document();
        json["appearance"] = JsonNode.Parse("""
            {"allowed_orientations":"all_24","collision":{"kind":"local_box",
                "offset":{"x":0.25,"y":0,"z":0.25},"size":{"x":0.5,"y":1,"z":0.5}},
             "presentation":{"kind":"cube","shapes":["beam","wedge","corner-wedge","cylinder","half","plate","cube"],
                "material":"concrete","tile_span":8,"faces":{"top":"tile.png","side":"tile.png","bottom":"tile.png","front":"tile.png"}},
             "behaviors":{"wheelset":{},"light":{"color":"#123abc","range":7.5}}}
            """);
        using var candidate = Load(json);
        var appearance = Appearance(candidate);
        Assert.AreEqual(C.AllowedVoxelOrientations.All24, appearance.AllowedOrientations);
        Assert.AreEqual(new C.VoxelCollision.LocalBox(new(0.25, 0, 0.25), new(0.5, 1, 0.5)), appearance.Collision);
        var cube = (C.VoxelPresentation.Cube)appearance.Presentation;
        Assert.AreEqual(7, cube.Shapes.Length);
        Assert.AreEqual(new C.VoxelShape("beam"), cube.Shapes[0]);
        Assert.AreEqual(C.MaterialCategory.Concrete, cube.Material);
        Assert.AreEqual(8, cube.TileSpan);
        Assert.IsTrue(appearance.Behaviors.Wheelset);
        Assert.AreEqual(new C.LightAppearance(new("#123abc"), 7.5), appearance.Behaviors.Light);
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("comment")]
    [DataRow("trailing")]
    public void Non_strict_json_is_rejected(string kind)
    {
        var text = kind switch
        {
            "duplicate" => ContentFixture.VoxelJson.Replace("\"value\":20", "\"value\":20,\"value\":30", StringComparison.Ordinal),
            "comment" => "/* comment */" + ContentFixture.VoxelJson,
            _ => ContentFixture.VoxelJson.Replace("\"icon\":\"icon.png\"}", "\"icon\":\"icon.png\",}", StringComparison.Ordinal)
        };
        AssertRejected(text, "invalid_json");
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("null")]
    [DataRow("value")]
    [DataRow("mass")]
    [DataRow("durability")]
    [DataRow("capacity")]
    [DataRow("nonfinite")]
    [DataRow("collision")]
    [DataRow("shape")]
    [DataRow("material")]
    [DataRow("tile")]
    [DataRow("visual")]
    public void Invalid_domain_fields_are_rejected(string kind)
    {
        var json = Document();
        switch (kind)
        {
            case "unknown": json["appearance"]!["presentation"]!["faces"]!["extra"] = "tile.png"; break;
            case "null": json["appearance"]!["collision"] = null; break;
            case "value": json["simulation"]!["value"] = 1000000001; break;
            case "mass": json["simulation"]!["mass"] = 0; break;
            case "durability": json["simulation"]!["max_durability"] = 0; break;
            case "capacity": json["simulation"]!["behaviors"] = JsonNode.Parse("""{"storage":{"capacity":2147483648}}"""); break;
            case "nonfinite": json["simulation"]!["behaviors"] = JsonNode.Parse("""{"power":{"output":1e400}}"""); break;
            case "collision": json["appearance"]!["collision"] = JsonNode.Parse("""{"kind":"local_box","offset":{"x":0,"y":0,"z":0},"size":{"x":1e-100,"y":1,"z":1}}"""); break;
            case "shape": json["appearance"]!["presentation"]!["shapes"] = JsonNode.Parse("""["cube","cube"]"""); break;
            case "material": json["appearance"]!["presentation"]!["material"] = "unknown"; break;
            case "tile": json["appearance"]!["presentation"]!["tile_span"] = 3; break;
            default: json["appearance"]!["behaviors"] = JsonNode.Parse("""{"wheelset":{"speed":2}}"""); break;
        }
        AssertRejected(json.ToJsonString(), "invalid_field");
    }

    [TestMethod]
    [DataRow("different", null)]
    [DataRow("shared", null)]
    [DataRow("Block", null)]
    [DataRow("block", "../tile.png")]
    [DataRow("block", "shared/sub/tile.png")]
    [DataRow("block", "Tile.png")]
    public void Id_and_asset_paths_cannot_escape_the_definition_folder(string id, string? asset)
    {
        var json = Document();
        json["id"] = id;
        if (asset is not null) json["appearance"]!["presentation"]!["faces"]!["top"] = asset;
        AssertRejected(json.ToJsonString(), "invalid_field");
    }

    [TestMethod]
    [DataRow("value", "/simulation/value", "invalid_field")]
    [DataRow("behavior", "/simulation/behaviors/propulsion/rated_speed", "invalid_field")]
    [DataRow("unknown", "/appearance/presentation/faces/a~0~1b", "invalid_field")]
    [DataRow("tag", "/simulation/tags/1", "invalid_field")]
    [DataRow("shape", "/appearance/presentation/shapes/1", "invalid_field")]
    [DataRow("duplicate", "/appearance/presentation/shapes/1", "invalid_field")]
    [DataRow("surface", "/appearance/presentation/surface_materials/1", "invalid_field")]
    [DataRow("schema", "/schema_version", "unsupported_format")]
    public void Invalid_fields_identify_their_json_pointer(string kind, string pointer, string code)
    {
        var json = Document();
        switch (kind)
        {
            case "value": json["simulation"]!["value"] = 0; break;
            case "behavior": json["simulation"]!["behaviors"] = JsonNode.Parse("""{"propulsion":{"traction":25}}"""); break;
            case "unknown": json["appearance"]!["presentation"]!["faces"]!["a~/b"] = "tile.png"; break;
            case "tag": json["simulation"]!["tags"] = JsonNode.Parse("""["raw_good","unknown"]"""); break;
            case "shape": json["appearance"]!["presentation"]!["shapes"] = JsonNode.Parse("""["cube","unknown"]"""); break;
            case "duplicate": json["appearance"]!["presentation"]!["shapes"] = JsonNode.Parse("""["cube","cube"]"""); break;
            case "surface": json["appearance"]!["presentation"] = JsonNode.Parse("""{"kind":"mesh","model":"model.glb","surface_materials":["concrete","unknown"]}"""); break;
            case "schema": json["schema_version"] = 2; break;
        }
        var fixture = new ContentFixture();
        fixture.AddCube(json.ToJsonString());
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single(d => d.Code == code);
        Assert.AreEqual(pointer, diagnostic.Field);
        Assert.AreEqual(new C.ContentSourceLocation(new("core"), "resources/voxels/block.voxel.json"), diagnostic.Source);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Invalid_utf8_voxel_is_a_package_error(bool mod)
    {
        const string id = "11111111-1111-4111-8111-111111111111";
        var fixture = new ContentFixture();
        fixture.AddCube();
        var bytes = Encoding.UTF8.GetBytes(ContentFixture.VoxelJson.Replace("方块", "invalid-utf8-marker", StringComparison.Ordinal));
        bytes[bytes.AsSpan().IndexOf("invalid-utf8-marker"u8)] = 0xff;
        if (mod)
        {
            fixture.AddFile(ContentFixture.Mods, "pack/mod.json", """
                {"schema_version":2,"id":"11111111-1111-4111-8111-111111111111","name":"包","version":"1.0.0","authors":["作者"],"dependencies":[]}
                """);
            fixture.Installation = fixture.Installation with { EnabledMods = [new(id)] };
            fixture.AddFile(ContentFixture.Mods, "pack/resources/voxels/block.voxel.json", bytes);
            using var candidate = fixture.Load();
            Assert.AreEqual(1, candidate.View.Voxels.Length);
            Assert.AreEqual("core:block", candidate.View.Voxels.Single().Definition.Value);
            var status = candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == id);
            Assert.AreEqual(C.PackageState.Invalid, status.State);
            Assert.IsTrue(status.Diagnostics.Any(d => d.Code == "invalid_json"));
        }
        else
        {
            fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json", bytes);
            var result = fixture.Prepare();
            Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
            Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == "invalid_json"));
        }
    }

    private static JsonObject Document() => JsonNode.Parse(ContentFixture.VoxelJson)!.AsObject();
    private static C.IContentCandidate Load(JsonObject json)
    {
        var fixture = new ContentFixture();
        fixture.AddCube(json.ToJsonString());
        return fixture.Load();
    }
    private static C.VoxelAppearanceProjection Appearance(C.IContentCandidate candidate) =>
        ((C.ContentLookup<C.VoxelAppearanceProjection>.Found)candidate.View.FindVoxelAppearance(candidate.View.Voxels.Single().Definition)).Value;
    private static void AssertRejected(string json, string code)
    {
        var fixture = new ContentFixture();
        fixture.AddCube(json);
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        var failure = (C.ContentResult<C.IContentCandidate>.Failure)result;
        Assert.IsTrue(failure.Diagnostics.Any(d => d.Code == code), string.Join("; ", failure.Diagnostics.Select(d => d.Code + ": " + d.Message)));
    }
}

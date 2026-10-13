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
    [DataRow("{\"kind\":\"none\",\"offset\":{\"x\":0,\"y\":0,\"z\":0}}")]
    [DataRow("{\"kind\":\"full_box\",\"size\":{\"x\":1,\"y\":1,\"z\":1}}")]
    [DataRow("{\"kind\":\"local_box\"}")]
    [DataRow("{\"kind\":\"local_box\",\"offset\":{\"x\":0,\"y\":0,\"z\":0},\"size\":{\"x\":1,\"y\":1}}")]
    [DataRow("{\"kind\":\"local_box\",\"offset\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"size\":{\"x\":1,\"y\":1,\"z\":1}}")]
    public void Collision_fields_follow_the_selected_shape(string collision)
    {
        var json = Document();
        json["appearance"]!["collision"] = JsonNode.Parse(collision);
        AssertRejected(json.ToJsonString(), "invalid_field");
    }

    [TestMethod]
    [DataRow("offset", "x", "-0.01")]
    [DataRow("offset", "y", "1.01")]
    [DataRow("size", "x", "0")]
    [DataRow("size", "y", "-0.1")]
    [DataRow("size", "z", "1.01")]
    [DataRow("size", "z", "0.51")]
    [DataRow("size", "x", "1e400")]
    public void Local_box_rejects_nonfinite_nonpositive_and_out_of_cell_dimensions(string point, string axis, string value)
    {
        var json = Document();
        var collision = JsonNode.Parse("""
            {"kind":"local_box","offset":{"x":0,"y":0,"z":0.5},"size":{"x":0.5,"y":0.5,"z":0.5}}
            """)!;
        collision[point]![axis] = JsonNode.Parse(value);
        json["appearance"]!["collision"] = collision;
        AssertRejected(json.ToJsonString(), "invalid_field");
    }

    [TestMethod]
    [DataRow("x", 1d, 1e-20, false)]
    [DataRow("y", 1d, 1e-20, false)]
    [DataRow("z", 1d, 1e-20, false)]
    [DataRow("x", 1e-20, 1d, false)]
    [DataRow("x", 0.25d, 0.75d, true)]
    [DataRow("x", 0.5d, 1e-20, true)]
    public void Local_box_checks_exact_cell_bounds_without_requiring_distinguishable_endpoints(
        string axis, double offset, double size, bool valid)
    {
        var json = Document();
        var collision = JsonNode.Parse("""
            {"kind":"local_box","offset":{"x":0,"y":0,"z":0},"size":{"x":1,"y":1,"z":1}}
            """)!;
        collision["offset"]![axis] = offset;
        collision["size"]![axis] = size;
        json["appearance"]!["collision"] = collision;
        if (valid)
        {
            using var candidate = Load(json);
            Assert.IsInstanceOfType<C.VoxelCollision.LocalBox>(Appearance(candidate).Collision);
        }
        else
            AssertRejected(json.ToJsonString(), "invalid_field");
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
    [DataRow("different", null, false)]
    [DataRow("shared", null, true)]
    [DataRow("Block", null, true)]
    [DataRow("_block", null, true)]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, true)]
    [DataRow("block", "../tile.png", true)]
    [DataRow("block", "shared/sub/tile.png", true)]
    [DataRow("block", "Tile.png", true)]
    public void Invalid_voxel_ids_and_asset_paths_report_their_field(string id, string? asset, bool matchingFileName)
    {
        var json = Document();
        json["id"] = id;
        if (asset is not null) json["appearance"]!["presentation"]!["faces"]!["top"] = asset;
        var fixture = new ContentFixture();
        fixture.AddCube();
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/voxels/block.voxel.json");
        var path = $"resources/voxels/{(matchingFileName ? id : "block")}.voxel.json";
        fixture.AddFile(ContentFixture.BuiltIn, path, json.ToJsonString());
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single(d => d.Code == "invalid_field");
        Assert.AreEqual(asset is null ? "/id" : "/appearance/presentation/faces/top", diagnostic.Field);
        Assert.AreEqual(new C.ContentSourceLocation(new("core"), path), diagnostic.Source);
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
    [DataRow("name_blank", "/display/name", "invalid_field")]
    [DataRow("name_long", "/display/name", "invalid_field")]
    [DataRow("description_blank", "/display/description", "invalid_field")]
    [DataRow("description_long", "/display/description", "invalid_field")]
    [DataRow("icon_extension", "/display/icon", "invalid_field")]
    [DataRow("face_extension", "/appearance/presentation/faces/top", "invalid_field")]
    [DataRow("model_extension", "/appearance/presentation/model", "invalid_field")]
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
            case "name_blank": json["display"]!["name"] = " \t "; break;
            case "name_long": json["display"]!["name"] = new string('a', 101); break;
            case "description_blank": json["display"]!["description"] = " \t "; break;
            case "description_long": json["display"]!["description"] = new string('a', 2001); break;
            case "icon_extension": json["display"]!["icon"] = "icon.jpg"; break;
            case "face_extension": json["appearance"]!["presentation"]!["faces"]!["top"] = "tile.jpg"; break;
            case "model_extension": json["appearance"]!["presentation"] = JsonNode.Parse("""{"kind":"mesh","model":"model.png","surface_materials":["concrete"]}"""); break;
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

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(100, 2000)]
    public void Display_text_is_trimmed_and_accepts_its_length_boundaries(int nameLength, int descriptionLength)
    {
        var json = Document();
        var name = new string('a', nameLength);
        var description = new string('b', descriptionLength);
        json["display"]!["name"] = " \t" + name + " \r\n";
        json["display"]!["description"] = " \t" + description + " \r\n";
        using var candidate = Load(json);
        var display = ((C.ContentLookup<C.VoxelDisplayProjection>.Found)candidate.View.FindVoxelDisplay(new("core:block"))).Value.Display;
        Assert.AreEqual(name, display.Name);
        Assert.AreEqual(description, display.Description);
    }

    [TestMethod]
    [DataRow(1024 * 1024, true)]
    [DataRow(1024 * 1024 + 1, false)]
    public void Definition_json_has_a_one_MiB_limit(int size, bool accepted)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        const string path = "resources/voxels/block.voxel.json";
        var bytes = new byte[size];
        Array.Fill(bytes, (byte)' ');
        Encoding.UTF8.GetBytes(ContentFixture.VoxelJson).CopyTo(bytes, 0);
        fixture.AddFile(ContentFixture.BuiltIn, path, bytes);
        var result = fixture.Prepare();
        if (accepted)
        {
            Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
            using var candidate = ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
            Assert.AreEqual("core:block", candidate.View.Voxels.Single().Definition.Value);
        }
        else
        {
            Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
            var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single(d => d.Code == "file_too_large");
            Assert.AreEqual(new C.ContentSourceLocation(new("core"), path), diagnostic.Source);
            Assert.AreEqual("", diagnostic.Field);
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

using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;

namespace Train.Engine.Tests.Assets;

internal static class GlbFixture
{
    internal static ImmutableArray<byte> Create(bool transformed = false, string? feature = null,
        byte[]? png = null, bool externalImage = false, bool externalBuffer = false,
        string? invalidAlpha = null, bool invalidMaterial = false, float[]? baseColorFactor = null,
        Action<JsonObject>? changeJson = null)
    {
        var root = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
             "nodes":[{"children":[1]},{"mesh":0}],
             "meshes":[{"primitives":[
               {"attributes":{"POSITION":0},"indices":1},
               {"attributes":{"POSITION":0},"indices":1,"material":0},
               {"attributes":{"POSITION":0},"indices":1,"material":1}]}],
             "materials":[{"alphaMode":"MASK","alphaCutoff":0.5},{"alphaMode":"BLEND"}],
             "buffers":[],"bufferViews":[],"accessors":[]}
            """)!.AsObject();
        using var binary = new MemoryStream();
        using var writer = new BinaryWriter(binary, Encoding.UTF8, leaveOpen: true);
        int View(byte[] bytes)
        {
            while (binary.Length % 4 != 0) writer.Write((byte)0);
            var views = root["bufferViews"]!.AsArray();
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = binary.Length, ["byteLength"] = bytes.Length });
            writer.Write(bytes);
            return views.Count - 1;
        }
        int Accessor(byte[] bytes, int componentType, int count, string type)
        {
            var accessors = root["accessors"]!.AsArray();
            accessors.Add(new JsonObject { ["bufferView"] = View(bytes), ["componentType"] = componentType,
                ["count"] = count, ["type"] = type });
            return accessors.Count - 1;
        }
        Accessor(Floats(-1, 0, 0, 2, 0, 0, 0, 3, 1), 5126, 3, "VEC3");
        root["accessors"]![0]!["min"] = JsonNode.Parse("[-1,0,0]");
        root["accessors"]![0]!["max"] = JsonNode.Parse("[2,3,1]");
        Accessor([0, 0, 1, 0, 2, 0], 5123, 3, "SCALAR");
        if (transformed)
        {
            root["nodes"]![0]!["translation"] = JsonNode.Parse("[10,20,30]");
            root["nodes"]![0]!["rotation"] = new JsonArray(0, 0, Math.Sqrt(0.5), Math.Sqrt(0.5));
            root["nodes"]![0]!["scale"] = JsonNode.Parse("[2,3,4]");
            root["nodes"]![1]!["translation"] = JsonNode.Parse("[1,2,3]");
            root["nodes"]![1]!["scale"] = JsonNode.Parse("[0.5,2,1]");
        }
        var primitives = root["meshes"]![0]!["primitives"]!.AsArray();
        for (var surface = 1; surface < primitives.Count; surface++)
        {
            var z = 2f * surface;
            var position = Accessor(Floats(-1, 0, z, 2, 0, z, 0, 3, z + 1), 5126, 3, "VEC3");
            root["accessors"]![position]!["min"] = new JsonArray(-1, 0, z);
            root["accessors"]![position]!["max"] = new JsonArray(2, 3, z + 1);
            primitives[surface]!["attributes"]!["POSITION"] = position;
        }
        if (feature == "cell")
        {
            var uv = Accessor(Floats(0, 1, 1, 1, 0, 0, 1, 0), 5126, 4, "VEC2");
            var indices = Accessor([0, 0, 1, 0, 2, 0, 1, 0, 3, 0, 2, 0], 5123, 6, "SCALAR");
            for (var surface = 0; surface < primitives.Count; surface++)
            {
                var z = -0.125f + surface * 0.075f;
                var position = Accessor(Floats(-0.25f, 0, z, 0.125f, 0, z,
                    -0.25f, 0.4f, z + 0.05f, 0.125f, 0.4f, z + 0.05f), 5126, 4, "VEC3");
                root["accessors"]![position]!["min"] = new JsonArray(-0.25f, 0, z);
                root["accessors"]![position]!["max"] = new JsonArray(0.125f, 0.4f, z + 0.05f);
                primitives[surface]!["attributes"]!["POSITION"] = position;
                primitives[surface]!["attributes"]!["TEXCOORD_0"] = uv;
                primitives[surface]!["indices"] = indices;
            }
            root["materials"]![1]!["alphaMode"] = "OPAQUE";
        }
        if (feature == "instances")
        {
            root["nodes"]![1]!["translation"] = new JsonArray(1, 2, 3);
            root["nodes"]!.AsArray().Add(new JsonObject { ["mesh"] = 0, ["translation"] = new JsonArray(-4, 5, 6) });
            root["nodes"]![0]!["children"]!.AsArray().Add(2);
        }
        if (feature == "tangent")
        {
            var unit = 1 / MathF.Sqrt(10);
            var normal = Accessor(Floats(0, -unit, 3 * unit, 0, -unit, 3 * unit, 0, -unit, 3 * unit), 5126, 3, "VEC3");
            var tangent = Accessor(Floats(1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1), 5126, 3, "VEC4");
            foreach (var primitive in primitives)
            {
                primitive!["attributes"]!["NORMAL"] = normal;
                primitive["attributes"]!["TANGENT"] = tangent;
            }
        }
        if (feature == "skin")
        {
            root["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "joint" });
            root["nodes"]![0]!["children"]!.AsArray().Add(2);
            root["nodes"]![1]!["skin"] = 0;
            root["skins"] = JsonNode.Parse("[{\"joints\":[2],\"skeleton\":2}]");
            var joints = Accessor(new byte[12], 5121, 3, "VEC4");
            var weights = Accessor(Floats(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0), 5126, 3, "VEC4");
            foreach (var primitive in primitives)
            {
                primitive!["attributes"]!["JOINTS_0"] = joints;
                primitive["attributes"]!["WEIGHTS_0"] = weights;
            }
        }
        if (feature == "morph")
        {
            var delta = Accessor(Floats(0, 0, 0, 0, 0, 0, 0, 0, 0), 5126, 3, "VEC3");
            root["accessors"]![delta]!["min"] = JsonNode.Parse("[0,0,0]");
            root["accessors"]![delta]!["max"] = JsonNode.Parse("[0,0,0]");
            root["meshes"]![0]!["weights"] = JsonNode.Parse("[0]");
            foreach (var primitive in primitives)
                primitive!["targets"] = new JsonArray(new JsonObject { ["POSITION"] = delta });
        }
        if (feature == "animation")
        {
            var times = Accessor(Floats(0, 1), 5126, 2, "SCALAR");
            root["accessors"]![times]!["min"] = JsonNode.Parse("[0]");
            root["accessors"]![times]!["max"] = JsonNode.Parse("[1]");
            var values = Accessor(Floats(0, 0, 0, 0, 0, 0), 5126, 2, "VEC3");
            root["animations"] = new JsonArray(new JsonObject
            {
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = times, ["output"] = values }),
                ["channels"] = JsonNode.Parse("[{\"sampler\":0,\"target\":{\"node\":1,\"path\":\"translation\"}}]")
            });
        }
        if (feature == "camera")
        {
            root["cameras"] = JsonNode.Parse("[{\"type\":\"perspective\",\"perspective\":{\"yfov\":0.7,\"znear\":0.1}}]");
            root["nodes"]![0]!["camera"] = 0;
        }
        if (feature == "light")
        {
            root["extensionsUsed"] = JsonNode.Parse("[\"KHR_lights_punctual\"]");
            root["extensions"] = JsonNode.Parse("{\"KHR_lights_punctual\":{\"lights\":[{\"type\":\"directional\"}]}}");
            root["nodes"]![0]!["extensions"] = JsonNode.Parse("{\"KHR_lights_punctual\":{\"light\":0}}");
        }
        if (png is not null)
        {
            root["images"] = new JsonArray(new JsonObject { ["bufferView"] = View(png), ["mimeType"] = "image/png" });
            root["textures"] = JsonNode.Parse("[{\"source\":0}]");
            root["materials"]![0]!["pbrMetallicRoughness"] = JsonNode.Parse("{\"baseColorTexture\":{\"index\":0}}");
        }
        if (baseColorFactor is not null)
        {
            var material = root["materials"]![0]!.AsObject();
            material["pbrMetallicRoughness"] ??= new JsonObject();
            material["pbrMetallicRoughness"]!["baseColorFactor"] = new JsonArray(baseColorFactor.Select(value => JsonValue.Create(value)).Cast<JsonNode?>().ToArray());
        }
        if (externalImage)
            root["images"] = JsonNode.Parse("[{\"uri\":\"outside.png\"}]");
        if (invalidAlpha is not null) root["materials"]![0]!["alphaMode"] = invalidAlpha;
        if (invalidMaterial) primitives[0]!["material"] = 99;
        root["buffers"]!.AsArray().Add(new JsonObject { ["byteLength"] = binary.Length });
        if (externalBuffer)
            root["buffers"]!.AsArray().Add(new JsonObject { ["uri"] = "outside.bin", ["byteLength"] = 4 });
        changeJson?.Invoke(root);
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        using var glb = new MemoryStream();
        using var chunks = new BinaryWriter(glb);
        var jsonLength = (json.Length + 3) & ~3;
        var binaryLength = ((int)binary.Length + 3) & ~3;
        chunks.Write(0x46546C67u);
        chunks.Write(2u);
        chunks.Write((uint)(12 + 8 + jsonLength + 8 + binaryLength));
        chunks.Write(jsonLength);
        chunks.Write(0x4E4F534Au);
        chunks.Write(json);
        for (var i = json.Length; i < jsonLength; i++) chunks.Write((byte)' ');
        chunks.Write(binaryLength);
        chunks.Write(0x004E4942u);
        chunks.Write(binary.ToArray());
        for (var i = (int)binary.Length; i < binaryLength; i++) chunks.Write((byte)0);
        return ImmutableArray.CreateRange(glb.ToArray());
    }

    private static byte[] Floats(params float[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var value in values) writer.Write(value);
        return stream.ToArray();
    }
}

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Train.App.Assets;
using Train.Content.Contracts;

namespace Train.Cli;

internal sealed partial class AssetInspector
{
    private static AssetInspection InspectGlb(ImmutableArray<byte> bytes)
    {
        using var input = new GlbInput(bytes);
        var root = input.Json.RootElement;
        if (Nonempty(root, "extensionsRequired")) throw Unsupported("命令行检查器不支持必需扩展。");
        var buffers = root.GetProperty("buffers");
        if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _))
            throw Unsupported("只支持单个 GLB BIN buffer。");
        var nodes = root.GetProperty("nodes");
        var count = nodes.GetArrayLength();
        var parents = Enumerable.Repeat(-1, count).ToArray();
        for (var i = 0; i < count; i++)
            if (nodes[i].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                    parents[child.GetInt32()] = i;
        var transforms = nodes.EnumerateArray().Select(Transform).ToArray();
        var surfaces = ImmutableArray.CreateBuilder<AssetAlphaMode>();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var meshCount = 0;
        var hasVertices = false;
        var morph = false;
        if (root.TryGetProperty("meshes", out var meshes))
            for (var mesh = 0; mesh < meshes.GetArrayLength(); mesh++)
                foreach (var primitive in input.Primitives(mesh).EnumerateArray())
                    morph |= Nonempty(primitive, "targets");
        for (var i = 0; i < count; i++)
        {
            var world = transforms[i];
            for (int parent = parents[i], depth = 0; parent >= 0; parent = parents[parent])
            {
                if (++depth >= count) throw new JsonException("GLB 节点层级存在环。");
                world *= transforms[parent];
            }
            if (!nodes[i].TryGetProperty("mesh", out var meshIndex)) continue;
            meshCount++;
            var mesh = meshIndex.GetInt32();
            var primitives = input.Primitives(mesh);
            for (var surface = 0; surface < primitives.GetArrayLength(); surface++)
            {
                var primitive = primitives[surface];
                surfaces.Add(GlbInput.AlphaMode(input.Material(mesh, surface)));
                var accessor = root.GetProperty("accessors")[primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                if (accessor.GetProperty("componentType").GetInt32() != 5126 ||
                    accessor.GetProperty("type").GetString() != "VEC3" || accessor.TryGetProperty("sparse", out _) ||
                    accessor.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean())
                    throw Unsupported("只支持未归一化、非 sparse 的 FLOAT VEC3 POSITION。");
                var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
                var data = input.Binary.Span.Slice(Integer(view, "byteOffset", 0), view.GetProperty("byteLength").GetInt32());
                var stride = Integer(view, "byteStride", 12);
                var offset = Integer(accessor, "byteOffset", 0);
                var vertices = accessor.GetProperty("count").GetInt32();
                for (var vertex = 0; vertex < vertices; vertex++)
                {
                    var position = data.Slice(checked(offset + vertex * stride), 12);
                    var point = Vector3.Transform(new Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(position),
                        BinaryPrimitives.ReadSingleLittleEndian(position[4..]),
                        BinaryPrimitives.ReadSingleLittleEndian(position[8..])), world);
                    minimum = Vector3.Min(minimum, point);
                    maximum = Vector3.Max(maximum, point);
                    hasVertices = true;
                }
            }
        }
        if (!hasVertices) minimum = maximum = Vector3.Zero;
        var light = root.TryGetProperty("extensions", out var rootExtensions) &&
            rootExtensions.TryGetProperty("KHR_lights_punctual", out var lights) && Nonempty(lights, "lights");
        return new AssetInspection.Success(new AssetMetadata.Glb(meshCount, surfaces.ToImmutable(),
            new(new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, maximum.Z)),
            Nonempty(root, "skins"), morph, Nonempty(root, "animations"), Nonempty(root, "cameras"), light,
            input.HasExternalImages));
    }

    private static Matrix4x4 Transform(JsonElement node)
    {
        float[] Values(string name, float[] fallback) => node.TryGetProperty(name, out var value)
            ? value.EnumerateArray().Select(item => item.GetSingle()).ToArray() : fallback;
        if (node.TryGetProperty("matrix", out _))
        {
            var m = Values("matrix", []);
            // glTF 列主序数组对应 System.Numerics 的行向量矩阵。
            return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        var t = Values("translation", [0, 0, 0]);
        var s = Values("scale", [1, 1, 1]);
        var r = Values("rotation", [0, 0, 0, 1]);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) *
            Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3])) *
            Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    private static int Integer(JsonElement value, string name, int fallback) =>
        value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;

    private static bool Nonempty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var array) && array.GetArrayLength() > 0;

    private static AssetDecodeException Unsupported(string diagnostic) =>
        new(AssetInspectionFailure.UnsupportedContent, diagnostic);
}

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
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0")
            throw Unsupported("只支持 glTF 2.0。");
        if (root.TryGetProperty("extensionsRequired", out var required) && required.GetArrayLength() > 0)
            throw Unsupported("命令行检查器不支持必需扩展。");
        foreach (var field in new[] { "bufferViews", "accessors" })
            if (root.TryGetProperty(field, out var values))
                foreach (var value in values.EnumerateArray()) RejectExtensions(value);
        var buffers = root.GetProperty("buffers");
        if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _))
            throw Unsupported("只支持单个 GLB BIN buffer。");
        var bufferLength = buffers[0].GetProperty("byteLength").GetInt32();
        if (bufferLength < 0 || bufferLength > input.Binary.Length || input.Binary.Length - bufferLength > 3)
            throw new JsonException("GLB buffer 长度无效。");
        ReadOnlyMemory<byte> View(int index)
        {
            var view = root.GetProperty("bufferViews")[index];
            RejectExtensions(view);
            if (view.GetProperty("buffer").GetInt32() != 0) throw new JsonException("GLB buffer 索引无效。");
            var offset = Integer(view, "byteOffset", 0);
            var length = view.GetProperty("byteLength").GetInt32();
            if (offset < 0 || length < 0 || offset > bufferLength - length)
                throw new JsonException("GLB bufferView 范围无效。");
            return input.Binary.Slice(offset, length);
        }
        (ReadOnlyMemory<byte> Data, int Count, int Stride, int Offset, int Component, string Type) Accessor(int index)
        {
            var accessor = root.GetProperty("accessors")[index];
            RejectExtensions(accessor);
            if (accessor.TryGetProperty("sparse", out _)) throw Unsupported("不支持 sparse accessor。");
            var component = accessor.GetProperty("componentType").GetInt32();
            var componentSize = component switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                5125 or 5126 => 4,
                _ => throw new JsonException("GLB accessor 分量类型无效。")
            };
            var type = accessor.GetProperty("type").GetString()!;
            var components = type switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4,
                "MAT2" or "MAT3" or "MAT4" => throw Unsupported("不支持几何属性中的 matrix accessor。"),
                _ => throw new JsonException("GLB accessor 类型无效。")
            };
            var viewIndex = accessor.GetProperty("bufferView").GetInt32();
            var data = View(viewIndex);
            var view = root.GetProperty("bufferViews")[viewIndex];
            var size = componentSize * components;
            var stride = Integer(view, "byteStride", size);
            var offset = Integer(accessor, "byteOffset", 0);
            var elements = accessor.GetProperty("count").GetInt32();
            if (stride < size || stride > 252 || stride % componentSize != 0 ||
                view.TryGetProperty("byteStride", out _) && (stride < 4 || stride % 4 != 0) ||
                offset < 0 || offset % componentSize != 0 ||
                ((long)Integer(view, "byteOffset", 0) + offset) % componentSize != 0 || elements <= 0 ||
                offset + (long)(elements - 1) * stride + size > data.Length)
                throw new JsonException("GLB accessor 范围或步长无效。");
            return (data, elements, stride, offset, component, type);
        }
        if (root.TryGetProperty("images", out var images))
            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("uri", out var uri))
                {
                    if (uri.GetString()!.StartsWith("data:", StringComparison.Ordinal))
                        throw Unsupported("不支持 data URI 图片。");
                    continue;
                }
                if (image.GetProperty("mimeType").GetString() != "image/png")
                    throw Unsupported("只支持内嵌 PNG 图片。");
                if (InspectPng(View(image.GetProperty("bufferView").GetInt32()).Span) is AssetInspection.Failure failure)
                    return failure;
            }
        var nodes = root.GetProperty("nodes");
        var count = nodes.GetArrayLength();
        var parents = Enumerable.Repeat(-1, count).ToArray();
        for (var i = 0; i < count; i++)
            if (nodes[i].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                {
                    var index = child.GetInt32();
                    if (index < 0 || index >= count || parents[index] != -1)
                        throw new JsonException("GLB 节点父级无效。");
                    parents[index] = i;
                }
        var transforms = new Matrix4x4[count];
        for (var i = 0; i < count; i++) transforms[i] = Transform(nodes[i]);
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
                morph |= Nonempty(primitive, "targets");
                RejectExtensions(primitive);
                var attributes = primitive.GetProperty("attributes");
                var positionIndex = attributes.GetProperty("POSITION").GetInt32();
                var accessor = root.GetProperty("accessors")[positionIndex];
                if (accessor.GetProperty("componentType").GetInt32() != 5126 ||
                    accessor.GetProperty("type").GetString() != "VEC3" ||
                    accessor.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean())
                    throw Unsupported("只支持非 sparse FLOAT VEC3 POSITION。");
                var positionAccessor = Accessor(positionIndex);
                var data = positionAccessor.Data.Span;
                var stride = positionAccessor.Stride;
                var offset = positionAccessor.Offset;
                var vertices = positionAccessor.Count;
                void ValidateAttributes(JsonElement values)
                {
                    foreach (var attribute in values.EnumerateObject())
                        if (Accessor(attribute.Value.GetInt32()).Count != vertices)
                            throw new JsonException("GLB 属性 accessor 顶点数量不一致。");
                }
                ValidateAttributes(attributes);
                if (primitive.TryGetProperty("targets", out var targets))
                    foreach (var target in targets.EnumerateArray()) ValidateAttributes(target);
                var drawCount = vertices;
                if (primitive.TryGetProperty("indices", out var indices))
                {
                    var indexAccessor = Accessor(indices.GetInt32());
                    var indexJson = root.GetProperty("accessors")[indices.GetInt32()];
                    if (indexAccessor.Type != "SCALAR" || indexAccessor.Component is not (5121 or 5123 or 5125) ||
                        indexJson.TryGetProperty("normalized", out var indexNormalized) && indexNormalized.GetBoolean())
                        throw new JsonException("GLB indices 必须是未归一化无符号整数 SCALAR。");
                    var indexView = root.GetProperty("bufferViews")[indexJson.GetProperty("bufferView").GetInt32()];
                    if (indexView.TryGetProperty("byteStride", out _))
                        throw new JsonException("GLB indices bufferView 不能声明 byteStride。");
                    drawCount = indexAccessor.Count;
                    for (var element = 0; element < drawCount; element++)
                    {
                        var encoded = indexAccessor.Data.Span[checked(indexAccessor.Offset + element * indexAccessor.Stride)..];
                        uint vertexIndex = indexAccessor.Component switch
                        {
                            5121 => encoded[0],
                            5123 => BinaryPrimitives.ReadUInt16LittleEndian(encoded),
                            _ => BinaryPrimitives.ReadUInt32LittleEndian(encoded)
                        };
                        if (vertexIndex >= vertices) throw new JsonException("GLB indices 超出 POSITION 顶点范围。");
                    }
                }
                var mode = Integer(primitive, "mode", 4);
                if (mode < 0 || mode > 6 || mode == 1 && drawCount % 2 != 0 ||
                    mode == 4 && drawCount % 3 != 0 || (mode is 2 or 3) && drawCount < 2 || (mode is 5 or 6) && drawCount < 3)
                    throw new JsonException("GLB primitive 模式或元素数量无效。");
                for (var vertex = 0; vertex < vertices; vertex++)
                {
                    var position = data.Slice(checked(offset + vertex * stride), 12);
                    var point = Vector3.Transform(new Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(position),
                        BinaryPrimitives.ReadSingleLittleEndian(position[4..]),
                        BinaryPrimitives.ReadSingleLittleEndian(position[8..])), world);
                    if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                        throw new JsonException("GLB 顶点或变换不是有限数。");
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
        float[] Values(string name, float[] fallback)
        {
            if (!node.TryGetProperty(name, out var value)) return fallback;
            var result = value.EnumerateArray().Select(item => item.GetSingle()).ToArray();
            if (result.Length != fallback.Length || result.Any(number => !float.IsFinite(number)))
                throw new JsonException("GLB 节点变换无效。");
            return result;
        }
        if (node.TryGetProperty("extensions", out var extensions) && extensions.EnumerateObject().Any(p => p.Name != "KHR_lights_punctual"))
            throw Unsupported("不支持节点扩展。");
        if (node.TryGetProperty("matrix", out _))
        {
            if (node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _))
                throw new JsonException("GLB matrix 与 TRS 不能同时出现。");
            var m = Values("matrix", new float[16]);
            if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
                throw new JsonException("GLB matrix 必须是仿射变换。");
            // glTF 列主序数组对应 System.Numerics 的行向量矩阵。
            return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        var t = Values("translation", [0, 0, 0]);
        var s = Values("scale", [1, 1, 1]);
        var r = Values("rotation", [0, 0, 0, 1]);
        var rotation = new Quaternion(r[0], r[1], r[2], r[3]);
        if (MathF.Abs(rotation.LengthSquared() - 1) > 0.0001f) throw new JsonException("GLB 旋转四元数必须归一化。");
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    private static int Integer(JsonElement value, string name, int fallback) =>
        value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;

    private static void RejectExtensions(JsonElement value)
    {
        if (value.TryGetProperty("extensions", out var extensions) && extensions.EnumerateObject().Any())
            throw Unsupported("不支持几何数据扩展。");
    }

    private static bool Nonempty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var array) && array.GetArrayLength() > 0;

    private static AssetDecodeException Unsupported(string diagnostic) =>
        new(AssetInspectionFailure.UnsupportedContent, diagnostic);
}

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Contracts;

namespace Train.App.Assets;

internal sealed class GlbInput : IDisposable
{
    internal ImmutableArray<byte> Bytes { get; }
    internal JsonDocument Json { get; }
    internal ReadOnlyMemory<byte> Binary { get; }
    internal bool HasExternalImages { get; }

    internal GlbInput(ImmutableArray<byte> bytes)
    {
        var data = bytes.AsSpan();
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546c67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != data.Length)
            throw new AssetDecodeException(AssetInspectionFailure.CannotDecode, "GLB 文件头或长度无效。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 2)
            throw new AssetDecodeException(AssetInspectionFailure.UnsupportedContent, "只支持 GLB 2。");
        ReadOnlySpan<byte> json = default;
        var offset = 12;
        var hasBinary = false;
        var binaryOffset = 0;
        var binaryLength = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8) throw new JsonException("GLB 块头不完整。");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            if (length % 4 != 0 || length > data.Length - offset - 8)
                throw new JsonException("GLB 块长度无效。");
            if (offset == 12)
            {
                if (kind != 0x4e4f534a) throw new JsonException("GLB 首块必须是 JSON。");
                json = data.Slice(offset + 8, (int)length);
            }
            else if (kind == 0x4e4f534a || kind == 0x004e4942 && hasBinary)
                throw new JsonException("GLB 块重复。");
            if (kind == 0x004e4942)
            {
                hasBinary = true;
                binaryOffset = offset + 8;
                binaryLength = (int)length;
            }
            offset += 8 + (int)length;
        }
        Bytes = bytes;
        Binary = bytes.AsMemory().Slice(binaryOffset, binaryLength);
        Json = JsonDocument.Parse(json.ToArray());
        try
        {
            if (Json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("GLB JSON 必须是对象。");
            if (HasExternalUri("buffers"))
                throw new AssetDecodeException(AssetInspectionFailure.UnsupportedContent, "GLB 引用了外部 buffer；不会读取外部文件。");
            HasExternalImages = HasExternalUri("images");
        }
        catch { Json.Dispose(); throw; }
    }

    private bool HasExternalUri(string field)
    {
        if (!Json.RootElement.TryGetProperty(field, out var values)) return false;
        if (values.ValueKind != JsonValueKind.Array) throw new JsonException($"GLB {field} 必须是数组。");
        var external = false;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException($"GLB {field} 项必须是对象。");
            if (!value.TryGetProperty("uri", out var uri)) continue;
            if (uri.ValueKind != JsonValueKind.String) throw new JsonException("GLB URI 必须是字符串。");
            external |= !uri.GetString()!.StartsWith("data:", StringComparison.Ordinal);
        }
        return external;
    }

    internal JsonElement Primitives(int mesh)
    {
        if (!Json.RootElement.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array ||
            mesh < 0 || mesh >= meshes.GetArrayLength() || meshes[mesh].ValueKind != JsonValueKind.Object ||
            !meshes[mesh].TryGetProperty("primitives", out var primitives) || primitives.ValueKind != JsonValueKind.Array)
            throw new JsonException("GLB meshes 和 primitives 必须是数组，mesh 项必须是对象。");
        return primitives;
    }

    internal JsonElement Material(int mesh, int surface)
    {
        var primitives = Primitives(mesh);
        if (surface < 0 || surface >= primitives.GetArrayLength() || primitives[surface].ValueKind != JsonValueKind.Object)
            throw new JsonException("GLB primitive 项必须是对象。");
        var primitive = primitives[surface];
        if (!primitive.TryGetProperty("material", out var index)) return default;
        if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var value) || value < 0 ||
            !Json.RootElement.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array ||
            value >= materials.GetArrayLength() || materials[value].ValueKind != JsonValueKind.Object)
            throw new JsonException("GLB 材质索引无效。");
        return materials[value];
    }

    internal static AssetAlphaMode AlphaMode(JsonElement material)
    {
        if (material.ValueKind == JsonValueKind.Undefined || !material.TryGetProperty("alphaMode", out var mode))
            return AssetAlphaMode.Opaque;
        if (mode.ValueKind != JsonValueKind.String) throw new JsonException("GLB alphaMode 必须是字符串。");
        return mode.GetString() switch
        {
            "OPAQUE" => AssetAlphaMode.Opaque,
            "MASK" => AssetAlphaMode.Mask,
            "BLEND" => AssetAlphaMode.Blend,
            _ => throw new JsonException("GLB alphaMode 无效。")
        };
    }

    public void Dispose() => Json.Dispose();
}

internal sealed class AssetDecodeException(AssetInspectionFailure kind, string message) : Exception(message)
{
    internal AssetInspectionFailure Kind { get; } = kind;
}

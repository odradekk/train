using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.Content.Contracts;

namespace Train.App.Assets;

internal sealed class GodotAssetDecoder : IDisposable
{
    // Godot 4.7.2 的 flag 32 只跳过图片和材质，仍解码几何；外部 buffer 已在 .NET 预检中拒绝。
    private const uint DiscardMaterials = 32;
    private readonly GltfDocument _document = new();
    private readonly GltfState _state = new() { HandleBinaryImageMode = GltfState.HandleBinaryImageModeEnum.EmbedAsUncompressed };
    private readonly HashSet<Texture2D> _transferredImages = new();
    private GltfNode[] _nodes = [];
    private GltfMesh[] _meshes = [];
    private Texture2D[] _images = [];
    internal GlbInput Input { get; }

    internal GodotAssetDecoder(GlbInput input)
    {
        Input = input;
        try
        {
            var error = _document.AppendFromBuffer(input.Bytes.AsSpan(), "", _state,
                input.HasExternalImages ? DiscardMaterials : 0);
            if (error != Error.Ok) throw DecodeFailure($"GLB 解码失败：{error}。");
            using (var nodes = (Godot.Collections.Array)_state.GetNodes()) _nodes = nodes.Select(value => value.As<GltfNode>()).ToArray();
            using (var meshes = (Godot.Collections.Array)_state.GetMeshes()) _meshes = meshes.Select(value => value.As<GltfMesh>()).ToArray();
            using (var images = (Godot.Collections.Array)_state.GetImages()) _images = images.Select(value => value.As<Texture2D>()).ToArray();
            if (!input.HasExternalImages)
            {
                var count = input.Json.RootElement.TryGetProperty("images", out var declared) ? declared.GetArrayLength() : 0;
                if (_images.Length != count) throw DecodeFailure("GLB 内嵌图片数量不匹配。");
                foreach (var texture in _images)
                {
                    if (texture is null) throw DecodeFailure("GLB 内嵌图片无法解码。");
                    using var image = texture.GetImage();
                    if (image is null || image.IsEmpty()) throw DecodeFailure("GLB 内嵌图片为空。");
                }
            }
        }
        catch { Dispose(); throw; }
    }

    internal static Image Png(ImmutableArray<byte> bytes)
    {
        var image = new Image();
        var error = image.LoadPngFromBuffer(bytes.AsSpan());
        if (error == Error.Ok && !image.IsEmpty()) return image;
        image.Dispose();
        throw DecodeFailure($"PNG 解码失败：{error}。");
    }

    internal IEnumerable<(int Mesh, Transform3D Transform)> Instances()
    {
        foreach (var node in _nodes)
        {
            if (node.Mesh < 0) continue;
            var transform = node.Xform;
            var parent = node.Parent;
            for (var depth = 0; parent >= 0; depth++)
            {
                if (depth >= _nodes.Length || parent >= _nodes.Length) throw DecodeFailure("GLB 节点层级无效。");
                transform = _nodes[parent].Xform * transform;
                parent = _nodes[parent].Parent;
            }
            yield return (node.Mesh, transform);
        }
    }

    internal ImporterMesh Mesh(int index) => _meshes[index].Mesh;

    internal AssetMetadata.Glb Metadata()
    {
        var surfaces = ImmutableArray.CreateBuilder<AssetAlphaMode>();
        var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        var meshNodes = 0;
        var hasVertices = false;
        foreach (var (meshIndex, transform) in Instances())
        {
            meshNodes++;
            var mesh = Mesh(meshIndex);
            var primitives = Input.Json.RootElement.GetProperty("meshes")[meshIndex].GetProperty("primitives");
            if (mesh.GetSurfaceCount() != primitives.GetArrayLength()) throw DecodeFailure("GLB primitive 与网格表面数量不匹配。");
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                surfaces.Add(GlbInput.AlphaMode(Input.Material(meshIndex, surface)));
                using var arrays = mesh.GetSurfaceArrays(surface);
                foreach (var vertex in arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array())
                {
                    var point = transform * vertex;
                    minimum = minimum.Min(point);
                    maximum = maximum.Max(point);
                    hasVertices = true;
                }
            }
        }
        if (!hasVertices) minimum = maximum = Vector3.Zero;
        using var skins = (Godot.Collections.Array)_state.GetSkins();
        using var skeletons = (Godot.Collections.Array)_state.GetSkeletons();
        using var animations = (Godot.Collections.Array)_state.GetAnimations();
        using var cameras = (Godot.Collections.Array)_state.GetCameras();
        using var lights = (Godot.Collections.Array)_state.GetLights();
        return new(meshNodes, surfaces.ToImmutable(),
            new(new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, maximum.Z)),
            skins.Count > 0 || skeletons.Count > 0, _meshes.Any(mesh => mesh.Mesh.GetBlendShapeCount() > 0),
            animations.Count > 0, cameras.Count > 0, lights.Count > 0, Input.HasExternalImages);
    }

    internal Texture2D? TakeBaseColorTexture(JsonElement material)
    {
        if (material.ValueKind == JsonValueKind.Undefined ||
            !material.TryGetProperty("pbrMetallicRoughness", out var pbr) ||
            !pbr.TryGetProperty("baseColorTexture", out var texture)) return null;
        using var textures = (Godot.Collections.Array)_state.GetTextures();
        var reference = textures[texture.GetProperty("index").GetInt32()].As<GltfTexture>();
        var result = _images[reference.SrcImage];
        _transferredImages.Add(result);
        return result;
    }

    private static AssetDecodeException DecodeFailure(string diagnostic) => new(AssetInspectionFailure.CannotDecode, diagnostic);

    public void Dispose()
    {
        foreach (var image in _images)
            if (image is not null && !_transferredImages.Contains(image)) image.Dispose();
        foreach (var mesh in _meshes) { mesh.Mesh?.Dispose(); mesh.Dispose(); }
        foreach (var node in _nodes) node.Dispose();
        _state.Dispose();
        _document.Dispose();
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.Content.Contracts;

namespace Train.App.Assets;

internal sealed class AssetLease<T>(T value, Action release) : IDisposable where T : class
{
    private Action? _release = release;
    internal T Value => _release is not null ? value : throw new ObjectDisposedException(nameof(AssetLease<T>));
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed record ModelSurface(Texture2D? BaseColorTexture, Color BaseColorFactor,
    AssetAlphaMode AlphaMode, float AlphaCutoff);

internal sealed class ModelAsset(ArrayMesh mesh, ImmutableArray<ModelSurface> surfaces) : IDisposable
{
    internal ArrayMesh Mesh { get; } = mesh;
    internal ImmutableArray<ModelSurface> Surfaces { get; } = surfaces;

    public void Dispose()
    {
        foreach (var texture in Surfaces.Select(surface => surface.BaseColorTexture).OfType<Texture2D>().Distinct())
            texture.Dispose();
        Mesh.Dispose();
    }
}

internal sealed class AssetResourceCache(GodotMainThreadDispatcher dispatcher) : IDisposable
{
    private enum Use { FaceTexture, Model }
    private sealed class Entry(object value, Action release)
    {
        internal object Value { get; } = value;
        internal Action Release { get; } = release;
        internal int Users { get; set; }
    }

    private readonly Dictionary<(AssetRevision Revision, Use Use), Entry> _resources = new();
    private bool _disposed;

    internal AssetLease<ImageTexture> AcquireFaceTexture(ContentAsset asset) => dispatcher.Invoke(() =>
        Acquire(asset, Use.FaceTexture, () =>
        {
            if (asset.Metadata is not AssetMetadata.Png) throw new ArgumentException("面贴图需要 PNG 资产。", nameof(asset));
            using var image = GodotAssetDecoder.Png(asset.Bytes);
            var error = image.GenerateMipmaps();
            if (error != Error.Ok) throw new InvalidOperationException($"面贴图 mipmap 创建失败：{error}。");
            return ImageTexture.CreateFromImage(image);
        }, texture => texture.Dispose()));

    internal AssetLease<ModelAsset> AcquireModel(ContentAsset asset) => dispatcher.Invoke(() =>
        Acquire(asset, Use.Model, () => CreateModel(asset), model => model.Dispose()));

    private AssetLease<T> Acquire<T>(ContentAsset asset, Use use, Func<T> create, Action<T> release) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = (asset.Revision, use);
        if (!_resources.TryGetValue(key, out var entry))
        {
            var resource = create();
            entry = new(resource, () => release(resource));
            _resources.Add(key, entry);
        }
        entry.Users++;
        return new((T)entry.Value, () => dispatcher.Invoke(() =>
        {
            if (--entry.Users == 0)
            {
                _resources.Remove(key);
                entry.Release();
            }
            return true;
        }));
    }

    private static ModelAsset CreateModel(ContentAsset asset)
    {
        if (asset.Metadata is not AssetMetadata.Glb) throw new ArgumentException("模型需要 GLB 资产。", nameof(asset));
        using var input = new GlbInput(asset.Bytes);
        if (input.HasExternalImages) throw new InvalidOperationException("已校验模型不能含外部图片。");
        using var decoded = new GodotAssetDecoder(input);
        var mesh = new ArrayMesh();
        var surfaces = ImmutableArray.CreateBuilder<ModelSurface>();
        try
        {
            foreach (var (meshIndex, transform) in decoded.Instances())
            {
                var source = decoded.Mesh(meshIndex);
                var normalBasis = transform.Basis.Inverse().Transposed();
                for (var surface = 0; surface < source.GetSurfaceCount(); surface++)
                {
                    using var arrays = source.GetSurfaceArrays(surface);
                    var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
                    for (var i = 0; i < vertices.Length; i++) vertices[i] = (transform * vertices[i]) * 2;
                    arrays[(int)Godot.Mesh.ArrayType.Vertex] = vertices;
                    if (arrays[(int)Godot.Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
                    {
                        var normals = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
                        for (var i = 0; i < normals.Length; i++) normals[i] = (normalBasis * normals[i]).Normalized();
                        arrays[(int)Godot.Mesh.ArrayType.Normal] = normals;
                    }
                    if (arrays[(int)Godot.Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
                    {
                        var tangents = arrays[(int)Godot.Mesh.ArrayType.Tangent].AsFloat32Array();
                        for (var i = 0; i < tangents.Length; i += 4)
                        {
                            var tangent = (transform.Basis * new Vector3(tangents[i], tangents[i + 1], tangents[i + 2])).Normalized();
                            tangents[i] = tangent.X; tangents[i + 1] = tangent.Y; tangents[i + 2] = tangent.Z;
                        }
                        arrays[(int)Godot.Mesh.ArrayType.Tangent] = tangents;
                    }
                    mesh.AddSurfaceFromArrays(source.GetSurfacePrimitiveType(surface), arrays);
                    if (mesh.GetSurfaceCount() != surfaces.Count + 1)
                        throw new InvalidOperationException("模型网格表面创建失败。");
                    var material = input.Material(meshIndex, surface);
                    var color = Colors.White;
                    if (material.ValueKind != JsonValueKind.Undefined && material.TryGetProperty("pbrMetallicRoughness", out var pbr) &&
                        pbr.TryGetProperty("baseColorFactor", out var factor))
                        color = new(factor[0].GetSingle(), factor[1].GetSingle(), factor[2].GetSingle(), factor[3].GetSingle());
                    var alpha = GlbInput.AlphaMode(material);
                    var cutoff = material.ValueKind != JsonValueKind.Undefined && material.TryGetProperty("alphaCutoff", out var value)
                        ? value.GetSingle() : 0.5f;
                    surfaces.Add(new(decoded.TakeBaseColorTexture(material), color, alpha, cutoff));
                }
            }
            return new(mesh, surfaces.ToImmutable());
        }
        catch
        {
            new ModelAsset(mesh, surfaces.ToImmutable()).Dispose();
            throw;
        }
    }

    // 关闭缓存不夺走仍有效的租约；宿主先释放租约，再关闭 dispatcher。
    public void Dispose()
    {
        dispatcher.RequireMainThread();
        _disposed = true;
    }
}

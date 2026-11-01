using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneResources
{
    private readonly record struct ModelMaterialKey(AssetRevision Asset, int Surface,
        MaterialCategory Category, PaintColor? Paint, SurfaceState State);

    private readonly Dictionary<ModelMaterialKey, SceneMaterialLease.Resource> _modelMaterials = [];
    private readonly Dictionary<(SceneMaterial, SurfaceState), SceneMaterialLease.Resource> _voxelMaterials = [];
    private readonly Dictionary<Color, SceneMaterialLease.Resource> _wheelMaterials = [];

    private SceneMaterialLease AcquireMaterial<TKey>(Dictionary<TKey, SceneMaterialLease.Resource> materials,
        TKey key, Func<ShaderMaterial> create) where TKey : notnull
    {
        if (!materials.TryGetValue(key, out var entry))
        {
            var created = new SceneMaterialLease.Resource(create());
            created.RemoveFromCache = () =>
            {
                _cache.RequireMainThread();
                if (materials.TryGetValue(key, out var current) && ReferenceEquals(current, created))
                    materials.Remove(key);
            };
            materials.Add(key, created);
            entry = created;
        }
        return new(entry);
    }

    private SceneMaterialLease AcquireModelMaterial(SceneVisual.Model model, int index, ModelSurface source)
    {
        var key = new ModelMaterialKey(model.Asset, index, model.Materials[index], model.Paint, model.Surface);
        return AcquireMaterial(_modelMaterials, key, () =>
        {
            var material = SurfaceMaterial(key.Category, source.BaseColorFactor, source.BaseColorTexture, key.Paint, _modelShader);
            try
            {
                SetSurface(material, key.State);
                material.SetShaderParameter(_alphaCutout, source.AlphaMode == AssetAlphaMode.Mask);
                material.SetShaderParameter(_alphaCutoff, source.AlphaCutoff);
                return material;
            }
            catch { material.Dispose(); throw; }
        });
    }

    private SceneMaterialLease AcquireWheelMaterial(Color color) => AcquireMaterial(_wheelMaterials, color,
        () => SurfaceMaterial(MaterialCategory.BareMetal, color));

    private ShaderMaterial GeometryMaterial(PreparedVisual owner, SceneVisual.Geometry geometry)
    {
        if (geometry.Mesh.Material is SceneMaterial.Voxel voxel)
        {
            var texture = owner.Own(_cache.AcquireFaceTexture(Asset(voxel.Texture)));
            return owner.Material(AcquireMaterial(_voxelMaterials, (geometry.Mesh.Material, geometry.Surface), () =>
            {
                var material = SurfaceMaterial(voxel.Category, Colors.White, texture.Value, voxel.Paint);
                try
                {
                    material.SetShaderParameter(_voxelPaintMask, true);
                    SetSurface(material, geometry.Surface);
                    return material;
                }
                catch { material.Dispose(); throw; }
            }));
        }
        if (geometry.Mesh.Material is not SceneMaterial.BuiltIn builtIn) throw new ArgumentOutOfRangeException(nameof(geometry));
        // 内建几何数量少，保留每个对象各自持有、直接更新材质的现有行为。
        var preparedMaterial = owner.Material(new SceneMaterialLease(builtIn.Kind switch
        {
            BuiltInMaterial.RailSteel => SurfaceMaterial(MaterialCategory.BareMetal, Color.FromHtml("#5e4434")),
            BuiltInMaterial.Sleeper => SurfaceMaterial(MaterialCategory.Concrete, Color.FromHtml("#9d9a93")),
            BuiltInMaterial.Construction => SurfaceMaterial(MaterialCategory.Concrete, new Color(0.72f, 0.48f, 0.13f)),
            BuiltInMaterial.Water => new ShaderMaterial { Shader = _waterShader },
            _ => throw new ArgumentOutOfRangeException(nameof(geometry))
        }));
        if (builtIn.Kind == BuiltInMaterial.RailSteel)
        {
            preparedMaterial.SetShaderParameter(_railSteel, true);
            preparedMaterial.SetShaderParameter(_railHeadColor, Color.FromHtml("#b9bcbf"));
        }
        SetSurface(preparedMaterial, geometry.Surface);
        return preparedMaterial;
    }
}

// Decision note: 2026-11-01-node-memory-remaining-gap
// 共享材质只在对象需要独立变化时分离；纹理仍由视觉对象自己的资产租约持有。
internal sealed class SceneMaterialLease : IDisposable
{
    internal sealed class Resource(ShaderMaterial material)
    {
        internal ShaderMaterial Material { get; } = material;
        internal int Users { get; set; }
        internal Action? RemoveFromCache { get; set; }

        internal void Uncache()
        {
            RemoveFromCache?.Invoke();
            RemoveFromCache = null;
        }

        internal void Release()
        {
            if (--Users != 0) return;
            Uncache();
            Material.Dispose();
        }
    }

    private Resource? _resource;
    private Resource Current => _resource ?? throw new ObjectDisposedException(nameof(SceneMaterialLease));
    internal ShaderMaterial Value => Current.Material;

    internal SceneMaterialLease(Resource resource)
    {
        _resource = resource;
        resource.Users++;
    }

    internal SceneMaterialLease(ShaderMaterial material) : this(new Resource(material)) { }

    internal ShaderMaterial MakeIndependent()
    {
        var resource = Current;
        if (resource.RemoveFromCache is null) return resource.Material;
        if (resource.Users == 1)
            resource.Uncache();
        else
        {
            var material = (ShaderMaterial)resource.Material.Duplicate();
            resource.Release();
            _resource = new(material) { Users = 1 };
        }
        return Value;
    }

    public void Dispose()
    {
        var resource = _resource;
        _resource = null;
        resource?.Release();
    }
}

using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class PreparedVisual : IDisposable
{
    private readonly AssetResourceCache _cache;
    private readonly List<IDisposable> _owned = [];
    private readonly List<ShaderMaterial> _materials = [];
    private bool _disposed;
    private bool _constructionFogExempt;

    internal PreparedVisual(AssetResourceCache cache, SceneVisual visual)
    {
        _cache = cache;
        Visual = visual;
    }

    internal Node3D Node { get; } = new();
    internal SceneVisual Visual { get; private set; }
    internal void ExemptConstructionFog(bool exempt, Shader normal, Shader followed, Shader normalModel, Shader followedModel)
    {
        if (_constructionFogExempt == exempt) return;
        if (exempt && Visual is SceneVisual.Model model && _materials.Count == 0)
        {
            // 普通模型共享材质；跟随豁免只切换本实例的副本，保留原租约到释放。
            var instance = Node.GetChild<MeshInstance3D>(0);
            for (var index = 0; index < model.Materials.Length; index++)
                instance.SetSurfaceOverrideMaterial(index,
                    Material((ShaderMaterial)instance.GetSurfaceOverrideMaterial(index).Duplicate()));
        }
        _constructionFogExempt = exempt;
        foreach (var material in _materials)
        {
            if (material.Shader == normal || material.Shader == followed)
                material.Shader = exempt ? followed : normal;
            else if (material.Shader == normalModel || material.Shader == followedModel)
                material.Shader = exempt ? followedModel : normalModel;
        }
        foreach (var material in _owned.OfType<StandardMaterial3D>())
        {
            material.StencilMode = exempt ? BaseMaterial3D.StencilModeEnum.Custom : BaseMaterial3D.StencilModeEnum.Disabled;
            material.StencilFlags = (int)BaseMaterial3D.StencilFlagsEnum.Write;
            material.StencilReference = 1;
        }
    }
    internal T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    internal ShaderMaterial Material(ShaderMaterial value) { _materials.Add(value); return Own(value); }

    internal bool CanUpdate(SceneVisual visual) => (Visual, visual) switch
    {
        (SceneVisual.Geometry old, SceneVisual.Geometry next) => old.Mesh.Vertices == next.Mesh.Vertices &&
            old.Mesh.TriangleIndices == next.Mesh.TriangleIndices && ((old.Mesh.Material, next.Mesh.Material) switch
            {
                (SceneMaterial.Voxel left, SceneMaterial.Voxel right) => left.Texture == right.Texture && left.Category == right.Category,
                (SceneMaterial.BuiltIn left, SceneMaterial.BuiltIn right) => left.Kind == right.Kind,
                _ => false
            }),
        (SceneVisual.Model old, SceneVisual.Model next) => old.Asset == next.Asset && old.Paint == next.Paint &&
            old.Surface == next.Surface && old.Materials.SequenceEqual(next.Materials),
        (SceneVisual.Wheelset old, SceneVisual.Wheelset next) => old.GaugeCells == next.GaugeCells &&
            old.RadiusCells == next.RadiusCells && old.WidthCells == next.WidthCells,
        (SceneVisual.Missing old, SceneVisual.Missing next) => old == next,
        _ => false
    };

    // 显示器先准备完整候选，再在整帧接入阶段调用；准备期间不能改变旧帧。
    internal void Update(SceneVisual visual)
    {
        _cache.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanUpdate(visual)) throw new ArgumentException("表现资源身份已改变。", nameof(visual));
        if (Visual == visual) return;
        switch (visual)
        {
            case SceneVisual.Geometry geometry:
                foreach (var material in _materials)
                {
                    SceneResources.SetSurface(material, geometry.Surface);
                    if (geometry.Mesh.Material is SceneMaterial.Voxel voxel) SceneResources.SetPaint(material, voxel.Paint);
                }
                break;
            case SceneVisual.Model model:
                Node.GetChild<MeshInstance3D>(0).SetInstanceShaderParameter("relative_height_plane",
                    new Vector4(model.RelativeHeightPlane.X, model.RelativeHeightPlane.Y,
                        model.RelativeHeightPlane.Z, model.RelativeHeightPlane.W));
                break;
            case SceneVisual.Wheelset wheelset:
                Node.Rotation = new((float)wheelset.RotationRadians, 0, 0);
                break;
        }
        Visual = visual;
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        Node.Free();
        for (var index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
        _owned.Clear();
    }
}

internal sealed class SceneResources : IDisposable
{
    private sealed class GeometryResource(ArrayMesh mesh)
    {
        internal ArrayMesh Mesh { get; } = mesh;
        internal int Users { get; set; }
    }

    private readonly record struct ModelMaterialKey(AssetRevision Asset, int Surface,
        MaterialCategory Category, PaintColor? Paint, SurfaceState State);

    private sealed class ModelMaterialResource(ShaderMaterial material)
    {
        internal ShaderMaterial Material { get; } = material;
        internal int Users { get; set; }
    }

    private readonly AssetResourceCache _cache;
    private readonly IContentView _content;
    private readonly Shader _surfaceShader;
    private readonly Shader _waterShader;
    private readonly Shader _fogShader;
    private readonly Shader _followedShader;
    private readonly Shader _modelShader;
    private readonly Shader _followedModelShader;
    private readonly Texture2D _damageMask;
    private readonly Dictionary<SceneMesh, GeometryResource> _geometry = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModelMaterialKey, ModelMaterialResource> _modelMaterials = [];
    private bool _disposed;

    internal SceneResources(AssetResourceCache cache, IContentView content)
    {
        cache.RequireMainThread();
        _cache = cache;
        _content = content;
        _surfaceShader = ResourceLoader.Load<Shader>("res://Rendering/scene_material.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException("场景材质着色器无法加载。");
        try
        {
            _waterShader = ResourceLoader.Load<Shader>("res://Rendering/water.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("水面着色器无法加载。");
            _fogShader = ResourceLoader.Load<Shader>("res://Rendering/construction_fog.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("构建雾着色器无法加载。");
            _followedShader = ResourceLoader.Load<Shader>("res://Rendering/followed_vehicle.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("跟随车辆着色器无法加载。");
            _modelShader = ResourceLoader.Load<Shader>("res://Rendering/scene_model.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("模型材质着色器无法加载。");
            _followedModelShader = ResourceLoader.Load<Shader>("res://Rendering/followed_model.gdshader", cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("跟随模型着色器无法加载。");
            _damageMask = ResourceLoader.Load<Texture2D>("res://Rendering/Effects/damage-mask.png", cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("损伤遮罩无法加载。");
        }
        catch
        {
            _damageMask?.Dispose(); _followedModelShader?.Dispose(); _modelShader?.Dispose(); _followedShader?.Dispose();
            _fogShader?.Dispose(); _waterShader?.Dispose(); _surfaceShader.Dispose(); throw;
        }
    }

    internal ShaderMaterial ConstructionFogMaterial() => new() { Shader = _fogShader };

    internal void ExemptConstructionFog(PreparedVisual visual, bool exempt) =>
        visual.ExemptConstructionFog(exempt, _surfaceShader, _followedShader, _modelShader, _followedModelShader);

    private bool HasGeometry(SceneMesh mesh) => _geometry.ContainsKey(mesh);

    internal AssetLease<ArrayMesh>? AcquireCachedGeometry(SceneMesh mesh)
    {
        _cache.RequireMainThread();
        return HasGeometry(mesh) ? AcquireGeometry(mesh, null) : null;
    }

    internal PreparedVisual? TryPrepare(SceneVisual visual, SceneMeshPacking? packing)
    {
        _cache.RequireMainThread();
        PackedSceneMesh? data = null;
        if (visual is SceneVisual.Geometry geometry && !HasGeometry(geometry.Mesh) && packing is not null &&
            !packing.TryTake(geometry.Mesh, out data)) return null;
        return Prepare(visual, data);
    }

    internal PreparedVisual Prepare(SceneVisual visual, PackedSceneMesh? data = null)
    {
        _cache.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new PreparedVisual(_cache, visual);
        try
        {
            switch (visual)
            {
                case SceneVisual.Geometry geometry:
                    var mesh = result.Own(AcquireGeometry(geometry.Mesh, data));
                    var material = GeometryMaterial(result, geometry.Mesh.Material);
                    SetSurface(material, geometry.Surface);
                    result.Node.AddChild(new MeshInstance3D { Mesh = mesh.Value, MaterialOverride = material });
                    break;
                case SceneVisual.Model model:
                    var asset = result.Own(_cache.AcquireModel(Asset(model.Asset))).Value;
                    if (asset.Surfaces.Length != model.Materials.Length)
                        throw new InvalidOperationException("模型材质类别与资产表面数量不匹配。");
                    var instance = new MeshInstance3D { Mesh = asset.Mesh };
                    result.Node.AddChild(instance);
                    for (var index = 0; index < asset.Surfaces.Length; index++)
                    {
                        var surface = result.Own(AcquireModelMaterial(model, index, asset.Surfaces[index])).Value;
                        instance.SetSurfaceOverrideMaterial(index, surface);
                    }
                    instance.SetInstanceShaderParameter("relative_height_plane", new Vector4(model.RelativeHeightPlane.X,
                        model.RelativeHeightPlane.Y, model.RelativeHeightPlane.Z, model.RelativeHeightPlane.W));
                    break;
                case SceneVisual.Wheelset wheelset:
                    var wheelMaterial = result.Material(SurfaceMaterial(MaterialCategory.BareMetal, new Color(0.26f, 0.28f, 0.30f)));
                    var wheelMarkerMaterial = result.Material(SurfaceMaterial(MaterialCategory.BareMetal, new Color("#8e9398")));
                    foreach (var side in new[] { -1, 1 })
                    {
                        var cylinder = result.Own(new CylinderMesh
                        {
                            TopRadius = (float)wheelset.RadiusCells,
                            BottomRadius = (float)wheelset.RadiusCells,
                            Height = (float)wheelset.WidthCells,
                            RadialSegments = 24
                        });
                        var wheel = new MeshInstance3D
                        {
                            Mesh = cylinder, MaterialOverride = wheelMaterial,
                            Position = new(side * (float)wheelset.GaugeCells / 2, 0, 0),
                            Rotation = new(0, 0, Mathf.Pi / 2)
                        };
                        result.Node.AddChild(wheel);
                        for (var spoke = 0; spoke < 4; spoke++)
                        {
                            var angle = spoke * Mathf.Pi / 2;
                            wheel.AddChild(new MeshInstance3D
                            {
                                Mesh = result.Own(new BoxMesh { Size = new((float)wheelset.RadiusCells * 0.95f, 0.015f, (float)wheelset.RadiusCells * 0.2f) }),
                                MaterialOverride = wheelMarkerMaterial,
                                Position = new Vector3((float)wheelset.RadiusCells * 0.475f,
                                    -side * ((float)wheelset.WidthCells / 2 + 0.01f), 0).Rotated(Vector3.Up, angle),
                                Rotation = new(0, angle, 0)
                            });
                        }
                    }
                    result.Node.Rotation = new((float)wheelset.RotationRadians, 0, 0);
                    break;
                case SceneVisual.Missing missing:
                    var marker = result.Own(new BoxMesh { Size = Vector3.One * (missing.KnownCell ? 0.85f : 0.45f) });
                    var markerMaterial = result.Own(new StandardMaterial3D
                    {
                        AlbedoColor = Colors.Magenta,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
                    });
                    result.Node.AddChild(new MeshInstance3D
                    {
                        Mesh = marker, MaterialOverride = markerMaterial,
                        Position = missing.KnownCell ? Vector3.One / 2 : Vector3.Zero
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(visual));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private ContentAsset Asset(AssetRevision revision) => _content.FindAsset(revision) is ContentLookup<ContentAsset>.Found found
        ? found.Value : throw new InvalidOperationException($"显示资产未就绪：{revision}。");

    private AssetLease<ShaderMaterial> AcquireModelMaterial(SceneVisual.Model model, int index, ModelSurface source)
    {
        var key = new ModelMaterialKey(model.Asset, index, model.Materials[index], model.Paint, model.Surface);
        if (!_modelMaterials.TryGetValue(key, out var entry))
        {
            var material = SurfaceMaterial(key.Category, source.BaseColorFactor, source.BaseColorTexture, key.Paint, _modelShader);
            try
            {
                SetSurface(material, key.State);
                material.SetShaderParameter("alpha_cutout", source.AlphaMode == AssetAlphaMode.Mask);
                material.SetShaderParameter("alpha_cutoff", source.AlphaCutoff);
            }
            catch { material.Dispose(); throw; }
            entry = new(material);
            _modelMaterials.Add(key, entry);
        }
        entry.Users++;
        return new(entry.Material, () =>
        {
            _cache.RequireMainThread();
            if (--entry.Users != 0) return;
            _modelMaterials.Remove(key);
            entry.Material.Dispose();
        });
    }

    private AssetLease<ArrayMesh> AcquireGeometry(SceneMesh source, PackedSceneMesh? data)
    {
        if (!_geometry.TryGetValue(source, out var entry))
        {
            entry = new(CreateMesh(data ?? PackedSceneMesh.Create(source, CancellationToken.None)));
            _geometry.Add(source, entry);
        }
        entry.Users++;
        return new(entry.Mesh, () =>
        {
            _cache.RequireMainThread();
            if (--entry.Users != 0) return;
            _geometry.Remove(source);
            entry.Mesh.Dispose();
        });
    }

    private static ArrayMesh CreateMesh(PackedSceneMesh source)
    {
        var mesh = new ArrayMesh();
        try
        {
            MeshSurfaces.Add(mesh, Mesh.PrimitiveType.Triangles,
                flags: (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift),
                vertices: source.Positions, normals: source.Normals, textureUvs: source.TextureUvs,
                faceUvs: source.FaceUvs, custom: source.Custom, indices: source.Indices);
            if (mesh.GetSurfaceCount() != 1) throw new InvalidOperationException("场景网格创建失败。");
            return mesh;
        }
        catch { mesh.Dispose(); throw; }
    }

    private ShaderMaterial GeometryMaterial(PreparedVisual owner, SceneMaterial material)
    {
        if (material is SceneMaterial.Voxel voxel)
        {
            var texture = owner.Own(_cache.AcquireFaceTexture(Asset(voxel.Texture)));
            var surface = owner.Material(SurfaceMaterial(voxel.Category, Colors.White, texture.Value, voxel.Paint));
            surface.SetShaderParameter("voxel_paint_mask", true);
            return surface;
        }
        if (material is not SceneMaterial.BuiltIn builtIn) throw new ArgumentOutOfRangeException(nameof(material));
        var preparedMaterial = owner.Material(builtIn.Kind switch
        {
            BuiltInMaterial.RailSteel => SurfaceMaterial(MaterialCategory.BareMetal, Color.FromHtml("#5e4434")),
            BuiltInMaterial.Sleeper => SurfaceMaterial(MaterialCategory.Concrete, Color.FromHtml("#9d9a93")),
            BuiltInMaterial.Construction => SurfaceMaterial(MaterialCategory.Concrete, new Color(0.72f, 0.48f, 0.13f)),
            BuiltInMaterial.Water => new ShaderMaterial { Shader = _waterShader },
            _ => throw new ArgumentOutOfRangeException(nameof(material))
        });
        if (builtIn.Kind == BuiltInMaterial.RailSteel)
        {
            preparedMaterial.SetShaderParameter("rail_steel", true);
            preparedMaterial.SetShaderParameter("rail_head_color", Color.FromHtml("#b9bcbf"));
        }
        return preparedMaterial;
    }

    private ShaderMaterial SurfaceMaterial(MaterialCategory category, Color color, Texture2D? texture = null, PaintColor? paint = null,
        Shader? shader = null)
    {
        var (roughness, metallic, specular) = category switch
        {
            MaterialCategory.PaintedMetal => (0.42f, 0.18f, 0.55f),
            MaterialCategory.BareMetal => (0.28f, 0.90f, 0.65f),
            MaterialCategory.Concrete => (0.90f, 0.0f, 0.30f),
            MaterialCategory.StoneSoil => (0.98f, 0.0f, 0.20f),
            MaterialCategory.Glass => (0.12f, 0.0f, 0.90f),
            MaterialCategory.PolymerRubber => (0.82f, 0.0f, 0.25f),
            MaterialCategory.Organic => (0.75f, 0.0f, 0.35f),
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
        var material = new ShaderMaterial { Shader = shader ?? _surfaceShader };
        try
        {
            material.SetShaderParameter("base_color", color);
            material.SetShaderParameter("has_texture", texture is not null);
            if (texture is not null)
            {
                using var value = Variant.From(texture);
                material.SetShaderParameter("base_texture", value);
            }
            material.SetShaderParameter("material_roughness", roughness);
            material.SetShaderParameter("material_metallic", metallic);
            material.SetShaderParameter("material_specular", specular);
            material.SetShaderParameter("material_category", (int)category);
            using (var value = Variant.From(_damageMask))
                material.SetShaderParameter("damage_mask", value);
            SetPaint(material, paint);
            return material;
        }
        catch { material.Dispose(); throw; }
    }

    internal static void SetPaint(ShaderMaterial material, PaintColor? paint)
    {
        material.SetShaderParameter("has_paint", paint is not null);
        material.SetShaderParameter("paint_color", paint is { } value ? Color.FromHtml(value.Value) : Colors.White);
    }

    internal static void SetSurface(ShaderMaterial material, SurfaceState surface)
    {
        material.SetShaderParameter("wear_enabled", surface.WearEnabled);
        material.SetShaderParameter("wear_seed", new Vector2I(unchecked((int)(uint)surface.WearSeed), unchecked((int)(uint)(surface.WearSeed >> 32))));
        material.SetShaderParameter("surface_damage", surface.Damage);
        material.SetShaderParameter("scorched", surface.Scorched);
    }

    // 所有 PreparedVisual 必须先由显示组释放；共享资产仍由各自租约拥有。
    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        if (_geometry.Count != 0 || _modelMaterials.Count != 0)
            throw new InvalidOperationException("场景显示资源仍在使用。");
        _disposed = true;
        _surfaceShader.Dispose();
        _waterShader.Dispose();
        _fogShader.Dispose();
        _followedShader.Dispose();
        _modelShader.Dispose();
        _followedModelShader.Dispose();
        _damageMask.Dispose();
    }
}

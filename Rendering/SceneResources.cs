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

    internal PreparedVisual(AssetResourceCache cache, SceneVisual visual)
    {
        _cache = cache;
        Visual = visual;
    }

    internal Node3D Node { get; } = new();
    internal SceneVisual Visual { get; private set; }
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
        (SceneVisual.Model old, SceneVisual.Model next) => old.Asset == next.Asset && old.Materials.SequenceEqual(next.Materials),
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
                foreach (var material in _materials)
                {
                    SceneResources.SetSurface(material, model.Surface);
                    SceneResources.SetPaint(material, model.Paint);
                    material.SetShaderParameter("relative_height_plane", new Vector4(model.RelativeHeightPlane.X,
                        model.RelativeHeightPlane.Y, model.RelativeHeightPlane.Z, model.RelativeHeightPlane.W));
                }
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

    private readonly AssetResourceCache _cache;
    private readonly IContentView _content;
    private readonly Shader _surfaceShader;
    private readonly Shader _waterShader;
    private readonly Dictionary<SceneMesh, GeometryResource> _geometry = new(ReferenceEqualityComparer.Instance);
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
        }
        catch { _surfaceShader.Dispose(); throw; }
    }

    internal PreparedVisual Prepare(SceneVisual visual)
    {
        _cache.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new PreparedVisual(_cache, visual);
        try
        {
            switch (visual)
            {
                case SceneVisual.Geometry geometry:
                    var mesh = result.Own(AcquireGeometry(geometry.Mesh));
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
                        var source = asset.Surfaces[index];
                        var surface = result.Material(SurfaceMaterial(model.Materials[index], source.BaseColorFactor,
                            source.BaseColorTexture, model.Paint));
                        SetSurface(surface, model.Surface);
                        surface.SetShaderParameter("model_height", true);
                        surface.SetShaderParameter("relative_height_plane", new Vector4(model.RelativeHeightPlane.X,
                            model.RelativeHeightPlane.Y, model.RelativeHeightPlane.Z, model.RelativeHeightPlane.W));
                        surface.SetShaderParameter("alpha_cutout", source.AlphaMode == AssetAlphaMode.Mask);
                        surface.SetShaderParameter("alpha_cutoff", source.AlphaCutoff);
                        instance.SetSurfaceOverrideMaterial(index, surface);
                    }
                    break;
                case SceneVisual.Wheelset wheelset:
                    var wheelMaterial = result.Material(SurfaceMaterial(MaterialCategory.BareMetal, new Color(0.26f, 0.28f, 0.30f)));
                    foreach (var side in new[] { -1, 1 })
                    {
                        var cylinder = result.Own(new CylinderMesh
                        {
                            TopRadius = (float)wheelset.RadiusCells,
                            BottomRadius = (float)wheelset.RadiusCells,
                            Height = (float)wheelset.WidthCells,
                            RadialSegments = 24
                        });
                        result.Node.AddChild(new MeshInstance3D
                        {
                            Mesh = cylinder, MaterialOverride = wheelMaterial,
                            Position = new(side * (float)wheelset.GaugeCells / 2, 0, 0),
                            Rotation = new(0, 0, Mathf.Pi / 2)
                        });
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

    private AssetLease<ArrayMesh> AcquireGeometry(SceneMesh source)
    {
        if (!_geometry.TryGetValue(source, out var entry))
        {
            entry = new(CreateMesh(source));
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

    private static ArrayMesh CreateMesh(SceneMesh source)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        var positions = new Vector3[source.Vertices.Length];
        var normals = new Vector3[source.Vertices.Length];
        var textureUvs = new Vector2[source.Vertices.Length];
        var faceUvs = new Vector2[source.Vertices.Length];
        var custom = new float[source.Vertices.Length * 4];
        for (var index = 0; index < source.Vertices.Length; index++)
        {
            var vertex = source.Vertices[index];
            positions[index] = new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            normals[index] = new(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
            textureUvs[index] = new(vertex.TextureUv.X, vertex.TextureUv.Y);
            faceUvs[index] = new(vertex.FaceUv.X, vertex.FaceUv.Y);
            custom[index * 4] = (int)vertex.ExposedEdges;
            custom[index * 4 + 1] = vertex.RelativeHeight;
            custom[index * 4 + 2] = vertex.Damage;
        }
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = textureUvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = faceUvs;
        arrays[(int)Mesh.ArrayType.Custom0] = custom;
        var indices = source.TriangleIndices.ToArray();
        // 契约为外侧看 CCW，Godot 为 CW；不反转外向法线。
        for (var index = 0; index < indices.Length; index += 3)
            (indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        try
        {
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays,
                flags: (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
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
            return owner.Material(SurfaceMaterial(voxel.Category, Colors.White, texture.Value, voxel.Paint));
        }
        if (material is not SceneMaterial.BuiltIn builtIn) throw new ArgumentOutOfRangeException(nameof(material));
        return owner.Material(builtIn.Kind switch
        {
            BuiltInMaterial.RailSteel => SurfaceMaterial(MaterialCategory.BareMetal, new Color(0.36f, 0.39f, 0.42f)),
            BuiltInMaterial.Sleeper => SurfaceMaterial(MaterialCategory.Organic, new Color(0.23f, 0.16f, 0.10f)),
            BuiltInMaterial.Construction => SurfaceMaterial(MaterialCategory.Concrete, new Color(0.72f, 0.48f, 0.13f)),
            BuiltInMaterial.Water => new ShaderMaterial { Shader = _waterShader },
            _ => throw new ArgumentOutOfRangeException(nameof(material))
        });
    }

    private ShaderMaterial SurfaceMaterial(MaterialCategory category, Color color, Texture2D? texture = null, PaintColor? paint = null)
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
        var material = new ShaderMaterial { Shader = _surfaceShader };
        try
        {
            material.SetShaderParameter("base_color", color);
            material.SetShaderParameter("has_texture", texture is not null);
            if (texture is not null) material.SetShaderParameter("base_texture", texture);
            material.SetShaderParameter("material_roughness", roughness);
            material.SetShaderParameter("material_metallic", metallic);
            material.SetShaderParameter("material_specular", specular);
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
        if (_geometry.Count != 0) throw new InvalidOperationException("场景显示资源仍在使用。");
        _disposed = true;
        _surfaceShader.Dispose();
        _waterShader.Dispose();
    }
}

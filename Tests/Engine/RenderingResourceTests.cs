using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Text.Json;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Engine.Tests.Assets;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class RenderingResourceTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void Geometry_uploads_outward_faces_and_all_vertex_and_surface_channels()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("channels");
        using var resources = new SceneResources(cache, Content(asset));
        var source = Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#80a0c0")));
        var surface = new SurfaceState(true, 0xfedcba9876543210UL, 0.375f, true);
        using var visual = resources.Prepare(new SceneVisual.Geometry(source, surface));
        var instance = Instance(visual);
        var mesh = (ArrayMesh)instance.Mesh;
        using var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var faceUv = arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        var custom = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var customFormat = ((ulong)mesh.SurfaceGetFormat(0) >> (int)Mesh.ArrayFormat.FormatCustom0Shift)
            & (ulong)Mesh.ArrayFormat.FormatCustomMask;
        Require(customFormat == (ulong)Mesh.ArrayCustomFormat.RgbaFloat, "表现通道需要完整 float 精度。");
        var outward = (vertices[indices[2]] - vertices[indices[0]]).Cross(vertices[indices[1]] - vertices[indices[0]]).Normalized();
        Require(outward.Dot(Vector3.Back) > 0.999f, "正面绕序没有保持契约的外侧。");
        for (var index = 0; index < source.Vertices.Length; index++)
        {
            var expected = source.Vertices[index];
            Require(vertices[index].IsEqualApprox(new Vector3(expected.Position.X, expected.Position.Y, expected.Position.Z)), "格坐标被缩放或反射。");
            Require(normals[index].Dot(outward) > 0.999f, "上传法线不朝外。");
            Require(uv[index].IsEqualApprox(new Vector2(expected.TextureUv.X, expected.TextureUv.Y)), "TextureUv 丢失。");
            Require(faceUv[index].IsEqualApprox(new Vector2(expected.FaceUv.X, expected.FaceUv.Y)), "FaceUv 丢失。");
            Near(custom[index * 4], (int)expected.ExposedEdges);
            Near(custom[index * 4 + 1], expected.RelativeHeight);
            Near(custom[index * 4 + 2], expected.Damage);
        }
        var material = Material(visual);
        AssertSurface(material, surface);
        Require(material.GetShaderParameter("has_paint").AsBool(), "几何涂装开关未上传。");
        Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#80a0c0")), "几何涂装颜色未上传。");
        var next = new SceneVisual.Geometry(source with
        {
            Material = new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#102030"))
        }, new(false, ulong.MaxValue, 0.75f, false));
        Require(visual.CanUpdate(next), "只改表面状态和涂装不应重建几何。");
        visual.Update(next);
        AssertSurface(material, next.Surface);
        Require(ReferenceEquals(instance.Mesh, mesh), "更新表面状态重建了网格。");
        Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#102030")), "更新没有替换涂装颜色。");
    }

    [Test]
    public void Model_overrides_preserve_surface_order_cutout_texture_and_height_plane()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        var bytes = GlbFixture.Create(png: image.SavePngToBuffer(), baseColorFactor: [0.25f, 0.5f, 0.75f, 0.8f],
            changeJson: json => json["materials"]![1]!["pbrMetallicRoughness"] =
                json["materials"]![0]!["pbrMetallicRoughness"]!.DeepClone());
        var inspection = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("render-model"), bytes);
        Require(inspection is AssetInspection.Success, "模型夹具无法解码。");
        var asset = new ContentAsset(new("render-model"), bytes, ((AssetInspection.Success)inspection).Metadata, []);
        using var resources = new SceneResources(cache, Content(asset));
        var source = new SceneVisual.Model(asset.Revision,
            [MaterialCategory.Concrete, MaterialCategory.PaintedMetal, MaterialCategory.Glass],
            new("#abcdef"), new(true, 0x8000000100000001UL, 0.25f, true), new(0.1f, 0.2f, 0.3f, 0.4f));
        using var first = resources.Prepare(source);
        using var second = resources.Prepare(source with { RelativeHeightPlane = new(0, 1, 0, -1) });
        var instance = Instance(first);
        var mesh = instance.Mesh;
        Require(mesh.GetInstanceId() == Instance(second).Mesh.GetInstanceId(), "显示实例绕过了模型缓存。");
        for (var index = 0; index < 3; index++)
        {
            var material = (ShaderMaterial)instance.GetSurfaceOverrideMaterial(index);
            AssertSurface(material, source.Surface);
            Require(material.GetShaderParameter("model_height").AsBool(), "模型高度平面没有启用。");
            Require(instance.GetInstanceShaderParameter("relative_height_plane").AsVector4().IsEqualApprox(new Vector4(0.1f, 0.2f, 0.3f, 0.4f)), "模型相对高度平面丢失。");
            Require(material.GetInstanceId() == Instance(second).GetSurfaceOverrideMaterial(index).GetInstanceId(),
                "不同高度平面的实例没有共享相同表面材质。");
            Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#abcdef")), "模型涂装丢失。");
            Require(material.GetShaderParameter("alpha_cutout").AsBool() == (index == 1), "模型 alpha mask 与表面次序不对应。");
        }
        var mask = (ShaderMaterial)instance.GetSurfaceOverrideMaterial(1);
        Near(mask.GetShaderParameter("alpha_cutoff").AsSingle(), 0.5f);
        Require(mask.GetShaderParameter("base_color").AsColor().IsEqualApprox(new Color(0.25f, 0.5f, 0.75f, 0.8f)), "模型基础色系数丢失。");
        var texture = mask.GetShaderParameter("base_texture").As<Texture2D>();
        using (var mipmapped = texture.GetImage()) Require(mipmapped.HasMipmaps(), "模型内嵌纹理没有 mipmap。");
        Require(texture.GetInstanceId() == ((ShaderMaterial)instance.GetSurfaceOverrideMaterial(2)).GetShaderParameter("base_texture").As<Texture2D>().GetInstanceId(), "同模型内嵌图片没有共享。");
        Require(!first.CanUpdate(source with { Paint = null }) &&
            !first.CanUpdate(source with { Surface = new(false, ulong.MaxValue, 0.5f, false) }),
            "涂装或表面状态变化必须重新准备共享材质。");
        var next = source with { RelativeHeightPlane = new(0, 1, 0, -2) };
        Require(first.CanUpdate(next), "高度平面变化不应重建网格或材质。");
        first.Update(next);
        Require(instance.GetInstanceShaderParameter("relative_height_plane").AsVector4().IsEqualApprox(new Vector4(0, 1, 0, -2)), "模型高度平面没有更新。");
        Require(Instance(second).GetInstanceShaderParameter("relative_height_plane").AsVector4().IsEqualApprox(new Vector4(0, 1, 0, -1)),
            "更新实例高度影响了另一实例。");
        first.Dispose();
        Require(GodotObject.IsInstanceValid(mesh) && GodotObject.IsInstanceValid(texture) && GodotObject.IsInstanceValid(mask),
            "释放一个模型实例销毁了另一实例的资源。");
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(texture) && !GodotObject.IsInstanceValid(mask),
            "最后模型显示实例没有释放共享材质、模型网格和内嵌纹理。");
    }

    [Test]
    public void Display_leases_share_mesh_and_mipmapped_face_texture_until_last_visual()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("display-shared");
        using var resources = new SceneResources(cache, Content(asset));
        var source = new SceneVisual.Geometry(Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.Organic, null)), new(false, 0, 0, false));
        using var first = resources.Prepare(source);
        using var second = resources.Prepare(source);
        var mesh = Instance(first).Mesh;
        var texture = Material(first).GetShaderParameter("base_texture").As<ImageTexture>();
        Require(mesh.GetInstanceId() == Instance(second).Mesh.GetInstanceId(), "同不可变几何没有共享 ArrayMesh。");
        Require(texture.GetInstanceId() == Material(second).GetShaderParameter("base_texture").As<ImageTexture>().GetInstanceId(), "显示实例没有共享面纹理。");
        using (var image = texture.GetImage()) Require(image.HasMipmaps(), "显示器使用了无 mipmap 的面纹理。");
        first.Node.Position = new(100, 200, -300);
        Require(first.CanUpdate(source), "对象移动破坏了几何复用。");
        first.Update(source with { Surface = new(true, 17, 0.1f, false) });
        Require(mesh.GetInstanceId() == Instance(first).Mesh.GetInstanceId(), "平移或状态更新重建了 ArrayMesh。");
        first.Dispose();
        Require(GodotObject.IsInstanceValid(mesh) && GodotObject.IsInstanceValid(texture), "共享资源在最后使用者结束前被释放。");
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(texture), "最后显示租约没有释放共享资源。");
    }

    [Test]
    public void Missing_markers_prepare_known_cells_and_unknown_positions_and_release_owned_resources()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        var reference = new ContentReference.Design(new("missing-design"));
        foreach (var knownCell in new[] { true, false })
        {
            var source = new SceneVisual.Missing(reference, knownCell);
            using var visual = resources.Prepare(source);
            var node = visual.Node;
            var instance = Instance(visual);
            var mesh = (BoxMesh)instance.Mesh;
            var material = (StandardMaterial3D)instance.MaterialOverride;
            Require(node.GetParent() is null, "缺失标记在准备时不得接入显示树。");
            Require(instance.Position.IsEqualApprox(knownCell ? Vector3.One / 2 : Vector3.Zero),
                "已知格标记必须位于格中心，未知范围标记必须位于放置点。");
            Require(mesh.Size.X > 0 && mesh.Size.X <= 1 && mesh.Size.IsEqualApprox(Vector3.One * mesh.Size.X),
                "缺失标记必须是格尺度内的立方体。");
            Require(material.AlbedoColor.IsEqualApprox(Colors.Magenta) &&
                material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded,
                "缺失标记必须使用可识别的不受光照影响的洋红色。");
            Require(visual.CanUpdate(source) && !visual.CanUpdate(source with { KnownCell = !knownCell }),
                "缺失标记类型改变需要重新准备。");
            visual.Dispose();
            Require(!GodotObject.IsInstanceValid(node) && !GodotObject.IsInstanceValid(instance) &&
                !GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(material),
                "缺失标记的节点、网格及材质必须一起释放。");
        }
    }

    [Test]
    public void Wheelset_uses_scene_dimensions_local_x_axis_and_supplied_rotation()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        var source = new SceneVisual.Wheelset(7, 1.25, 0.375, 0.7);
        using var visual = resources.Prepare(source);
        var wheels = visual.Node.GetChildren().OfType<MeshInstance3D>().OrderBy(node => node.Position.X).ToArray();
        Require(wheels.Length == 2, "轮对需要两侧车轮。");
        for (var index = 0; index < wheels.Length; index++)
        {
            var cylinder = (CylinderMesh)wheels[index].Mesh;
            Near(cylinder.TopRadius, source.RadiusCells);
            Near(cylinder.BottomRadius, source.RadiusCells);
            Near(cylinder.Height, source.WidthCells);
            Near(wheels[index].Position.X, (index == 0 ? -1 : 1) * source.GaugeCells / 2);
            Require(Math.Abs(wheels[index].Basis.Y.Dot(Vector3.Right)) > 0.999f, "车轮轴不沿对象局部 X。");
        }
        Near(visual.Node.Rotation.X, source.RotationRadians);
        var original = wheels.Select(wheel => wheel.Mesh.GetInstanceId()).ToArray();
        visual.Update(source with { RotationRadians = -0.3 });
        Near(visual.Node.Rotation.X, -0.3);
        Require(wheels.Select(wheel => wheel.Mesh.GetInstanceId()).SequenceEqual(original), "轮对转动重建了网格。");
        Require(!visual.CanUpdate(source with { WidthCells = 0.5 }), "轮对尺寸变化必须重新准备资源。");
    }

    [Test]
    public async Task Material_categories_and_builtins_have_sampling_contract_and_render_without_shader_errors()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("categories");
        using var output = new GodotOutputCapture();
        SceneResources? resources = null;
        var visuals = new List<PreparedVisual>();
        var viewport = new SubViewport { Size = new(128, 128), OwnWorld3D = true, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        OS.AddLogger(output);
        try
        {
            resources = new SceneResources(cache, Content(asset));
            TestScene.AddChild(viewport);
            var responses = new HashSet<(float Roughness, float Metallic, float Specular)>();
            foreach (var category in Enum.GetValues<MaterialCategory>())
            {
                var visual = resources.Prepare(new SceneVisual.Geometry(Triangle(new SceneMaterial.Voxel(asset.Revision, category, null)), new(false, 0, 0, false)));
                visuals.Add(visual);
                var material = Material(visual);
                responses.Add((material.GetShaderParameter("material_roughness").AsSingle(),
                    material.GetShaderParameter("material_metallic").AsSingle(), material.GetShaderParameter("material_specular").AsSingle()));
                var sampler = Regex.Match(material.Shader.Code, @"uniform\s+sampler2D\s+base_texture\s*:\s*([^;]+)");
                Require(sampler.Success && new[] { "source_color", "filter_linear_mipmap_anisotropic", "repeat_enable" }.All(sampler.Groups[1].Value.Contains), "基础贴图采样约定丢失。");
                Require(!Regex.IsMatch(material.Shader.Code, @"\bALPHA\s*="), "Demo 材质包含半透明输出。");
            }
            Require(responses.Count == Enum.GetValues<MaterialCategory>().Length, "材质类别没有独立光照响应。");
            var builtinMaterials = new Dictionary<BuiltInMaterial, ShaderMaterial>();
            foreach (var builtin in Enum.GetValues<BuiltInMaterial>())
            {
                var visual = resources.Prepare(new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(builtin)), new(false, 0, 0, false)));
                visuals.Add(visual);
                builtinMaterials.Add(builtin, Material(visual));
            }
            Require(builtinMaterials.Where(pair => pair.Key != BuiltInMaterial.Water)
                .Select(pair => pair.Value.GetShaderParameter("base_color").AsColor()).Distinct().Count() == 3,
                "钢轨、轨枕和施工没有独立的固有色。");
            Require(builtinMaterials[BuiltInMaterial.Water].Shader.GetInstanceId() != builtinMaterials[BuiltInMaterial.RailSteel].Shader.GetInstanceId(),
                "水面没有使用自己的着色器。");
            for (var index = 0; index < visuals.Count; index++)
            {
                visuals[index].Node.Position = new(index % 4 * 1.2f - 2.4f, index / 4 * 1.2f - 1.2f, 0);
                viewport.AddChild(visuals[index].Node);
            }
            var camera = new Camera3D { Position = new(0, 0, 8), Current = true };
            viewport.AddChild(camera);
            viewport.AddChild(new DirectionalLight3D { Rotation = new(-0.3f, -0.3f, 0) });
            var tree = TestScene.GetTree();
            for (var frame = 0; frame < 4; frame++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            // headless 使用 dummy 渲染器；真实窗口运行同一测试时才断言实际出图。
            if (DisplayServer.GetName() != "headless")
            {
                using var rendered = viewport.GetTexture().GetImage();
                Require(!rendered.IsEmpty(), "SubViewport 没有渲染输出。");
                Require(Enumerable.Range(0, rendered.GetWidth() * rendered.GetHeight()).Any(index =>
                    rendered.GetPixel(index % rendered.GetWidth(), index / rendered.GetWidth()).A > 0.5f), "材质场景没有绘制几何。");
            }
            Require(!output.Contains("Shader compilation failed") && !output.Contains("SHADER ERROR") && !output.Contains("Shader error"), "场景材质发生着色器编译错误。");
        }
        finally
        {
            foreach (var visual in visuals) visual.Dispose();
            resources?.Dispose();
            viewport.Free();
            OS.RemoveLogger(output);
        }
    }

    [Test]
    public async Task Model_instancing_records_completed_frame_draw_calls()
    {
        if (DisplayServer.GetName() == "headless") return;
        Require(RenderingServer.GetCurrentRenderingMethod() == "forward_plus", "模型绘制测量要求 Forward+。");
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var bytes = GlbFixture.Create(feature: "cell");
        var inspection = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("instancing-model"), bytes);
        Require(inspection is AssetInspection.Success, "批处理模型无法解码。");
        var asset = new ContentAsset(new("instancing-model"), bytes, ((AssetInspection.Success)inspection).Metadata, []);
        using var resources = new SceneResources(cache, Content(asset));
        var viewport = new SubViewport
        {
            Size = new(512, 512), OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        var samples = new Dictionary<int, ulong[]>();
        TestScene.AddChild(viewport);
        viewport.AddChild(new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = 11,
            Position = new(4.25f, 4.775f, 20), Current = true
        });
        viewport.AddChild(new DirectionalLight3D());
        try
        {
            foreach (var count in new[] { 1, 64 })
            {
                var visuals = new List<PreparedVisual>();
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        var visual = resources.Prepare(new SceneVisual.Model(asset.Revision,
                            [MaterialCategory.PaintedMetal, MaterialCategory.Concrete, MaterialCategory.Glass],
                            new("#4080c0"), new(true, 17, 0, false), new(0, 0.125f, 0, index / 64f)));
                        visuals.Add(visual);
                        visual.Node.Position = new(index % 8 * 1.25f, index / 8 * 1.25f, 0);
                        Instance(visual).CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                        viewport.AddChild(visual.Node);
                    }
                    await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var values = new ulong[5];
                    for (var frame = 0; frame < values.Length; frame++)
                    {
                        await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                        values[frame] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
                    }
                    samples.Add(count, values);
                }
                finally { foreach (var visual in visuals) visual.Dispose(); }
            }
            var directory = ProjectSettings.GlobalizePath("res://TestResults/issue-339-model-batching");
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, $"draw-calls-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json");
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                godot = Godot.Engine.GetVersionInfo()["string"].AsString(),
                renderingMethod = RenderingServer.GetCurrentRenderingMethod(),
                driver = RenderingServer.GetCurrentRenderingDriverName(), adapter = RenderingServer.GetVideoAdapterName(),
                viewportWidth = 512, viewportHeight = 512, surfacesPerModel = 3, samples,
                globalUniformBufferSlots = ProjectSettings.GetSetting("rendering/limits/global_shader_variables/buffer_size").AsInt64()
            }, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"MODEL_DRAW_CALLS={output}");
            Require(samples.Values.All(values => values.Distinct().Count() == 1), "模型绘制调用数不稳定，需检查原始测量。");
        }
        finally { viewport.Free(); }
    }

    private static void AssertSurface(ShaderMaterial material, SurfaceState expected)
    {
        Require(material.GetShaderParameter("wear_enabled").AsBool() == expected.WearEnabled, "WearEnabled 丢失。");
        var halves = material.GetShaderParameter("wear_seed").AsVector2I();
        var seed = (ulong)unchecked((uint)halves.X) | ((ulong)unchecked((uint)halves.Y) << 32);
        Require(seed == expected.WearSeed, "WearSeed 在着色器上传时丢失 64 位精度。");
        Near(material.GetShaderParameter("surface_damage").AsSingle(), expected.Damage);
        Require(material.GetShaderParameter("scorched").AsBool() == expected.Scorched, "Scorched 丢失。");
    }

    private static SceneMesh Triangle(SceneMaterial material) => new(material,
        [new(NVector3.Zero, NVector3.UnitZ, new NVector2(0.1f, 0.2f), NVector2.Zero, ExposedFaceEdges.MinimumU, 0.125f, 0.25f),
         new(NVector3.UnitX, NVector3.UnitZ, new NVector2(0.4f, 0.3f), NVector2.UnitX, ExposedFaceEdges.MaximumU, 0.5f, 0.375f),
         new(NVector3.UnitY, NVector3.UnitZ, new NVector2(3.3f, -2.2f), NVector2.UnitY, ExposedFaceEdges.MinimumV | ExposedFaceEdges.MaximumV, 0.875f, 0.75f)], [0, 1, 2]);

    private static MeshInstance3D Instance(PreparedVisual visual) => visual.Node.GetChildren().OfType<MeshInstance3D>().Single();
    private static ShaderMaterial Material(PreparedVisual visual) => (ShaderMaterial)Instance(visual).MaterialOverride;
    private static FixedContentView Content(ContentAsset asset) => new()
    {
        Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) }
    };
    private static ContentAsset Png(string revision)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return new(new(revision), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(4, 4), []);
    }
    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < 0.001, $"数值错误：{actual}，期望 {expected}。");
}

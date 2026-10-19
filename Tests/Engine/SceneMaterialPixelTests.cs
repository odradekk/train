using System.Collections.Immutable;
using System.Text.RegularExpressions;
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

public class SceneMaterialPixelTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Face_alpha_is_linear_paint_weight_and_never_transparency()
    {
        using var fixture = new PixelFixture(TestScene);
        foreach (var category in Enum.GetValues<MaterialCategory>())
        {
            using var original = await fixture.Face(category, null, false, ExposedFaceEdges.None);
            using var painted = await fixture.Face(category, new("#4080c0"), false, ExposedFaceEdges.None);
            for (var band = 0; band < 3; band++)
            {
                var source = original.GetPixel(24 + band * 48, 72);
                var actual = painted.GetPixel(24 + band * 48, 72);
                Require(actual.A > 0.99f, $"{category} 的面贴图 Alpha 错当透明度。");
                var weight = category is MaterialCategory.PaintedMetal or MaterialCategory.Concrete
                    ? new[] { 0f, 128f / 255, 1f }[band] : 0;
                var paint = Color.FromHtml("#4080c0").SrgbToLinear();
                var linear = source.SrgbToLinear();
                var expected = new Color(linear.R * (1 - weight + weight * paint.R),
                    linear.G * (1 - weight + weight * paint.G), linear.B * (1 - weight + weight * paint.B));
                Require(Distance(actual.SrgbToLinear(), expected) < 0.025f,
                    $"{category} Alpha 档 {band} 没有在线性空间按权重涂装。");
            }
        }
    }

    [Test]
    public async Task Mipmapped_zero_alpha_keeps_source_rgb_visible()
    {
        using var fixture = new PixelFixture(TestScene, uniformAlpha: 0);
        using var rendered = await fixture.Face(MaterialCategory.PaintedMetal, new("#000000"), false,
            ExposedFaceEdges.None, textureRepeats: 128);
        var pixel = rendered.GetPixel(72, 72);
        Require(pixel.A > 0.99f && pixel.R > 0.45f && pixel.G > 0.25f && pixel.B > 0.15f,
            "缩小采样后的 Alpha 0 原色丢失或被黑色涂装覆盖。");
    }

    [Test]
    public async Task Paint_chipping_strength_follows_alpha_at_the_same_surface_coordinates()
    {
        var strengths = new List<float>();
        foreach (var alpha in new byte[] { 0, 128, 255 })
        {
            using var fixture = new PixelFixture(TestScene, uniformAlpha: alpha);
            // 白色涂装使三张输入的底色相同；同坐标、种子、法线和高度使灰尘及杂色相同。
            using var interior = await fixture.Face(MaterialCategory.PaintedMetal, new("#ffffff"), true,
                ExposedFaceEdges.None);
            using var edge = await fixture.Face(MaterialCategory.PaintedMetal, new("#ffffff"), true,
                ExposedFaceEdges.MinimumV);
            var strength = 0f;
            for (var x = 12; x < 132; x++)
            for (var y = 136; y < 142; y++)
                strength += Distance(edge.GetPixel(x, y).SrgbToLinear(), interior.GetPixel(x, y).SrgbToLinear());
            strengths.Add(strength / (120 * 6));
        }
        Require(strengths[0] < 0.005f, "Alpha 0 保留区出现掉漆。");
        Require(strengths[2] > 0.01f, "有漆的外露棱边没有可见掉漆。");
        Require(Math.Abs(strengths[1] / strengths[2] - 128f / 255) < 0.12f,
            "相同底色和表面坐标下，中间 Alpha 的线性掉漆强度不是全权重的一半。");
    }

    [Test]
    public async Task Glb_alpha_only_cuts_out_and_does_not_weight_paint()
    {
        using var fixture = new PixelFixture(TestScene);
        using var original = await fixture.Model(null);
        using var painted = await fixture.Model(new("#4080c0"));
        Require(original.GetPixel(24, 72).A < 0.01f && painted.GetPixel(24, 72).A < 0.01f,
            "GLB MASK 没有镂空 Alpha 0 区。");
        var paint = Color.FromHtml("#4080c0").SrgbToLinear();
        foreach (var x in new[] { 72, 120 })
        {
            var source = original.GetPixel(x, 72).SrgbToLinear();
            var actual = painted.GetPixel(x, 72).SrgbToLinear();
            Require(actual.A > 0.99f && Distance(actual,
                new(source.R * paint.R, source.G * paint.G, source.B * paint.B)) < 0.025f,
                "GLB 可见表面没有整体乘涂装色，或错误地把 Alpha 当涂装权重。");
        }
    }

    [Test]
    public async Task Wear_edges_follow_category_and_preserve_unpainted_regions()
    {
        using var fixture = new PixelFixture(TestScene);
        foreach (var category in Enum.GetValues<MaterialCategory>())
        {
            using var clean = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None);
            using var interior = await fixture.Face(category, new("#204080"), true, ExposedFaceEdges.None);
            using var edge = await fixture.Face(category, new("#204080"), true, ExposedFaceEdges.MinimumV);
            var difference = new float[3];
            var lightening = 0f;
            for (var band = 0; band < 3; band++)
            for (var x = 12 + band * 48; x < 36 + band * 48; x++)
            for (var y = 136; y < 142; y++)
            {
                var exposed = edge.GetPixel(x, y);
                var covered = interior.GetPixel(x, y);
                difference[band] = Math.Max(difference[band], Distance(exposed, covered));
                lightening = Math.Max(lightening,
                    (exposed.R + exposed.G + exposed.B - covered.R - covered.G - covered.B) / 3);
            }
            if (category == MaterialCategory.Concrete)
                Require(lightening > 0.005f, "混凝土外露边没有浅色磕碰。");
            if (category == MaterialCategory.BareMetal)
                Require(difference.Max() < 0.01f, "裸金属外露边出现掉漆。");
            if (category == MaterialCategory.Glass)
            {
                Require(difference.Max() > 0.005f, "玻璃外露边没有积灰。");
                Require(Distance(clean.GetPixel(72, 72), edge.GetPixel(72, 72)) < 0.01f,
                    "玻璃非边缘区域叠加了磨损。");
            }
            if (category is MaterialCategory.StoneSoil or MaterialCategory.Organic)
                Require(AllEqual(clean, edge), $"{category} 不应叠加常态磨损。");
            using var repeated = await fixture.Face(category, new("#204080"), true, ExposedFaceEdges.MinimumV);
            Require(AllEqual(edge, repeated), $"{category} 重建后磨损图案改变。");
        }
    }

    [Test]
    public async Task Damage_follows_vertex_and_surface_amount_per_category()
    {
        using var fixture = new PixelFixture(TestScene);
        foreach (var category in Enum.GetValues<MaterialCategory>())
        {
            using var plain = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None);
            using var untouched = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None, vertexDamage: 0, surfaceDamage: 0.6f);
            using var structure = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None, vertexDamage: 1, surfaceDamage: 0.6f);
            using var capped = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None, vertexDamage: 1, surfaceDamage: 1);
            using var limit = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None, vertexDamage: 1, surfaceDamage: 0.65f);
            using var part = await fixture.Face(category, new("#204080"), false, ExposedFaceEdges.None, vertexDamage: 0.5f);
            Require(AllEqual(plain, untouched), $"{category} 顶点损伤为 0 时画面与不传损伤不同。");
            var structureDelta = MaxDistance(plain, structure);
            Require(structureDelta > 0.02f, $"{category} 顶点损伤 1、表面损伤 0.6 的画面变化只有 {structureDelta:F3}。");
            Require(AllEqual(capped, limit), $"{category} 表面损伤 1.0 与 0.65 的画面不同。");
            var partDelta = MaxDistance(plain, part);
            Require(partDelta > 0.02f, $"{category} 部件损伤 0.5 在表面损伤为 0 时的画面变化只有 {partDelta:F3}。");
        }
    }

    [Test]
    public async Task Model_instancing_records_completed_frame_draw_calls()
    {
        Require(RenderingServer.GetCurrentRenderingMethod() == "forward_plus", "模型绘制测量要求 Forward+。");
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var bytes = GlbFixture.Create(feature: "cell");
        var inspection = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("instancing-model"), bytes);
        Require(inspection is AssetInspection.Success, "批处理模型无法解码。");
        var asset = new ContentAsset(new("instancing-model"), bytes, ((AssetInspection.Success)inspection).Metadata, []);
        using var resources = new SceneResources(cache, new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset)
            }
        });
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
                        visual.Node.GetChildren().OfType<MeshInstance3D>().Single().CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
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
            GD.Print($"MODEL_DRAW_CALLS one=[{string.Join(",", samples[1])}] sixtyFour=[{string.Join(",", samples[64])}]");
            Require(samples.Values.All(values => values.Distinct().Count() == 1), "模型绘制调用数不稳定，需检查原始测量。");
            Require(samples[1][0] > 0, "模型绘制测量没有记录到绘制调用。");
            Require(samples[64][0] == samples[1][0], "64 个模型实例的绘制调用数高于单实例，实例批处理失效。");
        }
        finally { viewport.Free(); }
    }

    private static float Distance(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R),
        Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static bool AllEqual(Image a, Image b) => MaxDistance(a, b) <= 0.005f;

    private static float MaxDistance(Image a, Image b)
    {
        var distance = 0f;
        for (var y = 4; y < 140; y++)
        for (var x = 4; x < 140; x++)
            distance = Math.Max(distance, Distance(a.GetPixel(x, y), b.GetPixel(x, y)));
        return distance;
    }

    private sealed class PixelFixture : IDisposable
    {
        private readonly Node _scene;
        private readonly GodotMainThreadDispatcher _dispatcher = new();
        private readonly AssetResourceCache _cache;
        private readonly SceneResources _resources;
        private readonly SubViewport _viewport = new()
        {
            Size = new(144, 144), OwnWorld3D = true, TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        private readonly AssetRevision _png = new("pixel-face");
        private readonly AssetRevision _glb = new("pixel-model");

        internal PixelFixture(Node scene, byte? uniformAlpha = null)
        {
            _scene = scene;
            _cache = new(_dispatcher);
            using var image = Image.CreateEmpty(96, 32, false, Image.Format.Rgba8);
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 96; x++)
                image.SetPixel(x, y, new Color(0.8f, 0.6f, 0.4f,
                    uniformAlpha is { } alpha ? alpha / 255f : new[] { 0f, 128f / 255, 1f }[x / 32]));
            var png = image.SavePngToBuffer();
            var bytes = GlbFixture.Create(feature: "cell", png: png, changeJson: json =>
            {
                var primitives = json["meshes"]![0]!["primitives"]!.AsArray();
                primitives.RemoveAt(2);
                primitives.RemoveAt(0);
            });
            var inspection = new GodotAssetInspector(_dispatcher).Inspect(AssetKind.Glb, _glb, bytes);
            Require(inspection is AssetInspection.Success, "像素测试 GLB 夹具无法解码。");
            var assets = new[]
            {
                new ContentAsset(_png, ImmutableArray.CreateRange(png), new AssetMetadata.Png(96, 32), []),
                new ContentAsset(_glb, bytes, ((AssetInspection.Success)inspection).Metadata, [])
            };
            _resources = new(_cache, new FixedContentView
            {
                Assets = assets.ToDictionary(asset => asset.Revision,
                    asset => (ContentLookup<ContentAsset>)new ContentLookup<ContentAsset>.Found(asset))
            });
            scene.AddChild(_viewport);
            _viewport.AddChild(new Camera3D
            {
                Position = new(0.5f, 0.5f, 2), Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 1, Current = true
            });
        }

        internal async Task<Image> Face(MaterialCategory category, PaintColor? paint, bool wear,
            ExposedFaceEdges edges, float textureRepeats = 1, float vertexDamage = 0,
            float surfaceDamage = 0, bool scorched = false)
        {
            using var visual = _resources.Prepare(new SceneVisual.Geometry(Quad(
                new SceneMaterial.Voxel(_png, category, paint), edges, textureRepeats, vertexDamage),
                new(wear, 17, surfaceDamage, scorched)));
            var mesh = visual.Node.GetChildren().OfType<MeshInstance3D>().Single();
            return await Render(mesh.Mesh, (ShaderMaterial)mesh.MaterialOverride);
        }

        internal async Task<Image> Model(PaintColor? paint)
        {
            using var visual = _resources.Prepare(new SceneVisual.Model(_glb,
                [MaterialCategory.PaintedMetal],
                paint, new(false, 17, 0, false), new(0, 1, 0, 0)));
            var mesh = visual.Node.GetChildren().OfType<MeshInstance3D>().Single();
            var camera = _viewport.GetChildren().OfType<Camera3D>().Single();
            camera.Position = new(-0.125f, 0.4f, 2);
            camera.Size = 0.8f;
            return await Render(mesh.Mesh, (ShaderMaterial)mesh.GetSurfaceOverrideMaterial(0));
        }

        private async Task<Image> Render(Mesh mesh, ShaderMaterial source)
        {
            using var output = new GodotOutputCapture();
            OS.AddLogger(output);
            try
            {
                using var material = (ShaderMaterial)source.Duplicate();
                // 颜色契约独立于光照；保留生产 vertex/fragment，只禁用光照响应。
                using var shader = new Shader
                {
                    Code = Regex.Replace(source.Shader.Code, @"render_mode\s+([^;]+);", "render_mode $1, unshaded;")
                        .Replace("#include \"scene_surface.gdshaderinc\"",
                            "#include \"res://Rendering/scene_surface.gdshaderinc\"")
                };
                material.Shader = shader;
                var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = material };
                _viewport.AddChild(instance);
                try
                {
                    await _scene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    await _scene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var result = _viewport.GetTexture().GetImage();
                    Require(!result.IsEmpty(), "有窗口的 SubViewport 没有像素输出。");
                    Require(!output.Contains("Shader compilation failed") && !output.Contains("SHADER ERROR") &&
                        !output.Contains("Shader error"), "像素测试着色器编译失败。");
                    return result;
                }
                finally { instance.Free(); }
            }
            finally { OS.RemoveLogger(output); }
        }

        private static SceneMesh Quad(SceneMaterial material, ExposedFaceEdges edges, float repeats, float damage = 0)
        {
            SceneVertex Vertex(float x, float y) => new(new NVector3(x, y, 0), NVector3.UnitZ,
                new NVector2(x * repeats, y * repeats), new NVector2(x, y), edges, 1, damage);
            return new(material, [Vertex(0, 0), Vertex(1, 0), Vertex(1, 1), Vertex(0, 1)], [0, 1, 2, 0, 2, 3]);
        }

        public void Dispose()
        {
            _viewport.Free();
            _resources.Dispose();
            _cache.Dispose();
            _dispatcher.Dispose();
        }
    }
}

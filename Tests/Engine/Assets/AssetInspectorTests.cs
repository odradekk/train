using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests.Assets;

public class AssetInspectorTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Worker_glb_inspection_requires_main_thread_pumping()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        var bytes = GlbFixture.Create();
        var expected = Glb(inspector, bytes);
        var mainThread = System.Environment.CurrentManagedThreadId;
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = Task.Run(() =>
        {
            entered.SetResult(System.Environment.CurrentManagedThreadId);
            return Glb(inspector, bytes);
        });
        Require(await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)) != mainThread, "GLB 检查没有进入工作线程。");
        try
        {
            Require(!background.IsCompleted, "GLB 在主线程泵送前完成。");
            await FramesUntil(TestScene, () => background.IsCompleted, dispatcher);
            var actual = await background;
            Require(actual with { Surfaces = expected.Surfaces } == expected &&
                actual.Surfaces.SequenceEqual(expected.Surfaces), "GLB 工作线程请求的检查结果与主线程不一致。");
        }
        finally { dispatcher.Dispose(); }

        try
        {
            await Task.Run(() => Glb(inspector, bytes));
            throw new InvalidOperationException("关闭 dispatcher 后工作线程 GLB 检查仍然成功。");
        }
        catch (ObjectDisposedException) { }
    }

    [Test]
    public void Rectangular_png_reports_dimensions_and_damaged_png_cannot_decode()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        using var image = Image.CreateEmpty(3, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        var result = inspector.Inspect(AssetKind.Png, new AssetRevision("rectangular"),
            ImmutableArray.CreateRange(image.SavePngToBuffer()));
        Require(result is AssetInspection.Success { Metadata: AssetMetadata.Png { Width: 3, Height: 2 } },
            "矩形 PNG 应成功报告尺寸，合格性规则由 Content 执行。");
        CannotDecode(inspector.Inspect(AssetKind.Png, new AssetRevision("broken-png"),
            ImmutableArray.Create<byte>(137, 80, 78, 71, 13, 10, 26, 10)));
    }

    [Test]
    public void Glb_reports_asymmetric_geometry_transformed_bounds_and_primitive_alpha_order()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        var plain = Glb(inspector, GlbFixture.Create());
        Require(plain.MeshNodeCount == 1, "网格节点计数错误。");
        Require(plain.Surfaces.SequenceEqual(new[] { AssetAlphaMode.Opaque, AssetAlphaMode.Mask, AssetAlphaMode.Blend }),
            "表面顺序必须对应 primitive 的 JSON 材质，缺省为 Opaque。");
        Bounds(plain.Bounds, new(-1, 0, 0), new(2, 3, 5));
        Require(!plain.HasSkeleton && !plain.HasMorphTargets && !plain.HasAnimation
            && !plain.HasCamera && !plain.HasLight && !plain.HasExternalImages, "基础夹具误报特性。");
        var transformed = Glb(inspector, GlbFixture.Create(transformed: true));
        // 子变换后父节点绕 Z 旋转 90°：x=4-6y，y=22+x，z=42+4z。
        Bounds(transformed.Bounds, new(-14, 21, 42), new(4, 24, 62));
    }

    [Test]
    public void Legal_glb_features_are_reported_as_facts_without_content_policy_rejection()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        Require(Glb(inspector, GlbFixture.Create(feature: "skin")).HasSkeleton, "未报告骨骼。");
        Require(Glb(inspector, GlbFixture.Create(feature: "morph")).HasMorphTargets, "未报告形变。");
        Require(Glb(inspector, GlbFixture.Create(feature: "animation")).HasAnimation, "未报告动画。");
        Require(Glb(inspector, GlbFixture.Create(feature: "camera")).HasCamera, "未报告相机。");
        Require(Glb(inspector, GlbFixture.Create(feature: "light")).HasLight, "未报告灯光。");
        Require(Glb(inspector, GlbFixture.Create(externalImage: true)).HasExternalImages,
            "外部图片应报告为成功事实，不能尝试读取或提前执行 Content 禁止规则。");
    }

    [Test]
    public void External_buffers_invalid_materials_and_damaged_glb_are_rejected()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        var external = inspector.Inspect(AssetKind.Glb, new AssetRevision("external-buffer"),
            GlbFixture.Create(externalBuffer: true));
        Require(external is AssetInspection.Failure { Kind: AssetInspectionFailure.UnsupportedContent },
            "即使未使用，第二个非 data 外部 buffer 也必须拒绝。");
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("invalid-alpha"),
            GlbFixture.Create(invalidAlpha: "UNKNOWN")));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("invalid-material"),
            GlbFixture.Create(invalidMaterial: true)));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("invalid-material-type"),
            GlbFixture.Create(externalImage: true, changeJson: json =>
                json["meshes"]![0]!["primitives"]![0]!["material"] = "bad")));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("invalid-material-object"),
            GlbFixture.Create(externalImage: true, changeJson: json => json["materials"]![0] = 1)));
        var original = GlbFixture.Create();
        var truncated = original.RemoveAt(original.Length - 1);
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("truncated"), truncated));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("broken"), ImmutableArray.Create<byte>(1, 2, 3)));
        dispatcher.Dispose();
        Require(inspector.Inspect(AssetKind.Glb, new("no-engine-call"), GlbFixture.Create(externalBuffer: true))
            is AssetInspection.Failure { Kind: AssetInspectionFailure.UnsupportedContent },
            "外部 buffer 的预检不应进入已关闭的 Godot dispatcher。");
    }

    [Test]
    public void Malformed_geometry_json_returns_cannot_decode_and_shutdown_still_throws()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        foreach (var shape in new[] { "5", "{}", "null" })
            CannotDecode(inspector.Inspect(AssetKind.Glb, new("primitives-shape"),
                GlbFixture.Create(changeJson: json =>
                    json["meshes"]![0]!["primitives"] = System.Text.Json.Nodes.JsonNode.Parse(shape))));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new("meshes-shape"),
            GlbFixture.Create(changeJson: json => json["meshes"] = new System.Text.Json.Nodes.JsonObject())));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new("mesh-shape"),
            GlbFixture.Create(changeJson: json => json["meshes"]![0] = 5)));
        CannotDecode(inspector.Inspect(AssetKind.Glb, new("primitive-shape"),
            GlbFixture.Create(changeJson: json => json["meshes"]![0]!["primitives"]![0] = 5)));
        dispatcher.Dispose();
        try
        {
            inspector.Inspect(AssetKind.Glb, new("closed"), GlbFixture.Create());
            throw new Exception("关闭 dispatcher 不能被误报为解码失败。");
        }
        catch (ObjectDisposedException) { }
    }

    [Test]
    public void Embedded_png_is_decoded_and_damaged_embedded_png_is_rejected()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        using var image = Image.CreateEmpty(3, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Green);
        var valid = Glb(inspector, GlbFixture.Create(png: image.SavePngToBuffer()));
        Require(!valid.HasExternalImages, "内嵌 PNG 不能被算作外部图片。");
        CannotDecode(inspector.Inspect(AssetKind.Glb, new AssetRevision("bad-embedded-png"),
            GlbFixture.Create(png: [137, 80, 78, 71, 13, 10, 26, 10])));
    }

    private static AssetMetadata.Glb Glb(IAssetInspector inspector, ImmutableArray<byte> bytes)
    {
        var result = inspector.Inspect(AssetKind.Glb,
            new AssetRevision(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan()))), bytes);
        Require(result is AssetInspection.Success { Metadata: AssetMetadata.Glb }, $"合法 GLB 未成功解码：{result}");
        return (AssetMetadata.Glb)((AssetInspection.Success)result).Metadata;
    }

    private static void CannotDecode(AssetInspection result) => Require(
        result is AssetInspection.Failure { Kind: AssetInspectionFailure.CannotDecode }, $"损坏资产错误分类不正确：{result}");

    private static void Bounds(ModelBounds actual, LocalPoint minimum, LocalPoint maximum)
    {
        Require(Near(actual.MinimumMeters.X, minimum.X) && Near(actual.MinimumMeters.Y, minimum.Y)
            && Near(actual.MinimumMeters.Z, minimum.Z) && Near(actual.MaximumMeters.X, maximum.X)
            && Near(actual.MaximumMeters.Y, maximum.Y) && Near(actual.MaximumMeters.Z, maximum.Z),
            $"米制包围盒不正确：{actual}；期望 {minimum} 到 {maximum}。");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.0001;
}

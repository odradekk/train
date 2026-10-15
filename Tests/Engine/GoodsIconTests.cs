using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class GoodsIconTests(Node testScene) : TestClass(testScene)
{
    private static readonly DesignHash Design = new("icon-design");
    private static readonly GoodId First = new GoodId.Composite(new("icon:first"));
    private static readonly GoodId Second = new GoodId.Assembly(new("icon:second"));

    [Test]
    public void Failed_or_throwing_previews_release_their_capture_once()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var preview = new Preview(new(1), empty: true);
        var factory = new FixedDisplayFactory { PreviewDesignCall = (_, _) => new SceneResult<IDesignPreview>.Success(preview) };
        var cache = new GoodsIconCache();
        cache.Initialize(factory, dispatcher);
        TestScene.AddChild(cache);
        cache.Bind(new(Guid.NewGuid()), Content(1));
        try
        {
            var missing = cache.Create(First, null);
            TestScene.AddChild(missing);
            Require(preview.DisposeCalls == 1 && cache.GetChildCount() == 0 && Texture(missing) is null &&
                missing.TooltipText.Contains("没有可显示的网格"), "空网格失败必须清理已创建显示组、视口及预览，并明确显示失败。");
            missing.Free();
            cache.Bind(new(Guid.NewGuid()), Content(1));
            preview = new Preview(new(1), fault: true);
            var threw = false;
            try { cache.Create(First, null); }
            catch (InvalidOperationException exception) when (exception.Message == "preview fault") { threw = true; }
            Require(threw && preview.DisposeCalls == 1 && cache.GetChildCount() == 0,
                "Present意外异常必须传播，并由同一Capture清理预览。");
        }
        finally { cache.Free(); }
    }

    [Test]
    public async Task Ordinary_icons_share_resources_and_removal_while_hovered_is_safe()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        var revision = new AssetRevision("ordinary-icon");
        var asset = new ContentAsset(revision, System.Collections.Immutable.ImmutableArray.CreateRange(image.SavePngToBuffer()),
            new AssetMetadata.Png(4, 4), []);
        var content = new FixedContentView { Generation = new(1),
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> {
                [First] = new ContentLookup<GoodPresentation>.Found(new(First, new(new("普通物品", ""), revision, null, false), null, false)) },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [revision] = new ContentLookup<ContentAsset>.Found(asset) } };
        var cache = new GoodsIconCache();
        cache.Initialize(new FixedDisplayFactory(), dispatcher);
        TestScene.AddChild(cache);
        cache.Bind(new(Guid.NewGuid()), content);
        var first = cache.Create(First, 2);
        var second = cache.Create(First, 3);
        first.Position = new(100, 100); second.Position = new(180, 100);
        TestScene.AddChild(first); TestScene.AddChild(second);
        try
        {
            Require(Texture(first) is not null && ReferenceEquals(Texture(first), Texture(second)) &&
                first.TooltipText == "" && second.TooltipText == "", "成功普通图标及缓存命中不得提示缺失。");
            var firstFrame = (ShaderMaterial)first.GetChildren().OfType<ColorRect>().Single().Material;
            var secondFrame = (ShaderMaterial)second.GetChildren().OfType<ColorRect>().Single().Material;
            var firstImage = (ShaderMaterial)first.GetChildren().OfType<TextureRect>().Single().Material;
            var secondImage = (ShaderMaterial)second.GetChildren().OfType<TextureRect>().Single().Material;
            var frameShader = firstFrame.Shader;
            var imageShader = firstImage.Shader;
            Require(ReferenceEquals(frameShader, secondFrame.Shader) && ReferenceEquals(imageShader, secondImage.Shader),
                "图标必须共用两份Shader，状态材质仍独立。");
            Require(ReferenceEquals(UiStyle.NumericFont(), UiStyle.NumericFont()) &&
                ReferenceEquals(UiStyle.NumericFont(true), UiStyle.NumericFont(true)) &&
                !ReferenceEquals(UiStyle.NumericFont(), UiStyle.NumericFont(true)), "数字字体必须只共享常规与粗体两种资源。");
            await new UiTestInput(TestScene).Frames(2);
            var point = first.GetViewport().GetScreenTransform() * first.GetGlobalRect().GetCenter();
            using (var motion = new InputEventMouseMotion { Position = point, GlobalPosition = point }) Input.ParseInputEvent(motion);
            await new UiTestInput(TestScene).Frames(2);
            Require(firstFrame.GetShaderParameter("border").AsColor() == new Color("3f4751"), "真实鼠标悬停应刷新图标边框。");
            first.Quantity = 4;
            Require(firstFrame.GetShaderParameter("border").AsColor() == new Color("3f4751"), "数量修订不能丢失悬停状态。");
            first.Free();
            await new UiTestInput(TestScene).Frames(2);
            Require(!GodotObject.IsInstanceValid(firstFrame) && GodotObject.IsInstanceValid(frameShader) &&
                GodotObject.IsInstanceValid(imageShader) && GodotObject.IsInstanceValid(UiStyle.NumericFont(true)),
                "移除悬停图标只释放独占材质，不得释放共享Shader或字体。");
            second.Selected = true;
            Require(secondFrame.GetShaderParameter("border").AsColor() == new Color("e0a22e"), "其余图标仍能更新共享Shader的独立材质。");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(first)) first.Free();
            second.Free(); cache.Free();
        }
    }

    [Test]
    public async Task Shared_design_capture_is_retired_on_binding_generation_and_exit()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var previews = new List<Preview>();
        var factory = Factory(previews);
        var cache = new GoodsIconCache();
        cache.Initialize(factory, dispatcher);
        TestScene.AddChild(cache);
        var binding = new HostBinding(Guid.NewGuid());
        var content = Content(1);
        try
        {
            cache.Bind(binding, content);
            var first = cache.Create(First, 1);
            var second = cache.Create(Second, 2, 48);
            TestScene.AddChild(first); TestScene.AddChild(second);
            Require(factory.PreviewCalls.Count == 1 && previews[0].PresentCalls == 1,
                "同设计、同世代的不同货物必须共享一次只读预览及完整帧。");
            var viewport = cache.GetChildren().OfType<SubViewport>().Single();
            var mesh = viewport.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>().Single().Mesh;
            binding = new(Guid.NewGuid());
            cache.Bind(binding, content);
            Require(previews[0].DisposeCalls == 1 && !GodotObject.IsInstanceValid(viewport) && !GodotObject.IsInstanceValid(mesh),
                "绑定切换必须同步关闭预览并释放视口、显示网格。");
            await new UiTestInput(TestScene).Frames(2);
            Require(Texture(first) is null && Texture(second) is null,
                "旧绑定的已取消截图不得在之后的真实帧发布。");
            first.Free(); second.Free();

            var next = cache.Create(First, null);
            TestScene.AddChild(next);
            Require(factory.PreviewCalls.Count == 2, "新绑定不得复用旧绑定已退役的图标。");
            var secondViewport = cache.GetChildren().OfType<SubViewport>().Single();
            cache.Bind(binding, Content(2));
            Require(previews[1].DisposeCalls == 1 && !GodotObject.IsInstanceValid(secondViewport) && Texture(next) is null,
                "内容世代切换必须清理旧预览及控件纹理。");
            next.Free();
            var final = cache.Create(First, null);
            TestScene.AddChild(final);
            var finalViewport = cache.GetChildren().OfType<SubViewport>().Single();
            cache.Free();
            Require(previews[2].DisposeCalls == 1 && !GodotObject.IsInstanceValid(finalViewport) && Texture(final) is null,
                "退出必须释放进行中截图资源，并清除仍存活控件的纹理。");
            final.Free();
        }
        finally { if (GodotObject.IsInstanceValid(cache)) cache.Free(); }
    }

    [Test]
    public async Task Window_capture_has_transparent_background_and_shows_all_icon_states()
    {
        // dummy 渲染器只覆盖上个测试的资源生命周期，不提供像素验证证据。
        if (DisplayServer.GetName() == "headless") return;
        using var dispatcher = new GodotMainThreadDispatcher();
        var previews = new List<Preview>();
        var factory = Factory(previews);
        var cache = new GoodsIconCache();
        cache.Initialize(factory, dispatcher);
        var layer = new CanvasLayer();
        var panel = new ColorRect { Color = new("30363e"), Position = new(80, 80), Size = new(600, 390) };
        layer.AddChild(panel);
        TestScene.AddChild(layer); TestScene.AddChild(cache);
        var root = TestScene.GetTree().Root;
        var scale = root.ContentScaleFactor;
        root.ContentScaleFactor = 1;
        UiCapture.ConfigureWindowIfNeeded(root);
        cache.Bind(new(Guid.NewGuid()), Content(1));
        var controls = new List<GoodIcon>();
        try
        {
            for (var row = 0; row < 3; row++)
            {
                var size = new[] { 32, 48, 64 }[row];
                var caption = new Label { Text = $"{size}px    普通             选中             不可用", Position = new(20, 15 + row * 115) };
                panel.AddChild(caption);
                for (var column = 0; column < 3; column++)
                {
                    var icon = cache.Create(column == 1 ? Second : First, 123, size, column == 2);
                    icon.Selected = column == 1;
                    icon.Position = new(50 + column * 150, 45 + row * 115);
                    panel.AddChild(icon); controls.Add(icon);
                }
            }
            await FramesUntil(TestScene, () => Texture(controls[0]) is not null);
            Require(factory.PreviewCalls.Count == 1 && previews[0].DisposeCalls == 1 && cache.GetChildCount() == 0,
                "截图后必须只缓存纹理，释放预览和临时视口。");
            var cached = cache.Create(Second, null);
            panel.AddChild(cached);
            cached.Position = new(520, 30);
            Require(cached.TooltipText == "" && Texture(cached) is not null && controls.All(icon => icon.TooltipText == ""),
                "成功复合图标及截图完成后的缓存命中不得显示缺失或生成中提示。");
            var texture = Texture(controls[0])!;
            Require(controls.All(icon => ReferenceEquals(Texture(icon), texture)), "所有尺寸、状态和货物应共用设计纹理。");
            using (var image = texture.GetImage())
            {
                Require(!image.IsEmpty() && image.GetWidth() == 128 && image.GetHeight() == 128,
                    "图标必须由真实离屏视口生成128px图像。");
                Require(image.GetPixel(0, 0).A < 0.01f && image.GetPixel(127, 127).A < 0.01f,
                    "复合图标背景角落必须透明。");
                Require(Enumerable.Range(0, 128 * 128).Any(index => image.GetPixel(index % 128, index / 128).A > 0.5f),
                    "图标必须含真正绘制的非透明几何，不能只缓存透明图片。");
            }
            Require(controls.Where(icon => icon.Unavailable).All(icon => Mathf.IsEqualApprox(icon.Modulate.A, 0.45f)),
                "不可用控件整体alpha必须为45%。");
            await UiCapture.Capture(TestScene, "goods-icons-states-32-48-64");
            cache.Bind(new(Guid.NewGuid()), Content(2));
            Require(!GodotObject.IsInstanceValid(texture) && controls.All(icon => Texture(icon) is null),
                "世代切换必须释放已完成的图标纹理并清除控件引用。");
        }
        finally { cache.Free(); layer.Free(); root.ContentScaleFactor = scale; }
    }

    private static Texture2D? Texture(GoodIcon icon) => icon.GetChildren().OfType<TextureRect>().Single().Texture;

    private static FixedDisplayFactory Factory(List<Preview> previews) => new()
    {
        PreviewDesignCall = (content, _) =>
        {
            var preview = new Preview(content.Generation);
            previews.Add(preview);
            return new SceneResult<IDesignPreview>.Success(preview);
        }
    };

    private static FixedContentView Content(ulong generation)
    {
        var definition = new DesignDraft(new("图标测试", ""), new DesignSpecification.SceneModel(null), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(Design, new("icon-composition"), definition, null,
            new(Design, null, null, true, null, null, [], [], [], [], []), true, []);
        ContentLookup<GoodPresentation> Good(GoodId id) => new ContentLookup<GoodPresentation>.Found(
            new(id, new(new("图标测试", ""), null, null, false), Design, false));
        return new() { Generation = new(generation),
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [First] = Good(First), [Second] = Good(Second) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [Design] = new ContentLookup<DesignEvaluation>.Found(evaluation) } };
    }

    private sealed class Preview(ContentGeneration content, bool empty = false, bool fault = false) : IDesignPreview
    {
        public ContentGeneration Content => content;
        internal int PresentCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public SceneResult<ISceneFrame> Present()
        {
            PresentCalls++;
            if (fault) throw new InvalidOperationException("preview fault");
            Require(DisposeCalls == 0, "关闭的预览不得继续Present。");
            var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
                [new(new(-1, -1, 0), NVector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(1, -1, 0), NVector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(0, 1, 0), NVector3.UnitZ, new(0.5f, 1), new(0.5f, 1), ExposedFaceEdges.None, 1, 0)], [0, 1, 2]);
            var obj = new SceneObject(new(1), null, new(NVector3.Zero, System.Numerics.Quaternion.Identity), null,
                [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);
            var frame = new FixedSceneFrame { Update = new(null,
                new(new(Guid.NewGuid()), new(1), new(1), 1), NVector3.Zero, empty ? [] : [obj], [], new([], []), []) };
            return new SceneResult<ISceneFrame>.Success(frame);
        }
        public void Dispose() => DisposeCalls++;
    }
}

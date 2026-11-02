using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class GoodsIconTests
{
    private static Texture2D? Texture(GoodIcon icon) => icon.GetChildren().OfType<TextureRect>().Single().Texture;

    [Test]
    public async Task Draft_thumbnails_keep_the_pending_capture_until_the_content_binding_changes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var previews = new List<Preview>();
        var factory = Factory(previews);
        var cache = new GoodsIconCache(); cache.Initialize(factory, dispatcher); TestScene.AddChild(cache);
        var content = Content(1); cache.Bind(new(Guid.NewGuid()), content);
        var evaluation = (DesignEvaluation.Complete)((ContentLookup<DesignEvaluation>.Found)content.FindDesign(Design)).Value;
        GoodIcon? first = null, reopened = null;
        try
        {
            first = cache.CreateDesign(evaluation, 96); TestScene.AddChild(first);
            await FramesUntil(TestScene, () => cache.GetChildren().OfType<SubViewport>().Any());
            var viewport = cache.GetChildren().OfType<SubViewport>().Single();
            var mesh = viewport.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>().Single().Mesh;
            Require(previews.Single().PresentCalls == 1 && previews.Single().DisposeCalls == 0,
                "此情景应已提交完整截图视口，仍保留等待绘制的预览。");
            first.Free(); first = null;
            reopened = cache.CreateDesign(evaluation with { }, 160); TestScene.AddChild(reopened);
            Require(factory.PreviewCalls.Count == 1 && GodotObject.IsInstanceValid(viewport) && cache.GetChildCount() == 1,
                "卡片全部移除后，重新评估得到的同哈希设计必须继续使用原捕获。");
            cache.Bind(new(Guid.NewGuid()), Content(2));
            Require(!GodotObject.IsInstanceValid(viewport) && !GodotObject.IsInstanceValid(mesh) &&
                previews.Single().DisposeCalls == 1 && reopened.TooltipText == "图标绑定已失效",
                "内容绑定切换必须释放等待中的预览、视口及网格，并清除卡片绑定。");
        }
        finally { first?.Free(); reopened?.Free(); cache.Free(); }
    }

    [Test]
    public async Task Complete_and_partial_thumbnails_share_the_design_capture_with_goods()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var previews = new List<Preview>(); var factory = Factory(previews);
        var cache = new GoodsIconCache(); cache.Initialize(factory, dispatcher); TestScene.AddChild(cache);
        var content = Content(1); cache.Bind(new(Guid.NewGuid()), content);
        var complete = (DesignEvaluation.Complete)((ContentLookup<DesignEvaluation>.Found)content.FindDesign(Design)).Value;
        var partial = new DesignEvaluation.Partial(complete.Definition,
            new(new ComputedValue<double>.Unknown([]), new ComputedValue<Money>.Unknown([]),
                new ComputedValue<long>.Unknown([]), new ComputedValue<System.Collections.Immutable.ImmutableArray<PartProjection>>.Unknown([]),
                new ComputedValue<System.Collections.Immutable.ImmutableArray<GoodsQuantity>>.Unknown([]), new ComputedValue<int>.Unknown([])),
            new(0, new(0), 0, [], []), null, complete.Scene, []);
        var controls = new List<GoodIcon>();
        try
        {
            controls.Add(cache.CreateDesign(complete with { Scene = complete.Scene with { Design = null } }, 96));
            controls.Add(cache.CreateDesign(partial, 96));
            controls.Add(cache.Create(First, null));
            foreach (var icon in controls) TestScene.AddChild(icon);
            await FramesUntil(TestScene, () => cache.GetChildren().OfType<SubViewport>().Any());
            Require(factory.PreviewCalls.Count == 1 && cache.GetChildCount() == 1 && previews.Single().PresentCalls == 1 &&
                controls.Take(2).All(icon => icon.TooltipText == "缩略图正在生成") && controls[2].TooltipText == "图标正在生成",
                "完整评估采用 Design，部分评估采用 Scene.Design；相同哈希与世代必须和货物共享一次捕获。");
            cache.Bind(new(Guid.NewGuid()), Content(2));
            Require(previews.Single().DisposeCalls == 1 && cache.GetChildCount() == 0 &&
                controls.All(icon => icon.TooltipText == "图标绑定已失效"), "切换绑定必须同步释放共享捕获并清除所有控件绑定。");
        }
        finally { foreach (var icon in controls) icon.Free(); cache.Free(); }
    }

    [Test]
    public async Task Blueprint_thumbnails_share_the_existing_design_capture_with_goods()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var previews = new List<Preview>();
        var factory = Factory(previews);
        var cache = new GoodsIconCache(); cache.Initialize(factory, dispatcher); TestScene.AddChild(cache);
        cache.Bind(new(Guid.NewGuid()), Content(1));
        GoodIcon? thumbnail = null, good = null;
        try
        {
            thumbnail = cache.CreateDesign(Design, 96); TestScene.AddChild(thumbnail);
            good = cache.Create(First, null); TestScene.AddChild(good);
            await FramesUntil(TestScene, () => cache.GetChildren().OfType<SubViewport>().Any());
            Require(factory.PreviewCalls.Count == 1 && previews.Single().PresentCalls == 1 &&
                thumbnail.CustomMinimumSize == new Vector2(96, 96), "蓝图缩略图应复用设计预览，不必伪装成货物或重复渲染。");
            cache.Bind(new(Guid.NewGuid()), Content(2));
            Require(previews.Single().DisposeCalls == 1 && cache.GetChildCount() == 0,
                "切换内容绑定应同时释放蓝图与货物共享的缩略图资源。");
        }
        finally { thumbnail?.Free(); good?.Free(); cache.Free(); }
    }

}

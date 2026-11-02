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
    public async Task Draft_thumbnail_releases_its_capture_when_the_last_card_leaves()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        var previews = new List<Preview>();
        var cache = new GoodsIconCache(); cache.Initialize(Factory(previews), dispatcher); TestScene.AddChild(cache);
        var content = Content(1); cache.Bind(new(Guid.NewGuid()), content);
        var evaluation = ((ContentLookup<DesignEvaluation>.Found)content.FindDesign(Design)).Value;
        GoodIcon? first = null, second = null;
        try
        {
            first = cache.CreateDesign(evaluation, 96); second = cache.CreateDesign(evaluation, 160);
            TestScene.AddChild(first); TestScene.AddChild(second);
            await FramesUntil(TestScene, () => cache.GetChildCount() != 0);
            first.Free(); first = null;
            Require(previews.Single().DisposeCalls == 0, "同一草稿仍有卡片显示时不能提前释放预览。");
            second.Free(); second = null;
            Require(previews.Single().DisposeCalls == 1 && cache.GetChildCount() == 0,
                "草稿快照不在内容目录里，最后一张卡片移除后应释放它的预览，不能每次开库都积累。");
        }
        finally { first?.Free(); second?.Free(); cache.Free(); }
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

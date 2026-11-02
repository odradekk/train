using Chickensoft.GoDotTest;
using Train.App.Assets;
using Train.Content.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Cancelling_preparation_releases_its_next_frame_reservation()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var cancelInReservedFrame in new[] { false, true })
        {
            using var costs = new GeometryCostScope();
            await new UiTestInput(TestScene).Frames(1);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            using var display = Attach(cache, new FixedContentView());
            using var other = Attach(cache, new FixedContentView());
            var shown = Frame(1, []);
            await Commit(display, shown);
            var next = Frame(2, [Object(1, Peak(1))], candidate ? null : shown.Update.Frame,
                space: candidate ? 2UL : 1UL);
            if (candidate)
            {
                display.ReceiveCandidate(next.Update);
                await ScenePackingTestDriver.StartCandidate(display, TestScene);
            }
            else await ScenePackingTestDriver.StartFormal(display, next, TestScene);
            using (var active = other.Prepare(shown))
            {
                Require(active is not null, "预约前另一显示应能正常准备。");
                active!.Commit();
            }
            if (candidate) display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            else
            {
                using var blocked = display.Prepare(next);
                Require(blocked is null, "没有成本样本的几何应先预约下一帧，不能在本帧试做。");
            }
            if (cancelInReservedFrame) await new UiTestInput(TestScene).Frames(1);
            if (candidate) display.CancelCandidate();
            else display.CancelPreparation();
            if (!cancelInReservedFrame) await new UiTestInput(TestScene).Frames(1);
            using var resumed = other.Prepare(shown);
            Require(resumed is not null,
                $"取消{(candidate ? "候选" : "正式")}准备后，不能留下预约让其他显示空等一帧。");
            resumed!.Commit();
        }
    }
}

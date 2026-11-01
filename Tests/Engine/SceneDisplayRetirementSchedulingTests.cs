using Chickensoft.GoDotTest;
using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Formal_first_frames_ignore_exhausted_budget_and_clear_previous_retirement()
    {
        foreach (var change in new[] { "initial", "space", "session" })
        {
            await new UiTestInput(TestScene).Frames(1);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
            using var burner = Attach(cache, new FixedContentView());
            var baseline = Frame(1, []);
            await Commit(burner, baseline);
            Mesh? oldMesh = null, currentMesh = null;
            if (change != "initial")
            {
                await Commit(display, Frame(1, [Object(1, Peak(1))]));
                oldMesh = MeshOf(display.Objects[new(1)]);
                await Commit(display, Frame(2, [Object(2, Peak(2))], space: 2));
                currentMesh = MeshOf(display.Objects[new(2)]);
                Require(GodotObject.IsInstanceValid(oldMesh), "旧资源应留给之后的进程帧释放。");
            }
            await new UiTestInput(TestScene).Frames(1);
            long spent = 0;
            burner.Timestamp = () => spent;
            burner.Observe = item =>
            {
                if (item.Stage == SceneDisplayStage.FramePrepared) spent = Stopwatch.Frequency * 4 / 1000;
            };
            using (var empty = burner.Prepare(Frame(100, [], baseline.Update.Frame)))
                Require(empty is not null, "先完成空更新，再让收尾耗尽本帧共享预算。");
            long now = 0;
            display.Timestamp = () => now += Stopwatch.Frequency / 100;
            var next = Frame(3, [Object(3, Peak(3)), Object(4, Peak(4))],
                space: change == "space" ? 3UL : 2UL,
                session: change == "session" ? new SceneSessionId(Guid.Parse("00000000-0000-0000-0000-000000000003")) : null);
            var processFrame = Godot.Engine.GetProcessFrames();
            using var prepared = display.Prepare(next);
            Require(prepared is not null, $"{change}正式首帧必须越过耗尽的预算和旧退役，在本次调用内完整准备。");
            Require(change == "initial" || !GodotObject.IsInstanceValid(oldMesh) && GodotObject.IsInstanceValid(currentMesh),
                "先清上一批已摘下的旧债，准备期间不能释放当前显示帧。");
            prepared!.Commit();
            Require(Godot.Engine.GetProcessFrames() == processFrame && ReferenceEquals(display.DisplayedFrame, next) &&
                display.Objects.Count == 2 && display.Objects.Values.All(item => item.Root.IsInsideTree()),
                "首帧必须同帧一次原子接入完整对象。");
        }
    }

    [Test]
    public async Task Preparation_and_retirement_both_progress_without_spending_a_second_budget()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
        var wheel = Object(1, Peak(1)) with { Visuals = [new SceneVisual.Wheelset(2, 1, .5, 0)] };
        await Commit(display, Frame(1, [wheel]));
        var released = 0;
        for (var index = 0; index < 64; index++)
            display.Objects[wheel.Id].Visuals[0].Own(new AssetLease<object>(new(), () => released++));
        var shown = Frame(2, [], space: 2);
        await Commit(display, shown);
        display.Timestamp = () => released * Stopwatch.Frequency * 3 / 1000;
        var next = Frame(3, [Object(2, Peak(2)), Object(3, Peak(3))], shown.Update.Frame, space: 2);
        var pack = display.PackMesh;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        display.PackMesh = (mesh, cancellation) =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation).GetAwaiter().GetResult();
            return pack(mesh, cancellation);
        };
        try
        {
            using var pending = display.Prepare(next);
            Require(pending is null, "受控打包未放行前不能完成准备。");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.TrySetResult();
            try { await display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { display.PackMesh = pack; }
        }
        Require(released < 64, "开始检查进度前必须仍有真实退役积压，打包夹具不能清掉它。");
        List<TimeSpan> spans = [];
        display.Measure = (_, elapsed, _) => spans.Add(elapsed);
        SceneDisplay.PreparedFrame? prepared = null;
        for (var frame = 0; frame < 16 && prepared is null; frame++)
        {
            await new UiTestInput(TestScene).Frames(1);
            prepared = display.Prepare(next);
            var after = released;
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            Require(released <= after + 1, "同帧候选调用不能获得另一份4ms释放预算。");
        }
        using (prepared)
        {
            Require(prepared is not null && released > 0 && released < 64,
                $"仍有旧债时，未知几何也须在16个进程帧内完成，且退役必须同时取得进展。prepared={prepared is not null}, released={released}");
            prepared!.Commit();
        }
        Require(spans.All(span => span <= TimeSpan.FromMilliseconds(4)), "准备与退役只能使用同一份共享预算。");
        display.Dispose();
        Require(released == 64, "关闭必须清掉所有剩余租约。");
    }

    [Test]
    public async Task Retirement_gets_one_first_item_only_after_a_frame_without_release_progress()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
        await Commit(display, Frame(1, [Object(1, Peak(1))]));
        var retired = display.Objects[new(1)];
        var mesh = MeshOf(retired);
        long now = 0;
        display.Timestamp = () => now;
        display.Observe = item =>
        {
            if (item.Stage == SceneDisplayStage.FramePrepared) now += Stopwatch.Frequency * 4 / 1000;
        };
        await Commit(display, Frame(2, [], space: 2));
        for (var frame = 1; frame <= 8; frame++)
        {
            await new UiTestInput(TestScene).Frames(1);
            using var prepared = display.Prepare(Frame((ulong)frame + 2, [], display.DisplayedFrame!.Update.Frame, space: 2));
            Require(prepared is not null, "每帧4ms的活动收尾仍能完成，零成本退役步骤不另占一份预算。");
            prepared!.Commit();
            if (frame == 1)
                Require(GodotObject.IsInstanceValid(retired.Visuals[0].Node), "第一后续帧准备优先，耗尽预算后不能释放。");
            if (frame == 2)
                Require(!GodotObject.IsInstanceValid(retired.Visuals[0].Node) && GodotObject.IsInstanceValid(mesh),
                    "上一帧没有释放时，本帧先释放一个节点，剩余租约不得整组提前释放。");
        }
        Require(!GodotObject.IsInstanceValid(retired.Root) && !GodotObject.IsInstanceValid(mesh),
            "活动工作持续耗尽预算时，旧资源仍须在有限帧内全部释放。");
    }

    [Test]
    public async Task Normal_retirement_recovers_throughput_after_an_expensive_release()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
        await Commit(display, Frame(1, [Object(1, Peak(1))]));
        var retired = display.Objects[new(1)];
        long now = 0;
        var released = 0;
        for (var index = 0; index < 33; index++)
            retired.Visuals[0].Own(new AssetLease<object>(new(), () =>
            {
                now += released++ == 0 ? Stopwatch.Frequency * 8 / 1000 : Stopwatch.Frequency / 20000;
            }));
        var shown = Frame(2, [], space: 2);
        await Commit(display, shown);
        display.Timestamp = () => now;
        for (var frame = 0; frame < 8 && GodotObject.IsInstanceValid(retired.Root); frame++)
        {
            await new UiTestInput(TestScene).Frames(1);
            using var prepared = display.Prepare(shown);
        }
        Require(released == 33 && !GodotObject.IsInstanceValid(retired.Root),
            "一次8ms释放之后，32份各0.05ms的正常释放应恢复吞吐，8帧内清空旧批次。");
    }
}

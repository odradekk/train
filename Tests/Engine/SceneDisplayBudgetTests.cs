using System.Collections.Immutable;
using System.Diagnostics;
using Chickensoft.GoDotTest;
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
    public async Task Exhausted_shared_budget_does_not_force_more_work_in_the_same_frame()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        var next = Frame(2, [Object(1, Mesh()), Object(2, Mesh()), Object(3, Mesh())], first.Update.Frame);
        await ScenePackingTestDriver.StartFormal(display, next, TestScene);
        await new UiTestInput(TestScene).Frames(1);
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency;
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        Require(display.Prepare(next) is null, "单项成本超预算后应暂停。");
        var completed = events.Count(e => e.Stage == SceneDisplayStage.ResourcesPrepared);
        var processFrame = Godot.Engine.GetProcessFrames();
        for (var call = 0; call < 5; call++)
        {
            using var partial = display.Prepare(next);
            Require(partial is null && events.Count(e => e.Stage == SceneDisplayStage.ResourcesPrepared) == completed,
                "共享预算用完后，同帧的重复调用不能再强制推进资源。");
        }
        Require(Godot.Engine.GetProcessFrames() == processFrame, "预算断言必须处于同一真实进程帧。");
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        using var prepared = await Prepare(display, next);
        prepared.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, next) && display.Objects.Count == 3,
            "下一进程帧恢复额度后应完成全部资源并原子提交。");
    }

    [Test]
    public async Task Initial_matching_yields_and_emits_one_complete_required_set_before_resources()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        var mesh = Mesh();
        var objects = Enumerable.Range(1, 128).Select(i => Object((ulong)i, mesh)).ToImmutableArray();
        var next = Frame(2, objects, first.Update.Frame);
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency / 10000;
        using var partial = display.Prepare(next);
        Require(partial is null && events.Count == 0, "复用匹配必须跨帧续做，完整 Required 未知时不能先发开始事件。");
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        using var prepared = await Prepare(display, next);
        Require(events[0].Stage == SceneDisplayStage.FramePreparationStarted && events[0].ResourceObjects is { } required &&
            required.SequenceEqual(objects.Select(item => item.Id)) &&
            events.Count(e => e.Stage == SceneDisplayStage.FramePreparationStarted) == 1,
            "匹配完成后必须先发完整 Required，且同一候选只发一次。");
        prepared.Commit();
        Require(display.Objects.Count == 128, "分帧初始化不得丢对象。");
    }
    [Test]
    public async Task Debug_line_preparation_yields_and_only_publishes_the_complete_mesh()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        var lines = Enumerable.Range(0, 128).Select(i => new SceneDebugLine(new(i, 0, 0), new(i, 1, 0), new(1, 0, 0, 1)))
            .ToImmutableArray();
        var next = Frame(2, [], first.Update.Frame, debugLines: lines);
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency / 10000;
        using var partial = display.Prepare(next);
        Require(partial is null && display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null,
            "大量调试线也必须在尾部工作预算内分帧，未完成网格不能接入 D。");
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        using var prepared = await Prepare(display, next);
        prepared.Commit();
        var mesh = display.Root.GetNode<MeshInstance3D>("DebugLines").Mesh;
        using var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        Require(vertices.Length == 256, "跨帧续做不得漏线或重复写入线段。");
        Near(vertices[0], new(0, 0, 0));
        Near(vertices[^1], new(127, 1, 0));
    }

    [Test]
    public async Task Recent_resource_cost_reserves_room_and_an_oversized_item_still_progresses_on_a_fresh_frame()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        foreach (var milliseconds in new[] { 3, 4, 5 })
        {
            var next = Frame(2, [Object(1, Peak(1)), Object(2, Peak(2)), Object(3, Peak(3))], space: (ulong)milliseconds);
            display.ReceiveCandidate(next.Update);
            await ScenePackingTestDriver.StartCandidate(display, TestScene);
            display.Timestamp = () => (display.CandidateProgress(next.Update.Frame)!.Value.Prepared * milliseconds) * Stopwatch.Frequency / 1000;
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            Require(display.CandidateProgress(next.Update.Frame) is (1, 3),
                "准备一项后，剩余1ms不足再开始实测3ms的同类资源；超过4ms的首项也只能做一项。");
            for (var expected = 2; expected <= 3; expected++)
            {
                await new UiTestInput(TestScene).Frames(1);
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                Require(display.CandidateProgress(next.Update.Frame)!.Value.Prepared == expected,
                    "新帧必须允许一项推进，即使同类资源的最近成本超过整帧预算也不能饿死。");
            }
            await new UiTestInput(TestScene).Frames(1);
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            Require(display.PreparedCandidate == next.Update.Frame && ReferenceEquals(display.DisplayedFrame, first),
                "所有资源就绪后才可回执，候选准备不得改动 D。");
            display.CancelCandidate();
            display.Timestamp = () => 0;
        }
    }

    [Test]
    public async Task Candidate_and_active_resource_preparation_share_the_same_remaining_frame_budget()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        var active = Frame(2, [Object(42, Peak(42))], first.Update.Frame);
        await ScenePackingTestDriver.StartFormal(display, active, TestScene);
        var candidate = Frame(3, [Object(1, Peak(1)), Object(2, Peak(2))], space: 2);
        display.ReceiveCandidate(candidate.Update);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        display.Timestamp = () => display.CandidateProgress(candidate.Update.Frame)!.Value.Prepared * Stopwatch.Frequency * 3 / 1000;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(100));
        Require(display.CandidateProgress(candidate.Update.Frame) is (1, 2), "候选只能使用共享4ms额度中的3ms。");
        var activeReady = 0;
        display.Observe = e => { if (e.Stage == SceneDisplayStage.ResourcesPrepared) activeReady++; };
        using var partial = display.Prepare(active);
        Require(partial is null && activeReady == 0 && ReferenceEquals(display.DisplayedFrame, first),
            "候选已用3ms时，活动更新不能再开始预估3ms的资源，即使本次局部计时为零。");
        await new UiTestInput(TestScene).Frames(1);
        using var prepared = await Prepare(display, active);
        Require(activeReady == 1, "下一帧活动更新先行时必须推进，不能被候选饿死。");
        prepared.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, active), "活动更新必须完整提交。");
    }

}

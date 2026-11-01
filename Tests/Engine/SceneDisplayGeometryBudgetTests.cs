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
    public async Task Geometry_preparation_stops_inside_a_visual_and_publishes_only_after_resuming()
    {
        foreach (var candidate in new[] { false, true })
        {
            await new UiTestInput(TestScene).Frames(1);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            using var display = Attach(cache, new FixedContentView());
            var first = Frame(1, []);
            await Commit(display, first);
            var next = Frame(2, [Object(1, Peak(1))], candidate ? null : first.Update.Frame,
                space: candidate ? 2UL : 1UL);
            if (candidate)
            {
                display.ReceiveCandidate(next.Update);
                await ScenePackingTestDriver.StartCandidate(display, TestScene);
            }
            else await ScenePackingTestDriver.StartFormal(display, next, TestScene);
            List<SceneDisplayEvent> events = [];
            display.Observe = events.Add;
            long now = 0;
            display.Timestamp = () => now += Stopwatch.Frequency / 1000;
            if (candidate)
            {
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                Require(display.CandidateProgress(next.Update.Frame) is (0, 1),
                    "单个几何的阶段耗尽预算时，半成品不能计入已完成候选。");
            }
            else
            {
                using var incomplete = display.Prepare(next);
                Require(incomplete is null && !events.Any(item => item.Stage == SceneDisplayStage.ResourcesPrepared),
                    "单个几何的阶段耗尽预算时，不能先发布完整对象资源。");
            }
            Require(ReferenceEquals(display.DisplayedFrame, first) && display.Objects.Count == 0,
                "半成品不得接入显示或拾取索引。");
            display.Timestamp = () => 0;
            await new UiTestInput(TestScene).Frames(1);
            if (candidate)
            {
                await CompleteCandidate(display);
                Require(display.PreparedCandidate == next.Update.Frame, "候选续做后必须给出完整回执。");
            }
            using var prepared = await Prepare(display, next);
            prepared.Commit();
            Require(ReferenceEquals(display.DisplayedFrame, next) && display.Objects.Count == 1 &&
                MeshOf(display.Objects[new(1)]).GetSurfaceCount() == 1,
                "续做完成后原子提交完整网格，仍只有原来的一份 surface。");
        }
    }

    [Test]
    public async Task Cancelling_a_geometry_between_stages_releases_its_unpublished_resources()
    {
        foreach (var candidate in new[] { false, true })
        {
            await new UiTestInput(TestScene).Frames(1);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
            var first = Frame(1, [Object(99, Peak(99))]);
            await Commit(display, first);
            var kept = MeshOf(display.Objects[new(99)]);
            var next = Frame(2, [Object(1, Peak(1))], candidate ? null : first.Update.Frame,
                space: candidate ? 2UL : 1UL);
            if (candidate)
            {
                display.ReceiveCandidate(next.Update);
                await ScenePackingTestDriver.StartCandidate(display, TestScene);
            }
            else await ScenePackingTestDriver.StartFormal(display, next, TestScene);
            long now = 0;
            display.Timestamp = () => now += Stopwatch.Frequency * 3 / 5000;
            if (candidate)
            {
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                Require(display.CandidateProgress(next.Update.Frame) is (0, 1), "取消前应停在未发布的几何内。");
                display.CancelCandidate();
            }
            else
            {
                using var pending = display.Prepare(next);
                Require(pending is null, "取消前不能得到完整帧。");
                display.CancelPreparation();
            }
            Require(ReferenceEquals(display.DisplayedFrame, first) && GodotObject.IsInstanceValid(kept),
                "取消半成品不得释放当前显示的租约。");
            display.Dispose();
            Require(!GodotObject.IsInstanceValid(kept), "关闭须清空当前及半成品的资源缓存。");
        }
    }

    [Test]
    public async Task A_completed_candidate_replaces_the_same_partial_geometry_without_reusing_it_for_the_next_visual()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
        var first = Frame(1, [Object(99, Peak(99))]);
        await Commit(display, first);
        var a = Object(1, Peak(1));
        var b = Object(2, Peak(2));
        var formal = Frame(2, [a, b], first.Update.Frame);
        await ScenePackingTestDriver.StartFormal(display, formal, TestScene);
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency * 3 / 5000;
        using (var partial = display.Prepare(formal)) Require(partial is null, "正式视觉先在内部阶段暂停。");
        display.Timestamp = () => 0;
        var candidate = Frame(3, [a]);
        display.ReceiveCandidate(candidate.Update);
        await CompleteCandidate(display);
        using var prepared = await Prepare(display, formal);
        Require(prepared.Objects[b.Id].Visuals[0].Visual == b.Visuals[0],
            "取用已完成候选时必须清除同一视觉的半成品，不能把它接给下一个视觉。");
        prepared.Commit();
        Require(display.Objects.ContainsKey(a.Id) && display.Objects.ContainsKey(b.Id), "两份完整视觉都必须正确接入。");
    }

    [Test]
    public async Task Cheap_cache_hits_cannot_replace_the_measured_cold_geometry_cost()
    {
        foreach (var milliseconds in new[] { 3, 4, 5 })
            await CheckColdGeometryAdmission(measureCold: true, milliseconds);
    }

    [Test]
    public async Task Unmeasured_cold_geometry_waits_when_this_process_frame_already_did_work()
    {
        await CheckColdGeometryAdmission(measureCold: false);
    }

    private async Task CheckColdGeometryAdmission(bool measureCold, int milliseconds = 3)
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var resources = new SceneResources(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        var mesh = Peak(1);
        var source = new SceneVisual.Geometry(mesh, new(false, 0, 0, false));
        long now = 0;
        var charged = false;
        display.Timestamp = () =>
        {
            // 缓存首次持有完整网格时推进时钟，不依赖内部阶段或 Timestamp 的调用次数。
            if (measureCold && !charged && GeometryIsCached(resources, mesh))
            { now += Stopwatch.Frequency * milliseconds / 1000; charged = true; }
            return now;
        };
        SceneResources.GeometryPreparation? pending = null;
        try
        {
            var unlimited = new SceneDisplay.PreparationSlice(display, 0, null, true);
            using var cold = measureCold ? resources.TryPrepare(source, null, unlimited, ref pending)! : resources.Prepare(source);
            using var hit = resources.TryPrepare(source, null, unlimited, ref pending)!;
            Require(ReferenceEquals(cold.Node.GetChild<MeshInstance3D>(0).Mesh, hit.Node.GetChild<MeshInstance3D>(0).Mesh),
                "廉价路径须确实复用同一份缓存网格。");
            await new UiTestInput(TestScene).Frames(1);
            SpendPreparationTime(display, first, 2);
            display.Timestamp = () => 0;
            var other = Peak(2);
            var otherSource = new SceneVisual.Geometry(other, new(false, 0, 0, false));
            using var denied = resources.TryPrepare(otherSource, null,
                new(display, 0, TimeSpan.FromMilliseconds(4), false), ref pending);
            Require(denied is null && !GeometryIsCached(resources, other), measureCold
                ? "已测冷成本不能被0ms命中覆盖，剩余2ms时必须在生成新网格前停下。"
                : "没有冷成本样本时，本帧已做过工作就必须在生成新网格前停下。");
            await new UiTestInput(TestScene).Frames(1);
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            using var resumed = resources.TryPrepare(otherSource, null,
                new(display, 0, TimeSpan.FromMilliseconds(4), false), ref pending);
            Require(resumed is not null && GeometryIsCached(resources, other),
                "新帧首项必须恢复推进，估算超过整帧预算也不能饿死。");
        }
        finally { pending?.Dispose(); }
    }

    private static bool GeometryIsCached(SceneResources resources, SceneMesh mesh)
    {
        using var lease = resources.AcquireCachedGeometry(mesh);
        return lease is not null;
    }
}

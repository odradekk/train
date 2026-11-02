using Chickensoft.GoDotTest;
using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Candidate_makes_progress_when_formal_preparation_always_runs_first()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var shown = Frame(1, []);
        await Commit(display, shown);
        var candidate = Frame(2, [Object(1, Peak(1))], space: 2);
        display.ReceiveCandidate(candidate.Update);
        for (var frame = 0; frame < 12 && display.IsPreparingCandidate; frame++)
        {
            await new UiTestInput(TestScene).Frames(1);
            using (var active = display.Prepare(shown)) active?.Commit();
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Require(display.PreparedCandidate == candidate.Update.Frame,
            "正式准备固定先调用时，也不能取走候选未知阶段预约的首项，使候选永久等待。");
    }

    [Test]
    public async Task New_displays_reuse_geometry_costs_without_blocking_the_world_for_whole_frames()
    {
        using var costs = new GeometryCostScope();
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var world = Attach(cache, new FixedContentView(), completeInitialFrame: true);
        var shown = Frame(1, [Object(99, Peak(99))]);
        await Commit(world, shown);
        using var previewCache = new AssetResourceCache(dispatcher);
        for (var icon = 0; icon < 3; icon++)
        {
            using var preview = Attach(previewCache, new FixedContentView());
            var incoming = Frame(2, [Object(1, Peak(icon))]);
            SceneDisplay.PreparedFrame? prepared = null;
            for (var frame = 0; frame < 12 && prepared is null; frame++)
            {
                await new UiTestInput(TestScene).Frames(1);
                using var active = world.Prepare(shown);
                Require(active is not null,
                    "世界已取得几何成本样本后，新预览不能再次预约首项，整帧挡住世界显示。");
                active!.Commit();
                prepared = preview.Prepare(incoming);
                await preview.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30));
            }
            using (prepared)
            {
                Require(prepared is not null, "新显示必须沿用已有成本样本，在有限帧内完成准备。");
                prepared!.Commit();
            }
            Require(ReferenceEquals(preview.DisplayedFrame, incoming) && preview.Objects.Count == 1,
                "新显示须一次接入完整几何。");
        }
    }

    [Test]
    public async Task Geometry_preparation_stops_inside_a_visual_and_publishes_only_after_resuming()
    {
        foreach (var candidate in new[] { false, true })
        {
            using var costs = new GeometryCostScope();
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

    [Test]
    public async Task Incremental_shift_removal_and_multiple_visuals_match_uninterrupted_preparation()
    {
        using var costs = new GeometryCostScope();
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var sliced = Attach(cache, new FixedContentView());
        using var whole = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh(), new(10, 0, 0)),
            Object(2, Mesh(), new(2, 0, 0), new(1)), Object(3, Mesh())]);
        await Commit(sliced, first);
        await Commit(whole, first);
        var multiple = Object(4, Peak(2), new(7, 0, 0)) with
        {
            Visuals = [new SceneVisual.Geometry(Peak(2), new(false, 0, 0, false)),
                new SceneVisual.Geometry(Peak(3), new(false, 0, 0, false)),
                new SceneVisual.Geometry(Peak(4), new(false, 0, 0, false))]
        };
        var delta = Frame(2, [multiple, Object(5, Peak(5), new(9, 0, 0))], first.Update.Frame,
            shift: new(-100, 0, 0), origin: new(100, 0, 0), removed: [new(3)],
            fog: [new(new(-10), new(10))]);
        await ScenePackingTestDriver.StartFormal(sliced, delta, TestScene);
        long timestamp = 0;
        sliced.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var calls = 0;
        SceneDisplay.PreparedFrame? prepared = null;
        await AdvancePreparation(() =>
        {
            prepared = sliced.Prepare(delta);
            if (prepared is not null) return;
            calls++;
            Require(ReferenceEquals(sliced.DisplayedFrame, first) && sliced.Objects.ContainsKey(new(3)),
                "断点准备不能提前发布移除或新对象。");
            Near(sliced.Objects[new(1)].Root.GlobalPosition, new(10, 0, 0));
        }, () => prepared is not null);
        Require(calls >= 3, "多视觉与雾必须跨调用恢复。");
        using (prepared) prepared!.Commit();
        // 分步显示的一秒假时钟样本不能充当零耗时参照显示的估算。
        using var measuredCosts = new GeometryCostScope(TimeSpan.Zero);
        await ScenePackingTestDriver.StartFormal(whole, delta, TestScene);
        using (var uninterrupted = whole.Prepare(delta))
        {
            Require(uninterrupted is not null, "已有阶段样本后，零耗时准备必须一次完成同一增量。");
            uninterrupted!.Commit();
        }
        Require(sliced.Objects.Keys.ToHashSet().SetEquals(whole.Objects.Keys) && !sliced.Objects.ContainsKey(new(3)),
            "分步准备与一次完成必须具有同一对象集。");
        foreach (var (id, actual) in sliced.Objects)
        {
            var expected = whole.Objects[id];
            Near(actual.Root.GlobalPosition, expected.Root.GlobalPosition);
            Require(actual.Source == expected.Source && actual.Visuals.Length == expected.Visuals.Length,
                "恢复必须保留来源、顺序及完整视觉。");
            for (var index = 0; index < actual.Visuals.Length; index++)
            {
                using var arrays = actual.Visuals[index].Node.GetChild<MeshInstance3D>(0).Mesh.SurfaceGetArrays(0);
                using var otherArrays = expected.Visuals[index].Node.GetChild<MeshInstance3D>(0).Mesh.SurfaceGetArrays(0);
                Require(arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array().SequenceEqual(
                    otherArrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array()), "恢复不得重复或错置视觉网格。");
            }
            var hit = new SceneHit(delta.Update.Frame, id, new PickSubject.Terrain(new WorldLocation.Node(new(1))),
                default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
            Require(sliced.CanSelect(hit) && whole.CanSelect(hit), "已提交增量中的对象必须保持相同拾取兼容性。");
        }
        Near(sliced.Objects[new(1)].Root.GlobalPosition, new(-90, 0, 0));
        Near(sliced.Objects[new(2)].Root.GlobalPosition, new(-88, 0, 0));
        var fog = sliced.Root.GetNode<Node3D>("LoadingFog");
        var otherFog = whole.Root.GetNode<Node3D>("LoadingFog");
        Require(fog.GetChildCount() == 1 && otherFog.GetChildCount() == 1, "雾区域不能在恢复中丢失或重复。");
        Near(fog.GetChild<MeshInstance3D>(0).Position, otherFog.GetChild<MeshInstance3D>(0).Position);
    }

    private async Task CheckColdGeometryAdmission(bool measureCold, int milliseconds = 3)
    {
        using var costs = new GeometryCostScope();
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
            using var cold = measureCold ? resources.TryPrepare(source, null,
                new(display, 0, null, true), ref pending)! : null;
            using var hit = measureCold ? resources.TryPrepare(source, null,
                new(display, 0, null, true), ref pending)! : null;
            if (measureCold)
                Require(ReferenceEquals(cold!.Node.GetChild<MeshInstance3D>(0).Mesh, hit!.Node.GetChild<MeshInstance3D>(0).Mesh),
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
            PreparedVisual? resumed = null;
            var allowedFrames = measureCold ? 1 : 4;
            for (var frame = 0; frame < allowedFrames && resumed is null; frame++)
            {
                await new UiTestInput(TestScene).Frames(1);
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                resumed = resources.TryPrepare(otherSource, null,
                    new(display, 0, TimeSpan.FromMilliseconds(4), false), ref pending);
            }
            using (resumed)
                Require(resumed is not null && GeometryIsCached(resources, other),
                    measureCold ? $"已测冷成本为{milliseconds}ms时，必须恰好在下一帧首项完成续做。"
                    : "后续帧首项必须恢复推进，未知阶段不能饿死。");
        }
        finally { pending?.Dispose(); }
    }

    private static bool GeometryIsCached(SceneResources resources, SceneMesh mesh)
    {
        using var lease = resources.AcquireCachedGeometry(mesh);
        return lease is not null;
    }
}

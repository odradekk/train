using System.Diagnostics;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Rendering;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Known_packing_collection_cost_defers_acquisition_and_cancellation_releases_only_owned_meshes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        using var resources = new SceneResources(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[new(1)].Visuals[0];
        var baseline = Frame(1, []);
        await Commit(burner, baseline);
        var costs = new ScenePackingPreparation.Costs();
        await new UiTestInput(TestScene).Frames(1);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        long trainingTime = 0;
        display.Timestamp = () => trainingTime += Stopwatch.Frequency * 3 / 1000;
        using (var training = new ScenePackingPreparation(resources, costs))
            Require(training.Collect(Object(20, Peak(20)).Visuals[0],
                new(display, 0, TimeSpan.FromMilliseconds(4), false)), "首项收集必须完成并记录实测成本。");
        using var packing = new ScenePackingPreparation(resources, costs);
        var rejectedSource = Object(2, Peak(2)).Visuals[0];
        using var rejected = resources.Prepare(rejectedSource);
        var rejectedMesh = rejected.Node.GetChild<MeshInstance3D>(0).Mesh;
        await new UiTestInput(TestScene).Frames(1);
        SpendPreparationTime(burner, baseline, 2);
        display.Timestamp = () => 0;
        var slice = new SceneDisplay.PreparationSlice(display, 0, TimeSpan.FromMilliseconds(4), false);
        Require(!packing.Collect(rejectedSource, slice),
            "剩余2ms不足已知3ms收集成本，必须保留未收集状态。");
        rejected.Dispose();
        Require(!GodotObject.IsInstanceValid(rejectedMesh),
            "拒绝收集不能取得缓存租约，原使用方释放后网格必须退役。");
        await new UiTestInput(TestScene).Frames(1);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        var acceptedSource = rejectedSource;
        using var accepted = resources.Prepare(acceptedSource);
        var acceptedMesh = accepted.Node.GetChild<MeshInstance3D>(0).Mesh;
        Require(packing.Collect(acceptedSource, slice), "下一帧第一项必须允许收集并取得缓存租约。");
        accepted.Dispose();
        Require(GodotObject.IsInstanceValid(acceptedMesh), "成功收集后准备必须独立保活缓存网格。");
        packing.Dispose();
        packing.Dispose();
        Require(!GodotObject.IsInstanceValid(acceptedMesh), "取消必须立即且恰好释放已取得的租约。");
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[new(1)].Visuals[0], shown) && shown.Node.IsInsideTree(),
            "拒绝收集、恢复和取消均不能改变D及其视觉资源。");
    }

    [Test]
    public async Task Known_packing_start_cost_defers_background_work_until_the_next_frame()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        using var resources = new SceneResources(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[new(1)].Visuals[0];
        var baseline = Frame(1, []);
        await Commit(burner, baseline);
        display.Timestamp = () => 0;
        var costs = new ScenePackingPreparation.Costs();
        await new UiTestInput(TestScene).Frames(1);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        using (var training = new ScenePackingPreparation(resources, costs))
        {
            Require(training.Collect(Object(20, Peak(20)).Visuals[0], new(display, 0, null, true)),
                "训练作业必须先收集真实网格。");
            long trainingTime = 0;
            display.Timestamp = () => trainingTime += Stopwatch.Frequency * 3 / 1000;
            Require(training.Start(null, PackedSceneMesh.Create,
                new(display, 0, TimeSpan.FromMilliseconds(4), false), out var trainingTail),
                "首项启动必须完成并记录实测成本。");
            await trainingTail!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        display.Timestamp = () => 0;
        using var packing = new ScenePackingPreparation(resources, costs);
        await new UiTestInput(TestScene).Frames(1);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        var slice = new SceneDisplay.PreparationSlice(display, 0, TimeSpan.FromMilliseconds(4), false);
        Require(packing.Collect(Object(2, Peak(2)).Visuals[0], slice), "未缓存网格必须先完成输入收集。");
        await new UiTestInput(TestScene).Frames(1);
        SpendPreparationTime(burner, baseline, 2);
        var packed = 0;
        PackedSceneMesh Pack(Train.Scenes.Contracts.SceneMesh mesh, CancellationToken token)
        {
            Interlocked.Increment(ref packed);
            return PackedSceneMesh.Create(mesh, token);
        }
        Require(!packing.Start(null, Pack, slice, out var rejectedTail) &&
            rejectedTail is null && packing.Job is null && !packing.Started && Volatile.Read(ref packed) == 0,
            "剩余2ms不足已知3ms启动成本，不能创建或启动后台作业。");
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[new(1)].Visuals[0], shown) && shown.Node.IsInsideTree(),
            "拒绝启动必须保留D及原视觉资源。");
        await new UiTestInput(TestScene).Frames(1);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(packing.Start(null, Pack, slice, out var tail) && tail is not null,
            "下一帧第一项必须允许启动此前推迟的真实后台作业。");
        await tail!.WaitAsync(TimeSpan.FromSeconds(30));
        Require(Volatile.Read(ref packed) == 1, "恢复后每份输入只能真正打包一次。");
        packing.Dispose();
        Require(ReferenceEquals(display.DisplayedFrame, first) && shown.Node.IsInsideTree(),
            "取消已结束的未提交作业也不能改变D。");
    }

    [Test]
    public async Task Completed_packing_release_cost_survives_candidate_completion_and_reserves_active_budget() =>
        await CompletedPackingReleaseReservesBudget(3);

    [Test]
    public async Task Oversized_completed_packing_release_can_be_the_first_step_of_a_fresh_frame() =>
        await CompletedPackingReleaseReservesBudget(5);

    private async Task CompletedPackingReleaseReservesBudget(int milliseconds)
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[new(1)].Visuals[0];
        var baseline = Frame(1, []);
        await Commit(burner, baseline);
        long now = 0;
        var releases = 0;
        display.Timestamp = () => Interlocked.Read(ref now);
        display.PackMesh = (mesh, token) =>
        {
            // 完成后的作业仍持有取消源；正常Dispose同步执行该回调。
            _ = token.Register(() =>
            {
                Interlocked.Increment(ref releases);
                Interlocked.Add(ref now, Stopwatch.Frequency * milliseconds / 1000);
            });
            return PackedSceneMesh.Create(mesh, token);
        };
        var training = Frame(2, [Object(2, Peak(2))], space: 2);
        display.ReceiveCandidate(training.Update);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        if (display.PreparedCandidate is null)
        {
            await new UiTestInput(TestScene).Frames(1);
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        }
        Require(display.PreparedCandidate == training.Update.Frame && releases == 1,
            "候选完成时必须正常释放已结束作业，并实测同步释放成本。");
        display.CancelCandidate();
        var active = Frame(3, [Object(3, Peak(3))], first.Update.Frame);
        await ScenePackingTestDriver.StartFormal(display, active, TestScene);
        SpendPreparationTime(burner, baseline, 2);
        using (var deferred = display.Prepare(active))
        {
            Require(deferred is null && releases == 1,
                "候选作业完成后留下的释放成本必须供活动作业使用；剩余2ms不得开始释放。");
        }
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[new(1)].Visuals[0], shown) && shown.Node.IsInsideTree() &&
            GodotObject.IsInstanceValid(shown.Node),
            "拒绝释放准入时必须保留当前D及其视觉所有权。");
        await new UiTestInput(TestScene).Frames(1);
        using var resumed = display.Prepare(active);
        Require(releases == 2,
            "下一帧必须继续活动准备并释放作业，不能因候选留下的成本历史而饿死。");
        if (resumed is not null) resumed.Commit();
        else
        {
            await new UiTestInput(TestScene).Frames(1);
            using var prepared = display.Prepare(active);
            Require(prepared is not null, "超预算释放后的最终收尾必须在下一帧完成。");
            prepared!.Commit();
        }
        Require(ReferenceEquals(display.DisplayedFrame, active), "正常释放完成后才能提交新的D。");
    }
}

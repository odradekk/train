using System.Collections.Immutable;
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
    public async Task Cancelling_packing_input_collection_releases_its_cached_mesh_and_keeps_D()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[new(1)].Visuals[0];
        using var resources = new SceneResources(cache, new FixedContentView());
        var source = Object(2, Peak(2)).Visuals[0];
        using var original = resources.Prepare(source);
        var mesh = original.Node.GetChild<MeshInstance3D>(0).Mesh;
        using var packing = new ScenePackingPreparation(resources, new());
        var slice = new SceneDisplay.PreparationSlice(display, 0, null, true);
        Require(packing.Collect(source, slice) && packing.Collect(source, slice), "收集必须接收重复网格并只保留一次租约。");
        original.Dispose();
        Require(GodotObject.IsInstanceValid(mesh), "收集后未启动的准备必须独立保活缓存网格。");
        packing.Dispose();
        packing.Dispose();
        Require(!GodotObject.IsInstanceValid(mesh) &&
            resources.AcquireCachedGeometry(((Train.Scenes.Contracts.SceneVisual.Geometry)source).Mesh) is null,
            "中途取消收集必须释放唯一缓存租约，重复Dispose不得遗漏或再次释放。");
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[new(1)].Visuals[0], shown) && shown.Node.IsInsideTree(),
            "中途取消打包输入不能改变D及原视觉。");
    }

    [Test]
    public async Task Disposing_partially_released_packing_preserves_other_owners_and_releases_each_remaining_lease()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[new(1)].Visuals[0];
        using var resources = new SceneResources(cache, new FixedContentView());
        var a = Object(2, Peak(2)).Visuals[0];
        var b = Object(3, Peak(3)).Visuals[0];
        using var originalA = resources.Prepare(a);
        using var originalB = resources.Prepare(b);
        var meshA = originalA.Node.GetChild<MeshInstance3D>(0).Mesh;
        var meshB = originalB.Node.GetChild<MeshInstance3D>(0).Mesh;
        var geometryA = ((Train.Scenes.Contracts.SceneVisual.Geometry)a).Mesh;
        using var sentinel = resources.AcquireCachedGeometry(geometryA)!;
        using var packing = new ScenePackingPreparation(resources, new());
        var unlimited = new SceneDisplay.PreparationSlice(display, 0, null, true);
        Require(packing.Collect(a, unlimited) && packing.Collect(b, unlimited) &&
            packing.Start(null, PackedSceneMesh.Create, unlimited, out _), "准备必须独立接收两个缓存租约。");
        originalA.Dispose();
        originalB.Dispose();
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency * 3 / 1000;
        var slice = new SceneDisplay.PreparationSlice(display, 0, TimeSpan.FromMilliseconds(4), false);
        Require(!packing.Release(slice) && !GodotObject.IsInstanceValid(meshB) && GodotObject.IsInstanceValid(meshA),
            "尾部释放只推进一个缓存租约后应按预算暂停，不能释放余项。");
        packing.Dispose();
        packing.Dispose();
        Require(GodotObject.IsInstanceValid(meshA) && ReferenceEquals(sentinel.Value, meshA),
            "取消余下释放不能多退其他使用方的租约。");
        sentinel.Dispose();
        Require(!GodotObject.IsInstanceValid(meshA) && resources.AcquireCachedGeometry(geometryA) is null,
            "最后使用方释放后网格必须退役，不能遗留收集保活租约。");
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[new(1)].Visuals[0], shown) && shown.Node.IsInsideTree(),
            "尾部释放与中途取消不能改动D。");
    }

    [Test]
    public async Task Replacing_a_candidate_during_packing_collection_discards_the_old_cursor()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[new(1)].Visuals[0];
        var sources = Enumerable.Range(2, 24).Select(i => Object((ulong)i, Peak(i))).ToImmutableArray();
        var candidate = Frame(2, sources, space: 2);
        List<Train.Scenes.Contracts.SceneMesh> packed = [];
        display.PackMesh = (mesh, token) => { lock (packed) packed.Add(mesh); return PackedSceneMesh.Create(mesh, token); };
        display.ReceiveCandidate(candidate.Update);
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency * 3 / 1000;
        for (var frame = 0; frame < 3; frame++)
        {
            await new UiTestInput(TestScene).Frames(1);
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        }
        Require(packed.Count == 0 && display.PendingCandidateVisuals == 24 && display.PreparedCandidate is null,
            "候选必须仍停在跨帧输入收集，不能提前启动后台作业。");
        display.CancelCandidate();
        var replacement = Frame(3, [Object(100, Peak(100)), Object(101, Peak(101))], space: 3);
        display.ReceiveCandidate(replacement.Update);
        display.Timestamp = () => 0;
        await CompleteCandidate(display);
        var expected = replacement.Update.Upserts.Select(item => ((Train.Scenes.Contracts.SceneVisual.Geometry)item.Visuals[0]).Mesh);
        Require(packed.SequenceEqual(expected) && display.PreparedCandidate == replacement.Update.Frame,
            "替代候选只能收集并打包自己的网格各一次，不能恢复已取消游标。");
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[new(1)].Visuals[0], shown) && shown.Node.IsInsideTree(),
            "候选中途替换及完成不能发布新的D。");
    }
}

using Chickensoft.GoDotTest;
using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    private async Task RequireRetiredCacheReleased(SceneDisplay display, ISceneFrame frame, Godot.Mesh mesh)
    {
        await new UiTestInput(TestScene).Frames(1);
        using (var pending = display.Prepare(frame)) { }
        Require(!GodotObject.IsInstanceValid(mesh), "晋升取消候选租约且旧帧退役后，缓存网格必须释放。");
    }

    [Test]
    public async Task New_space_commit_detaches_retired_objects_and_keeps_resources_until_a_later_frame()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var kept = Object(2, Peak(2));
        var first = Frame(1, [Object(1, Peak(1)), kept]);
        await Commit(display, first);
        var retired = display.Objects[new(1)];
        var retiredMesh = MeshOf(retired);
        var keptVisual = display.Objects[new(2)].Visuals[0];
        var keptMesh = MeshOf(display.Objects[new(2)]);
        var next = Frame(2, [kept, Object(3, Peak(3))], space: 2);
        var oldHit = new SceneHit(first.Update.Frame, new(1),
            new PickSubject.Terrain(new WorldLocation.Node(new(1))), default, GridFace.PositiveY,
            default, System.Numerics.Vector3.UnitY, 1, []);
        using (var prepared = await Prepare(display, next))
        {
            Require(ReferenceEquals(display.DisplayedFrame, first) && retired.Root.IsInsideTree() &&
                !prepared.Objects[new(3)].Root.IsInsideTree(), "新空间准备完成前仍只显示旧帧。");
            prepared.Commit();
        }
        Require(GodotObject.IsInstanceValid(retired.Root) && GodotObject.IsInstanceValid(retiredMesh),
            "提交只摘除旧对象，释放必须留给后续进程帧。");
        Require(ReferenceEquals(display.DisplayedFrame, next) && display.Objects[new(3)].Root.IsInsideTree() &&
            !retired.Root.IsInsideTree() && !retired.Visuals[0].Node.IsInsideTree(),
            "提交必须原子接入新空间，并立即摘掉旧对象和旧视觉。");
        Require(!display.CanSelect(oldHit) && display.CanSelect(oldHit with { Frame = next.Update.Frame, Object = new(3) }),
            "提交后只接受新显示对象的拾取。");
        Require(ReferenceEquals(display.Objects[new(2)].Visuals[0], keptVisual) &&
            GodotObject.IsInstanceValid(keptVisual.Node) && keptVisual.Node.IsInsideTree() &&
            GodotObject.IsInstanceValid(keptMesh),
            "新空间保留的视觉和共享租约不能提前释放。");
        using (var sameFrame = display.Prepare(Frame(3, [], next.Update.Frame, space: 2))) { }
        Require(GodotObject.IsInstanceValid(retiredMesh), "提交所在帧不得开始退役释放。");
        await new UiTestInput(TestScene).Frames(1);
        using (var later = display.Prepare(Frame(4, [], next.Update.Frame, space: 2))) { }
        Require(!GodotObject.IsInstanceValid(retired.Root) && !GodotObject.IsInstanceValid(retiredMesh) &&
            GodotObject.IsInstanceValid(keptMesh), "后续准备必须释放旧资源，并保留当前显示所持租约。");
    }

    [Test]
    public async Task Retired_visual_resources_resume_inside_the_shared_process_frame_budget()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        await Commit(display, Frame(1, [Object(1, Peak(1))]));
        var retired = display.Objects[new(1)];
        var mesh = MeshOf(retired);
        var released = 0;
        for (var index = 0; index < 3; index++)
            retired.Visuals[0].Own(new AssetLease<object>(new(), () => released++));
        var next = Frame(2, [Object(2, Peak(2))], space: 2);
        await Commit(display, next);
        display.Timestamp = () => released * Stopwatch.Frequency * 3 / 1000;
        List<TimeSpan> measured = [];
        display.Measure = (_, elapsed, _) => measured.Add(elapsed);
        for (var expected = 1; expected <= 3; expected++)
        {
            await new UiTestInput(TestScene).Frames(1);
            using (var pending = display.Prepare(next)) { }
            Require(released == expected, "每次3ms的独立释放必须跨帧续做，不能整组Dispose。");
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            using (var repeated = display.Prepare(next)) { }
            Require(released == expected, "活动和候选及同帧重入必须共享已消耗的预算。");
            Require(measured.All(elapsed => elapsed <= TimeSpan.FromMilliseconds(4)),
                "退役消耗必须计入Prepare且不超确定性的共享4ms预算。");
        }
        display.Timestamp = () => 0;
        await RequireRetiredCacheReleased(display, next, mesh);
        Require(!GodotObject.IsInstanceValid(retired.Root), "资源释放完成后旧对象根也必须释放。");
    }

    [Test]
    public async Task Rapid_space_switches_wait_for_the_bounded_retirement_and_close_releases_everything()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        await Commit(display, Frame(1, [Object(1, Peak(1))]));
        var firstMesh = MeshOf(display.Objects[new(1)]);
        var second = Frame(2, [Object(2, Peak(2))], space: 2);
        await Commit(display, second);
        var secondMesh = MeshOf(display.Objects[new(2)]);
        var third = Frame(3, [Object(3, Peak(3))], space: 3);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var pending = display.Prepare(third);
            Require(pending is null && ReferenceEquals(display.DisplayedFrame, second) &&
                display.Objects.Count == 1 && GodotObject.IsInstanceValid(firstMesh),
                "前批退役未清空时不能接入下一空间或积累新的退役批次。");
        }
        await Commit(display, third);
        var thirdMesh = MeshOf(display.Objects[new(3)]);
        Require(!GodotObject.IsInstanceValid(firstMesh) && GodotObject.IsInstanceValid(secondMesh),
            "第三空间只能在第一空间清空后接入，第二空间刚退役不能当帧释放。");
        display.Dispose();
        Require(!GodotObject.IsInstanceValid(secondMesh) && !GodotObject.IsInstanceValid(thirdMesh),
            "关闭必须同时清空待退役资源和当前显示，不依赖之后还有Prepare。");
    }

    [Test]
    public async Task Retired_fog_boxes_detach_without_releasing_the_shared_root_and_material()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        SceneBounds kept = new(new(0, 0, 0), new(1, 1, 1));
        SceneBounds removed = new(new(10, 0, 0), new(11, 1, 1));
        SceneBounds added = new(new(20, 0, 0), new(21, 1, 1));
        await Commit(display, Frame(1, [], fog: [kept, removed]));
        var root = display.Root.GetNode<Node3D>("LoadingFog");
        var oldBoxes = root.GetChildren().OfType<MeshInstance3D>().ToArray();
        var removedBox = oldBoxes.Single(node => node.Position.X > 10);
        var removedMesh = removedBox.Mesh;
        var material = removedBox.MaterialOverride;
        var next = Frame(2, [], fog: [kept, added], space: 2);
        await Commit(display, next);
        var currentMeshes = root.GetChildren().OfType<MeshInstance3D>().Select(node => node.Mesh).ToArray();
        Require(root.IsInsideTree() && root.GetChildCount() == 2 && !removedBox.IsInsideTree() &&
            GodotObject.IsInstanceValid(removedMesh) && GodotObject.IsInstanceValid(material),
            "旧雾只摘掉未保留矩形，共享root和material必须随新组继续存活。");
        using (var blocked = display.Prepare(Frame(3, [], fog: [added], space: 3)))
            Require(blocked is null, "下一代雾必须等前代退役清空，不能倒序释放共享所有者。");
        await new UiTestInput(TestScene).Frames(1);
        using (var later = display.Prepare(next)) { }
        Require(!GodotObject.IsInstanceValid(removedBox) && !GodotObject.IsInstanceValid(removedMesh) &&
            GodotObject.IsInstanceValid(material) && root.IsInsideTree() &&
            currentMeshes.All(GodotObject.IsInstanceValid), "后续预算只释放旧代剩余box，当前雾仍完整可见。");
        await Commit(display, Frame(4, [], fog: [added], space: 3));
        display.Dispose();
        Require(!GodotObject.IsInstanceValid(removedMesh) && !GodotObject.IsInstanceValid(root) &&
            !GodotObject.IsInstanceValid(material) && currentMeshes.All(mesh => !GodotObject.IsInstanceValid(mesh)),
            "关闭先清旧雾再清当前拥有root/material的组，全部资源最终释放。");
    }
}

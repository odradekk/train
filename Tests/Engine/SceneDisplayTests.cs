using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Engine.Tests.Assets;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Train.Engine.Tests;

public class SceneDisplayTests(Node testScene) : TestClass(testScene)
{
    private static readonly SceneSessionId Session = new(Guid.Parse("7dd364aa-df8f-4144-b345-fc130ea11ca9"));

    [Test]
    public void Geometry_groups_reuse_visuals_across_insertions_and_removals_without_changing_the_display_before_commit()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var a = new SceneVisual.Geometry(Peak(41), new(false, 0, 0, false));
        var b = new SceneVisual.Geometry(Peak(42), a.Surface);
        var inserted = new SceneVisual.Geometry(Peak(43), a.Surface);
        var original = Object(1, a.Mesh) with { Visuals = [a, b] };
        var first = Frame(1, [original]);
        Commit(display, first);
        var old = display.Objects[original.Id].Visuals;
        var oldMeshes = old.Select(visual => visual.Node.GetChild<MeshInstance3D>(0).Mesh).ToArray();
        var repainted = a with { Surface = new(true, 17, 0.2f, false) };
        var next = Frame(2, [original with { Visuals = [inserted, repainted, b] }], first.Update.Frame);
        display.UpdatePoses(next);
        var hit = new SceneHit(next.Update.Frame, original.Id, new PickSubject.Terrain(new WorldLocation.Node(new(1))),
            default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
        Require(!display.CanSelect(hit), "新分组尚未显示时不能接受该对象的最新命中。");
        using (var cancelled = Prepare(display, next))
        {
            Require(ReferenceEquals(cancelled.Objects[original.Id].Visuals[1], old[0]) &&
                ReferenceEquals(cancelled.Objects[original.Id].Visuals[2], old[1]), "插入分组必须按网格身份复用后移的视觉。");
            Require(ReferenceEquals(old[0].Visual, a) && old.All(visual => visual.Node.IsInsideTree()) &&
                ReferenceEquals(display.DisplayedFrame, first), "完整提交前不能更新旧视觉状态或摘除旧节点。");
            var added = cancelled.Objects[original.Id].Visuals[0];
            var addedMesh = added.Node.GetChild<MeshInstance3D>(0).Mesh;
            cancelled.Dispose();
            Require(!GodotObject.IsInstanceValid(added.Node) && !GodotObject.IsInstanceValid(addedMesh) &&
                oldMeshes.All(GodotObject.IsInstanceValid), "取消完整候选只释放新增资源，保留复用网格。");
        }
        Commit(display, next);
        var addedVisual = display.Objects[original.Id].Visuals[0];
        var addedGeometry = addedVisual.Node.GetChild<MeshInstance3D>(0).Mesh;
        Require(ReferenceEquals(display.Objects[original.Id].Visuals[1], old[0]) &&
            ReferenceEquals(old[0].Visual, repainted) && ReferenceEquals(oldMeshes[0], old[0].Node.GetChild<MeshInstance3D>(0).Mesh),
            "提交必须保留同一 PreparedVisual 和 ArrayMesh，并更新当前表面状态。");
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        display.PackMesh = (_, _) => throw new InvalidOperationException("只重排已有网格不应重新打包。");
        var removed = Frame(3, [original with { Visuals = [b, repainted] }], next.Update.Frame);
        display.UpdatePoses(removed);
        Require(display.CanSelect(hit with { Frame = removed.Update.Frame }), "全部几何已经显示时，分组重排仍可拾取。");
        Commit(display, removed);
        Require(ReferenceEquals(display.Objects[original.Id].Visuals[0], old[1]) &&
            ReferenceEquals(display.Objects[original.Id].Visuals[1], old[0]) && oldMeshes.All(GodotObject.IsInstanceValid),
            "删除分组和交换顺序不能重建保留的视觉或网格。");
        Require(!GodotObject.IsInstanceValid(addedVisual.Node) && !GodotObject.IsInstanceValid(addedGeometry),
            "未匹配的旧视觉必须按身份退役，不能按新索引误释放保留资源。");
        Require(events.Single(item => item.Stage == SceneDisplayStage.FramePreparationStarted).ResourceObjects is { IsEmpty: true } &&
            events.All(item => item.Stage != SceneDisplayStage.ResourcesPrepared), "只重排复用视觉不能声明有新资源或发出资源准备事件。");
    }

    [Test]
    public void Repeated_meshes_match_visuals_once_and_non_geometry_still_matches_by_index()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var geometry = new SceneVisual.Geometry(Peak(44), new(false, 0, 0, false));
        var wheelset = new SceneVisual.Wheelset(2, 1, 0.5, 0);
        var original = Object(1, geometry.Mesh) with { Visuals = [geometry, wheelset, geometry] };
        var first = Frame(1, [original]);
        Commit(display, first);
        var old = display.Objects[original.Id].Visuals;
        var mesh = old[0].Node.GetChild<MeshInstance3D>(0).Mesh;
        Require(!ReferenceEquals(old[0], old[2]), "重复网格必须有各自独立的视觉。");
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        var next = Frame(2, [original with { Visuals = [geometry, geometry, wheelset, geometry] }], first.Update.Frame);
        display.UpdatePoses(next);
        var hit = new SceneHit(next.Update.Frame, original.Id, new PickSubject.Terrain(new WorldLocation.Node(new(1))),
            default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
        Require(!display.CanSelect(hit), "同一旧视觉不能重复匹配，新增的第三个实例尚未显示。");
        using (var prepared = Prepare(display, next))
        {
            var visuals = prepared.Objects[original.Id].Visuals;
            Require(ReferenceEquals(visuals[0], old[0]) && ReferenceEquals(visuals[1], old[2]) &&
                visuals.Distinct<PreparedVisual>(ReferenceEqualityComparer.Instance).Count() == 4,
                "重复网格按旧顺序一对一匹配，每个目标视觉必须独立。");
            Require(!ReferenceEquals(visuals[2], old[1]) && GodotObject.IsInstanceValid(old[1].Node),
                "非几何视觉移到其他索引时不能复用，旧资源必须保留到提交。");
            Require(new[] { 0, 1, 3 }.All(index => ReferenceEquals(mesh, visuals[index].Node.GetChild<MeshInstance3D>(0).Mesh)),
                "独立视觉仍应共享同一 ArrayMesh 租约。");
            prepared.Commit();
        }
        Require(!GodotObject.IsInstanceValid(old[1].Node), "提交必须释放索引改变而未匹配的轮对资源。");
        Require(events.Single(item => item.Stage == SceneDisplayStage.FramePreparationStarted).ResourceObjects is { } required &&
            required.SequenceEqual(new[] { original.Id }) && events.Count(item => item.Stage == SceneDisplayStage.ResourcesPrepared) == 1,
            "新增重复实例及移位轮对必须使整个对象在全部资源就绪后仅记录一次资源准备。");
        var current = display.Objects[original.Id].Visuals;
        var rotated = wheelset with { RotationRadians = 0.5 };
        Commit(display, Frame(3, [original with { Visuals = [geometry, geometry, rotated] }], next.Update.Frame));
        Require(ReferenceEquals(display.Objects[original.Id].Visuals[2], current[2]) &&
            ReferenceEquals(display.Objects[original.Id].Visuals[2].Visual, rotated) &&
            !GodotObject.IsInstanceValid(current[3].Node) && GodotObject.IsInstanceValid(mesh),
            "非几何同索引仍按 CanUpdate 复用；删除重复实例不能释放其余使用者的网格。");
    }

    [Test]
    public void Cancelling_partially_prepared_reordered_geometry_releases_only_new_resources_and_rejects_late_packing()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var a = new SceneVisual.Geometry(Peak(45), new(false, 0, 0, false));
        var b = new SceneVisual.Geometry(Peak(46), a.Surface);
        var original = Object(1, a.Mesh) with { Visuals = [a, b] };
        var first = Frame(1, [original]);
        Commit(display, first);
        var old = display.Objects[original.Id].Visuals;
        var oldMeshes = old.Select(visual => visual.Node.GetChild<MeshInstance3D>(0).Mesh).ToArray();
        var added = new SceneVisual.Geometry(Peak(47), a.Surface);
        var blocked = new SceneVisual.Geometry(Peak(48), a.Surface);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        display.PackMesh = (mesh, _) =>
        {
            if (ReferenceEquals(mesh, blocked.Mesh))
            {
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(30)), "未释放被取消的后续打包。");
            }
            return PackedSceneMesh.Create(mesh, CancellationToken.None);
        };
        var pending = Frame(2, [original with { Visuals = [b, added, blocked] }], first.Update.Frame);
        var before = OrphanIds();
        Require(display.Prepare(pending, TimeSpan.Zero) is null, "零预算只启动打包，不提前提交。");
        var packing = display.PreparationPacking;
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(30)), "后续网格必须在门控处等待。");
            Require(display.Prepare(pending) is null, "首个新增资源上传后必须等待后续网格。");
            var addedNode = NewOrphanRoots(before).Single(node => node.GetChildCount() == 1 && node.GetChild(0) is MeshInstance3D);
            var addedMesh = addedNode.GetChild<MeshInstance3D>(0).Mesh;
            Require(old.All(visual => visual.Node.IsInsideTree()) && oldMeshes.All(GodotObject.IsInstanceValid) &&
                ReferenceEquals(display.DisplayedFrame, first), "分步准备不能更改或释放旧显示组。");
            display.CancelPreparation();
            Require(!GodotObject.IsInstanceValid(addedNode) && !GodotObject.IsInstanceValid(addedMesh) &&
                oldMeshes.All(GodotObject.IsInstanceValid), "取消必须释放已上传的新资源，保留重排引用和待替换的旧资源。");
        }
        finally { release.Set(); }
        packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        display.PackMesh = PackedSceneMesh.Create;
        var replacement = new SceneVisual.Geometry(Peak(49), a.Surface);
        Commit(display, Frame(3, [original with { Visuals = [b, replacement] }], first.Update.Frame));
        Require(ReferenceEquals(display.Objects[original.Id].Visuals[0], old[1]) &&
            !GodotObject.IsInstanceValid(old[0].Node) && !GodotObject.IsInstanceValid(oldMeshes[0]) &&
            GodotObject.IsInstanceValid(oldMeshes[1]), "取消后的迟到结果不能接入；新提交只退役未匹配的旧网格。");
    }

    [Test]
    public async Task Promoted_subset_retains_prebuilt_visuals_for_later_batches_until_the_space_changes()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        Commit(display, Frame(1, []));
        var a = Object(1, Mesh());
        var b = Object(2, Mesh());
        var c = Object(3, Mesh());
        var candidate = Frame(2, [a, b, c], space: 2);
        var before = OrphanIds();
        display.ReceiveCandidate(candidate.Update);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        PrimeCandidate(display);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        var detached = NewOrphanRoots(before);
        Require(detached.Length == 3, "候选必须预备三个脱树视觉。");
        var meshes = detached.ToDictionary(node => node,
            node => node.GetChild<MeshInstance3D>(0).Mesh);
        var first = Frame(3, [a], space: 2);
        Commit(display, first);
        var firstVisual = display.Objects[a.Id].Visuals[0];
        Require(detached.Contains(firstVisual.Node) && display.CandidateProgress(first.Update.Frame) is (2, 3),
            "缩小后的晋升首帧必须保留尚未接入的B和C。");
        var next = Frame(4, [b], first.Update.Frame, space: 2);
        using (var prepared = Prepare(display, next))
        {
            var visual = prepared.Objects[b.Id].Visuals[0];
            Require(detached.Contains(visual.Node) && ReferenceEquals(meshes[visual.Node],
                visual.Node.GetChild<MeshInstance3D>(0).Mesh),
                "后续批次必须转移预备视觉及同一网格，不能再次Prepare。");
            prepared.Commit();
            Require(ReferenceEquals(visual, display.Objects[b.Id].Visuals[0]),
                "提交必须保留已转移的PreparedVisual本体。");
        }
        var unused = detached.Single(node => node.GetParent() is null);
        var unusedMesh = meshes[unused];
        Require(display.CandidateProgress(next.Update.Frame) is (1, 3), "C必须继续等待后续批次。");
        Commit(display, Frame(5, [], space: 3));
        Require(!GodotObject.IsInstanceValid(unused) && !GodotObject.IsInstanceValid(unusedMesh) &&
            !display.HasCandidateFor(candidate.Update.Frame), "切换Space必须释放尚未取用的C及其网格。");
    }

    [Test]
    public async Task Prebuilt_candidate_obeys_its_budget_and_remains_detached_until_all_visuals_are_prepared()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var displayed = Frame(1, [Object(1, Mesh())]);
        Commit(display, displayed);
        var children = display.Root.GetChildren().Select(node => node.GetInstanceId()).ToArray();
        var before = OrphanIds();
        var update = Frame(2, [Object(2, Mesh()), Object(3, Mesh())], space: 2).Update;
        display.ReceiveCandidate(update);
        await new UiTestInput(TestScene).Frames(1);
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency / 1000;
        PrimeCandidate(display);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(1.5));
        Require(display.CandidateProgress(update.Frame) is (1, 2) && display.PreparedCandidate is null,
            "预建候选必须在配额内只准备一个视觉，新空间也不能绕过预算提前给回执。");
        Require(ReferenceEquals(display.DisplayedFrame, displayed) && display.Objects.Count == 1 &&
            display.Root.GetChildren().Select(node => node.GetInstanceId()).SequenceEqual(children),
            "部分候选准备不得改变D或显示对象树。");
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        PrimeCandidate(display);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.PreparedCandidate == update.Frame && display.CandidateProgress(update.Frame) is (2, 2),
            "全部视觉准备完毕后才可给出候选回执。");
        var detached = NewOrphanRoots(before);
        Require(detached.Length == 2 && detached.All(node => node.GetParent() is null && !node.IsInsideTree()),
            "预建候选只能创建两个脱树视觉，不能为场景对象创建root。");
        Require(ReferenceEquals(display.DisplayedFrame, displayed) && display.Objects.Count == 1 &&
            display.Root.GetChildren().Select(node => node.GetInstanceId()).SequenceEqual(children),
            "完整候选准备也不得发布显示对象。");
    }

    [Test]
    public async Task Prebuilt_candidate_visual_transfers_to_the_formal_frame_and_uses_its_pose_and_material_state()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("prebuilt-transfer-texture");
        using var display = Attach(cache, new FixedContentView
        { Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) } });
        var displayed = Frame(1, [Object(1, Mesh())]);
        Commit(display, displayed);
        var mesh = Mesh(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#204080")));
        var update = Frame(2, [Object(2, mesh, new(7, 0, 9))], space: 2).Update;
        var before = OrphanIds();
        display.ReceiveCandidate(update);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        PrimeCandidate(display);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        var detached = NewOrphanRoots(before);
        Require(detached.Length == 1 && display.PreparedCandidate == update.Frame, "必须已有一个准备完成的脱树视觉。");
        var node = detached[0];
        var formalVisual = new SceneVisual.Geometry(mesh with
        { Material = new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#80a0c0")) },
            new(true, 19, .75f, true));
        var formal = Frame(3, [Object(2, mesh, new(-70, 4, 12)) with { Visuals = [formalVisual] }],
            origin: new(100, 0, 0), space: 2);
        using var prepared = Prepare(display, formal);
        var transferred = prepared.Objects[new(2)].Visuals[0];
        Require(ReferenceEquals(transferred.Node, node) && node.GetParent() is null && !node.IsInsideTree() &&
            display.CandidateProgress(update.Frame) is (0, 1),
            "正式准备必须转移候选视觉本体，提交前仍保持脱树。");
        Require(ReferenceEquals(display.DisplayedFrame, displayed), "正式准备不得提前更新D。");
        prepared.Commit();
        Require(ReferenceEquals(transferred, display.Objects[new(2)].Visuals[0]) &&
            ReferenceEquals(display.DisplayedFrame, formal), "提交必须接入同一PreparedVisual及正式帧。");
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-70, 4, 12));
        Near(display.Root.Position, Vector3.Zero);
        Require(display.DisplayedFrame!.Origin == new ScenePoint(100, 0, 0), "正式Origin必须成为新的显示基准。");
        var material = (ShaderMaterial)node.GetChild<MeshInstance3D>(0).MaterialOverride;
        Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#80a0c0")) &&
            material.GetShaderParameter("wear_enabled").AsBool() && material.GetShaderParameter("scorched").AsBool(),
            "复用视觉必须应用正式P的涂装与表面参数。");
        Require(MathF.Abs(material.GetShaderParameter("surface_damage").AsSingle() - .75f) < .001f,
            "复用视觉必须应用正式P的损伤参数。");
        display.CancelCandidate();
        Require(GodotObject.IsInstanceValid(node), "取消已转移候选不得释放活动视觉。");
    }

    [Test]
    public async Task Cancelling_or_replacing_a_prebuilt_space_releases_only_candidate_leases()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var shared = Png("prebuilt-active-texture");
        var exclusive = Png("prebuilt-exclusive-texture");
        using var display = Attach(cache, new FixedContentView
        { Assets = new[] { shared, exclusive }.ToDictionary(asset => asset.Revision,
            asset => (ContentLookup<ContentAsset>)new ContentLookup<ContentAsset>.Found(asset)) });
        var activeMesh = Mesh(new SceneMaterial.Voxel(shared.Revision, MaterialCategory.BareMetal, null));
        var exclusiveMesh = Mesh(new SceneMaterial.Voxel(exclusive.Revision, MaterialCategory.BareMetal, null));
        var displayed = Frame(1, [Object(1, activeMesh)]);
        Commit(display, displayed);
        var activeRoot = display.Objects[new(1)].Root;
        foreach (var replace in new[] { false, true })
        {
            using var sharedObserver = cache.AcquireFaceTexture(shared);
            using var exclusiveObserver = cache.AcquireFaceTexture(exclusive);
            var activeTexture = sharedObserver.Value;
            var candidateTexture = exclusiveObserver.Value;
            var before = OrphanIds();
            var update = Frame(2, [Object(2, activeMesh), Object(3, exclusiveMesh)], space: 2).Update;
            display.ReceiveCandidate(update);
            await new UiTestInput(TestScene).Frames(1);
            display.Timestamp = () => 0;
            PrimeCandidate(display);
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            var nodes = NewOrphanRoots(before);
            Require(nodes.Length == 2 && display.PreparedCandidate == update.Frame, "释放前必须完成两个候选视觉。");
            sharedObserver.Dispose();
            exclusiveObserver.Dispose();
            Require(GodotObject.IsInstanceValid(candidateTexture), "候选必须独立持有纹理租约。");
            if (replace) display.ReceiveCandidate(Frame(3, [], space: 3).Update);
            else display.CancelCandidate();
            Require(nodes.All(node => !GodotObject.IsInstanceValid(node)) && !GodotObject.IsInstanceValid(candidateTexture),
                "取消或换Space必须释放候选独有节点与纹理。");
            Require(GodotObject.IsInstanceValid(activeTexture) && GodotObject.IsInstanceValid(activeRoot) &&
                ReferenceEquals(display.DisplayedFrame, displayed) && ReferenceEquals(display.Objects[new(1)].Root, activeRoot),
                "候选释放不得破坏活动对象及其共享纹理租约。");
            Require(!display.HasCandidateFor(update.Frame) && display.PreparedCandidate is null,
                "取消或更换空间必须撤销旧候选回执。");
        }
    }

    [Test]
    public async Task Building_top_is_the_whole_displayed_object_and_empty_when_absent()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var parent = Object(1, Peak(25), new(0, 10, 0));
        var child = Object(2, Peak(1), new(0, 4, 0), new(1));
        Commit(display, Frame(1, [parent, child]));
        NearY(display.ObjectTopY(new(1)), 35);
        NearY(display.ObjectTopY(new(2)), 35);
        Require(display.ObjectTopY(new(9)) is null, "未显示的物体不应有顶面。");
        display.UpdatePoses(Frame(2, [parent, child], display.DisplayedFrame!.Update.Frame, new(0, -5, 0)));
        NearY(display.ObjectTopY(new(2)), 35);
    }

    [Test]
    public async Task Locked_candidate_finishes_before_new_objects_and_uses_latest_pose_after_commit()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh(), new(10, 0, 0))]);
        Commit(display, first);
        var mesh = Mesh();
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(2)));
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var candidate = Frame(2, [Object(2, mesh) with { Subject = subject }, Object(3, mesh)], first.Update.Frame);
        List<SceneDisplayEvent> events = [];
        display.Observe = item =>
        {
            if (item.Stage == SceneDisplayStage.FrameCommitted)
                Require(ReferenceEquals(display.DisplayedFrame, candidate) &&
                    display.Objects[new(3)].Root.IsInsideTree(), "提交记录必须在完整候选进入显示树后发出。");
            events.Add(item);
        };
        Prime(display, candidate);
        Require(display.Prepare(candidate) is null, "锁定候选必须在第一个资源后暂停。");
        Require(events.Count == 2 && events[0].Stage == SceneDisplayStage.FramePreparationStarted &&
            events[0].Object is null && events[0].ResourceObjects is { } required &&
            required.SequenceEqual(new SceneObjectId[] { new(2), new(3) }),
            "候选冻结时必须一次列出完整的新视觉资源对象。");
        Require(events[1].Object == new SceneObjectId(2) &&
            events[1].Stage == SceneDisplayStage.ResourcesPrepared && events[1].Frame == candidate.Update.Frame,
            "单个对象资源就绪必须立即记录，不能等待整个候选准备完成。");
        var latest = Frame(3,
            [Object(2, mesh, new(-70, 0, 0)) with { Subject = subject }, Object(4, mesh)],
            first.Update.Frame, new(-100, 0, 0), new(100, 0, 0),
            poses: ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(new(new(-70, 0, 0), NQuaternion.Identity))));
        display.UpdatePoses(latest);
        using var prepared = display.Prepare(latest);
        Require(prepared is not null, "第二次调用必须完成锁定候选，不能等待最新帧追加的对象。");
        Require(ReferenceEquals(display.PoseFrame, latest) && ReferenceEquals(display.DisplayedFrame, first),
            "准备期间姿态帧必须更新，网格基准必须保持最后完整帧。");
        Require(events.Count(item => item.Stage == SceneDisplayStage.ResourcesPrepared) == 2 &&
            events[^1].Stage == SceneDisplayStage.FramePrepared &&
            events.Count(item => item.Stage == SceneDisplayStage.FramePreparationStarted) == 1 &&
            events[0].ResourceObjects!.Value.SequenceEqual(new SceneObjectId[] { new(2), new(3) }),
            "分预算恢复不得重复记录对象资源，也不能提前记录提交。");
        prepared!.Commit();
        Require(events.Count == 5 && events.All(item => item.Frame == candidate.Update.Frame &&
            item.Candidate == events[0].Candidate) &&
            events[^1].Stage == SceneDisplayStage.FrameCommitted && events[^1].Object is null,
            "较新输入不能覆盖冻结候选的完整身份，完整提交必须仅记录一次。");
        display.UpdatePoses(latest);
        Require(ReferenceEquals(display.DisplayedFrame, candidate) && ReferenceEquals(display.PoseFrame, latest),
            "提交锁定候选后必须恢复最新姿态帧。");
        Require(!display.Objects.ContainsKey(new(4)), "最新帧追加的对象不得混入锁定候选。");
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-70, 0, 0));
        Near(display.Objects[new(1)].Root.GlobalPosition, new(-90, 0, 0));
        SceneHit Hit(ulong id) => new(latest.Update.Frame, new(id),
            new PickSubject.Terrain(new WorldLocation.Node(new(1))), default, GridFace.PositiveY,
            default, NVector3.UnitY, 1, []);
        Require(display.CanSelect(Hit(2)), "最新帧中与已显示对象兼容的命中必须可选。");
        Require(display.CanSelect(Hit(1)), "候选和最新帧均未更新的继承对象必须可选。");
        Require(!display.CanSelect(Hit(4)), "最新帧新增但尚未显示的对象不得可选。");
        Require(!display.CanSelect(Hit(3)), "候选更新而最新帧省略的对象无法证明兼容，不得可选。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Resource_prepared_waits_for_every_visual_of_the_object()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var original = Object(1, Mesh());
        var baseline = Frame(1, [original]);
        Commit(display, baseline);
        var reused = display.Objects[original.Id].Visuals[0];
        var added = Object(1, Mesh()).Visuals[0];
        var source = original with { Visuals = [original.Visuals[0], added, added] };
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var frame = Frame(2, [source], baseline.Update.Frame);
        Prime(display, frame);
        Require(display.Prepare(frame) is null && events.Count == 1 &&
            events[0].Stage == SceneDisplayStage.FramePreparationStarted && events[0].Object is null &&
            events[0].ResourceObjects is { } required && required.SequenceEqual(new[] { source.Id }),
            "复用一个、只新准备一个视觉资源不能记录整个对象资源就绪。");
        using var prepared = Prepare(display, frame);
        Require(ReferenceEquals(prepared.Objects[source.Id].Visuals[0], reused) &&
            prepared.Objects[source.Id].Visuals.Length == 3,
            "多视觉对象必须保留已显示的视觉资源，并准备其余两个视觉资源。");
        Require(events.Count == 3 && events[2].Stage == SceneDisplayStage.FramePrepared && events[1].Object == source.Id &&
            events[1].Stage == SceneDisplayStage.ResourcesPrepared,
            "全部视觉资源准备完才记录一次对象资源就绪。");
        prepared.Commit();
        Require(events.Count == 4 && events[3].Stage == SceneDisplayStage.FrameCommitted &&
            events[3].Object is null,
            "帧提交应在对象资源就绪与整帧准备完成之后记录一次。");
    }

    [Test]
    public async Task Reused_or_removed_visuals_and_empty_objects_do_not_emit_resource_timings()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var moving = Object(1, Mesh());
        var shrinking = Object(2, Mesh());
        shrinking = shrinking with { Visuals = [shrinking.Visuals[0], shrinking.Visuals[0]] };
        var emptied = Object(3, Mesh());
        var first = Frame(1, [moving, shrinking, emptied]);
        Commit(display, first);
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        var empty = Object(4, Mesh()) with { Visuals = [] };
        Commit(display, Frame(2, [
            moving with { Pose = new(new(10, 0, 0), NQuaternion.Identity) },
            shrinking with { Visuals = [shrinking.Visuals[0]] },
            emptied with { Visuals = [] }, empty], first.Update.Frame));
        Require(events.Count == 3 && events[0].Stage == SceneDisplayStage.FramePreparationStarted &&
            events[0].ResourceObjects is { IsEmpty: true } &&
            events[1].Stage == SceneDisplayStage.FramePrepared && events[2].Stage == SceneDisplayStage.FrameCommitted &&
            events.All(item => item.Object is null && item.Frame == display.DisplayedFrame!.Update.Frame),
            "全复用候选也必须准确记录完整准备与实际提交，但不能虚构逐对象资源时延。");
        Near(display.Objects[moving.Id].Root.Position, new(10, 0, 0));
        Require(display.Objects[shrinking.Id].Visuals.Length == 1 &&
            display.Objects[emptied.Id].Visuals.Length == 0 && display.Objects.ContainsKey(empty.Id),
            "不记录资源时延的更新仍须正常提交。");
    }

    [Test]
    public async Task Cancelled_candidates_keep_their_identity_when_the_same_frame_is_retried()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var reused = Mesh();
        var first = Frame(1, [Object(1, reused)]);
        Commit(display, first);
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var frame = Frame(2, [Object(1, reused), Object(2, Mesh()), Object(3, Mesh())],
            first.Update.Frame);
        Prime(display, frame);
        Require(display.Prepare(frame) is null, "候选必须在准备后续对象前暂停。");
        Require(events.Count == 2 && events[0].Stage == SceneDisplayStage.FramePreparationStarted &&
            events[0].Object is null && events[0].ResourceObjects is { } required &&
            required.SequenceEqual(new SceneObjectId[] { new(2), new(3) }) &&
            events[1].Object == new SceneObjectId(2) && events[1].Stage == SceneDisplayStage.ResourcesPrepared,
            "只为新准备视觉资源的对象记录资源就绪，复用对象不记录。");
        var cancelledCandidate = events[0].Candidate;
        display.CancelPreparation();
        Require(events[^1] == new SceneDisplayEvent(cancelledCandidate, frame.Update.Frame, null,
            SceneDisplayStage.HostCancelled), "取消必须单独记录原候选身份。");
        display.CancelPreparation();
        Require(events.Count == 3, "重复清理不能重复记录取消。");
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => timestamp;
        var abandoned = Prepare(display, frame);
        var abandonedCandidate = events[^1].Candidate;
        Require(abandonedCandidate != cancelledCandidate, "同一帧重试必须具有不同候选身份。");
        abandoned.Dispose();
        abandoned.Dispose();
        Require(events.Count(item => item.Stage == SceneDisplayStage.HostCancelled) == 2 &&
            events[^1] == new SceneDisplayEvent(abandonedCandidate, frame.Update.Frame, null,
                SceneDisplayStage.HostCancelled), "完整准备后丢弃也必须准确且仅记录一次取消。");
        Require(events.All(item => item.Stage != SceneDisplayStage.FrameCommitted), "取消候选不能记录成功提交。");
        using var retry = Prepare(display, frame);
        var committedCandidate = events[^1].Candidate;
        retry.Commit();
        Require(committedCandidate != abandonedCandidate && events.Count(item =>
            item.Stage == SceneDisplayStage.FrameCommitted) == 1 &&
            events[^1] == new SceneDisplayEvent(committedCandidate, frame.Update.Frame, null,
                SceneDisplayStage.FrameCommitted),
            "再次重试的提交不能归因于已经取消的候选。");
        var starts = events.Where(item => item.Stage == SceneDisplayStage.FramePreparationStarted).ToArray();
        Require(starts.Length == 3 && starts.Select(item => item.Candidate).Distinct().Count() == 3 &&
            starts.Select(item => item.Candidate).SequenceEqual(new[] { cancelledCandidate, abandonedCandidate, committedCandidate }) &&
            starts.All(item => item.Frame == frame.Update.Frame && item.Object is null &&
                item.ResourceObjects is { } objects && objects.SequenceEqual(new SceneObjectId[] { new(2), new(3) })),
            "每次重试必须重新冻结完整资源集合，并发出不同身份的开始通知。");
        display.Observe = null;
    }

    [Test]
    public async Task Candidate_keeps_its_leases_until_commit_and_reuses_displayed_visuals_in_the_next_target()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var kept = Png("candidate-kept-texture");
        var obsolete = Png("candidate-obsolete-texture");
        var content = new FixedContentView { Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
        {
            [kept.Revision] = new ContentLookup<ContentAsset>.Found(kept),
            [obsolete.Revision] = new ContentLookup<ContentAsset>.Found(obsolete)
        } };
        using var display = Attach(cache, content);
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        var oldRoot = display.Objects[new(1)].Root;
        await new UiTestInput(TestScene).Frames(1);
        using var keptLease = cache.AcquireFaceTexture(kept);
        using var obsoleteLease = cache.AcquireFaceTexture(obsolete);
        var keptTexture = keptLease.Value;
        var obsoleteTexture = obsoleteLease.Value;
        var keptMesh = Mesh(new SceneMaterial.Voxel(kept.Revision, MaterialCategory.BareMetal, null));
        var oldFog = ImmutableArray.Create(new SceneBounds(new(10, 0, 0), new(11, 1, 1)));
        var oldLines = ImmutableArray.Create(new SceneDebugLine(new(1, 0, 0), new(2, 0, 0), new(1, 0, 0, 1)));
        var candidate = Frame(2, [Object(2, keptMesh), Object(3,
            Mesh(new SceneMaterial.Voxel(obsolete.Revision, MaterialCategory.BareMetal, null))), Object(4, Mesh())],
            first.Update.Frame, fog: oldFog, debugLines: oldLines);
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency / 500;
        Prime(display, candidate);
        Require(display.Prepare(candidate) is null, "Preparing two textures must exhaust this frame's resource budget.");
        keptLease.Dispose();
        obsoleteLease.Dispose();
        Require(GodotObject.IsInstanceValid(keptTexture) && GodotObject.IsInstanceValid(obsoleteTexture),
            "Pending visuals must own both texture leases after the observers release them.");
        var fog = ImmutableArray.Create(new SceneBounds(new(20, 0, 0), new(22, 2, 2)));
        var lines = ImmutableArray.Create(new SceneDebugLine(new(3, 0, 0), new(4, 0, 0), new(0, 1, 0, 1)));
        var latest = Frame(3, [Object(2, keptMesh, new(30, 0, 0)), Object(5, Mesh()), Object(6, Mesh())],
            first.Update.Frame, removed: [new(1), new(3), new(4)], fog: fog, debugLines: lines);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => timestamp;
        using (var prepared = Prepare(display, latest))
        {
            Require(ReferenceEquals(prepared.Frame, candidate), "A newer frame must not replace the locked candidate.");
            Require(prepared.Fog.Regions.SequenceEqual(oldFog) && prepared.DebugLines!.Lines.SequenceEqual(oldLines),
                "Fog and debug lines must belong to the locked candidate.");
            prepared.Commit();
        }
        var retained = display.Objects[new(2)].Visuals[0];
        Require(display.Objects.ContainsKey(new(3)) && display.Objects.ContainsKey(new(4)) &&
            !display.Objects.ContainsKey(new(5)) && GodotObject.IsInstanceValid(obsoleteTexture),
            "The candidate must commit its complete object set and pending leases.");
        await new UiTestInput(TestScene).Frames(1);
        using (var prepared = Prepare(display, Frame(4, latest.Update.Upserts, candidate.Update.Frame,
            removed: [new(1), new(3), new(4)], fog: fog, debugLines: lines))) prepared.Commit();
        Require(ReferenceEquals(retained, display.Objects[new(2)].Visuals[0]) && display.Objects.Count == 3,
            "The next target must reuse a compatible displayed visual.");
        Require(!GodotObject.IsInstanceValid(oldRoot) && !GodotObject.IsInstanceValid(obsoleteTexture) &&
            GodotObject.IsInstanceValid(keptTexture), "Retirement must release obsolete resources while keeping reused leases.");
        Near(display.Objects[new(2)].Root.Position, new(30, 0, 0));
        using var arrays = display.Root.GetNode<MeshInstance3D>("DebugLines").Mesh.SurfaceGetArrays(0);
        Near(arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array()[0], new(3, 0, 0));
    }

    [Test]
    public async Task Changed_preparation_scope_releases_pending_textures_before_return()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("obsolete-pending-texture");
        var content = new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
                { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) }
        };
        using var display = Attach(cache, content);
        var first = Frame(1, []);
        Commit(display, first);
        var textured = Mesh(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.BareMetal, null));
        foreach (var changed in new[] { "base", "session", "request", "space" })
        {
            using var observer = cache.AcquireFaceTexture(asset);
            var texture = observer.Value;
            long timestamp = 0;
            display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
            var pending = Frame(2, [Object(2, textured), Object(3, Mesh())]);
            Prime(display, pending);
            Require(display.Prepare(pending) is null,
                "纹理对象准备后必须暂停。");
            observer.Dispose();
            Require(GodotObject.IsInstanceValid(texture), "待用资源必须持有纹理。");
            var latest = Frame(3, [Object(2, Mesh()), Object(3, Mesh())],
                @base: changed == "base" ? first.Update.Frame : null,
                space: changed == "space" ? 2UL : 1UL,
                session: changed == "session" ? new SceneSessionId(Guid.Parse("248c5802-f435-49f4-84fa-2dc3777779a0")) : null,
                request: changed == "request" ? 2UL : 1UL);
            using var prepared = display.Prepare(latest);
            Require((prepared is not null) == (changed is "session" or "space"),
                "新空间首帧必须一次准备完；同空间的范围变化仍须遵守预算。");
            Require(!GodotObject.IsInstanceValid(texture),
                $"{changed} 变化必须在返回前释放旧候选的待用纹理。");
            display.CancelPreparation();
        }
        Require(ReferenceEquals(display.DisplayedFrame, first) && display.Objects.Count == 0,
            "候选替换和清理不能发布部分对象。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Pose_overlay_restores_baseline_and_commit_does_not_repeat_origin_shift()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var mesh = Mesh();
        var first = Frame(1, [Object(1, mesh, new(10, 0, 0)), Object(2, mesh, new(2, 0, 0), new(1))]);
        Commit(display, first);
        var moving = Frame(2, [Object(1, mesh, new(-70, 0, 0)), Object(2, mesh, new(4, 0, 0), new(1))],
            first.Update.Frame, new(-100, 0, 0), new(100, 0, 0));
        Near(display.UpdatePoses(moving), new(-100, 0, 0));
        Near(display.Objects[new(1)].Root.GlobalPosition, new(-70, 0, 0));
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-66, 0, 0));
        var restored = Frame(3, [], first.Update.Frame, new(-200, 0, 0), new(200, 0, 0));
        Near(display.UpdatePoses(restored), new(-100, 0, 0));
        Near(display.Root.Position, new(-200, 0, 0));
        Near(display.Objects[new(1)].Root.Position, new(10, 0, 0));
        Near(display.Objects[new(2)].Root.Position, new(2, 0, 0));
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-188, 0, 0));
        Commit(display, restored);
        Near(display.Root.Position, Vector3.Zero);
        Near(display.Objects[new(1)].Root.GlobalPosition, new(-190, 0, 0));
        var subsequent = Frame(4, [], restored.Update.Frame, new(-50, 0, 0), new(250, 0, 0));
        Near(display.UpdatePoses(subsequent), new(-50, 0, 0));
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-238, 0, 0));
        Commit(display, subsequent);
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-238, 0, 0));
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Cross_space_loading_anchors_old_geometry_then_first_operable_frame_ignores_budget()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        var mesh = Mesh();
        var first = Frame(1, [Object(1, mesh, new(10, 0, 0)) with { Subject = subject },
            Object(2, mesh, new(2, 0, 0), new(1)), Object(3, mesh, new(30, 0, 0))]);
        Commit(display, first);
        var loading = Frame(2, [], origin: new(1000, 0, 0), readiness: SceneReadiness.Loading, space: 2,
            poses: ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(new(new(10, 0, 0), NQuaternion.Identity))));
        Near(display.UpdatePoses(loading), Vector3.Zero);
        Require(display.Prepare(loading) is null, "新空间 Loading 不得提交。");
        Near(display.Objects[new(3)].Root.GlobalPosition, new(30, 0, 0));
        var latest = Frame(3, [Object(4, mesh), Object(5, mesh)], origin: new(1100, 0, 0), space: 2,
            poses: ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(new(new(-60, 0, 0), NQuaternion.Identity))));
        Near(display.UpdatePoses(latest), new(-100, 0, 0));
        Near(display.Objects[new(1)].Root.GlobalPosition, new(-60, 0, 0));
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-58, 0, 0));
        Near(display.Objects[new(3)].Root.GlobalPosition, new(-70, 0, 0));
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        using var prepared = display.Prepare(latest);
        Require(prepared is not null && ReferenceEquals(display.DisplayedFrame, first),
            "切换空间的首个可操作帧必须一次准备完，提交前保留旧网格基准。");
        Require(ReferenceEquals(display.PoseFrame, latest), "旧几何必须使用最新完整替换帧的姿态。");
        prepared!.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, latest) && display.Objects.Keys.ToHashSet().SetEquals([new(4), new(5)]),
            "新空间必须原子提交全部对象并移除旧空间。");
        var subsequent = Frame(4, [Object(6, Mesh()), Object(7, Mesh())], latest.Update.Frame, space: 2);
        Require(display.Prepare(subsequent) is null && ReferenceEquals(display.DisplayedFrame, latest),
            "新空间后续帧必须恢复预算，保持已提交的完整显示。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Initial_loading_then_first_operable_frame_ignores_budget_and_subsequent_frames_restore_it()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView(), completeInitialFrame: true);
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        Require(display.Prepare(Frame(1, [], readiness: SceneReadiness.Loading)) is null &&
            display.DisplayedFrame is null && !display.IsPreparing,
            "首次 Loading 不得开始准备或提交显示。");
        var first = Frame(2, [Object(1, Mesh()), Object(2, Mesh())]);
        using (var prepared = display.Prepare(first))
        {
            Require(prepared is not null && display.DisplayedFrame is null,
                "首次可操作候选必须忽略预算一次准备完，提交前不得提前显示。");
            prepared!.Commit();
        }
        Require(ReferenceEquals(display.DisplayedFrame, first) &&
            display.Objects.Keys.ToHashSet().SetEquals([new(1), new(2)]),
            "首次可操作候选必须原子提交全部对象。");
        var subsequent = Frame(3, [Object(3, Mesh()), Object(4, Mesh())], first.Update.Frame);
        Require(display.Prepare(subsequent) is null && ReferenceEquals(display.DisplayedFrame, first),
            "首次提交后的同空间候选必须恢复预算并保留完整显示。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Displays_share_the_process_frame_budget_and_each_call_still_progresses()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var first = Attach(cache, new FixedContentView());
        using var second = Attach(cache, new FixedContentView());
        var baseline = Frame(0, []);
        Commit(first, baseline);
        Commit(second, baseline);
        long timestamp = 0;
        first.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var pending = Frame(1, [Object(1, Mesh()), Object(2, Mesh())], baseline.Update.Frame);
        Prime(first, pending);
        var processFrame = Godot.Engine.GetProcessFrames();
        Require(first.Prepare(pending) is null,
            "先调用的显示器必须先耗尽当前进程帧的预算。");
        var candidate = Frame(2, [Object(1, Mesh()), Object(2, Mesh()), Object(3, Mesh())], baseline.Update.Frame,
            fog: [new(new(10, 0, 0), new(11, 1, 1))]);
        Prime(second, candidate);
        for (var call = 0; call < 3; call++)
        {
            using var partial = second.Prepare(candidate);
            Require(partial is null,
                "同一进程帧的后调用者必须共用已耗尽预算，每次只推进一个资源。");
        }
        using (var prepared = second.Prepare(candidate))
        {
            Require(prepared is not null, "耗尽预算后仍必须逐项推进并最终完成，包含雾资源。");
            prepared!.Commit();
        }
        Require(Godot.Engine.GetProcessFrames() == processFrame, "共享预算断言必须发生在同一真实进程帧。");
        first.CancelPreparation();
        await new UiTestInput(TestScene).Frames(2);
        Require(Godot.Engine.GetProcessFrames() > processFrame, "重置预算前必须经过真实进程帧。");
        first.Timestamp = () => 0;
        Prime(first, candidate);
        using var next = first.Prepare(candidate);
        Require(next is not null, "下一进程帧必须恢复共享额度，让零耗时资源一次完成。");
    }

    [Test]
    public async Task Cancelled_preparation_releases_resources_without_touching_committed_frame()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("superseded-texture");
        var content = new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
                { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) }
        };
        using var display = Attach(cache, content);
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        using var lease = cache.AcquireFaceTexture(asset);
        var texture = lease.Value;
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var candidate = Frame(2,
            [Object(2, Mesh(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.BareMetal, null))), Object(3, Mesh())],
            first.Update.Frame);
        Prime(display, candidate);
        Require(display.Prepare(candidate) is null, "候选必须在第一个资源后耗尽预算。");
        lease.Dispose();
        Require(GodotObject.IsInstanceValid(texture), "尚在准备的候选必须持有纹理租约。");
        display.CancelPreparation();
        var latest = Frame(3, [], first.Update.Frame);
        using var prepared = display.Prepare(latest);
        Require(prepared is not null && !GodotObject.IsInstanceValid(texture) &&
            ReferenceEquals(display.DisplayedFrame, first),
            "取消的候选必须在主线程释放其独占资源，同时旧显示帧不变。");
        prepared!.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, latest) && display.Objects.Count == 1,
            "必须提交最新候选，不能接入已被取代的对象。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Full_replacement_uses_the_same_preparation_budget()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        var oldRoot = display.Objects[new(1)].Root;
        var replacement = Frame(2, [Object(2, Mesh()), Object(3, Mesh())]);
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        Prime(display, replacement);
        Require(display.Prepare(replacement) is null && ReferenceEquals(display.DisplayedFrame, first) &&
            GodotObject.IsInstanceValid(oldRoot), "完整替换必须遵守同一预算，准备未完成时保留旧显示。");
        using var prepared = display.Prepare(replacement);
        Require(prepared is not null && ReferenceEquals(display.DisplayedFrame, first),
            "第二帧必须复用已准备资源，且提交前不得发布候选。");
        prepared!.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, replacement) && display.Objects.Count == 2 &&
            !GodotObject.IsInstanceValid(oldRoot), "完整替换必须一次发布全部新对象并释放旧对象。");
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public void Debug_lines_use_committed_frame_coordinates_and_release_replaced_resources()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        Commit(display, Frame(0, []));
        Require(display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null,
            "空线集合不得创建调试线节点。");
        var lines = ImmutableArray.Create(new SceneDebugLine(new(10, 2, 3), new(20, 4, 6), new(1, 0, 0, 1)));
        var first = Frame(1, [], origin: new(100, 0, 0), debugLines: lines);
        Commit(display, first);
        var root = display.Root.GetNode<MeshInstance3D>("DebugLines");
        var mesh = root.Mesh;
        Require(mesh is ImmediateMesh && mesh.GetSurfaceCount() == 1,
            "调试线必须生成真实线段网格。");
        using var firstArrays = mesh.SurfaceGetArrays(0);
        var vertices = firstArrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        Require(vertices.Length == 2, "一条调试线必须包含两个顶点。");
        Near(vertices[0], new(10, 2, 3));
        Near(vertices[1], new(20, 4, 6));
        var colors = firstArrays[(int)Godot.Mesh.ArrayType.Color].AsColorArray();
        Require(colors.Length == 2 && colors.All(color => color == Colors.Red), "线段端点必须保留Scenes提供的颜色。");
        var unchanged = Frame(2, [], first.Update.Frame,
            debugLines: ImmutableArray.Create(lines[0]));
        using (var abandoned = Prepare(display, unchanged)) { }
        Require(ReferenceEquals(root, display.Root.GetNode<MeshInstance3D>("DebugLines")) &&
            GodotObject.IsInstanceValid(mesh), "丢弃复用线段的候选不得释放当前资源。");
        Commit(display, unchanged);
        Require(ReferenceEquals(root, display.Root.GetNode<MeshInstance3D>("DebugLines")) &&
            ReferenceEquals(mesh, root.Mesh), "线序列未变化必须复用节点和网格。");
        var shiftedLines = ImmutableArray.Create(new SceneDebugLine(new(-90, 2, 3), new(-80, 4, 6), new(0, 1, 0, 1)));
        var next = Frame(3, [], unchanged.Update.Frame, new(-100, 0, 0), new(200, 0, 0), debugLines: shiftedLines);
        using (var abandoned = Prepare(display, next))
        {
            var candidateRoot = abandoned.DebugLines!.Root;
            var candidateMesh = candidateRoot.Mesh;
            abandoned.Dispose();
            Require(!GodotObject.IsInstanceValid(candidateRoot) && !GodotObject.IsInstanceValid(candidateMesh),
                "丢弃新线段候选必须释放其节点和网格。");
        }
        Require(ReferenceEquals(root, display.Root.GetNode<MeshInstance3D>("DebugLines")) &&
            GodotObject.IsInstanceValid(mesh), "丢弃候选不得改变已显示线段。");
        Commit(display, next);
        Require(!GodotObject.IsInstanceValid(root) && !GodotObject.IsInstanceValid(mesh), "替换线段必须释放旧节点和网格。");
        var shiftedMesh = display.Root.GetNode<MeshInstance3D>("DebugLines").Mesh;
        using var shiftedArrays = shiftedMesh.SurfaceGetArrays(0);
        vertices = shiftedArrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        Near(vertices[0], new(-90, 2, 3));
        var empty = Frame(4, [], next.Update.Frame);
        using (var abandoned = Prepare(display, empty)) { }
        Require(GodotObject.IsInstanceValid(shiftedMesh), "丢弃空线候选不得释放当前线段。");
        Commit(display, empty);
        Require(display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null &&
            !GodotObject.IsInstanceValid(shiftedMesh), "空线集合必须清除旧线且不创建空组。");
        Commit(display, Frame(5, [], empty.Update.Frame, debugLines: lines));
        var lastRoot = display.Root.GetNode<MeshInstance3D>("DebugLines");
        var lastMesh = lastRoot.Mesh;
        display.Dispose();
        Require(!GodotObject.IsInstanceValid(lastRoot) && !GodotObject.IsInstanceValid(lastMesh),
            "关闭显示组必须释放调试线资源。");
    }

    [Test]
    public void Candidates_publish_only_on_commit_and_abandoned_resources_are_released()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh())]);
        using (var prepared = Prepare(display, first))
        {
            Require(display.DisplayedFrame is null && display.Objects.Count == 0,
                "准备首帧不得提前发布对象或显示帧。");
            prepared.Commit();
        }
        Require(ReferenceEquals(first, display.DisplayedFrame), "提交后首帧未成为显示帧。");
        var oldRoot = display.Objects[new(1)].Root;
        var oldMesh = MeshOf(display.Objects[new(1)]);
        var abandoned = Frame(2, [Object(2, Mesh())], first.Update.Frame);
        var candidate = Prepare(display, abandoned);
        var candidateRoot = candidate.Objects[new(2)].Root;
        var candidateMesh = MeshOf(candidate.Objects[new(2)]);
        Require(ReferenceEquals(first, display.DisplayedFrame) && display.Objects.Count == 1,
            "准备增量时旧显示组必须保持完整。");
        Require(candidateRoot.GetParent() is null, "候选对象不能在提交前挂入显示树。");
        candidate.Dispose();
        Require(!GodotObject.IsInstanceValid(candidateRoot) && !GodotObject.IsInstanceValid(candidateMesh),
            "未提交候选的节点和独占网格必须释放。");
        Require(GodotObject.IsInstanceValid(oldRoot) && GodotObject.IsInstanceValid(oldMesh),
            "丢弃候选不能释放旧显示组。");
        Require(ReferenceEquals(first, display.DisplayedFrame), "丢弃候选不能改变显示帧。");
        using var next = Prepare(display, abandoned);
        next.Commit();
        Require(ReferenceEquals(abandoned, display.DisplayedFrame) && display.Objects.Count == 2,
            "再次准备并提交应完整发布增量。");
    }

    [Test]
    public void Loading_and_wrong_base_preserve_the_last_displayed_frame()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        var root = display.Objects[new(1)].Root;
        var loading = Frame(2, [], first.Update.Frame, readiness: SceneReadiness.Loading);
        Require(display.Prepare(loading) is null, "Loading 帧不能提交显示。");
        var wrongBase = first.Update.Frame with { Revision = 999 };
        Require(display.Prepare(Frame(3, [Object(2, Mesh(), new(20, 0, 0))], wrongBase,
            shift: new(100, 0, 0), removed: [new(1)])) is null, "不匹配的 Base 不能准备增量。");
        Require(ReferenceEquals(first, display.DisplayedFrame) && ReferenceEquals(root, display.Objects[new(1)].Root),
            "拒绝候选必须保留最后成功帧和对象。");
        Require(display.Objects.Count == 1 && GodotObject.IsInstanceValid(root),
            "Base 错配不得部分删除或添加对象。");
        Near(root.Position, Vector3.Zero);
    }

    [Test]
    public void Preparation_failure_releases_earlier_leases_and_preserves_old_frame()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("candidate-texture");
        var unavailable = new AssetRevision(new string('E', 64));
        var content = new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset),
                [unavailable] = new ContentLookup<ContentAsset>.NotFound()
            }
        };
        using var display = Attach(cache, content);
        var first = Frame(1, [Object(1, Mesh())]);
        Commit(display, first);
        var oldRoot = display.Objects[new(1)].Root;
        var oldMesh = MeshOf(display.Objects[new(1)]);
        using var observingLease = cache.AcquireFaceTexture(asset);
        var texture = observingLease.Value;
        var failed = Frame(2,
        [
            Object(2, Mesh(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.BareMetal, null))),
            Object(3, Mesh(new SceneMaterial.Voxel(unavailable, MaterialCategory.BareMetal, null)))
        ], first.Update.Frame);
        var threw = false;
        try { using var unexpected = Prepare(display, failed); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "明确不可用的资产必须使准备失败，不能静默接入。");
        Require(ReferenceEquals(first, display.DisplayedFrame) && display.Objects.Count == 1,
            "准备失败不能发布部分对象或新帧。");
        Require(GodotObject.IsInstanceValid(oldRoot) && GodotObject.IsInstanceValid(oldMesh),
            "准备失败不能释放旧显示资源。");
        observingLease.Dispose();
        Require(!GodotObject.IsInstanceValid(texture), "失败前已取得的候选纹理租约没有释放。");
    }

    [Test]
    public void Origin_shift_moves_existing_roots_once_and_reuses_their_meshes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var mesh = Mesh();
        var first = Frame(1, [Object(1, mesh, new(10, 0, 20)), Object(2, mesh, new(2, 0, 3), new(1))]);
        Commit(display, first);
        var oldMesh = MeshOf(display.Objects[new(1)]);
        var identity = oldMesh.GetInstanceId();
        var oldVisual = display.Objects[new(1)].Visuals[0];
        var exits = 0;
        display.Objects[new(1)].Root.TreeExiting += () => exits++;
        var next = Frame(2, [Object(3, mesh, new(-50, 0, -60))], first.Update.Frame,
            new(-100, 5, 200), new(100, -5, -200));
        using (var candidate = Prepare(display, next))
        {
            Near(display.Objects[new(1)].Root.Position, new(10, 0, 20));
            candidate.Commit();
        }
        Near(display.Objects[new(1)].Root.Position, new(-90, 5, 220));
        Near(display.Objects[new(2)].Root.Position, new(2, 0, 3));
        Near(display.Objects[new(2)].Root.GlobalPosition, new(-88, 5, 223));
        Near(display.Objects[new(3)].Root.Position, new(-50, 0, -60));
        Require(MeshOf(display.Objects[new(1)]).GetInstanceId() == identity,
            "浮动原点平移不能重建旧网格。");
        Require(ReferenceEquals(oldVisual, display.Objects[new(1)].Visuals[0]) && exits == 0,
            "浮动原点平移必须复用表现且不让原根退出场景树。");
        var moved = Frame(3, [Object(1, mesh, new(-190, 10, 420))], next.Update.Frame,
            new(-100, 5, 200), new(200, -10, -400));
        Commit(display, moved);
        Near(display.Objects[new(1)].Root.Position, new(-190, 10, 420));
        Near(display.Objects[new(3)].Root.Position, new(-150, 5, 140));
        Require(MeshOf(display.Objects[new(1)]).GetInstanceId() == identity,
            "仅更新姿态的 Upsert 必须复用网格。");
        Require(ReferenceEquals(oldVisual, display.Objects[new(1)].Visuals[0]) && exits == 0,
            "仅更新姿态必须复用表现且保持根节点在树中。");
    }

    [Test]
    public void Unchanged_fog_is_reused_and_surviving_child_can_leave_a_retired_parent()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var mesh = Mesh();
        var regions = ImmutableArray.Create(new SceneBounds(NVector3.Zero, NVector3.One));
        var first = Frame(1, [Object(1, mesh), Object(2, mesh, parent: new(1)), Object(3, mesh)], fog: regions);
        Commit(display, first);
        var fog = display.Root.GetNode<Node3D>("LoadingFog");
        var parent = display.Objects[new(1)].Root;
        var child = display.Objects[new(2)].Root;
        var survivor = display.Objects[new(3)].Root;
        var exits = 0;
        survivor.TreeExiting += () => exits++;
        var next = Frame(2, [Object(2, mesh, new(4, 0, 0))], first.Update.Frame,
            removed: [new(1)], fog: ImmutableArray.Create(new SceneBounds(NVector3.Zero, NVector3.One)));
        using (var abandoned = Prepare(display, next)) { }
        Require(GodotObject.IsInstanceValid(fog) && ReferenceEquals(fog.GetParent(), display.Root),
            "丢弃复用雾的候选不得释放当前雾节点。");
        Commit(display, next);
        Require(ReferenceEquals(fog, display.Root.GetNode<Node3D>("LoadingFog")),
            "区域未变化必须保留原雾节点。");
        Require(exits == 0, "未更新的根不得退出场景树。");
        Require(!GodotObject.IsInstanceValid(parent) && GodotObject.IsInstanceValid(child) &&
            ReferenceEquals(child.GetParent(), display.Root), "退役父节点不能释放换父后存活的子对象。");
        Near(child.Position, new(4, 0, 0));
        var retainedBox = fog.GetChild<MeshInstance3D>(0);
        var fogExits = 0;
        retainedBox.TreeExiting += () => fogExits++;
        var changedRegions = Frame(3, [], next.Update.Frame, fog: [regions[0], new(new(4), new(7)), regions[0]]);
        using (var abandoned = Prepare(display, changedRegions)) { }
        Require(fog.GetChildCount() == 1 && GodotObject.IsInstanceValid(retainedBox),
            "放弃换雾准备不能接入新增矩形或释放借用的当前矩形。");
        Commit(display, changedRegions);
        Require(ReferenceEquals(fog, display.Root.GetNode<Node3D>("LoadingFog")) && fog.GetChildCount() == 3 &&
            ReferenceEquals(retainedBox.GetParent(), fog) && fogExits == 0,
            "换雾只接入新增矩形，未变化矩形不能重建或退出树，重复矩形仍须分别显示。");
        var retiredBoxes = fog.GetChildren().OfType<MeshInstance3D>().Where(item => item != retainedBox).ToArray();
        var partiallyRemoved = Frame(4, [], changedRegions.Update.Frame, fog: [regions[0], new(new(8), new(10))]);
        Commit(display, partiallyRemoved);
        Require(fog.GetChildCount() == 2 && ReferenceEquals(retainedBox.GetParent(), fog) && fogExits == 0 &&
            GodotObject.IsInstanceValid(retainedBox.Mesh) && GodotObject.IsInstanceValid(retainedBox.MaterialOverride) &&
            retiredBoxes.All(item => !GodotObject.IsInstanceValid(item)),
            "同时增删雾矩形只释放移除项，保留项及其网格、材质必须仍有效。");
        var replacement = Frame(5, [], partiallyRemoved.Update.Frame, fog: []);
        Commit(display, replacement);
        Require(!GodotObject.IsInstanceValid(retainedBox) && fog.GetChildCount() == 0,
            "删除雾区域必须释放退役矩形并保留空雾组。");
        Require(display.Root.GetNodeOrNull<Node3D>("LoadingFog") is { } currentFog &&
            ReferenceEquals(currentFog.GetParent(), display.Root), "换雾组不能因根节点重名失去当前组的命名入口。");
    }

    [Test]
    public void Removing_parents_and_children_retires_owned_resources_but_keeps_shared_meshes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var shared = Mesh();
        var first = Frame(1, [Object(1, shared), Object(2, shared, parent: new(1)), Object(3, shared)]);
        Commit(display, first);
        var parent = display.Objects[new(1)].Root;
        var child = display.Objects[new(2)].Root;
        var survivor = display.Objects[new(3)].Root;
        var sharedMesh = MeshOf(display.Objects[new(3)]);
        Commit(display, Frame(2, [], first.Update.Frame, removed: [new(2), new(1)]));
        Require(!GodotObject.IsInstanceValid(parent) && !GodotObject.IsInstanceValid(child),
            "删除父子对象必须分别退役两个节点。");
        Require(GodotObject.IsInstanceValid(sharedMesh) && GodotObject.IsInstanceValid(survivor),
            "移除部分使用者不能释放仍在显示的共享网格。");
        Require(display.Objects.Count == 1 && ReferenceEquals(survivor.GetParent(), display.Root),
            "剩余根对象必须仍在显示树中。");
        var replacement = Frame(3, [Object(4, Mesh())]);
        Commit(display, replacement);
        Require(!GodotObject.IsInstanceValid(survivor) && !GodotObject.IsInstanceValid(sharedMesh),
            "完整替换后最后使用者和共享网格必须退役。");
        Require(display.Objects.Count == 1 && display.Objects.ContainsKey(new(4)),
            "完整替换不能保留旧对象。");
        var lastMesh = MeshOf(display.Objects[new(4)]);
        Commit(display, Frame(4, []));
        Require(!GodotObject.IsInstanceValid(lastMesh) && display.Objects.Count == 0,
            "空完整帧也必须清除旧显示组并释放独占网格。");
    }

    [Test]
    public void Model_meter_coordinates_and_bottom_center_origin_compose_with_the_supplied_pose()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Glb(dispatcher, "model-coordinates");
        using var display = Attach(cache, new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) }
        });
        var model = new SceneVisual.Model(asset.Revision,
            [MaterialCategory.PaintedMetal, MaterialCategory.Concrete, MaterialCategory.Glass],
            null, new(false, 0, 0, false), new(0, 1, 0, 0));
        var source = new SceneObject(new(1), null, new(NVector3.Zero, NQuaternion.Identity),
            null, [model], [], []);
        var first = Frame(1, [source]);
        Commit(display, first);
        var instance = display.Objects[source.Id].Visuals[0].Node.GetChild<MeshInstance3D>(0);
        var mesh = instance.Mesh;
        using var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        Near(vertices[0], new(-0.5f, 0, -0.25f));
        Near(vertices[1], new(0.25f, 0, -0.25f));
        Near(vertices[2], new(-0.5f, 0.8f, -0.15f));
        using var frontArrays = mesh.SurfaceGetArrays(2);
        Near(frontArrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array()[2], new(-0.5f, 0.8f, 0.15f));
        Near(instance.GlobalPosition, Vector3.Zero);
        Near(instance.GlobalTransform * vertices[2], new(-0.5f, 0.8f, -0.15f));
        Commit(display, Frame(2, [source with
        {
            Pose = new(new(7, -3, 11), NQuaternion.CreateFromAxisAngle(NVector3.UnitY, MathF.PI / 2))
        }], first.Update.Frame));
        Require(instance.Mesh.GetInstanceId() == mesh.GetInstanceId(), "模型姿态变化重建了网格。");
        Near(instance.GlobalPosition, new(7, -3, 11));
        Near(instance.GlobalTransform * vertices[0], new(6.75f, -3, 11.5f));
        Near(instance.GlobalTransform * vertices[2], new(6.85f, -2.2f, 11.5f));
    }

    [Test]
    public void Model_material_leases_follow_candidate_cancellation_removal_and_asset_reload()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var oldAsset = Glb(dispatcher, "old-model");
        var reloadedAsset = Glb(dispatcher, "reloaded-model", 0.5f);
        using var display = Attach(cache, new FixedContentView
        {
            Assets = new[] { oldAsset, reloadedAsset }.ToDictionary(asset => asset.Revision,
                asset => (ContentLookup<ContentAsset>)new ContentLookup<ContentAsset>.Found(asset))
        });
        var model = new SceneVisual.Model(oldAsset.Revision,
            [MaterialCategory.PaintedMetal, MaterialCategory.Concrete, MaterialCategory.Glass],
            new("#204080"), new(true, 17, 0, false), new(0, 1, 0, 0));
        SceneObject Source(ulong id, SceneVisual.Model value) => new(new(id), null,
            new(NVector3.Zero, NQuaternion.Identity), null, [value], [], []);
        ShaderMaterial Material(SceneDisplay.DisplayObject value) =>
            (ShaderMaterial)value.Visuals[0].Node.GetChild<MeshInstance3D>(0).GetSurfaceOverrideMaterial(0);
        var first = Frame(1, [Source(1, model), Source(2, model)]);
        Commit(display, first);
        var shared = Material(display.Objects[new(1)]);
        var mesh = display.Objects[new(1)].Visuals[0].Node.GetChild<MeshInstance3D>(0).Mesh;
        Require(shared.GetInstanceId() == Material(display.Objects[new(2)]).GetInstanceId(),
            "同资产和表面状态没有共享模型材质。");
        var repainted = model with { Paint = new("#803020") };
        var changedSurface = model with { Surface = new(true, 18, 0, false) };
        var pending = Prepare(display, Frame(2, [Source(1, repainted), Source(3, changedSurface)], first.Update.Frame));
        var pendingPaint = Material(pending.Objects[new(1)]);
        var pendingSurface = Material(pending.Objects[new(3)]);
        Require(pendingPaint.GetInstanceId() != shared.GetInstanceId() &&
            pendingSurface.GetInstanceId() != shared.GetInstanceId() &&
            pendingPaint.GetInstanceId() != pendingSurface.GetInstanceId(), "涂装或种子变化污染了共享材质。");
        pending.Dispose();
        Require(!GodotObject.IsInstanceValid(pendingPaint) && !GodotObject.IsInstanceValid(pendingSurface) &&
            GodotObject.IsInstanceValid(shared), "取消候选没有退役新材质，或错误释放了活动材质。");
        var second = Frame(2, [Source(1, repainted)], first.Update.Frame);
        Commit(display, second);
        var painted = Material(display.Objects[new(1)]);
        Require(GodotObject.IsInstanceValid(shared), "改涂一个实例释放了仍被另一实例使用的材质。");
        var third = Frame(3, [], second.Update.Frame, removed: [new(2)]);
        Commit(display, third);
        Require(!GodotObject.IsInstanceValid(shared) && GodotObject.IsInstanceValid(painted),
            "删除最后旧材质使用者没有单独退役该材质。");
        var fourth = Frame(4, [Source(1, repainted with { Asset = reloadedAsset.Revision })], third.Update.Frame);
        Commit(display, fourth);
        var reloaded = Material(display.Objects[new(1)]);
        Require(!GodotObject.IsInstanceValid(painted) && !GodotObject.IsInstanceValid(mesh) &&
            GodotObject.IsInstanceValid(reloaded), "资产重载没有退役旧模型和旧材质。");
        Commit(display, Frame(5, []));
        Require(!GodotObject.IsInstanceValid(reloaded), "空完整帧没有释放最后模型材质。");
    }

    [Test]
    public async Task Incremental_shift_removal_and_multiple_visuals_match_uninterrupted_preparation()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var sliced = Attach(cache, new FixedContentView());
        using var whole = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh(), new(10, 0, 0)),
            Object(2, Mesh(), new(2, 0, 0), new(1)), Object(3, Mesh())]);
        Commit(sliced, first);
        Commit(whole, first);
        var multiple = Object(4, Peak(2), new(7, 0, 0)) with
        {
            Visuals = [new SceneVisual.Geometry(Peak(2), new(false, 0, 0, false)),
                new SceneVisual.Geometry(Peak(3), new(false, 0, 0, false)),
                new SceneVisual.Geometry(Peak(4), new(false, 0, 0, false))]
        };
        var delta = Frame(2, [multiple, Object(5, Peak(5), new(9, 0, 0))], first.Update.Frame,
            shift: new(-100, 0, 0), origin: new(100, 0, 0), removed: [new(3)],
            fog: [new(new(-10), new(10))]);
        Prime(sliced, delta);
        long timestamp = 0;
        sliced.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var calls = 0;
        SceneDisplay.PreparedFrame? prepared;
        while ((prepared = sliced.Prepare(delta)) is null && calls++ < 16)
        {
            Require(ReferenceEquals(sliced.DisplayedFrame, first) && sliced.Objects.ContainsKey(new(3)),
                "断点准备不能提前发布移除或新对象。");
            Near(sliced.Objects[new(1)].Root.GlobalPosition, new(10, 0, 0));
        }
        Require(prepared is not null && calls >= 3, "多视觉与雾必须跨调用恢复并在有限步骤内完成。");
        using (prepared) prepared!.Commit();
        await new UiTestInput(TestScene).Frames(1);
        Prime(whole, delta);
        using (var uninterrupted = whole.Prepare(delta))
        {
            Require(uninterrupted is not null, "结果就绪后零耗时准备必须一次完成同一增量。");
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

    [Test]
    public void Completed_meshes_upload_in_input_order_while_later_packing_is_blocked()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        foreach (var candidate in new[] { false, true })
        {
            using var display = Attach(cache, new FixedContentView());
            var baseline = Frame(1, []);
            Commit(display, baseline);
            using var secondEntered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var firstMesh = Peak(11);
            var secondMesh = Peak(12);
            List<SceneMesh> order = [];
            display.PackMesh = (mesh, cancellation) =>
            {
                order.Add(mesh);
                if (ReferenceEquals(mesh, secondMesh))
                {
                    secondEntered.Set();
                    Require(release.Wait(TimeSpan.FromSeconds(30)), "未释放后续网格打包。");
                }
                return PackedSceneMesh.Create(mesh, cancellation);
            };
            var frame = Frame(2, [Object(2, firstMesh), Object(3, secondMesh)],
                candidate ? null : baseline.Update.Frame, space: candidate ? 2UL : 1UL);
            var before = OrphanIds();
            List<SceneDisplayEvent> events = [];
            display.Observe = events.Add;
            if (candidate)
            {
                display.ReceiveCandidate(frame.Update);
                display.PrepareCandidate(TimeSpan.Zero);
            }
            else Require(display.Prepare(frame, TimeSpan.Zero) is null, "零预算只启动作业。");
            var packing = candidate ? display.CandidatePacking : display.PreparationPacking;
            try
            {
                Require(secondEntered.Wait(TimeSpan.FromSeconds(30)), "作业必须顺序打包到第二个网格。");
                if (candidate)
                {
                    display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                    Require(display.CandidateProgress(frame.Update.Frame) is (1, 2),
                        "首个网格完成即应上传，不能等待后续打包结束。");
                }
                else
                {
                    Require(display.Prepare(frame) is null && events.Count(item =>
                        item.Stage == SceneDisplayStage.ResourcesPrepared && item.Object == new SceneObjectId(2)) == 1,
                        "后续打包阻塞时，首个对象必须先发出资源就绪事件。");
                }
                Require(!packing.IsCompleted && ReferenceEquals(display.DisplayedFrame, baseline) &&
                    NewOrphanRoots(before).Length >= 1, "后台仍未完成时必须已有脱树资源，显示基准保持不变。");
            }
            finally { release.Set(); }
            packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Require(order.SequenceEqual(new[] { firstMesh, secondMesh }), "一个作业必须按输入顺序打包全部未缓存网格。");
        }
    }

    [Test]
    public void Cancelled_packing_discards_results_before_and_after_cancellation()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        foreach (var candidate in new[] { false, true })
        foreach (var readyFirst in new[] { false, true })
        {
            using var display = Attach(cache, new FixedContentView());
            var first = Frame(1, [Object(1, Mesh())]);
            Commit(display, first);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            display.PackMesh = (mesh, _) =>
            {
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(30)), "取消测试未释放后台打包。");
                return PackedSceneMesh.Create(mesh, CancellationToken.None);
            };
            var pending = Frame(2, [Object(2, Peak(2))], candidate ? null : first.Update.Frame,
                space: candidate ? 2UL : 1UL);
            if (candidate)
            {
                display.ReceiveCandidate(pending.Update);
                display.PrepareCandidate(TimeSpan.Zero);
            }
            else Require(display.Prepare(pending, TimeSpan.Zero) is null, "后台准备不得提前完成。");
            var packing = candidate ? display.CandidatePacking : display.PreparationPacking;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(30)), "后台打包未启动。");
                if (readyFirst)
                {
                    release.Set();
                    packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                }
                if (candidate) display.CancelCandidate();
                else display.CancelPreparation();
            }
            finally { release.Set(); }
            packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            display.PackMesh = PackedSceneMesh.Create;
            var next = Frame(3, [Object(3, Peak(3))], first.Update.Frame);
            using var prepared = Prepare(display, next);
            prepared.Commit();
            Require(ReferenceEquals(display.DisplayedFrame, next) && display.Objects.ContainsKey(new(3)) &&
                !display.Objects.ContainsKey(new(2)) && display.PreparedCandidate is null,
                "取消前已发布或取消后迟到的后台结果均不能接入最新帧。");
        }
    }

    [Test]
    public void Packing_failures_are_rethrown_by_main_thread_preparation_and_allow_retry()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        foreach (var candidate in new[] { false, true })
        foreach (var readyFirst in new[] { false, true })
        {
            using var display = Attach(cache, new FixedContentView());
            var first = Frame(1, [Object(1, Mesh())]);
            Commit(display, first);
            var failure = new InvalidOperationException("受控后台打包失败");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            display.PackMesh = (_, _) =>
            {
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(30)), "异常测试未释放后台打包。");
                throw failure;
            };
            var pending = Frame(2, [Object(2, Peak(2))], candidate ? null : first.Update.Frame,
                space: candidate ? 2UL : 1UL);
            if (candidate)
            {
                display.ReceiveCandidate(pending.Update);
                display.PrepareCandidate(TimeSpan.Zero);
            }
            else Require(display.Prepare(pending, TimeSpan.Zero) is null, "零预算只启动作业。");
            var packing = candidate ? display.CandidatePacking : display.PreparationPacking;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(30)), "后台打包必须启动并等待发布异常。");
                if (!readyFirst)
                {
                    if (candidate)
                    {
                        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                        Require(display.PreparedCandidate is null, "异常尚未发布时，候选必须继续等待。");
                    }
                    else Require(display.Prepare(pending) is null, "异常尚未发布时，正式准备必须继续等待。");
                    Require(!packing.IsCompleted, "门控释放前后台作业不能完成。");
                }
            }
            finally { release.Set(); }
            try { packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
            Exception? observed = null;
            try
            {
                if (candidate) display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                else { using var unexpected = display.Prepare(pending); }
            }
            catch (InvalidOperationException error) { observed = error; }
            Require(ReferenceEquals(observed, failure) && ReferenceEquals(display.DisplayedFrame, first) &&
                display.Objects.Count == 1, "后台异常必须由主线程准备原样重抛且保留旧显示。");
            display.CancelPreparation();
            display.CancelCandidate();
            display.PackMesh = PackedSceneMesh.Create;
            var next = Frame(3, [Object(3, Peak(3))], first.Update.Frame);
            Commit(display, next);
            Require(ReferenceEquals(display.DisplayedFrame, next), "失败清理后必须允许下一帧重新准备。");
        }
    }

    [Test]
    public void Promotion_cancels_unread_candidate_packing_and_retains_prepared_visuals()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        foreach (var registerBeforeRelease in new[] { true, false })
        {
            using var display = Attach(cache, new FixedContentView());
            var cachedSource = Object(1, Peak(registerBeforeRelease ? 35 : 36));
            Commit(display, Frame(1, [cachedSource]));
            var cachedMesh = MeshOf(display.Objects[cachedSource.Id]);
            var firstMesh = Peak(registerBeforeRelease ? 31 : 32);
            var blockedMesh = Peak(registerBeforeRelease ? 33 : 34);
            var first = Object(2, firstMesh);
            var retained = Object(3, firstMesh);
            var blocked = Object(4, blockedMesh);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var cancelled = new ManualResetEventSlim();
            display.PackMesh = (mesh, cancellation) =>
            {
                if (!ReferenceEquals(mesh, blockedMesh)) return PackedSceneMesh.Create(mesh, cancellation);
                using var registration = registerBeforeRelease ? cancellation.Register(cancelled.Set) : default;
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(30)), "晋升取消测试未释放候选打包。");
                using var lateRegistration = registerBeforeRelease ? default : cancellation.Register(cancelled.Set);
                return PackedSceneMesh.Create(mesh, cancellation);
            };
            var candidate = Frame(2, [first, retained, blocked, cachedSource], space: 2);
            var before = OrphanIds();
            display.ReceiveCandidate(candidate.Update);
            display.PrepareCandidate(TimeSpan.Zero);
            var packing = display.CandidatePacking;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(30)), "候选必须先发布首个网格并停在下一网格。");
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                var detached = NewOrphanRoots(before);
                Require(detached.Length == 2 && display.CandidateProgress(candidate.Update.Frame) is (2, 4),
                    "晋升前必须已有两个待取用视觉，剩余网格仍在打包。");
                var meshes = detached.ToDictionary(node => node, node => node.GetChild<MeshInstance3D>(0).Mesh);
                var formal = Frame(3, [first], space: 2);
                Commit(display, formal);
                var firstVisual = display.Objects[first.Id].Visuals[0];
                Require(detached.Contains(firstVisual.Node) && GodotObject.IsInstanceValid(firstVisual.Node) &&
                    ReferenceEquals(meshes[firstVisual.Node], MeshOf(display.Objects[first.Id])),
                    "首帧必须取用候选视觉及原网格。");
                release.Set();
                packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                Require(cancelled.IsSet, "晋升必须取消并结束不再消费的候选打包作业。");
                Require(!GodotObject.IsInstanceValid(cachedMesh), "晋升必须释放未取用候选的缓存网格租约。");
                display.PackMesh = PackedSceneMesh.Create;
                var next = Frame(4, [retained, blocked], formal.Update.Frame, space: 2);
                Commit(display, next);
                var retainedVisual = display.Objects[retained.Id].Visuals[0];
                Require(detached.Contains(retainedVisual.Node) && GodotObject.IsInstanceValid(retainedVisual.Node) &&
                    ReferenceEquals(meshes[retainedVisual.Node], MeshOf(display.Objects[retained.Id])) &&
                    ReferenceEquals(firstVisual, display.Objects[first.Id].Visuals[0]) &&
                    GodotObject.IsInstanceValid(MeshOf(display.Objects[blocked.Id])),
                    "取消打包必须保留未取用候选视觉，后续正式批次复用原资源并正常准备剩余网格。");
            }
            finally
            {
                release.Set();
                packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
        }
    }

    [Test]
    public void Candidate_packing_before_and_after_promotion_keeps_the_formal_visual()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        foreach (var readyFirst in new[] { false, true })
        {
            using var display = Attach(cache, new FixedContentView());
            Commit(display, Frame(1, []));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var mainThread = System.Environment.CurrentManagedThreadId;
            display.PackMesh = (mesh, _) =>
            {
                if (System.Environment.CurrentManagedThreadId != mainThread)
                {
                    entered.Set();
                    Require(release.Wait(TimeSpan.FromSeconds(30)), "晋升测试未释放候选打包。");
                }
                return PackedSceneMesh.Create(mesh, CancellationToken.None);
            };
            var source = Object(2, Peak(readyFirst ? 8 : 9), new(7, 0, 0));
            var candidate = Frame(2, [source], space: 2);
            display.ReceiveCandidate(candidate.Update);
            display.PrepareCandidate(TimeSpan.Zero);
            var packing = display.CandidatePacking;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(30)), "候选后台作业必须启动。");
                if (readyFirst)
                {
                    release.Set();
                    packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                }
                var formal = Frame(3, [source with { Pose = new(new(-70, 4, 12), NQuaternion.Identity) }], space: 2);
                using var prepared = display.Prepare(formal);
                Require(prepared is not null, "候选打包是否完成均不能阻止新空间首帧一次准备完成。");
                prepared!.Commit();
                var visual = display.Objects[source.Id].Visuals[0];
                var mesh = MeshOf(display.Objects[source.Id]);
                release.Set();
                packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                Require(ReferenceEquals(display.Objects[source.Id].Visuals[0], visual) &&
                    ReferenceEquals(MeshOf(display.Objects[source.Id]), mesh) &&
                    ReferenceEquals(display.DisplayedFrame, formal), "迟到候选结果不得覆盖或重建正式视觉。");
                Near(display.Objects[source.Id].Root.GlobalPosition, new(-70, 4, 12));
                display.CancelCandidate();
                Require(GodotObject.IsInstanceValid(visual.Node), "取消剩余候选不能释放晋升后的正式视觉。");
            }
            finally { release.Set(); }
        }
    }

    [Test]
    public void Packing_keeps_cached_geometry_alive_when_the_other_preparation_is_cancelled()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        List<string> failures = [];
        foreach (var formalDependsOnCandidate in new[] { true, false })
        foreach (var sameKey in formalDependsOnCandidate ? new[] { false, true } : new[] { false })
        {
            using var display = Attach(cache, new FixedContentView());
            var baseline = Frame(1, []);
            Commit(display, baseline);
            var a = Peak(21);
            var b = Peak(22);
            var c = Peak(23);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            display.PackMesh = (mesh, cancellation) =>
            {
                if (ReferenceEquals(mesh, b))
                {
                    entered.Set();
                    Require(release.Wait(TimeSpan.FromSeconds(30)), "缓存租约测试未释放网格打包。");
                }
                return PackedSceneMesh.Create(mesh, cancellation);
            };
            if (formalDependsOnCandidate)
            {
                var candidate = Frame(2, [Object(2, a)]);
                display.ReceiveCandidate(candidate.Update);
                PrimeCandidate(display);
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                Require(display.PreparedCandidate == candidate.Update.Frame, "候选必须先独占A的网格租约。");
                var formalId = sameKey ? 2UL : 4UL;
                var formal = Frame(3, [Object(3, b), Object(formalId, a)], baseline.Update.Frame);
                Require(display.Prepare(formal, TimeSpan.Zero) is null, "正式帧只启动打包。");
                var packing = display.PreparationPacking;
                try
                {
                    Require(entered.Wait(TimeSpan.FromSeconds(30)), "正式打包必须停在B，尚未消费A。");
                    display.CancelCandidate();
                }
                finally { release.Set(); }
                packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                using var prepared = display.Prepare(formal);
                if (prepared is null) failures.Add($"正式打包依赖的{(sameKey ? "同键" : "不同键")}候选A租约在取消候选后丢失");
                else
                {
                    prepared.Commit();
                    Require(display.Objects.Count == 2 && display.Objects[new(formalId)].Visuals.Length == 1 &&
                        GodotObject.IsInstanceValid(MeshOf(display.Objects[new(formalId)])), "正式帧必须持有有效A资源。");
                }
            }
            else
            {
                var formal = Frame(2, [Object(2, a), Object(3, b)], baseline.Update.Frame);
                Require(display.Prepare(formal, TimeSpan.Zero) is null, "正式帧只启动打包。");
                var formalPacking = display.PreparationPacking;
                Task? candidatePacking = null;
                var candidate = Frame(3, [Object(4, c), Object(5, a)], space: 2);
                try
                {
                    Require(entered.Wait(TimeSpan.FromSeconds(30)), "正式打包必须先发布A并停在B。");
                    Require(display.Prepare(formal) is null, "正式帧必须持有A视觉并等待B。");
                    display.ReceiveCandidate(candidate.Update);
                    display.PrepareCandidate(TimeSpan.Zero);
                    candidatePacking = display.CandidatePacking;
                    candidatePacking.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    display.CancelPreparation();
                }
                finally { release.Set(); }
                formalPacking.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                if (display.PreparedCandidate != candidate.Update.Frame)
                    failures.Add("候选打包依赖的正式A租约在取消正式准备后丢失");
                else
                {
                    var promoted = Frame(4, candidate.Update.Upserts, space: 2);
                    using var prepared = display.Prepare(promoted);
                    Require(prepared is not null, "候选持有的资源必须能够晋升。");
                    prepared!.Commit();
                    Require(display.Objects.Count == 2 && GodotObject.IsInstanceValid(MeshOf(display.Objects[new(5)])),
                        "候选晋升后必须持有有效A资源。");
                }
            }
        }
        Require(failures.Count == 0, string.Join("；", failures));
    }

    private static HashSet<ulong> OrphanIds()
    {
        using var nodes = (Godot.Collections.Array)Node.GetOrphanNodeIds();
        return nodes.Select(id => id.AsUInt64()).ToHashSet();
    }

    private static Node3D[] NewOrphanRoots(HashSet<ulong> before) => OrphanIds().Except(before)
        .Select(GodotObject.InstanceFromId).OfType<Node3D>().Where(node => node.GetParent() is null).ToArray();

    private SceneDisplay Attach(AssetResourceCache cache, FixedContentView content, bool completeInitialFrame = false)
    {
        var display = new SceneDisplay(cache, content, completeInitialFrame) { Timestamp = () => 0 };
        TestScene.AddChild(display.Root);
        return display;
    }

    private static SceneDisplay.PreparedFrame Prepare(SceneDisplay display, ISceneFrame frame)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (display.Prepare(frame) is { } prepared) return prepared;
            display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        throw new InvalidOperationException("已配置的可显示帧准备超时。");
    }

    private static void Prime(SceneDisplay display, ISceneFrame frame)
    {
        var timestamp = display.Timestamp;
        try
        {
            display.Timestamp = () => 0;
            using var prepared = display.Prepare(frame, TimeSpan.Zero);
            Require(prepared is null, "零预算只能启动后台打包，不能完成有新视觉的同空间帧。");
            display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        finally { display.Timestamp = timestamp; }
    }

    private static void PrimeCandidate(SceneDisplay display)
    {
        display.PrepareCandidate(TimeSpan.Zero);
        display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    private static void Commit(SceneDisplay display, ISceneFrame frame)
    {
        using var candidate = Prepare(display, frame);
        candidate.Commit();
    }

    private static Mesh MeshOf(SceneDisplay.DisplayObject item) =>
        ((MeshInstance3D)item.Visuals[0].Node.GetChild(0)).Mesh;

    private static FixedSceneFrame Frame(ulong revision, ImmutableArray<SceneObject> objects,
        SceneFrameStamp? @base = null, NVector3 shift = default, ScenePoint origin = default,
        SceneReadiness readiness = SceneReadiness.Ready, ImmutableArray<SceneObjectId> removed = default,
        ImmutableArray<SceneBounds> fog = default, ImmutableArray<SceneDebugLine> debugLines = default,
        ulong space = 1, ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>? poses = null,
        SceneSessionId? session = null, ulong request = 1) => new()
    {
        Origin = origin,
        Readiness = readiness,
        DebugLines = debugLines.IsDefault ? [] : debugLines,
        Poses = poses ?? ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty,
        Update = new(@base, new(session ?? Session, new(request), new(space), revision), shift, objects,
            removed.IsDefault ? [] : removed, new(fog.IsDefault ? [] : fog, []), [])
    };

    private static SceneObject Object(ulong id, SceneMesh mesh, NVector3 position = default,
        SceneObjectId? parent = null) => new(new(id), parent, new(position, NQuaternion.Identity),
        null, [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);

    private static SceneMesh Peak(float height) => new(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
        [
            new(NVector3.Zero, NVector3.UnitY, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
            new(NVector3.UnitX, NVector3.UnitY, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
            new(new(0, height, 0), NVector3.UnitY, new(0, 1), new(0, 1), ExposedFaceEdges.None, 1, 0)
        ], [0, 1, 2]);

    private static SceneMesh Mesh(SceneMaterial? material = null) => new(
        material ?? new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
        [
            new(NVector3.Zero, NVector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
            new(NVector3.UnitX, NVector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
            new(NVector3.UnitY, NVector3.UnitZ, new(0, 1), new(0, 1), ExposedFaceEdges.None, 1, 0)
        ], [0, 1, 2]);

    private static ContentAsset Png(string revision)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        return new(new(revision), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(4, 4), []);
    }

    private static ContentAsset Glb(GodotMainThreadDispatcher dispatcher, string revision, float tint = 1)
    {
        var bytes = GlbFixture.Create(feature: "cell", baseColorFactor: [tint, tint, tint, 1]);
        var result = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new(revision), bytes);
        Require(result is AssetInspection.Success, "模型夹具无法解码。");
        return new(new(revision), bytes, ((AssetInspection.Success)result).Metadata, []);
    }

    private static void Near(Vector3 actual, Vector3 expected) => Require(actual.DistanceTo(expected) < 0.001,
        $"期望坐标 {expected}，实际 {actual}。");

    private static void NearY(float? actual, float expected) =>
        Require(actual is { } value && MathF.Abs(value - expected) < 0.001f, $"顶面应为 {expected}，实际 {actual}。");
}

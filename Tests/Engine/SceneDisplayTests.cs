using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
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
    public async Task Latest_candidate_keeps_compatible_work_and_commits_only_latest_pose()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Mesh(), new(10, 0, 0))]);
        Commit(display, first);
        var mesh = Mesh();
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var previous = Frame(2, [Object(2, mesh), Object(3, mesh)], first.Update.Frame);
        Require(display.Prepare(previous) is null, "旧候选必须在第一个资源后暂停。");
        var latest = Frame(3, [Object(2, mesh, new(30, 0, 0)), Object(3, mesh)], first.Update.Frame);
        display.UpdatePoses(latest);
        using var prepared = display.Prepare(latest);
        Require(prepared is not null, "兼容资源必须保留，使第二次调用完成最新候选。");
        Require(ReferenceEquals(display.PoseFrame, latest) && ReferenceEquals(display.DisplayedFrame, first),
            "准备期间姿态帧必须更新，网格基准必须保持最后完整帧。");
        prepared!.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, latest), "不得提交被最新帧取代的候选。");
        Near(display.Objects[new(2)].Root.GlobalPosition, new(30, 0, 0));
        await new UiTestInput(TestScene).Frames(2);
    }

    [Test]
    public async Task Superseded_pending_textures_are_released_before_budget_return()
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
        for (var keepObject = 0; keepObject < 2; keepObject++)
        {
            using var observer = cache.AcquireFaceTexture(asset);
            var texture = observer.Value;
            long timestamp = 0;
            display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
            Require(display.Prepare(Frame(2, [Object(2, textured), Object(3, Mesh())], first.Update.Frame)) is null,
                "纹理对象准备后必须暂停。");
            observer.Dispose();
            Require(GodotObject.IsInstanceValid(texture), "待用资源必须持有纹理。");
            var latest = Frame(3,
                [Object(keepObject == 0 ? 4UL : 2UL, Mesh()), Object(5, Mesh())], first.Update.Frame);
            Require(display.Prepare(latest) is null, "最新候选仍须遵守单项推进预算。");
            Require(!GodotObject.IsInstanceValid(texture),
                "预算提前返回前必须释放已删除对象或不兼容视觉的待用纹理。");
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
    public async Task Cross_space_loading_anchors_old_geometry_and_subject_moves_its_children()
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
        Require(display.Prepare(latest) is null && ReferenceEquals(display.DisplayedFrame, first),
            "完整替换准备期间必须保留旧网格基准。");
        Require(ReferenceEquals(display.PoseFrame, latest), "旧几何必须使用最新完整替换帧的姿态。");
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
        long timestamp = 0;
        first.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var processFrame = Godot.Engine.GetProcessFrames();
        Require(first.Prepare(Frame(1, [Object(1, Mesh()), Object(2, Mesh())])) is null,
            "先调用的显示器必须先耗尽当前进程帧的预算。");
        var candidate = Frame(2, [Object(1, Mesh()), Object(2, Mesh()), Object(3, Mesh())]);
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
        try { using var unexpected = display.Prepare(failed); }
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
        var replacement = Frame(3, [], next.Update.Frame, fog: []);
        Commit(display, replacement);
        Require(!GodotObject.IsInstanceValid(fog), "雾区域变化后必须释放旧雾节点。");
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

    private SceneDisplay Attach(AssetResourceCache cache, FixedContentView content)
    {
        var display = new SceneDisplay(cache, content) { Timestamp = () => 0 };
        TestScene.AddChild(display.Root);
        return display;
    }

    private static SceneDisplay.PreparedFrame Prepare(SceneDisplay display, ISceneFrame frame) =>
        display.Prepare(frame) ?? throw new InvalidOperationException("已配置的可显示帧未产生候选。");

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
        ulong space = 1, ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>? poses = null) => new()
    {
        Origin = origin,
        Readiness = readiness,
        DebugLines = debugLines.IsDefault ? [] : debugLines,
        Poses = poses ?? ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty,
        Update = new(@base, new(Session, new(1), new(space), revision), shift, objects,
            removed.IsDefault ? [] : removed, new(fog.IsDefault ? [] : fog, []), [])
    };

    private static SceneObject Object(ulong id, SceneMesh mesh, NVector3 position = default,
        SceneObjectId? parent = null) => new(new(id), parent, new(position, NQuaternion.Identity),
        null, [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);

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

    private static void Near(Vector3 actual, Vector3 expected) => Require(actual.DistanceTo(expected) < 0.001,
        $"期望坐标 {expected}，实际 {actual}。");
}

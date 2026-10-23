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
            if (item.Stage == SceneDisplayStage.Committed)
                Require(ReferenceEquals(display.DisplayedFrame, candidate) &&
                    display.Objects[new(3)].Root.IsInsideTree(), "提交记录必须在完整候选进入显示树后发出。");
            events.Add(item);
        };
        Require(display.Prepare(candidate) is null, "锁定候选必须在第一个资源后暂停。");
        Require(events.Count == 1 && events[0].Object == new SceneObjectId(2) &&
            events[0].Stage == SceneDisplayStage.ResourcesPrepared && events[0].Frame == candidate.Update.Frame,
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
        Require(events.Count == 2 && events.All(item => item.Stage == SceneDisplayStage.ResourcesPrepared),
            "分预算恢复不得重复记录对象资源，也不能提前记录提交。");
        prepared!.Commit();
        Require(events.Count == 4 && events.All(item => item.Frame == candidate.Update.Frame &&
            item.Candidate == events[0].Candidate) &&
            events.Count(item => item.Stage == SceneDisplayStage.Committed) == 2,
            "较新输入不能覆盖冻结候选的完整身份，提交必须逐对象记录。");
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
        var added = Object(1, Mesh()).Visuals[0];
        var source = original with { Visuals = [original.Visuals[0], added, added] };
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var frame = Frame(2, [source], baseline.Update.Frame);
        Require(display.Prepare(frame) is null && events.Count == 0,
            "复用一个、只新准备一个视觉资源不能记录整个对象资源就绪。");
        using var prepared = Prepare(display, frame);
        Require(events.Count == 1 && events[0].Object == source.Id &&
            events[0].Stage == SceneDisplayStage.ResourcesPrepared,
            "全部视觉资源准备完才记录一次对象资源就绪。");
        prepared.Commit();
        Require(events.Count == 2 && events[1].Stage == SceneDisplayStage.Committed,
            "对象提交应在资源就绪之后单独记录。");
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
        Require(events.Count == 0, "姿态、删减视觉与无视觉对象都没有新准备视觉资源，不应记录资源时延。");
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
        Require(display.Prepare(frame) is null, "候选必须在准备后续对象前暂停。");
        Require(events.Count == 1 && events[0].Object == new SceneObjectId(2) &&
            events[0].Stage == SceneDisplayStage.ResourcesPrepared,
            "只为新准备视觉资源的对象记录资源就绪，复用对象不记录。");
        var cancelledCandidate = events[0].Candidate;
        display.CancelPreparation();
        Require(events[^1] == new SceneDisplayEvent(cancelledCandidate, frame.Update.Frame, null,
            SceneDisplayStage.HostCancelled), "取消必须单独记录原候选身份。");
        display.CancelPreparation();
        Require(events.Count == 2, "重复清理不能重复记录取消。");
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
        Require(events.All(item => item.Stage != SceneDisplayStage.Committed), "取消候选不能记录成功提交。");
        using var retry = Prepare(display, frame);
        var committedCandidate = events[^1].Candidate;
        retry.Commit();
        Require(committedCandidate != abandonedCandidate && events.Count(item =>
            item.Stage == SceneDisplayStage.Committed && item.Candidate == committedCandidate) == 2,
            "再次重试的提交不能归因于已经取消的候选。");
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
            Require(display.Prepare(Frame(2, [Object(2, textured), Object(3, Mesh())])) is null,
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
        var processFrame = Godot.Engine.GetProcessFrames();
        Require(first.Prepare(Frame(1, [Object(1, Mesh()), Object(2, Mesh())], baseline.Update.Frame)) is null,
            "先调用的显示器必须先耗尽当前进程帧的预算。");
        var candidate = Frame(2, [Object(1, Mesh()), Object(2, Mesh()), Object(3, Mesh())], baseline.Update.Frame,
            fog: [new(new(10, 0, 0), new(11, 1, 1))]);
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

    private SceneDisplay Attach(AssetResourceCache cache, FixedContentView content, bool completeInitialFrame = false)
    {
        var display = new SceneDisplay(cache, content, completeInitialFrame) { Timestamp = () => 0 };
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

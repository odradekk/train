using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class WorldSceneTests(Node testScene) : TestClass(testScene)
{
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
    private static readonly SceneSessionId SceneId = new(Guid.Parse("00000000-0000-0000-0000-000000000319"));

    [Test]
    public async Task Map_open_keeps_overlay_hidden_and_commits_near_partial_frame()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var partial = Frame(2, readiness: SceneReadiness.Partial, pick: new ScenePick.Miss());
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                [Success(Frame(1, readiness: SceneReadiness.Loading)), Success(partial)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            var controls = world.View.GetChildren().OfType<CanvasLayer>().Single().GetChildren().OfType<Control>().ToArray();
            Require(controls.Length == 1 && controls[0] is Label &&
                controls[0].MouseFilter == Control.MouseFilterEnum.Ignore && controls[0].AnchorRight == 0 &&
                controls[0].AnchorBottom == 0, "打开地图节点只能显示不阻挡输入的局部提示，不得创建全屏罩。");
            world.ProcessFrame(0, false);
            Require(world.View.Loading && world.Display!.DisplayedFrame is null, "首帧前应保留局部加载提示。");
            world.ProcessFrame(0, false);
            Require(world.View.Interactive && !world.View.Loading && ReferenceEquals(world.Display!.DisplayedFrame, partial),
                "近处可操作的Partial必须接入，无需等待远处Ready。");
            world.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
            Require(partial.LastPickRay is not null && session!.PresentCalls.Count == 2,
                "已接入的Partial必须支持拾取。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Preparation_tracks_latest_poses_and_commits_the_latest_frame()
    {
        await new UiTestInput(TestScene).Frames(2);
        var fixture = new Fixture(TestScene.GetTree());
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        SceneObject Moving(float x) => Object(1) with { Subject = subject,
            Pose = new(new(x, 0, 0), System.Numerics.Quaternion.Identity) };
        ImmutableDictionary<WorldObjectId, Lookup<ScenePose>> Poses(float x) =>
            ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(Moving(x).Pose));
        var first = Frame(1, objects: [Moving(0)], poses: Poses(0), pick: new ScenePick.Miss());
        SceneObject[] streamed = [Object(6), Object(7), Object(8)];
        var candidates = Enumerable.Range(2, 3).Select(revision =>
            Frame((ulong)revision, first.Update.Frame, objects:
                [Moving(revision * 10), .. streamed], poses: Poses(revision * 10), pick: new ScenePick.Miss(),
                readiness: revision == 2 ? SceneReadiness.Partial : SceneReadiness.Ready)).ToArray();
        var next = Frame(5, candidates[^1].Update.Frame, objects: [Moving(50), Object(20)], poses: Poses(50));
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                [Success(first), .. candidates.Select(Success), Success(next)], target: target)));
            var world = app.WorldScenes!;
            world.Open(target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            long timestamp = 0;
            world.Display!.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
            var root = world.Display.Objects[new(1)].Root;
            for (var i = 0; i < 2; i++)
            {
                var oldFocus = world.View.CameraRig.Focus.X;
                world.ProcessFrame(0.1, false);
                Require(ReferenceEquals(world.Display.DisplayedFrame, first) &&
                    ReferenceEquals(world.Display.Objects[new(1)].Root, root) && world.View.Interactive,
                    "预算耗尽后必须保留已提交网格和交互。");
                Require(Math.Abs(root.GlobalPosition.X - (i + 2) * 10) < 0.001 &&
                    world.View.CameraRig.Focus.X > oldFocus && world.View.CameraRig.Focus.X < root.GlobalPosition.X,
                    "准备未完成时真实节点必须更新到最新姿态，跟随焦点也必须持续推进。");
                world.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
                Require(first.LastPickRay is null && candidates[i].LastPickRay is not null,
                    "准备期间拾取必须查询最新姿态帧。");
            }
            var beforeCommit = world.View.CameraRig.Focus.X;
            world.ProcessFrame(0.1, false);
            Require(ReferenceEquals(world.Display.DisplayedFrame, candidates[^1]) && world.Display.Objects.Count == 4 &&
                Math.Abs(root.GlobalPosition.X - 40) < 0.001 && world.View.CameraRig.Focus.X > beforeCommit,
                "兼容资源必须保留进度，提交最新帧且不能让节点或焦点倒退。");
            Require(session!.PresentCalls.Count == 4 && session.PresentCalls.Skip(1).All(call =>
                ReferenceEquals(call.DisplayedFrame, first)) &&
                ReferenceEquals(session.PresentCalls[2].Request.Camera!.Frame, candidates[0]) &&
                ReferenceEquals(session.PresentCalls[3].Request.Camera!.Frame, candidates[1]),
                "Present网格基准必须保持完整提交帧，相机样本则跟随实际最新姿态帧。");
            world.Display.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            Require(ReferenceEquals(world.Display.DisplayedFrame, next) &&
                ReferenceEquals(session.PresentCalls[^1].DisplayedFrame, candidates[^1]) &&
                ReferenceEquals(session.PresentCalls[^1].Request.Camera!.Frame, candidates[^1]),
                "提交后的下一轮必须从新的已提交帧开始。");
        }
        finally { await fixture.Cleanup(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Space_handoff_converts_pending_origin_shift_for_objects_camera_and_sample()
    {
        await new UiTestInput(TestScene).Frames(2);
        var fixture = new Fixture(TestScene.GetTree());
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        SceneObject Body(ulong id, float x) => Object(id) with { Subject = subject,
            Pose = new(new(x, 0, 0), System.Numerics.Quaternion.Identity) };
        ImmutableDictionary<WorldObjectId, Lookup<ScenePose>> Poses(float x) =>
            ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(Body(1, x).Pose));
        var scenery = Object(2) with { Pose = new(new(30, 0, 0), System.Numerics.Quaternion.Identity) };
        var first = Frame(1, objects: [Body(1, 10), scenery], poses: Poses(10));
        var shifted = Frame(2, first.Update.Frame, objects: [Body(1, 5), Object(3), Object(4)],
            shift: new(-10, 0, 0), origin: new(10, 0, 0), poses: Poses(5));
        var loading = Frame(3, readiness: SceneReadiness.Loading, objects: [Body(11, 15)],
            origin: new(1000, 0, 0), space: 2, poses: Poses(15));
        SceneObject[] replacementObjects = [Body(11, -85), scenery with { Id = new(22),
            Pose = new(new(-70, 0, 0), System.Numerics.Quaternion.Identity) }, Object(23)];
        var pending = Frame(4, objects: [.. replacementObjects], origin: new(1100, 0, 0), space: 2, poses: Poses(-85));
        var latest = Frame(5, objects: [.. replacementObjects], origin: new(1100, 0, 0), space: 2, poses: Poses(-85));
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                [Success(first), Success(shifted), Success(loading), Success(pending), Success(latest)], target: target)));
            var world = app.WorldScenes!;
            world.Open(target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            long timestamp = 0;
            world.Display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
            world.ProcessFrame(0, false);
            Require(world.Display.IsPreparing && Math.Abs(world.View.CameraRig.Focus.X) < 0.001 &&
                Math.Abs(world.Display.Objects[new(1)].Root.GlobalPosition.X - 5) < 0.001,
                "切换前必须建立未提交的原点平移和跟随余量。");
            world.ProcessFrame(0, false);
            Require(Math.Abs(world.View.CameraRig.Focus.X - 10) < 0.001 &&
                Math.Abs(world.Display.Objects[new(1)].Root.GlobalPosition.X - 15) < 0.001 &&
                Math.Abs(world.Display.Objects[new(2)].Root.GlobalPosition.X - 30) < 0.001,
                "新空间交接坐标相对D对齐，旧叠加平移必须与相机一起换算，不能重复保留。");
            world.ProcessFrame(0, false);
            Require(world.Display.IsPreparing && ReferenceEquals(world.Display.DisplayedFrame, first) &&
                Math.Abs(world.Display.Objects[new(1)].Root.GlobalPosition.X + 85) < 0.001 &&
                Math.Abs(world.Display.Objects[new(2)].Root.GlobalPosition.X + 70) < 0.001 &&
                Math.Abs(world.View.CameraRig.Focus.X + 90) < 0.001,
                "Loading锚点后的原点变化必须同时换算旧对象和相机，保持跟随余量。");
            Require(ReferenceEquals(session!.PresentCalls[^1].Request.Camera!.Frame, loading) &&
                Math.Abs(session.PresentCalls[^1].Request.Camera!.Focus.X - 10) < 0.001,
                "相机样本的来源帧和焦点坐标必须配对。");
            await new UiTestInput(TestScene).Frames(2);
            world.Display.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            Require(ReferenceEquals(world.Display.DisplayedFrame, latest) &&
                Math.Abs(world.Display.Objects[new(11)].Root.GlobalPosition.X + 85) < 0.001 &&
                Math.Abs(world.View.CameraRig.Focus.X + 90) < 0.001,
                "完整替换提交不得再次施加原点变化或重置跟随余量。");
        }
        finally { await fixture.Cleanup(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Pending_picks_only_select_existing_objects_without_visual_or_parent_changes()
    {
        await new UiTestInput(TestScene).Frames(2);
        foreach (var scenario in new[] { "moving", "new", "null", "parent", "visual", "removed", "replacement" })
        {
            var fixture = new Fixture(TestScene.GetTree());
            var first = Frame(1, objects: [Object(1), Object(2)], pick: new ScenePick.Miss());
            var changed = Object(1) with { Pose = new(new(-70, 0, 0), System.Numerics.Quaternion.Identity) };
            if (scenario == "parent") changed = changed with { Parent = new(2) };
            if (scenario == "visual") changed = changed with { Visuals =
                [new SceneVisual.Geometry(Mesh with { TriangleIndices = ImmutableArray.Create(0, 1, 2) }, new(false, 0, 0, false))] };
            var hit = new SceneHit(Stamp(2), scenario == "null" ? null : new SceneObjectId(scenario == "new" ? 20UL : 1UL),
                new PickSubject.Terrain(new WorldLocation.Node(new(1))), default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
            var pending = new FixedSceneFrame
            {
                Update = new(scenario == "replacement" ? null : first.Update.Frame, Stamp(2), new(-100, 0, 0),
                    scenario == "removed" ? [Object(20), Object(21), Object(22)] :
                        [changed, Object(20), Object(21), Object(22)],
                    scenario == "removed" ? [new(1)] : [], new([], []), []),
                Origin = new(100, 0, 0),
                PickResult = scenario == "removed" ? new ScenePick.Miss() : new ScenePick.Hit(hit),
                TerrainHeightResult = new Lookup<double>.Missing()
            };
            FixedDisplaySession? session = null;
            try
            {
                var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                    [Success(first), Success(pending), Success(pending)], resolve:
                    ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty.Add(hit,
                        new SceneResult<PickSubject>.Success(hit.Subject)))));
                var world = app.WorldScenes!;
                var selected = 0;
                world.Selected += _ => selected++;
                world.Open(Target);
                world.Display!.Timestamp = () => 0;
                world.ProcessFrame(0, false);
                var center = TestScene.GetViewport().GetVisibleRect().Size / 2;
                world.Pick(center);
                var originalRay = first.LastPickRay!;
                long timestamp = 0;
                world.Display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
                world.ProcessFrame(0, false);
                Require(world.Display.IsPreparing && ReferenceEquals(world.Display.DisplayedFrame, first) &&
                    world.Display.Objects.ContainsKey(new(1)), $"{scenario}没有保持跨帧准备及旧对象。");
                world.Pick(center);
                Require(selected == (scenario == "moving" ? 1 : 0) &&
                    session!.ResolveCalls.Count == selected, $"{scenario}的准备期选择资格错误。");
                if (scenario == "moving")
                {
                    Require(Math.Abs(world.Display.Objects[new(1)].Root.GlobalPosition.X + 70) < 0.001,
                        "已显示对象没有采用最新姿态。");
                    Require(NVector3.Distance(pending.LastPickRay!.Origin, originalRay.Origin + new NVector3(-100, 0, 0)) < 0.01f,
                        "准备期间相机与拾取没有使用相同的新原点。");
                    world.ProcessFrame(0, false);
                    Require(ReferenceEquals(session!.PresentCalls[^1].Request.Camera!.Frame, pending) &&
                        ReferenceEquals(session.PresentCalls[^1].Request.PickRay!.Frame, pending),
                        "下一轮相机与拾取样本必须携带最新姿态帧。");
                }
            }
            finally { await fixture.Cleanup(); await new UiTestInput(TestScene).Frames(2); }
        }
    }

    [Test]
    public async Task Debug_flags_are_requested_and_regeneration_retires_only_the_scene_session()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var sessions = new List<FixedDisplaySession>();
        var factory = Factory(binding =>
        {
            var session = Session(binding, [Success(Frame(1, pick: new ScenePick.Miss(), debugLines:
                [new(new(3, 4, 5), new(6, 7, 8), new(1, 1, 0, 1))]))]);
            sessions.Add(session);
            return session;
        });
        try
        {
            var app = await Ready(fixture, setup, factory);
            var world = app.WorldScenes!;
            Require(world.Regenerate()?.Code == "no_scene", "地图状态应拒绝重新生成。");
            world.DebugLayers = SceneDebugLayer.Chunks | SceneDebugLayer.Centerline;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            Require(sessions[0].PresentCalls.Single().Request.DebugLayers == world.DebugLayers,
                "所选调试图层没有传给Scenes。");
            using (var arrays = world.Display!.Root.GetNode<MeshInstance3D>("DebugLines").Mesh.SurfaceGetArrays(0))
            {
                var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
                Require(vertices.SequenceEqual(new[] { new Vector3(3, 4, 5), new Vector3(6, 7, 8) }),
                    "Scenes返回的调试线没有成为真实Godot几何。");
            }
            var oldRoot = world.Display!.Root;
            world.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
            var status = app.Host!.GetStatus()!;
            Require(world.Regenerate() is null, "活动场景重新生成失败。");
            Require(sessions.Count == 2 && sessions[0].IsDisposed && !GodotObject.IsInstanceValid(oldRoot),
                "重新生成必须退役旧场景会话和显示组。");
            Require(sessions[1].RequestCalls.Single() == Target && factory.OpenCalls[1].Snapshot.Core == status.View &&
                app.Host.GetStatus()!.Binding == status.Binding && setup.Session.DisposeCount == 0,
                "重新生成应复用当前目标与核心读视图，不得替换核心会话。");
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            Require(sessions[1].PresentCalls.Single().Request.DebugLayers == world.DebugLayers,
                "重新生成丢失调试图层选择。");
            Require(sessions[1].PresentCalls.Single().Request.PickRay is null,
                "重新生成后的新会话仍收到旧会话的拾取样本。");
        }
        finally { await fixture.Cleanup(); }
    }
    private static readonly SceneMesh Mesh = new(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
        [new(NVector3.Zero, NVector3.UnitY, default, default, 0, 0, 0),
         new(NVector3.UnitZ, NVector3.UnitY, default, default, 0, 0, 0),
         new(NVector3.UnitX, NVector3.UnitY, default, default, 0, 0, 0)], [0, 1, 2]);

    [Test]
    public async Task App_injects_the_scene_factory_and_presents_on_real_process_frames()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var scene = Session(default, Enumerable.Range(1, 12).Select(i => Success(Frame((ulong)i))).ToImmutableArray());
        var factory = Factory(binding => Session(binding, scene.PresentResults));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            app.CreateScenes = () => factory;
            await fixture.AddToRoot(app);
            await Until(() => app.Host!.GetStatus() is not null);
            app.WorldScenes!.Open(Target);
            Require(app.WorldScenes.View.Loading && !app.WorldScenes.View.Interactive, "首帧接入之前必须显示局部加载提示且禁止场景交互。");
            Require(app.IsProcessing(), "App未启用_Process。");
            await Until(() => app.WorldScenes.Display?.DisplayedFrame is not null);
            app.SetProcess(false);
            Require(!app.WorldScenes.View.Loading && app.WorldScenes.View.Interactive, "实际_Process没有完成场景接入。");
            Require(factory.OpenCalls.Count == 1 && factory.OpenCalls[0].Snapshot.Core == setup.Session.Read(), "场景未使用活动固定读视图。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Loading_base_mismatch_and_minimization_keep_the_last_display_boundary()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1, pick: new ScenePick.Miss(), height: new Lookup<double>.Found(100));
        var loading = Frame(2, readiness: SceneReadiness.Loading,
            pick: new ScenePick.NotReady(), height: new Lookup<double>.Found(200));
        var badBase = new FixedSceneFrame
        {
            Update = new(Stamp(90), Stamp(3), new(100, 0, -200),
                [Object(2) with { Pose = new(new(30, 0, 40), System.Numerics.Quaternion.Identity) }],
                [new(1)], new([], []), []),
            TerrainHeightResult = new Lookup<double>.Missing()
        };
        var replacement = Frame(4);
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                [Success(first), Success(loading), Success(badBase), Success(replacement)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            var root = world.Display!.Root;
            var oldObject = world.Display.Objects[new(1)].Root;
            var oldTransform = oldObject.Transform;
            world.ProcessFrame(0, true);
            Require(session!.PresentCalls.Count == 1, "最小化仍调用了Present。");
            world.ProcessFrame(0, false);
            Require(ReferenceEquals(world.Display!.DisplayedFrame, first), "Loading替换了旧显示帧。");
            world.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
            Require(first.LastPickRay is null && loading.LastPickRay is not null,
                "Loading期间仍必须使用最新姿态帧查询拾取，未就绪不能产生选择。");
            Require(world.View.CameraRig.Camera.Position.Y > 200,
                "准备期间相机必须使用最新帧同坐标系的地形高度。");
            world.ProcessFrame(0, false);
            Require(session!.PresentCalls[^1].Request.PickRay is { } missSample &&
                ReferenceEquals(missSample.Frame, loading) && ReferenceEquals(missSample.Ray, loading.LastPickRay),
                "未就绪拾取没有携带最新姿态帧和实际射线传给下一次Present。");
            Require(ReferenceEquals(world.Display.DisplayedFrame, first), "Base不符部分接入了更新。");
            Require(ReferenceEquals(world.Display.Root, root) && world.Display.Objects.Count == 1 &&
                ReferenceEquals(world.Display.Objects[new(1)].Root, oldObject) && oldObject.Transform == oldTransform,
                "Base不符仍更换显示根、增删对象或移动了旧对象。");
            Require(ReferenceEquals(session.PresentCalls[1].DisplayedFrame, first) &&
                ReferenceEquals(session.PresentCalls[2].DisplayedFrame, first), "Loading之后Present丢失了首帧边界。");
            world.ProcessFrame(0, false);
            Require(session.PresentCalls[^1].DisplayedFrame is null && world.Display.DisplayedFrame == replacement,
                "Base不符后没有用null请求完整替换。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Failed_faulted_failure_and_resource_errors_close_the_scene_and_allow_reopening()
    {
        var problem = new SceneProblem("build_failed", SceneStage.Build, null,
            new ContentReference.Design(new(new string('D', 64))), new("fixture-package"), null, "原始诊断文本，不作为协议解析。");
        var unavailable = new AssetRevision(new string('E', 64));
        foreach (var (failure, expectedCode) in new (SceneResult<ISceneFrame>, string)[]
        {
            (Success(Frame(2, readiness: SceneReadiness.Failed, diagnostics: [problem])), "build_failed"),
            (new SceneResult<ISceneFrame>.Faulted(problem), "build_failed"),
            (new SceneResult<ISceneFrame>.Failure([problem]), "build_failed"),
            (Success(Frame(2, objects: [Object(1, new SceneVisual.Model(unavailable, [], null,
                new(false, 0, 0, false), default))])), "scene_adapter_failed")
        })
        {
            var fixture = new Fixture(TestScene.GetTree());
            var sessions = new List<FixedDisplaySession>();
            var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = Factory(binding =>
            {
                var id = sessions.Count == 0 ? SceneId : new SceneSessionId(Guid.NewGuid());
                var next = Session(binding, sessions.Count == 0 ? [Success(Frame(1)), failure] : [Success(Frame(3, session: id))],
                    sessions.Count == 0 ? closeGate.Task : null, id: id);
                sessions.Add(next);
                return next;
            });
            try
            {
                var app = await Ready(fixture, new Setup
                {
                    Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
                        { [unavailable] = new ContentLookup<ContentAsset>.NotFound() }
                }, factory);
                SceneProblem? reason = null;
                var world = app.WorldScenes!;
                world.ReturnedToMap += value => reason = value;
                world.Open(Target);
                world.Display!.Timestamp = () => 0;
                world.ProcessFrame(0, false);
                var oldRoot = world.Display!.Root;
                world.ProcessFrame(0, false);
                Require(world.Display is null && !world.View.Visible && !world.View.Interactive && sessions[0].IsDisposed,
                    "故障没有隐藏场景、停止交互和关闭会话。");
                Require(!GodotObject.IsInstanceValid(oldRoot) && reason?.Code == expectedCode,
                    $"故障必须报告{expectedCode}并退役旧组，不能用其他异常替代。");
                var consoleLogs = app.GetChildren().OfType<Train.Ui.GameConsole>().Single().LogEvents;
                Require(!consoleLogs.Any(log => log.Exception?.Message.Contains("UNCONFIGURED", StringComparison.Ordinal) == true),
                    "未配置的替身调用不能充当预期故障。");
                if (expectedCode == problem.Code)
                {
                    var logged = consoleLogs.Single(log =>
                        log.EventId == "scene_problem" && Equals(log.Attributes["code"], problem.Code));
                    Require(logged.Message == problem.Detail && Equals(logged.Attributes["package"], problem.Package!.Value.Value) &&
                        Equals(logged.Attributes["content"], problem.Content!.ToString()), "诊断没有保留Code、Content、Package和原始Detail。");
                }
                world.Open(Target);
                world.Display!.Timestamp = () => 0;
                world.ProcessFrame(0, false);
                Require(world.Display?.DisplayedFrame is not null && !closeGate.Task.IsCompleted,
                    "重开被旧会话关闭等待阻塞。");
                closeGate.SetResult();
                world.ProcessFrame(0, true);
            }
            finally { closeGate.TrySetResult(); await fixture.Cleanup(); }
        }
    }

    [Test]
    public async Task Picking_uses_the_displayed_camera_frame_after_origin_shift_and_resolves_before_selection()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var hit = new SceneHit(Stamp(1), new(1), new PickSubject.Terrain(new WorldLocation.Node(new(1))),
            default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
        var first = Frame(1, pick: new ScenePick.Hit(hit));
        var shifted = Frame(2, baseline: first.Update.Frame, origin: new(100, 0, 0), shift: new(-100, 0, 0),
            pick: new ScenePick.Hit(hit with { Frame = Stamp(2) }));
        var secondHit = ((ScenePick.Hit)shifted.PickResult!).Value;
        var notReady = Frame(3, pick: new ScenePick.NotReady());
        FixedDisplaySession? session = null;
        var factory = Factory(binding => session = Session(binding, [Success(first), Success(shifted),
            Success(notReady), Success(Frame(4))], resolve:
            new Dictionary<SceneHit, SceneResult<PickSubject>> { [hit] = new SceneResult<PickSubject>.Success(hit.Subject),
                [secondHit] = new SceneResult<PickSubject>.Success(secondHit.Subject) }.ToImmutableDictionary()));
        try
        {
            var app = await Ready(fixture, new Setup(), factory);
            var world = app.WorldScenes!;
            var selected = 0;
            world.Selected += _ => selected++;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            var center = TestScene.GetViewport().GetVisibleRect().Size / 2;
            world.Pick(center);
            var oldRay = first.LastPickRay!;
            world.ProcessFrame(0, false);
            Require(session!.PresentCalls[^1].Request.PickRay is { } hitSample &&
                ReferenceEquals(hitSample.Frame, first) && ReferenceEquals(hitSample.Ray, oldRay),
                "命中拾取样本没有保留拾取时的显示帧和射线。");
            world.Pick(center);
            Require(selected == 2 && session!.ResolveCalls.SequenceEqual(new[] { hit, secondHit }), "命中没有经当前会话Resolve交给宿主事件。");
            Require(NVector3.Distance(shifted.LastPickRay!.Origin, oldRay.Origin + new NVector3(-100, 0, 0)) < 0.01f &&
                NVector3.Distance(shifted.LastPickRay.Direction, oldRay.Direction) < 0.001f, "原点移动后射线基准漂移。");
            world.ProcessFrame(0, false);
            world.Pick(center);
            Require(selected == 2 && session!.ResolveCalls.Count == 2, "NotReady被当作可选择空地。");
            world.ProcessFrame(0, false);
            Require(session!.PresentCalls[^1].Request.PickRay is { } notReadySample &&
                ReferenceEquals(notReadySample.Frame, notReady) && ReferenceEquals(notReadySample.Ray, notReady.LastPickRay),
                "NotReady拾取没有替换为最近一次实际拾取样本。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Accepted_reload_reopens_with_loading_and_rejected_reload_keeps_the_old_picture()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var railway = new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), 50));
        var sessions = new List<FixedDisplaySession>();
        var oldHit = new SceneHit(Stamp(1), new(1), new PickSubject.Terrain(new WorldLocation.Node(new(1))),
            default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
        var factory = Factory(binding =>
        {
            var id = sessions.Count == 0 ? SceneId : new SceneSessionId(Guid.NewGuid());
            var session = Session(binding, [Success(Frame((ulong)sessions.Count + 1, session: id,
                    pick: new ScenePick.Hit(oldHit)))], resolve:
                ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty.Add(oldHit,
                    new SceneResult<PickSubject>.Success(oldHit.Subject)),
                observe: [new SceneResult<SimulationPosition>.Success(new(0, 1))], id: id, target: railway);
            sessions.Add(session);
            return session;
        });
        try
        {
            var app = await Ready(fixture, setup, factory);
            var world = app.WorldScenes!;
            world.Open(railway);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            var old = world.Display!.DisplayedFrame;
            var selected = 0;
            world.Selected += _ => selected++;
            var changedInput = new CoreInputId(new string('B', 64));
            var rejectedContent = new FixedContentView(HostContentScenario.CreateProjection(changedInput), new(2));
            setup.Reload = new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(rejectedContent, changes: ContentChange.CoreInput));
            var rejectedView = WorldSceneFixture.EmptyWorld(new(0, 1));
            setup.Session.QueueSubmit(new CoreCommand.PublishContent(rejectedContent.Core), CommandSource.Console,
                new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Rejected([new("rejected", "field", null, ["reason"])])), [], null), rejectedView);
            app.Host!.BeginReload(CommandSource.Console);
            await Pump(app.Host);
            Require(world.Display.DisplayedFrame == old && sessions.Count == 1 && !sessions[0].IsDisposed,
                "拒绝重载更换了显示组。");
            var nextContent = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(2));
            setup.Reload = new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(nextContent, changes: ContentChange.Presentation));
            app.Host.BeginReload(CommandSource.Console);
            await Pump(app.Host);
            Require(sessions[0].IsDisposed && sessions.Count == 2 && world.View.Loading && !world.View.Interactive,
                "有效重载没有更换绑定、关闭旧场景和加载。");
            Require(sessions[1].RequestCalls.SequenceEqual(new[] { railway }), "内容世代变更丢失了当前线路目标。");
            world.Pick(Vector2.Zero);
            Require(selected == 0 && sessions[0].ResolveCalls.Count == 0 && factory.OpenCalls[0].Binding != factory.OpenCalls[1].Binding,
                "旧绑定仍可拾取或新会话沿用了旧绑定。");
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            Require(world.Display!.DisplayedFrame != old && world.View.Interactive, "重载后的新帧未接入。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Unhandled_mouse_input_respects_a_control_covering_the_world()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding, [Success(Frame(1, pick: new ScenePick.Miss()))])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            app.GetNode<Control>("MainMenu").Hide();
            app.GetNode<Control>("GameUi/Root").Hide();
            var picks = 0;
            world.View.PickRequested += _ => picks++;
            var cover = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            TestScene.AddChild(cover);
            cover.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            try
            {
                var viewport = TestScene.GetViewport();
                var position = viewport.GetVisibleRect().Size / 2;
                var click = new InputEventMouseButton { Position = position, GlobalPosition = position,
                    ButtonIndex = MouseButton.Left, Pressed = true };
                viewport.PushInput(click, true);
                Require(picks == 0, "UI遮挡鼠标仍进入了世界_UnhandledInput。");
                cover.Free();
                viewport.PushInput(click, true);
                Require(picks == 1, "未遮挡点击没有交给拾取入口。");
                world.ReturnToMap();
                var exits = 0;
                world.ReturnedToMap += _ => exits++;
                viewport.PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true }, true);
                Require(exits == 0, "隐藏的世界视图仍消费地图的Escape。");
            }
            finally { if (GodotObject.IsInstanceValid(cover)) cover.Free(); }
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Restoring_a_new_host_binding_returns_to_map_without_reopening_the_old_target()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        FixedDisplaySession? session = null;
        var factory = Factory(binding =>
        {
            var id = new SceneSessionId(Guid.NewGuid());
            return session = Session(binding, [Success(Frame(1, session: id))], id: id);
        });
        try
        {
            var app = await Ready(fixture, setup, factory);
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            var returned = 0;
            SceneProblem? reason = null;
            world.ReturnedToMap += problem => { returned++; reason = problem; };
            var oldBinding = app.Host!.GetStatus()!.Binding;
            setup.RestoredSession = new(WorldSceneFixture.EmptyWorld(new(0, 0)));
            app.Host.BeginRestore("opaque", "opaque",
                new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            await Pump(app.Host);
            Require(app.Host.GetStatus()!.Binding != oldBinding && session!.IsDisposed && returned == 1 && reason is null,
                "宿主绑定切换必须关闭旧场景并正常返回地图。");
            Require(world.Display is null && !world.View.Visible && factory.OpenCalls.Count == 1,
                "宿主绑定切换仍重开了旧观察目标。");
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            Require(factory.OpenCalls.Count == 2 && factory.OpenCalls[0].Binding != factory.OpenCalls[1].Binding,
                "新宿主绑定再次打开世界没有分配新SceneBinding。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Returning_to_map_releases_the_displayed_cpu_frame()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding, [Success(Frame(1))])));
            var frame = OpenAndCloseFrame(app.WorldScenes!);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Require(!frame.IsAlive, "回到地图后宿主仍持有旧CPU帧。");
        }
        finally { await fixture.Cleanup(); }
    }

    // Keep Debug JIT locals containing the frame out of the collecting test's stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseFrame(WorldSceneController world)
    {
        world.Open(Target);
        world.Display!.Timestamp = () => 0;
        world.ProcessFrame(0, false);
        Require(world.View.Interactive && world.Display.DisplayedFrame is not null, "离开前场景未接入。");
        var frame = new WeakReference(world.Display.DisplayedFrame);
        world.ReturnToMap();
        return frame;
    }

    [Test]
    public async Task Reopening_under_the_same_host_and_content_keeps_the_scene_binding()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var sessions = new List<FixedDisplaySession>();
        var factory = Factory(binding =>
        {
            var id = new SceneSessionId(Guid.NewGuid());
            var session = Session(binding, [Success(Frame(1, session: id))], id: id);
            sessions.Add(session);
            return session;
        });
        try
        {
            var app = await Ready(fixture, new Setup(), factory);
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            world.ReturnToMap();
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            Require(sessions.Count == 2 && sessions[0].IsDisposed && world.View.Interactive,
                "回地图后必须可以新开场景会话。");
            Require(factory.OpenCalls[0].Binding == factory.OpenCalls[1].Binding,
                "同一个宿主绑定和内容世代重开不能改变SceneBinding。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Open_switches_targets_in_one_session_and_failed_requests_keep_the_active_target()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var railway = new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), 50));
        var rejected = new WorldSceneTarget.Location(new WorldLocation.Node(new(2)));
        var problem = new SceneProblem("target_missing", SceneStage.Input, null, null, null, null, "目标不存在。");
        var first = Frame(1, pick: new ScenePick.Miss());
        var next = Frame(3, request: 2, pick: new ScenePick.Miss());
        FixedDisplaySession? session = null;
        var factory = Factory(binding => session = new()
        {
            Id = SceneId, Binding = binding,
            RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                .Add(Target, new SceneResult<SceneRequestId>.Success(new(1)))
                .Add(railway, new SceneResult<SceneRequestId>.Success(new(2)))
                .Add(rejected, new SceneResult<SceneRequestId>.Failure([problem])),
            PresentResults = [Success(first), Success(Frame(2, request: 2, readiness: SceneReadiness.Loading)),
                Success(next), Success(Frame(4, request: 2, pick: new ScenePick.Miss()))]
        });
        try
        {
            var app = await Ready(fixture, new Setup(), factory);
            var world = app.WorldScenes!;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            world.Open(railway);
            world.Display!.Timestamp = () => 0;
            Require(factory.OpenCalls.Count == 1 && !session!.IsDisposed && !world.View.Loading && !world.View.Interactive,
                "打开另一目标必须复用会话，等待首帧时不能直接显示加载罩。");
            world.ProcessFrame(0, false);
            Require(!world.View.Loading && ReferenceEquals(world.Display!.DisplayedFrame, first),
                "新请求的Loading首帧应保留旧画面，不显示加载提示。");
            world.ProcessFrame(0, false);
            Require(world.View.Interactive && !world.View.Loading && ReferenceEquals(world.Display!.DisplayedFrame, next),
                "新请求Ready没有接入。");
            var focus = world.View.CameraRig.Focus;
            world.View.CameraRig.Pan(new(8, 0, -12));
            Require(world.View.CameraRig.Focus == focus + new Vector3(8, 0, -12),
                "线路地点应允许没有节点边界的自由平移。");
            world.Open(rejected);
            world.Display!.Timestamp = () => 0;
            Require(world.View.Interactive && !world.View.Loading && !session!.IsDisposed && world.Display!.DisplayedFrame == next,
                "失败的目标请求改变了活动场景。");
            world.ProcessFrame(0, false);
            Require(world.View.Interactive && session!.PresentCalls[^1].DisplayedFrame == next &&
                session.PresentCalls[^1].Request.Camera?.Focus == new NVector3(focus.X + 8, focus.Y, focus.Z - 12),
                "失败请求改变了活动请求，或线路平移没有传给下一次Present.Camera。");
            Require(factory.OpenCalls.Count == 1 && session!.RequestCalls.SequenceEqual(new[] { Target, railway, rejected }),
                "目标切换新开了场景会话或跳过RequestView。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Pausing_preserves_fractional_interpolation_and_returning_to_map_stops_camera_and_environment()
    {
        var fixture = new Fixture(TestScene.GetTree());
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                [Success(Frame(1)), Success(Frame(2)), Success(Frame(3))])));
            var world = app.WorldScenes!;
            var host = app.Host!;
            var environment = world.View.GetChildren().OfType<WorldEnvironment>().Single();
            Require(!world.View.CameraRig.Camera.Current && environment.Environment is null,
                "地图状态仍启用了世界相机或环境。");
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            Require(world.View.CameraRig.Camera.Current && environment.Environment is not null,
                "打开世界未启用相机和环境。");
            host.SetPaused(false);
            host.ProcessFrame(0);
            host.ProcessFrame(SimulationTime.StepSeconds * 0.5);
            world.ProcessFrame(0, false);
            var remainder = host.GetStatus()!.Remainder;
            Require(remainder > 0 && remainder < SimulationTime.StepSeconds, "没有建立部分模拟步长。");
            host.SetPaused(true);
            world.ProcessFrame(0, false);
            Require(Math.Abs(session!.PresentCalls[^1].Request.Interpolation - remainder / SimulationTime.StepSeconds) < 1e-9,
                "暂停把保留的部分步长改成了步末插值。");
            world.ReturnToMap();
            Require(!world.View.CameraRig.Camera.Current && environment.Environment is null,
                "关闭世界未停止相机和环境。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Returning_to_menu_through_saves_closes_the_scene_without_returning_to_map_or_logging_a_fault()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, setup, Factory(binding => session = Session(binding, [Success(Frame(1))])));
            var world = app.WorldScenes!;
            var returned = 0;
            world.ReturnedToMap += _ => returned++;
            world.Open(Target);
            world.Display!.Timestamp = () => 0;
            app._Process(0);
            Require(world.View.Interactive && world.Display?.DisplayedFrame is not null, "离开前场景未接入。");
            var oldRoot = world.Display!.Root;
            Require(app.Host!.RenameSave("unsaved before menu") is null, "无法准备未保存的离开流程。");
            Require(app.Saves!.ReturnToMenu() is null && app.Saves.Leaving is { Waiting: false },
                "返回主菜单没有进入存档离开保护。");
            app.Saves.ChooseLeave(LeaveChoice.Discard);
            Require(app.Host.GetStatus() is null && setup.Session.DisposeCount == 1,
                "存档离开流程没有结束宿主会话。");
            app._Process(0);
            Require(world.Display is null && !world.View.Visible && !world.View.Interactive && session!.IsDisposed,
                "下一帧未关闭场景、停交互和释放场景会话。");
            Require(!GodotObject.IsInstanceValid(oldRoot), "离开后仍保留旧显示根。");
            Require(returned == 0, "返回主菜单不能触发返回地图事件。");
            Require(!app.GetChildren().OfType<Train.Ui.GameConsole>().Single().LogEvents
                .Any(log => log.EventId == "scene_adapter_failed"), "正常离开被记录为场景接入故障。");
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task<Train.App.App> Ready(Fixture fixture, Setup setup, FixedDisplayFactory factory)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.Create;
        app.CreateScenes = () => factory;
        await fixture.AddToRoot(app);
        await Until(() => app.Host!.GetStatus() is not null);
        app.SetProcess(false);
        return app;
    }
    private async Task Until(Func<bool> complete)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!complete() && limit.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(complete(), "挂起保护超时：宿主准备未完成。");
    }
    private async Task Pump(HostCoordinator host)
    {
        await Until(() => { host.ProcessFrame(0); return !host.IsBusy; });
    }
    private static FixedDisplayFactory Factory(Func<SceneBinding, FixedDisplaySession> open) => new()
    { OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(open(binding)) };
    private static FixedDisplaySession Session(SceneBinding binding, ImmutableArray<SceneResult<ISceneFrame>> frames,
        Task? close = null, ImmutableDictionary<SceneHit, SceneResult<PickSubject>>? resolve = null,
        ImmutableArray<SceneResult<SimulationPosition>> observe = default, SceneSessionId? id = null,
        WorldSceneTarget? target = null) => new()
    {
        Id = id ?? SceneId, Binding = binding, PresentResults = frames, DisposeGate = close,
        RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(target ?? Target,
            new SceneResult<SceneRequestId>.Success(new(1))),
        ResolveResults = resolve ?? ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty,
        ObserveResults = observe.IsDefault ? [] : observe
    };
    private static SceneResult<ISceneFrame> Success(ISceneFrame frame) => new SceneResult<ISceneFrame>.Success(frame);
    private static SceneFrameStamp Stamp(ulong revision, SceneSessionId? session = null) => new(session ?? SceneId, new(1), new(1), revision);
    private static SceneObject Object(ulong id, SceneVisual? visual = null) => new(new(id), null,
        new(NVector3.Zero, System.Numerics.Quaternion.Identity), null,
        [visual ?? new SceneVisual.Geometry(Mesh, new(false, 0, 0, false))], [], []);
    private static FixedSceneFrame Frame(ulong revision, SceneFrameStamp? baseline = null,
        SceneReadiness readiness = SceneReadiness.Ready, ImmutableArray<SceneObject> objects = default,
        ScenePoint origin = default, NVector3 shift = default, ScenePick? pick = null,
        ImmutableArray<SceneProblem> diagnostics = default, SceneSessionId? session = null,
        Lookup<double>? height = null, ulong request = 1, ImmutableArray<SceneDebugLine> debugLines = default,
        ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>? poses = null, ulong space = 1) => new()
    {
        Update = new(baseline, Stamp(revision, session) with { Request = new(request), Space = new(space) }, shift, objects.IsDefault ? [Object(1)] : objects, [], new([], []), []),
        Origin = origin, Readiness = readiness, PickResult = pick,
        DebugLines = debugLines.IsDefault ? [] : debugLines,
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics,
        Poses = poses ?? ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty,
        TerrainHeightResult = height ?? new Lookup<double>.Missing()
    };

    private sealed class Setup
    {
        internal ScriptedCoreSession Session { get; } = new(WorldSceneFixture.EmptyWorld(new(0, 0)));
        internal ContentResult<IContentCandidate>? Reload { get; set; }
        internal ScriptedCoreSession? RestoredSession { get; set; }
        internal IReadOnlyDictionary<AssetRevision, ContentLookup<ContentAsset>> Assets { get; init; } =
            ImmutableDictionary<AssetRevision, ContentLookup<ContentAsset>>.Empty;
        internal HostCoordinator Create(TrainLogger logger)
        {
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1)) { Assets = Assets };
            return WorldSceneFixture.CreateHost(Session, content, logger, () => Reload
                ?? throw new InvalidOperationException("UNCONFIGURED: 重载结果。"), () => RestoredSession ?? Session);
        }
    }
}

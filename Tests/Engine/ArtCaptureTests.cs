using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.ArtCapture;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class ArtCaptureTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Straight_motion_is_rejected_without_core_commands_or_save_capture() =>
        await SampleMotion(turned: false);

    [Test]
    public async Task Changed_displayed_heading_satisfies_turn_requirement() =>
        await SampleMotion(turned: true);

    [Test]
    public async Task Lair_motion_samples_without_requiring_a_turn() =>
        await SampleMotion(turned: false, target: "lair");

    [Test]
    public async Task Group_motion_samples_without_requiring_a_turn() =>
        await SampleMotion(turned: false, target: "group");

    [Test]
    public async Task Eight_step_motion_samples_the_requested_simulation_interval() =>
        await SampleMotion(turned: false, target: "group", motionSteps: 8);

    [Test]
    public async Task Fixed_group_camera_preserves_focus_when_the_display_origin_changes() =>
        await SampleMotion(turned: false, target: "group", rebaseCamera: true);

    [Test]
    public void Motion_steps_are_optional_and_require_a_positive_integer()
    {
        string[] arguments = ["--capture-save", "save.json", "--capture-location", "node:1",
            "--capture-group", "1", "--capture-name", "combat"];
        Require(CaptureInput.Parse(arguments).MotionSteps == 2, "默认运动序列必须保持每帧两步。");
        Require(CaptureInput.Parse([.. arguments, "--capture-motion-steps", "8"]).MotionSteps == 8,
            "必须解析每帧八步的运动序列。");
        foreach (var value in new[] { "0", "-1", "1.5", "invalid", "18446744073709551616" })
        {
            try
            {
                CaptureInput.Parse([.. arguments, "--capture-motion-steps", value]);
                throw new InvalidOperationException("非法的运动步数未被拒绝。");
            }
            catch (ArgumentException) { }
        }
    }

    [Test]
    public async Task Last_group_member_can_disappear_during_sampling() =>
        await SampleMotion(turned: false, target: "group", missingAfterAdvance: true);

    [Test]
    public void Creature_capture_arguments_parse_and_reject_multiple_targets()
    {
        foreach (var target in new[] { "--capture-lair", "--capture-group" })
        {
            string[] arguments = ["--capture-save", "save.json", "--capture-location", "node:1",
                target, "7", "--capture-name", "creatures"];
            var input = CaptureInput.Parse(arguments);
            Require(target == "--capture-lair" ? input.Lair == new LairId(7) && input.Group is null :
                input.Group == new CreatureGroupId(7) && input.Lair is null, "必须解析选定的生物目标。");
            foreach (var other in new[] { "--capture-train", "--capture-lair", "--capture-group" }.Where(value => value != target))
            {
                try
                {
                    CaptureInput.Parse([.. arguments, other, "1"]);
                    throw new InvalidOperationException("多个截图目标未被拒绝。");
                }
                catch (ArgumentException) { }
            }
        }
    }

    [Test]
    public void Creature_capture_validates_location_and_group_members()
    {
        var location = new WorldLocation.Node(new(1));
        var lair = new CaptureInput("save.json", location, default, "lair", Lair: new(1));
        var group = new CaptureInput("save.json", location, default, "group", Group: new(1));
        var view = CreatureView();
        Require(lair.Validate(view).SequenceEqual([new WorldObjectId.Lair(new(1))]), "聚落必须解析到聚落对象。");
        Require(group.Validate(view).SequenceEqual([new WorldObjectId.Creature(new(1))]), "群必须解析到其成员。");
        Reject(lair with { Location = new WorldLocation.Edge(new(1), 0) }, view, "生物聚落不在指定场景");
        Reject(group with { Location = new WorldLocation.Edge(new(1), 0) }, view, "生物群不在指定场景");
        Reject(group, CreatureView(empty: true), "指定生物群不存在或成员为空");

        static void Reject(CaptureInput input, ICoreReadView core, string expected)
        {
            try
            {
                input.Validate(core);
                throw new InvalidOperationException("无效生物截图目标未被拒绝。");
            }
            catch (InvalidDataException exception)
            { Require(exception.Message.StartsWith(expected, StringComparison.Ordinal), exception.Message); }
        }
    }

    [Test]
    public async Task Missing_scene_is_rejected_before_output_or_core_writes() =>
        await RejectRestore(scenePresent: false, trainPresent: true, trainEdge: 1, "指定场景不存在");

    [Test]
    public async Task Missing_train_is_rejected_before_output_or_core_writes() =>
        await RejectRestore(scenePresent: true, trainPresent: false, trainEdge: 1, "指定列车不存在或编组为空");

    [Test]
    public async Task Train_in_another_scene_is_rejected_before_output_or_core_writes() =>
        await RejectRestore(scenePresent: true, trainPresent: true, trainEdge: 2, "列车不在指定场景");

    [Test]
    public async Task Missing_save_is_rejected_before_host_creation()
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (_, _, _, _) => throw new InvalidOperationException("缺存档时不能创建宿主。")
        };
        try
        {
            await run.RestoreAsync(new(Path.Combine(Output(name), "missing.json"), new WorldLocation.Edge(new(1), 0), new(1), name));
            throw new InvalidOperationException("缺存档未被拒绝。");
        }
        catch (IOException) { }
        Require(!Directory.Exists(Output(name)), "缺存档不能创建输出目录。");
    }

    [Test]
    public void Output_name_cannot_escape_validation_directory()
    {
        try
        {
            CaptureInput.Parse(["--capture-save", "save.json", "--capture-location", "edge:1",
                "--capture-train", "1", "--capture-name", "../escape"]);
            throw new InvalidOperationException("路径穿越名称未被拒绝。");
        }
        catch (ArgumentException) { }
    }

    private async Task RejectRestore(bool scenePresent, bool trainPresent, ulong trainEdge, string expected)
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        var root = ProjectSettings.GlobalizePath("res://.godot/validation");
        Directory.CreateDirectory(root);
        var save = Path.Combine(root, name + ".json");
        File.WriteAllText(save, SaveContainer.Encode(new(new(SaveContainer.FormatVersion, Guid.NewGuid(),
            "engine capture input", DateTimeOffset.UnixEpoch, new(0, 0), []), "opaque", "opaque")));
        var core = new ScriptedCoreSession(View(scenePresent, trainPresent, trainEdge));
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (logger, _, _, _) => CreateHost(core, logger),
            CreateScenes = () => throw new InvalidOperationException("输入校验失败前不能创建场景。")
        };
        try
        {
            try
            {
                await run.RestoreAsync(new(save, new WorldLocation.Edge(new(1), 0), new(1), name));
                throw new InvalidOperationException("无效输入未被拒绝。");
            }
            catch (InvalidDataException exception)
            {
                Require(exception.Message.StartsWith(expected, StringComparison.Ordinal), $"预期输入校验拒绝，实际为：{exception.Message}");
            }
            Require(!Directory.Exists(Output(name)), "输入校验失败不能创建输出目录。");
            Require(core.SubmitCount == 0 && core.CaptureCount == 0,
                "输入校验失败不能提交核心命令或捕获存档。");
        }
        finally { File.Delete(save); }
    }

    private async Task SampleMotion(bool turned, string? target = null, bool missingAfterAdvance = false, ulong motionSteps = 2, bool rebaseCamera = false)
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        var root = ProjectSettings.GlobalizePath("res://.godot/validation");
        Directory.CreateDirectory(root);
        var save = Path.Combine(root, name + ".json");
        File.WriteAllText(save, SaveContainer.Encode(new(new(SaveContainer.FormatVersion, Guid.NewGuid(),
            "engine capture input", DateTimeOffset.UnixEpoch, new(0, 0), []), "opaque", "opaque")));
        var core = new ScriptedCoreSession(target is null ? View(true, true, 1) : CreatureView());
        for (ulong step = 1; step <= 60 * motionSteps; step++)
            core.QueueAdvance(new(1, new(step, 0), [], null, null),
                target is null ? View(true, true, 1, new(step, 0)) :
                    CreatureView(empty: missingAfterAdvance, position: new(step, 0)));
        var sessionId = new SceneSessionId(Guid.NewGuid());
        WorldObjectId subject = target switch
        {
            "lair" => new WorldObjectId.Lair(new(1)),
            "group" => new WorldObjectId.Creature(new(1)),
            _ => new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))
        };
        var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
            [new(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                new(System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
                new(System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, new(0, 1), new(0, 1), ExposedFaceEdges.None, 1, 0)],
            [0, 1, 2]);
        System.Numerics.Vector3 Position(int index) => rebaseCamera
            ? index == 0 ? new(10, 20, 30) : new(-4086, 20, -8162)
            : new(0, 0, index);
        FixedSceneFrame Frame(int index) => new()
        {
            Origin = rebaseCamera && index > 0 ? new(4096, 0, 8192) : default,
            Update = new(null, new(sessionId, new(1), new(1), (ulong)index + 1), System.Numerics.Vector3.Zero,
                rebaseCamera ? [new(new(1), null, new(Position(index), System.Numerics.Quaternion.Identity), subject,
                    [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])] : [], [], new([], []), []),
            SimulationSeconds = (ulong)index * motionSteps / 60d,
            Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                missingAfterAdvance && index > 0 ? new Lookup<ScenePose>.Missing() : new Lookup<ScenePose>.Found(new(Position(index),
                    System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY,
                        turned && index == 60 ? 0.1f : 0)))),
            TerrainHeightResult = new Lookup<double>.Missing()
        };
        var factory = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(new CaptureDisplaySession(new FixedDisplaySession
            {
                Id = sessionId, Binding = binding,
                RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                    .Add(target is null ? new WorldSceneTarget.Follow(new(1), new(1)) :
                        new WorldSceneTarget.Location(new WorldLocation.Node(new(1))), new SceneResult<SceneRequestId>.Success(new(1))),
                ObserveResults = Enumerable.Range(1, (int)(60 * motionSteps)).Select(step => (SceneResult<SimulationPosition>)
                    new SceneResult<SimulationPosition>.Success(new((ulong)step, 0))).ToImmutableArray()
            }, () => Frame((int)(core.CurrentView.Position.CompletedSteps / motionSteps))))
        };
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (logger, _, _, _) => CreateHost(core, logger), CreateScenes = () => factory
        };
        try
        {
            await run.RestoreAsync(target is null
                ? new(save, new WorldLocation.Edge(new(1), 0), new(1), name, MotionSteps: motionSteps)
                : new(save, new WorldLocation.Node(new(1)), default, name,
                    Lair: target == "lair" ? new(1) : null, Group: target == "group" ? new(1) : null, MotionSteps: motionSteps));
            if (rebaseCamera)
            {
                Require(run.World!.Display!.PoseFrame!.Origin == default,
                    "首次固定相机定位必须发生在原点切换之前。");
                run.PlaceCamera("follow", 0, 240);
            }
            for (var frame = 1; frame <= 60; frame++)
            {
                await run.AdvanceTurnFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (rebaseCamera)
                {
                    Require(run.World!.Display!.PoseFrame!.Origin == new ScenePoint(4096, 0, 8192),
                        "推进后必须实际切换显示原点，才能验证缓存坐标的转换。");
                    var camera = run.World.View.CameraRig;
                    var focus = camera.Focus;
                    var cameraPosition = camera.Camera.Position;
                    run.PlaceCamera("follow", 0, 240);
                    Require(camera.Focus.DistanceTo(focus) < 0.001f &&
                        camera.Camera.Position.DistanceTo(cameraPosition) < 0.001f,
                        "浮动原点切换后，重复固定相机定位必须保持世界焦点和相机位置。");
                    var displayedMesh = run.World.Display!.Root.FindChildren("*", nameof(MeshInstance3D), true, false)
                        .OfType<MeshInstance3D>().Single();
                    Require(camera.Focus.DistanceTo((displayedMesh.GlobalTransform * displayedMesh.Mesh.GetAabb()).GetCenter()) < 0.001f,
                        "固定相机必须继续对准实际显示对象，不能使用旧原点下的包围盒。");
                }
                Require(core.CurrentView.Position.CompletedSteps == (ulong)frame * motionSteps,
                    "运动采样必须每帧只推进指定数量的模拟步。");
                Require(run.World!.Display!.DisplayedFrame!.SimulationSeconds == (ulong)frame * motionSteps / 60d,
                    "运动采样必须使用本次推进后的真实显示帧。");
            }
            if (turned) run.RequireTurn();
            else if (target is null)
            {
                try
                {
                    run.RequireTurn();
                    throw new InvalidOperationException("直线运动未被拒绝。");
                }
                catch (InvalidDataException exception)
                { Require(exception.Message.StartsWith("未过弯：", StringComparison.Ordinal), "直线运动必须按未过弯拒绝。"); }
            }
            Require(core.SubmitCount == 0 && core.CaptureCount == 0,
                "运动采样不能提交核心命令或捕获存档。");
        }
        finally { File.Delete(save); }
    }

    private sealed class CaptureDisplaySession(FixedDisplaySession session, Func<ISceneFrame> frame) : IWorldSceneSession
    {
        public SceneSessionId Id => session.Id;
        public SceneBinding Binding => session.Binding;
        public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target) => session.RequestView(target);
        public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events) =>
            session.Observe(snapshot, events);
        public SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame) =>
            new SceneResult<ISceneFrame>.Success(frame());
        public SceneResult<PickSubject> Resolve(SceneHit hit) => session.Resolve(hit);
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private static FixedCoreReadView CreatureView(bool empty = false, SimulationPosition position = default)
    {
        var terrain = new TerrainView(new("fixture:terrain"), 0, []);
        return new(position, HostCoreScenario.Input)
        {
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [new(1)] = new Lookup<NodeView>.Found(new(new(new(1), new(0, 0), terrain,
                    new NodeOccupant.Lair(new(1)), [], 0, 0, null), [], 0, 0, 0))
            },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
            {
                [new(1)] = new Lookup<EdgeView>.Found(new(new(new(1), new(1), new(2), 100,
                    terrain, 1, 1, [], []), [], null))
            },
            Lairs = new Dictionary<LairId, Lookup<LairView>>
            {
                [new(1)] = new Lookup<LairView>.Found(new(new(1), new(1), new("fixture:lair"), [new(1)]))
            },
            CreatureGroups = new Dictionary<CreatureGroupId, Lookup<CreatureGroupView>>
            {
                [new(1)] = new Lookup<CreatureGroupView>.Found(new(new(1), new(1),
                    new WorldLocation.Node(new(1)), null, CreatureActivity.Guarding, 0,
                    empty ? [] : [new(new(1), new("fixture:creature"), 1, 1)]))
            }
        };
    }

    private static FixedCoreReadView View(bool scenePresent, bool trainPresent, ulong trainEdge, SimulationPosition position = default)
    {
        var edge = new EdgeMapItem(new(1), new(1), new(2), 100,
            new(new("fixture:terrain"), 0, []), 1, 1, [], []);
        var train = new TrainView(new(1), new TrainPosition.Edge(new(trainEdge), 1, TravelDirection.Forward, 10),
            TrainActivity.Running, [new(1)], new(1), 0, 6, 1, 1, 1, 10, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], TrainOrderFacing.FirstEndLeading, null);
        return new(position, HostCoreScenario.Input)
        {
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
            {
                [new(1)] = scenePresent ? new Lookup<EdgeView>.Found(new(edge, [], null)) : new Lookup<EdgeView>.Missing()
            },
            Trains = new Dictionary<TrainId, Lookup<TrainView>>
            {
                [new(1)] = trainPresent ? new Lookup<TrainView>.Found(train) : new Lookup<TrainView>.Missing()
            }
        };
    }

    private static HostCoordinator CreateHost(ScriptedCoreSession session, TrainLogger logger)
    {
        var capture = new FixedCoreCapture(new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References,
            "opaque", HostCoreScenario.Input, new(1));
        var core = new ScriptedCoreFactory
        {
            DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
            PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(session,
                HostCoreScenario.References))
        };
        var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
        var content = new ScriptedContentFactory
        {
            DecodeCaptureCall = _ => new ContentResult<IContentCapture>.Success(new FixedContentCapture("opaque")),
            PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view))
                : throw new InvalidOperationException("UNCONFIGURED: 只允许存档恢复。")
        };
        return new(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
            new(new("builtin"), new("mods"), null, "fixture", []), logger,
            sessionOptions: new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
    }

    private static string Output(string name) => ProjectSettings.GlobalizePath($"res://.godot/validation/{name}");
}

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
using Train.Ui;
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
        await SampleMotion(turned: false, target: new WorldObjectId.Lair(new(1)));

    [Test]
    public async Task Group_motion_samples_without_requiring_a_turn() =>
        await SampleMotion(turned: false, target: new WorldObjectId.CreatureGroup(new(1)));

    [Test]
    public async Task Building_motion_samples_without_requiring_a_turn() =>
        await SampleMotion(turned: false, target: new WorldObjectId.Instance(new InstanceId.Building(new(1))));

    [Test]
    public async Task Destroyed_building_keeps_its_last_camera_and_continues_sampling() =>
        await SampleMotion(turned: false, target: new WorldObjectId.Instance(new InstanceId.Building(new(1))), missingAfterAdvance: true);

    [Test]
    public async Task Eight_step_motion_samples_the_requested_simulation_interval() =>
        await SampleMotion(turned: false, target: new WorldObjectId.CreatureGroup(new(1)), motionSteps: 8);

    [Test]
    public async Task Group_motion_waits_for_the_advanced_sample_after_an_old_ready_frame() =>
        await SampleMotion(turned: false, target: new WorldObjectId.CreatureGroup(new(1)), motionSteps: 8, delayAdvancedFrame: true);

    [Test]
    public async Task First_one_step_group_sample_waits_for_a_new_committed_frame() =>
        await SampleMotion(turned: false, target: new WorldObjectId.CreatureGroup(new(1)), motionSteps: 1, delayAdvancedFrame: true);

    [Test]
    public async Task Train_motion_keeps_its_existing_ready_frame_wait_behavior() =>
        await SampleMotion(turned: false, delayAdvancedFrame: true);

    [Test]
    public async Task Fixed_group_camera_preserves_focus_when_the_display_origin_changes()
    {
        var advanced = false;
        await SampleCamera(new WorldObjectId.CreatureGroup(new(1)), _ => CameraFrame(
            advanced ? new(-4086, 20, -8162) : new(10, 20, 30),
            advanced ? new(4096, 0, 8192) : default), async run =>
        {
            await run.PrepareSubjectCameraAsync(240);
            run.PlaceCamera("follow", 0, 240);
            var previous = run.World!.Display!.DisplayedFrame!.Update.Frame;
            advanced = true;
            await run.DisplayAsync(after: previous).WaitAsync(TimeSpan.FromSeconds(5));
            var camera = run.World!.View.CameraRig;
            var focus = camera.Focus;
            var position = camera.Camera.Position;
            run.PlaceCamera("follow", 0, 240);
            Require(camera.Focus.DistanceTo(focus) < 0.001f && camera.Camera.Position.DistanceTo(position) < 0.001f,
                "浮动原点切换后，固定相机必须保持世界焦点和位置。");
            Require(camera.Focus.DistanceTo(new(-4085.5f, 20.5f, -8162)) < 0.001f,
                "固定相机必须继续对准实际显示对象。");
        });
    }

    [Test]
    public async Task Fixed_capture_does_not_wait_for_fog_around_a_subject_that_moves_out_of_view()
    {
        var moved = false;
        var viewFogged = true;
        var distantFog = new SceneBounds(new(490, -128, -10), new(510, 128, 10));
        var viewFog = new SceneBounds(new(-2, -128, -2), new(2, 128, 2));
        await SampleCamera(new WorldObjectId.CreatureGroup(new(1)), _ => CameraFrame(
            moved ? new(500, 0, 0) : System.Numerics.Vector3.Zero, default,
            moved ? viewFogged ? [distantFog, viewFog] : [distantFog] : []), async run =>
        {
            await run.PrepareSubjectCameraAsync(96);
            var camera = run.World!.View.CameraRig;
            var focus = camera.Focus;
            var position = camera.Camera.Position;
            var previous = run.World.Display!.DisplayedFrame!.Update.Frame;
            moved = true;
            await run.DisplayAsync(after: previous).WaitAsync(TimeSpan.FromSeconds(5));
            run.PlaceCamera("follow", 0, 96);
            Require(camera.Focus.DistanceTo(focus) < .001f && camera.Camera.Position.DistanceTo(position) < .001f,
                "主体移出取景后，相机必须保持初始取景。");
            var waiting = run.DisplayAsync(allowMissing: true, includeCamera: true);
            Require(!waiting.IsCompleted, "主体移远后，缓存取景区内的雾仍必须阻止截图。");
            viewFogged = false;
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            var frame = run.World.Display.DisplayedFrame!;
            Require(frame.Update.Environment.FoggedRegions.Contains(distantFog) &&
                frame.FindPose(new WorldObjectId.Creature(new(1))) is Lookup<ScenePose>.Found { Value.Position.X: 500 },
                "远处主体和常驻雾必须保留，完成等待不能依赖将它们隐藏。");
        });
    }

    [Test]
    public async Task Building_outside_the_initial_view_is_loaded_from_its_overview_plot()
    {
        var target = new WorldObjectId.Instance(new InstanceId.Building(new(1)));
        var positioned = false;
        await SampleCamera(target, request =>
        {
            positioned = request.Camera is { Focus.X: > 195 and < 205, Focus.Z: > -85 and < -75 };
            var frame = CameraFrame(new(200, 2, -80), new(4096, 0, 8192), subject: target);
            return new FixedSceneFrame
            {
                Origin = frame.Origin,
                TerrainHeightResult = frame.TerrainHeightResult,
                PickResult = new ScenePick.Miss(),
                NodePanBounds = new(new(-400, -400), new(400, 400)),
                NodeOverview = new([new(7, PlotSize.SmallStation,
                    [new(190, -90), new(210, -90), new(210, -70), new(190, -70)])], [], []),
                Update = positioned ? frame.Update : frame.Update with { Upserts = [] },
                Poses = positioned ? frame.Poses : ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty
                    .Add(target, new Lookup<ScenePose>.Missing())
            };
        }, async run =>
        {
            Require(positioned, "建筑截图必须先按模板地块编号移动流送焦点。");
            await run.PrepareSubjectCameraAsync(96).WaitAsync(TimeSpan.FromSeconds(5));
            Require(run.World!.View.CameraRig.Focus.DistanceTo(new(200.5f, 2.5f, -80)) < .001f,
                "粗定位后必须继续用实际建筑网格确定取景中心。");
        }, coreView: CreatureView(settlementBuilding: true));
    }

    [Test]
    public async Task Creature_capture_waits_for_local_terrain_before_caching_camera_bounds()
    {
        var ready = false;
        ulong? first = null;
        await SampleCamera(new WorldObjectId.CreatureGroup(new(1)), request =>
        {
            first ??= Godot.Engine.GetProcessFrames();
            ready = Godot.Engine.GetProcessFrames() > first.Value && request.Camera is { Focus.X: > 90 };
            return CameraFrame(new(100, ready ? 20 : 0, 0), default,
                ready ? [new(new(1000, -128, 1000), new(1010, 128, 1010))]
                    : [new(new(90, -128, -10), new(110, 128, 10))]);
        }, async run =>
        {
            await run.PrepareSubjectCameraAsync(96).WaitAsync(TimeSpan.FromSeconds(5));
            Require(ready, "截图必须等待对象所在区域的生成遮罩消失。");
            run.PlaceCamera("follow", 0, 96);
            Require(run.World!.View.CameraRig.Focus.Y > 20, "固定相机必须使用地形完成后的显示姿态。");
        });
    }

    [Test]
    public async Task Lair_camera_uses_content_bounds_when_the_anchor_has_no_mesh()
    {
        var scene = new DesignSceneProjection(null, new(new(-3, 0, -5), new(4, 7, 6)), null,
            true, null, null, [], [], [], [], []);
        var appearance = new LairAppearanceProjection(new("fixture:lair"),
            new(new("fixture:model"), scene, new("聚落", "")), new(new("聚落", ""), null, null, false));
        await SampleCamera(new WorldObjectId.Lair(new(1)), _ => CameraFrame(new(100, 20, 30), default, subject: new WorldObjectId.Lair(new(1))), async run =>
        {
            await run.PrepareSubjectCameraAsync(96);
            run.PlaceCamera("follow", 0, 96);
            Require(run.World!.View.CameraRig.Focus.DistanceTo(new(101, 24, 31)) < 0.001f,
                "没有网格的聚落锚点必须使用内容占用范围确定相机焦点。");
            var focus = run.World.View.CameraRig.Focus;
            run.PlaceCamera("near", 0, 96);
            Require(run.World.View.CameraRig.Focus.DistanceTo(focus) < 0.001f &&
                run.World.View.CameraRig.Camera.Position.DistanceTo(focus) < WorldCamera.DefaultDistance,
                "聚落近景必须保持模型中心，并根据模型范围缩近。");
        }, appearance);
    }

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
        await SampleMotion(turned: false, target: new WorldObjectId.CreatureGroup(new(1)), missingAfterAdvance: true);

    [Test]
    public void Capture_arguments_parse_and_reject_multiple_targets()
    {
        foreach (var target in new[] { "--capture-lair", "--capture-group", "--capture-building", "--capture-vehicle" })
        {
            string[] arguments = ["--capture-save", "save.json", "--capture-location", "node:1",
                target, "7", "--capture-name", "creatures"];
            var input = CaptureInput.Parse(arguments);
            Require(target == "--capture-lair" ? input.Target == new WorldObjectId.Lair(new(7)) :
                target == "--capture-group" ? input.Target == new WorldObjectId.CreatureGroup(new(7)) :
                target == "--capture-building" ? input.Target == new WorldObjectId.Instance(new InstanceId.Building(new(7))) :
                input.Target == new WorldObjectId.Instance(new InstanceId.Vehicle(new(7))), "必须解析选定的截图目标。");
            foreach (var other in new[] { "--capture-train", "--capture-lair", "--capture-group", "--capture-building", "--capture-vehicle" }.Where(value => value != target))
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
        var lair = new CaptureInput("save.json", location, new WorldObjectId.Lair(new(1)), "lair");
        var group = new CaptureInput("save.json", location, new WorldObjectId.CreatureGroup(new(1)), "group");
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
    public void Building_capture_requires_an_existing_building_in_the_selected_node()
    {
        var building = new CaptureInput("save.json", new WorldLocation.Node(new(1)),
            new WorldObjectId.Instance(new InstanceId.Building(new(1))), "building");
        Require(building.Validate(CreatureView()).SequenceEqual([building.Target]), "建筑必须解析到自身实例。");
        foreach (var (input, view) in new[]
        {
            (building, CreatureView(buildingPresent: false)),
            (building, CreatureView(buildingNode: 2)),
            (building with { Location = new WorldLocation.Edge(new(1), 0) }, CreatureView())
        })
        {
            try
            {
                input.Validate(view);
                throw new InvalidOperationException("不存在或场景不匹配的建筑未被拒绝。");
            }
            catch (InvalidDataException) { }
        }
    }

    [Test]
    public void Vehicle_capture_validates_the_instance_scene_without_matching_edge_distance()
    {
        var vehicle = new CaptureInput("save.json", new WorldLocation.Edge(new(1), 0),
            new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))), "vehicle");
        var view = CreatureView();
        Require(vehicle.Validate(view).SequenceEqual([vehicle.Target]), "同线路上的车辆必须解析到自身实例，不比较线路距离。");
        try
        {
            (vehicle with { Location = new WorldLocation.Node(new(1)) }).Validate(view);
            throw new InvalidOperationException("车辆不在所选节点时未被拒绝。");
        }
        catch (InvalidDataException) { }
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
            await run.RestoreAsync(new(Path.Combine(Output(name), "missing.json"), new WorldLocation.Edge(new(1), 0), new WorldObjectId.Train(new(1)), name));
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
                await run.RestoreAsync(new(save, new WorldLocation.Edge(new(1), 0), new WorldObjectId.Train(new(1)), name));
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

    private async Task SampleMotion(bool turned, WorldObjectId? target = null, bool missingAfterAdvance = false, ulong motionSteps = 2, bool delayAdvancedFrame = false)
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        var root = ProjectSettings.GlobalizePath("res://.godot/validation");
        Directory.CreateDirectory(root);
        var save = Path.Combine(root, name + ".json");
        File.WriteAllText(save, SaveContainer.Encode(new(new(SaveContainer.FormatVersion, Guid.NewGuid(),
            "engine capture input", DateTimeOffset.UnixEpoch, new(0, 0), []), "opaque", "opaque")));
        target ??= new WorldObjectId.Train(new(1));
        var core = new ScriptedCoreSession(target is WorldObjectId.Train ? View(true, true, 1) : CreatureView());
        for (ulong step = 1; step <= 60 * motionSteps; step++)
            core.QueueAdvance(new(1, new(step, 0), [], null, null),
                target is WorldObjectId.Train ? View(true, true, 1, new(step, 0)) :
                    CreatureView(empty: missingAfterAdvance, position: new(step, 0), buildingPresent: !missingAfterAdvance));
        var sessionId = new SceneSessionId(Guid.NewGuid());
        WorldObjectId subject = target switch
        {
            WorldObjectId.Lair => target,
            WorldObjectId.Instance => target,
            WorldObjectId.CreatureGroup => new WorldObjectId.Creature(new(1)),
            _ => new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))
        };
        var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
            [new(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                new(System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
                new(System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, new(0, 1), new(0, 1), ExposedFaceEdges.None, 1, 0)],
            [0, 1, 2]);
        ulong? advancedPresentation = null;
        System.Numerics.Vector3 Position(int index) => new(0, 0, index);
        FixedSceneFrame Frame(int index) => new()
        {
            Update = new(null, new(sessionId, new(1), new(1), (ulong)index + 1), System.Numerics.Vector3.Zero,
                (delayAdvancedFrame || target is WorldObjectId.Instance) && !(missingAfterAdvance && index > 0) ? [new(new(1), null, new(Position(index), System.Numerics.Quaternion.Identity), subject,
                    [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])] : [], [],
                new([], []), []),
            SimulationSeconds = (ulong)index * motionSteps / 60d,
            Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                missingAfterAdvance && index > 0 ? new Lookup<ScenePose>.Missing() : new Lookup<ScenePose>.Found(new(Position(index),
                    System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY,
                        turned && index == 60 ? 0.1f : 0)))),
            TerrainHeightResult = new Lookup<double>.Missing()
        };
        ISceneFrame PresentFrame(SceneFrameRequest request)
        {
            var index = (int)(core.CurrentView.Position.CompletedSteps / motionSteps);
            if (delayAdvancedFrame && index == 1)
            {
                advancedPresentation ??= Godot.Engine.GetProcessFrames();
                if (Godot.Engine.GetProcessFrames() == advancedPresentation.Value) return Frame(0);
            }
            return Frame(index);
        }
        var factory = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(new CaptureDisplaySession(new FixedDisplaySession
            {
                Id = sessionId, Binding = binding,
                RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                    .Add(target is WorldObjectId.Train ? new WorldSceneTarget.Follow(new(1), new(1)) :
                        new WorldSceneTarget.Location(new WorldLocation.Node(new(1))), new SceneResult<SceneRequestId>.Success(new(1))),
                ObserveResults = Enumerable.Range(1, (int)(60 * motionSteps)).Select(step => (SceneResult<SimulationPosition>)
                    new SceneResult<SimulationPosition>.Success(new((ulong)step, 0))).ToImmutableArray()
            }, PresentFrame))
        };
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (logger, _, _, _) => CreateHost(core, logger), CreateScenes = () => factory
        };
        try
        {
            await run.RestoreAsync(new(save, target is WorldObjectId.Train
                ? new WorldLocation.Edge(new(1), 0) : new WorldLocation.Node(new(1)), target, name, motionSteps));
            Vector3? buildingFocus = null;
            if (target is WorldObjectId.Instance)
            {
                await run.PrepareSubjectCameraAsync(96);
                buildingFocus = run.World!.View.CameraRig.Focus;
            }
            for (var frame = 1; frame <= 60; frame++)
            {
                var advance = run.AdvanceTurnFrameAsync();
                if (delayAdvancedFrame && frame == 1)
                {
                    Require(advancedPresentation is not null, "推进后必须先呈现仍然 Ready 的旧采样。");
                    if (target is WorldObjectId.Train)
                    {
                        Require(advance.IsCompletedSuccessfully && run.World!.Display!.DisplayedFrame!.SimulationSeconds == 0,
                            "列车截图必须保持原有 Ready 等待行为，不受生物采样同步条件影响。");
                        break;
                    }
                    Require(!advance.IsCompleted, "生物采样不能在旧 Ready 帧仍可用时结束推进等待。");
                }
                await advance.WaitAsync(TimeSpan.FromSeconds(5));
                if (delayAdvancedFrame && frame == 1)
                {
                    Require(Godot.Engine.GetProcessFrames() > advancedPresentation!.Value,
                        "生物采样必须跨过真实处理帧，等待新候选可提交。");
                    var displayed = run.World!.Display!;
                    Require(displayed.DisplayedFrame!.SimulationSeconds == motionSteps / 60d,
                        "完成等待后必须显示本次推进后的采样时间。");
                    Require(displayed.Objects.Values.Single(item => item.Source.Subject == subject).Root.Position.Z == 1,
                        "完成等待后必须接入新采样的实际对象姿态。");
                }
                if (buildingFocus is { } focus)
                {
                    run.PlaceCamera("follow", 0, 96);
                    await run.DisplayAsync(allowMissing: true, includeCamera: true).WaitAsync(TimeSpan.FromSeconds(5));
                    Require(run.World!.View.CameraRig.Focus.DistanceTo(focus) < 0.001f,
                        "建筑运动采样必须保留固定取景，消失后继续沿用。");
                    if (missingAfterAdvance)
                        Require(run.World.Display!.DisplayedFrame!.FindPose(subject) is Lookup<ScenePose>.Missing &&
                            run.World.Display!.Objects.Values.All(item => item.Source.Subject != subject),
                            "建筑消失后必须继续采样真实的空对象帧。");
                }
                Require(core.CurrentView.Position.CompletedSteps == (ulong)frame * motionSteps,
                    "运动采样必须每帧只推进指定数量的模拟步。");
                Require(run.World!.Display!.DisplayedFrame!.SimulationSeconds == (ulong)frame * motionSteps / 60d,
                    "运动采样必须使用本次推进后的真实显示帧。");
            }
            if (turned) run.RequireTurn();
            else if (target is WorldObjectId.Train)
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

    private static FixedSceneFrame CameraFrame(System.Numerics.Vector3 position, ScenePoint origin,
        ImmutableArray<SceneBounds> fog = default, WorldObjectId? subject = null)
    {
        subject ??= new WorldObjectId.Creature(new(1));
        var pose = new ScenePose(position, System.Numerics.Quaternion.Identity);
        var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
            [new(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                new(System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
                new(System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, new(0, 1), new(0, 1), ExposedFaceEdges.None, 1, 0)],
            [0, 1, 2]);
        return new()
        {
            Origin = origin,
            Update = new(null, new(new(Guid.Empty), new(1), new(1), Godot.Engine.GetProcessFrames() + 1 + (ulong)origin.X),
                System.Numerics.Vector3.Zero,
                [new(new(1), null, pose, subject, subject is WorldObjectId.Lair ? [] :
                    [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])], [],
                new(fog.IsDefault ? [] : fog, []), []),
            Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(pose)),
            TerrainHeightResult = new Lookup<double>.Missing()
        };
    }

    private async Task SampleCamera(WorldObjectId target, Func<SceneFrameRequest, ISceneFrame> frame,
        Func<ArtCaptureRun, Task> inspect, LairAppearanceProjection? appearance = null, FixedCoreReadView? coreView = null)
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://.godot/validation"));
        var save = Output(name) + ".json";
        File.WriteAllText(save, SaveContainer.Encode(new(new(SaveContainer.FormatVersion, Guid.NewGuid(),
            "engine capture input", DateTimeOffset.UnixEpoch, new(0, 0), []), "opaque", "opaque")));
        var core = new ScriptedCoreSession(coreView ?? CreatureView());
        var factory = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(new CaptureDisplaySession(new FixedDisplaySession
            {
                Id = new(Guid.Empty), Binding = binding,
                RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(
                    new WorldSceneTarget.Location(new WorldLocation.Node(new(1))), new SceneResult<SceneRequestId>.Success(new(1)))
            }, frame))
        };
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (logger, _, _, _) => CreateHost(core, logger, appearance), CreateScenes = () => factory
        };
        try
        {
            await run.RestoreAsync(new(save, new WorldLocation.Node(new(1)), target, name)).WaitAsync(TimeSpan.FromSeconds(5));
            await inspect(run);
            Require(core.SubmitCount == 0 && core.CaptureCount == 0, "相机检查不能提交命令或捕获存档。");
        }
        finally { File.Delete(save); }
    }

    private sealed class CaptureDisplaySession(FixedDisplaySession session, Func<SceneFrameRequest, ISceneFrame> frame) : IWorldSceneSession
    {
        public SceneSessionId Id => session.Id;
        public SceneBinding Binding => session.Binding;
        public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target) => session.RequestView(target);
        public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events) =>
            session.Observe(snapshot, events);
        public SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame) =>
            new SceneResult<ISceneFrame>.Success(frame(request));
        public SceneResult<SceneUpdate?> PresentCandidate(SceneFrameStamp? prepared) => session.PresentCandidate(prepared);
        public SceneResult<PickSubject> Resolve(SceneHit hit) => session.Resolve(hit);
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private static FixedCoreReadView CreatureView(bool empty = false, SimulationPosition position = default, bool buildingPresent = true, ulong buildingNode = 1, bool settlementBuilding = false)
    {
        var terrain = new TerrainView(new("fixture:terrain"), 0, []);
        return new(position, HostCoreScenario.Input)
        {
            Economy = UiWorldFixture.Economy,
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [new(1)] = new Lookup<NodeView>.Found(new(new(new(1), new(0, 0), terrain,
                    settlementBuilding ? new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile) : new NodeOccupant.Lair(new(1)), [], 0, 0, null), [], 0, 0, 0))
            },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
            {
                [new(1)] = new Lookup<EdgeView>.Found(new(new(new(1), new(1), new(2), 100,
                    terrain, 1, 1, [], []), [], null))
            },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            {
                [new InstanceId.Vehicle(new(1))] = new Lookup<InstanceView>.Found(new(new InstanceId.Vehicle(new(1)),
                    new("fixture:vehicle"), new("fixture"), new WorldLocation.Edge(new(1), 10), SettlementRelation.PlayerOwned,
                    new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None, new(100, 100), [], [])),
                [new InstanceId.Building(new(1))] = buildingPresent
                    ? new Lookup<InstanceView>.Found(new(new InstanceId.Building(new(1)), new("fixture:building"), new("fixture"),
                        new WorldLocation.Node(new(buildingNode)), SettlementRelation.NonHostile,
                        new InstanceDetails.Building(new(1), PlotSize.SmallStation), InstanceLock.None, new(100, 100), [], []))
                    : new Lookup<InstanceView>.Missing()
            },
            Settlements = settlementBuilding ? new Dictionary<SettlementId, Lookup<SettlementView>>
            {
                [new(1)] = new Lookup<SettlementView>.Found(new(new(1), new(1), new("fixture:town"), null,
                    SettlementRelation.NonHostile, new(1),
                    [new(new(1), 7, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(1)))],
                    [], 0, 0, 0, [], [], [], new(0, 0, 0, 0),
                    new(new(new("fixture:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
                    new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
                    new(new(new("fixture:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), [])))
            } : new Dictionary<SettlementId, Lookup<SettlementView>>(),
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
            Economy = UiWorldFixture.Economy,
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

    private static HostCoordinator CreateHost(ScriptedCoreSession session, TrainLogger logger, LairAppearanceProjection? appearance = null)
    {
        var capture = new FixedCoreCapture(new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References,
            "opaque", HostCoreScenario.Input, new(1));
        var core = new ScriptedCoreFactory
        {
            DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
            PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(session,
                HostCoreScenario.References))
        };
        var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            Lairs = appearance is null ? new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>() :
                new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>
                { [appearance.Id] = new ContentLookup<LairAppearanceProjection>.Found(appearance) }
        };
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

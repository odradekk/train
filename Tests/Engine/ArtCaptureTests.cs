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
    public Task Straight_motion_is_rejected_without_core_commands_or_save_capture() => SampleMotion(turned: false);

    [Test]
    public Task Changed_displayed_heading_satisfies_turn_requirement() => SampleMotion(turned: true);

    [Test]
    public Task Missing_scene_is_rejected_before_output_or_core_writes() =>
        RejectRestore(scenePresent: false, trainPresent: true, trainEdge: 1, "指定场景不存在");

    [Test]
    public Task Missing_train_is_rejected_before_output_or_core_writes() =>
        RejectRestore(scenePresent: true, trainPresent: false, trainEdge: 1, "指定列车不存在或编组为空");

    [Test]
    public Task Train_in_another_scene_is_rejected_before_output_or_core_writes() =>
        RejectRestore(scenePresent: true, trainPresent: true, trainEdge: 2, "列车不在指定场景");

    [Test]
    public async Task Missing_save_is_rejected_before_host_creation()
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (_, _, _) => throw new InvalidOperationException("缺存档时不能创建宿主。")
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
            CreateHost = (logger, _, _) => CreateHost(core, logger),
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

    private async Task SampleMotion(bool turned)
    {
        var name = "capture-test-" + Guid.NewGuid().ToString("N");
        var root = ProjectSettings.GlobalizePath("res://.godot/validation");
        Directory.CreateDirectory(root);
        var save = Path.Combine(root, name + ".json");
        File.WriteAllText(save, SaveContainer.Encode(new(new(SaveContainer.FormatVersion, Guid.NewGuid(),
            "engine capture input", DateTimeOffset.UnixEpoch, new(0, 0), []), "opaque", "opaque")));
        var core = new ScriptedCoreSession(View(true, true, 1));
        for (ulong step = 1; step <= 120; step++)
            core.QueueAdvance(new(1, new(step, 0), [], null, null),
                new FixedCoreReadView(new(step, 0), HostCoreScenario.Input));
        var sessionId = new SceneSessionId(Guid.NewGuid());
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        FixedSceneFrame Frame(int index) => new()
        {
            Update = new(null, new(sessionId, new(1), new(1), (ulong)index + 1), System.Numerics.Vector3.Zero,
                [], [], new([], []), []),
            SimulationSeconds = index * 2d / 60,
            Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(subject,
                new Lookup<ScenePose>.Found(new(new(0, 0, index),
                    System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY,
                        turned && index == 60 ? 0.1f : 0)))),
            TerrainHeightResult = new Lookup<double>.Missing()
        };
        var factory = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(new FixedDisplaySession
            {
                Id = sessionId, Binding = binding,
                RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                    .Add(new WorldSceneTarget.Follow(new(1), new(1)), new SceneResult<SceneRequestId>.Success(new(1))),
                PresentResults = Enumerable.Range(0, 61).Select(index => (SceneResult<ISceneFrame>)
                    new SceneResult<ISceneFrame>.Success(Frame(index))).ToImmutableArray(),
                ObserveResults = Enumerable.Range(1, 120).Select(step => (SceneResult<SimulationPosition>)
                    new SceneResult<SimulationPosition>.Success(new((ulong)step, 0))).ToImmutableArray()
            })
        };
        using var run = new ArtCaptureRun(TestScene)
        {
            CreateHost = (logger, _, _) => CreateHost(core, logger), CreateScenes = () => factory
        };
        try
        {
            await run.RestoreAsync(new(save, new WorldLocation.Edge(new(1), 0), new(1), name));
            for (var frame = 1; frame <= 60; frame++)
            {
                await run.AdvanceTurnFrameAsync();
                Require(core.CurrentView.Position.CompletedSteps == (ulong)frame * 2,
                    "运动采样必须每帧只推进两个模拟步。");
                Require(run.World!.Display!.DisplayedFrame!.SimulationSeconds == frame * 2d / 60,
                    "运动采样必须使用本次推进后的真实显示帧。");
            }
            if (turned) run.RequireTurn();
            else
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

    private static FixedCoreReadView View(bool scenePresent, bool trainPresent, ulong trainEdge)
    {
        var edge = new EdgeMapItem(new(1), new(1), new(2), 100,
            new(new("fixture:terrain"), 0, []), 1, 1, [], []);
        var train = new TrainView(new(1), new TrainPosition.Edge(new(trainEdge), 1, TravelDirection.Forward, 10),
            TrainActivity.Running, [new(1)], new(1), 0, 6, 1, 1, 1, 10, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], null,
            TrainOrderFacing.FirstEndLeading);
        return new(new(0, 0), HostCoreScenario.Input)
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

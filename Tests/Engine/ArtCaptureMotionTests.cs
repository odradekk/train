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

public partial class ArtCaptureTests
{
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
                await new UiTestInput(TestScene).Frames(1);
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

}

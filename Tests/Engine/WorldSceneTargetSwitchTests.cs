using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class WorldSceneTargetTests
{
    [Test]
    public async Task Switching_followed_vehicles_reuses_the_session_and_faulted_requests_return_to_map()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var follow2 = new WorldSceneTarget.Follow(Train, new(2));
        var fault = new SceneProblem("scene_faulted", SceneStage.Input, Subject(2), null, null, null, "观察目标请求故障。");
        FixedDisplaySession? session = null;
        var factory = Factory(binding => session = new()
        {
            Id = SceneId, Binding = binding,
            RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                .Add(Follow3, new SceneResult<SceneRequestId>.Success(new(1)))
                .Add(Follow1, new SceneResult<SceneRequestId>.Success(new(2)))
                .Add(follow2, new SceneResult<SceneRequestId>.Faulted(fault)),
            PresentResults = [Success(Frame(1, 1, 1, 3, new(10, 0, 20))),
                Success(Frame(2, 2, 1, 1, new(110, 0, 20))), Success(Frame(3, 2, 1, 1, new(110, 0, 20)))]
        });
        try
        {
            var app = await Ready(fixture, new Setup(true), factory);
            var world = app.WorldScenes!;
            world.Open(Follow3);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            var focus = world.View.CameraRig.Focus;
            var cameraPosition = world.View.CameraRig.Camera.Position;
            world.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
            world.Open(Follow1);
            Require(factory.OpenCalls.Count == 1 && !session!.IsDisposed && !world.View.Loading && !world.View.Interactive,
                "同列车切换车辆应复用会话，禁用交互且不显示加载罩。");
            world.ProcessFrame(0, false);
            Require(world.View.Interactive && !world.View.Loading && session!.PresentCalls[^1].DisplayedFrame is null,
                "换车辆的Ready首帧没有完整接入，或出现了加载罩。");
            Require(session!.PresentCalls[^1].Request.PickRay is null,
                "同会话切换目标的首个Present仍收到旧请求的拾取样本。");
            Near(world.View.CameraRig.Focus, focus);
            Near(world.View.CameraRig.Camera.Position, cameraPosition);
            world.ProcessFrame(0.05, false);
            Require(world.View.CameraRig.Focus.X > focus.X && world.View.CameraRig.Focus.X < 110 &&
                factory.OpenCalls.Count == 1 && !world.View.Loading,
                "同空间换车辆没有延续相机焦点并逐帧平滑。");
            SceneProblem? reason = null;
            world.ReturnedToMap += problem => reason = problem;
            world.Open(follow2);
            Require(reason == fault && session!.IsDisposed && world.Display is null && !world.View.Visible,
                "Faulted目标请求没有报告故障并退出场景。");
            Require(session!.RequestCalls.SequenceEqual(new[] { Follow3, Follow1, follow2 }),
                "换车辆或故障请求没有经过当前会话RequestView。");
        }
        finally { await fixture.Cleanup(); }
    }
}

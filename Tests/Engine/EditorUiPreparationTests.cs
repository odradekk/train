using System.Collections.Immutable;
using Train.Scenes.ContractSupport;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.App.Saves;
using Train.Ui;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EditorUiTests
{
    [Test]
    public async Task Retry_keeps_full_replacement_request_through_loading_and_resource_budget()
    {
        var fixture = await Ready();
        try
        {
            var controller = fixture.Controller;
            var session = fixture.Factory.Session!;
            session.FailPresent = true;
            controller.Process(0);
            session.FailPresent = false;
            var stamp = controller.Display!.DisplayedFrame!.Update.Frame;
            var loading = true;
            var replacement = new Train.Scenes.ContractSupport.FixedSceneFrame
            {
                Update = new(null, stamp with { Revision = stamp.Revision + 1 }, default,
                    [new(new(1), null, new(default, System.Numerics.Quaternion.Identity), null,
                        [new SceneVisual.Missing(new ContentReference.Design(new("missing-one")), true),
                         new SceneVisual.Missing(new ContentReference.Design(new("missing-two")), true)], [], [])],
                    [], new([], []), []),
                TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing()
            };
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(loading
                ? new Train.Scenes.ContractSupport.FixedSceneFrame { Update = replacement.Update, Readiness = SceneReadiness.Loading }
                : replacement);
            controller.Process(0, true);
            Require(fixture.Editor.SceneUnavailable && session.Baselines[^1] is null && !controller.View.Interactive,
                "Loading不能恢复故障场景或改回增量请求。");
            Require(controller.Pick(Vector2.Zero) is ScenePick.NotReady && controller.PickPlane(Vector2.Zero) is null,
                "手动重试的Loading期间不得产生选择或工作平面回落。");
            loading = false;
            long timestamp = 0;
            controller.Display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
            controller.Process(0);
            Require(fixture.Editor.SceneUnavailable && controller.Display.IsPreparing && session.Baselines[^1] is null,
                "预算用尽时保留完整替换候选，不能提前标记恢复。");
            controller.Display.Timestamp = () => 0;
            await new UiTestInput(TestScene).Frames(1);
            controller.Process(0);
            Require(!fixture.Editor.SceneUnavailable && ReferenceEquals(controller.Display.DisplayedFrame, replacement) &&
                session.Baselines[^1] is null && controller.Display.Root.Visible,
                "完整替换真正提交后才恢复同一会话。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
}

using Chickensoft.GoDotTest;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SettlementSceneOpeningTests
{
    [Test]
    public async Task Simulation_boundaries_keep_settlement_details_waiting_until_the_scene_is_ready()
    {
        foreach (var previouslyOpened in new[] { false, true })
        {
            var after = EconomyFixture.Create(2, relation: SettlementRelation.Hostile);
            var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(),
                EconomyFixture.Content(mapPresentation: true, equipmentName: "装备"));
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            await Run([Frame(2, SceneReadiness.Partial)], (app, ui, shell) =>
            {
                if (previouslyOpened)
                {
                    var status = app.Host!.GetStatus()!;
                    ui.GetNode<EconomyPanels>("Root/World/EconomyPanels").OpenSettlement(
                        new(status.Binding, app.Host.Content!.Generation, Settlement), new(1));
                    Details(shell);
                }
                OpenNode(ui); Waiting(shell);
                Require(app.Host!.Step(1).Problem is null, "等待期间应能发布模拟边界。");
                ui._Process(0);
                Waiting(shell);
                return Task.CompletedTask;
            }, setup);
            setup.Session.AssertComplete();
        }
    }

    [Test]
    public async Task Restoring_another_session_discards_the_old_waiting_inspector()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(),
            EconomyFixture.Content(mapPresentation: true, equipmentName: "装备"));
        setup.ReplacementCandidate = new(UiWorldFixture.Session(EconomyFixture.Create(2)), HostCoreScenario.References);
        await Run([Frame(2, SceneReadiness.Ready)], async (app, ui, shell) =>
        {
            OpenNode(ui); Waiting(shell);
            var host = app.Host!;
            var binding = host.GetStatus()!.Binding;
            Require(host.BeginRestore("opaque core", "opaque content", host.SessionOptions!).Problem is null,
                "替换对局请求应被接受。");
            await FramesUntil(TestScene, () =>
            {
                host.ProcessFrame(0);
                return !host.IsBusy && host.GetStatus()!.Binding != binding;
            });
            Draw(); ui._Process(0);
            Require(shell.SelectedObject is null && shell.InspectorContent is null,
                "读档更换对局后不能发布上一局的等待检查器。");
        }, setup);
    }

    [Test]
    public async Task Publishing_another_content_generation_discards_the_waiting_inspector()
    {
        var view = EconomyFixture.Create();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true, equipmentName: "装备"));
        await Run([Frame(2, SceneReadiness.Ready)], async (app, ui, shell) =>
        {
            OpenNode(ui); Waiting(shell);
            var host = app.Host!;
            setup.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(
                new FixedCoreCapture(new(view.Position, "fixed", "fixed"), HostCoreScenario.References,
                    "opaque", view.InputIdentity, view.Revision)));
            var content = EconomyFixture.Content(2, mapPresentation: true, equipmentName: "装备");
            using var reload = new ScriptedContentCandidate(content, host.Content!, ContentChange.Presentation);
            setup.ContentFactory!.PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Reload
                ? new ContentResult<IContentCandidate>.Success(reload)
                : throw new InvalidOperationException("UNCONFIGURED: " + request.Purpose);
            Require(host.BeginReload(CommandSource.UserInterface).Problem is null, "内容重载请求应被接受。");
            await FramesUntil(TestScene, () =>
            {
                host.ProcessFrame(0);
                return !host.IsBusy && ReferenceEquals(host.Content, content);
            });
            Draw(); ui._Process(0);
            Require(shell.SelectedObject is null && shell.InspectorContent is null,
                "内容世代更换后不能发布原世代的等待检查器。");
        }, setup);
    }

    [Test]
    public async Task Leaving_the_session_discards_the_waiting_inspector()
    {
        await Run([Frame(2, SceneReadiness.Ready)], (app, ui, shell) =>
        {
            OpenNode(ui); Waiting(shell);
            Require(app.Saves!.ReturnToMenu() is null, "等待期间应能返回主菜单。");
            if (app.Saves.Leaving is not null) app.Saves.ChooseLeave(LeaveChoice.Discard);
            Draw(); ui._Process(0);
            Require(app.Host!.GetStatus() is null && shell.InspectorContent is null &&
                !ui.World.IsVisibleInTree(), "退出会话后不能保留或重新打开等待检查器。");
            return Task.CompletedTask;
        });
    }
}

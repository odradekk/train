using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Live_world_keeps_blueprint_mouse_selection_across_frames()
    {
        var blueprint = LiveBlueprint(false, "live-vehicle");
        var design = ((DesignEvaluation.Complete)blueprint.Evaluation).Core!;
        var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var setup = new HostSimulationTests.HostSetup(new ServiceView(expectedDesign: design), content);
        using var candidate = new ScriptedContentCandidate(content, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        await Run(setup, async (ui, panel) =>
        {
            setup.ContentFactory!.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
            var work = await OpenManufacture(ui, panel);
            var choice = Find<Button>(work, "Blueprint_0");
            await ScrollToLiveButton(choice);
            var position = choice.GetGlobalRect().GetCenter();
            await _input.MouseButton(position, Godot.MouseButton.Left, true);
            for (ulong revision = 2; revision <= 4; revision++)
            {
                PublishLiveView(setup, ui, new ServiceView(revision, expectedDesign: design));
                await _input.Frames();
            }
            await _input.MouseButton(position, Godot.MouseButton.Left, false);
            await _input.Frames(2);
            Require(Find<Button>(work, "Blueprint_0").ButtonPressed && !Find<Button>(work, "ConfirmWork").Disabled,
                "世界逐帧推进后，跨帧鼠标点击必须仍能选中蓝图并获得可确认报价。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Live_world_keeps_confirmation_mouse_press_until_release()
    {
        var setup = new HostSimulationTests.HostSetup(new ServiceView(), EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            var confirm = Find<Button>(work, "ConfirmWork");
            await ScrollToLiveButton(confirm);
            var position = confirm.GetGlobalRect().GetCenter();
            await _input.MouseButton(position, Godot.MouseButton.Left, true);
            ServiceView current = null!;
            for (ulong revision = 2; revision <= 4; revision++)
            {
                current = new ServiceView(revision);
                PublishLiveView(setup, ui, current);
                await _input.Frames();
            }
            Require(current.LastQuote is { Blockers.IsEmpty: true }, "跨帧确认夹具必须保持可施工。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(current.LastQuote!.Request, new(999)),
                CommandSource.UserInterface, Completed(1, current, current, new CommandDecision.Accepted([], [])), current);
            await _input.MouseButton(position, Godot.MouseButton.Left, false);
            await _input.Frames(2);
            Require(setup.Session.SubmitCount == 1, "世界逐帧推进不得取消鼠标按住的确认按钮；释放后应只提交一次。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Live_world_does_not_quote_every_building_blueprint_each_frame()
    {
        var first = LiveBlueprint(true, "first-building");
        var second = first with { Address = new(BlueprintLibrary.Local, new("second-building")) };
        var design = ((DesignEvaluation.Complete)first.Evaluation).Core!;
        var setup = new HostSimulationTests.HostSetup(
            new ServiceView(owned: true, emptyPlot: true, expectedDesign: design),
            EconomyFixture.Content(vehicleBlueprint: first, otherBlueprint: second));
        await Run(setup, async (ui, panel) =>
        {
            await Buildings(panel);
            await _input.Activate(Find<Button>(panel, "BuildPlot_3"));
            var work = Find<Control>(ui, "ConstructionWorkspace");
            Require(work.FindChildren("Blueprint_*", "Button", true, false).Count == 2,
                "建筑报价夹具必须展示多个合法蓝图。");
            for (ulong revision = 2; revision <= 4; revision++)
            {
                var current = new ServiceView(revision, owned: true, emptyPlot: true, expectedDesign: design);
                PublishLiveView(setup, ui, current);
                await _input.Frames();
                Require(current.QuoteCount <= 1,
                    $"未改变蓝图和地块时，每帧不得遍历报价所有建筑蓝图；本帧获取了 {current.QuoteCount} 次报价。");
            }
        });
        setup.Session.AssertComplete();
    }

    private async Task ScrollToLiveButton(Button button)
    {
        Require(button.IsVisibleInTree() && !button.Disabled, "跨帧鼠标操作必须从可用按钮开始。");
        for (var parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await _input.Frames(2);
    }

    private static void PublishLiveView(HostSimulationTests.HostSetup setup, GameUi ui, ServiceView view)
    {
        setup.Session.QueueAdvance(new(1, view.Position, [], null, null), view);
        var host = ui.GetParent<Train.App.App>().Host!;
        Require(host.Step(1).Problem is null, "无法发布逐帧推进的世界读视图。");
        Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, view, host.Content!);
    }

    private static BlueprintRecord LiveBlueprint(bool building, string id)
    {
        DesignKind kind = building ? new DesignKind.Building(PlotSize.Medium, BuildingKind.Production, [])
            : new DesignKind.Vehicle(SizeClass.Small, 2);
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new(id), new(id + "-composition"), kind, [], 10, 10, 1, new(100), [],
                [new(EconomyFixture.Food, 1)], 1, [], true, []), []);
        DesignSpecification specification = building
            ? new DesignSpecification.Building(PlotSize.Medium, BuildingKind.Production, [])
            : new DesignSpecification.Vehicle(SizeClass.Small);
        var draft = new DesignDraft(new("连续推进测试蓝图", ""), specification, new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
        return new(new(BlueprintLibrary.Local, new(id)), draft, [], evaluation);
    }
}

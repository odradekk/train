using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
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
    public async Task Core_plot_size_block_disables_blueprint_and_explains_required_plot()
    {
        var draft = new DesignDraft(new("需要大地块的建筑", ""),
            new DesignSpecification.Building(PlotSize.Large, BuildingKind.Production, []), new([], [], []));
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("large-building"), new("large-composition"), new DesignKind.Building(PlotSize.Large, BuildingKind.Production, []),
                [], 10, 1, 1, new(1), [], [], 0, [], true, []), []);
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("large-blueprint")), draft, [], evaluation);
        var view = new ServiceView(owned: true, emptyPlot: true, expectedDesign: design, plotTooSmall: true);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(vehicleBlueprint: blueprint));
        await Run(setup, async (ui, panel) =>
        {
            await Buildings(panel);
            await _input.Activate(Find<Button>(panel, "BuildPlot_3"));
            var work = Find<Control>(ui, "ConstructionWorkspace");
            var choice = Find<Button>(work, "Blueprint_0");
            Require(view.LastQuote!.Blockers.Any(problem => problem.Code == "conditions_not_met" && problem.Field == "Plot") &&
                choice.Disabled && choice.Text.Contains("需要大地块"),
                "核心判定地块放不下时应禁用蓝图并说明需要的地块尺寸。");
            Require(Find<Button>(work, "ConfirmWork").Disabled && setup.Session.SubmitCount == 0,
                "不适用地块的蓝图仍可开工。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Building_blueprint_quotes_sources_and_submits_build_building_on_owned_settlement()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("building-design"), new("building-composition"),
                new DesignKind.Building(PlotSize.Medium, BuildingKind.Production, [FacilityTag.FoodProcessing]),
                [], 10, 10, 1, new(100), [], [new(EconomyFixture.Food, 1)], 1, [], true, []), []);
        var draft = new DesignDraft(new("本局建筑蓝图", ""),
            new DesignSpecification.Building(PlotSize.Medium, BuildingKind.Production, [FacilityTag.FoodProcessing]), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("building-blueprint")), draft, [], evaluation);
        var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var target = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var blocked = new ServiceView(owned: true, blocked: true, nullPayment: true, emptyPlot: true, expectedDesign: design);
        var clear = new ServiceView(2, owned: true, nullPayment: true, emptyPlot: true, expectedDesign: design);
        var setup = new HostSimulationTests.HostSetup(blocked, content);
        var edit = new PreparedContentEdit.ConstructionTarget(design);
        using var candidate = new ScriptedContentCandidate(target, content, ContentChange.Persistent, edit: edit);
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (basis, requestedEdit) =>
            {
                Require(ReferenceEquals(basis, content) && requestedEdit is ContentEdit.ConstructionTarget requested &&
                    ReferenceEquals(requested.Target, draft), "建筑制造没有把所选蓝图交给内容准备目标。");
                return new ContentResult<IContentCandidate>.Success(candidate);
            };
            await Buildings(economy);
            await _input.Activate(Find<Button>(economy, "BuildPlot_3"));
            var work = Find<Control>(ui, "ConstructionWorkspace");
            await _input.Activate(Find<Button>(work, "Blueprint_0"));
            var quote = blocked.LastQuote!;
            Require(quote.Request is JobRequest.BuildBuilding build && build.Plot == new PlotId(3) &&
                ReferenceEquals(build.Design, design) && build.Sources.SequenceEqual(new InventoryId[] { new(10) }),
                "建筑报价必须固定当前地块且仅用当地库存。");
            Require(!work.FindChildren("Source_*", "CheckBox", true, false).Any(), "自有施工不应出现来源选择。");
            Require(!work.FindChildren("MaterialPurchase_*", "Label", true, false).Cast<Label>().Any(label => label.IsVisibleInTree()) &&
                !work.FindChildren("MaterialPayment_*", "Label", true, false).Cast<Label>().Any(label => label.IsVisibleInTree()),
                "自有施工材料表不应显示当地购买和费用列。");
            Require(Find<Label>(work, "WorkReason").Text.Contains("地块已被占用") && Find<Button>(work, "ConfirmWork").Disabled,
                "必须展示核心返回的地块阻塞，不得本地改写为可建。");
            Require(candidate.Activations == 0, "阻塞报价不得激活内容候选。");
            setup.Session.QueueAdvance(new(1, clear.Position, [], null, null), clear);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "无法发布可建造的读视图。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, clear, host.Content!);
            await _input.Frames(2);
            quote = clear.LastQuote!;
            Require(quote.Blockers.IsEmpty && !Find<Button>(work, "ConfirmWork").Disabled, "核心新边界未自动恢复可建报价。");
            setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(quote.Request), CommandSource.UserInterface,
                Completed(1, clear, clear, new CommandDecision.Accepted([], [])), clear);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            await _input.WaitUntil(() => candidate.Activations == 1);
            Require(setup.Session.SubmitCount == 1 && candidate.Activations == 1 && quote.Request is JobRequest.BuildBuilding &&
                ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, target), "确认没有提交自有建筑建造或没有激活当前候选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Buildings_page_lists_buildings_empty_plots_and_sites_without_submitting()
    {
        var view = new ServiceView(owned: true, emptyPlot: true, constructionPlot: true, buildingLock: InstanceLock.UnderRepair);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            var tabs = Find<TabContainer>(economy, "Pages");
            var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "建筑");
            await _input.ClickTab(tabs, index);
            var buildings = economy.FindChildren("SelectBuilding_*", "Button", true, false).Cast<Button>().ToArray();
            Require(buildings.Length == 2 && buildings.Any(item => item.Text.Contains("施工建筑")) &&
                buildings.Any(item => item.Text.Contains("普通建筑")) && buildings.All(item => item.Text.Contains("小地块")),
                "建筑页应使用显示名列出各建筑及地块尺寸。");
            Require(economy.FindChildren("BuildPlot_3", "Button", true, false).Count == 1 &&
                economy.FindChildren("ConstructionJob_4", "", true, false).Count == 1 &&
                !economy.FindChildren("OtherPlots", "Label", true, false).Cast<Label>().Any(label => label.IsVisibleInTree() && label.Text.Length > 0),
                "空地和工地应各显示一次，不再重复汇总。");
            Require(setup.Session.SubmitCount == 0, "浏览建筑页不应提交命令。");
            HideCaptureChrome(ui);
            await UiCapture.Capture(TestScene, "plots");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Building_saved_draft_uses_content_immediate_flag_with_the_same_confirm_button()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("refit-building"), new("fixture-plain-composition"),
                new DesignKind.Building(PlotSize.Small, BuildingKind.Production, []),
                [], 100, 1, 1, new(1), [], [], 0, [], true, []), []);
        var draft = new DesignDraft(new("建筑改装草稿", ""),
            new DesignSpecification.Building(PlotSize.Small, BuildingKind.Production, []), new([], [], []));
        var saved = new SavedContentDraft(new(Guid.Parse("00000000-0000-0000-0000-000000000021")), new(1),
            new DraftTarget.Instance(new InstanceId.Building(new(21))), new DraftPayload.Design(draft));
        var change = new DesignChangeProjection(new("fixture-plain-building"), new("fixture-plain-composition"), design, [], [], [], 0);
        var view = new ServiceView(owned: true, expectedDesign: design);
        var content = EconomyFixture.Content(drafts: [saved]);
        var setup = new HostSimulationTests.HostSetup(view, content);
        using var candidate = new ScriptedContentCandidate(EconomyFixture.Content(generation: 2, drafts: [saved]), content,
            ContentChange.Persistent, edit: new PreparedContentEdit.InstanceTarget(new(change, [], true, [])));
        await Run(setup, async (ui, panel) =>
        {
            setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
            {
                Require(ReferenceEquals(basis, content) && edit is ContentEdit.InstanceTarget requested &&
                    requested.Current == new DesignHash("fixture-plain-building") && ReferenceEquals(requested.Target, draft),
                    "建筑改装未准备当前建筑的保存草稿。");
                return new ContentResult<IContentCandidate>.Success(candidate);
            };
            await Buildings(panel);
            await _input.Activate(Find<Button>(panel, "SelectBuilding_21"));
            await _input.Activate(Find<Button>(Find<Control>(panel, "Building_21"), "RefitBuilding"));
            var work = Find<Control>(ui, "ConstructionWorkspace");
            Require(view.QuoteCount == 0 && !Find<Button>(work, "ConfirmWork").Disabled && candidate.Activations == 0,
                "内容允许立即应用时不应获取施工报价或提前激活。");
            setup.Session.QueueSubmit(new CoreCommand.ApplyInstanceDesign(new InstanceId.Building(new(21)), change),
                CommandSource.UserInterface, Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            Require(setup.Session.SubmitCount == 1 && candidate.Activations == 1 && view.QuoteCount == 0,
                "统一确认未立即应用建筑设计或创建了施工任务。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Damaged_building_repair_uses_only_settlement_inventory_and_healthy_building_does_not_quote()
    {
        foreach (var (damagedStructure, damagedPart) in new[] { (false, false), (true, false), (false, true) })
        {
            var view = new ServiceView(owned: true, damagedBuilding: damagedStructure, damagedBuildingPart: damagedPart);
            var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
            await Run(setup, async (_, panel) =>
            {
                await Buildings(panel);
                Require(view.QuoteCount == 0, "浏览建筑列表不应报价。");
                await _input.Activate(Find<Button>(panel, "SelectBuilding_21"));
                var row = Find<Control>(panel, "Building_21");
                var button = Find<Button>(row, "RepairBuilding");
                if (!damagedStructure && !damagedPart)
                {
                    Require(view.QuoteCount == 0 && button.Disabled && Find<Label>(row, "RepairReason").Text.Contains("没有损伤"),
                        "完好建筑不应获取报价或允许修理。");
                    return;
                }
                Require(view.LastQuote is { Funding: JobFunding.OwnInventory, Request: JobRequest.Repair request } &&
                    request.Instance == new InstanceId.Building(new(21)) && request.Sources.SequenceEqual(new InventoryId[] { new(10) }),
                    damagedPart ? "结构完好但部件受损的建筑应获取当地库存修理报价。" : "建筑修理必须只取当地库存。");
                Require(!button.Disabled, "受损建筑有效报价不能修理。");
                setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(view.LastQuote!.Request), CommandSource.UserInterface,
                    Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
                await _input.Activate(button);
                Require(setup.Session.SubmitCount == 1, "建筑修理没有提交自有任务。");
            });
            setup.Session.AssertComplete();
        }
    }

    [Test]
    public async Task Building_repair_displays_core_facility_block_without_submitting()
    {
        var view = new ServiceView(owned: true, damagedBuilding: true, facilityBlocked: true, secondFacility: true, buildingLock: InstanceLock.UnderRepair);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Buildings(panel);
            await _input.Activate(Find<Button>(panel, "SelectBuilding_21"));
            var row = Find<Control>(panel, "Building_21");
            Require(Find<Button>(row, "RepairBuilding").Disabled && Find<Label>(row, "RepairReason").Text == "条件未满足。",
                "设施锁定未显示中文阻塞或仍可修理。");
            Require(setup.Session.SubmitCount == 0, "设施受阻仍创建修理任务。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Vehicle_manufacture_lists_vehicle_blueprints_only()
    {
        BlueprintRecord Blueprint(bool building)
        {
            var draft = new DesignDraft(new(building ? "建筑蓝图" : "车辆蓝图", ""),
                building ? new DesignSpecification.Building(PlotSize.Small, BuildingKind.Production, []) : new DesignSpecification.Vehicle(SizeClass.Small),
                new([], [], []));
            var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
                new(new(building ? "building-target" : "vehicle-target"), new("target-composition"),
                    building ? new DesignKind.Building(PlotSize.Small, BuildingKind.Production, []) : new DesignKind.Vehicle(SizeClass.Small, 2),
                    [], 100, 1, 1, new(1), [], [], 0, [], true, []), []);
            var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
                new(null, null, null, true, null, null, [], [], [], [], []), true, []);
            return new(new(BlueprintLibrary.Local, new(building ? "building" : "vehicle")), draft, [], evaluation);
        }
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(vehicleBlueprint: Blueprint(false), otherBlueprint: Blueprint(true)));
        await Run(setup, async (ui, panel) =>
        {
            var work = await OpenManufacture(ui, panel);
            var choices = Find<Control>(work, "WorkChoices").FindChildren("Blueprint_*", "Button", true, false).Cast<Button>().ToArray();
            Require(choices.Length == 1 && choices[0].Text.Contains("车辆蓝图"), "车辆制造应只列车辆蓝图。");
            Require(view.QuoteCount == 0 && setup.Session.SubmitCount == 0, "浏览制造蓝图就获取报价或创建任务。");
        });
        setup.Session.AssertComplete();
    }

    private async Task Buildings(Control panel)
    {
        var tabs = Find<TabContainer>(panel, "Pages");
        await _input.ChooseTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "建筑"));
        await _input.Frames();
    }
}

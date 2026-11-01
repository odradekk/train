using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class ProductionPopulationUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Production_displays_current_and_frozen_flows_without_internal_identifiers()
    {
        var original = EconomyFixture.Create();
        var process = new ProcessRef.Production(new("fixture:press"));
        var missing = new ProcessRef.Production(new("fixture:missing"));
        var removed = new ProcessRef.Production(new("fixture:removed"));
        var content = EconomyFixture.Content(
            processes: [new(new ProcessDetails.Production(process.Definition, [], [new(EconomyFixture.Food, 2)]),
                new("v2"), 10, 1, 1, FacilityTag.FoodProcessing, [])],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(process)] = new ContentLookup<ContentDisplay>.Found(new(new("固定生产流程", ""), null, null, false)),
                [new CoreDefinitionReference.Process(missing)] = new ContentLookup<ContentDisplay>.Missing([]),
                [new CoreDefinitionReference.Process(removed)] = new ContentLookup<ContentDisplay>.NotFound()
            });
        ImmutableArray<FacilityView> facilities =
        [
            new(new(new(1), CapabilityKind.Production), process, true, 0, ProductionState.Running, 1, 1, [],
                new(process, new("v1"), 10, 3, .3, [new(EconomyFixture.Food, 2)], true), [], [],
                [new("output_blocked", "Facility", new WorldObjectId.Instance(new InstanceId.Building(new(1))), ["voxel:core:staple-food"])]),
            new(new(new(2), CapabilityKind.Production), missing, true, 0, ProductionState.ProcessMissing, 1, 0, [],
                new(removed, new("v0"), 17, 4, 4.0 / 17, [new(EconomyFixture.Food, 4)], true), [], [], [])
        ];
        var view = View(original, 2, new PopulationExecution.NotStarted([]), facilities);
        var setup = new HostSimulationTests.HostSetup(view, content);
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var binding = app.Host!.GetStatus()!.Binding;
            var economy = Find<EconomyPanels>(app, "EconomyPanels");
            economy.OpenSettlement(new(binding, content.Generation, new WorldObjectId.Settlement(new(1))), new(1));
            economy.Refresh(binding, view, content);
            await OpenProduction(Find<PanelContainer>(app, "Settlement"));
            var specifications = Find<PanelContainer>(app, "Settlement").FindChildren("CurrentSpecification", "Label", true, false).Cast<Label>().ToArray();
            var found = specifications.Single(label => label.Text.Contains("固定生产流程"));
            var foundBatch = Find<Label>(found.GetParent(), "Batch").Text;
            Require(found.Text.Contains("固定生产流程") && foundBatch.Contains("固定生产流程") && foundBatch.Contains("30%"),
                "建筑页应分别展示当前流程和已开始批次的流程与进度。");
            var blockers = Find<Label>(found.GetParent(), "Blockers").Text;
            Require(blockers.Contains("库存") && !blockers.Contains("建筑 1") && !blockers.Contains("output_blocked"),
                "生产受阻没有显示中文说明：" + blockers);
            var absent = specifications.Single(label => label.Text.Contains("流程内容缺失"));
            var absentBatch = Find<Label>(absent.GetParent(), "Batch").Text;
            Require(absentBatch.Contains("流程内容缺失") && !absent.Text.Contains("fixture:") && !absentBatch.Contains("fixture:") &&
                !found.Text.Contains("v2") && !foundBatch.Contains("v1"),
                "流程缺失时应说明原因，不能向玩家展示定义标识或修订。");
            Require(absentBatch.Contains("24%") &&
                Find<VBoxContainer>(absent.GetParent(), "FrozenOutputs").Visible,
                "流程缺失后冻结批次的进度或产出被隐藏。");
            Require(setup.Session.SubmitCount == 0, "生产只读页面提交了命令。");
        }
        finally { await fixture.Cleanup(); }
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Production_plan_filters_processes_submits_priority_and_requeries_batch_separately()
    {
        var press = new ProcessRef.Production(new("fixture:press"));
        var weave = new ProcessRef.Production(new("fixture:weave"));
        var reuse = new ProcessRef.Recovery(new("fixture:reuse"));
        var dig = new ProcessRef.Production(new("fixture:dig"));
        var content = EconomyFixture.Content(
            processes:
            [
                new(new ProcessDetails.Production(press.Definition, [], [new(EconomyFixture.Food, 2)]), new("v2"), 10, 1, 1, FacilityTag.FoodProcessing, []),
                new(new ProcessDetails.Production(weave.Definition, [], []), new("v2"), 10, 1, 1, FacilityTag.Assembly, []),
                new(new ProcessDetails.Recovery(reuse.Definition, [], [new(EconomyFixture.Food, 1)]), new("v2"), 8, 1, 1, FacilityTag.FoodProcessing, []),
                new(new ProcessDetails.Extraction(dig.Definition, new(EconomyFixture.Food, 1)), new("v2"), 6, 1, 1, FacilityTag.FoodProcessing, [])
            ],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(press)] = new ContentLookup<ContentDisplay>.Found(new(new("压榨", ""), null, null, false)),
                [new CoreDefinitionReference.Process(weave)] = new ContentLookup<ContentDisplay>.Found(new(new("装配", ""), null, null, false)),
                [new CoreDefinitionReference.Process(reuse)] = new ContentLookup<ContentDisplay>.Found(new(new("回收", ""), null, null, false)),
                [new CoreDefinitionReference.Process(dig)] = new ContentLookup<ContentDisplay>.Found(new(new("开采", ""), null, null, false))
            });
        ActiveBatchView Batch() => new(reuse, new("batch-v1"), 10, 1, .1, [new(EconomyFixture.Food, 1)], true);
        var basis = EconomyFixture.Create();
        var empty = View(basis, 1, new PopulationExecution.NotStarted([]), [Facility(null, false, 0, null)], buildingDesign: "fixture-food-building");
        var planned = View(basis, 2, new PopulationExecution.NotStarted([]), [Facility(press, true, 0, Batch())], buildingDesign: "fixture-food-building");
        var raised = View(basis, 3, new PopulationExecution.NotStarted([]), [Facility(press, true, -1, Batch())], buildingDesign: "fixture-food-building");
        var lowered = View(basis, 4, new PopulationExecution.NotStarted([]), [Facility(press, true, 1, Batch())], buildingDesign: "fixture-food-building");
        var middle = View(basis, 5, new PopulationExecution.NotStarted([]), [Facility(press, true, 0, Batch())], buildingDesign: "fixture-food-building");
        var stopped = View(basis, 6, new PopulationExecution.NotStarted([]), [Facility(press, false, 0, Batch())], buildingDesign: "fixture-food-building");
        var setup = new HostSimulationTests.HostSetup(empty, content);
        await Run(setup, async (ui, economy) =>
        {
            await OpenProduction(economy);
            var facility = Find<VBoxContainer>(economy, "Facility");
            var choice = Find<OptionButton>(facility, "Process");
            var names = Enumerable.Range(0, choice.ItemCount).Select(choice.GetItemText).ToArray();
            Require(names[0] == "选择流程" && names.Any(name => name.Contains("压榨") && name.Contains("主食") && name.Contains("产出") && name.Contains("消耗")) &&
                names.Any(name => name.Contains("回收")) && !names.Any(name => name.Contains("装配") || name.Contains("开采")),
                "流程应按能力和标签筛选，并写明产出与消耗。");
            Require(Find<Label>(facility, "CurrentSpecification").Text.Contains("未选择流程") && setup.Session.SubmitCount == 0,
                "空计划被写成已有流程或打开页面就提交了。");
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 0),
                CommandSource.UserInterface, Completed(1, empty, planned, new CommandDecision.Accepted([], [])), planned);
            await _input.Choose(choice, Array.FindIndex(names, name => name.Contains("压榨")));
            Require(Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨") &&
                Find<Label>(facility, "Batch").Text.Contains("回收") && Find<CheckButton>(facility, "Enabled").ButtonPressed,
                "受理后没有重新查询计划，或批次流程被写成计划流程。");
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, -1),
                CommandSource.UserInterface, Completed(2, planned, raised, new CommandDecision.Accepted([], [])), raised);
            await _input.Activate(Find<Button>(facility, "PriorityHigh"));
            Require(Find<Button>(facility, "PriorityHigh").ButtonPressed && Find<Label>(facility, "Batch").Text.Contains("回收"),
                "只改优先级后没有重新查询，或批次被替换。");
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 1),
                CommandSource.UserInterface, Completed(3, raised, lowered, new CommandDecision.Accepted([], [])), lowered);
            await _input.Activate(Find<Button>(facility, "PriorityLow"));
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 0),
                CommandSource.UserInterface, Completed(4, lowered, middle, new CommandDecision.Accepted([], [])), middle);
            await _input.Activate(Find<Button>(facility, "PriorityMiddle"));
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, false, 0),
                CommandSource.UserInterface, Completed(5, middle, stopped, new CommandDecision.Accepted([], [])), stopped);
            await _input.Activate(Find<CheckButton>(facility, "Enabled"));
            Require(setup.Session.SubmitCount == 5 && !Find<CheckButton>(facility, "Enabled").ButtonPressed &&
                Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨") &&
                !facility.FindChildren("ApplyPlan*", "Button", true, false).Any(), "改动应立即提交，并保留当前流程和冻结批次。");
            await ShowPlanControls(economy);
            HideCaptureChrome(ui);
            await UiCapture.Capture(TestScene, "production-plan");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Production_flow_change_preserves_existing_priority_when_a_revision_arrives_in_the_menu()
    {
        var press = new ProcessRef.Production(new("fixture:press"));
        var content = EconomyFixture.Content(
            processes: [new(new ProcessDetails.Production(press.Definition, [], [new(EconomyFixture.Food, 2)]), new("v2"), 10, 1, 1, FacilityTag.FoodProcessing, [])],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(press)] = new ContentLookup<ContentDisplay>.Found(new(new("压榨", ""), null, null, false))
            });
        var basis = EconomyFixture.Create();
        var empty = View(basis, 1, new PopulationExecution.NotStarted([]), [Facility(null, false, 7, null)], buildingDesign: "fixture-food-building");
        var stepped = View(basis, 2, new PopulationExecution.NotStarted([]), [Facility(null, false, 7, null)], buildingDesign: "fixture-food-building");
        var planned = View(basis, 3, new PopulationExecution.NotStarted([]), [Facility(press, true, 7, null)], buildingDesign: "fixture-food-building");
        var setup = new HostSimulationTests.HostSetup(empty, content);
        await Run(setup, async (ui, economy) =>
        {
            await OpenProduction(economy);
            var facility = Find<VBoxContainer>(economy, "Facility");
            var choice = Find<OptionButton>(facility, "Process");
            var host = ui.GetParent<Train.App.App>().Host!;
            await _input.Choose(choice, Enumerable.Range(0, choice.ItemCount).Single(index => choice.GetItemText(index).Contains("压榨")), () =>
            {
                setup.Session.QueueAdvance(new(1, stepped.Position, [], null, null), stepped);
                Require(host.Step(1).Problem is null, "无法发布设施未变的新修订。");
                setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 7),
                    CommandSource.UserInterface, Completed(1, stepped, planned, new CommandDecision.Accepted([], [])), planned);
            });
            Require(setup.Session.SubmitCount == 1 && Find<Button>(facility, "PriorityLow").ButtonPressed &&
                Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨"),
                "菜单打开时的新修订不能改写已有整数优先级，流程选择应立即提交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Unowned_production_plan_controls_do_not_submit()
    {
        var press = new ProcessRef.Production(new("fixture:press"));
        var view = View(EconomyFixture.Create(), 1, new PopulationExecution.NotStarted([]), [Facility(press, true, 2, null)],
            SettlementRelation.NonHostile, "fixture-food-building");
        var content = EconomyFixture.Content(
            processes: [new(new ProcessDetails.Production(press.Definition, [], []), new("v2"), 10, 1, 1, FacilityTag.FoodProcessing, [])],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(press)] = new ContentLookup<ContentDisplay>.Found(new(new("压榨", ""), null, null, false))
            });
        var setup = new HostSimulationTests.HostSetup(view, content);
        await Run(setup, async (_, economy) =>
        {
            var tabs = Find<TabContainer>(economy, "Pages");
            Require(!Enumerable.Range(0, tabs.GetTabCount()).Any(i => tabs.GetTabTitle(i) == "生产") &&
                setup.Session.SubmitCount == 0, "非敌对聚居地仍展示生产页或提交了生产命令。");
        });
        setup.Session.AssertComplete();
    }

    private static FacilityView Facility(ProcessRef? process, bool enabled, int priority, ActiveBatchView? batch) =>
        new(new(new(1), CapabilityKind.Production), process, enabled, priority,
            enabled ? ProductionState.Running : ProductionState.Disabled, 1, 0, [], batch, [], [], []);

    private async Task ShowPlanControls(Control economy)
    {
        await _input.Frames(2);
        foreach (var scroll in new[]
        {
            economy.GetNode<ScrollContainer>("Layout/Content/Body/Pages/建筑/Page"),
            economy.GetNode<ScrollContainer>("Layout/Content")
        })
        {
            var maximum = (int)scroll.GetVScrollBar().MaxValue;
            if (maximum > 0) scroll.ScrollVertical = maximum;
        }
        await _input.Frames(2);
    }

    private static void HideCaptureChrome(GameUi ui)
    {
        ui.GetNode<CanvasItem>("Root/ErrorPanel").Hide();
        foreach (var detail in ui.FindChildren("Details", "PanelContainer", true, false))
            ((CanvasItem)detail).Hide();
    }

    private async Task OpenProduction(Control economy)
    {
        var tabs = Find<TabContainer>(economy, "Pages");
        var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "建筑");
        await _input.ChooseTab(tabs, index);
        await _input.Frames();
        await _input.Activate(Find<Button>(economy, "SelectBuilding_1"));
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, Control, Task> body)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects"), 0);
            await body(ui, Find<PanelContainer>(ui, "Settlement"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

}

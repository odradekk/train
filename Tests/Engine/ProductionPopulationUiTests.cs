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

public class ProductionPopulationUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Run_reports_distinguish_undefined_empty_and_overloaded_capacity()
    {
        var original = EconomyFixture.Create();
        var content = EconomyFixture.Content();
        var setup = new HostSimulationTests.HostSetup(original, content);
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var binding = app.Host!.GetStatus()!.Binding;
            var economy = Find<EconomyPanels>(app, "EconomyPanels");
            economy.OpenReports();
            ulong revision = 2;
            foreach (var (ratio, expected) in new (double? Ratio, string Expected)[]
            {
                (null, "—（未进入线路，或进入时没有载货容量）"),
                (0, "0.0%"),
                (1.5, "150.0%")
            })
            {
                var totals = new RunAccumulatorView(new(0, 0), new(0), new(0), new(17), -17, 0, ratio, []);
                var view = new FixedCoreReadView(original.Position, original.InputIdentity, revision: new(revision++))
                {
                    World = original.World,
                    Economy = original.Economy with
                    {
                        RecentRunReports = [new(new(7), original.Position, RunEndReason.PlanReplaced, totals)]
                    }
                };
                economy.Refresh(binding, view, content);
                var text = Find<Label>(app, "ReportsText").Text;
                Require(text.Contains("载运率 " + expected), "经营报告未区分未定义、空载和超载的载运率。");
                Require(ratio is null || !text.Contains("未进入线路"), "有数值的载运率被显示为未定义。");
                Require(text.Contains("服务费用：17 · 净现金流 -17"), "运行报告未展示核心返回的服务费用。");
            }
            CoreProblem shortage = new("insufficient_goods", "Quantity", new WorldObjectId.Inventory(new(10)), []);
            var incomplete = new RunAccumulatorView(new(0, 0), new(0), new(0), new(0), 0, 0, null,
            [
                new(0, 0, TradeAction.Load, EconomyFixture.Food, 100, 0, new(0), [shortage]),
                new(0, 1, TradeAction.Unload, EconomyFixture.Food, 100, 0, new(0), [shortage])
            ]);
            economy.Refresh(binding, new FixedCoreReadView(original.Position, original.InputIdentity, revision: new(revision))
            {
                World = original.World,
                Economy = original.Economy with
                {
                    RecentRunReports = [new(new(7), original.Position, RunEndReason.PlanCompleted, incomplete)]
                }
            }, content);
            var reports = Find<Label>(app, "ReportsText").Text;
            Require(reports.Split("补货目标以内").Length == 2,
                $"货物不足的装载规则应说明补货目标保留，卸载规则不应：{reports}");
            Require(setup.Session.SubmitCount == 0, "只读经营报告提交了命令。");
        }
        finally { await fixture.Cleanup(); }
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Production_names_and_missing_identifiers_preserve_frozen_batches_and_short_revisions()
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
            var specifications = Find<PanelContainer>(app, "Settlement").FindChildren("CurrentSpecification", "Label", true, false).Cast<Label>().ToArray();
            var found = specifications.Single(label => label.Text.Contains("固定生产流程"));
            var foundBatch = Find<Label>(found.GetParent(), "Batch").Text;
            Require(found.Text.StartsWith("当前定义：固定生产流程 · 修订 v2\n") &&
                foundBatch.StartsWith("进行中批次：固定生产流程 · 启动修订 v1\n"),
                "生产页未展示名称，或短于八位的修订未原样保留。");
            var blockers = Find<Label>(found.GetParent(), "Blockers").Text;
            Require(blockers.Contains("产出无法进入库存：主食。对象：建筑 1。") && !blockers.Contains("output_blocked"),
                "生产受阻没有显示中文说明：" + blockers);
            var absent = specifications.Single(label => label.Text.Contains("定义标识：fixture:missing"));
            var absentBatch = Find<Label>(absent.GetParent(), "Batch").Text;
            Require(absent.Text.StartsWith("当前定义：流程定义缺失") &&
                absentBatch.StartsWith("进行中批次：流程定义缺失") && absentBatch.Contains("定义标识：fixture:removed"),
                "当前计划或冻结批次未明确显示定义缺失及各自的定义标识。");
            Require(absentBatch.Contains("启动修订 v0\n冻结工作量 17") &&
                Find<VBoxContainer>(absent.GetParent(), "FrozenOutputs").Visible,
                "流程缺失后冻结批次的修订、工作量或产出被隐藏。");
            Require(setup.Session.SubmitCount == 0, "生产只读页面提交了命令。");
        }
        finally { await fixture.Cleanup(); }
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Population_without_statistics_and_global_reports_preserve_unknown_and_ownership()
    {
        var original = EconomyFixture.Create();
        var content = EconomyFixture.Content();
        var needs = Enum.GetValues<PopulationNeed>().Select(kind => new PopulationNeedView(kind, 2, 0, 0, null)).ToImmutableArray();
        var pending = View(original, 2, new PopulationExecution.NotStarted(needs));
        var setup = new HostSimulationTests.HostSetup(pending, content);
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var host = app.Host!;
            var binding = host.GetStatus()!.Binding;
            var economy = Find<EconomyPanels>(app, "EconomyPanels");
            economy.OpenSettlement(new(binding, content.Generation, new WorldObjectId.Settlement(new(1))), new(1));
            economy.Refresh(binding, pending, content);
            var population = Find<Label>(app, "Population");
            Require(population.Text.Contains("尚未开始") && population.Text.Contains("未结算") &&
                !population.Text.Contains("满足率 0"), "尚未开始的人口被显示为零满足率。");
            Require(!population.Text.Contains("累计应需") && !population.Text.Contains("实际满足"),
                "尚未开始的人口显示了不存在的本期累计值。");
            Require(population.Text.Contains("主食") && population.Text.Contains("衣物") &&
                population.Text.Contains("改善饮食") && population.Text.Contains("日用品") &&
                population.Text.Contains("消费电子") && population.Text.Contains("医疗用品"), "人口页遗漏六类需求。");

            var active = View(original, 3, new PopulationExecution.InProgress(new(new("fixture:population"),
                new("executing-v1"), 720, 17, needs, null, null), PopulationTrend.Stable));
            economy.Refresh(binding, active, content);
            Require(population.Text.Contains("17/720") && population.Text.Contains("executing-v1") &&
                population.Text.Contains("未结算") && !population.Text.Contains("满足率 0"),
                "已开始但没有消费统计时，进度或空满足率显示错误。");

            economy.OpenReports();
            economy.Refresh(binding, active, content);
            var report = Find<Label>(app, "ReportsText").Text;
            Require(report.Contains("未归属服务支出：23") && !report.Contains("未接入"),
                "经营报告未展示核心返回的未归属服务支出。");
            Require(report.Contains("101 → 103") && !report.Contains("901 → 907"),
                "经营报告未展示自有聚居地结算，或混入NPC人口结算。");
            economy.Refresh(new HostBinding(Guid.NewGuid()), active, content);
            Require(Find<Label>(Find<PanelContainer>(app, "EconomyReports"), "State").Text.Contains("已失效"),
                "全局经营报告仍接受旧对局绑定。");
            Require(setup.Session.SubmitCount == 0, "只读页面提交了命令。");
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
        var planned = View(basis, 2, new PopulationExecution.NotStarted([]), [Facility(press, true, 4, Batch())], buildingDesign: "fixture-food-building");
        var raised = View(basis, 3, new PopulationExecution.NotStarted([]), [Facility(press, true, 9, Batch())], buildingDesign: "fixture-food-building");
        var stopped = View(basis, 4, new PopulationExecution.NotStarted([]), [Facility(press, false, 9, Batch())], buildingDesign: "fixture-food-building");
        var setup = new HostSimulationTests.HostSetup(empty, content);
        await Run(setup, async (ui, economy) =>
        {
            await OpenProduction(economy);
            var facility = Find<VBoxContainer>(economy, "Facility");
            var choice = Find<OptionButton>(facility, "PlanProcess_1");
            var names = Enumerable.Range(0, choice.ItemCount).Select(choice.GetItemText).ToArray();
            Require(names[0] == "选择流程" && names.Contains("压榨") && names.Contains("回收") &&
                !names.Contains("装配") && !names.Contains("开采"), "加工设施没有按能力和标签筛选流程。");
            Require(Find<Label>(facility, "CurrentSpecification").Text.Contains("未选择流程") && setup.Session.SubmitCount == 0,
                "空计划被写成已有流程或打开页面就提交了。");
            await _input.Choose(choice, Array.IndexOf(names, "压榨"));
            await _input.Enter(Find<LineEdit>(facility, "PlanPriority_1"), "4");
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 4),
                CommandSource.UserInterface, Completed(1, empty, planned, new CommandDecision.Accepted([], [])), planned);
            await _input.Activate(Find<Button>(facility, "ApplyPlan_1"));
            Require(Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨") &&
                Find<Label>(facility, "Batch").Text.Contains("回收") && Find<Label>(facility, "State").Text.Contains("优先级 4"),
                "受理后没有重新查询计划，或批次流程被写成计划流程。");
            await _input.Enter(Find<LineEdit>(facility, "PlanPriority_1"), "9");
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 9),
                CommandSource.UserInterface, Completed(2, planned, raised, new CommandDecision.Accepted([], [])), raised);
            await _input.Activate(Find<Button>(facility, "ApplyPlan_1"));
            Require(Find<Label>(facility, "State").Text.Contains("优先级 9") && Find<Label>(facility, "Batch").Text.Contains("回收"),
                "只改优先级后没有重新查询，或批次被替换。");
            setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, false, 9),
                CommandSource.UserInterface, Completed(3, raised, stopped, new CommandDecision.Accepted([], [])), stopped);
            await _input.Activate(Find<Button>(facility, "StopPlan_1"));
            Require(setup.Session.SubmitCount == 3 && Find<Label>(facility, "State").Text.Contains("计划停用") &&
                Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨") &&
                Find<Button>(facility, "StopPlan_1").Disabled, "停用没有以当前流程关闭计划，或已停用的计划仍可再停。");
            await ShowPlanControls(economy);
            HideCaptureChrome(ui);
            await UiCapture.Capture(TestScene, "production-plan");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Production_plan_keeps_pending_process_and_priority_across_revision()
    {
        var press = new ProcessRef.Production(new("fixture:press"));
        var content = EconomyFixture.Content(
            processes: [new(new ProcessDetails.Production(press.Definition, [], [new(EconomyFixture.Food, 2)]), new("v2"), 10, 1, 1, FacilityTag.FoodProcessing, [])],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(press)] = new ContentLookup<ContentDisplay>.Found(new(new("压榨", ""), null, null, false))
            });
        var basis = EconomyFixture.Create();
        var empty = View(basis, 1, new PopulationExecution.NotStarted([]), [Facility(null, false, 0, null)], buildingDesign: "fixture-food-building");
        var stepped = View(basis, 2, new PopulationExecution.NotStarted([]), [Facility(null, false, 0, null)], buildingDesign: "fixture-food-building");
        var planned = View(basis, 3, new PopulationExecution.NotStarted([]), [Facility(press, true, 4, null)], buildingDesign: "fixture-food-building");
        var setup = new HostSimulationTests.HostSetup(empty, content);
        await Run(setup, async (ui, economy) =>
        {
            await OpenProduction(economy);
            var facility = Find<VBoxContainer>(economy, "Facility");
            var choice = Find<OptionButton>(facility, "PlanProcess_1");
            await _input.Choose(choice, Enumerable.Range(0, choice.ItemCount).Single(index => choice.GetItemText(index) == "压榨"));
            await _input.Enter(Find<LineEdit>(facility, "PlanPriority_1"), "4");
            var host = ui.GetParent<Train.App.App>().Host!;
            await _input.ClickAcrossRevision(Find<Button>(facility, "ApplyPlan_1"), () =>
            {
                setup.Session.QueueAdvance(new(1, stepped.Position, [], null, null), stepped);
                Require(host.Step(1).Problem is null, "无法发布设施未变的新修订。");
                setup.Session.QueueSubmit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production), press, true, 4),
                    CommandSource.UserInterface, Completed(1, stepped, planned, new CommandDecision.Accepted([], [])), planned);
            });
            Require(setup.Session.SubmitCount == 1 && Find<Label>(facility, "State").Text.Contains("优先级 4") &&
                Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨"),
                "按下应用期间的新修订改写了优先级，或没有提交所选流程。");
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
            economy.GetNode<ScrollContainer>("Layout/Content/Body/Pages/生产/Page"),
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
        var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "生产");
        await _input.ChooseTab(tabs, index);
        await _input.Frames();
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

    private static FixedCoreReadView View(FixedCoreReadView source, ulong revision, PopulationExecution execution,
        ImmutableArray<FacilityView>? facilities = null, SettlementRelation relation = SettlementRelation.PlayerOwned,
        string buildingDesign = "fixture-plain-building")
    {
        var settlement = ((Lookup<SettlementView>.Found)source.FindSettlement(new(1))).Value;
        PopulationSettlementReport Report(long before, long after) => new(new(12, 0), new("settled-v1"),
            before, after, after - before, .8, .6, PopulationSettlementReason.Growth);
        var owned = settlement with
        {
            Relation = relation,
            Facilities = facilities ?? settlement.Facilities,
            Population = settlement.Population with
            {
                CurrentRule = new(new("fixture:population"), new("published-v2"), []),
                Execution = execution, LastSettlement = Report(101, 103), RecentSettlements = [Report(101, 103)]
            }
        };
        var npc = settlement with { Id = new(2), Node = new(2), Population = settlement.Population with
            { LastSettlement = Report(901, 907), RecentSettlements = [Report(901, 907)] } };
        var node = source.World.Nodes[0];
        return new(new(source.Position.CompletedSteps, revision), source.InputIdentity, source.Fault, new(revision))
        {
            World = source.World with { Nodes = [node with { Occupant = new NodeOccupant.Settlement(owned.Id, owned.Relation) },
                node with { Id = new(2), Occupant = new NodeOccupant.Settlement(npc.Id, npc.Relation) }] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [new(1)] = source.FindNode(new(1)), [new(2)] = source.FindNode(new(2))
            },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [new WorldLocation.Node(new(1))] = source.FindLocation(new WorldLocation.Node(new(1))),
                [new WorldLocation.Node(new(2))] = source.FindLocation(new WorldLocation.Node(new(2)))
            },
            Economy = source.Economy with { UnassignedServicePayments = new(23) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                { [owned.Id] = new Lookup<SettlementView>.Found(owned), [npc.Id] = new Lookup<SettlementView>.Found(npc) },
            Inventories = new ulong[] { 10, 11, 12, 13 }.ToDictionary(id => new InventoryId(id), id => source.FindInventory(new(id))),
            Leases = new Dictionary<LeaseId, Lookup<LeaseView>> { [new(1)] = source.FindLease(new(1)) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = source.FindTrain(new(7)) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            {
                [new InstanceId.Vehicle(new(9))] = source.FindInstance(new InstanceId.Vehicle(new(9))),
                [new InstanceId.Vehicle(new(10))] = source.FindInstance(new InstanceId.Vehicle(new(10))),
                [new InstanceId.Building(new(1))] = BuildingInstance(1, buildingDesign),
                [new InstanceId.Building(new(2))] = BuildingInstance(2, buildingDesign)
            }
        };
    }

    private static Lookup<InstanceView> BuildingInstance(ulong id, string design) => new Lookup<InstanceView>.Found(new(
        new InstanceId.Building(new(id)), new(design), new("fixture-plain-composition"),
        new WorldLocation.Node(new(1)), SettlementRelation.PlayerOwned,
        new InstanceDetails.Building(new(id), PlotSize.Small), InstanceLock.None, new(100, 100), [], []));
}

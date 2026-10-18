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
            var economy = app.GetNode<EconomyPanels>("GameUi/Root/World/EconomyPanels");
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
                var text = economy.GetNode<Label>("EconomyReports/Layout/Content/Body/ReportsScroll/Reports/ReportsText").Text;
                Require(text.Contains("载运率 " + expected), "经营报告未区分未定义、空载和超载的载运率。");
                Require(ratio is null || !text.Contains("未进入线路"), "有数值的载运率被显示为未定义。");
                Require(text.Contains("服务费用：17 · 净现金流 -17"), "运行报告未展示核心返回的服务费用。");
            }
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
                new(process, new("v1"), 10, 3, .3, [new(EconomyFixture.Food, 2)], true), [], [], []),
            new(new(new(2), CapabilityKind.Production), missing, true, 0, ProductionState.ProcessMissing, 1, 0, [],
                new(removed, new("v0"), 17, 4, 4.0 / 17, [new(EconomyFixture.Food, 4)], true), [], [], [])
        ];
        var view = View(original, 2, new PopulationExecution.NotStarted([]), facilities);
        var setup = new HostSimulationTests.HostSetup(original, content);
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var binding = app.Host!.GetStatus()!.Binding;
            var economy = app.GetNode<EconomyPanels>("GameUi/Root/World/EconomyPanels");
            economy.OpenSettlement(new(binding, content.Generation, new WorldObjectId.Settlement(new(1))), new(1));
            economy.Refresh(binding, view, content);
            var specifications = economy.FindChildren("CurrentSpecification", "Label", true, false).Cast<Label>().ToArray();
            var found = specifications.Single(label => label.Text.Contains("固定生产流程"));
            var foundBatch = Find<Label>(found.GetParent(), "Batch").Text;
            Require(found.Text.StartsWith("当前定义：固定生产流程 · 修订 v2\n") &&
                foundBatch.StartsWith("进行中批次：固定生产流程 · 启动修订 v1\n"),
                "生产页未展示名称，或短于八位的修订未原样保留。");
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
        var setup = new HostSimulationTests.HostSetup(original, content);
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var host = app.Host!;
            var binding = host.GetStatus()!.Binding;
            var economy = app.GetNode<EconomyPanels>("GameUi/Root/World/EconomyPanels");
            economy.OpenSettlement(new(binding, content.Generation, new WorldObjectId.Settlement(new(1))), new(1));
            var needs = Enum.GetValues<PopulationNeed>().Select(kind => new PopulationNeedView(kind, 2, 0, 0, null)).ToImmutableArray();
            var pending = View(original, 2, new PopulationExecution.NotStarted(needs));
            economy.Refresh(binding, pending, content);
            var population = economy.GetNode<Label>("Settlement/Layout/Content/Body/Pages/人口/Body/Population");
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
            var report = economy.GetNode<Label>("EconomyReports/Layout/Content/Body/ReportsScroll/Reports/ReportsText").Text;
            Require(report.Contains("未归属服务支出：23") && !report.Contains("未接入"),
                "经营报告未展示核心返回的未归属服务支出。");
            Require(report.Contains("101 → 103") && !report.Contains("901 → 907"),
                "经营报告未展示自有聚居地结算，或混入NPC人口结算。");
            economy.Refresh(new HostBinding(Guid.NewGuid()), active, content);
            Require(economy.GetNode<Label>("EconomyReports/Layout/Content/Body/State").Text.Contains("已失效"),
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
        var empty = new PlanView(1, SettlementRelation.PlayerOwned, [Facility(null, false, 0, null)], new("fixture-food-building"));
        var planned = new PlanView(2, SettlementRelation.PlayerOwned, [Facility(press, true, 4, Batch())], new("fixture-food-building"));
        var raised = new PlanView(3, SettlementRelation.PlayerOwned, [Facility(press, true, 9, Batch())], new("fixture-food-building"));
        var stopped = new PlanView(4, SettlementRelation.PlayerOwned, [Facility(press, false, 9, Batch())], new("fixture-food-building"));
        var setup = new HostSimulationTests.HostSetup(empty, content);
        await Run(setup, async (_, economy) =>
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
                Find<Label>(facility, "CurrentSpecification").Text.Contains("压榨"), "停用没有以当前流程关闭计划。");
            await UiCapture.Capture(TestScene, "production-plan");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Unowned_production_plan_controls_do_not_submit()
    {
        var press = new ProcessRef.Production(new("fixture:press"));
        var view = new PlanView(1, SettlementRelation.NonHostile, [Facility(press, true, 2, null)], new("fixture-food-building"));
        var content = EconomyFixture.Content(
            processes: [new(new ProcessDetails.Production(press.Definition, [], []), new("v2"), 10, 1, 1, FacilityTag.FoodProcessing, [])],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(press)] = new ContentLookup<ContentDisplay>.Found(new(new("压榨", ""), null, null, false))
            });
        var setup = new HostSimulationTests.HostSetup(view, content);
        await Run(setup, async (_, economy) =>
        {
            await OpenProduction(economy);
            var facility = Find<VBoxContainer>(economy, "Facility");
            Require(Find<OptionButton>(facility, "PlanProcess_1").Disabled && !Find<LineEdit>(facility, "PlanPriority_1").Editable &&
                Find<Button>(facility, "ApplyPlan_1").Disabled && Find<Button>(facility, "StopPlan_1").Disabled &&
                setup.Session.SubmitCount == 0, "非自有聚居地可以修改生产计划。");
        });
        setup.Session.AssertComplete();
    }

    private static FacilityView Facility(ProcessRef? process, bool enabled, int priority, ActiveBatchView? batch) =>
        new(new(new(1), CapabilityKind.Production), process, enabled, priority,
            enabled ? ProductionState.Running : ProductionState.Disabled, 1, 0, [], batch, [], [], []);

    private async Task OpenProduction(EconomyPanels economy)
    {
        var tabs = Find<TabContainer>(economy, "Pages");
        var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "生产");
        var bar = tabs.GetTabBar();
        await _input.Frames(2);
        await _input.Click(bar.GlobalPosition + bar.GetTabRect(index).GetCenter());
        Require(tabs.CurrentTab == index, "真实点击未打开生产。");
        await _input.Frames();
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, EconomyPanels, Task> body)
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
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Content/Body/Objects"), 0);
            await _input.Activate(Find<Button>(ui.GetNode<WorldPanels>("Root/World/WorldPanels"), "Economy"));
            await body(ui, ui.GetNode<EconomyPanels>("Root/World/EconomyPanels"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private static FixedCoreReadView View(FixedCoreReadView source, ulong revision, PopulationExecution execution,
        ImmutableArray<FacilityView>? facilities = null)
    {
        var settlement = ((Lookup<SettlementView>.Found)source.FindSettlement(new(1))).Value;
        PopulationSettlementReport Report(long before, long after) => new(new(12, 0), new("settled-v1"),
            before, after, after - before, .8, .6, PopulationSettlementReason.Growth);
        var owned = settlement with
        {
            Relation = SettlementRelation.PlayerOwned,
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
        return new(source.Position, source.InputIdentity, source.Fault, new(revision))
        {
            World = source.World with { Nodes = [node with { Occupant = new NodeOccupant.Settlement(owned.Id, owned.Relation) },
                node with { Id = new(2), Occupant = new NodeOccupant.Settlement(npc.Id, npc.Relation) }] },
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
                [new InstanceId.Building(new(1))] = BuildingInstance(1),
                [new InstanceId.Building(new(2))] = BuildingInstance(2)
            }
        };
    }

    private static Lookup<InstanceView> BuildingInstance(ulong id) => new Lookup<InstanceView>.Found(new(
        new InstanceId.Building(new(id)), new("fixture-plain-building"), new("fixture-plain-composition"),
        new WorldLocation.Node(new(1)), SettlementRelation.PlayerOwned,
        new InstanceDetails.Building(new(id), PlotSize.Small), InstanceLock.None, new(100, 100), [], []));

    private sealed class PlanView(ulong revision, SettlementRelation relation, ImmutableArray<FacilityView> facilities, DesignHash design) : ICoreReadView
    {
        private readonly ICoreReadView basis = EconomyFixture.Create(revision, vehicle: EconomyFixture.VehicleState.Owned);
        public SimulationPosition Position => basis.Position;
        public CoreRevision Revision => basis.Revision;
        public CoreInputId InputIdentity => basis.InputIdentity;
        public CoreFault? Fault => basis.Fault;
        public WorldSummary World => basis.World;
        public EconomyView Economy => basis.Economy;
        public Lookup<SettlementView> FindSettlement(SettlementId id)
        {
            if (id != new SettlementId(1) || basis.FindSettlement(id) is not Lookup<SettlementView>.Found found) return basis.FindSettlement(id);
            return new Lookup<SettlementView>.Found(found.Value with { Relation = relation, Facilities = facilities });
        }
        public Lookup<NodeView> FindNode(NodeId id) => basis.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => basis.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => basis.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => basis.FindTrain(id);
        public Lookup<InstanceView> FindInstance(InstanceId id) => id is InstanceId.Building building
            ? new Lookup<InstanceView>.Found(new(id, design, new("fixture-food-composition"), new WorldLocation.Node(new(1)),
                SettlementRelation.PlayerOwned, new InstanceDetails.Building(new(building.Id.Value), PlotSize.Small),
                InstanceLock.None, new(100, 100), [], []))
            : basis.FindInstance(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => basis.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => basis.FindLease(id);
        public Lookup<JobView> FindJob(JobId id) => basis.FindJob(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => basis.FindFacility(key);
        public Lookup<CombatView> FindCombat(CombatId id) => basis.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => basis.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => basis.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => basis.FindLair(id);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => basis.GetJobQuote(request, funding);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => basis.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => basis.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => basis.QuoteLease(request);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => basis.PreviewSettlement(node);
    }
}

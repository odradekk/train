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
            var population = economy.GetNode<Label>("Settlement/Layout/Body/Pages/人口/Body/Population");
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
            var report = economy.GetNode<Label>("EconomyReports/Layout/Body/ReportsScroll/Reports/ReportsText").Text;
            Require(report.Contains("服务费用：未接入") && report.Contains("未归属服务支出：未接入"),
                "服务费用被伪装成已接入金额。");
            Require(report.Contains("101 → 103") && !report.Contains("901 → 907"),
                "经营报告未展示自有聚居地结算，或混入NPC人口结算。");
            economy.Refresh(new HostBinding(Guid.NewGuid()), active, content);
            Require(economy.GetNode<Label>("EconomyReports/Layout/Body/State").Text.Contains("已失效"),
                "全局经营报告仍接受旧对局绑定。");
            Require(setup.Session.SubmitCount == 0, "只读页面提交了命令。");
        }
        finally { await fixture.Cleanup(); }
        setup.Session.AssertComplete();
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
            Economy = source.Economy,
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                { [owned.Id] = new Lookup<SettlementView>.Found(owned), [npc.Id] = new Lookup<SettlementView>.Found(npc) },
            Inventories = new ulong[] { 10, 11, 12, 13 }.ToDictionary(id => new InventoryId(id), id => source.FindInventory(new(id))),
            Leases = new Dictionary<LeaseId, Lookup<LeaseView>> { [new(1)] = source.FindLease(new(1)) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = source.FindTrain(new(7)) },
            Instances = new ulong[] { 9, 10 }.ToDictionary(id => (InstanceId)new InstanceId.Vehicle(new(id)),
                id => source.FindInstance(new InstanceId.Vehicle(new(id))))
        };
    }
}

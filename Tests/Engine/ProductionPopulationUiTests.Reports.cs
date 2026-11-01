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

public partial class ProductionPopulationUiTests
{
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
            var population = Find<Label>(app, "PopulationSummary");
            Require(population.Text.Contains("暂无统计") &&
                !population.Text.Contains("满足率 0"), "尚未开始的人口被显示为零满足率。");
            Require(!population.Text.Contains("累计应需") && !population.Text.Contains("实际满足"),
                "尚未开始的人口显示了不存在的本期累计值。");

            var active = View(original, 3, new PopulationExecution.InProgress(new(new("fixture:population"),
                new("executing-v1"), 720, 17, needs, null, null), PopulationTrend.Stable));
            economy.Refresh(binding, active, content);
            Require(population.Text.Contains("暂无统计") && !population.Text.Contains("executing-v1") &&
                !population.Text.Contains("满足率 0"), "没有消费统计时，不能虚构满足率或展示内部规则修订。");

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
            Plots = [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(1))),
                new(new(2), 2, PlotSize.Small, [], new PlotOccupant.Building(new(2)))],
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

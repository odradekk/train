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

public partial class OwnedSettlementInspectorUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Overview_shows_population_labor_sites_and_defense_without_internal_identifiers()
    {
        var view = View();
        var setup = new HostSimulationTests.HostSetup(view, Content());
        await Run(setup, async (_, panel) =>
        {
            var tabs = Find<TabContainer>(panel, "Pages");
            Require(Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle)
                .SequenceEqual(new[] { "概况", "库存", "建筑", "现车", "建造与工地" }), "自有聚居地应显示三个新页和两个保留页。");
            Require(Find<Label>(panel, "Title").Text == "青川", "标题应为聚居地显示名。");
            var population = Find<Label>(panel, "PopulationSummary").Text;
            Require(population.Contains("常住人口 120") && population.Contains("80.0%") && population.Contains("暂无统计"),
                "概况应区分已统计的基本满足率和未知改善满足率。");
            var labor = Find<Label>(panel, "LaborSummary").Text;
            Require(labor.Contains("可用 60") && labor.Contains("占用 40") && labor.Contains("闲置 20"), "概况应展示核心提供的劳动力。");
            Require(Find<Label>(panel, "OwnedSites").Text.Contains("50%") && Find<Label>(panel, "OwnedSites").Text.Contains("10 秒"),
                "工地应显示实际进度和按当前工作速率换算的剩余秒数。");
            Require(Find<Label>(panel, "DefenseSummary").Text.Contains("没有防御建筑"), "无武器建筑时应说明缺少防御。");
            var text = string.Join("\n", Enumerable.Range(0, 3).SelectMany(index =>
                tabs.GetChild(index).FindChildren("*", "", true, false).Select(node => node switch
                { Label label => label.Text, Button button => button.Text, _ => "" })));
            Require(new[] { "fixture", "Building", "建筑 1", "任务 90", "private-revision", "private-hash" }.All(value => !text.Contains(value)),
                "概况、库存和建筑页的标签与按钮不能显示内部编号、定义标识、修订或枚举。");
            await UiCapture.Capture(TestScene, "owned-overview");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Attention_opens_the_matching_good_or_blocked_building_and_ignores_disabled_facilities()
    {
        var setup = new HostSimulationTests.HostSetup(View(), Content());
        await Run(setup, async (_, panel) =>
        {
            var attention = Find<VBoxContainer>(panel, "Attention");
            var buildings = attention.FindChildren("BlockedBuilding*", "Button", true, false).Cast<Button>().ToArray();
            Require(buildings.Length == 1 && buildings[0].Text.Contains("食品建筑") &&
                !attention.FindChildren("BlockedBuilding_1", "Button", true, false).Any() &&
                !attention.FindChildren("BlockedBuilding_4", "Button", true, false).Any(),
                "只有启用但停工的后排建筑应进入需要处理，生产中和已停用的建筑不应出现。");
            await _input.Activate(buildings[0]);
            var tabs = Find<TabContainer>(panel, "Pages");
            var building = Find<VBoxContainer>(panel, "Building_2");
            Require(tabs.GetTabTitle(tabs.CurrentTab) == "建筑" && Find<VBoxContainer>(building, "Details").Visible &&
                !Find<VBoxContainer>(Find<VBoxContainer>(panel, "Building_1"), "Details").Visible,
                "点击停工事项应打开并展开对应建筑。");
            var reason = Find<Label>(building, "Blockers").Text;
            Require(Find<Label>(building, "State").Text.Contains("等待原料") && reason.Contains("原料") && reason.Contains("补充") &&
                !reason.Contains("inputs_missing") && !reason.Contains("Facility"), "停工建筑应显示中文状态、原因与补充原料的处理办法。");
            await _input.ClickTab(tabs, 0);
            var shortage = attention.GetChildren().OfType<Button>().Single(button => button.Text.Contains("装备"));
            Require(shortage.Text.Contains("5"), "补货事项应显示装备的目标。");
            await _input.Activate(shortage);
            await _input.Frames(2);
            Require(tabs.GetTabTitle(tabs.CurrentTab) == "库存" && Find<VBoxContainer>(panel, "Trade").Visible &&
                Find<Label>(panel, "TrainCargo").Text.Contains("装备"), "点击装备缺货事项应展开装备的装卸。");
            var expandedRow = Find<VBoxContainer>(panel, "Trade").GetParent();
            Require(expandedRow.GetIndex() > 0 && expandedRow.GetChild<Button>(0).FindChildren("*", "Label", true, false)
                .Cast<Label>().Any(label => label.Text == "装备"), "展开的必须是后排装备行，而不是第一项主食。");
            Require(Find<VBoxContainer>(panel, "Trade").GetParent().GetChild<Button>(0).HasFocus(),
                "从需要处理进入库存后，键盘焦点应跟随对应货物行。");
            Require(setup.Session.SubmitCount == 0, "导航不应提交命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Hostile_settlement_keeps_only_the_remaining_legacy_pages_and_combat_entry()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(relation: SettlementRelation.Hostile), EconomyFixture.Content());
        await Run(setup, (_, panel) =>
        {
            var tabs = Find<TabContainer>(panel, "Pages");
            Require(Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle)
                .SequenceEqual(new[] { "现车", "仓库租用", "建造与工地" }) && Find<Button>(panel, "OpenSettlementCombat").Visible,
                "敌对聚居地应保留战斗入口与剩余旧页，不提前提供概况。");
            return Task.CompletedTask;
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Defense_counts_damaged_weapon_parts_and_updates_when_they_are_destroyed()
    {
        var before = View(); var after = View(2, defenseDestroyed: true);
        var setup = new HostSimulationTests.HostSetup(before, Content(armed: true));
        setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
        await Run(setup, (ui, panel) =>
        {
            var defense = Find<Label>(panel, "DefenseSummary");
            Require(defense.Text.Contains("防御建筑 1 座") && defense.Text.Contains("可用武器 1 件"),
                "部分受损但尚未毁坏的武器仍应计入防御。");
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试应发布武器已毁坏的修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, after, host.Content!);
            Require(defense.Text.Contains("可用武器 0 件"), "毁坏的武器不能仍计入可用防御。");
            return Task.CompletedTask;
        });
        setup.Session.AssertComplete();
    }

    internal static Train.Content.ContractSupport.FixedContentView Content(bool armed = false)
    {
        var process = new ProcessRef.Production(new("fixture:press"));
        return EconomyFixture.Content(mapPresentation: true, armedBuilding: armed, equipmentName: "装备",
            processes: [new(new ProcessDetails.Production(process.Definition, [new(EconomyFixture.Food, 8)], [new(EconomyFixture.Food, 3)]),
                new("private-revision"), 10, 1, 1, FacilityTag.FoodProcessing, [])],
            definitionDisplays: new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Process(process)] = new ContentLookup<ContentDisplay>.Found(new(new("食品加工", ""), null, null, false)),
                [new CoreDefinitionReference.SettlementType(new("fixture:town"))] = new ContentLookup<ContentDisplay>.Found(new(new("聚居地", ""), null, new("fixture-food-png"), false))
            });
    }

    internal static FixedCoreReadView View(ulong revision = 1, bool defenseDestroyed = false, bool includeSite = true,
        DesignHash? siteDesign = null, bool includeVehicleJob = false, ProductionState stoppedState = ProductionState.WaitingForInputs)
    {
        var basis = EconomyFixture.Create(revision, relation: SettlementRelation.PlayerOwned, leaseCount: 0);
        var settlement = ((Lookup<SettlementView>.Found)basis.FindSettlement(new(1))).Value;
        var process = new ProcessRef.Production(new("fixture:press"));
        var sites = new JobView.Construction(new(new(90), ConstructionKind.Construction, settlement.Id,
            new(new(3), CapabilityKind.Construction), new ConstructionTarget.NewBuilding(new(4), siteDesign ?? new("fixture-food-building")), ConstructionState.Working,
            0, 100, 50, .5, 5, [], [], []));
        var owned = settlement with
        {
            Plots = [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(1))),
                new(new(2), 2, PlotSize.Small, [], new PlotOccupant.Building(new(2))),
                new(new(3), 3, PlotSize.Small, [], new PlotOccupant.Building(new(3))),
                new(new(4), 4, PlotSize.Small, [], includeSite ? new PlotOccupant.Construction(new(90)) : new PlotOccupant.Empty()),
                new(new(5), 5, PlotSize.Small, [], new PlotOccupant.Building(new(4)))],
            Facilities = [new(new(new(1), CapabilityKind.Production), process, true, 0, ProductionState.Running, 1, 1, [], null, [], [], []),
                new(new(new(2), CapabilityKind.Production), process, true, 7, stoppedState, 1, 0, [], null, [], [],
                    [new(stoppedState == ProductionState.Locked ? "instance_locked" : "inputs_missing", "Facility", null, [])]),
                new(new(new(4), CapabilityKind.Production), null, false, 0, ProductionState.Disabled, defenseDestroyed ? 0 : 1, 0, [], null, [], [], [])],
            Population = settlement.Population with { Residents = 120, AvailableLabor = 60, AllocatedLabor = 40, IdleLabor = 20,
                Execution = new PopulationExecution.InProgress(new(new("fixture:population"), new("private-revision"),
                    720, 10, [], .8, null), PopulationTrend.Stable) },
            OwnedJobs = [.. includeSite ? new JobId[] { new(90) } : [], .. includeVehicleJob ? new JobId[] { new(91) } : []]
        };
        Lookup<InstanceView> Building(ulong id, string design) => new Lookup<InstanceView>.Found(new(
            new InstanceId.Building(new(id)), new(design), new("private-hash"), new WorldLocation.Node(new(1)),
            SettlementRelation.PlayerOwned, new InstanceDetails.Building(new(id), PlotSize.Small), InstanceLock.None, new(100, 100),
            [new(new(1), new(id == 4 && defenseDestroyed ? 0 : 50, 100), false, id == 4 && defenseDestroyed)], []));
        return new(basis.Position, basis.InputIdentity, basis.Fault, basis.Revision)
        {
            World = basis.World, Economy = basis.Economy,
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [new(1)] = basis.FindNode(new(1)), [new(2)] = basis.FindNode(new(2)) },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>> { [new WorldLocation.Node(new(1))] = basis.FindLocation(new WorldLocation.Node(new(1))),
                [new WorldLocation.Node(new(2))] = basis.FindLocation(new WorldLocation.Node(new(2))) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = basis.FindTrain(new(7)) },
            Inventories = new ulong[] { 10, 11, 12, 13 }.ToDictionary(id => new InventoryId(id), id => basis.FindInventory(new(id))),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = new Lookup<SettlementView>.Found(owned) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            {
                [new InstanceId.Vehicle(new(9))] = basis.FindInstance(new InstanceId.Vehicle(new(9))),
                [new InstanceId.Vehicle(new(10))] = basis.FindInstance(new InstanceId.Vehicle(new(10))),
                [new InstanceId.Building(new(1))] = Building(1, "fixture-food-building"),
                [new InstanceId.Building(new(2))] = Building(2, "fixture-food-building"),
                [new InstanceId.Building(new(3))] = Building(3, "fixture-service-building"),
                [new InstanceId.Building(new(4))] = Building(4, "fixture-plain-building")
            },
            Jobs = new Dictionary<JobId, Lookup<JobView>> { [new(90)] = new Lookup<JobView>.Found(sites),
                [new(91)] = new Lookup<JobView>.Found(new JobView.Construction(new(new(91), ConstructionKind.Construction, settlement.Id,
                    new(new(3), CapabilityKind.Construction), new ConstructionTarget.NewVehicle(new("fixture-vehicle")), ConstructionState.Queued,
                    0, 200, 0, 0, 0, [], [], []))) },
            TradeQuotes = (from quantity in new[] { 1L, 10L } from good in new[] { EconomyFixture.Food, EconomyFixture.Equipment }
                select new TradeQuoteRequest(new(7), new(1), new(10), TradeAction.Load, good, quantity)).ToDictionary(request => request,
                request => (CoreResult<TradeQuote>)new CoreResult<TradeQuote>.Success(new(request, new(revision), request.Quantity, null, [], [])))
        };
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
            var host = app.Host!;
            var economy = Find<EconomyPanels>(app, "EconomyPanels");
            economy.OpenSettlement(new(host.GetStatus()!.Binding, host.Content!.Generation, new WorldObjectId.Settlement(new(1))), new(1));
            await _input.Frames(2);
            await body(app.GetNode<GameUi>("GameUi"), Find<PanelContainer>(app, "Settlement"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }
}

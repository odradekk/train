using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.ContractSupport;
using Train.Ui;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Neutral_settlement_starts_with_overview_and_keeps_only_its_five_pages()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
        await Run(setup, async (_, panel) =>
        {
            var tabs = Find<TabContainer>(panel, "Pages");
            var titles = Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle).ToArray();
            Require(titles.SequenceEqual(new[] { "概况", "市场", "现车", "仓库", "建造与工地" }),
                "非敌对聚居地应只显示概况、市场和暂时保留的三个页面。");
            Require(tabs.CurrentTab == 0, "选中聚居地后应先显示概况。");
            Require(!panel.FindChildren("OpenSettlementCombat", "Button", true, false).Any(), "概况已提供宣战，不应重复显示战斗入口。");
            Require(!panel.FindChildren("LocalInventory", "OptionButton", true, false).Any(), "非敌对聚居地顶部不应显示本地库存选择。");
            Require(Find<Label>(panel, "Title").Text == "青川", "检查器标题应使用内容提供的聚居地名称。");
            await UiCapture.Capture(TestScene, "settlement-overview");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Overview_summarizes_station_leases_and_market_without_showing_storage_identities()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            Require(Find<Label>(panel, "PresentTrains").Text.Contains("列车 7"), "概况应显示本站列车并沿用车队列表名称。");
            Require(Find<Label>(panel, "Commissions").Text.Contains("没有进行中的委托"), "无委托时应显示空状态。");
            var lease = Find<Label>(panel, "LeaseSummary").Text;
            Require(lease.Contains("5/20") && lease.Contains("第2天 00:00") && !lease.Contains("库存 11"),
                "仓库摘要应显示用量和游戏到期时间，不显示库存身份。");
            var market = Find<Label>(panel, "MarketTrends").Text;
            Require(market.Contains("主食") && market.Contains("40") && market.Contains("20") && !market.Contains("fixture:"),
                "市场动向应直接说明核心供需，不显示缺失内容身份。");
            await _input.Activate(Find<Button>(panel, "OpenMarket"));
            var tabs = Find<TabContainer>(panel, "Pages");
            Require(tabs.GetTabTitle(tabs.CurrentTab) == "市场", "市场动向的入口应直接切到市场页。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Overview_commissions_show_rounded_remaining_seconds_or_no_current_work()
    {
        var working = new ConstructionJobView(new(90), ConstructionKind.Construction, new(1),
            new(new(1), CapabilityKind.Construction), new ConstructionTarget.NewVehicle(new("fixture-vehicle")),
            ConstructionState.Working, 0, 100, 51, .51, 6, [], [], []);
        var blocked = working with { Id = new(91), Kind = ConstructionKind.Repair,
            Target = new ConstructionTarget.Existing(new InstanceId.Vehicle(new(10)), new("fixture-vehicle")),
            State = ConstructionState.ResourceBlocked, ProgressWork = 20, Progress = .2, ActualWorkPerSecond = 0 };
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(commissionJobs: [working, blocked]), EconomyFixture.Content());
        await Run(setup, (_, panel) =>
        {
            Require(Find<Label>(panel, "Commissions").Text ==
                "进行中的委托\n正在制造设计内容缺失 · 进度 51% · 预计还需 9 秒\n正在修理列车 7 的第 1 辆车 · 进度 20% · 当前未施工 · 资源受阻",
                "委托应写明对象、进度和向上取整的剩余秒数，资源受阻时说明当前未施工及状态。");
            return Task.CompletedTask;
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Overview_commissions_name_vehicle_and_refit_targets()
    {
        var vehicle = new ConstructionJobView(new(90), ConstructionKind.Construction, new(1),
            new(new(1), CapabilityKind.Construction), new ConstructionTarget.NewVehicle(new("fixture-vehicle")),
            ConstructionState.Working, 0, 100, 50, .5, 5, [], [], []);
        var trainRefit = vehicle with { Id = new(91), Kind = ConstructionKind.Refit,
            Target = new ConstructionTarget.Existing(new InstanceId.Vehicle(new(10)), new("fixture-refit")) };
        var parked = vehicle with { Id = new(92), Kind = ConstructionKind.Refit,
            Target = new ConstructionTarget.Existing(new InstanceId.Vehicle(new(9)), new("fixture-refit")) };
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned,
            commissionJobs: [vehicle, trainRefit, parked]), CommissionContent());
        await Run(setup, async (_, panel) =>
        {
            var text = Find<Label>(panel, "Commissions").Text;
            Require(text.Contains("正在制造货车") && text.Contains("正在改装列车 7 的第 1 辆车") &&
                text.Contains("正在改装停放车辆（货车）") && !text.Contains("改装后货车"),
                "制造应写设计名，列车内改装应写车辆位置，停放车辆改装应写当前设计名以便辨认，而不是改装后的设计名。");
            Require(new[] { "fixture", "private", "NewVehicle", "NewAssembly", "Existing", "任务 90", "车辆 9" }
                .All(value => !text.Contains(value)), "委托摘要不能暴露内部身份或枚举。");
            await UiCapture.Capture(TestScene, "settlement-commission-targets");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public void Overview_refit_identifies_the_second_vehicle_by_train_order()
    {
        var job = new ConstructionJobView(new(90), ConstructionKind.Refit, new(1),
            new(new(1), CapabilityKind.Construction), new ConstructionTarget.Existing(new InstanceId.Vehicle(new(10)), new("fixture-vehicle")),
            ConstructionState.Working, 0, 100, 50, .5, 5, [], [], []);
        var basis = EconomyFixture.Create(commissionJobs: [job]);
        var train = ((Lookup<TrainView>.Found)basis.FindTrain(new(7))).Value with { Vehicles = [new(12), new(10)] };
        var view = new FixedCoreReadView(basis.Position, basis.InputIdentity, revision: basis.Revision)
        {
            World = basis.World,
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [new InstanceId.Vehicle(new(10))] = basis.FindInstance(new InstanceId.Vehicle(new(10))) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
            Jobs = new Dictionary<JobId, Lookup<JobView>> { [job.Id] = new Lookup<JobView>.Found(new JobView.Construction(job)) }
        };
        var settlement = ((Lookup<SettlementView>.Found)basis.FindSettlement(new(1))).Value;
        var body = new VBoxContainer();
        try
        {
            var page = new SettlementOverviewPage(_ => { }, () => true, _ => throw new InvalidOperationException("只查看概况，不应提交命令。"), _ => { }, _ => { });
            page.Build(body, false);
            page.Refresh(view, EconomyFixture.Content(), settlement, []);
            var text = Find<Label>(body, "Commissions").Text;
            Require(text.Contains("正在改装列车 7 的第 2 辆车") && !text.Contains("车辆 10"),
                "改装摘要应使用列车名和编组车序，不能把车辆身份当成车序。");
        }
        finally { body.Free(); }
    }

    [Test]
    public async Task Overview_commissions_explain_missing_parked_design_and_vehicle_without_internal_identities()
    {
        var parked = new ConstructionJobView(new(90), ConstructionKind.Repair, new(1),
            new(new(1), CapabilityKind.Construction), new ConstructionTarget.Existing(new InstanceId.Vehicle(new(9)), new("fixture-vehicle")),
            ConstructionState.Working, 0, 100, 50, .5, 5, [], [], []);
        var missing = parked with { Id = new(91), Target = new ConstructionTarget.Existing(new InstanceId.Vehicle(new(11)), new("fixture-vehicle")) };
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned,
            commissionJobs: [parked, missing]), EconomyFixture.Content());
        await Run(setup, (_, panel) =>
        {
            var text = Find<Label>(panel, "Commissions").Text;
            Require(text.Contains("停放车辆（设计内容缺失）") && text.Contains("车辆已不存在") &&
                !text.Contains("fixture-vehicle") && !text.Contains("车辆 11"),
                "缺失设计或车辆时应说明缺失对象，不显示哈希或车辆编号。");
            return Task.CompletedTask;
        });
        setup.Session.AssertComplete();
    }

    private static FixedContentView CommissionContent()
    {
        var basis = EconomyFixture.Content(mapPresentation: true);
        ContentLookup<DesignEvaluation> Design(string hash, string name) =>
            new ContentLookup<DesignEvaluation>.Found(new DesignEvaluation.Complete(new(hash), new("private-composition"),
                new(new(name, ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], [])), null,
                new(null, null, null, true, null, null, [], [], [], [], []), true, []));
        return new(basis.Core, basis.Generation)
        {
            Voxels = basis.Voxels, LocalDrafts = basis.LocalDrafts, Blueprints = basis.Blueprints,
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
                { [new CoreDefinitionReference.SettlementType(new("fixture:town"))] = basis.FindDefinitionDisplay(new CoreDefinitionReference.SettlementType(new("fixture:town"))) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
                { [new(1)] = basis.FindSettlementLayout(new(1)), [new(2)] = basis.FindSettlementLayout(new(2)) },
            Goods = new GoodId[] { EconomyFixture.Food, EconomyFixture.Equipment, new GoodId.Voxel(new("fixture:ore")) }
                .ToDictionary(good => good, basis.FindGood),
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [new("fixture-food-png")] = basis.FindAsset(new("fixture-food-png")) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            {
                [new("fixture-vehicle")] = Design("fixture-vehicle", "货车"),
                [new("fixture-refit")] = Design("fixture-refit", "改装后货车")
            }
        };
    }

    [Test]
    public async Task Declaration_requires_a_consequence_confirmation_and_cancel_submits_nothing()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await _input.Activate(Find<Button>(panel, "DeclareWar"));
            var dialog = Find<ConfirmationDialog>(panel, "DeclareWarConfirmation");
            Require(dialog.Visible && dialog.DialogText.Contains("无法存取") && dialog.DialogText.Contains("租期继续") &&
                dialog.DialogText.Contains("交易") && dialog.DialogText.Contains("防御"), "宣战确认应说明交易、租约和战斗的具体后果。");
            Require(dialog.GetOkButton().Disabled && dialog.DialogText.Contains("玩家车辆"), "在场车辆必须离站后才能宣战，并说明原因。");
            await UiCapture.Capture(TestScene, "settlement-war-confirmation");
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(!dialog.Visible && setup.Session.SubmitCount == 0, "取消宣战不得提交命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Confirming_war_submits_once_and_opens_the_existing_hostile_pages()
    {
        var before = EconomyFixture.Create(trainAtNode: false);
        var after = EconomyFixture.Create(2, trainAtNode: false, relation: SettlementRelation.Hostile);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.DeclareWar(new(1)), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
        await Run(setup, async (ui, panel) =>
        {
            await _input.Activate(Find<Button>(panel, "DeclareWar"));
            var dialog = Find<ConfirmationDialog>(panel, "DeclareWarConfirmation");
            await _input.Activate(dialog.GetOkButton(), dialog);
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            var current = Find<PanelContainer>(ui, "Settlement");
            var tabs = Find<TabContainer>(current, "Pages");
            Require(Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle)
                .SequenceEqual(new[] { "现车", "仓库", "建造与工地" }) && setup.Session.SubmitCount == 1,
                "宣战后应只保留敌对聚居地的剩余旧页，而且只提交一次。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Rejected_war_keeps_its_reason_in_the_overview_without_opening_a_global_error()
    {
        var before = EconomyFixture.Create(trainAtNode: false);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
        setup.Session.QueueSubmit(new CoreCommand.DeclareWar(new(1)), CommandSource.UserInterface,
            Completed(1, before, before, new CommandDecision.Rejected([
                new("invalid_state", "Settlement.Relation", new WorldObjectId.Settlement(new(1)), [])])), before);
        await Run(setup, async (ui, panel) =>
        {
            await _input.Activate(Find<Button>(panel, "DeclareWar"));
            var dialog = Find<ConfirmationDialog>(panel, "DeclareWarConfirmation");
            await _input.Activate(dialog.GetOkButton(), dialog);
            await _input.Frames(3);
            Require(Find<Label>(panel, "WarReason").Text.Contains("状态不符合操作要求"),
                "宣战拒绝原因未保留：" + Find<Label>(panel, "WarReason").Text);
            Require(ui.ErrorText.Length == 0, "宣战拒绝不应弹全局错误：" + ui.ErrorText);
            Require(setup.Session.SubmitCount == 1, "宣战被拒后不能自动重试。");
            await _input.Activate(Find<Button>(panel, "DeclareWar"));
            Require(Find<Label>(panel, "WarReason").Text.Length == 0, "重新操作宣战时应清除旧拒绝原因。");
            await _input.Activate(dialog.GetCancelButton(), dialog);
        });
        setup.Session.AssertComplete();
    }
}

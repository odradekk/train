using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class OwnedSettlementDemolitionUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);
    private static readonly JobRequest.Dismantle Request = new(new(new(3), CapabilityKind.Construction), new InstanceId.Building(new(1)), 0);
    internal static ICoreReadView Preview() => new DemolitionView();

    [Test]
    public async Task Demolition_requires_confirmation_and_cancel_preserves_building_then_reports_command_outcome_in_place()
    {
        var before = new DemolitionView();
        var setup = new HostSimulationTests.HostSetup(before, OwnedSettlementInspectorUiTests.Content());
        var command = new CoreCommand.StartOwnedJob(Request);
        setup.Session.QueueSubmit(command, CommandSource.UserInterface,
            Completed(1, before, before, new CommandDecision.Rejected([new("instance_locked", "Instance", null, [])])), before);
        setup.Session.QueueSubmit(command, CommandSource.UserInterface,
            Completed(2, before, before, new CommandDecision.Accepted([], [])), before);
        await Run(setup, async (_, panel) =>
        {
            var building = await Building(panel);
            await _input.Activate(Find<Button>(building, "Demolish"));
            var dialog = Find<ConfirmationDialog>(panel, "DismantleConfirmation");
            Require(dialog.Visible && dialog.DialogText.Contains("食品建筑") && dialog.DialogText.Contains("不能撤销") &&
                dialog.DialogText.Contains("聚居地库存"), "确认应写明拆哪座建筑、不能撤销和回收材料去向。");
            await UiCapture.Capture(TestScene, "owned-demolition-confirmation");
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(!dialog.Visible && building.Visible && setup.Session.SubmitCount == 0, "取消应保留建筑且不提交命令。");
            await _input.Activate(Find<Button>(building, "Demolish"));
            await _input.Activate(dialog.GetOkButton(), dialog);
            Require(setup.Session.SubmitCount == 1 && Find<Label>(building, "DismantleResult").Text.Length > 0,
                "确认只能提交一次，核心拒绝应显示在原建筑行。");
            var rejection = Find<Label>(building, "DismantleResult").Text;
            Require(!rejection.Contains("instance_locked") && !rejection.Contains("Instance"), "拒绝应使用中文，不暴露内部码。");
            await _input.Frames(2);
            Require(setup.Session.SubmitCount == 1, "拒绝后不能自动重试。");
            await _input.Activate(Find<Button>(building, "Demolish"));
            await _input.Activate(dialog.GetOkButton(), dialog);
            Require(setup.Session.SubmitCount == 2 && Find<Label>(building, "DismantleResult").Text.Length > 0 &&
                Find<Label>(building, "DismantleResult").Text != rejection, "显式重试受理后应在原处更新结果。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Demolition_quote_blocker_disables_confirmation_and_revision_never_submits_automatically()
    {
        var before = new DemolitionView(blocked: true);
        var after = new DemolitionView(2);
        var setup = new HostSimulationTests.HostSetup(before, OwnedSettlementInspectorUiTests.Content());
        setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
        await Run(setup, async (ui, panel) =>
        {
            var building = await Building(panel);
            await _input.Activate(Find<Button>(building, "Demolish"));
            var dialog = Find<ConfirmationDialog>(panel, "DismantleConfirmation");
            Require(dialog.Visible && dialog.GetOkButton().Disabled && dialog.DialogText.Contains("尚未结束"),
                "施工建筑正忙时应显示核心的中文原因并禁用确认。");
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试应发布新核心修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, after, host.Content!);
            await _input.Frames(2);
            Require(!dialog.GetOkButton().Disabled && setup.Session.SubmitCount == 0, "报价阻碍解除后应允许确认，但不能自动提交拆除。");
        });
        setup.Session.AssertComplete();
    }

    private async Task<Control> Building(Control panel)
    {
        var tabs = Find<TabContainer>(panel, "Pages");
        await _input.ChooseTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(index => tabs.GetTabTitle(index) == "建筑"));
        await _input.Activate(Find<Button>(panel, "SelectBuilding_1"));
        return Find<VBoxContainer>(panel, "Building_1");
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
            Find<EconomyPanels>(app, "EconomyPanels").OpenSettlement(new(host.GetStatus()!.Binding,
                host.Content!.Generation, new WorldObjectId.Settlement(new(1))), new(1));
            await _input.Frames(2);
            await body(app.GetNode<GameUi>("GameUi"), Find<PanelContainer>(app, "Settlement"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private sealed class DemolitionView(ulong revision = 1, bool blocked = false) : ICoreReadView
    {
        private readonly ICoreReadView basis = OwnedSettlementInspectorUiTests.View(revision, includeSite: blocked);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        {
            if (request != Request || funding != JobFunding.OwnInventory)
                throw new InvalidOperationException("UNCONFIGURED: demolition quote");
            return new CoreResult<JobQuote>.Success(new(request, funding, Revision, 40, [], [], [], new(0), new(0), new(0),
                blocked ? [new("busy", "Facility", null, [])] : []));
        }
        public SimulationPosition Position => basis.Position;
        public CoreRevision Revision => basis.Revision;
        public CoreInputId InputIdentity => basis.InputIdentity;
        public CoreFault? Fault => basis.Fault;
        public WorldSummary World => basis.World;
        public EconomyView Economy => basis.Economy;
        public Lookup<NodeView> FindNode(NodeId id) => basis.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => basis.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => basis.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => basis.FindTrain(id);
        public Lookup<InstanceView> FindInstance(InstanceId id) => basis.FindInstance(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => basis.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => basis.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => basis.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => basis.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => basis.FindJob(id);
        public Lookup<CombatView> FindCombat(CombatId id) => basis.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => basis.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => basis.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => basis.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => basis.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => basis.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => basis.QuoteLease(request);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => basis.PreviewSettlement(node);
    }
}

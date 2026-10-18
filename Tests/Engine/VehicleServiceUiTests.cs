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

public class VehicleServiceUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Repair_uses_ordered_core_quote_and_explicit_price_without_retrying_rejection()
    {
        var before = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            Require(before.QuoteCount == 0, "隐藏服务页不应获取报价。");
            await Services(economy);
            await SelectRepair(economy);
            await _input.Choose(Find<OptionButton>(economy, "ServiceSource"), 1);
            await _input.Activate(Find<Button>(economy, "AddServiceSource"));
            await _input.Choose(Find<OptionButton>(economy, "ServiceSource"), 2);
            await _input.Activate(Find<Button>(economy, "AddServiceSource"));
            await _input.ClickItem(Find<ItemList>(economy, "ServiceSourceOrder"), 1);
            await _input.Activate(Find<Button>(economy, "MoveServiceSourceUp"));
            var quote = before.LastQuote!;
            Require(quote.Request is JobRequest.Repair repair && repair.Sources.SequenceEqual(new InventoryId[] { new(11), new(12) }), "材料来源顺序没有传给核心。");
            var text = Find<Label>(economy, "ServiceQuote").Text;
            Require(text.Contains("NPC 补购") && text.Contains("731") && text.Contains("× 3"), "未显示核心补购和精确付款。");
            var count = before.QuoteCount;
            await _input.Frames(4);
            Require(before.QuoteCount == count, "同一读视图逐帧重复获取报价。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(731)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Rejected([new("price_changed", "MaximumPayment", null, ["fixture:price-rise"])])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1 && before.QuoteCount == count && Find<Button>(economy, "ConfirmService").Disabled,
                "旧价被拒绝后自动重报、重试或仍可确认。");
            Require(ui.ErrorText.Contains("price_changed") || Find<Label>(economy, "ServiceResult").Text.Contains("拒绝"), "拒绝没有显示。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Blockers_null_payment_and_new_boundary_update_queue_without_replacing_controls()
    {
        var before = new ServiceView(blocked: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            await Services(economy);
            await SelectRepair(economy);
            var description = Find<Label>(economy, "ServiceQuote");
            Require(description.Text.Contains("fixture:missing") && description.Text.Contains("fixture:capacity") &&
                Find<Button>(economy, "ConfirmService").Disabled, "报价没有完整显示所有阻塞或允许提交。");
            var row = Find<Label>(Find<VBoxContainer>(economy, "ServiceJobs"), "ServiceJob_4");
            Require(row.Text.Contains("fixture:missing") && row.Text.Contains("劳动力"), "队列缺料和工作限制未显示。");
            var next = new ServiceView(2, nullPayment: true);
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
            Require(host.Step(1).Problem is null, "无法发布新边界。");
            economy.Refresh(host.GetStatus()!.Binding, next, host.Content!);
            Require(ReferenceEquals(description, Find<Label>(economy, "ServiceQuote")) &&
                ReferenceEquals(row, Find<Label>(Find<VBoxContainer>(economy, "ServiceJobs"), "ServiceJob_4")) &&
                row.Text.Contains("60/100") && row.Text.Contains("电力"), "边界刷新没有更新进度和限制或替换了控件。");
            Require(Find<Button>(economy, "ConfirmService").Disabled && description.Text.Contains("不能确认"), "NPC未提供总付款仍可确认。");
            var gone = new ServiceView(3, missingFacility: true);
            setup.Session.QueueAdvance(new(1, gone.Position, [], null, null), gone);
            Require(host.Step(1).Problem is null, "无法发布对象移除边界。");
            economy.Refresh(host.GetStatus()!.Binding, gone, host.Content!);
            Require(description.Text.Contains("已不存在") && Find<Button>(economy, "ConfirmService").Disabled && gone.QuoteCount == 0,
                "设施消失后仍向核心报价或未说明原因。");
            economy.Refresh(new HostBinding(Guid.NewGuid()), gone, host.Content!);
            Require(Find<Button>(economy, "ConfirmService").Disabled && setup.Session.SubmitCount == 0, "陈旧绑定仍可提交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Core_completion_removes_queue_row_and_opens_existing_formation()
    {
        var before = new ServiceView();
        var after = new ServiceView(2, completed: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            await Services(economy);
            var completion = new CoreEvent.ConstructionCompleted(new(4), ConstructionKind.Construction,
                new ConstructionTarget.NewVehicle(new("fixture-vehicle")), new InstanceId.Vehicle(new(9)), []);
            setup.Session.QueueAdvance(new(1, after.Position, [new(after.Position, 0, completion)], null, null), after);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "不能发布真实宿主完工事件。");
            await _input.WaitUntil(() => Find<Label>(economy, "ServiceResult").Text.Contains("可编组"));
            Require(Find<VBoxContainer>(economy, "ServiceJobs").GetChildCount() == 0,
                "已不在CommissionJobs的任务仍保留在队列。");
            await _input.Activate(Find<Button>(economy, "FormServiceVehicle"));
            var formation = ui.GetNode<TrainFormationPanel>("Root/World/TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            Require(available.IsVisibleInTree() && available.ItemCount == 1 && available.GetItemText(0).Contains("车辆 9"),
                "完工后的原车辆没有进入现有编组入口。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Build_from_local_blueprint_submits_content_candidate_design_and_activates_only_after_acceptance()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("service-design"), new("service-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
                [], 10, 10, 1, new(100), [], [new(EconomyFixture.Food, 1)], 1, [], true, []), []);
        var draft = new DesignDraft(new("本局制造蓝图", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("service-blueprint")), draft, [], evaluation);
        var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var target = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var before = new ServiceView(expectedDesign: design);
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var candidate = new ScriptedContentCandidate(target, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        var preparations = 0;
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
            {
                Require(ReferenceEquals(basis, content) && edit is ContentEdit.ConstructionTarget requested &&
                    ReferenceEquals(requested.Target, draft), "制造没有把所选本局蓝图交给内容准备目标。");
                preparations++;
                return new ContentResult<IContentCandidate>.Success(candidate);
            };
            await Services(economy);
            await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
            var choices = Find<OptionButton>(economy, "ServiceBlueprint");
            Require(choices.ItemCount == 1 && choices.GetItemText(0).Contains("本局库"), "本局库蓝图未展示。");
            await _input.Choose(choices, 0);
            Require(preparations == 1 && candidate.Activations == 0 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, content),
                "报价前错误激活内容或没有准备候选。");
            var quote = before.LastQuote!;
            Require(quote.Request is JobRequest.BuildVehicle build && ReferenceEquals(build.Design, design),
                "UI未使用内容候选提供的DesignInput。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(731)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            await _input.WaitUntil(() => candidate.Activations == 1);
            Require(setup.Session.SubmitCount == 1 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, target),
                "制造接受后没有激活同一候选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_repair_submits_owned_job_without_npc_payment()
    {
        var before = new ServiceView(nullPayment: true, owned: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Services(economy);
            await SelectRepair(economy);
            var quote = before.LastQuote!;
            Require(quote.Funding == JobFunding.OwnInventory && quote.TotalPayment is null &&
                !Find<Button>(economy, "ConfirmService").Disabled, "自有修理错误依赖NPC付款。");
            setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(quote.Request), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            Require(setup.Session.SubmitCount == 1, "自有修理未通过StartOwnedJob提交。");
        });
        setup.Session.AssertComplete();
    }

    private async Task SelectRepair(EconomyPanels economy)
    {
        await _input.Choose(Find<OptionButton>(economy, "ServiceOperation"), 1);
        await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
        await _input.Choose(Find<OptionButton>(economy, "ServiceVehicle"), 1);
    }

    private async Task Services(EconomyPanels economy)
    {
        var tabs = Find<TabContainer>(economy, "Pages");
        var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "车辆服务");
        var bar = tabs.GetTabBar();
        await _input.Frames(2);
        await _input.Click(bar.GlobalPosition + bar.GetTabRect(index).GetCenter());
        Require(tabs.CurrentTab == index, "真实点击未打开车辆服务。");
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

    private sealed class ServiceView(ulong revision = 1, bool blocked = false, bool nullPayment = false, bool completed = false, bool missingFacility = false, DesignInput? expectedDesign = null, bool owned = false) : ICoreReadView
    {
        private readonly ICoreReadView basis = EconomyFixture.Create(revision, vehicle: EconomyFixture.VehicleState.Owned);
        private static readonly FacilityKey Facility = new(new(20), CapabilityKind.Construction);
        private static readonly ImmutableArray<CoreProblem> Missing = [new("insufficient_goods", "materials", null, ["fixture:missing"])];
        internal int QuoteCount { get; private set; }
        internal JobQuote? LastQuote { get; private set; }
        public Lookup<SettlementView> FindSettlement(SettlementId id)
        {
            if (basis.FindSettlement(id) is not Lookup<SettlementView>.Found found) return basis.FindSettlement(id);
            return new Lookup<SettlementView>.Found(found.Value with { Relation = owned ? SettlementRelation.PlayerOwned : found.Value.Relation, Plots = missingFacility ? [] : [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(20)))], CommissionJobs = completed || owned ? [] : [new(4)] });
        }
        public Lookup<JobView> FindJob(JobId id) => id.Value == 4
            ? new Lookup<JobView>.Found(new JobView.Construction(new(id, ConstructionKind.Repair, new(1), Facility,
                new ConstructionTarget.Existing(new InstanceId.Vehicle(new(9)), new("fixture-vehicle")), ConstructionState.ResourceBlocked,
                0, 100, revision == 1 ? 20 : 60, revision == 1 ? .2 : .6, 3,
                revision == 1 ? [WorkLimit.Labor] : [WorkLimit.Power], [], Missing)))
            : throw new InvalidOperationException("UNCONFIGURED: FindJob");
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        {
            var sources = request switch
            {
                JobRequest.Repair repair when repair.Facility == Facility && repair.Instance is InstanceId.Vehicle { Id.Value: 9 or 10 } => repair.Sources,
                JobRequest.BuildVehicle build when build.Facility == Facility && expectedDesign is not null && ReferenceEquals(build.Design, expectedDesign) => build.Sources,
                _ => throw new InvalidOperationException("UNCONFIGURED: GetJobQuote request")
            };
            if (funding != (owned ? JobFunding.OwnInventory : JobFunding.NpcCommission)) throw new InvalidOperationException("UNCONFIGURED: GetJobQuote funding");
            QuoteCount++;
            LastQuote = new(request, funding, Revision, 100, [new(EconomyFixture.Food, 7)],
                [.. sources.Select(id => new JobMaterialSourceView(id, [new(EconomyFixture.Food, 2)]))],
                [new(EconomyFixture.Food, 3)], new(500), new(231), nullPayment ? null : new(731),
                blocked ? [.. Missing, new("insufficient_capacity", "queue", null, ["fixture:capacity"])] : []);
            return new CoreResult<JobQuote>.Success(LastQuote);
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
        public Lookup<InstanceView> FindInstance(InstanceId id) => id is InstanceId.Building { Id.Value: 20 }
            ? new Lookup<InstanceView>.Missing() : basis.FindInstance(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => basis.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => basis.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => basis.FindFacility(key);
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

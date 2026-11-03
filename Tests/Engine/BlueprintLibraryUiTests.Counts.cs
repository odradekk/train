using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class BlueprintLibraryUiTests
{
    [Test]
    public async Task Vehicle_counts_are_reused_across_new_read_views_and_card_selection_until_refresh()
    {
        var basis = Vehicles("pack-locomotive");
        var views = new List<CountedView> { new(basis) };
        var fixture = await Ready(views[0]);
        try
        {
            var library = Library(fixture); var input = new UiTestInput(TestScene);
            var queries = views[0].LocationQueries;
            Require(queries == basis.World.Nodes.Length + basis.World.Edges.Length && Shown(library, "2 辆车使用这个设计"),
                "打开库时应只遍历一趟地点，并显示当前自有车辆数。");
            for (ulong step = 1; step <= 4; step++)
            {
                var next = new CountedView(basis, step); views.Add(next);
                fixture.CoreSession.QueueAdvance(new(1, new(step, 0), [], null, null), next);
                Require(fixture.App.Host!.Step(1).Completed == 1, "测试必须通过真实宿主换到新的读视图。");
                await input.Frames(3);
            }
            Require(views.Sum(view => view.LocationQueries) == queries && views.All(view => view.SettlementQueries == 0),
                "新读视图不能使开着的蓝图库重新查询地点或聚居地。");
            Require(Shown(library, "2 辆车使用这个设计"), "库开着时保留上次刷新时的统计。");
            await input.Activate(Button(library, "内容包", exact: true));
            Require(Shown(library, "1 辆车使用这个设计") && views.Sum(view => view.LocationQueries) == queries,
                "另一设计的车辆也应在打开时已汇总，选中时只查表。");
            await input.Activate(Button(library, "本局蓝图库", exact: true));
            library.Hide(); library.Open(); await input.Frames(3);
            Require(Shown(library, "0 辆车使用这个设计") && views.Sum(view => view.LocationQueries) == queries * 2,
                "重新打开时应只再扫一趟，显示最新读视图中的车辆数。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    private sealed class CountedView(ICoreReadView basis, ulong step = 0) : ICoreReadView
    {
        internal int LocationQueries { get; private set; }
        internal int SettlementQueries { get; private set; }
        public SimulationPosition Position => new(step, 0);
        public CoreRevision Revision => new(step);
        public CoreInputId InputIdentity => basis.InputIdentity;
        public CoreFault? Fault => basis.Fault;
        public WorldSummary World => basis.World;
        public EconomyView Economy => basis.Economy;
        public Lookup<LocationView> FindLocation(WorldLocation location)
        {
            LocationQueries++;
            var result = basis.FindLocation(location);
            return step != 0 && result is Lookup<LocationView>.Found found
                ? new Lookup<LocationView>.Found(found.Value with { Instances = [] }) : result;
        }
        public Lookup<SettlementView> FindSettlement(SettlementId id) { SettlementQueries++; return basis.FindSettlement(id); }
        public Lookup<InstanceView> FindInstance(InstanceId id) => basis.FindInstance(id);
        public Lookup<NodeView> FindNode(NodeId id) => basis.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => basis.FindEdge(id);
        public Lookup<TrainView> FindTrain(TrainId id) => basis.FindTrain(id);
        public CoreResult<TrainFormationPreview> PreviewTrainFormation(System.Collections.Immutable.ImmutableArray<VehicleId> vehicles, TrainId? train) =>
            basis.PreviewTrainFormation(vehicles, train);
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
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => basis.GetJobQuote(request, funding);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => basis.PreviewSettlement(node);
        public CoreResult<RailwayWorkPreview> PreviewRailwayWork(RailwayWorkRequest request, SettlementId? materialSource) => basis.PreviewRailwayWork(request, materialSource);
    }
}

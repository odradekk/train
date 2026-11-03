using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests
{
    private async Task WithInspector(ICoreReadView view, Func<GameUi, Control, HostSimulationTests.HostSetup, Task> run)
    {
        var fixture = new Chickensoft.GodotTestDriver.Fixture(TestScene.GetTree());
        var basis = EconomyFixture.Content(mapPresentation: true, equipmentName: "设备");
        var content = new FixedContentView(basis.Core, basis.Generation)
        {
            Voxels = basis.Voxels,
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> {
                [EconomyFixture.Food] = basis.FindGood(EconomyFixture.Food),
                [EconomyFixture.Equipment] = basis.FindGood(EconomyFixture.Equipment) },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> {
                [new("fixture-food-png")] = basis.FindAsset(new("fixture-food-png")) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> {
                [new("fixture-vehicle")] = basis.FindDesign(new("fixture-vehicle")) },
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>> {
                [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = basis.FindDefinitionDisplay(new CoreDefinitionReference.Terrain(new("fixture:plain"))),
                [new CoreDefinitionReference.SettlementType(new("fixture:town"))] = basis.FindDefinitionDisplay(new CoreDefinitionReference.SettlementType(new("fixture:town"))) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = basis.FindSettlementLayout(new(1)),
                [new(2)] = basis.FindSettlementLayout(new(1)) is ContentLookup<SettlementLayoutProjection>.Found found
                    ? new ContentLookup<SettlementLayoutProjection>.Found(found.Value with { Display = new("石港", "") })
                    : throw new InvalidOperationException("缺少固定聚居地内容。")
            }
        };
        var setup = new HostSimulationTests.HostSetup(view, content);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            var input = new UiTestInput(TestScene);
            await input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            await input.Activate(Find<Button>(ui, "Fleet"));
            await input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            await run(ui, Find<GameShell>(ui, "*").InspectorContent!, setup);
        }
        finally { await fixture.Cleanup(); }
    }

    private sealed class InspectorView : ICoreReadView
    {
        private readonly ICoreReadView _basis = EconomyFixture.Create();
        internal TrainView Train { get; set; } = ((Lookup<TrainView>.Found)EconomyFixture.Create().FindTrain(new(7))).Value;
        internal Dictionary<InstanceId, InstanceView> Instances { get; } = [];
        internal InventoryView? Inventory { get; set; }
        internal SettlementView? Settlement { get; set; }
        internal Dictionary<VehicleId, JobQuote> RepairQuotes { get; } = [];
        internal int RepairQuoteRequests { get; private set; }
        internal CombatView? Combat { get; set; }
        internal bool TrainExists { get; set; } = true;
        public SimulationPosition Position => _basis.Position;
        public CoreRevision Revision => _basis.Revision;
        public CoreInputId InputIdentity => _basis.InputIdentity;
        public CoreFault? Fault => null;
        public EconomyView Economy => _basis.Economy with { RecentRunReports = Train.RecentReports };
        public WorldSummary World => _basis.World with
        {
            Nodes = [_basis.World.Nodes[0], _basis.World.Nodes[0] with { Id = new(2), Position = new(750, 300),
                Occupant = new NodeOccupant.Settlement(new(2), SettlementRelation.NonHostile) }],
            Edges = [new(new(1), new(1), new(2), 300, _basis.World.Nodes[0].Terrain, 1, 1, [], [])],
            Trains = TrainExists ? [new(Train.Id, Train.Position, Train.Activity)] : [],
            Combats = Combat is { } combat ? [new(combat.Id, combat.Location)] : []
        };
        public Lookup<TrainView> FindTrain(TrainId id) => id == Train.Id
            ? TrainExists ? new Lookup<TrainView>.Found(Train) : new Lookup<TrainView>.Missing()
            : _basis.FindTrain(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => new Lookup<EdgeView>.Found(new(World.Edges.Single(edge => edge.Id == id), [], null));
        public Lookup<NodeView> FindNode(NodeId id) => new Lookup<NodeView>.Found(new(World.Nodes.Single(node => node.Id == id), [new(1)], 80, 0, 80));
        public Lookup<LocationView> FindLocation(WorldLocation location) => _basis.FindLocation(location);
        public Lookup<InstanceView> FindInstance(InstanceId id) => Instances.TryGetValue(id, out var instance)
            ? new Lookup<InstanceView>.Found(instance) : _basis.FindInstance(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => Settlement is not null && Settlement.Id == id
            ? new Lookup<SettlementView>.Found(Settlement) : _basis.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => Inventory is not null && Inventory.Id == id
            ? new Lookup<InventoryView>.Found(Inventory) : _basis.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => _basis.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => _basis.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => _basis.FindJob(id);
        public Lookup<CombatView> FindCombat(CombatId id) => Combat is { } combat && combat.Id == id
            ? new Lookup<CombatView>.Found(combat) : _basis.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => _basis.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => _basis.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => _basis.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => _basis.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => _basis.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => _basis.QuoteLease(request);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        {
            if (request is JobRequest.Repair) RepairQuoteRequests++;
            return request is JobRequest.Repair { Instance: InstanceId.Vehicle vehicle } &&
                funding == JobFunding.NpcCommission && RepairQuotes.TryGetValue(vehicle.Id, out var quote)
                ? new CoreResult<JobQuote>.Success(quote) : _basis.GetJobQuote(request, funding);
        }
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => _basis.PreviewSettlement(node);
        public CoreResult<RailwayWorkPreview> PreviewRailwayWork(RailwayWorkRequest request, SettlementId? materialSource) => _basis.PreviewRailwayWork(request, materialSource);
        public CoreResult<TrainFormationPreview> PreviewTrainFormation(ImmutableArray<VehicleId> orderedVehicles, TrainId? train) =>
            train == Train.Id && orderedVehicles.SequenceEqual(Train.Vehicles)
                ? new CoreResult<TrainFormationPreview>.Success(new(20, 10, 100, 100, [], null))
                : _basis.PreviewTrainFormation(orderedVehicles, train);
    }
}

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

public partial class VehicleServiceUiTests
{
    private async Task Services(Control economy)
    {
        var tabs = Find<TabContainer>(economy, "Pages");
        var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "建造与工地");
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
            await _input.ClickItem(NodeObjects(ui), FirstSelectableItem(NodeObjects(ui)));
            await body(ui, Find<PanelContainer>(ui, "Settlement"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private sealed partial class ServiceView(ulong revision = 1, bool blocked = false, bool nullPayment = false, bool completed = false, bool missingFacility = false, DesignInput? expectedDesign = null, bool owned = false, bool paused = false, bool changedDesign = false, bool emptyPlot = false, bool constructionPlot = false, InstanceLock buildingLock = InstanceLock.None, bool buildingWork = false, bool buildingPaused = false, bool facilityBlocked = false, PlotSize targetPlot = PlotSize.Small, bool activeTrain = false, bool healthyParkedVehicle = false) : ICoreReadView
    {
        internal bool DamagedParkedVehicle { get; init; }
        private readonly ICoreReadView basis = EconomyFixture.Create(revision, vehicle: EconomyFixture.VehicleState.Owned,
            relation: owned ? SettlementRelation.PlayerOwned : SettlementRelation.NonHostile,
            extraOwned: healthyParkedVehicle ? [new(12)] : [],
            trainActivity: activeTrain ? TrainActivity.Waiting : TrainActivity.Stopped,
            trainPlan: activeTrain ? new RunPlan(false, [new PlanStop.Waypoint(new(1))]) : null);
        private static readonly FacilityKey Facility = new(new(20), CapabilityKind.Construction);
        private static readonly ImmutableArray<CoreProblem> Missing = [new("insufficient_goods", "materials", null, ["fixture:missing"])];
        internal int QuoteCount { get; private set; }
        internal JobQuote? LastQuote { get; private set; }
        internal List<JobRequest.Repair> RepairRequests { get; } = [];
        public Lookup<SettlementView> FindSettlement(SettlementId id)
        {
            if (basis.FindSettlement(id) is not Lookup<SettlementView>.Found found) return basis.FindSettlement(id);
            PlotView[] plots = missingFacility ? [] : [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(20))), new(new(2), 2, PlotSize.Small, [], new PlotOccupant.Building(new(21)))];
            if (emptyPlot && !missingFacility) plots = [.. plots, new(new(3), 3, PlotSize.Medium, [], new PlotOccupant.Empty())];
            if (constructionPlot && !missingFacility) plots = [.. plots, new(new(4), 4, PlotSize.Large, [], new PlotOccupant.Construction(new(4)))];
            return new Lookup<SettlementView>.Found(found.Value with { Plots = [.. plots], CommissionJobs = completed || owned ? [] : [new(4)], OwnedJobs = owned && !completed ? buildingWork ? [new(4), new(5), new(6), new(7), new(8), new(9)] : [new(4), new(5)] : [] });
        }
        public Lookup<JobView> FindJob(JobId id)
        {
            if (constructionPlot && id.Value == 4)
                return new Lookup<JobView>.Found(new JobView.Construction(new(id, ConstructionKind.Construction, new(1), Facility,
                    new ConstructionTarget.NewBuilding(new(4), new("fixture-plain-building")), ConstructionState.ResourceBlocked,
                    0, 100, 20, .2, 3, [WorkLimit.Labor], [], Missing)));
            if (buildingWork && owned && id.Value is 6 or 7 or 8 or 9)
            {
                var kind = id.Value switch
                {
                    6 => ConstructionKind.Deconstruction, 7 => ConstructionKind.Repair, 8 => ConstructionKind.Refit, _ => ConstructionKind.Construction
                };
                ConstructionTarget target = id.Value == 9
                    ? new ConstructionTarget.NewBuilding(new(3), new("fixture-plain-building"))
                    : new ConstructionTarget.Existing(new InstanceId.Building(new(21)), new("fixture-plain-building"));
                var state = id.Value == 6 && buildingPaused ? ConstructionState.Paused : ConstructionState.ResourceBlocked;
                return new Lookup<JobView>.Found(new JobView.Construction(new(id, kind, new(1), Facility, target, state, 0, 100, 20, .2, 3, [WorkLimit.Labor], [], Missing)));
            }
            return id.Value is 4 or 5 && (id.Value == 4 || owned)
            ? new Lookup<JobView>.Found(new JobView.Construction(new(id, owned ? ConstructionKind.Deconstruction : ConstructionKind.Repair, new(1), Facility,
                new ConstructionTarget.Existing(new InstanceId.Vehicle(new(9)), new("fixture-vehicle")), id.Value == 4 && paused ? ConstructionState.Paused : ConstructionState.ResourceBlocked,
                0, 100, revision == 1 ? 20 : 60, revision == 1 ? .2 : .6, 3,
                revision == 1 ? [WorkLimit.Labor] : [WorkLimit.Power], [], Missing)))
            : throw new InvalidOperationException("UNCONFIGURED: FindJob");
        }
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        {
            if (request is JobRequest.Repair requestedRepair) RepairRequests.Add(requestedRepair);
            var sources = request switch
            {
                JobRequest.Repair repair when repair.Facility == Facility && repair.Instance is InstanceId.Vehicle { Id.Value: 9 or 10 } => repair.Sources,
                JobRequest.Repair repair when owned && repair.Facility == Facility && repair.Instance is InstanceId.Building { Id.Value: 21 } => repair.Sources,
                JobRequest.Dismantle dismantle when owned && dismantle.Facility == Facility && dismantle.Instance is InstanceId.Vehicle { Id.Value: 9 or 12 } => [],
                JobRequest.Dismantle dismantle when buildingWork && owned && dismantle.Facility == Facility && dismantle.Instance is InstanceId.Building { Id.Value: 21 } => [],
                JobRequest.Refit refit when refit.Facility == Facility && expectedDesign is not null && ReferenceEquals(refit.Change.Target, expectedDesign) => refit.Sources,
                JobRequest.BuildVehicle build when build.Facility == Facility && expectedDesign is not null && ReferenceEquals(build.Design, expectedDesign) => build.Sources,
                JobRequest.BuildBuilding build when owned && build.Facility == Facility && build.Plot == new PlotId(3) && expectedDesign is not null && ReferenceEquals(build.Design, expectedDesign) => build.Sources,
                _ => throw new InvalidOperationException("UNCONFIGURED: GetJobQuote request")
            };
            if (funding != (owned ? JobFunding.OwnInventory : JobFunding.NpcCommission)) throw new InvalidOperationException("UNCONFIGURED: GetJobQuote funding");
            QuoteCount++;
            ImmutableArray<CoreProblem> problems = blocked
                ? [.. Missing, new("insufficient_capacity", "queue", null, ["fixture:capacity"])] : [];
            if (blocked && request is JobRequest.BuildBuilding) problems = problems.Add(new("plot_occupied", "Plot", null, []));
            if (facilityBlocked) problems = problems.Add(new("conditions_not_met", "Facility", new WorldObjectId.Instance(new InstanceId.Building(Facility.Building)), []));
            LastQuote = new(request, funding, Revision, 100, [new(EconomyFixture.Food, 7)],
                [.. sources.Select(id => new JobMaterialSourceView(id, [new(EconomyFixture.Food, 2)]))],
                [new(EconomyFixture.Food, 3)], new(500), new(231), nullPayment ? null : new(999), problems);
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
        public Lookup<InstanceView> FindInstance(InstanceId id)
        {
            if (DamagedParkedVehicle && id is InstanceId.Vehicle { Id.Value: 9 } && basis.FindInstance(id) is Lookup<InstanceView>.Found damaged)
                return new Lookup<InstanceView>.Found(damaged.Value with { Structure = new(60, 100) });
            if (changedDesign && id is InstanceId.Vehicle { Id.Value: 9 } && basis.FindInstance(id) is Lookup<InstanceView>.Found vehicle)
                return new Lookup<InstanceView>.Found(vehicle.Value with { Design = new("changed-vehicle") });
            if (id is InstanceId.Building { Id.Value: 21 })
                return new Lookup<InstanceView>.Found(new(id, new("fixture-plain-building"), new("fixture-plain-composition"),
                    new WorldLocation.Node(new(1)), SettlementRelation.PlayerOwned, new InstanceDetails.Building(new(2), targetPlot),
                    InstanceLock.None, new(100, 100), [], []));
            if (id is InstanceId.Building { Id.Value: 20 })
                return new Lookup<InstanceView>.Found(new(id, new("fixture-service-building"), new("fixture-building-composition"),
                    new WorldLocation.Node(new(1)), owned ? SettlementRelation.PlayerOwned : SettlementRelation.NonHostile,
                    new InstanceDetails.Building(new(1), PlotSize.Small), buildingLock, new(100, 100), [], []));
            return basis.FindInstance(id);
        }
        public Lookup<InventoryView> FindInventory(InventoryId id)
        {
            var found = basis.FindInventory(id);
            if (buildingWork && id.Value == 10 && found is Lookup<InventoryView>.Found inventory)
                return new Lookup<InventoryView>.Found(inventory.Value with { Reservations = [new(new(6), 40, [new(EconomyFixture.Food, 2)])] });
            return found;
        }
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

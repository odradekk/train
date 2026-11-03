using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class VehicleRepairsTests
{
    [TestMethod]
    public void QuotesDamagedVehiclesInFormationOrderAndSkipsUndamagedVehicles()
    {
        var structure = Vehicle(8, structure: 6);
        var part = Vehicle(3, part: 2);
        var healthy = Vehicle(5);
        var view = new RepairReadView(20, structure, part, healthy);
        var first = RepairQuote(40, 8, new(4));
        var second = RepairQuote(40, 3, new(7));
        view.Add(first);
        view.Add(second);

        var result = VehicleRepairs.Quote([new(8), new(5), new(3)], Settlement(Building(40)), view, new FixedContentView());

        Assert.IsNull(result.Reason);
        Assert.AreEqual(new Money(11), result.Total);
        Assert.AreEqual(2, result.Jobs.Length);
        Assert.AreSame(first, result.Jobs[0]);
        Assert.AreSame(second, result.Jobs[1]);
    }

    [TestMethod]
    public void UsesFirstUnblockedBuildingInPlotOrder()
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6));
        view.Add(RepairQuote(40, 8, new(2), Blocker));
        var chosen = RepairQuote(20, 8, new(9));
        view.Add(chosen);
        view.Add(RepairQuote(10, 8, new(1)));
        var settlement = Settlement(Building(40),
            new(new(41), 41, PlotSize.Small, [], new PlotOccupant.Empty()),
            new(new(42), 42, PlotSize.Small, [], new PlotOccupant.Construction(new(7))),
            Building(20), Building(10));

        var result = VehicleRepairs.Quote([new(8)], settlement, view, new FixedContentView());

        Assert.IsNull(result.Reason);
        Assert.AreEqual(new Money(9), result.Total);
        Assert.AreEqual(1, result.Jobs.Length);
        Assert.AreSame(chosen, result.Jobs[0]);
    }

    [TestMethod]
    public void RejectsTheGroupWhenOneDamagedVehicleHasNoQuote()
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6), Vehicle(3, part: 2));
        view.Add(RepairQuote(40, 8, new(4)));
        view.AddFailure(40, 3);

        var result = VehicleRepairs.Quote([new(8), new(3)], Settlement(Building(40)), view, new FixedContentView());

        AssertUnavailable(result);
    }

    [TestMethod]
    public void RejectsRepairWhenEveryBuildingQuoteHasBlockers()
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6));
        view.Add(RepairQuote(40, 8, new(4), Blocker));
        view.Add(RepairQuote(20, 8, new(7), Blocker));

        var result = VehicleRepairs.Quote([new(8)], Settlement(Building(40), Building(20)), view, new FixedContentView());

        AssertUnavailable(result);
    }

    [TestMethod]
    public void WhenAllFacilitiesAreBlockedKeepsTheFirstFacilityReason()
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6));
        view.Add(RepairQuote(40, 8, new(4), new CoreProblem("insufficient_goods", "Sources", null, [])));
        view.Add(RepairQuote(20, 8, new(7), new CoreProblem("capability_zero", "Facility", null, [])));

        var result = VehicleRepairs.Quote([new(8)], Settlement(Building(40), Building(20)), view, new FixedContentView());

        Assert.AreEqual("货物不足。", result.Reason);
        Assert.IsEmpty(result.Jobs);
        Assert.IsNull(result.Total);
    }

    [TestMethod]
    public void RejectsAnOtherwiseUnblockedQuoteWithoutTotalPayment()
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6));
        view.Add(RepairQuote(40, 8, null));

        var result = VehicleRepairs.Quote([new(8)], Settlement(Building(40)), view, new FixedContentView());

        AssertUnavailable(result);
    }

    [TestMethod]
    public void AllowsPaymentEqualToTheWallet()
    {
        var view = new RepairReadView(11, Vehicle(8, structure: 6), Vehicle(3, part: 2));
        view.Add(RepairQuote(40, 8, new(4)));
        view.Add(RepairQuote(40, 3, new(7)));

        var result = VehicleRepairs.Quote([new(8), new(3)], Settlement(Building(40)), view, new FixedContentView());

        Assert.IsNull(result.Reason);
        Assert.AreEqual(new Money(11), result.Total);
        Assert.AreEqual(2, result.Jobs.Length);
    }

    [TestMethod]
    public void RejectsPaymentOneAboveTheWallet()
    {
        var view = new RepairReadView(10, Vehicle(8, structure: 6), Vehicle(3, part: 2));
        view.Add(RepairQuote(40, 8, new(4)));
        view.Add(RepairQuote(40, 3, new(7)));

        var result = VehicleRepairs.Quote([new(8), new(3)], Settlement(Building(40)), view, new FixedContentView());

        AssertUnavailable(result);
        StringAssert.Contains(result.Reason!, "资金不足");
    }

    [TestMethod]
    [DataRow(SettlementRelation.PlayerOwned)]
    [DataRow(SettlementRelation.Hostile)]
    public void OnlyNonHostileSettlementsOfferCommissionedRepairs(SettlementRelation relation)
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6));
        view.Add(RepairQuote(40, 8, new(4)));
        var settlement = Settlement(Building(40)) with { Relation = relation };

        var result = VehicleRepairs.Quote([new(8)], settlement, view, new FixedContentView());

        AssertUnavailable(result);
    }

    [TestMethod]
    public void RejectsRepairWhenTheSettlementHasNoBuilding()
    {
        var view = new RepairReadView(20, Vehicle(8, structure: 6));
        var settlement = Settlement(new PlotView(new(41), 41, PlotSize.Small, [], new PlotOccupant.Empty()));

        var result = VehicleRepairs.Quote([new(8)], settlement, view, new FixedContentView());

        AssertUnavailable(result);
    }

    [TestMethod]
    public void AnEmptyGroupNeedsNoRepairs()
    {
        var result = VehicleRepairs.Quote([], Settlement(Building(40)), new RepairReadView(0), new FixedContentView());

        Assert.AreEqual(0, result.Jobs.Length);
        Assert.AreEqual(new Money(0), result.Total);
        Assert.AreEqual("没有损伤，不需要修理", result.Reason);
    }

    [TestMethod]
    public void UndamagedVehiclesNeedNoRepairs()
    {
        var view = new RepairReadView(0, Vehicle(8), Vehicle(3));

        var result = VehicleRepairs.Quote([new(8), new(3)], Settlement(Building(40)), view, new FixedContentView());

        Assert.AreEqual(0, result.Jobs.Length);
        Assert.AreEqual(new Money(0), result.Total);
        Assert.AreEqual("没有损伤，不需要修理", result.Reason);
    }

    private static void AssertUnavailable(VehicleRepairQuote result)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Reason));
        Assert.IsTrue(result.Reason!.Any(character => character is >= '\u4e00' and <= '\u9fff'), result.Reason);
    }

    private static readonly CoreProblem Blocker = new("fixture_repair_unavailable", "Repair", null, []);
    private static readonly RuleStatusView Rule = new(new("fixture:rule"), null, []);

    private static InstanceView Vehicle(ulong id, long structure = 10, long part = 10) => new(
        new InstanceId.Vehicle(new(id)), new("fixture:vehicle"), new("fixture:composition"), new WorldLocation.Node(new(1)),
        SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None,
        new(structure, 10), [new(new(1), new(part, 10), false, false)], []);

    private static PlotView Building(ulong id) => new(new(id), checked((int)id), PlotSize.Small, [], new PlotOccupant.Building(new(id)));

    private static SettlementView Settlement(params PlotView[] plots) => new(new(1), new(1), new("fixture:town"), null,
        SettlementRelation.NonHostile, new(1), [.. plots], [], 0, 1, 0, [], [], [], new(0, 0, 0, 0),
        new(Rule, 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
        new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
        new(Rule, new(0, 0, 0, 0, 0, 0, 0), []), []);

    private static JobQuote RepairQuote(ulong building, ulong vehicle, Money? total, params CoreProblem[] blockers) => new(
        new JobRequest.Repair(new(new(building), CapabilityKind.Construction), new InstanceId.Vehicle(new(vehicle)), [], 0),
        JobFunding.NpcCommission, new(5), 17, 1, [], [], [], [], new(0), total, total, [.. blockers]);

    private sealed class RepairReadView : ICoreReadView
    {
        private readonly FixedCoreReadView _fixed;
        private readonly Dictionary<(BuildingId, VehicleId), CoreResult<JobQuote>> _quotes = [];

        internal RepairReadView(long wallet, params InstanceView[] vehicles)
        {
            _fixed = new()
            {
                Revision = new(5),
                Fault = null,
                Economy = new(new(wallet), Rule, Rule, Rule, new(0), [], []),
                Instances = vehicles.ToDictionary(vehicle => vehicle.Id,
                    vehicle => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(vehicle))
            };
        }

        internal void Add(JobQuote quote)
        {
            var repair = (JobRequest.Repair)quote.Request;
            _quotes.Add((repair.Facility.Building, ((InstanceId.Vehicle)repair.Instance).Id), new CoreResult<JobQuote>.Success(quote));
        }

        internal void AddFailure(ulong building, ulong vehicle) =>
            _quotes.Add((new(building), new(vehicle)), new CoreResult<JobQuote>.Failure([Blocker]));

        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        {
            if (funding == JobFunding.NpcCommission &&
                request is JobRequest.Repair
                {
                    Facility: { Capability: CapabilityKind.Construction } facility,
                    Instance: InstanceId.Vehicle vehicle, Sources: { IsEmpty: true }, Priority: 0
                } && _quotes.TryGetValue((facility.Building, vehicle.Id), out var result)) return result;
            throw new InvalidOperationException($"UNCONFIGURED: repair quote {request}, {funding}");
        }

        public SimulationPosition Position => _fixed.Position;
        public CoreRevision Revision => _fixed.Revision;
        public CoreInputId InputIdentity => _fixed.InputIdentity;
        public CoreFault? Fault => _fixed.Fault;
        public WorldSummary World => _fixed.World;
        public EconomyView Economy => _fixed.Economy;
        public Lookup<NodeView> FindNode(NodeId id) => _fixed.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => _fixed.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => _fixed.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => _fixed.FindTrain(id);
        public CoreResult<TrainFormationPreview> PreviewTrainFormation(ImmutableArray<VehicleId> orderedVehicles, TrainId? train) =>
            _fixed.PreviewTrainFormation(orderedVehicles, train);
        public Lookup<InstanceView> FindInstance(InstanceId id) => _fixed.FindInstance(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => _fixed.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => _fixed.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => _fixed.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => _fixed.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => _fixed.FindJob(id);
        public Lookup<CombatView> FindCombat(CombatId id) => _fixed.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => _fixed.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => _fixed.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => _fixed.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => _fixed.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => _fixed.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => _fixed.QuoteLease(request);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => _fixed.PreviewSettlement(node);
        public CoreResult<RailwayWorkPreview> PreviewRailwayWork(RailwayWorkRequest request, SettlementId? materialSource) => _fixed.PreviewRailwayWork(request, materialSource);
    }
}

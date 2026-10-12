using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.ContractSupport;

public sealed class FixedCoreReadView : ICoreReadView
{
    private SimulationPosition? position;
    public SimulationPosition Position
    {
        get => position ?? throw Unconfigured(nameof(Position));
        init => position = value;
    }

    private CoreRevision? revision;
    public CoreRevision Revision
    {
        get => revision ?? throw Unconfigured(nameof(Revision));
        init => revision = value;
    }

    private CoreInputId? inputIdentity;
    public CoreInputId InputIdentity
    {
        get => inputIdentity ?? throw Unconfigured(nameof(InputIdentity));
        init => inputIdentity = value;
    }

    private WorldSummary? world;
    public WorldSummary World
    {
        get => world ?? throw Unconfigured(nameof(World));
        init => world = value;
    }

    private EconomyView? economy;
    public EconomyView Economy
    {
        get => economy ?? throw Unconfigured(nameof(Economy));
        init => economy = value;
    }

    private CoreFault? fault;
    private bool faultConfigured;
    public CoreFault? Fault
    {
        get => faultConfigured ? fault : throw Unconfigured(nameof(Fault));
        init { fault = value; faultConfigured = true; }
    }

    private ImmutableDictionary<NodeId, Lookup<NodeView>> nodes = ImmutableDictionary<NodeId, Lookup<NodeView>>.Empty;
    public IReadOnlyDictionary<NodeId, Lookup<NodeView>> Nodes
    {
        init => nodes = value.ToImmutableDictionary();
    }
    public Lookup<NodeView> FindNode(NodeId id)
        => nodes.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindNode));

    private ImmutableDictionary<EdgeId, Lookup<EdgeView>> edges = ImmutableDictionary<EdgeId, Lookup<EdgeView>>.Empty;
    public IReadOnlyDictionary<EdgeId, Lookup<EdgeView>> Edges
    {
        init => edges = value.ToImmutableDictionary();
    }
    public Lookup<EdgeView> FindEdge(EdgeId id)
        => edges.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindEdge));

    private ImmutableDictionary<WorldLocation, Lookup<LocationView>> locations = ImmutableDictionary<WorldLocation, Lookup<LocationView>>.Empty;
    public IReadOnlyDictionary<WorldLocation, Lookup<LocationView>> Locations
    {
        init => locations = value.ToImmutableDictionary();
    }
    public Lookup<LocationView> FindLocation(WorldLocation id)
        => locations.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindLocation));

    private ImmutableDictionary<TrainId, Lookup<TrainView>> trains = ImmutableDictionary<TrainId, Lookup<TrainView>>.Empty;
    public IReadOnlyDictionary<TrainId, Lookup<TrainView>> Trains
    {
        init => trains = value.ToImmutableDictionary();
    }
    public Lookup<TrainView> FindTrain(TrainId id)
        => trains.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindTrain));

    private ImmutableDictionary<InstanceId, Lookup<InstanceView>> instances = ImmutableDictionary<InstanceId, Lookup<InstanceView>>.Empty;
    public IReadOnlyDictionary<InstanceId, Lookup<InstanceView>> Instances
    {
        init => instances = value.ToImmutableDictionary();
    }
    public Lookup<InstanceView> FindInstance(InstanceId id)
        => instances.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindInstance));

    private ImmutableDictionary<SettlementId, Lookup<SettlementView>> settlements = ImmutableDictionary<SettlementId, Lookup<SettlementView>>.Empty;
    public IReadOnlyDictionary<SettlementId, Lookup<SettlementView>> Settlements
    {
        init => settlements = value.ToImmutableDictionary();
    }
    public Lookup<SettlementView> FindSettlement(SettlementId id)
        => settlements.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindSettlement));

    private ImmutableDictionary<InventoryId, Lookup<InventoryView>> inventories = ImmutableDictionary<InventoryId, Lookup<InventoryView>>.Empty;
    public IReadOnlyDictionary<InventoryId, Lookup<InventoryView>> Inventories
    {
        init => inventories = value.ToImmutableDictionary();
    }
    public Lookup<InventoryView> FindInventory(InventoryId id)
        => inventories.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindInventory));

    private ImmutableDictionary<LeaseId, Lookup<LeaseView>> leases = ImmutableDictionary<LeaseId, Lookup<LeaseView>>.Empty;
    public IReadOnlyDictionary<LeaseId, Lookup<LeaseView>> Leases
    {
        init => leases = value.ToImmutableDictionary();
    }
    public Lookup<LeaseView> FindLease(LeaseId id)
        => leases.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindLease));

    private ImmutableDictionary<FacilityKey, Lookup<FacilityView>> facilities = ImmutableDictionary<FacilityKey, Lookup<FacilityView>>.Empty;
    public IReadOnlyDictionary<FacilityKey, Lookup<FacilityView>> Facilities
    {
        init => facilities = value.ToImmutableDictionary();
    }
    public Lookup<FacilityView> FindFacility(FacilityKey id)
        => facilities.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindFacility));

    private ImmutableDictionary<JobId, Lookup<JobView>> jobs = ImmutableDictionary<JobId, Lookup<JobView>>.Empty;
    public IReadOnlyDictionary<JobId, Lookup<JobView>> Jobs
    {
        init => jobs = value.ToImmutableDictionary();
    }
    public Lookup<JobView> FindJob(JobId id)
        => jobs.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindJob));

    private ImmutableDictionary<CombatId, Lookup<CombatView>> combats = ImmutableDictionary<CombatId, Lookup<CombatView>>.Empty;
    public IReadOnlyDictionary<CombatId, Lookup<CombatView>> Combats
    {
        init => combats = value.ToImmutableDictionary();
    }
    public Lookup<CombatView> FindCombat(CombatId id)
        => combats.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindCombat));

    private ImmutableDictionary<CreatureGroupId, Lookup<CreatureGroupView>> creatureGroups = ImmutableDictionary<CreatureGroupId, Lookup<CreatureGroupView>>.Empty;
    public IReadOnlyDictionary<CreatureGroupId, Lookup<CreatureGroupView>> CreatureGroups
    {
        init => creatureGroups = value.ToImmutableDictionary();
    }
    public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id)
        => creatureGroups.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindCreatureGroup));

    private ImmutableDictionary<CreatureId, Lookup<CreatureView>> creatures = ImmutableDictionary<CreatureId, Lookup<CreatureView>>.Empty;
    public IReadOnlyDictionary<CreatureId, Lookup<CreatureView>> Creatures
    {
        init => creatures = value.ToImmutableDictionary();
    }
    public Lookup<CreatureView> FindCreature(CreatureId id)
        => creatures.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindCreature));

    private ImmutableDictionary<LairId, Lookup<LairView>> lairs = ImmutableDictionary<LairId, Lookup<LairView>>.Empty;
    public IReadOnlyDictionary<LairId, Lookup<LairView>> Lairs
    {
        init => lairs = value.ToImmutableDictionary();
    }
    public Lookup<LairView> FindLair(LairId id)
        => lairs.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindLair));

    public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => throw Unconfigured(nameof(QuoteTrade));
    public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => throw Unconfigured(nameof(QuoteVehicle));
    public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => throw Unconfigured(nameof(QuoteLease));
    public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => throw Unconfigured(nameof(GetJobQuote));
    public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => throw Unconfigured(nameof(PreviewSettlement));

    private static InvalidOperationException Unconfigured(string member)
        => new($"UNCONFIGURED: {member}");
}

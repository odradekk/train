using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.ContractSupport;

public sealed class FixedCoreReadView : ICoreReadView
{
    public FixedCoreReadView() { }

    public FixedCoreReadView(SimulationPosition position, CoreInputId inputIdentity,
        CoreFault? fault = null, CoreRevision revision = default)
    {
        Position = position;
        InputIdentity = inputIdentity;
        Fault = fault;
        Revision = revision;
    }

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

    public EconomyView Economy => throw Unconfigured(nameof(Economy));

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

    public Lookup<SettlementView> FindSettlement(SettlementId id) => throw Unconfigured(nameof(FindSettlement));

    public Lookup<InventoryView> FindInventory(InventoryId id) => throw Unconfigured(nameof(FindInventory));

    public Lookup<LeaseView> FindLease(LeaseId id) => throw Unconfigured(nameof(FindLease));

    public Lookup<FacilityView> FindFacility(FacilityKey id) => throw Unconfigured(nameof(FindFacility));

    public Lookup<JobView> FindJob(JobId id) => throw Unconfigured(nameof(FindJob));

    private ImmutableDictionary<CombatId, Lookup<CombatView>> combats = ImmutableDictionary<CombatId, Lookup<CombatView>>.Empty;
    public IReadOnlyDictionary<CombatId, Lookup<CombatView>> Combats
    {
        init => combats = value.ToImmutableDictionary();
    }
    public Lookup<CombatView> FindCombat(CombatId id)
        => combats.TryGetValue(id, out var result) ? result : throw Unconfigured(nameof(FindCombat));

    public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => throw Unconfigured(nameof(FindCreatureGroup));

    public Lookup<CreatureView> FindCreature(CreatureId id) => throw Unconfigured(nameof(FindCreature));

    public Lookup<LairView> FindLair(LairId id) => throw Unconfigured(nameof(FindLair));

    public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => throw Unconfigured(nameof(QuoteTrade));
    public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => throw Unconfigured(nameof(QuoteVehicle));
    public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => throw Unconfigured(nameof(QuoteLease));
    public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => throw Unconfigured(nameof(GetJobQuote));
    public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => throw Unconfigured(nameof(PreviewSettlement));

    private static InvalidOperationException Unconfigured(string member)
        => new($"UNCONFIGURED: {member}");
}

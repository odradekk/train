using Train.Core.Contracts;
using Train.Core.World;
using Train.Core.Railway;

namespace Train.Core.Sessions;

internal sealed record CoreReadView(SimulationPosition Position, CoreRevision Revision,
    CoreInputId InputIdentity, CoreFault? Fault, WorldState State, CoreContentProjection Content) : ICoreReadView
{
    public WorldSummary World { get; } = State.Map;
    public EconomyView Economy => throw new NotImplementedException("Economy 尚未实现。");
    public Lookup<NodeView> FindNode(NodeId id)
    {
        if (id.Value == 0) throw new ArgumentException("节点编号必须非零。", nameof(id));
        return Lookup(State.FindNode(id));
    }
    public Lookup<EdgeView> FindEdge(EdgeId id)
    {
        if (id.Value == 0) throw new ArgumentException("线路编号必须非零。", nameof(id));
        return Lookup(State.FindEdge(id));
    }
    public Lookup<LocationView> FindLocation(WorldLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location is WorldLocation.Node { Id.Value: 0 } or WorldLocation.Edge { Id.Value: 0 })
            throw new ArgumentException("地点编号必须非零。", nameof(location));
        return Lookup(State.FindLocation(location));
    }
    public Lookup<TrainView> FindTrain(TrainId id)
    {
        if (id.Value == 0) throw new ArgumentException("列车编号必须非零。", nameof(id));
        var train = State.Trains.FirstOrDefault(t => t.Id == id);
        return Lookup(train is null ? null : TrainOperations.Read(State, Content, train));
    }
    public Lookup<InstanceView> FindInstance(InstanceId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id is InstanceId.Vehicle { Id.Value: 0 } or InstanceId.Building { Id.Value: 0 })
            throw new ArgumentException("实例编号必须非零。", nameof(id));
        return Lookup(State.FindInstance(id));
    }
    private static Lookup<T> Lookup<T>(T? value) where T : class
        => value is null ? new Lookup<T>.Missing() : new Lookup<T>.Found(value);
    public Lookup<SettlementView> FindSettlement(SettlementId id)
        => throw new NotImplementedException("FindSettlement 尚未实现。");
    public Lookup<InventoryView> FindInventory(InventoryId id) => throw new NotImplementedException("FindInventory 尚未实现。");
    public Lookup<LeaseView> FindLease(LeaseId id) => throw new NotImplementedException("FindLease 尚未实现。");
    public Lookup<FacilityView> FindFacility(FacilityKey key) => throw new NotImplementedException("FindFacility 尚未实现。");
    public Lookup<JobView> FindJob(JobId id) => throw new NotImplementedException("FindJob 尚未实现。");
    public Lookup<CombatView> FindCombat(CombatId id) => throw new NotImplementedException("FindCombat 尚未实现。");
    public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id)
        => throw new NotImplementedException("FindCreatureGroup 尚未实现。");
    public Lookup<CreatureView> FindCreature(CreatureId id) => throw new NotImplementedException("FindCreature 尚未实现。");
    public Lookup<LairView> FindLair(LairId id) => throw new NotImplementedException("FindLair 尚未实现。");
    public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request)
        => throw new NotImplementedException("QuoteTrade 尚未实现。");
    public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request)
        => throw new NotImplementedException("QuoteVehicle 尚未实现。");
    public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request)
        => throw new NotImplementedException("QuoteLease 尚未实现。");
    public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        => throw new NotImplementedException("GetJobQuote 尚未实现。");
    public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node)
        => throw new NotImplementedException("PreviewSettlement 尚未实现。");
}

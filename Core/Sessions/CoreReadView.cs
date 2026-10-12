using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal sealed record CoreReadView(SimulationPosition Position, CoreRevision Revision,
    CoreInputId InputIdentity, CoreFault? Fault) : ICoreReadView
{
    public WorldSummary World => throw new NotImplementedException("World 尚未实现。");
    public EconomyView Economy => throw new NotImplementedException("Economy 尚未实现。");
    public Lookup<NodeView> FindNode(NodeId id) => throw new NotImplementedException("FindNode 尚未实现。");
    public Lookup<EdgeView> FindEdge(EdgeId id) => throw new NotImplementedException("FindEdge 尚未实现。");
    public Lookup<LocationView> FindLocation(WorldLocation location)
        => throw new NotImplementedException("FindLocation 尚未实现。");
    public Lookup<TrainView> FindTrain(TrainId id) => throw new NotImplementedException("FindTrain 尚未实现。");
    public Lookup<InstanceView> FindInstance(InstanceId id) => throw new NotImplementedException("FindInstance 尚未实现。");
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

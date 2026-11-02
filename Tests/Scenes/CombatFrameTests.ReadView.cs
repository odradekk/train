using Train.Core.Contracts;

namespace Train.Scenes.Tests;

public sealed partial class CombatFrameTests
{
    private sealed class MissingInstance(ICoreReadView inner, InstanceId missing) : ICoreReadView
    {
        public Lookup<InstanceView> FindInstance(InstanceId id) => id == missing ? new Lookup<InstanceView>.Missing() : inner.FindInstance(id);
        public SimulationPosition Position => inner.Position;
        public CoreRevision Revision => inner.Revision;
        public CoreInputId InputIdentity => inner.InputIdentity;
        public CoreFault? Fault => inner.Fault;
        public WorldSummary World => inner.World;
        public EconomyView Economy => inner.Economy;
        public Lookup<NodeView> FindNode(NodeId id) => inner.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => inner.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => inner.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => inner.FindTrain(id);
        public Lookup<SettlementView> FindSettlement(SettlementId id) => inner.FindSettlement(id);
        public Lookup<InventoryView> FindInventory(InventoryId id) => inner.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => inner.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => inner.FindFacility(key);
        public Lookup<JobView> FindJob(JobId id) => inner.FindJob(id);
        public Lookup<CombatView> FindCombat(CombatId id) => inner.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => inner.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => inner.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => inner.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => inner.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => inner.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => inner.QuoteLease(request);
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => inner.GetJobQuote(request, funding);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => inner.PreviewSettlement(node);
        public CoreResult<TrainFormationPreview> PreviewTrainFormation(System.Collections.Immutable.ImmutableArray<VehicleId> orderedVehicles, TrainId? train) => inner.PreviewTrainFormation(orderedVehicles, train);
    }
}

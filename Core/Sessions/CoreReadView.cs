using Train.Core.Contracts;
using Train.Core.World;
using Train.Core.Railway;
using Train.Core.Inventory;
using Train.Core.Production;
using Train.Core.Settlements;
using Train.Core.Trading;

namespace Train.Core.Sessions;

internal sealed record CoreReadView(SimulationPosition Position, CoreRevision Revision,
    CoreInputId InputIdentity, CoreFault? Fault, WorldState State, CoreContentProjection Content) : ICoreReadView
{
    public WorldSummary World { get; } = State.Map;
    public EconomyView Economy => SettlementQueries.Economy(State, Content);
    public Lookup<NodeView> FindNode(NodeId id)
    {
        if (id.Value == 0) throw new ArgumentException("节点编号必须非零。", nameof(id));
        return Lookup(State.FindNode(id, Content));
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
    {
        if (id.Value == 0) throw new ArgumentException("聚居地编号必须非零。", nameof(id));
        var settlement = State.Settlements.FirstOrDefault(s => s.Id == id);
        return Lookup(settlement is null ? null : SettlementQueries.Read(State, Content, settlement, Position.CompletedSteps));
    }
    public Lookup<InventoryView> FindInventory(InventoryId id)
    {
        if (id.Value == 0) throw new ArgumentException("库存编号必须非零。", nameof(id));
        var inventory = State.Inventories.FirstOrDefault(i => i.Id == id);
        return Lookup(inventory is null ? null : InventoryStorage.Read(State, Content, Position.CompletedSteps, inventory));
    }
    public Lookup<LeaseView> FindLease(LeaseId id)
    {
        if (id.Value == 0) throw new ArgumentException("租约编号必须非零。", nameof(id));
        var lease = State.Leases.FirstOrDefault(l => l.Id == id);
        return Lookup(lease is null ? null : InventoryLeases.Read(lease, Position.CompletedSteps));
    }
    public Lookup<FacilityView> FindFacility(FacilityKey key)
    {
        var view = ProductionQueries.Find(State, Content, key);
        if (view is null) return new Lookup<FacilityView>.Missing();
        var settlement = State.Settlements.Single(s => s.Plots.Any(p => p.Building == key.Building));
        return Lookup(ProductionQueries.Potential(State, Content, settlement).Select(p => p.View).SingleOrDefault(v => v.Key == key) ?? view);
    }
    public Lookup<JobView> FindJob(JobId id) => throw new NotImplementedException("FindJob 尚未实现。");
    public Lookup<CombatView> FindCombat(CombatId id) => throw new NotImplementedException("FindCombat 尚未实现。");
    public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id)
        => throw new NotImplementedException("FindCreatureGroup 尚未实现。");
    public Lookup<CreatureView> FindCreature(CreatureId id) => throw new NotImplementedException("FindCreature 尚未实现。");
    public Lookup<LairView> FindLair(LairId id) => throw new NotImplementedException("FindLair 尚未实现。");
    public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request)
    {
        InventoryCommands.Validate(request);
        try { return new CoreResult<TradeQuote>.Success(InventoryCommands.Quote(State, Content, Position.CompletedSteps, Revision, request)); }
        catch (Exception exception)
        {
            return new CoreResult<TradeQuote>.Faulted(new("internal_error", Position, nameof(QuoteTrade), "", null),
                new(exception.Message, exception.StackTrace));
        }
    }
    public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request)
    {
        VehicleTrades.Validate(request);
        try { return new CoreResult<VehicleTradeQuote>.Success(VehicleTrades.Quote(State, Content, Revision, request)); }
        catch (Exception exception)
        {
            return new CoreResult<VehicleTradeQuote>.Faulted(new("internal_error", Position, nameof(QuoteVehicle), "", null),
                new(exception.Message, exception.StackTrace));
        }
    }
    public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request)
    {
        InventoryLeases.Validate(request);
        try { return new CoreResult<LeaseQuote>.Success(InventoryLeases.Quote(State, Content, Position.CompletedSteps, Revision, request)); }
        catch (Exception exception)
        {
            return new CoreResult<LeaseQuote>.Faulted(new("internal_error", Position, nameof(QuoteLease), "", null),
                new(exception.Message, exception.StackTrace));
        }
    }
    public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        => throw new NotImplementedException("GetJobQuote 尚未实现。");
    public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node)
        => throw new NotImplementedException("PreviewSettlement 尚未实现。");
}

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Train.Core.Contracts;

namespace Train.Cli;

internal sealed record RunOutput(int ProcessId,
    [property: JsonRequired] RuntimeIdentity Runtime,
    [property: JsonRequired] CoreInputId InputIdentity,
    [property: JsonRequired] ImmutableArray<CoreCheckpoint> Checkpoints, int AcceptedCommands, int RejectedCommands,
    bool ReplayVerified, ImmutableArray<EconomySample> Samples, ImmutableArray<RecipeReceipt> CommandReceipts,
    ImmutableArray<GoodsTransferSample> GoodsTransfers, ImmutableArray<ProductionCompletedSample> ProductionCompletions,
    ImmutableArray<ProductionStateSample> ProductionStateChanges);

internal sealed record RecipeReceipt(int CommandIndex, string Kind, string Decision,
    ImmutableArray<ProblemSample> Problems, long? QuotedTotal);
internal sealed record ProblemSample(string Code, string Field, ImmutableArray<string> RelatedIdentities)
{
    internal static ProblemSample From(CoreProblem problem) => new(problem.Code, problem.Field, problem.RelatedIdentities);
}

internal sealed record GoodSample(string Kind, string? Definition = null, string? Composition = null)
{
    internal static GoodSample From(GoodId good) => good switch
    {
        GoodId.Voxel voxel => new("voxel", voxel.Definition.Value),
        GoodId.Composite composite => new("composite", composite.Definition.Value),
        GoodId.Assembly assembly => new("assembly", Composition: assembly.Composition.Value),
        _ => throw new InvalidOperationException("未知货物身份。")
    };
}

internal sealed record RuleSample(int StopIndex, int RuleIndex, string Action, GoodSample Good,
    long RequestedQuantity, long CompletedQuantity, long ActualAmount, ImmutableArray<ProblemSample> IncompleteReasons);
internal sealed record RunTotalsSample(SimulationPosition StartedAt, long SalesReceipts, long PurchasePayments,
    long ServicePayments, long NetCashFlow, long TransportedUnits, double? LoadedCapacityRatio,
    ImmutableArray<RuleSample> IncompleteRules)
{
    internal static RunTotalsSample From(RunAccumulatorView run) => new(run.StartedAt,
        run.SalesReceipts.Value, run.PurchasePayments.Value, run.ServicePayments.Value,
        run.NetCashFlow, run.TransportedUnits, run.LoadedCapacityRatio,
        run.IncompleteRules.Select(rule => new RuleSample(rule.StopIndex, rule.RuleIndex,
            rule.Action.ToString(), GoodSample.From(rule.Good), rule.RequestedQuantity,
            rule.CompletedQuantity, rule.ActualAmount.Value,
            rule.IncompleteReasons.Select(ProblemSample.From).ToImmutableArray())).ToImmutableArray());
}

internal sealed record RunReportSample(ulong Train, SimulationPosition EndedAt, string Reason, RunTotalsSample Totals)
{
    internal static RunReportSample From(RunReport report) => new(report.Train.Value, report.EndedAt,
        report.Reason.ToString(), RunTotalsSample.From(report.Totals));
}

internal sealed record TrainPositionSample(string Kind, ulong Id, double DistanceMeters, string? Phase = null,
    int? Track = null, int? PlatformNumber = null, string? Direction = null,
    ulong? EnteredFrom = null, int? EnteredTrack = null, ulong? NextEdge = null, int? NextTrack = null)
{
    internal static TrainPositionSample From(TrainPosition position) => position switch
    {
        TrainPosition.Node node => new("node", node.Id.Value, node.DistanceMeters, node.Phase.ToString(),
            PlatformNumber: node.PlatformNumber, EnteredFrom: node.EnteredFrom?.Value, EnteredTrack: node.EnteredTrack,
            NextEdge: node.NextEdge?.Value, NextTrack: node.NextTrack),
        TrainPosition.Edge edge => new("edge", edge.Id.Value, edge.DistanceMeters,
            Track: edge.Track, Direction: edge.Direction.ToString()),
        _ => throw new InvalidOperationException("未知列车位置。")
    };
}

internal sealed record VehicleConditionSample(ulong Id, long StructureCurrent, long StructureMaximum,
    int DamagedParts, int DestroyedParts);
internal sealed record TrainSample(ulong Id, TrainPositionSample Position, string Activity, ulong Inventory, RunTotalsSample? CurrentRun,
    ImmutableArray<RunReportSample> RecentReports, ImmutableArray<VehicleConditionSample> Vehicles);
internal sealed record NodeOccupantSample(ulong Id, string Occupant, ulong? Subject);
internal sealed record SettlementSample(ulong Id, ulong Node, string Relation, bool Surrendered, ulong Inventory,
    ImmutableArray<ulong> VehiclesForSale, ImmutableArray<ulong> CommissionJobs,
    ImmutableArray<MarketGoodSample> MarketGoods, ImmutableArray<FacilitySample> Facilities,
    long Residents, ImmutableArray<PopulationNeedSample> PopulationNeeds);
internal sealed record LeaseSample(ulong Id, ulong Settlement, ulong Inventory, ulong ExpiresAtStep, bool Expired);
internal sealed record GoodsSample(GoodSample Good, long Quantity)
{
    internal static GoodsSample From(GoodsQuantity goods) => new(GoodSample.From(goods.Good), goods.Quantity);
}
internal sealed record MarketGoodSample(GoodSample Good, long Stock, long RestockTarget, long ReservedForUse,
    long SellableQuantity, long ReceivableQuantity, ImmutableArray<ProblemSample> Blockers);
internal sealed record FacilityKeySample(ulong Building, string Capability)
{
    internal static FacilityKeySample From(FacilityKey key) => new(key.Building.Value, key.Capability.ToString());
}
internal sealed record ProcessSample(string Kind, string Definition)
{
    internal static ProcessSample From(ProcessRef process) => process switch
    {
        ProcessRef.Production production => new("production", production.Definition.Value),
        ProcessRef.Recovery recovery => new("recovery", recovery.Definition.Value),
        _ => throw new InvalidOperationException("未知生产流程。")
    };
}
internal sealed record GoodsRateSample(GoodSample Good, double UnitsPerSecond);
internal sealed record FacilitySample(FacilityKeySample Key, ProcessSample? Process, string State,
    double ActualWorkPerSecond, ImmutableArray<string> Limits, ImmutableArray<GoodsSample> OutputBuffer,
    ImmutableArray<GoodsRateSample> PotentialConsumption, ImmutableArray<ProblemSample> Blockers);
internal sealed record PopulationNeedSample(string Kind, double RequiredPerSecond, double AccumulatedRequired,
    double AccumulatedSatisfied, double? SatisfactionRatio);
internal sealed record GoodsTransferSample(SimulationPosition Position, int Ordinal, string Action, GoodSample Good,
    long Quantity, ulong Source, ulong Destination, long ActualAmount, ulong Train, ulong Settlement);
internal sealed record ProductionCompletedSample(SimulationPosition Position, int Ordinal, FacilityKeySample Facility,
    ProcessSample Process, ImmutableArray<GoodsSample> Outputs, bool Buffered);
internal sealed record ProductionStateSample(SimulationPosition Position, int Ordinal, FacilityKeySample Facility,
    string Before, string After, ImmutableArray<ProblemSample> Reasons);
internal sealed record InventorySample(ulong Id, long Capacity, long Used, long Available, string Access,
    ImmutableArray<GoodsSample> Goods);
internal sealed record CommissionJobSample(ulong Id, string Kind, string State, int Priority, long FrozenTotalWork,
    double ProgressWork, double Progress, double ActualWorkPerSecond, ImmutableArray<string> Limits,
    ImmutableArray<GoodsSample> InvestedGoods, ImmutableArray<ProblemSample> Blockers)
{
    internal static CommissionJobSample From(ConstructionJobView job) => new(job.Id.Value, job.Kind.ToString(), job.State.ToString(),
        job.Priority, job.FrozenTotalWork, job.ProgressWork, job.Progress, job.ActualWorkPerSecond,
        job.Limits.Select(limit => limit.ToString()).ToImmutableArray(),
        job.InvestedGoods.Select(goods => new GoodsSample(GoodSample.From(goods.Good), goods.Quantity)).ToImmutableArray(),
        job.Blockers.Select(ProblemSample.From).ToImmutableArray());
}

internal sealed record EconomySample(SimulationPosition Position, long Wallet, ImmutableArray<TrainSample> Trains,
    ImmutableArray<NodeOccupantSample> Nodes, ImmutableArray<SettlementSample> Settlements, ImmutableArray<LeaseSample> Leases,
    ImmutableArray<InventorySample> Inventories, long UnassignedServicePayments,
    ImmutableArray<CommissionJobSample> CommissionJobs, ImmutableSortedDictionary<string, double> Counts)
{
    internal static EconomySample Read(ICoreReadView view)
    {
        var trains = view.World.Trains.OrderBy(train => train.Id.Value)
            .Select(train => Found(view.FindTrain(train.Id))).ToArray();
        var settlements = view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .OrderBy(settlement => settlement.Id.Value).Select(settlement => Found(view.FindSettlement(settlement.Id))).ToArray();
        var leases = settlements.SelectMany(settlement => settlement.Leases).Distinct().OrderBy(lease => lease.Value)
            .Select(lease => Found(view.FindLease(lease))).ToArray();
        var inventories = trains.Select(train => train.Inventory).Concat(settlements.Select(settlement => settlement.Inventory))
            .Concat(leases.Select(lease => lease.Inventory)).Distinct().OrderBy(inventory => inventory.Value)
            .Select(inventory => Found(view.FindInventory(inventory)));
        return new(view.Position, view.Economy.Wallet.Value,
            trains.Select(train => new TrainSample(train.Id.Value, TrainPositionSample.From(train.Position), train.Activity.ToString(), train.Inventory.Value,
                train.CurrentRun is { } run ? RunTotalsSample.From(run) : null,
                train.RecentReports.Select(RunReportSample.From).ToImmutableArray(),
                train.Vehicles.Select(vehicle => Condition(view, vehicle)).ToImmutableArray())).ToImmutableArray(),
            view.World.Nodes.OrderBy(node => node.Id.Value).Select(node => new NodeOccupantSample(node.Id.Value,
                node.Occupant switch
                {
                    NodeOccupant.Settlement => "settlement",
                    NodeOccupant.Lair => "lair",
                    _ => "empty"
                },
                node.Occupant switch
                {
                    NodeOccupant.Settlement settlement => settlement.Id.Value,
                    NodeOccupant.Lair lair => lair.Id.Value,
                    _ => null
                })).ToImmutableArray(),
            settlements.Select(settlement => new SettlementSample(settlement.Id.Value, settlement.Node.Value,
                settlement.Relation.ToString(), settlement.Surrendered,
                settlement.Inventory.Value, settlement.VehiclesForSale.Select(vehicle => vehicle.Value).ToImmutableArray(),
                settlement.CommissionJobs.Select(job => job.Value).ToImmutableArray(),
                settlement.Market.Goods.Select(good => new MarketGoodSample(GoodSample.From(good.Good), good.Stock,
                    good.RestockTarget, good.ReservedForUse, good.SellableQuantity, good.ReceivableQuantity,
                    good.Blockers.Select(ProblemSample.From).ToImmutableArray())).ToImmutableArray(),
                settlement.Facilities.Select(facility => new FacilitySample(FacilityKeySample.From(facility.Key),
                    facility.Process is null ? null : ProcessSample.From(facility.Process), facility.State.ToString(),
                    facility.ActualWorkPerSecond, facility.Limits.Select(limit => limit.ToString()).ToImmutableArray(),
                    facility.OutputBuffer.Select(GoodsSample.From).ToImmutableArray(),
                    facility.PotentialConsumption.Select(rate => new GoodsRateSample(GoodSample.From(rate.Good), rate.UnitsPerSecond)).ToImmutableArray(),
                    facility.Blockers.Select(ProblemSample.From).ToImmutableArray())).ToImmutableArray(),
                settlement.Population.Residents, Needs(settlement.Population.Execution).Select(need =>
                    new PopulationNeedSample(need.Kind.ToString(), need.RequiredPerSecond, need.AccumulatedRequired,
                        need.AccumulatedSatisfied, need.SatisfactionRatio)).ToImmutableArray())).ToImmutableArray(),
            leases.Select(lease => new LeaseSample(lease.Id.Value, lease.Settlement.Value, lease.Inventory.Value,
                lease.ExpiresAtStep, lease.Expired)).ToImmutableArray(),
            inventories.Select(inventory => new InventorySample(inventory.Id.Value, inventory.Capacity,
                inventory.Used, inventory.Available, inventory.Access.ToString(),
                inventory.Stacks.Select(stack => new GoodsSample(GoodSample.From(stack.Good), stack.Quantity)).ToImmutableArray()))
                .ToImmutableArray(), view.Economy.UnassignedServicePayments.Value,
            settlements.SelectMany(settlement => settlement.CommissionJobs).OrderBy(job => job.Value)
                .Select(job => CommissionJobSample.From(((JobView.Construction)Found(view.FindJob(job))).Value))
                .ToImmutableArray(), WorldCounts.Read(view));
    }

    private static VehicleConditionSample Condition(ICoreReadView view, VehicleId vehicle)
    {
        var instance = Found(view.FindInstance(new InstanceId.Vehicle(vehicle)));
        var damaged = 0;
        var destroyed = 0;
        foreach (var part in instance.Parts)
        {
            if (part.Destroyed) destroyed++;
            else if (part.Durability.Maximum > 0 && part.Durability.EffectiveCurrent < part.Durability.Maximum) damaged++;
        }
        return new(vehicle.Value, instance.Structure.EffectiveCurrent, instance.Structure.Maximum, damaged, destroyed);
    }

    private static ImmutableArray<PopulationNeedView> Needs(PopulationExecution execution) => execution switch
    {
        PopulationExecution.NotStarted initial => initial.CurrentNeeds,
        PopulationExecution.InProgress current => current.Period.Needs,
        PopulationExecution.Disabled disabled => disabled.RetainedPeriod?.Needs ?? [],
        _ => throw new InvalidOperationException("未知人口执行状态。")
    };

    private static T Found<T>(Lookup<T> lookup) where T : notnull => lookup is Lookup<T>.Found found
        ? found.Value : throw new InvalidDataException($"采样对象不存在：{lookup}。");
}

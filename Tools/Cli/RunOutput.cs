using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Train.Core.Contracts;

namespace Train.Cli;

internal sealed record RunOutput(int ProcessId,
    [property: JsonRequired] RuntimeIdentity Runtime,
    [property: JsonRequired] CoreInputId InputIdentity,
    [property: JsonRequired] ImmutableArray<CoreCheckpoint> Checkpoints, int AcceptedCommands, int RejectedCommands,
    bool ReplayVerified, ImmutableArray<EconomySample> Samples, ImmutableArray<RecipeReceipt> CommandReceipts);

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
    int? Track = null, int? PlatformNumber = null, string? Direction = null)
{
    internal static TrainPositionSample From(TrainPosition position) => position switch
    {
        TrainPosition.Node node => new("node", node.Id.Value, node.DistanceMeters, node.Phase.ToString(),
            PlatformNumber: node.PlatformNumber),
        TrainPosition.Edge edge => new("edge", edge.Id.Value, edge.DistanceMeters,
            Track: edge.Track, Direction: edge.Direction.ToString()),
        _ => throw new InvalidOperationException("未知列车位置。")
    };
}

internal sealed record TrainSample(ulong Id, TrainPositionSample Position, string Activity, ulong Inventory, RunTotalsSample? CurrentRun,
    ImmutableArray<RunReportSample> RecentReports);
internal sealed record SettlementSample(ulong Id, ulong Node, ulong Inventory, ImmutableArray<ulong> VehiclesForSale,
    ImmutableArray<ulong> CommissionJobs);
internal sealed record LeaseSample(ulong Id, ulong Settlement, ulong Inventory, ulong ExpiresAtStep, bool Expired);
internal sealed record GoodsSample(GoodSample Good, long Quantity);
internal sealed record InventorySample(ulong Id, long Capacity, long Used, long Available, string Access,
    ImmutableArray<GoodsSample> Goods);
internal sealed record CommissionJobSample(ulong Id, string State, int Priority, long FrozenTotalWork,
    double ProgressWork, double Progress, double ActualWorkPerSecond, ImmutableArray<string> Limits,
    ImmutableArray<GoodsSample> InvestedGoods, ImmutableArray<ProblemSample> Blockers)
{
    internal static CommissionJobSample From(ConstructionJobView job) => new(job.Id.Value, job.State.ToString(),
        job.Priority, job.FrozenTotalWork, job.ProgressWork, job.Progress, job.ActualWorkPerSecond,
        job.Limits.Select(limit => limit.ToString()).ToImmutableArray(),
        job.InvestedGoods.Select(goods => new GoodsSample(GoodSample.From(goods.Good), goods.Quantity)).ToImmutableArray(),
        job.Blockers.Select(ProblemSample.From).ToImmutableArray());
}

internal sealed record EconomySample(SimulationPosition Position, long Wallet, ImmutableArray<TrainSample> Trains,
    ImmutableArray<SettlementSample> Settlements, ImmutableArray<LeaseSample> Leases,
    ImmutableArray<InventorySample> Inventories, long UnassignedServicePayments,
    ImmutableArray<CommissionJobSample> CommissionJobs)
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
                train.RecentReports.Select(RunReportSample.From).ToImmutableArray())).ToImmutableArray(),
            settlements.Select(settlement => new SettlementSample(settlement.Id.Value, settlement.Node.Value,
                settlement.Inventory.Value, settlement.VehiclesForSale.Select(vehicle => vehicle.Value).ToImmutableArray(),
                settlement.CommissionJobs.Select(job => job.Value).ToImmutableArray())).ToImmutableArray(),
            leases.Select(lease => new LeaseSample(lease.Id.Value, lease.Settlement.Value, lease.Inventory.Value,
                lease.ExpiresAtStep, lease.Expired)).ToImmutableArray(),
            inventories.Select(inventory => new InventorySample(inventory.Id.Value, inventory.Capacity,
                inventory.Used, inventory.Available, inventory.Access.ToString(),
                inventory.Stacks.Select(stack => new GoodsSample(GoodSample.From(stack.Good), stack.Quantity)).ToImmutableArray()))
                .ToImmutableArray(), view.Economy.UnassignedServicePayments.Value,
            settlements.SelectMany(settlement => settlement.CommissionJobs).OrderBy(job => job.Value)
                .Select(job => CommissionJobSample.From(((JobView.Construction)Found(view.FindJob(job))).Value))
                .ToImmutableArray());
    }

    private static T Found<T>(Lookup<T> lookup) where T : notnull => lookup is Lookup<T>.Found found
        ? found.Value : throw new InvalidDataException($"采样对象不存在：{lookup}。");
}

using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.World;

namespace Train.Core.Trading;

internal static class RunReports
{
    internal static RunAccumulatorState Begin(SimulationPosition position) => new(position, new(0), new(0), 0, 0, 0, []);

    internal static RunAccumulatorView Read(RunAccumulatorState run) => new(run.StartedAt,
        run.SalesReceipts, run.PurchasePayments, new(0), checked(run.SalesReceipts.Value - run.PurchasePayments.Value),
        run.TransportedUnits, run.CapacityDistance == 0 ? 0 : run.LoadedUnitsDistance / run.CapacityDistance, run.IncompleteRules);

    internal static TrainState End(ref WorldState world, CoreContentProjection content, TrainState train,
        SimulationPosition position, RunEndReason reason, ImmutableArray<CoreEvent>.Builder events)
    {
        if (train.CurrentRun is not { } run) return train;
        var report = new RunReport(train.Id, position, reason, Read(run));
        var reports = world.RecentRunReports.Add(report);
        if (MarketPrices.Rule(world, content) is { } rule && reports.Length > rule.RecentReportLimit)
            reports = reports.RemoveRange(0, reports.Length - rule.RecentReportLimit);
        world = world with { RecentRunReports = reports };
        events.Add(new CoreEvent.RunReported(report));
        return train with { CurrentRun = null };
    }

    internal static TrainState Transferred(TrainState train, TradeAction action, long quantity, Money amount)
    {
        if (train.CurrentRun is not { } run) return train;
        return train with { CurrentRun = run with
        {
            SalesReceipts = new(checked(run.SalesReceipts.Value + (action == TradeAction.Sell ? amount.Value : 0))),
            PurchasePayments = new(checked(run.PurchasePayments.Value + (action == TradeAction.Buy ? amount.Value : 0))),
            TransportedUnits = checked(run.TransportedUnits + (action is TradeAction.Sell or TradeAction.Unload ? quantity : 0))
        } };
    }

    internal static TrainState EnteredEdge(WorldState world, CoreContentProjection content, TrainState train, EdgeMapItem edge, ulong step)
    {
        if (train.CurrentRun is not { } run) return train;
        var inventory = world.Inventories.Single(i => i.Id == train.Inventory);
        var loaded = run.LoadedUnitsDistance + InventoryStorage.Used(inventory) * edge.LengthMeters;
        var capacity = run.CapacityDistance + InventoryStorage.Read(world, content, step, inventory).Capacity * edge.LengthMeters;
        return train with { CurrentRun = run with { LoadedUnitsDistance = loaded, CapacityDistance = capacity } };
    }
}

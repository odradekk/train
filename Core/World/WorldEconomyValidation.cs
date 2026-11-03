using Train.Core.Contracts;

namespace Train.Core.World;

internal static partial class WorldStateValidation
{
    private static void ValidateEconomy(WorldState world)
    {
        const string field = "/world/cash_flows";
        Items(world.CashFlows, field);
        foreach (var entry in world.CashFlows)
        {
            Require(Enum.IsDefined(entry.Kind), field);
            var receipt = entry.Kind is CashFlowKind.GoodsSale or CashFlowKind.VehicleSale or CashFlowKind.LairReward;
            Require(receipt ? entry.Amount > 0 : entry.Amount < 0, field + "/amount");
            Require(entry.Settlement is null || entry.Settlement.Value.Value > 0 && entry.Settlement.Value.Value < world.Next.Settlement,
                field + "/settlement");
            Require(entry.Train is null || entry.Train.Value.Value > 0 && entry.Train.Value.Value < world.Next.Train, field + "/train");
            if (entry.Kind is CashFlowKind.GoodsSale or CashFlowKind.GoodsPurchase)
            {
                Require(entry.Good is not null && entry.Quantity > 0 && entry.Settlement is not null && entry.Train is not null, field);
                Good(entry.Good!, field + "/good");
            }
            else Require(entry.Good is null && entry.Quantity == 0, field);
            if (entry.Kind == CashFlowKind.LairReward)
                Require(entry.Settlement is null && entry.Train is null, field);
        }
        foreach (var settlement in world.Settlements)
            ProductionGoods(settlement.TodayProduction, "/world/settlements/today_production");
    }

    private static void ValidateCashFlowBoundary(WorldState world, SimulationPosition boundary)
    {
        SimulationPosition? previous = null;
        foreach (var entry in world.CashFlows)
        {
            Require(NotAfter(entry.Position, boundary) && (previous is null || NotAfter(previous.Value, entry.Position)), "/world/cash_flows/position");
            previous = entry.Position;
        }
    }

    private static bool NotAfter(SimulationPosition a, SimulationPosition b)
        => a.CompletedSteps < b.CompletedSteps || a.CompletedSteps == b.CompletedSteps && a.Sequence <= b.Sequence;

    private static void RunTotals(RunAccumulatorView totals, string field)
    {
        Require(totals is not null && totals.SalesReceipts.Value >= 0 && totals.PurchasePayments.Value >= 0
            && totals.ServicePayments.Value == 0 && totals.NetCashFlow == totals.SalesReceipts.Value - totals.PurchasePayments.Value
            && totals.TransportedUnits >= 0
            && (totals.LoadedCapacityRatio is not { } ratio || double.IsFinite(ratio) && ratio >= 0), field);
        Items(totals.IncompleteRules, field + "/incomplete_rules");
        (int Stop, int Rule) previous = (-1, -1);
        foreach (var result in totals.IncompleteRules)
        {
            Require(result.StopIndex >= 0 && result.RuleIndex >= 0 && (result.StopIndex, result.RuleIndex).CompareTo(previous) > 0
                && Enum.IsDefined(result.Action) && result.RequestedQuantity >= 0 && result.CompletedQuantity >= 0
                && result.CompletedQuantity <= result.RequestedQuantity && result.ActualAmount.Value >= 0, field + "/incomplete_rules");
            Require(result.Action is TradeAction.Buy or TradeAction.Sell || result.ActualAmount.Value == 0,
                field + "/incomplete_rules/actual_amount");
            Good(result.Good, field + "/incomplete_rules/good");
            Items(result.IncompleteReasons, field + "/incomplete_rules/incomplete_reasons");
            Require(!result.IncompleteReasons.IsEmpty, field + "/incomplete_rules/incomplete_reasons");
            foreach (var problem in result.IncompleteReasons)
            {
                Require(!string.IsNullOrEmpty(problem.Code) && problem.Field is not null, field + "/incomplete_rules/incomplete_reasons");
                Items(problem.RelatedIdentities, field + "/incomplete_rules/incomplete_reasons/related_identities");
            }
            previous = (result.StopIndex, result.RuleIndex);
        }
    }

    internal static void ValidateBoundary(WorldState world, SimulationPosition boundary)
    {
        ValidateCashFlowBoundary(world, boundary);
        var completedSteps = boundary.CompletedSteps;
        foreach (var job in world.FoundingJobs)
            Require(job.ElapsedTicks <= completedSteps, "/world/founding_jobs/elapsed_ticks");
        foreach (var job in world.RailwayJobs)
            Require(job.ElapsedTicks <= completedSteps, "/world/railway_jobs/elapsed_ticks");
        foreach (var report in world.RecentRunReports)
            Require(NotAfter(report.EndedAt, boundary), "/world/recent_run_reports/ended_at");
        foreach (var train in world.Trains)
            Require(train.CurrentRun is not { } run || NotAfter(run.StartedAt, boundary), "/world/trains/current_run/started_at");
        foreach (var state in world.ResourceRegenerations)
        {
            var interval = WorldResources.IntervalSteps(world.Source.Configuration.Resources.Single(e => e.Good == state.Good).RegenerationIntervalSeconds);
            Require(interval is not { } period
                ? state.Sequence == 0
                : state.Sequence <= completedSteps / period,
                "/world/resource_regenerations");
        }
        foreach (var train in world.Trains)
            Require(train.QueueEnteredStep is null || train.QueueEnteredStep <= completedSteps,
                "/world/trains/queue_entered_step");
        foreach (var combat in world.Combats)
        {
            Require(combat.StartedAtStep > 0 && combat.StartedAtStep <= completedSteps,
                "/world/combats/started_at_step");
            foreach (var controller in combat.Controllers)
                Require(controller.JoinedAtStep >= combat.StartedAtStep && controller.JoinedAtStep <= completedSteps,
                    "/world/combats/controllers/joined_at_step");
            foreach (var creature in combat.Creatures)
                Require(creature.JoinedAtStep >= combat.StartedAtStep && creature.JoinedAtStep <= completedSteps,
                    "/world/combats/creatures/joined_at_step");
        }
        foreach (var group in world.CreatureGroups)
            Require(group.ActivityStartedAtStep <= completedSteps, "/world/creature_groups/activity_started_at_step");
        foreach (var settlement in world.Settlements)
        {
            Require(settlement.PopulationState.LastSettlement is not { } report || report.Position.CompletedSteps <= completedSteps,
                "/world/settlements/population_state/last_settlement/position");
            var previousSettlement = settlement.PopulationState.LastSettlement?.Position.CompletedSteps ?? 0;
            if (settlement.PopulationState.Period is not { } period) continue;
            const string field = "/world/settlements/population_state/period";
            Require(period.LastAdvancedStep >= previousSettlement && period.LastAdvancedStep <= completedSteps,
                field + "/last_advanced_step");
            var sinceAnchor = settlement.PopulationState.Available ? completedSteps - period.LastAdvancedStep : 0;
            Require(sinceAnchor < (ulong)(period.Rule.StatisticsPeriodTicks - period.ElapsedSteps), field + "/elapsed_steps");
            var elapsed = (ulong)period.ElapsedSteps + sinceAnchor;
            Require(elapsed > 0 && elapsed <= completedSteps - previousSettlement, field + "/elapsed_steps");
            if (elapsed < (ulong)period.Rule.ConsumptionIntervalTicks)
                Require(period.Required.All(r => r == 0) && period.Satisfied.All(s => s == 0), field);
        }
    }
}

using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Settlements;

internal static class PopulationQueries
{
    internal static PopulationView Read(WorldState world, CoreContentProjection content, SettlementState settlement, ulong step)
    {
        var inputs = PopulationOperations.ReadInputs(world, content);
        var problems = PopulationOperations.Problems(world, settlement, inputs);
        var state = settlement.PopulationState;
        var current = SettlementQueries.Rule(content, world.Source.Configuration.Rules.Population, inputs.Current?.Revision, "Population");
        var period = state.Period;
        PopulationPeriodView? periodView = null;
        var trend = PopulationTrend.Stable;
        if (period is not null)
        {
            var hasStatistics = period.ElapsedSteps >= period.Rule.ConsumptionIntervalTicks;
            double? basic = hasStatistics ? PopulationOperations.Satisfaction(period, PopulationNeedClass.Basic) : null;
            double? improvement = hasStatistics ? PopulationOperations.Satisfaction(period, PopulationNeedClass.Improvement) : null;
            var elapsed = checked((ulong)period.ElapsedSteps + (state.Available ? step - period.LastAdvancedStep : 0));
            periodView = new(period.Rule.Id, period.Rule.Revision, (ulong)period.Rule.StatisticsPeriodTicks, elapsed,
                period.Rule.Needs.Select((need, i) => new PopulationNeedView(need.Need,
                    settlement.Population * need.AmountPerPersonSecond, period.Required[i], period.Satisfied[i],
                    !hasStatistics ? null : period.Required[i] == 0 ? 1 : Math.Min(1, period.Satisfied[i] / period.Required[i]))).ToImmutableArray(),
                basic, improvement);
            if (basic is { } satisfaction)
            {
                var rate = PopulationOperations.ChangeRate(period.Rule, satisfaction, improvement!.Value);
                trend = rate < 0 ? settlement.Population == 1 ? PopulationTrend.AtMinimum : PopulationTrend.Declining
                    : rate > 0 ? PopulationTrend.Growing : PopulationTrend.Stable;
            }
        }
        PopulationExecution execution = !problems.IsEmpty ? new PopulationExecution.Disabled(periodView, problems)
            : periodView is not null ? new PopulationExecution.InProgress(periodView, trend)
            : new PopulationExecution.NotStarted(inputs.Current!.Needs.Select(need =>
                new PopulationNeedView(need.Need, settlement.Population * need.AmountPerPersonSecond, 0, 0, null)).ToImmutableArray());
        var available = problems.IsEmpty ? settlement.Population * inputs.Current!.LaborRatio : 0;
        var allocated = settlement.ProductionFacilities.Sum(f => f.LastAllocatedLabor)
            + settlement.LastConstructionAllocatedLabor;
        return new(current, settlement.Population, available, allocated, Math.Max(0, available - allocated), execution,
            state.LastSettlement, state.LastSettlement is { } report ? [report] : []);
    }
}

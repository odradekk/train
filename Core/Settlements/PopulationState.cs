using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Settlements;

internal sealed record PopulationConsumptionRemainder(GoodId Good, double Units);

// Idle steps derive progress from the session clock; only consumption and availability transitions move this anchor.
internal sealed record PopulationPeriodState(PopulationRuleProjection Rule, int ElapsedSteps, ulong LastAdvancedStep,
    ImmutableArray<double> Required, ImmutableArray<double> Satisfied);

internal sealed record PopulationState(PopulationPeriodState? Period,
    ImmutableArray<PopulationConsumptionRemainder> ConsumptionRemaining, double ChangeRemaining,
    ImmutableArray<bool> Shortages, PopulationSettlementReport? LastSettlement, bool Available)
{
    internal static PopulationState Initial { get; } = new(null, [], 0, [false, false, false, false, false, false], null, true);
}

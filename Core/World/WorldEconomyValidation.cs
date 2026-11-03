using Train.Core.Contracts;

namespace Train.Core.World;

internal static partial class WorldStateValidation
{
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
}

using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Ui;

internal static class EconomyReportData
{
    internal const int CashFlowBatchSize = 200;

    internal static int CashFlowLimit(int requested, int previousEnd, int count) =>
        count - previousEnd > CashFlowBatchSize ? CashFlowBatchSize : requested;

    internal static (long Income, long Expenses) Totals(ImmutableArray<CashFlowEntry> entries) =>
        (entries.Where(entry => entry.Amount > 0).Sum(entry => entry.Amount),
            entries.Where(entry => entry.Amount < 0).Sum(entry => entry.Amount));

    internal static (long Sales, long Purchases, long ServicesAndRent) SettlementTotals(
        ImmutableArray<CashFlowEntry> entries, SettlementId settlement)
    {
        var local = entries.Where(entry => entry.Settlement == settlement).ToArray();
        return (local.Where(entry => entry.Kind is CashFlowKind.GoodsSale or CashFlowKind.VehicleSale).Sum(entry => entry.Amount),
            local.Where(entry => entry.Kind is CashFlowKind.GoodsPurchase or CashFlowKind.VehiclePurchase).Sum(entry => entry.Amount),
            local.Where(entry => entry.Kind is CashFlowKind.Construction or CashFlowKind.Refit or CashFlowKind.Repair or
                CashFlowKind.Deconstruction or CashFlowKind.Rent).Sum(entry => entry.Amount));
    }

    internal static (ulong Day, long Amount)[] Days(ImmutableArray<CashFlowEntry> entries, ulong completedSteps)
    {
        var today = completedSteps / UiInput.StepsPerDay;
        var first = today > 6 ? today - 6 : 0;
        var totals = entries.Where(entry => entry.Position.CompletedSteps / UiInput.StepsPerDay >= first)
            .GroupBy(entry => entry.Position.CompletedSteps / UiInput.StepsPerDay)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Amount));
        return Enumerable.Range(0, (int)(today - first + 1))
            .Select(index => (Day: first + (ulong)index, Amount: totals.GetValueOrDefault(first + (ulong)index))).ToArray();
    }

    internal static string KindText(CashFlowKind kind) => kind switch
    {
        CashFlowKind.GoodsSale => "卖出货物", CashFlowKind.GoodsPurchase => "买入货物",
        CashFlowKind.VehicleSale => "卖出车辆", CashFlowKind.VehiclePurchase => "买入车辆",
        CashFlowKind.Construction => "建造", CashFlowKind.Refit => "改装", CashFlowKind.Repair => "修理",
        CashFlowKind.Deconstruction => "拆解", CashFlowKind.Rent => "仓库租金", CashFlowKind.LairReward => "清剿奖励",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

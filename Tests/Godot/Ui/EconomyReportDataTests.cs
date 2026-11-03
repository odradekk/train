using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class EconomyReportDataTests
{
    [TestMethod]
    [DataRow(599, 400)]
    [DataRow(600, 400)]
    [DataRow(601, 200)]
    [DataRow(1000, 200)]
    public void A_large_backlog_limits_the_next_refresh_to_one_new_batch(int count, int expected)
    {
        Assert.AreEqual(expected, EconomyReportData.CashFlowLimit(400, 400, count));
        Assert.AreEqual(400, EconomyReportData.CashFlowLimit(400, count, count),
            "没有新记录时，显示更早的记录仍可逐批增加。");
    }

    [TestMethod]
    public void All_cash_flow_categories_contribute_to_global_and_settlement_totals()
    {
        var settlement = new SettlementId(3);
        ImmutableArray<CashFlowEntry> entries =
        [
            Entry(CashFlowKind.GoodsSale, 50, settlement), Entry(CashFlowKind.GoodsPurchase, -20, settlement),
            Entry(CashFlowKind.VehicleSale, 30, settlement), Entry(CashFlowKind.VehiclePurchase, -40, settlement),
            Entry(CashFlowKind.Construction, -7, settlement), Entry(CashFlowKind.Refit, -8, settlement),
            Entry(CashFlowKind.Repair, -9, settlement), Entry(CashFlowKind.Deconstruction, -10, settlement),
            Entry(CashFlowKind.Rent, -11, settlement), Entry(CashFlowKind.LairReward, 12, null)
        ];

        Assert.AreEqual((92L, -105L), EconomyReportData.Totals(entries));
        Assert.AreEqual((80L, -60L, -45L), EconomyReportData.SettlementTotals(entries, settlement));
        Assert.AreEqual((0L, 0L, 0L), EconomyReportData.SettlementTotals(entries, new(4)));
        Assert.AreEqual((0L, 0L), EconomyReportData.Totals([]));
    }

    [TestMethod]
    public void Daily_cash_flow_uses_midnight_and_only_the_latest_seven_game_days()
    {
        const ulong day = 60UL * 60 * 24 * 60;
        ImmutableArray<CashFlowEntry> entries =
        [
            Entry(CashFlowKind.LairReward, 100, null),
            Entry(CashFlowKind.Rent, -200, new(3)) with { Position = new(day, 0) },
            Entry(CashFlowKind.Rent, -30, new(3)) with { Position = new(day * 2, 0) },
            Entry(CashFlowKind.LairReward, 5, null) with { Position = new(day * 8 - 1, 0) },
            Entry(CashFlowKind.LairReward, 17, null) with { Position = new(day * 8, 1) }
        ];

        CollectionAssert.AreEqual(new (ulong, long)[]
        { (2, -30), (3, 0), (4, 0), (5, 0), (6, 0), (7, 5), (8, 17) },
            EconomyReportData.Days(entries, day * 8));
        CollectionAssert.AreEqual(new (ulong, long)[] { (0, 0) }, EconomyReportData.Days([], 0));
    }

    [TestMethod]
    public void Ledger_categories_use_player_facing_names()
    {
        var names = Enum.GetValues<CashFlowKind>().Select(EconomyReportData.KindText).ToArray();
        CollectionAssert.AreEqual(new[] { "卖出货物", "买入货物", "卖出车辆", "买入车辆", "建造", "改装", "修理", "拆解", "仓库租金", "清剿奖励" }, names);
    }

    private static CashFlowEntry Entry(CashFlowKind kind, long amount, SettlementId? settlement) =>
        new(new(0, 0), kind, amount, settlement, null,
            kind is CashFlowKind.GoodsSale or CashFlowKind.GoodsPurchase ? new GoodId.Composite(new("fixture:food")) : null,
            kind is CashFlowKind.GoodsSale or CashFlowKind.GoodsPurchase ? 2 : 0);
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class EconomyReportTextTests
{
    [TestMethod]
    public void RunReportUsesGameTimeAndPreservesTradeAmounts()
    {
        GoodId good = new GoodId.Composite(new("fixture:food"));
        var content = new FixedContentView
        {
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [good] = new ContentLookup<GoodPresentation>.Found(
                    new(good, new(new("粮食", ""), null, null, false), null, false))
            }
        };
        var minute = 60UL * (ulong)SimulationTime.StepsPerSecond;
        var totals = new RunAccumulatorView(new(61 * minute, 0), new(137), new(29), new(5), 103, 7, null,
            [new(0, 0, TradeAction.Buy, good, 10, 7, new(29), [])]);

        var text = EconomyPanels.ReportText(new(new(7), new(1442 * minute, 0), RunEndReason.PlanCompleted, totals), content);

        StringAssert.Contains(text, "第1天 01:01");
        StringAssert.Contains(text, "第2天 00:02");
        Assert.IsFalse(text.Contains("步"), "运行起止时间应使用游戏时间。");
        StringAssert.Contains(text, "销售收入 137");
        StringAssert.Contains(text, "采购支出 29");
        StringAssert.Contains(text, "服务费用：5");
        StringAssert.Contains(text, "净现金流 103");
        StringAssert.Contains(text, "买入 粮食");
        StringAssert.Contains(text, "7/10");
        StringAssert.Contains(text, "实际金额 29");
    }
}

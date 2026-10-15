using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class EconomicQueriesTests
{
    [TestMethod]
    public void MissingProductionQueryStopsReadsAndReportsTheExactEntryOnce()
    {
        var reports = new List<string>();
        var queries = new EconomicQueries(reports.Add);
        Assert.IsFalse(queries.TryRead<int>("QuoteTrade", () => throw new NotImplementedException(), out _));
        Assert.IsFalse(queries.TryRead<int>("FindInventory", () => throw new AssertFailedException("缺失入口之后不能继续读取或给出替代结果。"), out _));
        CollectionAssert.AreEqual(new[] { "核心尚未提供：QuoteTrade" }, reports);
        Assert.AreEqual("QuoteTrade", queries.MissingEntry);
        var reopened = new EconomicQueries(reports.Add);
        Assert.IsTrue(reopened.TryRead("QuoteTrade", () => 731, out var total));
        Assert.AreEqual(731, total);
    }

    [TestMethod]
    public void UnexpectedQueryExceptionsRemainVisible()
    {
        var queries = new EconomicQueries(_ => Assert.Fail("其他异常不能伪装成核心尚未提供。"));
        var expected = new InvalidOperationException("UNCONFIGURED: QuoteTrade");
        var actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
            queries.TryRead<int>("QuoteTrade", () => throw expected, out _));
        Assert.AreSame(expected, actual);
        Assert.IsNull(queries.MissingEntry);
    }
}

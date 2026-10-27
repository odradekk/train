using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class GcReadingTests
{
    [TestMethod]
    public void DifferenceKeepsPauseAndEachGenerationRelativeToStart()
    {
        var start = new GcReading(TimeSpan.FromSeconds(2), [10, 4, 1]);
        var end = new GcReading(TimeSpan.FromSeconds(2.125), [13, 6, 2]);

        var difference = end.Since(start);

        Assert.AreEqual(.125, difference.Pause.TotalSeconds);
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, difference.CollectionsByGeneration);
        CollectionAssert.AreEqual(new[] { 10, 4, 1 }, start.CollectionsByGeneration);
        CollectionAssert.AreEqual(new[] { 13, 6, 2 }, end.CollectionsByGeneration);
    }
}

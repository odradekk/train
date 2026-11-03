using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class RailwayDurationTests
{
    [TestMethod]
    [DataRow(59ul, "59 秒")]
    [DataRow(60ul, "1 分")]
    [DataRow(61ul, "2 分")]
    [DataRow(3600ul, "1 时")]
    [DataRow(90060ul, "1 天 1 时 1 分")]
    public void Duration_uses_seconds_or_days_hours_and_minutes(ulong seconds, string expected)
        => Assert.AreEqual(expected, LineInspector.Duration(seconds * (ulong)SimulationTime.StepsPerSecond));
}

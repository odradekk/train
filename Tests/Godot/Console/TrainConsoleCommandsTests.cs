using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Console;

[TestClass]
public sealed class TrainConsoleCommandsTests
{
    [TestMethod]
    public void FormPreservesVehicleOrderWithoutBusinessValidation()
    {
        Assert.IsTrue(TrainConsoleCommands.TryParse(["FORM", "9", "2", "9"], out var command, out var error));
        Assert.IsNull(error);
        var form = (CoreCommand.FormTrain)command!;
        CollectionAssert.AreEqual(new ulong[] { 9, 2, 9 }, form.OrderedVehicles.Select(id => id.Value).ToArray());
    }

    [TestMethod]
    public void SingleTrainCommandsPreserveId()
    {
        Assert.IsTrue(TrainConsoleCommands.TryParse(["DISBAND", "7"], out var disband, out _));
        Assert.AreEqual(new TrainId(7), ((CoreCommand.DisbandTrain)disband!).Train);
        Assert.IsTrue(TrainConsoleCommands.TryParse(["Start", "7"], out var start, out _));
        Assert.AreEqual(new TrainId(7), ((CoreCommand.StartTrain)start!).Train);
        Assert.IsTrue(TrainConsoleCommands.TryParse(["stop", "7"], out var stop, out _));
        Assert.AreEqual(new TrainId(7), ((CoreCommand.StopTrain)stop!).Train);
    }

    [TestMethod]
    public void PlanPreservesMixedStopsAndConvertsSeconds()
    {
        foreach (var mode in new[] { "ONCE", "Loop" })
        {
            Assert.IsTrue(TrainConsoleCommands.TryParse(
                ["PLAN", "7", mode, "Station:4:3", "WAYPOINT:8", "station:4:0"], out var command, out var error));
            Assert.IsNull(error);
            var plan = (CoreCommand.ReplaceRunPlan)command!;
            Assert.AreEqual(new TrainId(7), plan.Train);
            Assert.AreEqual(mode == "Loop", plan.Plan.Loop);
            Assert.AreEqual(3, plan.Plan.Stops.Length);
            var station = (PlanStop.Station)plan.Plan.Stops[0];
            Assert.AreEqual(new NodeId(4), station.Node);
            Assert.AreEqual(3UL * (ulong)SimulationTime.StepsPerSecond, station.DwellSteps);
            Assert.IsTrue(station.GoodsRules.IsEmpty);
            Assert.AreEqual(new NodeId(8), ((PlanStop.Waypoint)plan.Plan.Stops[1]).Node);
            var last = (PlanStop.Station)plan.Plan.Stops[2];
            Assert.AreEqual(new NodeId(4), last.Node);
            Assert.AreEqual(0UL, last.DwellSteps);
            Assert.IsTrue(last.GoodsRules.IsEmpty);
        }
    }

    [TestMethod]
    public void InvalidSyntaxReturnsNoCommandAndExplainsFailure()
    {
        string[][] invalid =
        [
            [], ["form"], ["form", "0"], ["form", "1", "bad"], ["unknown", "1"],
            ["start"], ["start", "0"], ["start", "1", "extra"], ["disband", "0"], ["stop", "0"],
            ["plan", "0", "once", "waypoint:1"], ["plan", "1", "once"],
            ["plan", "1", "other", "waypoint:1"], ["plan", "1", "once", "waypoint:0"],
            ["plan", "1", "once", "waypoint:1:2"], ["plan", "1", "once", "station:1"],
            ["plan", "1", "once", "station:0:1"], ["plan", "1", "once", "station:1:-1"],
            ["plan", "1", "once", "station:1:1.5"], ["plan", "1", "once", "station:1:+1"],
            ["plan", "1", "once", "station:1:18446744073709551615"],
            ["plan", "1", "once", "station:1:18446744073709551616"],
            ["plan", "1", "once", "unknown:1"], ["form", "18446744073709551616"], ["form", "+1"]
        ];
        foreach (var args in invalid)
        {
            Assert.IsFalse(TrainConsoleCommands.TryParse(args, out var command, out var error), string.Join(' ', args));
            Assert.IsNull(command);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

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
    public void PlanSyntaxMapsMixedStopsAndModes()
    {
        foreach (var mode in new[] { "ONCE", "Loop" })
        {
            Assert.IsTrue(TrainConsoleCommands.TryParse(
                ["PLAN", "7", mode, "Station:4:3", "WAYPOINT:8"], out var command, out var error));
            Assert.IsNull(error);
            var plan = (CoreCommand.ReplaceRunPlan)command!;
            Assert.AreEqual(new TrainId(7), plan.Train);
            Assert.AreEqual(mode == "Loop", plan.Plan.Loop);
            Assert.AreEqual(2, plan.Plan.Stops.Length);
            var station = (PlanStop.Station)plan.Plan.Stops[0];
            Assert.AreEqual(new NodeId(4), station.Node);
            Assert.AreEqual(new NodeId(8), ((PlanStop.Waypoint)plan.Plan.Stops[1]).Node);
        }
    }

    [TestMethod]
    public void InvalidSyntaxReturnsNoCommandAndExplainsFailure()
    {
        string[][] invalid =
        [
            [], ["form"], ["form", "0"], ["form", "1", "bad"], ["unknown", "1"],
            ["start"], ["start", "0"], ["start", "1", "extra"], ["disband", "0"], ["stop", "0"],
            ["plan", "1", "once"],
            ["plan", "1", "other", "waypoint:1"], ["plan", "1", "once", "waypoint:bad"],
            ["plan", "1", "once", "waypoint:1:2"], ["plan", "1", "once", "station:1"],
            ["plan", "1", "once", "unknown:1"], ["form", "18446744073709551616"], ["form", "+1"]
        ];
        foreach (var args in invalid)
        {
            Assert.IsFalse(TrainConsoleCommands.TryParse(args, out var command, out var error), string.Join(' ', args));
            Assert.IsNull(command);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
    }

    [TestMethod]
    public void ExecuteUsesConsoleSourceAndTheCurrentSessionBinding()
    {
        using var fixture = new HostFixture();
        var original = fixture.ReadyHost();
        var commands = new TrainConsoleCommands(fixture.Host, fixture.Logger, error => Assert.Fail(error));
        var before = fixture.Host.GetStatus()!.Binding;
        var replacementView = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input);
        var submittedView = new FixedCoreReadView(new(0, 1), HostCoreScenario.Input);
        var replacement = new ScriptedCoreSession(replacementView);
        var candidate = new ScriptedCoreCandidate(replacement, HostCoreScenario.References);
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(candidate);
        fixture.Load = request => request.Purpose is ContentLoadPurpose.Restore
            ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content))
            : throw new InvalidOperationException("UNCONFIGURED: 控制台替换情景。");
        Assert.IsNull(fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options).Problem);
        fixture.PumpUntil(() => fixture.Host.GetStatus()!.Binding != before);
        replacement.QueueSubmit(new CoreCommand.FormTrain([new(2), new(1)]), CommandSource.Console,
            new SubmissionResult.Completed(new(1, replacementView.Position, submittedView.Position,
                new CommandDecision.Accepted([new WorldObjectId.Train(new(7))], [])), [], null), submittedView);

        commands.Execute(["form", "2", "1"]);

        Assert.AreEqual(0, original.SubmitCount);
        Assert.AreEqual(1, replacement.SubmitCount);
        replacement.AssertComplete();
        fixture.AssertNoUnexpectedFailures();
    }
}

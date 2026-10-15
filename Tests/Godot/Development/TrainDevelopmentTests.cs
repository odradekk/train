using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Development;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Development;

[TestClass]
public sealed class TrainDevelopmentTests
{
    [TestMethod]
    public void ConsoleTrainRejectionStopsConditionAtNewCommandBoundaryOnly()
    {
        foreach (var subject in new WorldObjectId?[] { null, new WorldObjectId.Node(new(7)) })
        {
            using var fixture = new HostFixture();
            var session = fixture.ReadyHost();
            var binding = fixture.Host.GetStatus()!.Binding;
            var errors = new List<string>();
            var commands = new TrainConsoleCommands(fixture.Host, fixture.Logger, errors.Add);
            using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()));
            var clockReads = 0;
            using var tools = new DeveloperTools(fixture.Host, saves, () => clockReads++ * 0.006);
            var old = Rejected(1, new(0, 0), new(0, 1));
            var current = Rejected(2, new(1, 0), new(1, 1));
            // 替身只公布已提交命令的公开录制回执，不验证 Core 的录制实现。
            session.ReadRecordingCall = () => new(session.CurrentView.Position, [],
                session.SubmitCount switch { 0 => [], 1 => [old], _ => [old, current] },
                [], [], [], HostCoreScenario.References);
            session.QueueSubmit(new CoreCommand.StartTrain(new(1)), CommandSource.Console,
                new SubmissionResult.Completed(old.Result, [], null), View(old.Result.After));
            commands.Execute(["start", "1"]);
            Assert.IsNull(tools.StartCondition(new DeveloperCondition.Rejected("train.start", subject)));
            session.QueueAdvance(new(1, new(1, 0), [], null, null), View(new(1, 0)));

            tools.Process();

            Assert.IsTrue(tools.Running, "启用前的列车拒绝回执不能触发条件。");
            Assert.AreEqual(0, tools.StopReasons.Count);
            session.QueueSubmit(new CoreCommand.StartTrain(new(1)), CommandSource.Console,
                new SubmissionResult.Completed(current.Result, [], null), View(current.Result.After));
            commands.Execute(["start", "1"]);

            CollectionAssert.AreEqual(new[] { "匹配命令被拒绝" }, tools.StopReasons.ToArray());
            Assert.AreEqual(current.Result.After, fixture.Host.GetStatus()!.View.Position);
            Assert.AreEqual(binding, fixture.Host.GetStatus()!.Binding);
            var recorded = fixture.Host.ReadRecording(binding)!.Commands;
            Assert.AreSame(current.Result, recorded[^1].Result);
            Assert.AreEqual(CommandSource.Console, recorded[^1].Source);
            Assert.AreSame(current, tools.ReadCommands()!.Commands[^1]);
            tools.Process();
            Assert.IsFalse(tools.Running);
            Assert.AreEqual(1, session.AdvanceCount, "命中命令完整边界后不能继续推进。");
            Assert.AreEqual(2, session.SubmitCount);
            Assert.AreEqual(2, errors.Count);
            fixture.AssertNoUnexpectedFailures();
        }
    }

    private static RecordedCommand Rejected(ulong number, SimulationPosition before, SimulationPosition after)
        => new(CommandSource.Console, "{\"kind\":\"train.start\",\"train\":1}",
            new(number, before, after, new CommandDecision.Rejected(
                [new("no_route", "route", new WorldObjectId.Node(new(7)), [])])),
            HostCoreScenario.Input, HostCoreScenario.Input);

    private static FixedCoreReadView View(SimulationPosition position)
    {
        var node = new NodeView(new(new(7), new(1, 2), new(new("fixture:terrain"), 0, []),
            new NodeOccupant.Empty(), [], 0, 1, null), [], 80, 0, 80);
        return new(position, HostCoreScenario.Input)
        {
            World = new(1, new("fixture:railway"), 100, 100, [], [], [], [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [new(7)] = new Lookup<NodeView>.Found(node) }
        };
    }
}

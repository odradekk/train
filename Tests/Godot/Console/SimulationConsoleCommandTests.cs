using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Console;

[TestClass]
public sealed class SimulationConsoleCommandTests
{
    [TestMethod]
    public void InvalidArgumentsReportUsageBeforeSessionAccess()
    {
        using var fixture = new HostFixture();
        var events = new List<string>();
        var commands = new SimulationConsoleCommands(fixture.Host,
            (_, _, eventId, _, _, _) => events.Add(eventId));
        string[][] invalid =
        [
            ["unknown"], ["status", "extra"], ["pause", "extra"], ["resume", "extra"],
            ["checkpoint", "extra"], ["speed"], ["speed", "3"], ["speed", "01"],
            ["speed", "2", "extra"], ["step", "0"], ["step", "-1"], ["step", "+1"],
            ["step", "1.0"], ["step", " 1"], ["step", "18446744073709551616"], ["step", "1", "extra"]
        ];
        foreach (var arguments in invalid) commands.Execute(arguments);
        Assert.AreEqual(invalid.Length, events.Count);
        Assert.IsTrue(events.All(eventId => eventId == SimulationConsoleCommands.UsageEventId));
        Assert.ThrowsExactly<ArgumentNullException>(() => commands.Execute(null!));
    }

    [TestMethod]
    public void ValidCommandsWithoutSessionReportUnavailable()
    {
        using var fixture = new HostFixture();
        var events = new List<string>();
        var commands = new SimulationConsoleCommands(fixture.Host,
            (_, _, eventId, _, _, _) => events.Add(eventId));
        string[][] valid =
        [
            [], ["STATUS"], ["PAUSE"], ["RESUME"], ["speed", "1"], ["speed", "2"],
            ["speed", "4"], ["speed", "8"], ["step"], ["step", "18446744073709551615"], ["checkpoint"]
        ];
        foreach (var arguments in valid) commands.Execute(arguments);
        Assert.AreEqual(valid.Length, events.Count);
        Assert.IsTrue(events.All(eventId => eventId == SimulationConsoleCommands.SessionUnavailableEventId));
    }

    [TestMethod]
    public void SpeedKeepsPlayerPauseAndResumeOnlyClearsPlayerPause()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var messages = new List<(string EventId, string Message)>();
        var commands = new SimulationConsoleCommands(fixture.Host,
            (_, _, eventId, message, _, _) => messages.Add((eventId, message)));

        commands.Execute(["speed", "4"]);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
        commands.Execute(["resume"]);
        Assert.IsFalse(fixture.Host.GetStatus()!.PlayerPaused);
        commands.Execute(["pause"]);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        commands.Execute(["status"]);
        Assert.AreEqual(SimulationConsoleCommands.StatusEventId, messages[^1].EventId);
        StringAssert.Contains(messages[^1].Message, "玩家暂停=True");
        Assert.IsFalse(messages[^1].Message.Contains("待处理步数", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CheckpointReportsDeclaredPositionAndHash()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var expected = fixture.Capture(new(27, 4));
        session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(expected));
        var events = new List<(LogLevel Level, string EventId, string Message,
            IReadOnlyDictionary<string, object?>? Attributes)>();
        var commands = new SimulationConsoleCommands(fixture.Host,
            (level, _, eventId, message, attributes, _) => events.Add((level, eventId, message, attributes)));
        commands.Execute(["checkpoint"]);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(SimulationConsoleCommands.CheckpointEventId, events[0].EventId);
        Assert.AreEqual(LogLevel.Info, events[0].Level);
        var attributes = events[0].Attributes;
        Assert.IsNotNull(attributes);
        Assert.AreEqual(expected.Checkpoint.Sha256, attributes["sha256"]);
        Assert.AreEqual(expected.Position.CompletedSteps, attributes["completed_steps"]);
        Assert.AreEqual(expected.Position.Sequence, attributes["sequence"]);
    }

    [TestMethod]
    public void StepWhileRunningRequiresPauseWithoutAdvancing()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Write);
        commands.Execute(["resume"]);
        commands.Execute(["step", "3"]);
        Assert.AreEqual(SimulationConsoleCommands.RequiresPauseEventId, fixture.Logs[^1].EventId);
        Assert.AreEqual(0, session.AdvanceCount);
    }

    [TestMethod]
    public void PauseNotificationStopsManualStepsAndReportsActualCountOnce()
    {
        var notifications = 0;
        using var fixture = new HostFixture(_ => { notifications++; return NotificationAction.Pause; });
        var session = fixture.ReadyHost();
        session.QueueAdvance(new(1, new(1, 0),
            [new(new(1, 0), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null, null),
            new FixedCoreReadView(new(1, 0), HostCoreScenario.Input));
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Write);
        commands.Execute(["step", "3"]);
        var log = fixture.Logs[^1];
        Assert.AreEqual(SimulationConsoleCommands.SteppedEventId, log.EventId);
        Assert.AreEqual(3UL, log.Attributes["requested_steps"]);
        Assert.AreEqual(1UL, log.Attributes["steps_advanced"]);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(1, session.AdvanceCount);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
    }

    [TestMethod]
    public void FrozenSessionUsesDiagnosticCheckpointAndCannotResumeOrRepeatFaultLog()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var position = new SimulationPosition(1, 2);
        var fault = new CoreFault("fixture_fault", position, "advance", "inventory", null);
        session.QueueAdvance(new(1, position, [], fault, null),
            new FixedCoreReadView(position, HostCoreScenario.Input, fault));
        var expected = fixture.Capture(position);
        session.SetCapture(CapturePurpose.Diagnostic, new CoreResult<ICoreCapture>.Success(expected));
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Write);
        commands.Execute(["step", "4"]);
        Assert.AreEqual(SimulationConsoleCommands.FrozenEventId, fixture.Logs[^1].EventId);
        StringAssert.Contains(fixture.Logs[^1].Message, "实际推进 1 步");
        StringAssert.Contains(fixture.Logs[^1].Message, "fixture_fault/advance/inventory");
        commands.Execute(["checkpoint"]);
        Assert.AreEqual(SimulationConsoleCommands.CheckpointEventId, fixture.Logs[^1].EventId);
        Assert.AreEqual(expected.Checkpoint.Sha256, fixture.Logs[^1].Attributes["sha256"]);
        Assert.AreEqual(expected.Position.CompletedSteps, fixture.Logs[^1].Attributes["completed_steps"]);
        session.SetCapture(CapturePurpose.Diagnostic, new CoreResult<ICoreCapture>.Failure(
            [new CoreProblem("fixture_capture_failed", "checkpoint", null, [])]));
        commands.Execute(["checkpoint"]);
        Assert.AreEqual(SimulationConsoleCommands.CheckpointFailedEventId, fixture.Logs[^1].EventId);
        StringAssert.Contains(fixture.Logs[^1].Message, "fixture_capture_failed/checkpoint");
        commands.Execute(["resume"]);
        Assert.AreEqual(SimulationConsoleCommands.FrozenEventId, fixture.Logs[^1].EventId);
        Assert.IsTrue(fixture.Host.GetStatus()!.IsFrozen);
        Assert.IsTrue(fixture.Host.GetStatus()!.IsPaused);
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "simulation_fault"));
        Assert.AreEqual(1, session.AdvanceCount);
    }

    [TestMethod]
    public void RefocusAndResumeKeepOtherPauseSources()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        fixture.Host.AutoPauseOnFocusLoss = true;
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Write);
        fixture.Host.SetFocused(false);
        commands.Execute(["resume"]);
        Assert.IsFalse(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.IsTrue(fixture.Host.GetStatus()!.FocusPaused);
        Assert.IsTrue(fixture.Host.GetStatus()!.IsPaused);
        StringAssert.Contains(fixture.Logs[^1].Message, "模拟保持暂停");
        commands.Execute(["pause"]);
        fixture.Host.SetFocused(true);
        Assert.IsFalse(fixture.Host.GetStatus()!.FocusPaused);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.IsTrue(fixture.Host.GetStatus()!.IsPaused);
    }

    [TestMethod]
    public void TechnicalOperationLockRejectsMutationsAsBusy()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var candidate = new ScriptedContentCandidate(fixture.Content, fixture.Content, ContentChange.None);
        fixture.Load = _ =>
        {
            entered.Set();
            if (!release.Wait(900)) throw new AssertFailedException("受控准备门未释放。");
            return new ContentResult<IContentCandidate>.Success(candidate);
        };
        try
        {
            var operation = fixture.Host.BeginReload();
            Assert.IsNull(operation.Problem);
            Assert.IsTrue(entered.Wait(500));
            var commands = new SimulationConsoleCommands(fixture.Host, fixture.Write);
            string[][] mutations = [["resume"], ["pause"], ["speed", "8"], ["step", "2"]];
            foreach (var arguments in mutations)
            {
                commands.Execute(arguments);
                Assert.AreEqual(SimulationConsoleCommands.BusyEventId, fixture.Logs[^1].EventId);
            }
            Assert.IsTrue(fixture.Host.GetStatus()!.OperationLocked);
            Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
            Assert.AreEqual(SimulationSpeed.One, fixture.Host.GetStatus()!.Speed);
            Assert.AreEqual(0, session.AdvanceCount);
            Assert.IsTrue(fixture.Host.Cancel(operation.Operation));
        }
        finally { release.Set(); fixture.PumpUntil(() => candidate.Disposals == 1); }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Development;
using Train.App.Saves;
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
    private HostFixture? _fixture;
    public TestContext TestContext { get; set; } = null!;

    private HostFixture CreateFixture(Func<CoreEvent, NotificationAction>? notification = null) =>
        _fixture = new(notification);

    [TestCleanup]
    public void VerifyHostLogs()
    {
        if (TestContext.CurrentTestOutcome == UnitTestOutcome.Passed)
            _fixture?.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void InvalidArgumentsReportUsageBeforeSessionAccess()
    {
        using var fixture = CreateFixture();
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
        string[][] invalid =
        [
            ["unknown"], ["status", "extra"], ["pause", "extra"], ["resume", "extra"],
            ["checkpoint", "extra"], ["speed"], ["speed", "3"], ["speed", "01"],
            ["speed", "2", "extra"], ["step", "0"], ["step", "-1"], ["step", "+1"],
            ["step", "1.0"], ["step", " 1"], ["step", "18446744073709551616"], ["step", "1", "extra"]
        ];
        foreach (var arguments in invalid) commands.Execute(arguments);
        var events = fixture.Logs;
        Assert.AreEqual(invalid.Length, events.Count);
        Assert.IsTrue(events.All(log => log.EventId == SimulationConsoleCommands.UsageEventId));
        Assert.ThrowsExactly<ArgumentNullException>(() => commands.Execute(null!));
    }

    [TestMethod]
    public void ValidCommandsWithoutSessionReportUnavailable()
    {
        using var fixture = CreateFixture();
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
        string[][] valid =
        [
            [], ["STATUS"], ["PAUSE"], ["RESUME"], ["speed", "1"], ["speed", "2"],
            ["speed", "4"], ["speed", "8"], ["step"], ["step", "18446744073709551615"], ["checkpoint"]
        ];
        foreach (var arguments in valid) commands.Execute(arguments);
        var events = fixture.Logs;
        Assert.AreEqual(valid.Length, events.Count);
        Assert.IsTrue(events.All(log => log.EventId == SimulationConsoleCommands.SessionUnavailableEventId));
    }

    [TestMethod]
    public void SpeedKeepsPlayerPauseAndResumeOnlyClearsPlayerPause()
    {
        using var fixture = CreateFixture();
        fixture.ReadyHost();
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);

        commands.Execute(["speed", "4"]);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
        commands.Execute(["resume"]);
        Assert.IsFalse(fixture.Host.GetStatus()!.PlayerPaused);
        commands.Execute(["pause"]);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        commands.Execute(["status"]);
        Assert.AreEqual(SimulationConsoleCommands.StatusEventId, fixture.Logs[^1].EventId);
        StringAssert.Contains(fixture.Logs[^1].Message, "玩家暂停=True");
        Assert.IsFalse(fixture.Logs[^1].Message.Contains("待处理步数", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CheckpointReportsDeclaredPositionAndHash()
    {
        using var fixture = CreateFixture();
        var session = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input)
        {
            World = new(1, new("fixture:railway"), 100, 100, [], [], [], [], [])
        });
        var prepareRestore = fixture.Core.PrepareRestoreCall;
        fixture.Core.PrepareRestoreCall = (capture, projection, options) =>
        {
            prepareRestore!(capture, projection, options);
            return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(session, HostCoreScenario.References));
        };
        fixture.ReadyHost();
        var expected = fixture.Capture(new(27, 4));
        session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(expected));
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
        var previousCount = fixture.Logs.Count;
        commands.Execute(["checkpoint"]);
        Assert.AreEqual(expected.Checkpoint, tools.Checkpoints.Single().Checkpoint);
        Assert.AreEqual("控制台检查点", tools.Checkpoints.Single().Label);
        var events = fixture.Logs.Skip(previousCount).ToArray();
        Assert.AreEqual(1, events.Length);
        Assert.AreEqual(SimulationConsoleCommands.CheckpointEventId, events[0].EventId);
        Assert.AreEqual(LogLevel.Info, events[0].Level);
        var attributes = events[0].Attributes;
        Assert.IsNotNull(attributes);
        Assert.AreEqual(expected.Checkpoint.Sha256, attributes["sha256"]);
        Assert.AreEqual(expected.Position.CompletedSteps, attributes["completed_steps"]);
        Assert.AreEqual(expected.Position.Sequence, attributes["sequence"]);
    }

    [TestMethod]
    public void OrdinaryCheckpointFailureReportsAllProblemFieldsOnceWithoutRetainingCheckpoint()
    {
        using var fixture = CreateFixture();
        var session = fixture.ReadyHost();
        session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Failure(
            [new("capture_rejected", "checkpoint", null, []), new("input_unavailable", "input", null, [])]));
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
        var previousCount = fixture.Logs.Count;
        commands.Execute(["checkpoint"]);
        var log = fixture.Logs.Skip(previousCount).Single();
        Assert.AreEqual(SimulationConsoleCommands.CheckpointFailedEventId, log.EventId);
        Assert.AreEqual(LogLevel.Warning, log.Level);
        Assert.AreEqual("capture_rejected,input_unavailable", log.Attributes["problem_code"]);
        Assert.AreEqual("checkpoint,input", log.Attributes["problem_field"]);
        Assert.AreEqual(0, tools.Checkpoints.Count);
    }

    [TestMethod]
    public void StepWhileRunningRequiresPauseWithoutAdvancing()
    {
        using var fixture = CreateFixture();
        var session = fixture.ReadyHost();
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
        commands.Execute(["resume"]);
        commands.Execute(["step", "3"]);
        Assert.AreEqual(SimulationConsoleCommands.RequiresPauseEventId, fixture.Logs[^1].EventId);
        Assert.AreEqual(0, session.AdvanceCount);
    }

    [TestMethod]
    public void PauseNotificationStopsManualStepsAndReportsActualCountOnce()
    {
        var notifications = 0;
        using var fixture = CreateFixture(_ => { notifications++; return NotificationAction.Pause; });
        var session = fixture.ReadyHost();
        session.QueueAdvance(new(1, new(1, 0),
            [new(new(1, 0), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null, null),
            new FixedCoreReadView(new(1, 0), HostCoreScenario.Input));
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
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
        using var fixture = CreateFixture();
        var session = fixture.ReadyHost();
        var position = new SimulationPosition(1, 0);
        var fault = new CoreFault("fixture_fault", position, "advance", "inventory", new WorldObjectId.Train(new(7)));
        var diagnostic = new CoreDiagnostic("fixture message", "fixture stack");
        session.QueueAdvance(new(1, position, [], null, null),
            new FixedCoreReadView(position, HostCoreScenario.Input));
        session.QueueAdvance(new(0, position, [], fault, diagnostic),
            new FixedCoreReadView(position, HostCoreScenario.Input, fault)
            { World = new(1, new("fixture:railway"), 100, 100, [], [], [], [], []) });
        var expected = fixture.Capture(position);
        session.SetCapture(CapturePurpose.Diagnostic, new CoreResult<ICoreCapture>.Success(expected));
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
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
        Assert.AreEqual(LogLevel.Warning, fixture.Logs[^1].Level);
        Assert.AreEqual("fixture_capture_failed", fixture.Logs[^1].Attributes["problem_code"]);
        Assert.AreEqual("checkpoint", fixture.Logs[^1].Attributes["problem_field"]);
        Assert.AreEqual(1, tools.Checkpoints.Count);
        session.SetCapture(CapturePurpose.Diagnostic, new CoreResult<ICoreCapture>.Faulted(fault, diagnostic));
        var checkpointFailures = fixture.Logs.Count(log => log.EventId == SimulationConsoleCommands.CheckpointFailedEventId);
        commands.Execute(["checkpoint"]);
        Assert.AreEqual(checkpointFailures + 1, fixture.Logs.Count(log => log.EventId == SimulationConsoleCommands.CheckpointFailedEventId));
        StringAssert.Contains(fixture.Logs[^1].Message, fault.Code);
        var checkpointFailure = fixture.Logs[^1];
        Assert.AreEqual(LogLevel.Warning, checkpointFailure.Level);
        Assert.AreEqual(fault.Code, checkpointFailure.Attributes["fault_code"]);
        Assert.AreEqual(fault.Operation, checkpointFailure.Attributes["operation"]);
        Assert.AreEqual(fault.Field, checkpointFailure.Attributes["field"]);
        Assert.AreEqual(position.CompletedSteps, checkpointFailure.Attributes["fault_completed_steps"]);
        Assert.AreEqual(position.Sequence, checkpointFailure.Attributes["fault_sequence"]);
        Assert.AreEqual(fault.Subject!.ToString(), checkpointFailure.Attributes["subject"]);
        Assert.AreEqual(diagnostic.Message, checkpointFailure.Attributes["diagnostic_message"]);
        Assert.AreEqual(diagnostic.StackTrace, checkpointFailure.Attributes["diagnostic_stack_trace"]);
        Assert.AreEqual(1, tools.Checkpoints.Count);
        commands.Execute(["pause"]);
        Assert.AreEqual(SimulationConsoleCommands.PausedEventId, fixture.Logs[^1].EventId);
        commands.Execute(["speed", "4"]);
        Assert.AreEqual(SimulationConsoleCommands.SpeedChangedEventId, fixture.Logs[^1].EventId);
        Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
        commands.Execute(["step"]);
        Assert.AreEqual(SimulationConsoleCommands.FrozenEventId, fixture.Logs[^1].EventId);
        commands.Execute(["resume"]);
        Assert.AreEqual(SimulationConsoleCommands.FrozenEventId, fixture.Logs[^1].EventId);
        Assert.AreEqual(fault.Code, fixture.Logs[^1].Attributes["fault_code"]);
        Assert.AreEqual(fault.Operation, fixture.Logs[^1].Attributes["operation"]);
        Assert.AreEqual(fault.Field, fixture.Logs[^1].Attributes["field"]);
        var faultLog = fixture.Logs.Single(log => log.EventId == "simulation_fault");
        Assert.AreEqual(LogLevel.Error, faultLog.Level);
        Assert.AreEqual(fault.Code, faultLog.Attributes["fault_code"]);
        Assert.AreEqual(fault.Operation, faultLog.Attributes["operation"]);
        Assert.AreEqual(fault.Field, faultLog.Attributes["field"]);
        Assert.AreEqual(position.CompletedSteps, faultLog.Attributes["fault_completed_steps"]);
        Assert.AreEqual(position.Sequence, faultLog.Attributes["fault_sequence"]);
        Assert.IsNull(faultLog.Attributes["command_id"]);
        Assert.AreEqual(fault.Subject!.ToString(), faultLog.Attributes["subject"]);
        Assert.AreEqual(fixture.Host.GetStatus()!.Binding.Session.ToString(), faultLog.Attributes["run_session"]);
        Assert.AreEqual(diagnostic.Message, faultLog.Attributes["diagnostic_message"]);
        Assert.AreEqual(diagnostic.StackTrace, faultLog.Attributes["diagnostic_stack_trace"]);
        Assert.IsFalse(faultLog.Attributes.ContainsKey("attempted_transaction_id"));
        Assert.IsNull(faultLog.Exception);
        Assert.IsTrue(fixture.Host.GetStatus()!.IsFrozen);
        Assert.IsTrue(fixture.Host.GetStatus()!.IsPaused);
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "simulation_fault"));
        Assert.AreEqual(2, session.AdvanceCount);
    }

    [TestMethod]
    public void RefocusAndResumeKeepOtherPauseSources()
    {
        using var fixture = CreateFixture();
        fixture.ReadyHost();
        fixture.Host.AutoPauseOnFocusLoss = true;
        using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
        using var tools = new DeveloperTools(fixture.Host, saves);
        var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
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
        using var fixture = CreateFixture();
        var session = fixture.ReadyHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var differentContent = new FixedContentView(fixture.Content.Core, new(2));
        var candidate = new ScriptedContentCandidate(differentContent, fixture.Content, ContentChange.Presentation);
        fixture.Load = _ =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "重载准备任务的放行信号未触发");
            return new ContentResult<IContentCandidate>.Success(candidate);
        };
        try
        {
            var operation = fixture.Host.BeginReload(CommandSource.Console);
            Assert.IsNull(operation.Problem);
            HostFixture.WaitForSignal(entered, "重载准备任务未进入");
            using var saves = new SaveService(fixture.Host, new FileSaveStore(Path.GetTempPath()), fixture.CreateEditor());
            using var tools = new DeveloperTools(fixture.Host, saves);
            var commands = new SimulationConsoleCommands(fixture.Host, fixture.Logger, tools);
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
        Assert.AreEqual(0, candidate.Activations);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
    }
}

using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostCoordinatorTests
{
    [TestMethod]
    public void Notification_policy_failure_keeps_committed_events_and_later_notification_actions()
    {
        var calls = 0;
        using var fixture = new HostFixture(_ => ++calls == 1
            ? throw new InvalidOperationException("fixed notification failure") : NotificationAction.Pause);
        fixture.AllowHostFailure("host_notification_failed");
        var session = fixture.ReadyHost();
        var command = new CoreCommand.StopTrain(new(1));
        ImmutableArray<EventEnvelope> events = [new(new(0, 1), 0, new CoreEvent.TrainArrived(new(1), new(1))),
            new(new(0, 1), 1, new CoreEvent.RailBroken(new(1), new(new(1), 0, 2)))];
        session.QueueSubmit(command, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Accepted([], [])), events, null),
            new FixedCoreReadView(new(0, 1), HostCoreScenario.Input));
        fixture.Host.SetPaused(false);
        var received = new List<int>();
        var consumerCalls = 0;
        var observedPause = false;
        fixture.Host.BoundaryCompleted += (_, _, _, batch) =>
        { consumerCalls++; received.AddRange(batch.Select(envelope => envelope.Ordinal)); observedPause = fixture.Host.GetStatus()!.PlayerPaused; };
        var result = fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.UserInterface);
        Assert.IsInstanceOfType<SubmissionResult.Completed>(result);
        Assert.AreEqual(new SimulationPosition(0, 1), fixture.Host.GetStatus()!.View.Position);
        CollectionAssert.AreEqual(new[] { 0, 1 }, received);
        Assert.AreEqual(1, consumerCalls);
        Assert.IsTrue(observedPause);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "host_notification_failed"));
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
    }

    [TestMethod]
    public void Capture_fault_without_a_session_fault_does_not_freeze_or_create_a_reload_operation()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var fault = new CoreFault("capture_failed", new(0, 0), "capture", "state", null);
        session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Faulted(fault, new("fixed", "stack")));
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Faulted>(fixture.Host.CaptureCheckpoint());
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
        Assert.AreEqual("capture_failed", fixture.Host.BeginReload(CommandSource.Console).Problem?.Code);
        Assert.IsFalse(fixture.Host.IsBusy);
        Assert.AreEqual(0, fixture.Logs.Count(log => log.EventId == "simulation_fault"));
        Assert.AreEqual(0, fixture.Logs.Count(log => log.EventId == "simulation_checkpoint_failed"));
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "content_reload_failed"));
        Assert.AreEqual("Faulted", fixture.Logs.Single(log => log.EventId == "content_reload_summary").Attributes["status"]);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(1.0 / 60)]
    public void Reload_completes_in_frame_and_skips_the_next_interval_containing_commit_time(double completionDelta)
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        fixture.Host.SetPaused(false);
        var target = new FixedContentView(fixture.Content.Core, new(2));
        var candidate = new ScriptedContentCandidate(target, fixture.Content, ContentChange.Presentation);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(candidate);
        if (completionDelta > 0)
            for (var i = 1u; i <= 4; i++)
                session.QueueAdvance(new(1, new(i, 0), [], null, null), new FixedCoreReadView(new(i, 0), HostCoreScenario.Input));
        fixture.Host.BeginReload(CommandSource.Console);
        Assert.IsTrue(SpinWait.SpinUntil(() => { fixture.Host.ProcessFrame(completionDelta); return !fixture.Host.IsBusy; }, 900));
        Assert.AreEqual(completionDelta > 0 ? 4 : 0, session.AdvanceCount, "完成帧应使用正常间隔。");
        fixture.Host.ProcessFrame(100);
        Assert.AreEqual(completionDelta > 0 ? 4 : 0, session.AdvanceCount, "下一帧不能追赶提交耗时。");
        Assert.AreSame(target, fixture.Host.Content);
        Assert.AreEqual(1, candidate.Activations);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsPaused);
        Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
    }

    [TestMethod]
    public void Slow_notification_at_one_speed_keeps_the_remaining_steps()
    {
        using var fixture = new HostFixture(_ => NotificationAction.SlowToOne);
        var session = fixture.ReadyHost();
        for (var i = 1u; i <= 2; i++)
            session.QueueAdvance(new(1, new(i, 0), [new(new(i, 0), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null, null),
                new FixedCoreReadView(new(i, 0), HostCoreScenario.Input));
        fixture.Host.SetPaused(false);
        fixture.Host.ProcessFrame(1.0 / 30);
        Assert.AreEqual(2, session.AdvanceCount);
        Assert.AreEqual(2UL, fixture.Host.GetStatus()!.View.Position.CompletedSteps);
        Assert.AreEqual(SimulationSpeed.One, fixture.Host.GetStatus()!.Speed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Pause_wins_over_slow_in_same_boundary_in_both_event_orders(bool reverse)
    {
        using var fixture = new HostFixture(@event => @event is CoreEvent.TrainArrived ? NotificationAction.SlowToOne : NotificationAction.Pause);
        var session = fixture.ReadyHost();
        CoreEvent slow = new CoreEvent.TrainArrived(new(1), new(1));
        CoreEvent pause = new CoreEvent.RailBroken(new(1), new(new(1), 0, 2));
        ImmutableArray<EventEnvelope> events = [new(new(1, 0), 0, reverse ? pause : slow), new(new(1, 0), 1, reverse ? slow : pause)];
        session.QueueAdvance(new(1, new(1, 0), events, null, null), new FixedCoreReadView(new(1, 0), HostCoreScenario.Input));
        fixture.Host.SetSpeed(SimulationSpeed.Eight);
        fixture.Host.SetPaused(false);
        fixture.Host.ProcessFrame(SimulationTime.StepSeconds);
        Assert.AreEqual(1, session.AdvanceCount);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual(SimulationSpeed.Eight, fixture.Host.GetStatus()!.Speed);
    }

    [TestMethod]
    [DataRow("reload", "invalid")]
    [DataRow("reload", "fault")]
    [DataRow("reload", "exception")]
    [DataRow("new", "invalid")]
    [DataRow("restore", "invalid")]
    [DataRow("new", "fault")]
    [DataRow("restore", "fault")]
    [DataRow("new", "exception")]
    [DataRow("restore", "exception")]
    public void Preparation_failure_uses_operation_event_and_logs_each_content_diagnostic_once(string operation, string outcome)
    {
        using var fixture = new HostFixture();
        var eventId = operation == "reload" ? "content_reload_failed" : operation == "new" ? "host_new_game_failed" : "host_restore_failed";
        if (operation != "reload") fixture.AllowHostFailure(eventId);
        fixture.Load = _ => outcome switch
        {
            "invalid" => new ContentResult<IContentCandidate>.Failure([
                new("invalid_one", ContentDiagnosticSeverity.Error, "decode", null, null, "one", null, "one"),
                new("invalid_two", ContentDiagnosticSeverity.Warning, "validate", null, null, "two", null, "two")]),
            "fault" => new ContentResult<IContentCandidate>.Faulted("load_fault", "fixed fault", "fixed stack"),
            _ => throw new InvalidOperationException("fixed preparation exception")
        };
        if (operation == "reload") fixture.Host.BeginReload(CommandSource.Console);
        else if (operation == "new") fixture.Host.BeginNewGame(new("fixture:profile"), 1, "fixed", HostFixture.Options);
        else fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.IsNull(fixture.Host.GetStatus());
        Assert.IsNull(fixture.Host.Content);
        Assert.AreEqual(outcome == "invalid" ? 2 : outcome == "fault" ? 1 : 0, fixture.Logs.Count(log => log.EventId == "content_invalid"));
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == eventId));
        Assert.AreEqual(outcome == "invalid" ? "Rejected" : "Faulted", fixture.Logs.Single(log => log.EventId == eventId).Attributes["status"]);
        Assert.AreEqual(operation == "reload" ? 1 : 0, fixture.Logs.Count(log => log.EventId == "content_reload_summary"));
        Assert.AreEqual(operation == "reload" ? 1 : 0, fixture.Logs.Count(log => log.EventId == "content_reload_failed"));
    }

    [TestMethod]
    [DataRow("world_rejected")]
    [DataRow("world_faulted")]
    [DataRow("core_rejected")]
    public void New_game_failure_before_activation_preserves_running_group(string outcome)
    {
        using var fixture = new HostFixture();
        fixture.AllowHostFailure("host_new_game_failed");
        fixture.ReadyHost();
        var binding = fixture.Host.GetStatus()!.Binding;
        fixture.Host.SetPaused(false);
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        var content = new ScriptedContentCandidate(fixture.Content);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(content);
        fixture.World = (_, _, _, _) => outcome switch
        {
            "world_rejected" => new ContentResult<WorldGenerationInput>.Failure([new("world_invalid", ContentDiagnosticSeverity.Error, "generate", null, null, "profile", null, "fixed")]),
            "world_faulted" => new ContentResult<WorldGenerationInput>.Faulted("world_fault", "fixed", "stack"),
            _ => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World)
        };
        fixture.Core.PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Failure([new("new_rejected", "input", null, [])]);
        fixture.Host.BeginNewGame(new("fixture:profile"), 1, "fixed", HostFixture.Options);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreEqual(binding, fixture.Host.GetStatus()!.Binding);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsPaused);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
        Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
        Assert.AreEqual(1, content.Disposals);
        Assert.AreEqual(0, content.Activations);
        Assert.AreEqual(outcome == "core_rejected" ? 1 : 0, fixture.Core.PrepareNewCount);
        Assert.AreEqual(outcome == "world_faulted" ? "Faulted" : "Rejected",
            fixture.Logs.Single(log => log.EventId == "host_new_game_failed").Attributes["status"]);
    }

    [TestMethod]
    public void New_game_waits_for_content_missing_confirmation_and_applies_current_focus_pause()
    {
        using var fixture = new HostFixture();
        var missing = new MissingContentReference(new ContentReference.Generation(new("fixture:missing")), null, "runtime", []);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content,
            report: new(fixture.Content.Packages, [missing], [], [])));
        fixture.World = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World);
        fixture.Core.PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(fixture.CoreCandidate);
        fixture.Host.AutoPauseOnFocusLoss = true;
        var operation = fixture.Host.BeginNewGame(new("fixture:profile"), 1, "fixed", HostFixture.Options);
        fixture.PumpUntil(() => fixture.Host.MissingConfirmation is not null);
        Assert.IsNull(fixture.Host.GetStatus());
        Assert.AreEqual(0, fixture.CoreCandidate.ActivationCount);
        fixture.Host.SetFocused(false);
        Assert.IsTrue(fixture.Host.ConfirmMissing(operation.Operation));
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.IsTrue(fixture.Host.GetStatus()!.FocusPaused);
        Assert.IsNull(fixture.Host.SetPaused(false));
        Assert.IsTrue(fixture.Host.GetStatus()!.IsPaused);
        Assert.AreEqual(1, fixture.CoreCandidate.ActivationCount);
    }

    [TestMethod]
    public void Host_calls_from_a_different_thread_fail_before_touching_session()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        Exception? observed = null;
        var thread = new Thread(() => { try { fixture.Host.SetPaused(false); } catch (Exception exception) { observed = exception; } });
        thread.Start();
        Assert.IsTrue(thread.Join(500));
        Assert.IsInstanceOfType<InvalidOperationException>(observed);
        StringAssert.Contains(observed.Message, "协调线程");
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual(0, fixture.Session.AdvanceCount);
    }

    [TestMethod]
    public void Default_notification_policy_keeps_speed_and_dispatches_each_step_once()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        for (var step = 1u; step <= 2; step++)
            session.QueueAdvance(new(1, new(step, 0),
                [new(new(step, 0), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null, null),
                new FixedCoreReadView(new(step, 0), HostCoreScenario.Input));
        var positions = new List<ulong>();
        var eventCounts = new List<int>();
        fixture.Host.BoundaryCompleted += (_, view, _, events) =>
        { eventCounts.Add(events.Length); positions.Add(view.Position.CompletedSteps); };
        fixture.Host.SetSpeed(SimulationSpeed.Two);
        fixture.Host.SetPaused(false);
        fixture.Host.ProcessFrame(SimulationTime.StepSeconds);
        CollectionAssert.AreEqual(new ulong[] { 1, 2 }, positions);
        CollectionAssert.AreEqual(new[] { 1, 1 }, eventCounts);
        Assert.AreEqual(SimulationSpeed.Two, fixture.Host.GetStatus()!.Speed);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsPaused);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Accepted_settlement_copies_are_paired_before_consumers_or_stop_on_pairing_failure(bool fail)
    {
        using var fixture = new HostFixture();
        if (fail) fixture.AllowHostFailure("host_pairing_failed");
        var session = fixture.ReadyHost();
        var command = new CoreCommand.FoundSettlement(new(1), new(1));
        ImmutableArray<OwnedSettlementTemplateReference> copies = [new(new(91), new("fixture:template"), [])];
        var paired = new FixedContentView(fixture.Content.Core, new(1), new(2));
        IContentView? copiedBasis = null;
        ImmutableArray<OwnedSettlementTemplateReference> copiedReferences = [];
        var copyCalls = 0;
        fixture.Copies = (basis, actual) =>
        {
            copyCalls++; copiedBasis = basis; copiedReferences = actual;
            if (fail) throw new InvalidOperationException("fixed pairing failure");
            return paired;
        };
        session.QueueSubmit(command, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Accepted([], copies)),
                [new(new(0, 1), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null),
            new FixedCoreReadView(new(0, 1), HostCoreScenario.Input));
        var observations = 0;
        IContentView? observedContent = null;
        fixture.Host.BoundaryCompleted += (_, _, content, _) =>
        { observedContent = content; observations++; };
        var result = fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.UserInterface);
        Assert.IsInstanceOfType<SubmissionResult.Completed>(result);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(((SubmissionResult.Completed)result).Receipt.Decision);
        Assert.AreEqual(fail ? 0 : 1, observations);
        if (!fail) Assert.AreSame(paired, observedContent);
        Assert.AreEqual(1, copyCalls);
        Assert.AreSame(fixture.Content, copiedBasis);
        CollectionAssert.AreEqual(copies.ToArray(), copiedReferences.ToArray());
        Assert.AreSame(fail ? fixture.Content : paired, fixture.Host.Content);
        Assert.AreEqual(fail, fixture.Host.GetStatus()!.IsFrozen);
        if (fail)
        {
            Assert.AreEqual("frozen", fixture.Host.SetPaused(false)?.Code);
            Assert.AreEqual("frozen", fixture.Host.Step(1).Problem?.Code);
            Assert.AreEqual(0, session.AdvanceCount);
            Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "host_pairing_failed"));
        }
    }

    [TestMethod]
    public void Cancel_A_start_B_then_A_returns_without_rebinding_B()
    {
        using var fixture = new HostFixture();
        using var enteredA = new ManualResetEventSlim();
        using var releaseA = new ManualResetEventSlim();
        var sessionA = new ScriptedCoreSession(new FixedCoreReadView(new(11, 0), HostCoreScenario.Input));
        var sessionB = new ScriptedCoreSession(new FixedCoreReadView(new(22, 0), HostCoreScenario.Input));
        var candidateA = new ScriptedCoreCandidate(sessionA, HostCoreScenario.References);
        var candidateB = new ScriptedCoreCandidate(sessionB, HostCoreScenario.References);
        var contentA = new ScriptedContentCandidate(fixture.Content);
        var contentB = new ScriptedContentCandidate(fixture.Content);
        var loads = 0;
        fixture.Load = request => request.Purpose is ContentLoadPurpose.Restore
            ? new ContentResult<IContentCandidate>.Success(Interlocked.Increment(ref loads) == 1 ? contentA : contentB)
            : throw new AssertFailedException("未配置用途。");
        fixture.Core.DecodeStateCall = payload => new CoreResult<ICoreCapture>.Success(new FixedCoreCapture(
            new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References, payload, HostCoreScenario.Input, new(1)));
        fixture.Core.PrepareRestoreCall = (saved, _, _) =>
        {
            if (saved.Encode() == "A")
            {
                enteredA.Set();
                if (!releaseA.Wait(900)) throw new AssertFailedException("A 的准备任务的放行信号未触发。");
                return new CoreResult<ICoreSessionCandidate>.Success(candidateA);
            }
            if (saved.Encode() == "B") return new CoreResult<ICoreSessionCandidate>.Success(candidateB);
            throw new AssertFailedException("未配置载荷。");
        };
        try
        {
            var a = fixture.Host.BeginRestore("A", "content-state", HostFixture.Options);
            Assert.IsTrue(enteredA.Wait(500));
            Assert.IsTrue(fixture.Host.Cancel(a.Operation));
            var b = fixture.Host.BeginRestore("B", "content-state", HostFixture.Options);
            Assert.IsNull(b.Problem);
            fixture.PumpUntil(() => fixture.Host.GetStatus()?.View.Position.CompletedSteps == 22);
            var bindingB = fixture.Host.GetStatus()!.Binding;
            Assert.IsFalse(releaseA.IsSet, "B 必须在 A 返回前激活。");
            releaseA.Set();
            fixture.PumpUntil(() => candidateA.DisposeCount == 1);
            Assert.AreEqual(bindingB, fixture.Host.GetStatus()!.Binding);
            Assert.AreEqual(22UL, fixture.Host.GetStatus()!.View.Position.CompletedSteps);
            Assert.AreEqual(0, candidateA.ActivationCount);
            Assert.AreEqual(1, contentA.Disposals);
            Assert.AreEqual(1, sessionA.DisposeCount);
        }
        finally { releaseA.Set(); }
    }

    [TestMethod]
    public void Command_events_keep_order_and_continue_after_consumer_failure_without_reentry()
    {
        using var fixture = new HostFixture(_ => NotificationAction.Pause);
        fixture.AllowHostFailure("host_consumer_failed");
        var session = fixture.ReadyHost();
        var command = new CoreCommand.StopTrain(new(7));
        var position = new SimulationPosition(0, 1);
        ImmutableArray<EventEnvelope> events = [new(position, 0, new CoreEvent.TrainArrived(new(7), new(1))),
            new(position, 1, new CoreEvent.RailBroken(new(1), new(new(1), 0, 2)))];
        session.QueueSubmit(command, CommandSource.Console,
            new SubmissionResult.Completed(new(1, new(0, 0), position, new CommandDecision.Accepted([], [])), events, null),
            new FixedCoreReadView(position, HostCoreScenario.Input));
        fixture.Host.SetPaused(false);
        var binding = fixture.Host.GetStatus()!.Binding;
        var observed = new List<int>();
        var consumerCalls = 0;
        SimulationPosition? observedPosition = null;
        IContentView? observedContent = null;
        var observedPause = false;
        SubmissionResult? reentryResult = null;
        fixture.Host.BoundaryCompleted += (_, _, _, _) => throw new InvalidOperationException("consumer failed");
        fixture.Host.BoundaryCompleted += (_, view, content, batch) =>
        {
            consumerCalls++; observedPosition = view.Position; observedContent = content;
            observedPause = fixture.Host.GetStatus()!.PlayerPaused;
            observed.AddRange(batch.Select(envelope => envelope.Ordinal));
            reentryResult = fixture.Host.Submit(binding, command, CommandSource.Console);
        };
        Assert.IsInstanceOfType<SubmissionResult.Completed>(fixture.Host.Submit(binding, command, CommandSource.Console));
        CollectionAssert.AreEqual(new[] { 0, 1 }, observed);
        Assert.AreEqual(1, consumerCalls);
        Assert.AreEqual(position, observedPosition);
        Assert.AreSame(fixture.Content, observedContent);
        Assert.IsTrue(observedPause);
        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(reentryResult);
        Assert.AreEqual("busy", ((SubmissionResult.NotAccepted)reentryResult!).Reason.Code);
        Assert.AreEqual(1, session.SubmitCount);
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "host_consumer_failed"));
    }

    [TestMethod]
    public void Each_automatic_step_applies_notification_before_the_next_step()
    {
        using var fixture = new HostFixture(_ => NotificationAction.SlowToOne);
        var session = fixture.ReadyHost();
        session.QueueAdvance(new(1, new(1, 0), [new(new(1, 0), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null, null),
            new FixedCoreReadView(new(1, 0), HostCoreScenario.Input));
        fixture.Host.SetSpeed(SimulationSpeed.Eight);
        fixture.Host.SetPaused(false);
        fixture.Host.ProcessFrame(1.0 / 60);
        Assert.AreEqual(1UL, fixture.Host.GetStatus()!.View.Position.CompletedSteps);
        Assert.AreEqual(SimulationSpeed.One, fixture.Host.GetStatus()!.Speed);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsPaused);
        Assert.AreEqual(1, session.AdvanceCount);
    }

    [TestMethod]
    public void Missing_confirmation_is_specific_to_the_prepared_candidate()
    {
        using var fixture = new HostFixture();
        var missing = new MissingCoreReference(new CoreDefinitionReference.Voxel(new("fixture:missing")), "inventory");
        var candidate = new ScriptedCoreCandidate(fixture.Session, HostCoreScenario.References, [missing]);
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(candidate);
        var first = fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => fixture.Host.MissingConfirmation is not null);
        Assert.IsNull(fixture.Host.GetStatus());
        Assert.AreEqual(first.Operation, fixture.Host.MissingConfirmation!.Value.Operation);
        Assert.IsTrue(fixture.Host.Cancel(first.Operation));
        var secondSession = new ScriptedCoreSession(new FixedCoreReadView(new(2, 0), HostCoreScenario.Input));
        var secondCandidate = new ScriptedCoreCandidate(secondSession, HostCoreScenario.References, [missing]);
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(secondCandidate);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        var second = fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => fixture.Host.MissingConfirmation?.Operation == second.Operation);
        Assert.IsFalse(fixture.Host.ConfirmMissing(first.Operation));
        Assert.AreEqual(0, secondCandidate.ActivationCount);
        Assert.IsTrue(fixture.Host.ConfirmMissing(second.Operation));
        Assert.AreEqual(1, secondCandidate.ActivationCount);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
    }

    [TestMethod]
    public void Operation_lock_reports_busy_and_cancel_restores_settings_without_catchup()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        fixture.Host.SetPaused(false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var abandoned = new ScriptedContentCandidate(new FixedContentView(fixture.Content.Core, new(2)),
            fixture.Content, ContentChange.Presentation);
        fixture.Load = _ =>
        {
            entered.Set();
            if (!release.Wait(900)) throw new AssertFailedException("未释放重载。");
            return new ContentResult<IContentCandidate>.Success(abandoned);
        };
        try
        {
            var op = fixture.Host.BeginReload(CommandSource.Console);
            Assert.IsTrue(entered.Wait(500));
            Assert.IsTrue(fixture.Host.GetStatus()!.OperationLocked);
            Assert.AreEqual("busy", fixture.Host.SetPaused(true)?.Code);
            Assert.AreEqual("busy", fixture.Host.Step(1).Problem?.Code);
            Assert.AreEqual("busy", fixture.Host.BeginReload(CommandSource.Console).Problem?.Code);
            Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(fixture.Host.CaptureCheckpoint());
            Assert.IsTrue(fixture.Host.Cancel(op.Operation));
            Assert.IsFalse(fixture.Host.GetStatus()!.IsPaused);
            Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
            fixture.Host.ProcessFrame(100);
            Assert.AreEqual(0UL, fixture.Host.GetStatus()!.View.Position.CompletedSteps);
            Assert.AreEqual("busy", fixture.Host.BeginReload(CommandSource.Console).Problem?.Code);
        }
        finally { release.Set(); fixture.PumpUntil(() => abandoned.Disposals == 1); }
        Assert.AreEqual(0, abandoned.Activations);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
    }

    [TestMethod]
    [DataRow("accepted")]
    [DataRow("rejected")]
    [DataRow("faulted")]
    [DataRow("not_accepted")]
    public void Business_reload_only_activates_on_acceptance_and_keeps_fault_frozen(string outcome)
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        fixture.Host.SetSpeed(SimulationSpeed.Four);
        fixture.Host.SetPaused(false);
        var target = new FixedContentView(HostContentScenario.CreateProjection(new(new string('B', 64))), new(2));
        var candidate = new ScriptedContentCandidate(target, fixture.Content);
        ContentLoadPurpose? observedPurpose = null;
        fixture.Load = request =>
        {
            observedPurpose = request.Purpose;
            return new ContentResult<IContentCandidate>.Success(candidate);
        };
        var fault = new CoreFault("fixture_fault", new(0, 1), "publish", "content", null);
        CommandDecision decision = outcome switch
        {
            "accepted" => new CommandDecision.Accepted([], []),
            "rejected" => new CommandDecision.Rejected([new("content_missing", "content", null, [])]),
            _ => new CommandDecision.Faulted(fault)
        };
        SubmissionResult result = outcome == "not_accepted" ? new SubmissionResult.NotAccepted(new("busy", "content", null, []))
            : new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), decision), [],
                outcome == "faulted" ? new("fixed diagnostic", "fixed stack") : null);
        session.QueueSubmit(new CoreCommand.PublishContent(target.Core), CommandSource.Console, result,
            new FixedCoreReadView(new(0, outcome == "not_accepted" ? 0u : 1u),
                outcome == "accepted" ? target.Core.Identity : fixture.Content.Core.Identity,
                outcome == "faulted" ? fault : null));
        var observations = 0;
        var observedPairs = new List<(CoreInputId Core, IContentView Content)>();
        fixture.Host.BoundaryCompleted += (_, view, content, _) =>
        {
            observedPairs.Add((view.InputIdentity, content));
            observations++;
        };
        Assert.IsNull(fixture.Host.BeginReload(CommandSource.Console).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreEqual(outcome == "accepted" ? 1 : 0, candidate.Activations);
        Assert.AreSame(outcome == "accepted" ? target : fixture.Content, fixture.Host.Content);
        Assert.AreEqual(outcome == "faulted", fixture.Host.GetStatus()!.IsFrozen);
        Assert.AreEqual(SimulationSpeed.Four, fixture.Host.GetStatus()!.Speed);
        Assert.IsFalse(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual(outcome == "not_accepted" ? 0 : 1, observations);
        foreach (var pair in observedPairs)
        {
            Assert.AreEqual(pair.Content.Core.Identity, pair.Core);
            Assert.AreSame(outcome == "accepted" ? target : fixture.Content, pair.Content);
        }
        Assert.IsInstanceOfType<ContentLoadPurpose.Reload>(observedPurpose);
        Assert.AreSame(fixture.Content, ((ContentLoadPurpose.Reload)observedPurpose!).Basis);
        Assert.AreEqual(outcome == "faulted" ? 1 : 0, fixture.Logs.Count(log => log.EventId == "simulation_fault"));
        Assert.AreEqual(1, candidate.Disposals);
        var summary = fixture.Logs.Single(log => log.EventId == "content_reload_summary");
        Assert.AreEqual(outcome == "accepted", summary.Attributes["changed"]);
        Assert.AreEqual(outcome == "accepted" ? 2UL : 1UL, summary.Attributes["generation"]);
        Assert.AreEqual(outcome == "accepted" ? 0 : 1, fixture.Logs.Count(log => log.EventId == "content_reload_failed"));
    }

    [TestMethod]
    [DataRow(ContentChange.Presentation)]
    [DataRow(ContentChange.None)]
    public void Non_business_reload_never_submits_core_command(ContentChange change)
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var target = change == ContentChange.None ? fixture.Content :
            new FixedContentView(fixture.Content.Core, new(2));
        var candidate = new ScriptedContentCandidate(target, fixture.Content, change);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(candidate);
        fixture.Host.BeginReload(CommandSource.Console);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreSame(change == ContentChange.None ? fixture.Content : target, fixture.Host.Content);
        Assert.AreEqual(change == ContentChange.None ? 0 : 1, candidate.Activations);
        Assert.AreEqual(0, fixture.Session.SubmitCount);
        Assert.AreSame(fixture.Session.CurrentView, fixture.Host.GetStatus()!.View);
        Assert.AreEqual(change != ContentChange.None,
            fixture.Logs.Single(log => log.EventId == "content_reload_summary").Attributes["changed"]);
    }

    [TestMethod]
    public void Menu_reload_only_activates_content_without_a_core_session()
    {
        using var fixture = new HostFixture();
        ContentLoadPurpose? observedPurpose = null;
        fixture.Load = request =>
        {
            observedPurpose = request.Purpose;
            return new ContentResult<IContentCandidate>.Success(fixture.ContentCandidate);
        };
        fixture.Host.BeginReload(CommandSource.Console);
        fixture.PumpUntil(() => fixture.Host.Content is not null);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
        Assert.IsInstanceOfType<ContentLoadPurpose.Initial>(observedPurpose);
        Assert.IsNull(fixture.Host.GetStatus());
        Assert.AreEqual(1, fixture.ContentCandidate.Activations);
        Assert.AreEqual(0, fixture.Core.PrepareRestoreCount);
        Assert.AreEqual(0, fixture.Core.PrepareNewCount);
    }

    [TestMethod]
    public void New_game_pairs_final_template_owners_before_activation_and_uses_owner_thread()
    {
        using var fixture = new HostFixture();
        var owner = Environment.CurrentManagedThreadId;
        var preparationThread = owner;
        ImmutableArray<OwnedSettlementTemplateReference> templates = [new(new(91), new("fixture:template"), [])];
        var references = HostCoreScenario.References with { Templates = templates };
        var candidate = new ScriptedCoreCandidate(fixture.Session, references);
        var paired = new FixedContentView(fixture.Content.Core, new(1), new(2));
        (IContentView View, GenerationProfileId Profile, ulong Seed, string Version)? worldArguments = null;
        (WorldGenerationInput World, CoreContentProjection Projection, CoreSessionOptions Options)? coreArguments = null;
        (IContentView View, ImmutableArray<OwnedSettlementTemplateReference> Copies, int Activations)? copyArguments = null;
        (IContentView Content, int Thread)? observedBoundary = null;
        var boundaryCalls = 0;
        fixture.Load = request => request.Purpose is ContentLoadPurpose.Initial
            ? new ContentResult<IContentCandidate>.Success(fixture.ContentCandidate) : throw new AssertFailedException("用途失配。");
        fixture.World = (view, profile, seed, version) =>
        {
            worldArguments = (view, profile, seed, version);
            return new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World);
        };
        fixture.Core.PrepareNewCall = (world, projection, options) =>
        {
            preparationThread = Environment.CurrentManagedThreadId;
            coreArguments = (world, projection, options);
            return new CoreResult<ICoreSessionCandidate>.Success(candidate);
        };
        fixture.Copies = (view, copies) =>
        {
            copyArguments = (view, copies, candidate.ActivationCount);
            return paired;
        };
        fixture.Host.BoundaryCompleted += (_, _, content, _) =>
        { boundaryCalls++; observedBoundary = (content, Environment.CurrentManagedThreadId); };
        fixture.Host.BeginNewGame(new("fixture:profile"), 1, "fixture-generator", HostFixture.Options);
        fixture.PumpUntil(() => fixture.Host.GetStatus() is not null);
        Assert.AreNotEqual(owner, preparationThread);
        Assert.IsNotNull(worldArguments);
        Assert.AreSame(fixture.Content, worldArguments.Value.View);
        Assert.AreEqual(new GenerationProfileId("fixture:profile"), worldArguments.Value.Profile);
        Assert.AreEqual(1UL, worldArguments.Value.Seed);
        Assert.AreEqual("fixture-generator", worldArguments.Value.Version);
        Assert.IsNotNull(coreArguments);
        Assert.AreSame(HostCoreScenario.World, coreArguments.Value.World);
        Assert.AreSame(fixture.Content.Core, coreArguments.Value.Projection);
        Assert.AreEqual(HostFixture.Options, coreArguments.Value.Options);
        Assert.IsNotNull(copyArguments);
        Assert.AreEqual(0, copyArguments.Value.Activations);
        Assert.AreSame(fixture.Content, copyArguments.Value.View);
        CollectionAssert.AreEqual(templates.ToArray(), copyArguments.Value.Copies.ToArray());
        Assert.AreEqual(1, boundaryCalls);
        Assert.AreSame(paired, observedBoundary!.Value.Content);
        Assert.AreEqual(owner, observedBoundary.Value.Thread);
        Assert.AreEqual(owner, candidate.ActivationThread);
        Assert.AreEqual(1, candidate.ActivationCount);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreSame(paired, fixture.Host.Content);
    }

    [TestMethod]
    public void Preparation_failure_preserves_old_group_and_old_bindings_are_rejected_after_success()
    {
        using var fixture = new HostFixture();
        fixture.AllowHostFailure("host_restore_failed");
        fixture.ReadyHost();
        fixture.Host.SetPaused(false);
        var original = fixture.Host.GetStatus()!.Binding;
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Failure([new("invalid_state", "state", null, [])]);
        var failedContent = new ScriptedContentCandidate(fixture.Content);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(failedContent);
        fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreEqual(original, fixture.Host.GetStatus()!.Binding);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsPaused);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
        Assert.AreEqual(1, failedContent.Disposals);
        var nextSession = new ScriptedCoreSession(new FixedCoreReadView(new(4, 0), HostCoreScenario.Input));
        var next = new ScriptedCoreCandidate(nextSession, HostCoreScenario.References);
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(next);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreNotEqual(original, fixture.Host.GetStatus()!.Binding);
        Assert.IsTrue(fixture.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual(1, fixture.Session.DisposeCount);
        var rejected = fixture.Host.Submit(original, new CoreCommand.StopTrain(new(1)), CommandSource.UserInterface);
        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(rejected);
        Assert.AreEqual("content_changed", ((SubmissionResult.NotAccepted)rejected).Reason.Code);
        Assert.AreEqual(0, nextSession.SubmitCount);
    }

    [TestMethod]
    public void Bad_candidate_activation_cleans_new_session_and_does_not_freeze_old_group()
    {
        using var fixture = new HostFixture();
        fixture.AllowHostFailure("host_activation_failed");
        fixture.ReadyHost();
        fixture.Host.SetPaused(false);
        var binding = fixture.Host.GetStatus()!.Binding;
        var bad = new ReadFailureSession();
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
            new ScriptedCoreCandidate(bad, HostCoreScenario.References));
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreEqual(binding, fixture.Host.GetStatus()!.Binding);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
        Assert.AreEqual(1, bad.Disposals);
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "host_activation_failed"));
    }

    // 故意违规的提供者只验证宿主异常清理，不作为合规契约替身。
    private sealed class ReadFailureSession : ICoreSession
    {
        internal int Disposals { get; private set; }
        public ICoreReadView Read() => throw new InvalidOperationException("故意破坏激活后的读取。");
        public SubmissionResult Submit(CoreCommand command, CommandSource source) => throw new InvalidOperationException("UNCONFIGURED");
        public AdvanceResult Advance(uint steps) => throw new InvalidOperationException("UNCONFIGURED");
        public CoreResult<ICoreCapture> Capture(CapturePurpose purpose) => throw new InvalidOperationException("UNCONFIGURED");
        public CoreRecordingWindow ReadRecording() => throw new InvalidOperationException("UNCONFIGURED");
        public CoreResult<RecordingStart> CaptureRecordingStart() => throw new InvalidOperationException("UNCONFIGURED");
        public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome) => throw new InvalidOperationException("UNCONFIGURED");
        public void Dispose() => Disposals++;
    }
}

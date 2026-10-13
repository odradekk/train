using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostOperationTests
{
    [TestMethod]
    public void Candidate_core_fault_notifies_as_fault_without_rejecting_or_freezing_the_old_group()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        fixture.AllowHostFailure("host_restore_failed");
        var before = fixture.Host.GetStatus()!;
        var fault = new CoreFault("candidate_fault", new(0, 0), "decode", "state", null);
        fixture.Core.DecodeStateCall = _ => new CoreResult<ICoreCapture>.Faulted(fault, new("fixed", "fixed stack"));
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        fixture.Host.BeginRestore("faulting state", "content-state", HostFixture.Options);
        fixture.PumpUntil(() => ended is not null);
        Assert.AreEqual(HostOperationStatus.Faulted, ended!.Status);
        Assert.AreEqual(OperationKind.Restore, ended.Kind);
        Assert.AreEqual(fault, ended.Fault);
        Assert.AreEqual(fault.Code, ended.FaultCode);
        Assert.IsTrue(ended.Problems.IsEmpty);
        Assert.AreEqual(before.Binding, fixture.Host.GetStatus()!.Binding);
        Assert.IsFalse(fixture.Host.GetStatus()!.IsFrozen);
        Assert.IsFalse(fixture.Host.IsBusy);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Candidate_content_fault_keeps_its_fault_code_out_of_rejection_problems()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        fixture.AllowHostFailure("host_new_game_failed");
        fixture.Load = _ => new ContentResult<IContentCandidate>.Faulted("content_fault", "fixed message", "fixed stack");
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        fixture.Host.BeginNewGame(new("fixture"), 1, "fixture", HostFixture.Options);
        fixture.PumpUntil(() => ended is not null);
        Assert.AreEqual(HostOperationStatus.Faulted, ended!.Status);
        Assert.AreEqual("content_fault", ended.FaultCode);
        Assert.IsNull(ended.Fault);
        Assert.IsTrue(ended.Problems.IsEmpty);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Reload_capture_fault_notifies_synchronously_as_a_core_fault()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var fault = new CoreFault("capture_fault", new(0, 0), "capture", "state", null);
        fixture.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Faulted(fault, new("fixed", "fixed stack")));
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        fixture.Host.BeginReload(CommandSource.Console);
        Assert.AreEqual(HostOperationStatus.Faulted, ended!.Status);
        Assert.AreEqual(OperationKind.Reload, ended.Kind);
        Assert.AreEqual(fault, ended.Fault);
        Assert.IsTrue(ended.Problems.IsEmpty);
        Assert.IsFalse(fixture.Host.IsBusy);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Command_fault_is_recorded_once_by_the_host_and_shares_the_display_text()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var fault = new CoreFault("command_fault", new(0, 1), "submit", "train", new WorldObjectId.Train(new(1)));
        var command = new CoreCommand.StopTrain(new(1));
        fixture.Session.QueueSubmit(command, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, new(0, 0), fault.Position, new CommandDecision.Faulted(fault)), [], new("fixed", "fixed stack")),
            new FixedCoreReadView(fault.Position, HostCoreScenario.Input, fault));
        var result = fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.UserInterface);
        var count = fixture.Logs.Count;
        Assert.AreEqual(CommandDiagnostics.Describe(fault), CommandDiagnostics.Report(fixture.Logger, result));
        Assert.AreEqual(count, fixture.Logs.Count);
        Assert.AreEqual(1, fixture.Logs.Count(log => log.EventId == "simulation_fault"));
        Assert.IsFalse(fixture.Logs.Any(log => log.EventId == "train_command_failed"));
        Assert.IsTrue(fixture.Host.GetStatus()!.IsFrozen);
        Assert.AreEqual(1, fixture.Session.SubmitCount);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Reload_capture_rejection_notifies_without_starting_background_work()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var problem = new CoreProblem("capture_rejected", "capture", null, []);
        fixture.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Failure([problem]));
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        var operation = fixture.Host.BeginReload(CommandSource.Console);
        Assert.AreEqual(problem, operation.Problem);
        Assert.AreEqual(OperationKind.Reload, ended!.Kind);
        Assert.AreEqual(HostOperationStatus.Rejected, ended.Status);
        Assert.AreEqual(problem, ended.Problems.Single());
        Assert.IsFalse(fixture.Host.IsBusy);
        fixture.AssertNoUnexpectedFailures();
    }
    [TestMethod]
    public void Async_rejection_notifies_the_ui_with_content_reasons_and_keeps_the_old_group()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var before = fixture.Host.GetStatus()!;
        var diagnostic = new ContentDiagnostic("fixture_missing", ContentDiagnosticSeverity.Error,
            "load", null, null, "profile", null, "生成配置缺失。");
        fixture.Load = _ => new ContentResult<IContentCandidate>.Failure([diagnostic]);
        HostOperationResult? ended = null;
        fixture.Host.OperationEnded += result => ended = result;
        var operation = fixture.Host.BeginNewGame(new("fixture"), 1, "fixture", HostFixture.Options);
        fixture.PumpUntil(() => ended is not null);
        Assert.AreEqual(operation.Operation, ended!.Operation);
        Assert.AreEqual(HostOperationStatus.Rejected, ended.Status);
        Assert.AreEqual(diagnostic, ended.Diagnostics.Single());
        Assert.AreEqual(before.Binding, fixture.Host.GetStatus()!.Binding);
        Assert.IsFalse(fixture.Host.IsBusy);
    }

    [TestMethod]
    public void Successful_restore_notifies_once_and_consumer_failure_does_not_stop_later_consumers()
    {
        using var fixture = new HostFixture();
        fixture.AllowHostFailure("host_operation_consumer_failed");
        fixture.Host.OperationEnded += _ => throw new InvalidOperationException("fixed consumer failure");
        var results = new List<HostOperationResult>();
        fixture.Host.OperationEnded += results.Add;
        fixture.ReadyHost();
        fixture.Host.ProcessFrame(0);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(OperationKind.Restore, results[0].Kind);
        Assert.AreEqual(HostOperationStatus.Committed, results[0].Status);
        Assert.IsNotNull(fixture.Host.GetStatus());
        fixture.AssertNoUnexpectedFailures();
    }
}

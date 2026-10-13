using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostOperationTests
{
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
        Assert.AreEqual("Reload", ended!.Kind);
        Assert.AreEqual("Rejected", ended.Status);
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
        Assert.AreEqual("Rejected", ended.Status);
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
        Assert.AreEqual("Restore", results[0].Kind);
        Assert.AreEqual("Committed", results[0].Status);
        Assert.IsNotNull(fixture.Host.GetStatus());
        fixture.AssertNoUnexpectedFailures();
    }
}

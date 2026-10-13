using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Recording;

[TestClass]
[TestCategory("fast")]
public sealed class HostRecordingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Completed_rejected_and_faulted_publications_retain_the_unactivated_target(bool faulted)
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var target = new FixedContentView(HostContentScenario.CreateProjection(new("target-input")), new(2), new(2));
        var candidate = new ScriptedContentCandidate(target, fixture.Content);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(candidate);
        var fault = new CoreFault("fault", new(0, 1), "publish", "content", null);
        CommandDecision decision = faulted ? new CommandDecision.Faulted(fault) :
            new CommandDecision.Rejected([new("rejected", "content", null, [])]);
        session.QueueSubmit(new CoreCommand.PublishContent(target.Core), CommandSource.Console,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), decision), [], null),
            new FixedCoreReadView(new(0, 1), fixture.Content.Core.Identity, faulted ? fault : null));
        Assert.IsNull(fixture.Host.BeginReload(CommandSource.Console).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        ImmutableArray<IContentView> retained = [];
        fixture.ReplayCapture = (history, _) =>
        {
            retained = history;
            return new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "opaque"));
        };
        var (snapshot, failure) = fixture.Host.CaptureRecording(fixture.Host.GetStatus()!.Binding, Window(fixture));
        Assert.IsNull(failure);
        Assert.IsNotNull(snapshot);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
        Assert.AreEqual(0, candidate.Activations);
        Assert.AreEqual(2, retained.Length);
        Assert.AreSame(fixture.Content, retained[0]);
        Assert.AreSame(target, retained[1]);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Same_input_revisions_and_settlement_copies_are_retained_in_order_until_released()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var revision = new FixedContentView(fixture.Content.Core, new(2), new(2));
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(
            new ScriptedContentCandidate(revision, fixture.Content, ContentChange.Persistent));
        Assert.IsNull(fixture.Host.BeginReload(CommandSource.Console).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        var copied = new FixedContentView(fixture.Content.Core, new(2), new(3));
        fixture.Copies = (basis, _) =>
        {
            Assert.AreSame(revision, basis);
            return copied;
        };
        var command = new CoreCommand.FoundSettlement(new(1), new(2));
        session.QueueSubmit(command, CommandSource.Console,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1),
                new CommandDecision.Accepted([], [new(new(9), new("template"), [])])), [], null),
            new FixedCoreReadView(new(0, 1), fixture.Content.Core.Identity));
        fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.Console);
        ImmutableArray<IContentView> retained = [];
        fixture.ReplayCapture = (history, _) =>
        {
            retained = history;
            return new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "opaque"));
        };
        var binding = fixture.Host.GetStatus()!.Binding;
        void Capture() => Assert.IsNull(fixture.Host.CaptureRecording(binding, Window(fixture)).Failure);
        Capture();
        CollectionAssert.AreEqual(new IContentView[] { fixture.Content, revision, copied }, retained.ToArray());
        fixture.Host.ReleaseRecordingHistory([]);
        Capture();
        Assert.AreEqual(3, retained.Length, "没有 ReleasedInputs 时不能释放旧视图。");
        session.ConfirmRecordingStartCall = (_, _) => new([fixture.Content.Core.Identity], []);
        fixture.Host.BeginRecordingWrite(binding, new(1));
        fixture.Host.ConfirmRecordingStart(binding, new(1), RecordingWriteOutcome.PublishedWithDependencies, [fixture.Content]);
        Capture();
        CollectionAssert.AreEqual(new IContentView[] { fixture.Content, copied }, retained.ToArray());
        fixture.Host.ReleaseRecordingHistory([]);
        Capture();
        Assert.AreEqual(1, retained.Length);
        Assert.AreSame(copied, retained[0], "当前视图不能因 ReleasedInputs 被释放。");
        fixture.AssertNoUnexpectedFailures();
    }

    internal static CoreRecordingWindow Window(HostFixture fixture, ulong point = 1, ulong step = 0)
        => new(new(step, 0), [new(new(point), fixture.Capture(new(step, 0)))], [], [], [], [], HostCoreScenario.References);
}

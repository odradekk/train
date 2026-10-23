using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Recording;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostDesignSubmissionTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Completed_design_command_activates_only_on_acceptance_and_retains_replay_dependencies(int outcome)
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var target = new FixedContentView(fixture.Content.Core, fixture.Content.Generation, new(2));
        var design = Design(fixture.Content.Core.Identity);
        using var candidate = new ScriptedContentCandidate(target, fixture.Content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        var command = new CoreCommand.CommissionVehicleWork(new JobRequest.BuildVehicle(
            new(new(1), CapabilityKind.Construction), design, [], 0), new(100));
        var fault = new CoreFault("construction_fault", new(0, 1), "commission", "design", null);
        CommandDecision decision = outcome switch
        {
            0 => new CommandDecision.Accepted([], []),
            1 => new CommandDecision.Rejected([new("price_condition_failed", "MaximumPayment", null, [])]),
            _ => new CommandDecision.Faulted(fault)
        };
        var completed = new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), decision), [], null);
        session.QueueSubmit(command, CommandSource.UserInterface, completed,
            new FixedCoreReadView(new(0, 1), fixture.Content.Core.Identity, outcome == 2 ? fault : null));
        IContentView? observed = null;
        fixture.Host.BoundaryCompleted += (_, _, content, _) => observed = content;

        var result = fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command,
            CommandSource.UserInterface, candidate);

        Assert.AreSame(completed, result);
        Assert.AreSame(outcome == 0 ? target : fixture.Content, fixture.Host.Content);
        Assert.AreSame(fixture.Host.Content, observed, "事件消费者只能观察完整配对后的内容。");
        Assert.AreEqual(outcome == 0 ? 1 : 0, candidate.Activations);
        ImmutableArray<IContentView> retained = [];
        fixture.ReplayCapture = (history, _) =>
        {
            retained = history;
            return new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "opaque"));
        };
        Assert.IsNull(fixture.Host.CaptureRecording(fixture.Host.GetStatus()!.Binding,
            HostRecordingTests.Window(fixture)).Failure);
        CollectionAssert.AreEqual(new IContentView[] { fixture.Content, target }, retained.ToArray(),
            "拒绝和故障命令也需要未激活目标的录制依赖。");
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Unaccepted_design_command_neither_activates_nor_retains_the_candidate()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var target = new FixedContentView(fixture.Content.Core, new(2), new(2));
        using var candidate = new ScriptedContentCandidate(target, fixture.Content);
        var command = new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(
            new(new(1), CapabilityKind.Construction), Design(fixture.Content.Core.Identity), [], 0));
        var rejected = new SubmissionResult.NotAccepted(new("frozen", "session", null, []));
        session.QueueSubmit(command, CommandSource.UserInterface, rejected, session.CurrentView);

        Assert.AreSame(rejected, fixture.Host.Submit(fixture.Host.GetStatus()!.Binding,
            command, CommandSource.UserInterface, candidate));
        Assert.AreSame(fixture.Content, fixture.Host.Content);
        Assert.AreEqual(0, candidate.Activations);
        fixture.ReplayCapture = (history, _) =>
        {
            CollectionAssert.AreEqual(new IContentView[] { fixture.Content }, history.ToArray());
            return new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "opaque"));
        };
        Assert.IsNull(fixture.Host.CaptureRecording(fixture.Host.GetStatus()!.Binding,
            HostRecordingTests.Window(fixture)).Failure);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Old_binding_or_content_candidate_is_rejected_before_core_submission(bool oldBinding)
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var current = fixture.Host.GetStatus()!;
        var other = new FixedContentView(fixture.Content.Core, fixture.Content.Generation, new(2));
        using var candidate = new ScriptedContentCandidate(other, oldBinding ? fixture.Content : other);
        var command = new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(
            new(new(1), CapabilityKind.Construction), Design(fixture.Content.Core.Identity), [], 0));

        var result = fixture.Host.Submit(oldBinding ? new(Guid.NewGuid()) : current.Binding,
            command, CommandSource.UserInterface, candidate);

        var rejected = (SubmissionResult.NotAccepted)result;
        Assert.AreEqual("content_changed", rejected.Reason.Code);
        Assert.AreEqual(oldBinding ? "host.binding" : "host.content", rejected.Reason.Field);
        Assert.AreEqual(0, session.SubmitCount);
        Assert.AreEqual(0, candidate.Activations);
        Assert.AreSame(fixture.Content, fixture.Host.Content);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Accepted_design_target_keeps_panel_generation_until_reload()
    {
        using var fixture = new HostFixture();
        var session = fixture.ReadyHost();
        var binding = fixture.Host.GetStatus()!.Binding;
        var opened = fixture.Content.Generation;
        Assert.IsTrue(fixture.Host.PanelGenerationCurrent(binding, opened));
        var activated = new FixedContentView(fixture.Content.Core, new(opened.Value + 1));
        using var candidate = new ScriptedContentCandidate(activated, fixture.Content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(Design(fixture.Content.Core.Identity)));
        var command = new CoreCommand.CommissionVehicleWork(new JobRequest.BuildVehicle(
            new(new(1), CapabilityKind.Construction), Design(fixture.Content.Core.Identity), [], 0), new(100));
        session.QueueSubmit(command, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Accepted([], [])), [], null),
            session.CurrentView);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(((SubmissionResult.Completed)fixture.Host.Submit(
            binding, command, CommandSource.UserInterface, candidate)).Receipt.Decision);
        Assert.AreSame(activated, fixture.Host.Content);
        Assert.IsTrue(fixture.Host.PanelGenerationCurrent(binding, opened));
        Assert.IsTrue(fixture.Host.PanelGenerationCurrent(binding, activated.Generation));
        var ordinary = new CoreCommand.StopTrain(new(1));
        session.QueueSubmit(ordinary, CommandSource.UserInterface,
            new SubmissionResult.Completed(new(2, new(0, 1), new(0, 2), new CommandDecision.Accepted([], [])), [], null),
            session.CurrentView);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(((SubmissionResult.Completed)fixture.Host.Submit(
            binding, ordinary, CommandSource.UserInterface)).Receipt.Decision);
        Assert.IsTrue(fixture.Host.PanelGenerationCurrent(binding, opened), "接受普通命令后打开时的世代失效。");
        var reloaded = new FixedContentView(fixture.Content.Core, new(activated.Generation.Value + 1));
        var reload = new ScriptedContentCandidate(reloaded, activated, ContentChange.Presentation);
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(reload);
        Assert.IsNull(fixture.Host.BeginReload(CommandSource.Console).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreSame(reloaded, fixture.Host.Content);
        Assert.IsFalse(fixture.Host.PanelGenerationCurrent(binding, opened));
        Assert.IsTrue(fixture.Host.PanelGenerationCurrent(binding, reloaded.Generation));
        reload.Dispose();
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void Blueprint_save_keeps_the_opened_panel_generation()
    {
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var binding = fixture.Host.GetStatus()!.Binding;
        var opened = fixture.Content.Generation;
        var activated = new FixedContentView(fixture.Content.Core, new(opened.Value + 1));
        using var candidate = new ScriptedContentCandidate(activated, fixture.Content,
            ContentChange.Library | ContentChange.Persistent,
            edit: new PreparedContentEdit.BlueprintSaved(new(BlueprintLibrary.Local, new("published")), new("published-design")));
        fixture.Factory.PrepareEditCall = (basis, edit) =>
        {
            Assert.AreSame(fixture.Content, basis);
            Assert.IsInstanceOfType<ContentEdit.SaveBlueprint>(edit);
            return new ContentResult<IContentCandidate>.Success(candidate);
        };
        var draft = new DesignDraft(new("发布蓝图", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
        Assert.IsNull(fixture.Host.BeginContentEdit(new ContentEdit.SaveBlueprint(
            new BlueprintSaveTarget.New(BlueprintLibrary.Local), draft), _ => new FileWriteResult(true)).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy && ReferenceEquals(fixture.Host.Content, activated));
        Assert.IsTrue(fixture.Host.PanelGenerationCurrent(binding, opened), "保存蓝图后打开时的世代失效。");
        fixture.AssertNoUnexpectedFailures();
    }

    private static DesignInput Design(CoreInputId input) => new(input,
        new(new("service-design"), new("service-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
            [], 10, 10, 1, new(100), [], [new(new GoodId.Voxel(new("fixture:ore")), 1)], 1, [], true, []), []);
}

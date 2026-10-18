using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreSessionTests
{
    private static readonly CoreContentProjection A = Content('A');
    private static readonly CoreContentProjection B = Content('B');
    private static readonly CoreContentProjection C = Content('C');
    private static readonly CoreEvent First = new CoreEvent.TrainArrived(new(11), new(21));
    private static readonly CoreEvent Second = new CoreEvent.TrainArrived(new(12), new(22));
    private static readonly CoreEvent Third = new CoreEvent.TrainArrived(new(13), new(23));

    [TestMethod]
    public void Publishing_different_content_commits_its_projection_and_command_boundary()
    {
        using var session = Session();

        var result = Completed(session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest));

        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        Assert.AreEqual(10UL, result.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 2), result.Receipt.Before);
        Assert.AreEqual(new SimulationPosition(7, 3), result.Receipt.After);
        AssertBoundary(session, new(7, 3), 11, 21, B);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNull(result.Diagnostic);
    }

    [TestMethod]
    public void Consecutive_publications_take_consecutive_numbers_and_sequences()
    {
        using var session = Session();

        var first = Completed(session.Submit(new CoreCommand.PublishContent(B), CommandSource.Console));
        var second = Completed(session.Submit(new CoreCommand.PublishContent(C), CommandSource.UserInterface));

        Assert.AreEqual(10UL, first.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 3), first.Receipt.After);
        Assert.AreEqual(11UL, second.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 3), second.Receipt.Before);
        Assert.AreEqual(new SimulationPosition(7, 4), second.Receipt.After);
        AssertBoundary(session, new(7, 4), 12, 22, C);
    }

    [TestMethod]
    public void Retained_views_do_not_change_when_the_session_publishes_and_advances()
    {
        using var session = Session();
        var retained = session.Read();

        session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest);
        session.Advance(1);

        AssertBoundary(session, new(8, 0), 11, 22, B);
        Assert.AreEqual(new SimulationPosition(7, 2), retained.Position);
        Assert.AreEqual(new CoreRevision(20), retained.Revision);
        Assert.AreEqual(new CoreInputId(new string('A', 64)), retained.InputIdentity);
        Assert.IsNull(retained.Fault);
    }

    [TestMethod]
    public void Rejected_commands_take_a_number_and_sequence_without_changing_content_or_emitting_events()
    {
        using var session = Session();

        var result = Completed(session.ExecuteCommand(new CoreCommand.PublishContent(B), CommandSource.AutomationTest, _ =>
            new CommandEffect.Rejected([new("invalid_state", "test", null, [])])));

        Assert.IsInstanceOfType<CommandDecision.Rejected>(result.Receipt.Decision);
        Assert.AreEqual("invalid_state", ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single().Code);
        Assert.AreEqual(10UL, result.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 2), result.Receipt.Before);
        Assert.AreEqual(new SimulationPosition(7, 3), result.Receipt.After);
        AssertBoundary(session, new(7, 3), 11, 21, A);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNull(result.Diagnostic);
    }

    [TestMethod]
    public void Advancing_resets_the_boundary_sequence_and_does_not_take_command_numbers()
    {
        using var session = Session();

        var result = session.Advance(2);

        Assert.AreEqual(2u, result.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(9, 0), result.Position);
        AssertBoundary(session, new(9, 0), 10, 22, A);
        Assert.IsNull(result.Fault);
        Assert.IsNull(result.Diagnostic);
    }

    [TestMethod]
    public void Queries_and_zero_steps_do_not_change_the_boundary()
    {
        using var session = Session();
        session.Read();

        var result = session.Advance(0);

        Assert.AreEqual(0u, result.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(7, 2), result.Position);
        AssertBoundary(session, new(7, 2), 10, 20, A);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNull(result.Fault);
        Assert.IsNull(result.Diagnostic);
    }

    [TestMethod]
    public void Frozen_sessions_do_not_accept_commands_or_repeat_diagnostics()
    {
        var fault = new CoreFault("internal_error", new(7, 3), "Submit", "", null);
        using var session = new CoreSession(WorldFixture.State(new(7, 2), 10, new(20), A) with { Content = A, Fault = fault });

        var submission = session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest);
        var advancement = session.Advance(1);

        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(submission);
        Assert.AreEqual("frozen", ((SubmissionResult.NotAccepted)submission).Reason.Code);
        Assert.AreEqual(0u, advancement.CompletedSteps);
        Assert.AreEqual(fault, advancement.Fault);
        Assert.IsNull(advancement.Diagnostic);
        AssertBoundary(session, new(7, 2), 10, 20, A, fault);
    }

    [TestMethod]
    [DataRow(ulong.MaxValue, 2UL)]
    [DataRow(10UL, ulong.MaxValue)]
    public void Exhausted_command_numbers_or_sequences_do_not_accept_or_change_state(ulong number, ulong sequence)
    {
        using var session = new CoreSession(WorldFixture.State(new(7, sequence), number, new(20), A) with { Content = A });

        var result = session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest);

        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(result);
        Assert.AreEqual("identity_exhausted", ((SubmissionResult.NotAccepted)result).Reason.Code);
        AssertBoundary(session, new(7, sequence), number, 20, A);
    }

    [TestMethod]
    public void Command_faults_restore_business_and_counters_freeze_and_increment_revision()
    {
        using var session = Session();
        var retained = session.Read();

        var result = Completed(session.ExecuteCommand(new CoreCommand.PublishContent(B), CommandSource.AutomationTest, _ => throw new InvalidOperationException("命令测试故障。")));

        Assert.IsInstanceOfType<CommandDecision.Faulted>(result.Receipt.Decision);
        var fault = ((CommandDecision.Faulted)result.Receipt.Decision).Fault;
        Assert.AreEqual(new CoreFault("internal_error", new(7, 3), "Submit", "", null), fault);
        Assert.AreEqual(10UL, result.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 2), result.Receipt.Before);
        Assert.AreEqual(new SimulationPosition(7, 2), result.Receipt.After);
        AssertBoundary(session, new(7, 2), 10, 21, A, fault);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual("命令测试故障。", result.Diagnostic.Message);
        Assert.AreEqual(new SimulationPosition(7, 2), retained.Position);
        Assert.AreEqual(new CoreRevision(20), retained.Revision);
        Assert.AreEqual(new CoreInputId(new string('A', 64)), retained.InputIdentity);
        Assert.IsNull(retained.Fault);

        var frozenSubmission = session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest);
        var frozenAdvancement = session.Advance(4);

        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(frozenSubmission);
        Assert.AreEqual("frozen", ((SubmissionResult.NotAccepted)frozenSubmission).Reason.Code);
        Assert.AreEqual(0u, frozenAdvancement.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(7, 2), frozenAdvancement.Position);
        Assert.AreEqual(fault, frozenAdvancement.Fault);
        Assert.IsNull(frozenAdvancement.Diagnostic);
        AssertBoundary(session, new(7, 2), 10, 21, A, fault);
    }

    [TestMethod]
    public void A_fault_at_the_maximum_revision_freezes_without_wrapping_the_revision()
    {
        using var session = new CoreSession(WorldFixture.State(new(7, 2), 10, new(ulong.MaxValue), A) with { Content = A });
        var retained = session.Read();

        var result = session.Advance(1);

        Assert.AreEqual(0u, result.CompletedSteps);
        var fault = new CoreFault("internal_error", new(8, 0), "Advance", "", null);
        Assert.AreEqual(fault, result.Fault);
        Assert.IsNotNull(result.Diagnostic);
        AssertBoundary(session, new(7, 2), 10, ulong.MaxValue, A, fault);
        Assert.AreEqual(new CoreRevision(ulong.MaxValue), retained.Revision);
        Assert.IsNull(retained.Fault);

        var submission = session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(submission);
        Assert.AreEqual("frozen", ((SubmissionResult.NotAccepted)submission).Reason.Code);
    }

    [TestMethod]
    public void A_step_fault_keeps_completed_steps_and_events_and_increments_revision_for_the_fault()
    {
        using var session = Session();
        var calls = 0;

        var result = session.Advance(4, _ => ++calls switch
        {
            1 => (B, [First, Second]),
            2 => (B, [Third]),
            _ => throw new InvalidOperationException("步骤测试故障。")
        });

        Assert.AreEqual(2u, result.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(9, 0), result.Position);
        var fault = new CoreFault("internal_error", new(10, 0), "Advance", "", null);
        Assert.AreEqual(fault, result.Fault);
        AssertBoundary(session, new(9, 0), 10, 23, B, fault);
        CollectionAssert.AreEqual(new EventEnvelope[]
        {
            new(new(8, 0), 0, First), new(new(8, 0), 1, Second), new(new(9, 0), 0, Third)
        }, result.Events.ToArray());
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual("步骤测试故障。", result.Diagnostic.Message);

        var frozen = session.Advance(1);
        Assert.AreEqual(0u, frozen.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(9, 0), frozen.Position);
        Assert.AreEqual(fault, frozen.Fault);
        Assert.IsNull(frozen.Diagnostic);
        Assert.IsTrue(frozen.Events.IsEmpty);
        AssertBoundary(session, new(9, 0), 10, 23, B, fault);
    }

    [TestMethod]
    public void Accepted_command_events_use_the_committed_position_and_zero_based_ordinals()
    {
        using var session = Session();

        var result = Completed(session.ExecuteCommand(new CoreCommand.PublishContent(B), CommandSource.AutomationTest, _ => new CommandEffect.Accepted(B, [First, Second])));

        Assert.AreEqual(10UL, result.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 3), result.Receipt.After);
        AssertBoundary(session, new(7, 3), 11, 21, B);
        CollectionAssert.AreEqual(new EventEnvelope[]
        {
            new(new(7, 3), 0, First), new(new(7, 3), 1, Second)
        }, result.Events.ToArray());
    }

    [TestMethod]
    public void Step_events_are_ordered_by_committed_position_then_ordinal()
    {
        using var session = Session();
        var calls = 0;

        var result = session.Advance(2, content => ++calls == 1
            ? (content, [First, Second]) : (content, [Third]));

        Assert.AreEqual(2u, result.CompletedSteps);
        AssertBoundary(session, new(9, 0), 10, 22, A);
        CollectionAssert.AreEqual(new EventEnvelope[]
        {
            new(new(8, 0), 0, First), new(new(8, 0), 1, Second), new(new(9, 0), 0, Third)
        }, result.Events.ToArray());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Malformed_published_arrays_are_rejected_before_identity_checks_without_consuming_a_command(
        bool sameIdentity, bool missingReferences)
    {
        using var session = Session();
        var malformed = sameIdentity ? A : B;
        malformed = missingReferences ? malformed with { MissingReferences = default } : malformed with { Designs = default };
        var before = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var payload = before.Encode();
        var checkpoint = before.Checkpoint;

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            session.Submit(new CoreCommand.PublishContent(malformed), CommandSource.AutomationTest));

        Assert.AreEqual("Uninitialized array", exception.ParamName);
        AssertBoundary(session, new(7, 2), 10, 20, A);
        var after = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(payload, after.Encode());
        Assert.AreEqual(checkpoint, after.Checkpoint);
        var accepted = Completed(session.Submit(new CoreCommand.PublishContent(B), CommandSource.AutomationTest));
        Assert.IsInstanceOfType<CommandDecision.Accepted>(accepted.Receipt.Decision);
        Assert.AreEqual(10UL, accepted.Receipt.Number);
        AssertBoundary(session, new(7, 3), 11, 21, B);
    }

    [TestMethod]
    public void Publishing_the_current_identity_is_a_precondition_error_without_state_changes()
    {
        using var session = Session();

        Assert.ThrowsExactly<ArgumentException>(() =>
            session.Submit(new CoreCommand.PublishContent(A), CommandSource.AutomationTest));

        AssertBoundary(session, new(7, 2), 10, 20, A);
    }

    [TestMethod]
    public void An_unimplemented_command_throws_without_becoming_a_completed_command()
    {
        using var session = Session();

        Assert.ThrowsExactly<NotImplementedException>(() =>
            session.Submit(new CoreCommand.SetProductionPlan(new(new(1), CapabilityKind.Production),
                new ProcessRef.Production(new("fixture:process")), true, 0), CommandSource.AutomationTest));

        AssertBoundary(session, new(7, 2), 10, 20, A);
    }

    private static CoreSession Session() => new(WorldFixture.State(new(7, 2), 10, new(20), A) with { Content = A });

    private static CoreContentProjection Content(char identity)
        => WorldFixture.Content with { Identity = new(new string(identity, 64)) };

    private static SubmissionResult.Completed Completed(SubmissionResult result)
    {
        Assert.IsInstanceOfType<SubmissionResult.Completed>(result);
        return (SubmissionResult.Completed)result;
    }

    private static void AssertBoundary(CoreSession session, SimulationPosition position,
        ulong number, ulong revision, CoreContentProjection content, CoreFault? fault = null)
    {
        var view = session.Read();
        Assert.AreEqual(position, view.Position);
        Assert.AreEqual(new CoreRevision(revision), view.Revision);
        Assert.AreEqual(content.Identity, view.InputIdentity);
        Assert.AreEqual(fault, view.Fault);
        Assert.AreEqual(number, session.State.NextCommandNumber);
        Assert.AreEqual(content, session.State.Content);
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreRecordingTests
{
    [TestMethod]
    public void Publication_has_an_independently_written_format_two_vector()
    {
        const string expected = "{\"input_format_version\":2,\"kind\":\"content.publish\",\"input_identity\":\"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\"}";
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        var record = session.ReadRecording().Commands.Single();
        Assert.AreEqual(expected, record.CanonicalPayload);
        Assert.AreEqual(CommandSource.Console, record.Source);
        var decoded = Success(CoreCommandEncoding.Decode("{\"input_identity\":\"" + new string('B', 64)
            + "\",\"kind\":\"content.publish\",\"input_format_version\":2}"));
        Assert.AreEqual(Content('B').Identity, decoded);
    }

    [TestMethod]
    public void All_completed_decisions_keep_their_target_dependencies_and_fault_history()
    {
        using var session = Session();
        var initial = session.ReadRecording().Starts.Single();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.UserInterface);
        var later = Success(session.CaptureRecordingStart());
        session.ExecuteCommand(new CoreCommand.PublishContent(Content('C')), CommandSource.Console,
            _ => new CommandEffect.Rejected([new("invalid_state", "test", null, [])]));
        session.ExecuteCommand(new CoreCommand.PublishContent(Content('D')), CommandSource.AutomationTest,
            _ => throw new InvalidOperationException("test fault"));
        var before = session.ReadRecording();
        Assert.AreEqual(3, before.Commands.Length);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(before.Commands[1].Result.Decision);
        Assert.IsInstanceOfType<CommandDecision.Faulted>(before.Commands[2].Result.Decision);
        CollectionAssert.AreEqual(new[] { 'A', 'B', 'C', 'D' }.Select(c => Content(c).Identity).ToArray(),
            before.RequiredReferences.Inputs.ToArray());
        var fault = before.FaultSegments.Single();
        Assert.AreEqual(initial, fault.Start);
        Assert.AreEqual(3, fault.Commands.Length);
        Assert.AreEqual(session.Read().Fault, fault.Fault);
        var maintenance = session.ConfirmRecordingStart(later.Id, RecordingWriteOutcome.PublishedWithDependencies);
        Assert.IsTrue(maintenance.ReleasedInputs.IsEmpty);
        Assert.AreEqual(later, session.ReadRecording().Starts.Single());
        Assert.AreEqual(fault, session.ReadRecording().FaultSegments.Single());
        Assert.AreEqual(3, before.Commands.Length);
    }

    [TestMethod]
    public void Pending_or_failed_writes_do_not_trim_and_a_new_publication_closes_the_gap()
    {
        using var session = Session();
        var initial = session.ReadRecording().Starts.Single();
        session.ConfirmRecordingStart(initial.Id, RecordingWriteOutcome.PublishedWithDependencies);
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        var failed = Success(session.CaptureRecordingStart());
        Assert.AreEqual(2, session.ReadRecording().Starts.Length);
        var failure = session.ConfirmRecordingStart(failed.Id, RecordingWriteOutcome.Failed);
        Assert.IsTrue(failure.ReleasedInputs.IsEmpty);
        Assert.AreEqual(2, session.ReadRecording().Starts.Length);
        Assert.IsNull(failure.Gaps.Single().Until);
        session.Advance(3);
        var replacement = Success(session.CaptureRecordingStart());
        var success = session.ConfirmRecordingStart(replacement.Id, RecordingWriteOutcome.PublishedWithDependencies);
        Assert.AreEqual(failed.State.Position, success.Gaps.Single().From);
        Assert.AreEqual(replacement.State.Position, success.Gaps.Single().Until);
        CollectionAssert.AreEqual(new[] { Content('A').Identity }, success.ReleasedInputs.ToArray());
        Assert.AreEqual(replacement, session.ReadRecording().Starts.Single());
        Assert.IsTrue(session.ReadRecording().Commands.IsEmpty);
    }

    [TestMethod]
    public void A_late_initial_confirmation_does_not_move_the_window_backwards()
    {
        using var session = Session();
        var initial = session.ReadRecording().Starts.Single();
        session.Advance(2);
        var latest = Success(session.CaptureRecordingStart());
        session.ConfirmRecordingStart(latest.Id, RecordingWriteOutcome.PublishedWithDependencies);
        session.ConfirmRecordingStart(initial.Id, RecordingWriteOutcome.PublishedWithDependencies);
        Assert.AreEqual(latest, session.ReadRecording().Starts.Single());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            session.ConfirmRecordingStart(initial.Id, RecordingWriteOutcome.Failed));
        Assert.ThrowsExactly<ArgumentException>(() =>
            session.ConfirmRecordingStart(new(99), RecordingWriteOutcome.PublishedWithDependencies));
    }

    [TestMethod]
    public void The_requested_number_of_published_starts_controls_retention()
    {
        using var session = new CoreSession(new(new(0, 0), 1, new(0), Content('A')), 2);
        var initial = session.ReadRecording().Starts.Single();
        session.ConfirmRecordingStart(initial.Id, RecordingWriteOutcome.PublishedWithDependencies);
        session.Advance(1);
        var second = Success(session.CaptureRecordingStart());
        session.ConfirmRecordingStart(second.Id, RecordingWriteOutcome.PublishedWithDependencies);
        Assert.AreEqual(2, session.ReadRecording().Starts.Length);
        session.Advance(1);
        var third = Success(session.CaptureRecordingStart());
        session.ConfirmRecordingStart(third.Id, RecordingWriteOutcome.PublishedWithDependencies);
        CollectionAssert.AreEqual(new[] { second, third }, session.ReadRecording().Starts.ToArray());
    }

    [TestMethod]
    public void Checkpoints_are_only_starts_and_the_end_and_queries_do_not_record_inputs()
    {
        using var session = Session();
        session.Read();
        session.Capture(CapturePurpose.Save);
        session.Advance(0);
        Assert.IsTrue(session.ReadRecording().Commands.IsEmpty);
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        session.Advance(2);
        var window = session.ReadRecording();
        Assert.AreEqual(2, window.Commands.Length);
        Assert.AreEqual(2, window.Checkpoints.Length);
        Assert.AreEqual(new SimulationPosition(2, 0), window.EndPosition);
        Assert.AreEqual(window.EndPosition, window.Checkpoints[^1].Position);
        session.Dispose();
        Assert.AreEqual(2, window.Commands.Length);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.ReadRecording());
    }

    [TestMethod]
    public void Faults_and_programming_errors_do_not_create_extra_completed_records()
    {
        using var session = Session(ulong.MaxValue);
        Assert.ThrowsExactly<ArgumentException>(() =>
            session.Submit(new CoreCommand.PublishContent(Content('A')), CommandSource.Console));
        Assert.IsTrue(session.ReadRecording().Commands.IsEmpty);
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        Assert.AreEqual(1, session.ReadRecording().Commands.Length);
        Assert.AreEqual(1, session.ReadRecording().FaultSegments.Length);
    }

    [TestMethod]
    public void A_step_fault_records_the_last_complete_boundary_and_preserves_its_start()
    {
        using var session = Session(ulong.MaxValue);
        var result = session.Advance(1);
        var window = session.ReadRecording();
        Assert.AreEqual(new SimulationPosition(0, 0), window.EndPosition);
        Assert.AreEqual(result.Fault, window.FaultSegments.Single().Fault);
        Assert.IsTrue(window.Commands.IsEmpty);
        Assert.AreEqual(2, window.Checkpoints.Length);
        Assert.AreNotEqual(window.Checkpoints[0].CanonicalText, window.Checkpoints[1].CanonicalText);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Restore_rejects_nonpositive_recording_retention(int retention)
    {
        using var session = Session();
        var capture = Success(session.Capture(CapturePurpose.Save));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CoreFactory.Create().PrepareRestore(capture,
            Content('A'), new(new("build", "sim", "os", "arch", "net"), false, retention)));
    }

    private static CoreSession Session(ulong revision = 0) => new(new(new(0, 0), 1, new(revision), Content('A')));
    private static CoreContentProjection Content(char identity)
        => new(new(new string(identity, 64)), [], [], [], [], [], [], [], [], [], [], [], [], [], []);
    private static T Success<T>(CoreResult<T> result) where T : notnull
        => ((CoreResult<T>.Success)result).Value;
}

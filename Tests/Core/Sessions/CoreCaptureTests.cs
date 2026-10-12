using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreCaptureTests
{
    private const string Identity = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Payload = "{\"core_state_format_version\":1,\"position\":{\"completed_steps\":7,\"sequence\":2},\"next_command_number\":10,\"revision\":20,\"input_identity\":\"" + Identity + "\",\"fault\":null}";
    private const string CheckpointText = "{\"position\":{\"completed_steps\":7,\"sequence\":2},\"next_command_number\":10,\"fault\":null}";
    private const string CheckpointHash = "7164320630B008C414C22F3CAFDC6EB23A7BFED698B24626192A501986BE4927";
    private const string FaultText = "{\"code\":\"internal_error\",\"position\":{\"completed_steps\":7,\"sequence\":3},\"operation\":\"Submit\",\"field\":\"\",\"subject\":null}";
    private const string DiagnosticPayload = "{\"core_state_format_version\":1,\"position\":{\"completed_steps\":7,\"sequence\":2},\"next_command_number\":10,\"revision\":21,\"input_identity\":\"" + Identity + "\",\"fault\":" + FaultText + "}";
    private const string DiagnosticCheckpoint = "{\"position\":{\"completed_steps\":7,\"sequence\":2},\"next_command_number\":10,\"fault\":" + FaultText + "}";
    private static readonly CoreSessionOptions Options = new(new("test", "test", "windows", "x64", "test"), false, 1);

    [TestMethod]
    public void Capture_encodes_current_state_and_an_independent_checkpoint_vector()
    {
        using var session = Session();

        var capture = Capture(session);

        Assert.AreEqual(Payload, capture.Encode());
        Assert.AreEqual(new SimulationPosition(7, 2), capture.Position);
        Assert.AreEqual(new CoreRevision(20), capture.Revision);
        Assert.AreEqual(Content('A').Identity, capture.InputIdentity);
        Assert.AreEqual(new CoreCheckpoint(new(7, 2), CheckpointText, CheckpointHash), capture.Checkpoint);
        AssertReferences(capture.References, 'A');
    }

    [TestMethod]
    public void Decode_normalizes_whitespace_and_preserves_the_saved_boundary_without_content()
    {
        var payload = " \n" + Payload + "\r\n";

        var capture = Decode(payload);

        Assert.AreEqual(Payload, capture.Encode());
        Assert.AreEqual(new SimulationPosition(7, 2), capture.Position);
        Assert.AreEqual(new CoreRevision(20), capture.Revision);
        Assert.AreEqual(new CoreInputId(Identity), capture.InputIdentity);
        AssertReferences(capture.References, 'A');
        Assert.AreEqual(new CoreCheckpoint(new(7, 2), CheckpointText, CheckpointHash), capture.Checkpoint);
    }

    [TestMethod]
    public void Shared_capture_contract_runs_against_the_real_session()
    {
        var references = new CoreReferenceManifest([], [], [], [Content('A').Identity]);
        using var session = Session();

        CaptureScenario.AssertContract(session, Content('A').Identity, references, references);
    }

    [TestMethod]
    public void Publishing_and_disposal_leave_retained_capture_and_references_unchanged()
    {
        var session = Session();
        var retained = Capture(session);
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.AutomationTest);
        session.Advance(2);
        var current = Capture(session);
        session.Dispose();

        Assert.AreEqual(Payload, retained.Encode());
        Assert.AreEqual(new CoreCheckpoint(new(7, 2), CheckpointText, CheckpointHash), retained.Checkpoint);
        AssertReferences(retained.References, 'A');
        AssertReferences(current.References, 'B');
        Assert.AreEqual(new SimulationPosition(9, 0), current.Position);
        Assert.AreEqual(new CoreRevision(23), current.Revision);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Capture(CapturePurpose.Save));
    }

    [TestMethod]
    public void Frozen_save_is_rejected_but_diagnostic_capture_preserves_attempted_fault_boundary()
    {
        using var session = Session();
        session.ExecuteCommand(_ => throw new InvalidOperationException("diagnostic only"));

        AssertFailure(session.Capture(CapturePurpose.Save), "frozen", "Fault");
        var diagnostic = Success(session.Capture(CapturePurpose.Diagnostic));
        Assert.AreEqual(DiagnosticPayload, diagnostic.Encode());
        Assert.AreEqual(new CoreCheckpoint(new(7, 2), DiagnosticCheckpoint,
            "83C6FA841D3B06E3CEDCC04222B7DE927038B4C9C65C248165534A03CD2AE2FB"), diagnostic.Checkpoint);
        var decoded = Decode(diagnostic.Encode());
        Assert.AreEqual(DiagnosticPayload, decoded.Encode());
        Assert.AreEqual(diagnostic.Checkpoint, decoded.Checkpoint);
        AssertFailure(CoreFactory.Create().PrepareRestore(decoded, Content('A'), Options), "frozen", "Fault");
        Assert.AreEqual(new SimulationPosition(7, 2), session.Read().Position);
    }

    [TestMethod]
    public void Diagnostic_round_trip_preserves_a_failed_step_separately_from_the_last_boundary()
    {
        using var session = Session();
        session.Advance(1, _ => throw new InvalidOperationException("failed step"));
        var capture = Success(session.Capture(CapturePurpose.Diagnostic));

        var decoded = Decode(capture.Encode());
        Assert.AreEqual(new SimulationPosition(7, 2), decoded.Position);
        Assert.AreEqual(new CoreRevision(21), decoded.Revision);
        Assert.AreEqual(new CoreFault("internal_error", new(8, 0), "Advance", "", null), ((CoreCapture)decoded).Fault);
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        Assert.AreEqual(capture.Checkpoint, decoded.Checkpoint);
        AssertFailure(CoreFactory.Create().PrepareRestore(decoded, Content('A'), Options), "frozen", "Fault");
    }

    [TestMethod]
    public void Same_input_restore_preserves_boundary_and_continues_command_numbering()
    {
        var saved = Decode(Payload);
        using var candidate = Success(CoreFactory.Create().PrepareRestore(saved, Content('A'), Options));
        Assert.IsTrue(candidate.AdaptationReport.IsEmpty);
        Assert.IsTrue(candidate.MissingReferences.IsEmpty);
        AssertReferences(candidate.References, 'A');
        Assert.AreEqual(new SimulationPosition(7, 2), candidate.View.Position);
        Assert.AreEqual(new CoreRevision(20), candidate.View.Revision);
        using var session = candidate.Activate();
        Assert.AreEqual(Payload, Capture(session).Encode());
        Assert.AreEqual(saved.Checkpoint, Capture(session).Checkpoint);

        var completed = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);

        Assert.AreEqual(10UL, completed.Receipt.Number);
        Assert.AreEqual(new SimulationPosition(7, 2), completed.Receipt.Before);
        Assert.AreEqual(new SimulationPosition(7, 3), completed.Receipt.After);
        Assert.AreEqual(new CoreRevision(21), session.Read().Revision);
        Assert.AreEqual(1u, session.Advance(1).CompletedSteps);
        Assert.AreEqual(new SimulationPosition(8, 0), session.Read().Position);
        Assert.AreEqual(new CoreRevision(22), session.Read().Revision);
    }

    [TestMethod]
    public void Ordinary_restore_reports_only_identity_adaptation_and_preserves_saved_capture()
    {
        var saved = Decode(Payload);
        using var candidate = Success(CoreFactory.Create().PrepareRestore(saved, Content('B'), Options));

        var problem = candidate.AdaptationReport.Single();
        Assert.AreEqual("content_changed", problem.Code);
        Assert.AreEqual("InputIdentity", problem.Field);
        Assert.IsNull(problem.Subject);
        CollectionAssert.AreEqual(new[] { Identity, new string('B', 64) }, problem.RelatedIdentities.ToArray());
        Assert.IsTrue(candidate.MissingReferences.IsEmpty);
        AssertReferences(candidate.References, 'B');
        using var session = candidate.Activate();
        var adapted = Capture(session);
        Assert.AreEqual(Payload.Replace(Identity, new string('B', 64)), adapted.Encode());
        Assert.AreEqual(saved.Checkpoint, adapted.Checkpoint);
        Assert.AreEqual(Payload, saved.Encode());
        AssertReferences(saved.References, 'A');
        var completed = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        Assert.AreEqual(10UL, completed.Receipt.Number);
    }

    [TestMethod]
    public void Revision_and_input_identity_are_outside_the_checkpoint()
    {
        using var session = new CoreSession(new(new(7, 2), 10, new(ulong.MaxValue), Content('B')));

        Assert.AreEqual(new CoreCheckpoint(new(7, 2), CheckpointText, CheckpointHash), Capture(session).Checkpoint);
    }

    [TestMethod]
    public void Restore_rejects_foreign_capture_as_a_programming_error()
    {
        using var other = new CaptureSessionDouble();
        var capture = Success(other.Capture(CapturePurpose.Save));

        Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareRestore(capture, Content('A'), Options));
    }

    [TestMethod]
    public void Maximum_unsigned_values_round_trip_without_clamping()
    {
        using var session = new CoreSession(new(new(ulong.MaxValue, ulong.MaxValue), ulong.MaxValue,
            new(ulong.MaxValue), Content('A')));
        var saved = Capture(session);
        var decoded = Decode(saved.Encode());
        using var candidate = Success(CoreFactory.Create().PrepareRestore(decoded, Content('A'), Options));
        using var restored = candidate.Activate();

        Assert.AreEqual(saved.Encode(), Capture(restored).Encode());
        Assert.AreEqual(saved.Checkpoint, Capture(restored).Checkpoint);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("[]")]
    public void Malformed_or_nonobject_payload_returns_a_root_problem(string payload)
        => AssertFailure(CoreFactory.Create().DecodeState(payload), "unsupported_format", "");

    [TestMethod]
    public void Unsupported_version_is_rejected_before_reading_an_unknown_state_schema()
        => AssertFailure(CoreFactory.Create().DecodeState("{\"core_state_format_version\":2}"),
            "unsupported_format", "/core_state_format_version");

    [TestMethod]
    [DataRow("\"core_state_format_version\":1", "\"core_state_format_version\":2", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":1", "\"core_state_format_version\":\"1\"", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":1", "\"core_state_format_version\":null", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":1", "\"core_state_format_version\":-1", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":1", "\"core_state_format_version\":1.5", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":1", "\"core_state_format_version\":18446744073709551616", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":1,", "", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"revision\":20,", "", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":20,\"extra\":1", "invalid_state", "/extra")]
    [DataRow("\"revision\":20", "\"revision\":20,\"revision\":21", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":18446744073709551616", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":-1", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":1.5", "invalid_state", "/revision")]
    [DataRow("\"next_command_number\":10", "\"next_command_number\":0", "invalid_state", "/next_command_number")]
    [DataRow("\"completed_steps\":7", "\"completed_steps\":false", "invalid_state", "/position/completed_steps")]
    [DataRow("\"sequence\":2", "\"sequence\":-1", "invalid_state", "/position/sequence")]
    [DataRow("\"sequence\":2", "\"sequence\":2,\"other\":0", "invalid_state", "/position/other")]
    [DataRow("\"completed_steps\":7,\"sequence\":2", "", "invalid_state", "/position/completed_steps")]
    [DataRow(Identity, "abcd", "invalid_state", "/input_identity")]
    [DataRow(Identity, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "invalid_state", "/input_identity")]
    [DataRow(Identity, "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "invalid_state", "/input_identity")]
    [DataRow(Identity, "\\uD800", "invalid_state", "/input_identity")]
    public void Invalid_saved_fields_return_the_exact_problem_path(string before, string after, string code, string field)
        => AssertFailure(CoreFactory.Create().DecodeState(Payload.Replace(before, after)), code, field);

    [TestMethod]
    [DataRow("\"code\":\"internal_error\"", "\"code\":\"\"", "/fault/code")]
    [DataRow("\"code\":\"internal_error\",", "", "/fault/code")]
    [DataRow("\"operation\":\"Submit\"", "\"operation\":0", "/fault/operation")]
    [DataRow("\"sequence\":3", "\"sequence\":null", "/fault/position/sequence")]
    [DataRow("\"subject\":null", "\"subject\":{}", "/fault/subject")]
    public void Invalid_diagnostic_fields_are_located_without_freezing_an_existing_session(string before, string after, string field)
    {
        using var active = Session();
        AssertFailure(CoreFactory.Create().DecodeState(DiagnosticPayload.Replace(before, after)), "invalid_state", field);
        Assert.AreEqual(Payload, Capture(active).Encode());
        Assert.IsNull(active.Read().Fault);
    }

    private static CoreSession Session() => new(new(new(7, 2), 10, new(20), Content('A')));
    private static CoreContentProjection Content(char identity)
        => new(new(new string(identity, 64)), [], [], [], [], [], [], [], [], [], [], [], [], [], []);
    private static ICoreCapture Capture(ICoreSession session) => Success(session.Capture(CapturePurpose.Save));
    private static ICoreCapture Decode(string payload) => Success(CoreFactory.Create().DecodeState(payload));
    private static T Success<T>(CoreResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<CoreResult<T>.Success>(result);
        return ((CoreResult<T>.Success)result).Value;
    }
    private static void AssertFailure<T>(CoreResult<T> result, string code, string field) where T : notnull
    {
        Assert.IsInstanceOfType<CoreResult<T>.Failure>(result);
        var problem = ((CoreResult<T>.Failure)result).Problems.Single();
        Assert.AreEqual(code, problem.Code);
        Assert.AreEqual(field, problem.Field);
        Assert.IsNull(problem.Subject);
        Assert.IsTrue(problem.RelatedIdentities.IsEmpty);
    }
    private static void AssertReferences(CoreReferenceManifest references, char identity)
    {
        Assert.IsTrue(references.Designs.IsEmpty);
        Assert.IsTrue(references.Templates.IsEmpty);
        Assert.IsTrue(references.Definitions.IsEmpty);
        CollectionAssert.AreEqual(new[] { Content(identity).Identity }, references.Inputs.ToArray());
    }
}

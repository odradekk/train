// Train.Tests.Core.Simulation 验证命令日志记录、摘要链、窗口裁剪和会话接入。
// 固定记录文本为手写协议向量；链摘要由标准 SHA-256 从前一文本摘要和记录文本独立计算。

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationCommandLogTests
{
    private const string PayloadText = "{\"input_format_version\":2,\"kind\":\"fixture.no_change\"}";
    private static readonly EncodedCommand Payload = new(PayloadText,
        "360A66E43D7828BE95C4EEF9D923A04986D8D37B0ECD077AA9023276EB9BFEA3");

    public static IEnumerable<object[]> OutcomeRecords()
    {
        yield return
        [
            new CommandExecution(1, new SimulationPosition(0, 1), 1, CommandSource.Ui, CommandOutcome.Executed),
            "{\"record_format_version\":1,\"source\":\"ui\",\"command_id\":1,\"position\":{\"completed_steps\":0,\"sequence\":1},\"transaction_id\":1,\"payload\":{\"input_format_version\":2,\"kind\":\"fixture.no_change\"},\"outcome\":\"executed\",\"reason\":null}",
            "A3854AB693362167DFD16F2BFDF369B3F1560696276E470E746B5AFD878515EE"
        ];
        yield return
        [
            new CommandExecution(1, new SimulationPosition(0, 1), 1, CommandSource.Console,
                CommandOutcome.Rejected, "target_missing"),
            "{\"record_format_version\":1,\"source\":\"console\",\"command_id\":1,\"position\":{\"completed_steps\":0,\"sequence\":1},\"transaction_id\":1,\"payload\":{\"input_format_version\":2,\"kind\":\"fixture.no_change\"},\"outcome\":\"rejected\",\"reason\":\"target_missing\"}",
            "766E4465CDB25F9D5FB54AB64AD68D5FA6A93CF9B7179046D516C746573B980E"
        ];
        yield return
        [
            new CommandExecution(1, new SimulationPosition(0, 1), 1, CommandSource.Automation,
                CommandOutcome.Faulted, "fixture_failure"),
            "{\"record_format_version\":1,\"source\":\"automation\",\"command_id\":1,\"position\":{\"completed_steps\":0,\"sequence\":1},\"transaction_id\":1,\"payload\":{\"input_format_version\":2,\"kind\":\"fixture.no_change\"},\"outcome\":\"faulted\",\"reason\":\"fixture_failure\"}",
            "BCD61F786AFF14D3FB55D838BB4422AD6D8FED3191847C83F1EB64888045B79A"
        ];
    }

    [DataTestMethod]
    [DynamicData(nameof(OutcomeRecords), DynamicDataSourceType.Method)]
    public void Each_outcome_has_a_fixed_record_text_and_first_chain_hash(
        CommandExecution execution, string expectedText, string expectedChain)
    {
        var snapshot = SimulationCommandLog.Append(CommandLogSnapshot.Empty, execution, Payload);

        Assert.AreEqual(SimulationCommandLog.InitialSha256, snapshot.PrefixSha256);
        Assert.AreEqual(1, snapshot.Entries.Length);
        Assert.AreEqual(execution, snapshot.Entries[0].Execution);
        Assert.AreEqual(Payload, snapshot.Entries[0].Payload);
        Assert.AreEqual(expectedText, snapshot.Entries[0].CanonicalText);
        Assert.AreEqual(expectedChain, snapshot.Entries[0].ChainSha256);
        Assert.AreEqual(Chain(SimulationCommandLog.InitialSha256, expectedText), expectedChain);
    }

    [TestMethod]
    public void Append_returns_a_new_snapshot_without_changing_the_old_window()
    {
        var first = SimulationCommandLog.Append(CommandLogSnapshot.Empty, Execution(1), Payload);
        var oldEntries = first.Entries;

        var second = SimulationCommandLog.Append(first, Execution(2), Payload);

        Assert.AreEqual(SimulationCommandLog.InitialSha256, first.PrefixSha256);
        Assert.AreEqual(1, first.Entries.Length);
        Assert.AreEqual(oldEntries, first.Entries);
        Assert.AreEqual(1UL, first.Entries[0].Execution.CommandId);
        Assert.AreEqual(2, second.Entries.Length);
    }

    [TestMethod]
    public void The_256_257_and_258_entry_windows_keep_the_full_history_chain_and_advance_the_anchor()
    {
        var current = CommandLogSnapshot.Empty;
        var expectedChains = new List<string>();
        var previous = SimulationCommandLog.InitialSha256;
        CommandLogSnapshot? at256 = null;
        CommandLogSnapshot? at257 = null;

        for (ulong id = 1; id <= 258; id++)
        {
            var text = RecordText(id);
            previous = Chain(previous, text);
            expectedChains.Add(previous);
            current = SimulationCommandLog.Append(current, Execution(id), Payload);
            if (id == 256)
                at256 = current;
            if (id == 257)
                at257 = current;
        }

        AssertWindow(at256!, SimulationCommandLog.InitialSha256, 1, 256, expectedChains);
        AssertWindow(at257!, expectedChains[0], 2, 257, expectedChains);
        AssertWindow(current, expectedChains[1], 3, 258, expectedChains);
        Assert.AreEqual(expectedChains[257], current.Entries[^1].ChainSha256);
    }

    private static CommandExecution Execution(ulong id) => new(
        id, new SimulationPosition(0, id), id, CommandSource.Ui, CommandOutcome.Executed);

    private static string RecordText(ulong id) =>
        $"{{\"record_format_version\":1,\"source\":\"ui\",\"command_id\":{id},\"position\":{{\"completed_steps\":0,\"sequence\":{id}}},\"transaction_id\":{id},\"payload\":{PayloadText},\"outcome\":\"executed\",\"reason\":null}}";

    private static string Chain(string previous, string recordText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + recordText)));

    private static void AssertWindow(CommandLogSnapshot snapshot, string prefix, ulong firstId, ulong lastId,
        IReadOnlyList<string> expectedChains)
    {
        Assert.AreEqual(prefix, snapshot.PrefixSha256);
        Assert.AreEqual(256, snapshot.Entries.Length);
        Assert.AreEqual(firstId, snapshot.Entries[0].Execution.CommandId);
        Assert.AreEqual(lastId, snapshot.Entries[^1].Execution.CommandId);
        foreach (var entry in snapshot.Entries)
            Assert.AreEqual(expectedChains[checked((int)entry.Execution.CommandId - 1)], entry.ChainSha256);
    }

}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:simulation")]
public sealed class SimulationSessionLoggingTests
{
    [TestMethod]
    public void Session_records_executed_rejected_and_faulted_commands_automatically()
    {
        var state = new ResourceState { NextEntityId = 8, EntityIds = ImmutableSortedSet.Create(7UL) };
        var session = new SimulationSession(state, new ResourceCalculations(), new ResourceCommandCodec());

        session.Submit(new NoChangeCommand(), CommandSource.Ui);
        session.Submit(new DeleteEntityCommand(99), CommandSource.Console);
        session.Submit(new InvalidCandidateCommand(), CommandSource.Automation);

        var entries = session.GetCommandLog().Entries;
        CollectionAssert.AreEqual(
            new[] { CommandOutcome.Executed, CommandOutcome.Rejected, CommandOutcome.Faulted },
            entries.Select(entry => entry.Execution.Outcome).ToArray());
        CollectionAssert.AreEqual(new[] { "ui", "console", "automation" },
            entries.Select(entry => SourceText(entry.CanonicalText)).ToArray());
        Assert.AreEqual("invalid_entity_id", entries[2].Execution.Reason);
    }

    [TestMethod]
    public void A_fault_log_keeps_the_attempt_identity_while_state_rolls_back_and_later_entry_rejection_adds_nothing()
    {
        var before = new ResourceState { NextEntityId = 8, EntityIds = ImmutableSortedSet.Create(7UL) };
        var session = new SimulationSession(before, new ResourceCalculations(), new ResourceCommandCodec());

        var faulted = (SubmissionResult.Completed)session.Submit(
            new InvalidCandidateCommand(), CommandSource.Automation);

        Assert.AreEqual(CommandOutcome.Faulted, faulted.Execution.Outcome);
        Assert.AreEqual(1UL, faulted.Execution.CommandId);
        Assert.AreEqual(new SimulationPosition(0, 1), faulted.Execution.Position);
        Assert.AreEqual(1UL, faulted.Execution.TransactionId);
        var frozen = (ResourceState)session.GetState();
        Assert.AreEqual(1UL, frozen.NextCommandId);
        Assert.AreEqual(0UL, frozen.BoundarySequence);
        Assert.AreEqual(0UL, frozen.TransactionCount);
        Assert.AreEqual(8UL, frozen.NextEntityId);
        CollectionAssert.AreEqual(new ulong[] { 7 }, frozen.EntityIds.ToArray());
        Assert.IsNotNull(frozen.Fault);

        var rejected = session.Submit(new NoChangeCommand(), CommandSource.Ui);

        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(rejected);
        Assert.AreEqual(1, session.GetCommandLog().Entries.Length);
        Assert.AreEqual(faulted.Execution, session.GetCommandLog().Entries[0].Execution);
    }

    [TestMethod]
    public void Entry_rejection_does_not_create_a_log_record()
    {
        var state = new ResourceState { NextCommandId = ulong.MaxValue, TransactionCount = ulong.MaxValue - 1 };
        var session = new SimulationSession(state, new ResourceCalculations(), new ResourceCommandCodec());

        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(
            session.Submit(new NoChangeCommand(), CommandSource.Ui));
        Assert.AreEqual(CommandLogSnapshot.Empty, session.GetCommandLog());
    }

    [TestMethod]
    public void Encoding_failure_before_acceptance_neither_consumes_identity_nor_creates_a_log_record()
    {
        var before = new ResourceState();
        var calculations = new ScriptedCalculations
        {
            Command = (_, _, _) => throw new AssertFailedException("编码失败后不应进入命令计算。")
        };
        var session = new SimulationSession(before, calculations, new ResourceCommandCodec());

        Assert.ThrowsException<ArgumentException>(() =>
            session.Submit(new UnsupportedCommand(), CommandSource.Ui));

        Assert.AreEqual(before, session.GetState());
        Assert.AreEqual(CommandLogSnapshot.Empty, session.GetCommandLog());
        Assert.AreEqual(0, calculations.CommandInputs.Count);
    }

    [TestMethod]
    public void Encoding_and_command_calculation_observe_the_old_state_and_old_log_until_publication()
    {
        var before = new ResourceState();
        SimulationSession? session = null;
        var observed = new List<(SimulationState State, CommandLogSnapshot Log)>();
        var codec = new ResourceCommandCodec
        {
            WritingFields = () => observed.Add((session!.GetState(), session.GetCommandLog()))
        };
        var calculations = new ScriptedCalculations
        {
            Command = (state, _, context) =>
            {
                observed.Add((session!.GetState(), session.GetCommandLog()));
                return new CommandDecision.Execute(new Transition(state with
                {
                    NextCommandId = context.CommandId!.Value + 1,
                    BoundarySequence = context.Position.Sequence,
                    TransactionCount = context.TransactionId
                }, ImmutableArray<DomainEvent>.Empty));
            }
        };
        session = new SimulationSession(before, calculations, codec);

        session.Submit(new NoChangeCommand(), CommandSource.Ui);

        Assert.AreEqual(2, observed.Count);
        foreach (var snapshot in observed)
        {
            Assert.AreEqual(before, snapshot.State);
            Assert.AreEqual(CommandLogSnapshot.Empty, snapshot.Log);
        }
        Assert.AreEqual(2UL, session.GetState().NextCommandId);
        Assert.AreEqual(1, session.GetCommandLog().Entries.Length);
    }

    private sealed record UnsupportedCommand : CoreCommand;

    private static string SourceText(string canonicalText)
    {
        const string marker = "\"source\":\"";
        var start = canonicalText.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = canonicalText.IndexOf('"', start);
        return canonicalText[start..end];
    }
}

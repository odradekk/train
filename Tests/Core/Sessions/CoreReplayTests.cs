using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreReplayTests
{
    private static readonly RuntimeIdentity Runtime = new("build", "sim", "windows", "x64", "net");
    private static readonly ImmutableArray<CoreContentProjection> History = [Content('A'), Content('B'), Content('C')];

    [TestMethod]
    public void Real_adaptation_and_rejected_publication_replay_at_every_sampled_boundary()
    {
        using var session = Session();
        var original = Content('A');
        var changed = Content('B') with
        {
            Designs = original.Designs.Select(d => d with
            {
                MaxStructureDurability = 40, MassTons = 2,
                Parts = d.Parts.Select(p => p with { MaxDurability = 30,
                    Capabilities = d.Design == WorldFixture.Station ? [] : p.Capabilities }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var rejected = changed with { Identity = Content('C').Identity, Goods = [] };
        var samples = new List<ICoreCapture> { Success(session.Capture(CapturePurpose.Save)) };
        var receipts = new List<CommandReceipt>();
        void Sample() => samples.Add(Success(session.Capture(CapturePurpose.Save)));
        void Publish(CoreContentProjection content)
        {
            var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content),
                CommandSource.AutomationTest);
            receipts.Add(result.Receipt);
            Sample();
        }

        Publish(changed);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(receipts[0].Decision);
        var vehicle = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(new DurabilityView(40, 40), vehicle.Structure);
        Assert.IsTrue(session.Read().World.Nodes.All(n => n.Platforms == 0));
        session.Advance(1);
        Sample();
        Publish(rejected);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(receipts[1].Decision);
        Assert.AreEqual(changed.Identity, session.Read().InputIdentity);
        session.Advance(1);
        Sample();
        Publish(original);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(receipts[2].Decision);
        session.Advance(1);
        Sample();

        var recording = session.ReadRecording();
        CollectionAssert.AreEqual(receipts.ToArray(), recording.Commands.Select(c => c.Result).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, receipts.Select(r => r.Number).ToArray());
        CollectionAssert.AreEqual(new SimulationPosition[] { new(0, 1), new(1, 1), new(2, 1) },
            receipts.Select(r => r.After).ToArray());
        Assert.AreEqual(changed.Identity, recording.Commands[1].InputBefore);
        Assert.AreEqual(changed.Identity, recording.Commands[1].InputAfter);
        Assert.IsTrue(recording.RequiredReferences.Inputs.Contains(rejected.Identity));
        var input = Input(session) with { Checkpoints = samples.Select(s => s.Checkpoint).ToImmutableArray() };
        AssertFailure(input, [original, changed], "content_missing", "Commands[1].CanonicalPayload/input_identity");
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, Runtime, [original, changed, rejected]));
        foreach (var sample in samples)
        {
            var result = replay.Advance(3, sample.Position);
            Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
            Assert.IsNull(result.Difference);
            Assert.AreEqual(sample.Position, replay.Read().Position);
            Assert.AreEqual(sample.InputIdentity, replay.Read().InputIdentity);
            Assert.AreEqual(sample.Revision, replay.Read().Revision);
            Assert.AreEqual(sample.Encode(), replay.CaptureBoundary().Encode());
            Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(
                sample.Checkpoint, replay.CaptureBoundary().Checkpoint)));
        }
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }

    [TestMethod]
    public void Replay_restores_the_captured_world_without_regenerating_it()
    {
        using var session = Session();
        var input = Input(session);
        var saved = JsonNode.Parse(input.InitialStatePayload)!;
        saved["world"]!["money"]!["value"] = 1001;
        using var replay = Replay(input with { InitialStatePayload = saved.ToJsonString(), Checkpoints = [] });
        var restored = JsonNode.Parse(replay.CaptureBoundary().Checkpoint.CanonicalText)!;
        Assert.AreEqual(1001, restored["world"]!["money"]!["value"]!.GetValue<int>());
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }

    [TestMethod]
    public void Recording_round_trip_replays_all_inputs_and_commandless_tail_at_the_same_position()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.UserInterface);
        session.Advance(2);
        session.Submit(new CoreCommand.PublishContent(Content('A')), CommandSource.AutomationTest);
        session.Advance(4);
        var input = Input(session);
        using var replay = Replay(input);
        var retained = replay.CaptureBoundary();
        Assert.AreEqual(ReplayStop.BudgetReached, replay.Advance(2).Stop);
        Assert.AreEqual(new SimulationPosition(2, 1), replay.Read().Position);
        var result = replay.Advance(4);
        Assert.AreEqual(ReplayStop.EndOfRecording, result.Stop);
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(
            Success(session.Capture(CapturePurpose.Save)).Checkpoint, replay.CaptureBoundary().Checkpoint)));
        Assert.AreEqual(session.Read().Revision, replay.Read().Revision);
        Assert.AreEqual(session.Read().InputIdentity, replay.Read().InputIdentity);
        replay.Dispose();
        Assert.AreEqual(new SimulationPosition(0, 0), retained.Position);
        Assert.ThrowsExactly<ObjectDisposedException>(() => replay.Advance(1));
    }

    [TestMethod]
    public void Stop_at_a_same_step_sequence_does_not_execute_the_next_command()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        using var replay = Replay(Input(session));
        Assert.AreEqual(ReplayStop.RequestedPosition, replay.Advance(0, new(0, 1)).Stop);
        Assert.AreEqual(Content('B').Identity, replay.Read().InputIdentity);
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
        Assert.AreEqual(new SimulationPosition(0, 2), replay.Read().Position);
    }

    [TestMethod]
    public void Empty_recordings_advance_to_the_recorded_end_without_inventing_commands()
    {
        using var session = Session();
        session.Advance(5);
        using var replay = Replay(Input(session));
        Assert.AreEqual(ReplayStop.BudgetReached, replay.Advance(0).Stop);
        Assert.AreEqual(ReplayStop.RequestedPosition, replay.Advance(4, new(3, 0)).Stop);
        Assert.AreEqual(new SimulationPosition(3, 0), replay.Read().Position);
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(8).Stop);
        Assert.AreEqual(new SimulationPosition(5, 0), replay.Read().Position);
    }

    [TestMethod]
    [DataRow("GameBuild")]
    [DataRow("SimulationVersion")]
    [DataRow("OperatingSystem")]
    [DataRow("Architecture")]
    [DataRow("DotNetRuntime")]
    public void Runtime_identity_mismatch_is_ordinal_and_names_the_field(string field)
    {
        using var session = Session();
        var changed = field switch
        {
            "GameBuild" => Runtime with { GameBuild = "BUILD" },
            "SimulationVersion" => Runtime with { SimulationVersion = "SIM" },
            "OperatingSystem" => Runtime with { OperatingSystem = "WINDOWS" },
            "Architecture" => Runtime with { Architecture = "X64" },
            _ => Runtime with { DotNetRuntime = "NET" }
        };
        var failure = (CoreResult<ICoreReplay>.Failure)CoreFactory.Create().PrepareReplay(Input(session), changed, History);
        Assert.AreEqual("incompatible_replay", failure.Problems.Single().Code);
        Assert.AreEqual("OriginalRuntime." + field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Preparation_validates_the_last_payload_and_all_dependencies_before_replay_exists()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        var input = Input(session);
        var corrupt = input with { Commands = input.Commands.SetItem(1, input.Commands[1] with
        {
            CanonicalPayload = "{\"input_format_version\":1,\"kind\":\"content.publish\"}"
        }) };
        AssertFailure(corrupt, History, "unsupported_format", "Commands[1].CanonicalPayload/input_format_version");
        AssertFailure(input, [Content('A'), Content('B')], "content_missing", "Commands[1].CanonicalPayload/input_identity");
        AssertFailure(input, [Content('B'), Content('C')], "content_missing", "InitialStatePayload.InputIdentity");
        AssertFailure(input with { FormatVersion = 2 }, History, "unsupported_format", "FormatVersion");
        Assert.AreEqual(new SimulationPosition(0, 2), session.Read().Position);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Preparation_rejects_invalid_history_even_when_it_is_only_used_later_or_unused(bool unused)
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        var invalid = Content(unused ? 'D' : 'C') with { Designs = default };
        var history = unused ? History.Add(invalid) : History.SetItem(2, invalid);
        Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareReplay(Input(session), Runtime, history));
    }

    [TestMethod]
    [DataRow(true, "Commands[0].InputBefore")]
    [DataRow(false, "Commands[0].InputAfter")]
    public void Missing_recorded_input_dependencies_name_the_actual_member(bool before, string field)
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        var input = Input(session);
        var command = input.Commands.Single();
        var missing = Content('D').Identity;
        AssertFailure(input with
        {
            Commands = [before ? command with { InputBefore = missing } : command with { InputAfter = missing }]
        }, History, "content_missing", field);
    }

    [TestMethod]
    [DataRow("{\"input_format_version\":2,\"kind\":\"\\uD800\"}", "/kind")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"content.publish\",\"input_identity\":\"\\uD800\"}", "/input_identity")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"content.publish\",\"\\uD800\":1}", "")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"content.publish\",\"a/b~c\":1}", "/a~1b~0c")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"content.publish\",\"kind\":\"content.publish\"}", "/kind")]
    public void Malformed_payloads_fail_with_a_field_instead_of_throwing(string payload, string field)
    {
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void An_invalid_unicode_field_in_the_start_payload_is_a_preparation_failure()
    {
        using var session = Session();
        var input = Input(session);
        AssertFailure(input with
        {
            InitialStatePayload = input.InitialStatePayload.Replace("\"fault\":null", "\"\\uD800\":null", StringComparison.Ordinal)
        }, History, "invalid_state", "");
    }

    [TestMethod]
    public void A_recorded_rejection_is_checked_instead_of_assuming_the_command_succeeded()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        var input = Input(session);
        var command = input.Commands.Single();
        using var replay = Replay(input with
        {
            Commands = [command with
            {
                Result = command.Result with { Decision = new CommandDecision.Rejected([new("invalid_state", "test", null, [])]) },
                InputAfter = command.InputBefore
            }]
        });
        var result = replay.Advance(1);
        Assert.AreEqual(ReplayStop.FirstDifference, result.Stop);
        Assert.AreEqual(new SimulationPosition(0, 1), result.Difference!.Position);
        StringAssert.StartsWith(result.Difference.Field, "Result/Decision/");
    }

    [TestMethod]
    public void Receipt_difference_stops_at_the_first_command_and_names_its_field()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        session.Submit(new CoreCommand.PublishContent(Content('C')), CommandSource.Console);
        var input = Input(session);
        var first = input.Commands[0];
        using var replay = Replay(input with { Commands = input.Commands.SetItem(0,
            first with { Result = first.Result with { Number = 88 } }) });
        var result = replay.Advance(1);
        Assert.AreEqual(ReplayStop.FirstDifference, result.Stop);
        Assert.AreEqual(new SimulationPosition(0, 1), result.Position);
        Assert.AreEqual("Result/Number", result.Difference!.Field);
        Assert.AreEqual("88", result.Difference.Expected);
        Assert.AreEqual("1", result.Difference.Actual);
        Assert.AreEqual(result.Stop, replay.Advance(9).Stop);
        Assert.AreEqual(result.Position, replay.Read().Position);
    }

    [TestMethod]
    public void Receipt_comparison_uses_array_contents_and_world_identity_kinds()
    {
        CommandReceipt Receipt() => new(1, new(0, 0), new(0, 1), new CommandDecision.Rejected(
            [new("content_missing", "Design", new WorldObjectId.Node(new(8)), ["one", "two"])]));
        var expected = Receipt();
        var actual = Receipt();
        Assert.AreNotEqual(expected, actual);
        Assert.IsNull(CoreCheckpoints.Difference(new(0, 1), CoreCheckpoints.ReceiptText(expected), CoreCheckpoints.ReceiptText(actual)));
        var changed = actual with { Decision = new CommandDecision.Rejected(
            [new("content_missing", "Design", new WorldObjectId.Edge(new(8)), ["one", "two"])]) };
        Assert.AreEqual("/Decision/Problems/0/Subject/Kind", CoreCheckpoints.Difference(new(0, 1),
            CoreCheckpoints.ReceiptText(expected), CoreCheckpoints.ReceiptText(changed))!.Field);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Real_command_and_step_faults_are_reproduced_at_the_last_complete_boundary(bool command)
    {
        using var session = Session(ulong.MaxValue);
        if (command) session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        else session.Advance(1);
        using var replay = Replay(Input(session));
        var result = replay.Advance(1);
        Assert.AreEqual(ReplayStop.RecordedFault, result.Stop);
        Assert.AreEqual(session.Read().Fault, result.Fault);
        Assert.AreEqual(new SimulationPosition(0, 0), result.Position);
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(
            Success(session.Capture(CapturePurpose.Diagnostic)).Checkpoint, replay.CaptureBoundary().Checkpoint)));
    }

    [TestMethod]
    public void An_unrecorded_real_fault_has_a_distinct_stop_reason()
    {
        using var session = Session(ulong.MaxValue);
        var input = Input(session) with { EndPosition = new(2, 0), Checkpoints = [] };
        using var replay = Replay(input);
        var result = replay.Advance(2);
        Assert.AreEqual(ReplayStop.UnexpectedFault, result.Stop);
        Assert.AreEqual("Advance", result.Fault!.Operation);
    }

    [TestMethod]
    public void A_missing_recorded_step_fault_does_not_expose_state_past_the_end()
    {
        using var session = Session();
        using var replay = Replay(Input(session) with
        {
            ExpectedFault = new("internal_error", new(1, 0), "Advance", "", null)
        });
        var result = replay.Advance(1);
        Assert.AreEqual(ReplayStop.FirstDifference, result.Stop);
        Assert.AreEqual("Fault", result.Difference!.Field);
        Assert.AreEqual(new SimulationPosition(0, 0), result.Position);
        Assert.AreEqual(new CoreRevision(0), replay.Read().Revision);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void A_missing_recorded_command_fault_keeps_the_last_complete_boundary()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        var input = Input(session);
        var command = input.Commands.Single();
        var fault = new CoreFault("internal_error", new(0, 1), "Submit", "", null);
        using var replay = Replay(input with
        {
            Commands = [command with
            {
                Result = command.Result with { After = command.Result.Before, Decision = new CommandDecision.Faulted(fault) },
                InputAfter = command.InputBefore
            }],
            Checkpoints = [], EndPosition = new(0, 0), ExpectedFault = fault
        });
        var result = replay.Advance(1);
        Assert.AreEqual(ReplayStop.FirstDifference, result.Stop);
        Assert.AreEqual(new SimulationPosition(0, 0), result.Position);
        Assert.AreEqual(Content('A').Identity, replay.Read().InputIdentity);
        Assert.AreEqual(new CoreRevision(0), replay.Read().Revision);
        Assert.AreEqual(new SimulationPosition(0, 0), replay.CaptureBoundary().Position);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void Checkpoint_comparison_reports_fields_and_rejects_a_forged_digest()
    {
        using var session = Session();
        var actual = Success(session.Capture(CapturePurpose.Save)).Checkpoint;
        var text = actual.CanonicalText.Replace("\"next_command_number\":1", "\"next_command_number\":2", StringComparison.Ordinal);
        var expected = actual with { CanonicalText = text, Sha256 = Hash(text) };
        var comparison = (CheckpointComparison.Different)Success(CoreFactory.Create().CompareCheckpoints(expected, actual));
        Assert.AreEqual("/next_command_number", comparison.Difference.Field);
        Assert.AreEqual(actual.Position, comparison.Difference.Position);
        var failure = (CoreResult<CheckpointComparison>.Failure)CoreFactory.Create().CompareCheckpoints(
            expected with { Sha256 = actual.Sha256 }, actual);
        Assert.AreEqual("Sha256", failure.Problems.Single().Field);
        using var replay = Replay(Input(session) with { Checkpoints = [expected] });
        var result = replay.Advance(0);
        Assert.AreEqual(ReplayStop.FirstDifference, result.Stop);
        Assert.AreEqual("/next_command_number", result.Difference!.Field);
    }

    [TestMethod]
    public void A_world_field_difference_is_reported_by_comparison_and_replay_at_its_json_pointer()
    {
        using var session = Session();
        var actual = Success(session.Capture(CapturePurpose.Save)).Checkpoint;
        var node = JsonNode.Parse(actual.CanonicalText)!;
        node["world"]!["money"]!["value"] = 1001;
        var text = node.ToJsonString();
        var expected = actual with { CanonicalText = text, Sha256 = Hash(text) };
        var comparison = (CheckpointComparison.Different)Success(CoreFactory.Create().CompareCheckpoints(expected, actual));
        Assert.AreEqual("/world/money/value", comparison.Difference.Field);
        Assert.AreEqual("1001", comparison.Difference.Expected);
        Assert.AreEqual("1000", comparison.Difference.Actual);
        using var replay = Replay(Input(session) with { Checkpoints = [expected] });
        var result = replay.Advance(0);
        Assert.AreEqual(ReplayStop.FirstDifference, result.Stop);
        Assert.AreEqual(actual.Position, result.Position);
        Assert.AreEqual("/world/money/value", result.Difference!.Field);
    }

    [TestMethod]
    public void An_invalid_world_checkpoint_fails_validation_before_replay_exists()
    {
        using var session = Session();
        var actual = Success(session.Capture(CapturePurpose.Save)).Checkpoint;
        var node = JsonNode.Parse(actual.CanonicalText)!;
        node["world"]!["money"]!["value"] = -1;
        var text = node.ToJsonString();
        var invalid = actual with { CanonicalText = text, Sha256 = Hash(text) };
        var failure = (CoreResult<CheckpointComparison>.Failure)CoreFactory.Create().CompareCheckpoints(invalid, actual);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual("/world/money", failure.Problems.Single().Field);
        AssertFailure(Input(session) with { Checkpoints = [invalid] }, History, "invalid_state", "/world/money");
    }

    [TestMethod]
    public void Preparation_decodes_all_train_commands_before_execution()
    {
        using var session = Session();
        session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.Console);
        var input = Input(session);
        CoreCommand[] commands = [new CoreCommand.FormTrain([new(1)]), new CoreCommand.DisbandTrain(new(2)),
            new CoreCommand.ReplaceRunPlan(new(2), new(false, [new PlanStop.Waypoint(new(1))])),
            new CoreCommand.StartTrain(new(2)), new CoreCommand.StopTrain(new(2))];
        foreach (var command in commands)
        {
            var record = input.Commands.Single() with
            {
                CanonicalPayload = CoreCommandEncoding.Encode(command), InputAfter = Content('A').Identity
            };
            using var replay = Replay(input with { Commands = [record] });
            Assert.AreEqual(new SimulationPosition(0, 0), replay.Read().Position);
        }
        var invalid = input.Commands.Single() with
        {
            CanonicalPayload = "{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":0}"
        };
        AssertFailure(input with { Commands = [invalid] }, History, "invalid_state", "Commands[0].CanonicalPayload/train");
    }

    private static CoreReplayInput Input(CoreSession session)
    {
        var window = session.ReadRecording();
        return new(1, Runtime, window.Starts[0].State.Encode(), window.Commands, window.Checkpoints,
            window.EndPosition, session.Read().Fault);
    }

    private static ICoreReplay Replay(CoreReplayInput input)
        => Success(CoreFactory.Create().PrepareReplay(input, Runtime, History));
    private static CoreSession Session(ulong revision = 0) => new(WorldFixture.State(revision: new(revision), content: Content('A')));
    private static CoreContentProjection Content(char identity)
        => WorldFixture.Content with { Identity = new(new string(identity, 64)) };
    private static T Success<T>(CoreResult<T> result) where T : notnull => ((CoreResult<T>.Success)result).Value;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void AssertFailure(CoreReplayInput input, ImmutableArray<CoreContentProjection> history, string code, string field)
    {
        var failure = (CoreResult<ICoreReplay>.Failure)CoreFactory.Create().PrepareReplay(input, Runtime, history);
        Assert.AreEqual(code, failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }
}

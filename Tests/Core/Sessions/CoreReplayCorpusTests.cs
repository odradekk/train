using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using VerifyMSTest;
using VerifyTests;

namespace Train.Core.Tests.Sessions;

[TestClass]
[UsesVerify]
[TestCategory("integration")]
public sealed partial class CoreReplayCorpusTests
{
    private static readonly RuntimeIdentity Runtime = new("corpus-1", "core-1", "fixed-os", "fixed-arch", "fixed-dotnet");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "Sessions", "ReplayCorpus");

    [TestMethod]
    [DataRow("same-step", 0UL, 0UL, 1UL, 0UL, 6UL)]
    [DataRow("restored-boundary", 7UL, 2UL, 10UL, 20UL, 13UL)]
    public async Task Command_corpus_replays_against_committed_checkpoints(string name, ulong step,
        ulong sequence, ulong number, ulong revision, ulong end)
    {
        var actual = Replay(name, step, sequence, number, revision, end);
        var baseline = Path.Combine(Corpus, name + ".verified.txt");
        var difference = File.Exists(baseline)
            ? FirstDifference(JsonSerializer.Deserialize<CoreCheckpoint[]>(File.ReadAllText(baseline))!, actual)
            : null;
        var directory = Path.Combine(Path.GetTempPath(), "core-replay-corpus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (File.Exists(baseline))
            File.Copy(baseline, Path.Combine(directory, name + ".verified.txt"));
        var settings = new VerifySettings();
        settings.DisableDiff();
        settings.UseDirectory(directory);
        settings.UseFileName(name);
        try
        {
            await Verifier.Verify(JsonSerializer.Serialize(actual, Json), settings);
            Assert.IsNull(difference, Describe(difference));
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error)
        {
            Assert.Fail($"{Describe(difference)}\n{error.Message}\n语料基准：{baseline}\nVerify 接收目录：{directory}");
        }
    }

    [TestMethod]
    public void Modified_middle_baseline_reports_the_first_position_and_field()
    {
        var actual = Replay("same-step", 0, 0, 1, 0, 6);
        var baseline = JsonSerializer.Deserialize<CoreCheckpoint[]>(
            File.ReadAllText(Path.Combine(Corpus, "same-step.verified.txt")))!;
        Assert.IsTrue(baseline.Length >= 4);
        var modified = baseline.ToArray();
        for (var index = 1; index <= 2; index++)
        {
            var text = JsonNode.Parse(modified[index].CanonicalText)!.AsObject();
            text["next_command_number"] = text["next_command_number"]!.GetValue<ulong>() + 1;
            var canonical = text.ToJsonString();
            modified[index] = modified[index] with
            {
                CanonicalText = canonical,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            };
        }

        var difference = FirstDifference(modified, actual);

        Assert.IsNotNull(difference);
        Assert.AreEqual(baseline[1].Position, difference.Position);
        Assert.AreEqual("/next_command_number", difference.Field);
        Assert.AreNotEqual(difference.Expected, difference.Actual);
        TestContext.WriteLine(Describe(difference));
    }

    private static CoreCheckpoint[] Replay(string name, ulong step, ulong sequence, ulong number,
        ulong revision, ulong end)
    {
        var identity = new string('A', 64);
        var initial = $"{{\"core_state_format_version\":1,\"position\":{{\"completed_steps\":{step},\"sequence\":{sequence}}},\"next_command_number\":{number},\"revision\":{revision},\"input_identity\":\"{identity}\",\"fault\":null}}";
        var commands = File.ReadLines(Path.Combine(Corpus, name + ".commands.jsonl"))
            .Select(ReadCommand).ToImmutableArray();
        var history = new[] { 'A', 'B', 'C' }.Select(character =>
            new CoreContentProjection(new(new string(character, 64)), [], [], [], [], [], [], [], [], [], [], [], [], [], []))
            .ToImmutableArray();
        using var replay = Success(CoreFactory.Create().PrepareReplay(
            new(1, Runtime, initial, commands, [], new(end, 0), null), Runtime, history));
        var checkpoints = new List<CoreCheckpoint> { replay.CaptureBoundary().Checkpoint };
        while (true)
        {
            var result = replay.Advance(2);
            Assert.IsTrue(result.Stop is ReplayStop.BudgetReached or ReplayStop.EndOfRecording,
                $"语料 {name} 重放在 {result.Position} 停止：{result.Stop}；{Describe(result.Difference)}");
            checkpoints.Add(replay.CaptureBoundary().Checkpoint);
            if (result.Stop == ReplayStop.EndOfRecording)
                return checkpoints.ToArray();
        }
    }

    private static RecordedCommand ReadCommand(string line)
    {
        using var document = JsonDocument.Parse(line);
        var value = document.RootElement;
        var receipt = value.GetProperty("result");
        Assert.AreEqual("accepted", receipt.GetProperty("decision").GetString());
        return new(Enum.Parse<CommandSource>(value.GetProperty("source").GetString()!),
            value.GetProperty("canonical_payload").GetString()!,
            new(receipt.GetProperty("number").GetUInt64(), Position(receipt.GetProperty("before")),
                Position(receipt.GetProperty("after")), new CommandDecision.Accepted([], [])),
            new(value.GetProperty("input_before").GetString()!), new(value.GetProperty("input_after").GetString()!));
    }

    private static SimulationPosition Position(JsonElement value) =>
        new(value.GetProperty("completed_steps").GetUInt64(), value.GetProperty("sequence").GetUInt64());

    private static CheckpointDifference? FirstDifference(CoreCheckpoint[] expected, CoreCheckpoint[] actual)
    {
        var factory = CoreFactory.Create();
        for (var index = 0; index < Math.Min(expected.Length, actual.Length); index++)
        {
            if (Success(factory.CompareCheckpoints(expected[index], actual[index])) is CheckpointComparison.Different different)
                return different.Difference;
        }
        if (expected.Length != actual.Length)
        {
            var index = Math.Min(expected.Length, actual.Length);
            var position = index < expected.Length ? expected[index].Position : actual[index].Position;
            return new(position, $"/checkpoints/{index}", index < expected.Length ? "present" : "missing",
                index < actual.Length ? "present" : "missing");
        }
        return null;
    }

    private static string Describe(CheckpointDifference? difference) => difference is null
        ? "无检查点字段分歧。"
        : $"首个分歧：位置 {difference.Position.CompletedSteps}:{difference.Position.Sequence}，字段 {difference.Field}，预期 {difference.Expected}，实际 {difference.Actual}。";

    private static T Success<T>(CoreResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<CoreResult<T>.Success>(result);
        return ((CoreResult<T>.Success)result).Value;
    }
}

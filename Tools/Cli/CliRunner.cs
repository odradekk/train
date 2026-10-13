using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Cli;

internal sealed record GenerationIdentity(
    [property: JsonRequired] string Profile,
    [property: JsonRequired] ulong Seed,
    [property: JsonRequired] string GeneratorVersion,
    [property: JsonRequired] string Configuration,
    [property: JsonRequired] string GenerationRevision,
    [property: JsonRequired] string CoreInputId);
internal sealed record RunOutput(int ProcessId,
    [property: JsonRequired] RuntimeIdentity Runtime,
    [property: JsonRequired] GenerationIdentity Input,
    [property: JsonRequired] ImmutableArray<CoreCheckpoint> Checkpoints, int AcceptedCommands, int RejectedCommands,
    bool ReplayVerified);

internal sealed class CliRunner(ICoreFactory core, IContentFactory content, RuntimeIdentity runtime)
{
    internal RunOutput Run(string repository, Recipe recipe, bool verifyReplay)
    {
        const string versionPrefix = "config/version=";
        var versionLine = File.ReadLines(Path.Combine(repository, "project.godot"))
            .Single(line => line.StartsWith(versionPrefix, StringComparison.Ordinal));
        var gameVersion = JsonSerializer.Deserialize<string>(versionLine[versionPrefix.Length..])
            ?? throw new InvalidDataException("project.godot 缺少游戏版本。");
        using var loaded = Require(content.PrepareLoad(new(new(CliContentFiles.BuiltIn,
            CliContentFiles.Mods, null, gameVersion, []), new ContentLoadPurpose.Initial()),
            new CliContentFiles(repository), new PngAssetInspector()));
        if (loaded.Report.Diagnostics.Any(d => d.Severity == ContentDiagnosticSeverity.Error))
            throw new InvalidDataException(string.Join('\n', loaded.Report.Diagnostics.Select(d => $"{d.Field}: {d.Code} {d.Message}")));
        var view = loaded.Activate();
        var input = Require(content.CreateWorldInput(view, new(recipe.GenerationProfile), recipe.Seed, recipe.GeneratorVersion));
        using var candidate = Require(core.PrepareNew(input, view.Core, new(runtime, false, 1)));
        view = content.WithSettlementCopies(view, candidate.References.Templates);
        using var session = candidate.Activate();
        var checkpoints = ImmutableArray.CreateBuilder<CoreCheckpoint>();
        var accepted = 0;
        var rejected = 0;
        Sample();
        foreach (var command in recipe.Commands)
        {
            AdvanceTo(command.At);
            switch (session.Submit(command.Parse(), CommandSource.Console))
            {
                case SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }:
                    accepted++;
                    break;
                case SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected }:
                    rejected++;
                    break;
                case SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted fault }:
                    throw new InvalidDataException($"命令故障：{fault.Fault.Code} {fault.Fault.Field}。");
                case SubmissionResult.NotAccepted notAccepted:
                    throw new InvalidDataException($"命令未受理：{notAccepted.Reason.Code} {notAccepted.Reason.Field}。");
            }
            Sample();
        }
        AdvanceTo(recipe.Target);
        var samples = checkpoints.ToImmutable();
        if (verifyReplay) VerifyReplay(session.ReadRecording(), view, samples);
        return new(Environment.ProcessId, runtime, new(recipe.GenerationProfile, input.WorldSeed,
            input.GeneratorVersion, JsonSerializer.Serialize(input.Configuration, Recipe.Json),
            input.GenerationContentRevision.Value, view.Core.Identity.Value), samples, accepted, rejected, verifyReplay);

        void Sample() => checkpoints.Add(Require(session.Capture(CapturePurpose.Save)).Checkpoint);
        void AdvanceTo(SimulationPosition target)
        {
            var position = session.Read().Position;
            while (position.CompletedSteps < target.CompletedSteps)
            {
                var steps = (uint)Math.Min(uint.MaxValue, target.CompletedSteps - position.CompletedSteps);
                var result = session.Advance(steps);
                if (result.Fault is not null || result.CompletedSteps != steps)
                    throw new InvalidDataException($"推进失败：{result.Fault?.Code} {result.Fault?.Field}。");
                position = result.Position;
                Sample();
            }
            if (position != target) throw new InvalidDataException($"模拟位置不符：预期 {target}，实际 {position}。");
        }
    }

    internal void Compare(RunOutput expected, RunOutput actual)
    {
        if (expected.Input is null || actual.Input is null || expected.Input != actual.Input)
            throw new InvalidDataException("配方或内容已变，需要重新制作。");
        if (expected.Runtime != runtime || actual.Runtime != runtime)
            throw new InvalidDataException("构建或运行环境不兼容，不能验证确定性一致。");
        if (expected.Checkpoints.IsDefaultOrEmpty || actual.Checkpoints.IsDefaultOrEmpty)
            throw new InvalidDataException("运行结果缺少检查点。");
        if (expected.Checkpoints.Length != actual.Checkpoints.Length)
            throw new InvalidDataException("检查点数量不同。");
        for (var index = 0; index < expected.Checkpoints.Length; index++)
            Compare(expected.Checkpoints[index], actual.Checkpoints[index]);
    }

    private void VerifyReplay(CoreRecordingWindow recording, IContentView view, ImmutableArray<CoreCheckpoint> checkpoints)
    {
        using var replay = Require(core.PrepareReplay(new(1, runtime, recording.Starts[0].State.Encode(),
            recording.Commands, checkpoints, recording.EndPosition, null), runtime, [view.Core]));
        foreach (var checkpoint in checkpoints)
        {
            var result = replay.Advance(uint.MaxValue, checkpoint.Position);
            if (result.Stop != ReplayStop.RequestedPosition)
                throw new InvalidDataException($"严格重放停止：{result.Stop} {result.Position} {result.Difference?.Field}。");
            Compare(checkpoint, replay.CaptureBoundary().Checkpoint);
        }
        var end = replay.Advance(0);
        if (end.Stop != ReplayStop.EndOfRecording)
            throw new InvalidDataException($"严格重放未结束：{end.Stop}。");
    }

    private void Compare(CoreCheckpoint expected, CoreCheckpoint actual)
    {
        if (Require(core.CompareCheckpoints(expected, actual)) is CheckpointComparison.Different different)
            throw new InvalidDataException($"检查点不同：{different.Difference.Position} {different.Difference.Field}；"
                + $"预期 {different.Difference.Expected}，实际 {different.Difference.Actual}。");
    }

    private static T Require<T>(CoreResult<T> result) where T : notnull => result switch
    {
        CoreResult<T>.Success success => success.Value,
        CoreResult<T>.Failure failure => throw new InvalidDataException(string.Join('\n', failure.Problems.Select(p => $"{p.Code}: {p.Field}"))),
        CoreResult<T>.Faulted fault => throw new InvalidDataException($"{fault.Fault.Code}: {fault.Fault.Field}"),
        _ => throw new InvalidOperationException("未知核心结果。")
    };

    private static T Require<T>(ContentResult<T> result) where T : notnull => result switch
    {
        ContentResult<T>.Success success => success.Value,
        ContentResult<T>.Failure failure => throw new InvalidDataException(string.Join('\n', failure.Diagnostics.Select(d => $"{d.Code}: {d.Field} {d.Message}"))),
        ContentResult<T>.Faulted fault => throw new InvalidDataException($"{fault.Code}: {fault.Message}"),
        _ => throw new InvalidOperationException("未知内容结果。")
    };
}

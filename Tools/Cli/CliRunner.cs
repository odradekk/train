using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.App.Saves;
using System.Text;

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
        var prepared = PrepareNew(repository, recipe);
        using var session = prepared.Core;
        return Execute(prepared, recipe, recipe.Target, verifyReplay);
    }

    internal void Save(string repository, Recipe recipe, string target, ulong? untilStep)
    {
        var prepared = PrepareNew(repository, recipe);
        using var session = prepared.Core;
        var end = untilStep is { } step ? new SimulationPosition(step, 0) : recipe.Target;
        Execute(prepared, recipe, end, false);
        var state = Require(session.Capture(CapturePurpose.Save));
        var embedded = Require(content.Capture(prepared.Content, state.References));
        var document = new SaveDocument(new(SaveContainer.FormatVersion, recipe.Save.Id, recipe.Save.Name,
            recipe.Save.SavedAt, new(state.Position.CompletedSteps, state.Position.Sequence),
            embedded.ReferencedPackages.Select(p => new SavePackage(p.Id.Value, p.Version)).ToImmutableArray()),
            state.Encode(), embedded.Encode());
        var result = Store(target).Write(target, document, overwrite: false);
        if (!result.Published) throw new IOException($"{result.Code}: {result.Message}");
        if (result.CleanupMessage is not null) Console.Error.WriteLine(result.CleanupMessage);
        File.WriteAllText(InputPath(target), JsonSerializer.Serialize(prepared.Input, Recipe.Json), new UTF8Encoding(false));
    }

    internal RunOutput Resume(string repository, Recipe recipe, string path)
    {
        var prepared = PrepareRestore(repository, recipe, Store(path).Read(path));
        using var session = prepared.Core;
        return Execute(prepared, recipe, recipe.Target, false);
    }

    internal void CheckExample(string repository, Recipe recipe, string path)
    {
        var expected = JsonSerializer.Deserialize<GenerationIdentity>(File.ReadAllText(InputPath(path)), Recipe.Json)
            ?? throw new InvalidDataException("示例缺少生成输入身份。");
        var fresh = PrepareNew(repository, recipe, expected);
        using var freshSession = fresh.Core;
        var actual = Execute(fresh, recipe, recipe.Target, false);
        var restored = PrepareRestore(repository, recipe, Store(path).Read(path));
        using var restoredSession = restored.Core;
        CompareInputs(expected, restored.Input);
        Compare(Require(restoredSession.Capture(CapturePurpose.Save)).Checkpoint, actual.Checkpoints[^1]);
    }

    internal static string InputPath(string savePath) => Path.ChangeExtension(savePath, "input.json");
    private static FileSaveStore Store(string path) => new(Path.GetDirectoryName(Path.GetFullPath(path))!);

    private IContentCandidate Load(string repository, ContentLoadPurpose purpose)
    {
        const string versionPrefix = "config/version=";
        var versionLine = File.ReadLines(Path.Combine(repository, "project.godot"))
            .Single(line => line.StartsWith(versionPrefix, StringComparison.Ordinal));
        var gameVersion = JsonSerializer.Deserialize<string>(versionLine[versionPrefix.Length..])
            ?? throw new InvalidDataException("project.godot 缺少游戏版本。");
        var loaded = Require(content.PrepareLoad(new(new(CliContentFiles.BuiltIn,
            CliContentFiles.Mods, null, gameVersion, []), purpose),
            new CliContentFiles(repository), new PngAssetInspector()));
        if (loaded.Report.Diagnostics.Any(d => d.Severity == ContentDiagnosticSeverity.Error))
        {
            var message = string.Join('\n', loaded.Report.Diagnostics.Select(d => $"{d.Field}: {d.Code} {d.Message}"));
            loaded.Dispose();
            throw new InvalidDataException(message);
        }
        if (!loaded.Report.RuntimeMissing.IsEmpty)
        {
            var message = string.Join('\n', loaded.Report.RuntimeMissing.Select(m => $"缺少内容：{m.Field}"));
            loaded.Dispose();
            throw new InvalidDataException(message);
        }
        return loaded;
    }

    private (ICoreSession Core, IContentView Content, GenerationIdentity Input) PrepareNew(
        string repository, Recipe recipe, GenerationIdentity? expected = null)
    {
        using var loaded = Load(repository, new ContentLoadPurpose.Initial());
        var view = loaded.View;
        var input = Require(content.CreateWorldInput(view, new(recipe.GenerationProfile), recipe.Seed, recipe.GeneratorVersion));
        var identity = Identity(recipe, input, view.Core.Identity);
        if (expected is not null) CompareInputs(expected, identity);
        using var candidate = Require(core.PrepareNew(input, view.Core, new(runtime, false, 1)));
        view = content.WithSettlementCopies(view, candidate.References.Templates);
        loaded.Activate();
        return (candidate.Activate(), view, identity);
    }

    private (ICoreSession Core, IContentView Content, GenerationIdentity Input) PrepareRestore(
        string repository, Recipe recipe, SaveDocument document)
    {
        var state = Require(core.DecodeState(document.CorePayload));
        var embedded = Require(content.DecodeCapture(document.ContentPayload));
        using var loaded = Load(repository, new ContentLoadPurpose.Restore(embedded, state.References));
        using var candidate = Require(core.PrepareRestore(state, loaded.View.Core, new(runtime, false, 1)));
        if (!candidate.MissingReferences.IsEmpty)
            throw new InvalidDataException(string.Join('\n', candidate.MissingReferences.Select(m => $"缺少引用：{m.FieldPath}")));
        var view = loaded.View;
        var generation = Require(content.CreateWorldInput(view, new(recipe.GenerationProfile), recipe.Seed, recipe.GeneratorVersion));
        var identity = Identity(recipe, generation with { WorldSeed = candidate.View.World.WorldSeed }, state.InputIdentity);
        loaded.Activate();
        return (candidate.Activate(), view, identity);
    }

    private static GenerationIdentity Identity(Recipe recipe, WorldGenerationInput input, CoreInputId coreInput) =>
        new(recipe.GenerationProfile, input.WorldSeed, input.GeneratorVersion,
            JsonSerializer.Serialize(input.Configuration, Recipe.Json), input.GenerationContentRevision.Value, coreInput.Value);

    private RunOutput Execute((ICoreSession Core, IContentView Content, GenerationIdentity Input) prepared, Recipe recipe,
        SimulationPosition target, bool verifyReplay)
    {
        var session = prepared.Core;
        var start = session.Read().Position;
        if (Order(target, start) < 0 || Order(target, recipe.Target) > 0)
            throw new InvalidDataException("目标位置不在配方的剩余运行范围内。");
        var checkpoints = ImmutableArray.CreateBuilder<CoreCheckpoint>();
        var accepted = 0;
        var rejected = 0;
        Sample();
        foreach (var command in recipe.Commands)
        {
            if (Order(command.At, start) < 0) continue;
            if (Order(command.At, target) >= 0) break;
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
        AdvanceTo(target);
        var samples = checkpoints.ToImmutable();
        if (verifyReplay) VerifyReplay(session.ReadRecording(), prepared.Content, samples);
        return new(Environment.ProcessId, runtime, prepared.Input, samples, accepted, rejected, verifyReplay);

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
        CompareInputs(expected.Input, actual.Input);
        if (expected.Runtime != runtime || actual.Runtime != runtime)
            throw new InvalidDataException("构建或运行环境不兼容，不能验证确定性一致。");
        if (expected.Checkpoints.IsDefaultOrEmpty || actual.Checkpoints.IsDefaultOrEmpty)
            throw new InvalidDataException("运行结果缺少检查点。");
        var offset = 0;
        while (offset < expected.Checkpoints.Length && expected.Checkpoints[offset].Position != actual.Checkpoints[0].Position) offset++;
        if (expected.Checkpoints.Length - offset != actual.Checkpoints.Length)
            throw new InvalidDataException("检查点数量不同。");
        for (var index = 0; index < actual.Checkpoints.Length; index++)
            Compare(expected.Checkpoints[index + offset], actual.Checkpoints[index]);
    }

    private static void CompareInputs(GenerationIdentity? expected, GenerationIdentity? actual)
    {
        if (expected is null || actual is null || expected != actual)
            throw new InvalidDataException("配方或内容已变，需要重新制作。");
    }

    private static int Order(SimulationPosition left, SimulationPosition right) =>
        left.CompletedSteps != right.CompletedSteps ? left.CompletedSteps.CompareTo(right.CompletedSteps)
            : left.Sequence.CompareTo(right.Sequence);

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

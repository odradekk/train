using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.App.Saves;

namespace Train.Cli;

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
    }

    internal RunOutput Resume(string repository, Recipe recipe, string path)
    {
        var prepared = PrepareRestore(repository, Store(path).Read(path));
        using var session = prepared.Core;
        return Execute(prepared, recipe, recipe.Target, false);
    }

    internal void CheckExample(string repository, Recipe recipe, string path)
    {
        var fresh = PrepareNew(repository, recipe);
        using var freshSession = fresh.Core;
        var actual = Execute(fresh, recipe, recipe.Target, false);
        var restored = PrepareRestore(repository, Store(path).Read(path));
        using var restoredSession = restored.Core;
        CompareInputs(fresh.InputIdentity, restored.InputIdentity);
        Compare(Require(restoredSession.Capture(CapturePurpose.Save)).Checkpoint, actual.Checkpoints[^1]);
    }

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

    private (ICoreSession Core, IContentView Content, CoreInputId InputIdentity) PrepareNew(
        string repository, Recipe recipe)
    {
        using var loaded = Load(repository, new ContentLoadPurpose.Initial());
        var view = loaded.View;
        var input = Require(content.CreateWorldInput(view, new(recipe.GenerationProfile), recipe.Seed, recipe.GeneratorVersion));
        var identity = view.Core.Identity;
        using var candidate = Require(core.PrepareNew(input, view.Core, new(runtime, false, 1)));
        view = content.WithSettlementCopies(view, candidate.References.Templates);
        loaded.Activate();
        return (candidate.Activate(), view, identity);
    }

    private (ICoreSession Core, IContentView Content, CoreInputId InputIdentity) PrepareRestore(
        string repository, SaveDocument document)
    {
        var state = Require(core.DecodeState(document.CorePayload));
        var embedded = Require(content.DecodeCapture(document.ContentPayload));
        using var loaded = Load(repository, new ContentLoadPurpose.Restore(embedded, state.References));
        using var candidate = Require(core.PrepareRestore(state, loaded.View.Core, new(runtime, false, 1)));
        if (!candidate.MissingReferences.IsEmpty)
            throw new InvalidDataException(string.Join('\n', candidate.MissingReferences.Select(m => $"缺少引用：{m.FieldPath}")));
        var view = loaded.View;
        loaded.Activate();
        return (candidate.Activate(), view, state.InputIdentity);
    }

    private RunOutput Execute((ICoreSession Core, IContentView Content, CoreInputId InputIdentity) prepared, Recipe recipe,
        SimulationPosition target, bool verifyReplay)
    {
        var session = prepared.Core;
        var start = session.Read().Position;
        if (Order(target, start) < 0 || Order(target, recipe.Target) > 0)
            throw new InvalidDataException("目标位置不在配方的剩余运行范围内。");
        var checkpoints = ImmutableArray.CreateBuilder<CoreCheckpoint>();
        var observations = ImmutableArray.CreateBuilder<EconomySample>();
        var receipts = ImmutableArray.CreateBuilder<RecipeReceipt>();
        var checkpointSteps = recipe.CheckpointSteps.IsDefault ? [] : recipe.CheckpointSteps;
        var accepted = 0;
        var rejected = 0;
        Sample();
        for (var index = 0; index < recipe.Commands.Length; index++)
        {
            var command = recipe.Commands[index];
            if (Order(command.At, start) < 0) continue;
            if (Order(command.At, target) >= 0) break;
            AdvanceTo(command.At);
            var parsed = command.Parse(prepared.Content);
            long? quotedTotal = parsed switch
            {
                CoreCommand.BuyVehicle buy when session.Read().QuoteVehicle(new(buy.Seller, buy.Vehicle, VehicleTradeAction.Buy))
                    is CoreResult<VehicleTradeQuote>.Success quote => quote.Value.Total?.Value,
                CoreCommand.CommissionVehicleWork commission when session.Read().GetJobQuote(commission.Request, JobFunding.NpcCommission)
                    is CoreResult<JobQuote>.Success quote => quote.Value.TotalPayment?.Value,
                _ => null
            };
            switch (session.Submit(parsed, CommandSource.Console))
            {
                case SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }:
                    accepted++;
                    receipts.Add(new(index, command.Kind, "accepted", [], quotedTotal));
                    break;
                case SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected decision }:
                    rejected++;
                    receipts.Add(new(index, command.Kind, "rejected",
                        decision.Problems.Select(ProblemSample.From).ToImmutableArray(), quotedTotal));
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
        return new(Environment.ProcessId, runtime, prepared.InputIdentity, samples, accepted, rejected, verifyReplay,
            observations.ToImmutable(), receipts.ToImmutable());

        void Sample()
        {
            checkpoints.Add(Require(session.Capture(CapturePurpose.Save)).Checkpoint);
            observations.Add(EconomySample.Read(session.Read()));
        }
        void AdvanceTo(SimulationPosition target)
        {
            var position = session.Read().Position;
            while (position.CompletedSteps < target.CompletedSteps)
            {
                var next = checkpointSteps.FirstOrDefault(step => step > position.CompletedSteps
                    && step < target.CompletedSteps, target.CompletedSteps);
                var steps = (uint)Math.Min(uint.MaxValue, next - position.CompletedSteps);
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
        CompareInputs(expected.InputIdentity, actual.InputIdentity);
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

    private static void CompareInputs(CoreInputId expected, CoreInputId actual)
    {
        if (expected != actual)
            throw new InvalidDataException("配方或内容已变，需要重新制作。");
    }

    private static int Order(SimulationPosition left, SimulationPosition right) =>
        left.CompletedSteps != right.CompletedSteps ? left.CompletedSteps.CompareTo(right.CompletedSteps)
            : left.Sequence.CompareTo(right.Sequence);

    private void VerifyReplay(CoreRecordingWindow recording, IContentView view, ImmutableArray<CoreCheckpoint> checkpoints)
    {
        using var replay = Require(core.PrepareReplay(new(2, runtime, recording.Starts[0].State.Encode(),
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

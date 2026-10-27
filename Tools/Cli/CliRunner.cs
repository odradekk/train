using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.App.Measurement;
using Train.App.Saves;

namespace Train.Cli;

internal sealed class CliRunner(ICoreFactory core, IContentFactory content, RuntimeIdentity runtime)
{
    private static readonly JsonSerializerOptions ProgressJson = new(Recipe.Json) { WriteIndented = false };

    internal RunOutput Run(string repository, Recipe recipe, bool verifyReplay, string progress)
    {
        var prepared = PrepareNew(repository, recipe);
        using var session = prepared.Core;
        return Execute(prepared, recipe, recipe.Target, verifyReplay, progress).Output;
    }

    internal void Save(string repository, Recipe recipe, string target, ulong? untilStep)
    {
        var prepared = PrepareNew(repository, recipe);
        using var session = prepared.Core;
        var end = untilStep is { } step ? new SimulationPosition(step, 0) : recipe.Target;
        var executed = Execute(prepared, recipe, end, false, null);
        RequireNoFindings(executed.Output);
        var state = Require(session.Capture(CapturePurpose.Save));
        var embedded = Require(content.Capture(executed.Content, state.References));
        var document = new SaveDocument(new(SaveContainer.FormatVersion, recipe.Save.Id, recipe.Save.Name,
            recipe.Save.SavedAt, new(state.Position.CompletedSteps, state.Position.Sequence),
            embedded.ReferencedPackages.Select(p => new SavePackage(p.Id.Value, p.Version)).ToImmutableArray()),
            state.Encode(), embedded.Encode());
        var result = Store(target).Write(target, document, overwrite: false);
        if (!result.Published) throw new IOException($"{result.Code}: {result.Message}");
        if (result.CleanupMessage is not null) Console.Error.WriteLine(result.CleanupMessage);
    }

    internal RunOutput Resume(string repository, Recipe recipe, string path, string progress)
    {
        var prepared = PrepareRestore(repository, Store(path).Read(path));
        using var session = prepared.Core;
        return Execute(prepared, recipe, recipe.Target, false, progress).Output;
    }

    internal void CheckExample(string repository, Recipe recipe, string path)
    {
        var fresh = PrepareNew(repository, recipe);
        using var freshSession = fresh.Core;
        var actual = Execute(fresh, recipe, recipe.Target, false, null).Output;
        RequireNoFindings(actual);
        var restored = PrepareRestore(repository, Store(path).Read(path));
        using var restoredSession = restored.Core;
        CompareInputs(fresh.InputIdentity, restored.InputIdentity);
        Compare(Require(restoredSession.Capture(CapturePurpose.Save)).Checkpoint, actual.Checkpoints[^1]);
    }

    internal static FileSaveStore Store(string path) => new(Path.GetDirectoryName(Path.GetFullPath(path))!);

    internal static ContentInstallation Installation(string repository)
    {
        const string versionPrefix = "config/version=";
        var versionLine = File.ReadLines(Path.Combine(repository, "project.godot"))
            .Single(line => line.StartsWith(versionPrefix, StringComparison.Ordinal));
        var gameVersion = JsonSerializer.Deserialize<string>(versionLine[versionPrefix.Length..])
            ?? throw new InvalidDataException("project.godot 缺少游戏版本。");
        return new(CliContentFiles.BuiltIn, CliContentFiles.Mods, null, gameVersion, []);
    }

    private IContentCandidate Load(string repository, ContentLoadPurpose purpose) =>
        Load(Installation(repository), purpose, new CliContentFiles(repository), new AssetInspector());

    internal IContentCandidate Load(ContentInstallation installation, ContentLoadPurpose purpose,
        IContentFiles files, IAssetInspector inspector)
    {
        var loaded = Require(content.PrepareLoad(new(installation, purpose), files, inspector));
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

    internal static void RequireNoFindings(RunOutput output)
    {
        if (!output.Findings.IsDefaultOrEmpty)
            throw new InvalidDataException("不变量不满足：\n" + string.Join('\n', output.Findings));
    }

    private (RunOutput Output, IContentView Content) Execute((ICoreSession Core, IContentView Content, CoreInputId InputIdentity) prepared, Recipe recipe,
        SimulationPosition target, bool verifyReplay, string? progress)
    {
        var session = prepared.Core;
        var view = prepared.Content;
        var start = session.Read().Position;
        if (Order(target, start) < 0 || Order(target, recipe.Target) > 0)
            throw new InvalidDataException("目标位置不在配方的剩余运行范围内。");
        var checkpoints = ImmutableArray.CreateBuilder<CoreCheckpoint>();
        var observations = ImmutableArray.CreateBuilder<EconomySample>();
        var receipts = ImmutableArray.CreateBuilder<RecipeReceipt>();
        var transfers = ImmutableArray.CreateBuilder<GoodsTransferSample>();
        var completions = ImmutableArray.CreateBuilder<ProductionCompletedSample>();
        var stateChanges = ImmutableArray.CreateBuilder<ProductionStateSample>();
        var checkpointSteps = recipe.CheckpointSteps.IsDefault ? [] : recipe.CheckpointSteps;
        var accepted = 0;
        var rejected = 0;
        var findings = ImmutableArray.CreateBuilder<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var jobProgress = new Dictionary<JobId, double>();
        long retainedBytes = 0;
        using var progressWriter = progress is null ? null : new StreamWriter(progress, false, new UTF8Encoding(false)) { AutoFlush = true };
        var clock = Stopwatch.StartNew();
        Sample();
        var measured = (Steps: start.CompletedSteps, Seconds: 0d, Transfers: 0, Completions: 0,
            Progress: new Dictionary<JobId, double>(jobProgress));
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
            var submission = session.Submit(parsed, CommandSource.Console);
            if (submission is SubmissionResult.Completed completed)
            {
                if (completed.Receipt.Decision is CommandDecision.Accepted { SettlementCopies: { Length: > 0 } copies })
                    view = content.WithSettlementCopies(view, copies);
                Observe(completed.Events);
            }
            switch (submission)
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
            if (command.ExpectedDecision is { } expected && expected != receipts[^1].Decision)
            {
                var problems = string.Join(", ", receipts[^1].Problems.Select(problem => $"{problem.Code}: {problem.Field}"));
                throw new InvalidDataException($"命令 {index}（{command.Kind}）的裁决不符：预期 {expected}，实际 {receipts[^1].Decision}"
                    + (problems.Length == 0 ? "。" : $"；问题：{problems}。"));
            }
            Sample();
        }
        AdvanceTo(target);
        var samples = checkpoints.ToImmutable();
        if (verifyReplay) VerifyReplay(session.ReadRecording(), view, samples);
        return (new(Environment.ProcessId, runtime, prepared.InputIdentity, samples, accepted, rejected, verifyReplay,
            observations.ToImmutable(), receipts.ToImmutable(), transfers.ToImmutable(), completions.ToImmutable(), stateChanges.ToImmutable(),
            findings.ToImmutable()), view);

        void Observe(ImmutableArray<EventEnvelope> events)
        {
            foreach (var envelope in events)
                switch (envelope.Event)
                {
                    case CoreEvent.GoodsTransferred transfer:
                        transfers.Add(new(envelope.Position, envelope.Ordinal, transfer.Action.ToString(),
                            GoodSample.From(transfer.Good), transfer.Quantity, transfer.Source.Value,
                            transfer.Destination.Value, transfer.ActualAmount.Value, transfer.Train.Value, transfer.Settlement.Value));
                        break;
                    case CoreEvent.ProductionCompleted production:
                        completions.Add(new(envelope.Position, envelope.Ordinal, FacilityKeySample.From(production.Facility),
                            ProcessSample.From(production.Process), production.Outputs.Select(GoodsSample.From).ToImmutableArray(), production.Buffered));
                        break;
                    case CoreEvent.ProductionStateChanged state:
                        stateChanges.Add(new(envelope.Position, envelope.Ordinal, FacilityKeySample.From(state.Facility),
                            state.Before.ToString(), state.After.ToString(), state.Reasons.Select(ProblemSample.From).ToImmutableArray()));
                        break;
                }
        }

        void Sample()
        {
            var capture = Require(session.Capture(CapturePurpose.Save));
            checkpoints.Add(capture.Checkpoint);
            retainedBytes += checkpoints[^1].CanonicalText.Length * sizeof(char);
            var read = session.Read();
            observations.Add(EconomySample.Read(read, view));
            foreach (var (item, field, detail) in WorldInvariants.Check(read, jobProgress))
                if (reported.Add(item + " " + field)) findings.Add($"{read.Position} {item} {field}：{detail}");
            // 每个样本都要能通过读档时的状态校验，否则问题要等到恢复时才暴露。
            switch (core.DecodeState(capture.Encode()))
            {
                case CoreResult<ICoreCapture>.Failure decode:
                    foreach (var problem in decode.Problems)
                        if (reported.Add("state " + problem.Field))
                            findings.Add($"{read.Position} state {problem.Field}：读档校验拒绝（{problem.Code}）");
                    break;
                case CoreResult<ICoreCapture>.Faulted fault when reported.Add("state fault " + fault.Fault.Field):
                    findings.Add($"{read.Position} state {fault.Fault.Field}：读档校验故障（{fault.Fault.Code}）");
                    break;
            }
            if (read.Position.Sequence != 0) return;
            var nodeCounts = new Dictionary<NodeId, ImmutableSortedDictionary<string, double>>();
            foreach (var expectation in recipe.Expectations.Where(item => item.At == read.Position.CompletedSteps))
            {
                var counts = observations[^1].Counts;
                if (expectation.Node is { } node)
                {
                    var id = new NodeId(node);
                    if (!nodeCounts.TryGetValue(id, out counts))
                    {
                        counts = WorldCounts.Read(read, view, id);
                        nodeCounts.Add(id, counts);
                    }
                }
                var actual = counts[expectation.Key];
                if (actual < expectation.Minimum || actual > expectation.Maximum)
                    throw new InvalidDataException($"{expectation.At}:0 的 {expectation.Key}"
                        + (expectation.Node is { } at ? $"（节点 {at}）" : "")
                        + $"不符：预期 {expectation.Minimum?.ToString() ?? "-∞"}–{expectation.Maximum?.ToString() ?? "∞"}，实际 {actual}。");
            }
        }
        void AdvanceTo(SimulationPosition until)
        {
            var position = session.Read().Position;
            while (position.CompletedSteps < until.CompletedSteps)
            {
                var next = checkpointSteps.FirstOrDefault(step => step > position.CompletedSteps
                    && step < until.CompletedSteps, until.CompletedSteps);
                var steps = (uint)Math.Min(uint.MaxValue, next - position.CompletedSteps);
                var result = session.Advance(steps);
                if (result.Fault is not null || result.CompletedSteps != steps)
                    throw new InvalidDataException($"推进失败：{result.Fault?.Code} {result.Fault?.Field}。");
                Observe(result.Events);
                position = result.Position;
                Sample();
                if (checkpointSteps.Contains(position.CompletedSteps) || position == target) Measure();
            }
            if (position != until) throw new InvalidDataException($"模拟位置不符：预期 {until}，实际 {position}。");
        }

        void Measure()
        {
            if (progressWriter is null) return;
            var read = session.Read();
            var seconds = clock.Elapsed.TotalSeconds;
            var counts = WorldCounts.Read(read, view).ToBuilder();
            counts["goods_transfers"] = transfers.Count - measured.Transfers;
            counts["production_completions"] = completions.Count - measured.Completions;
            counts["jobs_progressed"] = jobProgress.Count(job => job.Value > measured.Progress.GetValueOrDefault(job.Key));
            var misses = recipe.Load is { } load && read.Position.CompletedSteps >= load.From
                ? load.Targets.Where(item => counts[item.Key] < item.Minimum || counts[item.Key] > item.Maximum)
                    .Select(item => $"{item.Key}={counts[item.Key]}").ToImmutableArray()
                : [];
            using var process = Process.GetCurrentProcess();
            progressWriter.WriteLine(JsonSerializer.Serialize(new RunMeasurement(read.Position, seconds,
                (read.Position.CompletedSteps - measured.Steps) / (seconds - measured.Seconds),
                GC.GetTotalMemory(forceFullCollection: true), ProcessMemory.Read(process, 0).Private, process.WorkingSet64,
                retainedBytes, counts.ToImmutable(), misses), ProgressJson));
            // 强制回收和写进度不计入下一段模拟速度。
            measured = (read.Position.CompletedSteps, clock.Elapsed.TotalSeconds, transfers.Count, completions.Count,
                new Dictionary<JobId, double>(jobProgress));
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
            throw new InvalidDataException($"检查点不同：{different.Difference.Position} {different.Difference.Field}"
                + $"{Objects(expected.CanonicalText, different.Difference.Field)}；"
                + $"预期 {different.Difference.Expected}，实际 {different.Difference.Actual}。");
    }

    // 字段指针里的数组只有下标；沿指针列出经过的数组元素的 id，报告差异属于哪个对象。
    private static string Objects(string canonical, string field)
    {
        using var document = JsonDocument.Parse(canonical);
        var current = document.RootElement;
        var objects = new List<string>();
        var name = "";
        foreach (var segment in field.Split('/').Skip(1).Select(part => part.Replace("~1", "/").Replace("~0", "~")))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property))
            {
                (current, name) = (property, segment);
                continue;
            }
            if (current.ValueKind != JsonValueKind.Array || !int.TryParse(segment, out var index) || index >= current.GetArrayLength()) break;
            current = current[index];
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("id", out var id))
                objects.Add($"{name} {id.GetRawText()}");
        }
        return objects.Count == 0 ? "" : $"（对象：{string.Join(" / ", objects)}）";
    }

    internal static T Require<T>(CoreResult<T> result) where T : notnull => result switch
    {
        CoreResult<T>.Success success => success.Value,
        CoreResult<T>.Failure failure => throw new InvalidDataException(string.Join('\n', failure.Problems.Select(p => $"{p.Code}: {p.Field}"))),
        CoreResult<T>.Faulted fault => throw new InvalidDataException($"{fault.Fault.Code}: {fault.Fault.Field}"),
        _ => throw new InvalidOperationException("未知核心结果。")
    };

    internal static T Require<T>(ContentResult<T> result) where T : notnull => result switch
    {
        ContentResult<T>.Success success => success.Value,
        ContentResult<T>.Failure failure => throw new InvalidDataException(string.Join('\n', failure.Diagnostics.Select(d => $"{d.Code}: {d.Field} {d.Message}"))),
        ContentResult<T>.Faulted fault => throw new InvalidDataException($"{fault.Code}: {fault.Message}"),
        _ => throw new InvalidOperationException("未知内容结果。")
    };
}

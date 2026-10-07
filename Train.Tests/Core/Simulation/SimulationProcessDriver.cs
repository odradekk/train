// Train.Tests.Core.Simulation 在独立进程中运行固定确定性场景。
// 驱动只编排真实会话、调度器、载荷文件和可供父进程核验的证据。

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

internal static class SimulationProcessDriver
{
    private const string SemanticContent = "fixture.resource/v1;stock,holdings,entity_ids,demands;test-generator-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int UnknownInvocation(string[] args)
    {
        System.Console.Error.WriteLine($"未知 Train.Tests 进程参数：{string.Join(' ', args)}");
        return 64;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            var commandCodec = new ResourceCommandCodec();
            var stateCodec = new ResourceStateCodec();
            var sourcePayload = stateCodec.Encode(stateCodec.Capture(CreateInitialState()));
            var sourceSha = Sha256(sourcePayload);

            SimulationSession session;
            var samples = ImmutableArray.CreateBuilder<ProcessCheckpoint>();
            var executions = ImmutableArray.CreateBuilder<ProcessExecution>();
            var host = new ProcessHost(options.Schedule);
            if (options.Mode == "resume")
            {
                var payload = await File.ReadAllTextAsync(Required(options.Input, "--input"), Encoding.UTF8);
                var decoded = stateCodec.Decode(payload);
                if (decoded is not DeterminismResult<CoreStateSnapshot>.Success decodedState)
                    throw new InvalidOperationException($"恢复载荷读取失败：{Describe(decoded)}");
                var restored = stateCodec.Restore(decodedState.Value, new ResourceCalculations(), commandCodec);
                if (restored is not DeterminismResult<SimulationSession>.Success restoredSession)
                    throw new InvalidOperationException($"恢复会话失败：{Describe(restored)}");
                session = restoredSession.Value;
                AddSample(stateCodec, session.GetState(), samples);
                RunRemainder(session, stateCodec, host, samples, executions);
            }
            else
            {
                session = new SimulationSession(CreateInitialState(), new ResourceCalculations(), commandCodec);
                RunPrefix(session, stateCodec, host, samples, executions);
                if (options.Mode == "save")
                {
                    var payload = stateCodec.Encode(stateCodec.Capture(session.GetState()));
                    await File.WriteAllTextAsync(Required(options.Payload, "--payload"), payload, new UTF8Encoding(false));
                }
                else if (options.Mode == "run")
                {
                    RunRemainder(session, stateCodec, host, samples, executions);
                }
                else
                {
                    throw new ArgumentException($"未知模式：{options.Mode}");
                }
            }

            var state = (ResourceState)session.GetState();
            var identity = new ProcessIdentity(
                sourceSha,
                await BuildSha256Async(),
                Sha256(SemanticContent),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                Environment.Version.ToString());
            var result = new ProcessResult(
                identity,
                samples.ToImmutable(),
                executions.ToImmutable(),
                Snapshot(state),
                session.GetCommandLog().PrefixSha256,
                session.GetCommandLog().Entries.Length,
                host.AdvanceCalls.ToImmutableArray(),
                host.Speeds.ToImmutableArray(),
                host.PauseFrames);
            await File.WriteAllTextAsync(Required(options.Output, "--output"),
                JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception exception)
        {
            System.Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunPrefix(
        SimulationSession session, SimulationStateCodec codec, ProcessHost host,
        ImmutableArray<ProcessCheckpoint>.Builder samples, ImmutableArray<ProcessExecution>.Builder executions)
    {
        Submit(session, codec, new NoChangeCommand(), samples, executions);
        Submit(session, codec, new CreateEntityCommand(), samples, executions);
        AdvanceTo(session, codec, host, 1, samples);
        Submit(session, codec, new DeleteEntityCommand(21), samples, executions);
        Submit(session, codec, new DeleteEntityCommand(21), samples, executions);
        Submit(session, codec, new CreateThenRejectCommand(), samples, executions);
    }

    private static void RunRemainder(
        SimulationSession session, SimulationStateCodec codec, ProcessHost host,
        ImmutableArray<ProcessCheckpoint>.Builder samples, ImmutableArray<ProcessExecution>.Builder executions)
    {
        AdvanceTo(session, codec, host, 2, samples);
        Submit(session, codec, new CreateEntityCommand(), samples, executions);
        AdvanceTo(session, codec, host, 4, samples);
    }

    private static void Submit(
        SimulationSession session, SimulationStateCodec codec, CoreCommand command,
        ImmutableArray<ProcessCheckpoint>.Builder samples, ImmutableArray<ProcessExecution>.Builder executions)
    {
        if (session.Submit(command, CommandSource.Automation) is not SubmissionResult.Completed completed)
            throw new InvalidOperationException("固定场景中的命令被入口拒收。");
        executions.Add(new ProcessExecution(
            completed.Execution.CommandId,
            completed.Execution.Position.CompletedSteps,
            completed.Execution.Position.Sequence,
            completed.Execution.TransactionId,
            completed.Execution.Outcome.ToString(),
            completed.Execution.Reason));
        AddSample(codec, completed.StateAfter, samples);
    }

    private static void AdvanceTo(
        SimulationSession session, SimulationStateCodec codec, ProcessHost host, ulong target,
        ImmutableArray<ProcessCheckpoint>.Builder samples)
    {
        while (session.GetState().CompletedSteps < target)
        {
            if (host.AvailableSteps == 0)
                host.CollectNextFrame(session.GetState());
            var remaining = target - session.GetState().CompletedSteps;
            var allocated = Math.Min(Math.Min(host.AvailableSteps, remaining), host.MaximumAdvanceBatch);
            if (allocated > 0)
            {
                var advanced = session.Advance(allocated);
                if (advanced.StepsAdvanced != allocated)
                    throw new InvalidOperationException($"只推进了 {advanced.StepsAdvanced}/{allocated} 步。");
                host.Consume(allocated);
            }
        }
        AddSample(codec, session.GetState(), samples);
    }

    private static void AddSample(
        SimulationStateCodec codec, SimulationState state, ImmutableArray<ProcessCheckpoint>.Builder samples)
    {
        var checkpoint = codec.CreateCheckpoint(state);
        var resources = (ResourceState)state;
        var random = resources.EntityIds.Select(entityId => new ProcessRandom(entityId,
            KeyedRandom.Sample(new RandomKey(resources.WorldSeed, "fixture.resource-choice",
                ImmutableArray.Create(resources.TransactionCount, entityId))))).ToImmutableArray();
        samples.Add(new ProcessCheckpoint(
            checkpoint.Position.CompletedSteps, checkpoint.Position.Sequence,
            checkpoint.CanonicalText, checkpoint.Sha256, random));
    }

    private static ResourceState CreateInitialState() => new()
    {
        NextEntityId = 21,
        EntityIds = ImmutableSortedSet.Create<ulong>(10, 20),
        Demands = ImmutableArray.Create(
            new ResourceDemand(10, 1, 0, 3, true),
            new ResourceDemand(20, 1, 0, 4, true))
    };

    private static ProcessState Snapshot(ResourceState state) => new(
        state.WorldSeed, state.GeneratorVersion, state.CompletedSteps, state.BoundarySequence,
        state.NextCommandId, state.NextEntityId, state.TransactionCount,
        state.Fault?.Code, state.Stock,
        state.Holdings.OrderBy(pair => pair.Key).Select(pair => new ProcessHolding(pair.Key, pair.Value)).ToImmutableArray(),
        state.EntityIds.ToImmutableArray(),
        state.Demands.OrderBy(item => item.EntityId).ThenBy(item => item.RequestId)
            .Select(item => new ProcessDemand(item.EntityId, item.RequestId, item.Priority, item.Amount, item.AllowPartial))
            .ToImmutableArray());

    private static string Describe<T>(DeterminismResult<T> result) where T : notnull =>
        result is DeterminismResult<T>.Failure failure
            ? $"{failure.Error.Kind}/{failure.Error.Code}{failure.Error.Path}"
            : "unexpected_success";

    private static DriverOptions Parse(string[] args)
    {
        string? mode = null, output = null, input = null, payload = null;
        var schedule = "steady";
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"参数缺少值：{args[i]}");
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--mode": mode = value; break;
                case "--output": output = value; break;
                case "--input": input = value; break;
                case "--payload": payload = value; break;
                case "--schedule": schedule = value; break;
                default: throw new ArgumentException($"未知参数：{args[i - 1]}");
            }
        }
        return new DriverOptions(mode ?? throw new ArgumentException("缺少 --mode。"), output, input, payload, schedule);
    }

    private static string Required(string? value, string name) =>
        value ?? throw new ArgumentException($"缺少 {name}。");

    internal static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static async Task<string> BuildSha256Async()
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var manifest = new StringBuilder();
        foreach (var fileName in new[] { "Train.Core.dll", "Train.Tests.dll" })
        {
            var path = Path.Combine(directory, fileName);
            manifest.Append(fileName).Append(':')
                .Append(Sha256(await File.ReadAllBytesAsync(path))).Append('\n');
        }
        return Sha256(manifest.ToString());
    }

    private sealed record DriverOptions(string Mode, string? Output, string? Input, string? Payload, string Schedule);
}

internal sealed class ProcessHost
{
    private static readonly (double Speed, bool Paused)[] SteadyFrames =
    [
        (1, true), (1, false), (2, false), (1, false), // step 1
        (4, false),                                    // step 2
        (8, false)                                     // steps 3-4 in one budget
    ];
    private static readonly (double Speed, bool Paused)[] BurstyFrames =
    [
        (8, true), (4, false),                         // step 1
        (2, false), (1, false), (1, false),           // step 2
        (1, true), (8, false)                         // steps 3-4 in one budget
    ];

    private readonly SimulationScheduler _scheduler = new();
    private readonly (double Speed, bool Paused)[] _frames;
    private int _nextFrame;

    internal ProcessHost(string schedule) => _frames = schedule switch
    {
        "steady" => SteadyFrames,
        "bursty" => BurstyFrames,
        _ => throw new ArgumentException($"未知调度：{schedule}")
    };

    internal ulong MaximumAdvanceBatch => ReferenceEquals(_frames, SteadyFrames) ? 1UL : 2UL;
    internal ulong AvailableSteps { get; private set; }
    internal List<ulong> AdvanceCalls { get; } = [];
    internal HashSet<double> Speeds { get; } = [];
    internal int PauseFrames { get; private set; }

    internal void CollectNextFrame(SimulationState state)
    {
        if (_nextFrame >= _frames.Length)
            throw new InvalidOperationException("固定调度未在帧预算内到达目标位置。");
        var (speed, paused) = _frames[_nextFrame++];
        var beforeRemainder = _scheduler.Remainder;
        var beforeBudget = AvailableSteps;
        var beforePosition = new SimulationPosition(state.CompletedSteps, state.BoundarySequence);
        var collected = _scheduler.CollectSteps(SimulationClock.StepDuration * 0.25 * speed, paused);
        AvailableSteps = checked(AvailableSteps + collected);
        if (paused)
        {
            PauseFrames++;
            if (collected != 0 || _scheduler.Remainder != beforeRemainder || AvailableSteps != beforeBudget ||
                beforePosition != new SimulationPosition(state.CompletedSteps, state.BoundarySequence))
                throw new InvalidOperationException("暂停帧修改了步预算、余量或模拟位置。");
        }
        else
        {
            Speeds.Add(speed);
        }
    }

    internal void Consume(ulong steps)
    {
        AvailableSteps -= steps;
        AdvanceCalls.Add(steps);
    }
}

internal sealed record ProcessResult(
    ProcessIdentity Identity,
    ImmutableArray<ProcessCheckpoint> Samples,
    ImmutableArray<ProcessExecution> Executions,
    ProcessState State,
    string LogPrefixSha256,
    int LogEntryCount,
    ImmutableArray<ulong> AdvanceCalls,
    ImmutableArray<double> Speeds,
    int PauseFrames);
internal sealed record ProcessIdentity(
    string SourceSha256, string BuildSha256, string SemanticContentSha256,
    string OperatingSystem, string Architecture, string DotNetRuntime);
internal sealed record ProcessCheckpoint(
    ulong CompletedSteps, ulong Sequence, string CanonicalText, string Sha256,
    ImmutableArray<ProcessRandom> Random);
internal sealed record ProcessRandom(ulong EntityId, ulong Value);
internal sealed record ProcessExecution(
    ulong CommandId, ulong CompletedSteps, ulong Sequence, ulong TransactionId, string Outcome, string? Reason);
internal sealed record ProcessState(
    ulong WorldSeed, string GeneratorVersion, ulong CompletedSteps, ulong BoundarySequence,
    ulong NextCommandId, ulong NextEntityId, ulong TransactionCount, string? Fault,
    ulong Stock, ImmutableArray<ProcessHolding> Holdings, ImmutableArray<ulong> EntityIds,
    ImmutableArray<ProcessDemand> Demands);
internal sealed record ProcessHolding(ulong EntityId, ulong Amount);
internal sealed record ProcessDemand(ulong EntityId, ulong RequestId, int Priority, ulong Amount, bool AllowPartial);

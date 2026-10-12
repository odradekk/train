
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

[TestClass]
[TestCategory("integration")]
public sealed class SimulationProcessTests
{
    private const string ZeroSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("steady", "bursty")]
    [DataRow("bursty", "steady")]
    public async Task Independent_processes_match_across_frame_pause_and_speed_schedules(
        string baselineSchedule, string repeatSchedule)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Assert.Inconclusive("确定性身份验收仅支持 Windows x64。");

        var directory = CreateEvidenceDirectory();
        var baseline = await RunProcess(directory, "baseline", "run", baselineSchedule);
        var repeat = await RunProcess(directory, "repeat", "run", repeatSchedule);

        AssertIdentity(baseline.Identity);
        Assert.AreEqual(baseline.Identity, repeat.Identity);
        AssertCompleteRun(baseline, baselineSchedule);
        AssertCompleteRun(repeat, repeatSchedule);
        AssertTraceComparison(baseline, repeat);
    }

    [TestMethod]
    [DataRow("steady", "bursty")]
    [DataRow("bursty", "steady")]
    public async Task Saved_boundary_restores_in_a_new_process_and_matches_continuous_run(
        string baselineSchedule, string resumedSchedule)
    {
        var directory = CreateEvidenceDirectory();
        var payloadPath = Path.Combine(directory, "split.core-state.json");
        var baseline = await RunProcess(directory, "baseline", "run", baselineSchedule);
        var prefix = await RunProcess(directory, "save", "save", baselineSchedule, payload: payloadPath);
        var resumed = await RunProcess(directory, "resume", "resume", resumedSchedule, input: payloadPath);

        AssertSplitBoundary(prefix);
        Assert.IsTrue(File.Exists(payloadPath), "保存进程必须留下真实恢复载荷。\n" + directory);
        Assert.AreEqual(baseline.Identity, resumed.Identity, "恢复链必须沿用同一原始输入、构建、内容和运行时身份。");
        AssertCompleteRun(baseline, baselineSchedule);
        AssertFinalState(resumed.State);
        Assert.AreEqual(1, resumed.Executions.Length, "恢复后的新进程只执行剩余的一条命令。");
        Assert.AreEqual(1, resumed.LogEntryCount, "恢复后的命令日志从新链开始。");
        Assert.AreEqual(ZeroSha256, resumed.LogPrefixSha256);
        Assert.AreEqual(4, resumed.Samples.Length);
        Assert.AreEqual(prefix.Samples[^1].CanonicalText, baseline.Samples[5].CanonicalText);
        Assert.AreEqual(prefix.Samples[^1].Sha256, baseline.Samples[5].Sha256);
        Assert.AreEqual(prefix.Samples[^1].CanonicalText, resumed.Samples[0].CanonicalText,
            "恢复成功后必须先采样恢复边界，不能等下一步覆盖错误状态。");
        Assert.AreEqual(prefix.Samples[^1].Sha256, resumed.Samples[0].Sha256);

        var resumedTail = resumed.Samples;
        var baselineTail = baseline.Samples
            .Where(point => Position(point).CompareTo(new SimulationPosition(1, 3)) >= 0)
            .ToImmutableArray();
        AssertTraceComparison(baseline with { Samples = baselineTail }, resumed with { Samples = resumedTail });
    }

    private static void AssertCompleteRun(ProcessResult result, string schedule)
    {
        AssertFinalState(result.State);
        Assert.AreEqual(6, result.Executions.Length);
        Assert.AreEqual(6, result.LogEntryCount);
        Assert.AreEqual(ZeroSha256, result.LogPrefixSha256);
        var expectedCalls = schedule == "steady"
            ? new ulong[] { 1, 1, 1, 1 }
            : new ulong[] { 1, 1, 2 };
        CollectionAssert.AreEqual(expectedCalls, result.AdvanceCalls.ToArray());
        CollectionAssert.AreEquivalent(new[] { 1.0, 2.0, 4.0, 8.0 }, result.Speeds.ToArray());
        Assert.IsTrue(result.PauseFrames > 0);

        var expectedPositions = new[]
        {
            new SimulationPosition(0, 1), new SimulationPosition(0, 2), new SimulationPosition(1, 0),
            new SimulationPosition(1, 1), new SimulationPosition(1, 2), new SimulationPosition(1, 3),
            new SimulationPosition(2, 0), new SimulationPosition(2, 1), new SimulationPosition(4, 0)
        };
        CollectionAssert.AreEqual(expectedPositions, result.Samples.Select(Position).ToArray(),
            "采样必须恰好落在命令边界、step 1、step 2 和 step 4；调用分批方式不改变采样位置。");

        var expectedExecutions = new[]
        {
            new ProcessExecution(1, 0, 1, 1, "Executed", null),
            new ProcessExecution(2, 0, 2, 2, "Executed", null),
            new ProcessExecution(3, 1, 1, 4, "Executed", null),
            new ProcessExecution(4, 1, 2, 5, "Rejected", "target_missing"),
            new ProcessExecution(5, 1, 3, 6, "Rejected", "requirements_not_met"),
            new ProcessExecution(6, 2, 1, 8, "Executed", null)
        };
        CollectionAssert.AreEqual(expectedExecutions, result.Executions.ToArray());

        var final = result.Samples[^1];
        Assert.AreEqual(SimulationProcessDriver.Sha256(final.CanonicalText), final.Sha256,
            "期末检查点摘要必须由实际规范 UTF-8 文档独立计算得到。");
        Assert.AreEqual(FinalCheckpointJson, final.CanonicalText, "期末检查点必须匹配手工固定的 golden 文档。");
        Assert.AreEqual(SimulationProcessDriver.Sha256(FinalCheckpointJson), final.Sha256);
        CollectionAssert.AreEqual(new[]
        {
            new ProcessRandom(10, 11307587350992695523UL),
            new ProcessRandom(20, 13514275882266078810UL),
            new ProcessRandom(22, 11129591333154154132UL)
        }, final.Random.ToArray(), "期末随机键结果必须匹配独立 Python 参考向量。");
    }

    private static void AssertFinalState(ProcessState state)
    {
        Assert.AreEqual(42UL, state.WorldSeed);
        Assert.AreEqual("test-generator-v1", state.GeneratorVersion);
        Assert.AreEqual(4UL, state.CompletedSteps);
        Assert.AreEqual(0UL, state.BoundarySequence);
        Assert.AreEqual(7UL, state.NextCommandId);
        Assert.AreEqual(23UL, state.NextEntityId);
        Assert.AreEqual(10UL, state.TransactionCount);
        Assert.IsNull(state.Fault);
        Assert.AreEqual(0UL, state.Stock);
        CollectionAssert.AreEqual(
            new[] { new ProcessHolding(10, 3), new ProcessHolding(20, 2) }, state.Holdings.ToArray());
        CollectionAssert.AreEqual(new ulong[] { 10, 20, 22 }, state.EntityIds.ToArray());
        CollectionAssert.AreEqual(new[]
        {
            new ProcessDemand(10, 1, 0, 3, true),
            new ProcessDemand(20, 1, 0, 4, true)
        }, state.Demands.ToArray());
    }

    private static void AssertSplitBoundary(ProcessResult prefix)
    {
        var state = prefix.State;
        Assert.AreEqual(1UL, state.CompletedSteps);
        Assert.AreEqual(3UL, state.BoundarySequence);
        Assert.AreEqual(6UL, state.NextCommandId);
        Assert.AreEqual(22UL, state.NextEntityId);
        Assert.AreEqual(6UL, state.TransactionCount);
        Assert.AreEqual(0UL, state.Stock);
        CollectionAssert.AreEqual(new ulong[] { 10, 20 }, state.EntityIds.ToArray());
        Assert.AreEqual(5, prefix.Executions.Length);
        Assert.AreEqual(5, prefix.LogEntryCount);
    }

    private static void AssertIdentity(ProcessIdentity identity)
    {
        AssertSha256(identity.SourceSha256, "原始起点");
        AssertSha256(identity.BuildSha256, "实际测试构建");
        AssertSha256(identity.SemanticContentSha256, "语义内容");
        Assert.AreEqual(SimulationProcessDriver.Sha256(InitialStateJson), identity.SourceSha256);
        Assert.AreEqual(ExpectedBuildSha256(), identity.BuildSha256);
        Assert.AreEqual(
            SimulationProcessDriver.Sha256("fixture.resource/v1;stock,holdings,entity_ids,demands;test-generator-v1"),
            identity.SemanticContentSha256);
        Assert.AreEqual("windows", identity.OperatingSystem);
        Assert.AreEqual("x64", identity.Architecture);
        Assert.IsFalse(string.IsNullOrWhiteSpace(identity.DotNetRuntime));
    }

    private static void AssertTraceComparison(ProcessResult left, ProcessResult right)
    {
        var codecResultLeft = DecodeTrace(left);
        var codecResultRight = DecodeTrace(right);
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            var comparison = SimulationCheckpoints.Compare(codecResultLeft, codecResultRight);
            Assert.IsInstanceOfType<CheckpointComparison.Equal>(comparison);
        }

        Assert.AreEqual(left.Samples.Length, right.Samples.Length,
            "不能只相信 Compare 的 Equal 分支；独立核对每个实际采样。");
        for (var index = 0; index < left.Samples.Length; index++)
        {
            Assert.AreEqual(Position(left.Samples[index]), Position(right.Samples[index]));
            Assert.AreEqual(left.Samples[index].CanonicalText, right.Samples[index].CanonicalText);
            Assert.AreEqual(left.Samples[index].Sha256, right.Samples[index].Sha256);
            Assert.AreEqual(SimulationProcessDriver.Sha256(left.Samples[index].CanonicalText), left.Samples[index].Sha256);
            CollectionAssert.AreEqual(left.Samples[index].Random.ToArray(), right.Samples[index].Random.ToArray(),
                $"采样 {Position(left.Samples[index])} 的随机键证据不同。");
        }
    }

    private static CheckpointTrace DecodeTrace(ProcessResult result)
    {
        var points = ImmutableArray.CreateBuilder<AuthoritativeCheckpoint>();
        foreach (var sample in result.Samples)
        {
            var decoded = SimulationCheckpoints.Decode(sample.CanonicalText);
            Assert.IsInstanceOfType<DeterminismResult<AuthoritativeCheckpoint>.Success>(decoded);
            var point = ((DeterminismResult<AuthoritativeCheckpoint>.Success)decoded).Value;
            Assert.AreEqual(Position(sample), point.Position);
            Assert.AreEqual(sample.Sha256, point.Sha256);
            points.Add(point);
        }
        var identity = result.Identity;
        return new CheckpointTrace(
            new DeterminismIdentity(identity.SourceSha256, identity.BuildSha256, identity.SemanticContentSha256,
                new DeterminismEnvironment(identity.OperatingSystem, identity.Architecture, identity.DotNetRuntime)),
            points.ToImmutable());
    }

    private async Task<ProcessResult> RunProcess(
        string directory, string name, string mode, string schedule, string? input = null, string? payload = null)
    {
        var output = Path.Combine(directory, $"{name}-{schedule}.json");
        var stdoutPath = Path.Combine(directory, $"{name}-{schedule}.stdout.log");
        var stderrPath = Path.Combine(directory, $"{name}-{schedule}.stderr.log");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(typeof(SimulationProcessTests).Assembly.Location);
        start.ArgumentList.Add("--simulation-process");
        Add(start, "--mode", mode);
        Add(start, "--schedule", schedule);
        Add(start, "--output", output);
        if (input is not null) Add(start, "--input", input);
        if (payload is not null) Add(start, "--payload", payload);

        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动模拟测试子进程。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await File.WriteAllTextAsync(stdoutPath, await stdoutTask, new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderrPath, await stderrTask, new UTF8Encoding(false));
            throw new AssertFailedException($"模拟子进程超过 30 秒；证据目录：{directory}");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await File.WriteAllTextAsync(stdoutPath, stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(stderrPath, stderr, new UTF8Encoding(false));
        Assert.AreEqual(0, process.ExitCode,
            $"模拟子进程失败（exit {process.ExitCode}）。\nstdout: {stdoutPath}\nstderr: {stderrPath}\n{stderr}");
        Assert.IsTrue(File.Exists(output), $"子进程未生成结果：{output}");
        return JsonSerializer.Deserialize<ProcessResult>(await File.ReadAllTextAsync(output), JsonOptions)
               ?? throw new AssertFailedException($"无法读取子进程结果：{output}");
    }

    private string CreateEvidenceDirectory()
    {
        var resultsDirectory = TestContext.ResultsDirectory
            ?? throw new InvalidOperationException("测试宿主未提供结果目录。");
        var path = Path.Combine(resultsDirectory, "simulation-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Add(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static SimulationPosition Position(ProcessCheckpoint point) => new(point.CompletedSteps, point.Sequence);

    private static void AssertSha256(string value, string label) =>
        Assert.IsTrue(value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'),
            $"{label}摘要不是大写 SHA-256：{value}");

    private static string ExpectedBuildSha256()
    {
        var directory = Path.GetDirectoryName(typeof(SimulationProcessTests).Assembly.Location)!;
        var manifest = new StringBuilder();
        foreach (var fileName in new[] { "Train.Core.dll", "Train.Core.Tests.dll" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, fileName));
            manifest.Append(fileName).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(bytes))).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    // 与 codec 独立维护，避免同一实现同时生成实际值和预期值。
    private const string InitialStateJson =
        "{\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{\"world_seed\":42,\"generator_version\":\"test-generator-v1\",\"completed_steps\":0,\"boundary_sequence\":0,\"next_command_id\":1,\"next_entity_id\":21,\"transaction_count\":0,\"fault\":null},\"domain\":{\"stock\":5,\"holdings\":[],\"entity_ids\":[10,20],\"demands\":[{\"entity_id\":10,\"request_id\":1,\"priority\":0,\"amount\":3,\"allow_partial\":true},{\"entity_id\":20,\"request_id\":1,\"priority\":0,\"amount\":4,\"allow_partial\":true}]}}";

    private const string FinalCheckpointJson =
        "{\"checkpoint_format_version\":1,\"objects\":[{\"kind\":\"fixture.entity\",\"id\":10,\"fields\":{\"holding\":3}},{\"kind\":\"fixture.entity\",\"id\":20,\"fields\":{\"holding\":2}},{\"kind\":\"fixture.entity\",\"id\":22,\"fields\":{\"holding\":null}},{\"kind\":\"fixture.resources\",\"id\":0,\"fields\":{\"demands\":[{\"allow_partial\":true,\"amount\":3,\"entity_id\":10,\"priority\":0,\"request_id\":1},{\"allow_partial\":true,\"amount\":4,\"entity_id\":20,\"priority\":0,\"request_id\":1}],\"stock\":0}},{\"kind\":\"simulation\",\"id\":0,\"fields\":{\"boundary_sequence\":0,\"completed_steps\":4,\"fault\":null,\"generator_version\":\"test-generator-v1\",\"next_command_id\":7,\"next_entity_id\":23,\"transaction_count\":10,\"world_seed\":42}}]}";
}

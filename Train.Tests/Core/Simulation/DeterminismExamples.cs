// Train.Tests.Core.Simulation 保存确定性契约的数据定义例子。
// 当前没有测试方法；函数例子与独立向量将在设计确认后加入。

using System.Collections.Immutable;
using System.Text.Json;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

/// <summary>覆盖结果分支、可选字段及结构的数据例子，不调用待实现的编码器生成预期值。</summary>
internal static class DeterminismExamples
{
    internal static readonly SimulationState Boundary = new(42, "test-generator-v1");
    internal static readonly ResourceState Resources = new();
    internal static readonly ResourceState FrozenResources = Resources with
    {
        Fault = new SimulationFault("fixture_failure", SimulationPhase.Command,
            new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1), 7)
    };
    internal static readonly CoreStateSnapshot BoundarySnapshot = new("fixture.boundary", 1, Boundary);
    internal static readonly CoreStateSnapshot ResourceSnapshot = new("fixture.resources", 1, Resources);
    internal static readonly CoreStateSnapshot FrozenSnapshot = new("fixture.resources", 1, FrozenResources);

    internal const string NoChangeText = "{\"input_format_version\":2,\"kind\":\"fixture.no_change\"}";
    // 独立使用标准 SHA-256 对上述字面 UTF-8 文本计算，未调用设计桩。
    internal const string NoChangeSha256 = "360A66E43D7828BE95C4EEF9D923A04986D8D37B0ECD077AA9023276EB9BFEA3";
    internal static readonly EncodedCommand NoChangePayload = new(NoChangeText, NoChangeSha256);
    internal static readonly CommandExecution Executed = new(1, new SimulationPosition(0, 1), 1,
        CommandSource.Ui, CommandOutcome.Executed);
    internal static readonly CommandExecution Rejected = Executed with
    {
        Source = CommandSource.Console, Outcome = CommandOutcome.Rejected, Reason = "target_missing"
    };
    internal static readonly CommandExecution Faulted = Executed with
    {
        Source = CommandSource.Automation, Outcome = CommandOutcome.Faulted, Reason = "fixture_failure"
    };
    internal static readonly CommandLogSnapshot EmptyLog = CommandLogSnapshot.Empty;
    internal const string ExecutedRecordText =
        "{\"record_format_version\":1,\"source\":\"ui\",\"command_id\":1,\"position\":{\"completed_steps\":0,\"sequence\":1},\"transaction_id\":1,\"payload\":{\"input_format_version\":2,\"kind\":\"fixture.no_change\"},\"outcome\":\"executed\",\"reason\":null}";
    internal const string FirstChainSha256 = "A3854AB693362167DFD16F2BFDF369B3F1560696276E470E746B5AFD878515EE";
    internal static readonly CommandLogEntry ExecutedEntry = new(
        Executed, NoChangePayload, ExecutedRecordText, FirstChainSha256);
    internal static readonly CommandLogSnapshot OneEntryLog = new(
        SimulationCommandLog.InitialSha256, ImmutableArray.Create(ExecutedEntry));

    internal static readonly DeterminismError UnknownFormat = new(
        DeterminismErrorKind.UnknownFormat, "unsupported_input_format", "/input_format_version");
    internal static readonly DeterminismError InvalidData = new(
        DeterminismErrorKind.InvalidData, "invalid_entity_id", "/simulation/next_entity_id");
    internal static readonly DeterminismError IncompatibleContext = new(
        DeterminismErrorKind.IncompatibleContext, "build_mismatch", "/identity/build_sha256");
    internal static readonly DeterminismResult<CoreCommand> DecodedCommand =
        new DeterminismResult<CoreCommand>.Success(new NoChangeCommand());
    internal static readonly DeterminismResult<CoreCommand> FailedCommand =
        new DeterminismResult<CoreCommand>.Failure(UnknownFormat);
    internal static readonly DeterminismResult<SimulationState> ReadDomain =
        new DeterminismResult<SimulationState>.Success(Resources);
    internal static readonly DeterminismResult<CoreStateSnapshot> DecodedState =
        new DeterminismResult<CoreStateSnapshot>.Success(ResourceSnapshot);
    internal static readonly DeterminismResult<CoreStateSnapshot> FailedState =
        new DeterminismResult<CoreStateSnapshot>.Failure(InvalidData);
    internal static readonly DeterminismResult<SimulationSession> RestoredSession =
        new DeterminismResult<SimulationSession>.Success(new SimulationSession(Resources, new ResourceCalculations(), new ResourceCommandCodec()));
    internal static readonly DeterminismResult<SimulationSession> FailedRestore =
        new DeterminismResult<SimulationSession>.Failure(InvalidData);

    internal static readonly RandomKey EmptyComponents = new(0, "fixture.empty", ImmutableArray<ulong>.Empty);
    internal static readonly RandomKey ResourceChoice = new(42, "fixture.resource-choice", ImmutableArray.Create(6UL, 7UL));
    internal static readonly RandomKey MaxComponents = new(ulong.MaxValue, "fixture.max", ImmutableArray.Create(0UL, ulong.MaxValue));
    internal const ulong ZeroRandomValue = 0;
    // TRNG、版本 1、种子 42、23 字节用途名、两个分量 6/7；只按布局给出字节。
    internal static readonly ImmutableArray<byte> ResourceKeyBytes = ImmutableArray.CreateRange(Convert.FromHexString(
        "54524E47010000002A0000000000000017000000666978747572652E7265736F757263652D63686F6963650200000006000000000000000700000000000000"));

    internal const string BoundaryCheckpointText =
        "{\"checkpoint_format_version\":1,\"objects\":[{\"kind\":\"simulation\",\"id\":0,\"fields\":{\"boundary_sequence\":0,\"completed_steps\":0,\"fault\":null,\"generator_version\":\"test-generator-v1\",\"next_command_id\":1,\"next_entity_id\":1,\"transaction_count\":0,\"world_seed\":42}}]}";
    internal const string BoundaryCheckpointSha256 = "0695BD795403B63AD2824158AA2A345D640D614DF5174688C55A0F931579F86D";
    internal static readonly AuthoritativeCheckpoint BoundaryCheckpoint =
        new(new SimulationPosition(0, 0), BoundaryCheckpointText, BoundaryCheckpointSha256);
    internal static readonly CheckpointObject ResourceObject = new(new CheckpointObjectId("fixture.resources", 0),
        JsonSerializer.Deserialize<JsonElement>("{\"stock\":5,\"entity_ids\":[],\"holdings\":[],\"demands\":[]}"));
    internal static readonly CheckpointObject EntityObject = new(new CheckpointObjectId("fixture.entity", 7),
        JsonSerializer.Deserialize<JsonElement>("{\"holding\":3,\"history\":[{\"step\":1,\"amount\":3}]}"));
    internal static readonly JsonElement FieldTree = JsonSerializer.Deserialize<JsonElement>(
        "{\"null\":null,\"false\":false,\"true\":true,\"integer\":-9223372036854775808,\"unsigned\":18446744073709551615,\"double\":1.5,\"text\":\"例子\",\"empty\":[],\"nested\":[{\"values\":[0,1]}]}");
    internal static readonly DeterminismEnvironment WindowsX64 = new("windows", "x64", "10.0.0");
    // 此处只是身份格式的数据例子，不是已执行跨进程对照的身份或证据。
    internal static readonly DeterminismIdentity Identity = new(
        "63EE2F12989CBF1CF86EAE2DDAD318FA7A5F0F2C54BA0E93E7AA7204042C331D", NoChangeSha256, NoChangeSha256, WindowsX64);
    internal static readonly CheckpointTrace EmptyTrace = new(Identity, ImmutableArray<AuthoritativeCheckpoint>.Empty);
    internal static readonly CheckpointTrace OnePointTrace = new(Identity, ImmutableArray.Create(BoundaryCheckpoint));
    internal static readonly CheckpointDifference ChangedHolding = new(
        new SimulationPosition(0, 1), new CheckpointObjectId("fixture.entity", 7), "/holding", "3", "4");
    internal static readonly CheckpointDifference MissingObject = ChangedHolding with
    {
        FieldPath = "", LeftValue = "{\"holding\":3}", RightValue = null
    };
    internal static readonly CheckpointDifference MissingPoint = new(
        new SimulationPosition(1, 0), null, "", BoundaryCheckpointText, null);
    internal static readonly CheckpointDifference NullFieldAndMissingField = ChangedHolding with
    {
        FieldPath = "/target", LeftValue = "null", RightValue = null
    };
    internal static readonly CheckpointComparison Equal = new CheckpointComparison.Equal();
    internal static readonly CheckpointComparison Different = new CheckpointComparison.Different(ChangedHolding);
    internal static readonly CheckpointComparison NotComparable = new CheckpointComparison.NotComparable(IncompatibleContext);
}

// Train.Core.Simulation 定义完整核心状态的捕获、文档读取和恢复。
// 领域格式提供业务字段与检查点投影；玩家存档容器和文件操作不属于本文件。

using System.Collections.Immutable;
using System.Text.Json;

namespace Train.Core.Simulation;

/// <summary>表示已捕获的完整不可变边界与解释它的领域格式。</summary>
/// <param name="StateKind">非空领域格式名，不是 CLR 类型名或注册序号。</param>
/// <param name="StateVersion">领域格式版本，从 1 开始；与核心包络版本和玩家存档版本分别解释。</param>
/// <param name="State">所有影响后续模拟的权威数据，包括故障与派生领域状态。</param>
/// <remarks>不保存日志、暂停、倍速、宿主余量、UI 或玩家存档身份。</remarks>
/// <example>资源夹具捕获库存、持有量、实体和需求，同时保留全部基础边界字段。</example>
public sealed record CoreStateSnapshot(string StateKind, uint StateVersion, SimulationState State);

/// <summary>计算一种领域状态的核心载荷、恢复候选和检查点投影。</summary>
/// <example>恢复目标消失拒绝后的资源夹具时，下一个命令号和步内次序不能重置。</example>
public abstract class SimulationStateCodec
{
    /// <summary>核心状态包络的第一版；不是完整玩家存档格式版本。</summary>
    public const uint CoreStateFormatVersion = 1;

    /// <summary>此格式支持的稳定领域名。</summary>
    public abstract string StateKind { get; }

    /// <summary>此格式支持的正领域版本。</summary>
    public abstract uint StateVersion { get; }

    /// <summary>把已有完整边界连同领域格式标识捕获为不可变值。</summary>
    /// <param name="state">由会话 GetState 取得的最后完整状态，或已经验证的创建起点。</param>
    /// <returns>包含同一完整边界全部权威数据的捕获值。</returns>
    /// <remarks>不推进模拟、改变计数或解除故障；宿主在完整边界处理捕获请求。</remarks>
    public CoreStateSnapshot Capture(SimulationState state)
        => new(StateKind, StateVersion, state);

    /// <summary>计算完整核心状态的带版本载荷文本。</summary>
    /// <param name="snapshot">此领域格式的完整捕获值，包括基础边界和全部业务字段。</param>
    /// <returns>可跨进程读取的紧凑 UTF-8 JSON 文本，不含玩家存档容器。</returns>
    /// <remarks>不写文件或修改捕获值；基础边界与领域字段分别编码，再组成载荷。</remarks>
    /// <exception cref="ArgumentNullException">捕获值为空。</exception>
    /// <exception cref="ArgumentException">捕获值的领域名或版本不属于此格式。</exception>
    public string Encode(CoreStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var error = CheckSnapshotFormat(snapshot);
        if (error is not null)
            throw new ArgumentException($"捕获值不属于此状态格式：{error.Code} {error.Path}", nameof(snapshot));
        return SimulationJson.Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("core_state_format_version", CoreStateFormatVersion);
            writer.WriteString("state_kind", snapshot.StateKind);
            writer.WriteNumber("state_version", snapshot.StateVersion);
            writer.WritePropertyName("simulation");
            SimulationStateDocument.Write(writer, snapshot.State);
            writer.WritePropertyName("domain");
            writer.WriteStartObject();
            WriteDomain(writer, snapshot.State);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>读取核心包络与领域数据，得到尚未发布的完整状态候选。</summary>
    /// <param name="json">包含核心包络和领域数据的载荷文本。</param>
    /// <returns>完整候选，或带稳定路径的格式、类型及范围错误。</returns>
    /// <remarks>不创建或修改运行会话；包络读取成功后，领域读取才能得到完整派生状态。</remarks>
    public DeterminismResult<CoreStateSnapshot> Decode(string json)
    {
        var envelope = ReadEnvelope(json);
        if (envelope is DeterminismResult<(SimulationState Boundary, JsonElement Domain)>.Failure failure)
            return new DeterminismResult<CoreStateSnapshot>.Failure(failure.Error);

        var value = ((DeterminismResult<(SimulationState Boundary, JsonElement Domain)>.Success)envelope).Value;
        var result = ReadDomain(value.Boundary, value.Domain);
        return result switch
        {
            DeterminismResult<SimulationState>.Success domain when SameBoundary(value.Boundary, domain.Value) =>
                new DeterminismResult<CoreStateSnapshot>.Success(new(StateKind, StateVersion, domain.Value)),
            DeterminismResult<SimulationState>.Success =>
                new DeterminismResult<CoreStateSnapshot>.Failure(new(
                    DeterminismErrorKind.InvalidData, "domain_changed_boundary", "/domain")),
            DeterminismResult<SimulationState>.Failure domain =>
                new DeterminismResult<CoreStateSnapshot>.Failure(domain.Error with
                { Path = "/domain" + domain.Error.Path }),
            _ => throw new InvalidOperationException("未知的领域读取结果。")
        };
    }

    /// <summary>校验完整恢复候选，并为有效候选创建一个新会话。</summary>
    /// <param name="snapshot">已读取或已捕获的完整候选，不是要逐字段覆盖当前会话的补丁。</param>
    /// <param name="calculations">负责起点不变量与后续模拟的领域计算。</param>
    /// <param name="commands">此领域的命令 v2 格式，用于新会话的执行日志。</param>
    /// <returns>校验通过的新会话，或首个恢复错误；失败没有可发布会话。</returns>
    /// <remarks>不修改已有会话，调用方只在成功后整体替换。保留 Fault 的候选恢复后仍冻结；新会话日志从空链开始。</remarks>
    public DeterminismResult<SimulationSession> Restore(
        CoreStateSnapshot snapshot, SimulationCalculations calculations, SimulationCommandCodec commands)
    {
        var formatError = CheckSnapshotFormat(snapshot);
        if (formatError is not null)
            return new DeterminismResult<SimulationSession>.Failure(formatError);
        var violation = calculations.CheckInitialState(snapshot.State);
        return violation is null
            ? new DeterminismResult<SimulationSession>.Success(new(snapshot.State, calculations, commands))
            : new DeterminismResult<SimulationSession>.Failure(LocateInitialViolation(violation));
    }

    /// <summary>计算完整边界的权威对象、规范检查点文档及其摘要。</summary>
    /// <param name="state">包含全部业务字段和基础边界的不可变状态。</param>
    /// <returns>以该边界位置定位的规范文档；不包含来源、构建及语义内容身份。</returns>
    /// <remarks>不读取日志或宿主状态，不改变会话；公共编码器处理基础对象和领域对象的相同字段树。</remarks>
    public AuthoritativeCheckpoint CreateCheckpoint(SimulationState state) =>
        SimulationCheckpoints.Encode(ProjectDomain(state).Add(ProjectBoundary(state)));

    /// <summary>校验包络版本与字段，读取完整基础边界和独立存活的领域字段。</summary>
    private DeterminismResult<(SimulationState Boundary, JsonElement Domain)> ReadEnvelope(string json)
    {
        try
        {
            var root = SimulationJson.ParseObject(json);
            CheckEnvelopeFields(root);

            var coreVersion = SimulationJson.UInt32(EnvelopeProperty(root, "core_state_format_version"),
                "/core_state_format_version");
            if (coreVersion != CoreStateFormatVersion)
                return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.UnknownFormat,
                    "unsupported_core_state_format", "/core_state_format_version");

            var kind = SimulationJson.String(EnvelopeProperty(root, "state_kind"), "/state_kind", true);
            if (!string.Equals(kind, StateKind, StringComparison.Ordinal))
                return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.UnknownFormat,
                    "unsupported_state_kind", "/state_kind");

            var version = SimulationJson.UInt32(EnvelopeProperty(root, "state_version"), "/state_version");
            if (version != StateVersion)
                return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.UnknownFormat,
                    "unsupported_state_version", "/state_version");

            var boundary = SimulationStateDocument.Read(EnvelopeProperty(root, "simulation"), "/simulation");
            var domain = EnvelopeProperty(root, "domain");
            SimulationJson.RequireObject(domain, "/domain");
            return new DeterminismResult<(SimulationState, JsonElement)>.Success((boundary, domain.Clone()));
        }
        catch (JsonException)
        {
            return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.InvalidData, "invalid_json", "");
        }
        catch (SimulationJsonException exception)
        {
            return new DeterminismResult<(SimulationState, JsonElement)>.Failure(exception.Error);
        }
    }

    /// <summary>拒绝包络中的未知和重复字段，缺失字段按协议读取顺序定位。</summary>
    private static void CheckEnvelopeFields(JsonElement root)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "core_state_format_version", "state_kind", "state_version", "simulation", "domain"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var path = "/" + SimulationJson.Escape(property.Name);
            if (!seen.Add(property.Name)) throw SimulationJson.Invalid("duplicate_field", path);
            if (!allowed.Contains(property.Name)) throw SimulationJson.Invalid("unknown_field", path);
        }
    }

    /// <summary>按固定读取顺序取得包络字段并定位缺失值。</summary>
    private static JsonElement EnvelopeProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property)
            ? property
            : throw SimulationJson.Invalid("missing_field", "/" + SimulationJson.Escape(name));

    /// <summary>检查候选捕获值是否属于此领域格式。</summary>
    private DeterminismError? CheckSnapshotFormat(CoreStateSnapshot snapshot)
    {
        if (!string.Equals(snapshot.StateKind, StateKind, StringComparison.Ordinal))
            return new(DeterminismErrorKind.UnknownFormat, "unsupported_state_kind", "/state_kind");
        return snapshot.StateVersion != StateVersion
            ? new(DeterminismErrorKind.UnknownFormat, "unsupported_state_version", "/state_version")
            : null;
    }

    /// <summary>把起点不变量错误定位到核心或领域载荷中的字段。</summary>
    private static DeterminismError LocateInitialViolation(InvariantViolation violation)
    {
        var commonField = violation.Field switch
        {
            nameof(SimulationState.WorldSeed) => "world_seed",
            nameof(SimulationState.GeneratorVersion) => "generator_version",
            nameof(SimulationState.CompletedSteps) => "completed_steps",
            nameof(SimulationState.BoundarySequence) => "boundary_sequence",
            nameof(SimulationState.NextCommandId) => "next_command_id",
            nameof(SimulationState.NextEntityId) => "next_entity_id",
            nameof(SimulationState.TransactionCount) => "transaction_count",
            nameof(SimulationState.Fault) => "fault",
            nameof(SimulationState) => "",
            _ => null
        };
        var path = commonField is null
            ? "/domain/" + SnakeCase(violation.Field)
            : "/simulation" + (commonField.Length == 0 ? "" : "/" + commonField);
        return new(DeterminismErrorKind.InvalidData, violation.Code, path);
    }

    /// <summary>把全部基础权威字段和可选故障投影为 simulation/0 对象。</summary>
    private static CheckpointObject ProjectBoundary(SimulationState state)
    {
        var fields = SimulationJson.ParseObject(SimulationJson.Format(writer =>
            SimulationStateDocument.Write(writer, state)));
        return new(new CheckpointObjectId("simulation", 0), fields);
    }

    /// <summary>检查领域读取没有更改包络提供的任何基础权威字段。</summary>
    private static bool SameBoundary(SimulationState expected, SimulationState actual) =>
        expected.WorldSeed == actual.WorldSeed &&
        string.Equals(expected.GeneratorVersion, actual.GeneratorVersion, StringComparison.Ordinal) &&
        expected.CompletedSteps == actual.CompletedSteps &&
        expected.BoundarySequence == actual.BoundarySequence &&
        expected.NextCommandId == actual.NextCommandId &&
        expected.NextEntityId == actual.NextEntityId &&
        expected.TransactionCount == actual.TransactionCount &&
        expected.Fault == actual.Fault;

    /// <summary>把领域属性名转换为载荷字段名。</summary>
    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) =>
        char.IsUpper(character) && index > 0
            ? "_" + char.ToLowerInvariant(character)
            : char.ToLowerInvariant(character).ToString()));

    /// <summary>构造没有部分读取结果的失败。</summary>
    private static DeterminismResult<T>.Failure Failure<T>(
        DeterminismErrorKind kind, string code, string path) where T : notnull =>
        new(new DeterminismError(kind, code, path));

    /// <summary>在已打开的 domain 对象中写入全部需要恢复的业务字段。</summary>
    /// <param name="writer">已经写完基础边界、位于 domain 对象中的 JSON 写入器。</param>
    /// <param name="state">为业务字段提供数据的完整状态。</param>
    /// <remarks>只写字段，不开关 domain 对象。字段和集合顺序由此领域格式固定，有限 double 使用 R 文本。</remarks>
    protected abstract void WriteDomain(Utf8JsonWriter writer, SimulationState state);

    /// <summary>根据已读取的基础边界与业务字段构造完整领域状态。</summary>
    /// <param name="boundary">包络中读取的基础权威字段，不得重置计数或故障。</param>
    /// <param name="fields">domain 对象；读取结果不得依赖此元素的文档存活期。</param>
    /// <returns>完整领域状态，或未知字段、缺失字段、类型与范围错误；错误路径相对于 domain，公共读取入口添加 /domain 前缀。</returns>
    protected abstract DeterminismResult<SimulationState> ReadDomain(SimulationState boundary, JsonElement fields);

    /// <summary>把所有影响后续模拟的业务数据投影为有稳定身份的检查点对象。</summary>
    /// <param name="state">完整领域状态；基础 simulation 对象由公共层另行加入。</param>
    /// <returns>身份不重复的业务对象；无序集合使用稳定身份，序列保留业务顺序。</returns>
    protected abstract ImmutableArray<CheckpointObject> ProjectDomain(SimulationState state);
}

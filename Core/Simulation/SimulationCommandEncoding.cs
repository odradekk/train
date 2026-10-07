// Train.Core.Simulation 定义命令 v2 的编码边界。
// 公共包络由本模块负责，具体种类、字段与集合含义由领域格式负责。

using System.Text.Json;

namespace Train.Core.Simulation;

/// <summary>表示已经按命令种类规范编码的完整负载与其摘要。</summary>
/// <param name="CanonicalText">无 BOM、无尾换行的紧凑 JSON；顶层先写版本、种类，再写该种类的固定字段。</param>
/// <param name="Sha256">CanonicalText 的 UTF-8 字节对应的 64 个字符的大写十六进制 SHA-256 文本。</param>
/// <remarks>此值由编码器产生；摘要不包含来源、位置或执行结果。</remarks>
/// <example>测试命令 fixture.delete_entity 的 entity_id 为 7，负载只有版本、种类和目标 ID。</example>
public sealed record EncodedCommand(string CanonicalText, string Sha256);

/// <summary>计算命令 v2 的规范负载，或将外部文本读为已知领域命令。</summary>
/// <remarks>领域格式定义具体命令；公共层负责版本、重复字段、UTF-8 和摘要。</remarks>
/// <example>字段顺序不同的 fixture.delete_entity 文本应读为相同目标，并重新编码为相同规范文本。</example>
public abstract class SimulationCommandCodec
{
    /// <summary>当前命令输入格式版本；旧版本 1 已退役。</summary>
    /// <seealso href="../../docs/design/04-deterministic-simulation.md#规范编码"/>
    public const uint InputFormatVersion = 2;

    /// <summary>计算一个已知命令的规范 UTF-8 JSON 文本及负载摘要。</summary>
    /// <param name="command">不带模拟位置的已解析领域命令。</param>
    /// <returns>由命令数据决定的规范负载；来源和执行身份在此值外记录。</returns>
    /// <remarks>不修改命令、会话或日志；规范文本与摘要是两个独立计算。</remarks>
    public EncodedCommand Encode(CoreCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = EncodePayload(command);
        return new EncodedCommand(text, ComputePayloadSha256(text));
    }

    /// <summary>校验公共包络和领域字段，将文本读为一个已知命令。</summary>
    /// <param name="json">外部命令文本，字段顺序不影响读取；不是待执行队列。</param>
    /// <returns>完整命令，或未知格式、非法数据错误；失败不分配命令身份。</returns>
    /// <remarks>不修改会话或日志；先读取公共包络，再交给领域读取种类字段。</remarks>
    public DeterminismResult<CoreCommand> Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var envelope = ReadEnvelope(json);
            return envelope switch
            {
                DeterminismResult<(string Kind, JsonElement Fields)>.Success success =>
                    ReadFields(success.Value.Kind, success.Value.Fields),
                DeterminismResult<(string Kind, JsonElement Fields)>.Failure failure =>
                    new DeterminismResult<CoreCommand>.Failure(failure.Error),
                _ => throw new InvalidOperationException("未知读取结果分支。")
            };
        }
        catch (SimulationJsonException exception)
        {
            return new DeterminismResult<CoreCommand>.Failure(exception.Error);
        }
        catch (JsonException)
        {
            return new DeterminismResult<CoreCommand>.Failure(
                new(DeterminismErrorKind.InvalidData, "invalid_json", ""));
        }
    }

    /// <summary>计算公共版本、领域种类和固定种类字段组成的规范负载。</summary>
    private string EncodePayload(CoreCommand command)
    {
        var kind = GetKind(command);
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("命令种类必须非空。", nameof(command));
        var text = SimulationJson.Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("input_format_version", InputFormatVersion);
            writer.WriteString("kind", kind);
            WriteFields(writer, command);
            writer.WriteEndObject();
        });
        // 类型构造并不保证领域字段合法；按同一输入契约检查后才能接受和记录。
        if (Decode(text) is DeterminismResult<CoreCommand>.Failure failure)
            throw new ArgumentException($"命令字段非法：{failure.Error.Code} {failure.Error.Path}", nameof(command));
        return text;
    }

    /// <summary>计算规范负载 UTF-8 字节的大写 SHA-256 文本。</summary>
    private static string ComputePayloadSha256(string text) =>
        SimulationJson.Sha256(text);

    /// <summary>读取版本和种类，检查重复字段，分离拥有独立生命周期的领域字段。</summary>
    private static DeterminismResult<(string Kind, JsonElement Fields)> ReadEnvelope(string json)
    {
        var root = SimulationJson.ParseObject(json);
        if (!root.TryGetProperty("input_format_version", out var version))
            return Unknown("unsupported_input_format", "/input_format_version");
        if (SimulationJson.UInt32(version, "/input_format_version") != InputFormatVersion)
            return Unknown("unsupported_input_format", "/input_format_version");
        if (!root.TryGetProperty("kind", out var kindField))
            return Unknown("unknown_command_kind", "/kind");
        var kind = SimulationJson.String(kindField, "/kind", nonEmpty: true);
        var fieldsText = SimulationJson.Format(writer =>
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("input_format_version" or "kind"))
                    property.WriteTo(writer);
            writer.WriteEndObject();
        });
        return new DeterminismResult<(string Kind, JsonElement Fields)>.Success(
            (kind, SimulationJson.ParseObject(fieldsText)));
    }

    /// <summary>报告缺失或不受支持的公共格式身份。</summary>
    private static DeterminismResult<(string Kind, JsonElement Fields)> Unknown(string code, string path) =>
        new DeterminismResult<(string Kind, JsonElement Fields)>.Failure(
            new(DeterminismErrorKind.UnknownFormat, code, path));

    /// <summary>取得该具体命令在版本 2 中的稳定种类名。</summary>
    /// <param name="command">属于此领域格式的命令。</param>
    /// <returns>不依赖类型运行时哈希或注册顺序的种类名。</returns>
    protected abstract string GetKind(CoreCommand command);

    /// <summary>按该种类的固定顺序写入公共包络以外的命令字段。</summary>
    /// <param name="writer">位于已打开的命令 JSON 对象中，已经写入版本和种类。</param>
    /// <param name="command">为字段提供数据的具体命令。</param>
    /// <remarks>只写字段，不开关顶层对象。集合按稳定身份排序，序列保持顺序，有限 double 使用 InvariantCulture 的 R 文本。</remarks>
    protected abstract void WriteFields(Utf8JsonWriter writer, CoreCommand command);

    /// <summary>按具体种类校验全部领域字段并构造命令。</summary>
    /// <param name="kind">已经读取的版本 2 种类名。</param>
    /// <param name="fields">不含版本和种类的领域字段对象；公共层已检查重复字段。</param>
    /// <returns>完整命令，或未知种类、未知字段、缺失字段、类型及范围错误。</returns>
    protected abstract DeterminismResult<CoreCommand> ReadFields(string kind, JsonElement fields);
}

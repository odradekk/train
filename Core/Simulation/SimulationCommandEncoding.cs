
using System.Text.Json;

namespace Train.Core.Simulation;

public sealed record EncodedCommand(string CanonicalText, string Sha256);

public abstract class SimulationCommandCodec
{
    public const uint InputFormatVersion = 2;

    public EncodedCommand Encode(CoreCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = EncodePayload(command);
        return new EncodedCommand(text, ComputePayloadSha256(text));
    }

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

    private static string ComputePayloadSha256(string text) =>
        SimulationJson.Sha256(text);

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

    private static DeterminismResult<(string Kind, JsonElement Fields)> Unknown(string code, string path) =>
        new DeterminismResult<(string Kind, JsonElement Fields)>.Failure(
            new(DeterminismErrorKind.UnknownFormat, code, path));

    protected abstract string GetKind(CoreCommand command);

    protected abstract void WriteFields(Utf8JsonWriter writer, CoreCommand command);

    protected abstract DeterminismResult<CoreCommand> ReadFields(string kind, JsonElement fields);
}

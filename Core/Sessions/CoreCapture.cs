using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal sealed class CoreCapture : ICoreCapture
{
    private readonly string? _payload;

    internal CoreCapture(CoreState state)
        : this(state.Position, state.NextCommandNumber, state.Revision, state.Content.Identity, state.Fault) { }

    private CoreCapture(SimulationPosition position, ulong nextCommandNumber, CoreRevision revision,
        CoreInputId inputIdentity, CoreFault? fault, string? payload = null)
    {
        Position = position;
        NextCommandNumber = nextCommandNumber;
        Revision = revision;
        InputIdentity = inputIdentity;
        Fault = fault;
        _payload = payload;
        References = new([], [], [], [inputIdentity]);
        var text = Format(WriteBoundary);
        Checkpoint = new(position, text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    public SimulationPosition Position { get; }
    public CoreRevision Revision { get; }
    public CoreInputId InputIdentity { get; }
    public CoreReferenceManifest References { get; }
    public CoreCheckpoint Checkpoint { get; }
    internal ulong NextCommandNumber { get; }
    internal CoreFault? Fault { get; }

    public string Encode() => _payload ?? Format(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("core_state_format_version", 1);
        writer.WritePropertyName("position");
        WritePosition(writer, Position);
        writer.WriteNumber("next_command_number", NextCommandNumber);
        writer.WriteNumber("revision", Revision.Value);
        writer.WriteString("input_identity", InputIdentity.Value);
        writer.WritePropertyName("fault");
        WriteFault(writer);
        writer.WriteEndObject();
    });

    internal static CoreResult<ICoreCapture> Decode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidState("");
            if (!root.TryGetProperty("core_state_format_version", out var version))
                throw new InvalidState("/core_state_format_version");
            if (Unsigned(version, "/core_state_format_version") != 1)
                return new CoreResult<ICoreCapture>.Failure([new("unsupported_format", "/core_state_format_version", null, [])]);
            Object(root, "", "core_state_format_version", "position", "next_command_number",
                "revision", "input_identity", "fault");
            var position = ReadPosition(root.GetProperty("position"), "/position");
            var number = Unsigned(root.GetProperty("next_command_number"), "/next_command_number", nonzero: true);
            var revision = new CoreRevision(Unsigned(root.GetProperty("revision"), "/revision"));
            var identity = String(root.GetProperty("input_identity"), "/input_identity");
            if (identity.Length != 64 || !identity.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
                throw new InvalidState("/input_identity");
            var fault = ReadFault(root.GetProperty("fault"));
            return new CoreResult<ICoreCapture>.Success(new CoreCapture(position, number, revision, new(identity), fault, payload));
        }
        catch (InvalidState error)
        {
            return new CoreResult<ICoreCapture>.Failure([new("invalid_state", error.Field, null, [])]);
        }
        catch (JsonException)
        {
            return new CoreResult<ICoreCapture>.Failure([new("invalid_state", "", null, [])]);
        }
    }

    private void WriteBoundary(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("position");
        WritePosition(writer, Position);
        writer.WriteNumber("next_command_number", NextCommandNumber);
        writer.WritePropertyName("fault");
        WriteFault(writer);
        writer.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter writer, SimulationPosition position)
    {
        writer.WriteStartObject();
        writer.WriteNumber("completed_steps", position.CompletedSteps);
        writer.WriteNumber("sequence", position.Sequence);
        writer.WriteEndObject();
    }

    private void WriteFault(Utf8JsonWriter writer)
    {
        if (Fault is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("code", Fault.Code);
        writer.WritePropertyName("position");
        WritePosition(writer, Fault.Position);
        writer.WriteString("operation", Fault.Operation);
        writer.WriteString("field", Fault.Field);
        writer.WriteNull("subject");
        writer.WriteEndObject();
    }

    private static CoreFault? ReadFault(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        Object(value, "/fault", "code", "position", "operation", "field", "subject");
        var code = String(value.GetProperty("code"), "/fault/code", nonempty: true);
        var position = ReadPosition(value.GetProperty("position"), "/fault/position");
        var operation = String(value.GetProperty("operation"), "/fault/operation", nonempty: true);
        var field = String(value.GetProperty("field"), "/fault/field");
        if (value.GetProperty("subject").ValueKind != JsonValueKind.Null)
            throw new InvalidState("/fault/subject");
        return new(code, position, operation, field, null);
    }

    private static SimulationPosition ReadPosition(JsonElement value, string path)
    {
        Object(value, path, "completed_steps", "sequence");
        return new(Unsigned(value.GetProperty("completed_steps"), path + "/completed_steps"),
            Unsigned(value.GetProperty("sequence"), path + "/sequence"));
    }

    private static void Object(JsonElement value, string path, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidState(path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            string name;
            try { name = property.Name; }
            catch (InvalidOperationException) { throw new InvalidState(path); }
            if (!fields.Contains(name, StringComparer.Ordinal) || !seen.Add(name))
                throw new InvalidState(path + "/" + name.Replace("~", "~0").Replace("/", "~1"));
        }
        foreach (var name in fields)
            if (!seen.Contains(name))
                throw new InvalidState(path + "/" + name);
    }

    private static ulong Unsigned(JsonElement value, string path, bool nonzero = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result) || (nonzero && result == 0))
            throw new InvalidState(path);
        return result;
    }

    private static string String(JsonElement value, string path, bool nonempty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidState(path);
        string result;
        try { result = value.GetString()!; }
        catch (InvalidOperationException) { throw new InvalidState(path); }
        if (nonempty && result.Length == 0)
            throw new InvalidState(path);
        return result;
    }

    private static string Format(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class InvalidState(string field) : Exception
    {
        internal string Field { get; } = field;
    }
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Sessions;

internal sealed class CoreCapture : ICoreCapture
{
    internal CoreCapture(CoreState state)
        : this(state.Position, state.NextCommandNumber, state.Revision, state.Content.Identity, state.Fault,
            state.World, WorldReferences.Create(state.World, state.Content)) { }

    private CoreCapture(SimulationPosition position, ulong nextCommandNumber, CoreRevision revision,
        CoreInputId inputIdentity, CoreFault? fault, WorldState world, CoreReferenceManifest references)
    {
        Position = position;
        NextCommandNumber = nextCommandNumber;
        Revision = revision;
        InputIdentity = inputIdentity;
        Fault = fault;
        World = world;
        References = references;
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
    internal WorldState World { get; }

    public string Encode() => Format(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("core_state_format_version", 23);
        writer.WritePropertyName("position");
        WritePosition(writer, Position);
        writer.WriteNumber("next_command_number", NextCommandNumber);
        writer.WriteNumber("revision", Revision.Value);
        writer.WriteString("input_identity", InputIdentity.Value);
        writer.WritePropertyName("fault");
        WriteFault(writer);
        writer.WritePropertyName("world");
        WorldJson.Write(writer, World);
        writer.WritePropertyName("references");
        WorldJson.WriteReferences(writer, References);
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
                return new CoreResult<ICoreCapture>.Failure([new("unsupported_format", "", null, [])]);
            if (!root.TryGetProperty("core_state_format_version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetUInt64(out var numberVersion) || numberVersion != 23)
                return new CoreResult<ICoreCapture>.Failure([new("unsupported_format", "/core_state_format_version", null, [])]);
            Object(root, "", "core_state_format_version", "position", "next_command_number",
                "revision", "input_identity", "fault", "world", "references");
            var position = ReadPosition(root.GetProperty("position"), "/position");
            var number = Unsigned(root.GetProperty("next_command_number"), "/next_command_number", nonzero: true);
            var revision = new CoreRevision(Unsigned(root.GetProperty("revision"), "/revision"));
            var identity = String(root.GetProperty("input_identity"), "/input_identity");
            if (!WorldFormat.IsHash(identity))
                throw new CoreStateFormatException("/input_identity");
            var fault = ReadFault(root.GetProperty("fault"));
            var world = WorldJson.Read(root.GetProperty("world"));
            WorldStateValidation.ValidateBoundary(world, position);
            var references = WorldJson.ReadReferences(root.GetProperty("references"));
            var knownMissing = references.Definitions.Where(d => d.WasMissing)
                .Select(d => new MissingCoreReference(d.Definition, d.Field)).ToImmutableArray();
            if (!WorldJson.SameReferences(references, WorldReferences.Create(world, new CoreInputId(identity), knownMissing)))
                throw new CoreStateFormatException("/references");
            return new CoreResult<ICoreCapture>.Success(new CoreCapture(position, number, revision, new(identity), fault, world, references));
        }
        catch (CoreStateFormatException error)
        {
            return new CoreResult<ICoreCapture>.Failure([new("invalid_state", error.Field, null, [])]);
        }
        catch (JsonException)
        {
            return new CoreResult<ICoreCapture>.Failure([new("unsupported_format", "", null, [])]);
        }
        catch (InvalidOperationException)
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
        writer.WritePropertyName("world");
        WorldJson.Write(writer, World);
        writer.WriteEndObject();
    }

    internal static CoreProblem? ValidateCheckpoint(CoreCheckpoint checkpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(checkpoint.CanonicalText);
            var root = document.RootElement;
            Object(root, "", "position", "next_command_number", "fault", "world");
            if (ReadPosition(root.GetProperty("position"), "/position") != checkpoint.Position)
                return new("invalid_state", "/position", null, []);
            Unsigned(root.GetProperty("next_command_number"), "/next_command_number", nonzero: true);
            ReadFault(root.GetProperty("fault"));
            var world = WorldJson.Read(root.GetProperty("world"));
            WorldStateValidation.ValidateBoundary(world, checkpoint.Position);
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint.CanonicalText))) != checkpoint.Sha256)
                return new("invalid_state", "Sha256", null, []);
            return null;
        }
        catch (CoreStateFormatException error) { return new("invalid_state", error.Field, null, []); }
        catch (JsonException) { return new("unsupported_format", "CanonicalText", null, []); }
        catch (InvalidOperationException) { return new("invalid_state", "", null, []); }
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
            throw new CoreStateFormatException("/fault/subject");
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
            throw new CoreStateFormatException(path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            string name;
            try { name = property.Name; }
            catch (InvalidOperationException) { throw new CoreStateFormatException(path); }
            if (!fields.Contains(name, StringComparer.Ordinal) || !seen.Add(name))
                throw new CoreStateFormatException(path + "/" + name.Replace("~", "~0").Replace("/", "~1"));
        }
        foreach (var name in fields)
            if (!seen.Contains(name))
                throw new CoreStateFormatException(path + "/" + name);
    }

    private static ulong Unsigned(JsonElement value, string path, bool nonzero = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result) || (nonzero && result == 0))
            throw new CoreStateFormatException(path);
        return result;
    }

    private static string String(JsonElement value, string path, bool nonempty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new CoreStateFormatException(path);
        string result;
        try { result = value.GetString()!; }
        catch (InvalidOperationException) { throw new CoreStateFormatException(path); }
        if (nonempty && result.Length == 0)
            throw new CoreStateFormatException(path);
        return result;
    }

    private static string Format(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

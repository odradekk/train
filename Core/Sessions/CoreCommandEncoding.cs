using System.Text.Json;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal static class CoreCommandEncoding
{
    internal static string Encode(CoreCommand.PublishContent command)
    {
        if (!ValidIdentity(command.Content.Identity.Value))
            throw new ArgumentException("输入身份必须是大写 SHA-256。", nameof(command));
        return "{\"input_format_version\":2,\"kind\":\"content.publish\",\"input_identity\":\""
            + command.Content.Identity.Value + "\"}";
    }

    internal static CoreResult<CoreInputId> Decode(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input_format_version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetUInt32(out var number) || number != 2)
                return Failure("unsupported_format", "/input_format_version");
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || ReadString(kind, "/kind") != "content.publish")
                return Failure("unsupported_format", "/kind");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                string name;
                try { name = property.Name; }
                catch (InvalidOperationException) { return Failure("invalid_state", ""); }
                if (name is not ("input_format_version" or "kind" or "input_identity") || !seen.Add(name))
                    return Failure("invalid_state", "/" + name.Replace("~", "~0").Replace("/", "~1"));
            }
            if (!root.TryGetProperty("input_identity", out var identity)
                || identity.ValueKind != JsonValueKind.String || !ValidIdentity(ReadString(identity, "/input_identity")))
                return Failure("invalid_state", "/input_identity");
            return new CoreResult<CoreInputId>.Success(new(identity.GetString()!));
        }
        catch (JsonException)
        {
            return Failure("unsupported_format", "");
        }
        catch (InvalidString error)
        {
            return Failure("invalid_state", error.Field);
        }
        catch (InvalidOperationException)
        {
            return Failure("invalid_state", "");
        }
    }

    private static string? ReadString(JsonElement value, string field)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException) { throw new InvalidString(field); }
    }

    private sealed class InvalidString(string field) : Exception
    {
        internal string Field { get; } = field;
    }

    private static bool ValidIdentity(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static CoreResult<CoreInputId> Failure(string code, string field)
        => new CoreResult<CoreInputId>.Failure([new(code, field, null, [])]);
}

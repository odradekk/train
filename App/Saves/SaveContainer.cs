using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Saves;

internal sealed record SaveHeader(int SaveFormatVersion, Guid Id, string Name, DateTimeOffset SavedAt,
    SimulationPosition Position, ImmutableArray<PackageMetadata> Packages);
internal sealed record SaveDocument(SaveHeader Header, string CorePayload, string ContentPayload);
internal sealed class SaveFormatException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal static class SaveContainer
{
    internal const int FormatVersion = 6;

    internal static string Encode(SaveDocument document)
    {
        ValidateHeader(JsonSerializer.SerializeToElement(document.Header));
        ArgumentNullException.ThrowIfNull(document.CorePayload);
        ArgumentNullException.ThrowIfNull(document.ContentPayload);
        return JsonSerializer.Serialize(document);
    }

    internal static SaveDocument Decode(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            CheckObject(root);
            CheckLegacy(root);
            var header = ValidateHeader(root.GetProperty("Header"));
            var core = root.GetProperty("CorePayload");
            var content = root.GetProperty("ContentPayload");
            if (core.ValueKind != JsonValueKind.String || content.ValueKind != JsonValueKind.String)
                throw Invalid();
            return new(header, core.GetString()!, content.GetString()!);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw Invalid();
        }
    }

    internal static SaveHeader ReadHeader(Stream stream)
    {
        // 单字节读取停在头部末尾，避免列表查询消费后面的模块载荷。
        using var prefix = new MemoryStream();
        var depth = 0;
        var quoted = false;
        var escaped = false;
        var headerStarted = false;
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0) throw Invalid();
            prefix.WriteByte((byte)value);
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
                continue;
            }
            if (value == '"') quoted = true;
            else if (value == '{')
            {
                depth++;
                if (depth == 2) headerStarted = true;
            }
            else if (value == '}')
            {
                depth--;
                if (headerStarted && depth == 1)
                {
                    prefix.WriteByte((byte)'}');
                    try
                    {
                        using var json = JsonDocument.Parse(prefix.ToArray());
                        CheckObject(json.RootElement);
                        CheckLegacy(json.RootElement);
                        return ValidateHeader(json.RootElement.GetProperty("Header"));
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
                    { throw Invalid(); }
                }
                if (depth == 0)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(prefix.ToArray());
                        CheckLegacy(json.RootElement);
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
                    { throw Invalid(); }
                    throw Invalid();
                }
            }
        }
    }

    private static SaveHeader ValidateHeader(JsonElement header)
    {
        CheckObject(header);
        var version = header.GetProperty("SaveFormatVersion").GetInt32();
        CheckVersion(version);
        foreach (var field in new[] { "Id", "Name", "SavedAt", "Position", "Packages" })
            _ = header.GetProperty(field);
        var position = header.GetProperty("Position");
        CheckObject(position);
        _ = position.GetProperty("CompletedSteps").GetUInt64();
        _ = position.GetProperty("Sequence").GetUInt64();
        var result = header.Deserialize<SaveHeader>() ?? throw Invalid();
        if (result.Id == Guid.Empty || string.IsNullOrWhiteSpace(result.Name) || result.SavedAt == default || result.Packages.IsDefault)
            throw Invalid();
        foreach (var package in result.Packages)
            if (package is null || string.IsNullOrWhiteSpace(package.Id.Value) || string.IsNullOrWhiteSpace(package.Name)
                || string.IsNullOrWhiteSpace(package.Version) || package.Authors.IsDefault || package.Dependencies.IsDefault)
                throw Invalid();
        return result;
    }

    private static void CheckObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw Invalid();
            if (field.Value.ValueKind == JsonValueKind.Object) CheckObject(field.Value);
            else if (field.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in field.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) CheckObject(item);
        }
    }

    private static void CheckLegacy(JsonElement root)
    {
        foreach (var name in new[] { "SaveFormatVersion", "saveFormatVersion", "FormatVersion", "formatVersion" })
            if (root.TryGetProperty(name, out var value)) CheckVersion(value.GetInt32());
    }

    private static void CheckVersion(int version)
    {
        if (version is >= 2 and <= 5)
            throw new SaveFormatException("unsupported-legacy-format", "基于全局体素世界的旧存档已不支持");
        if (version != FormatVersion)
            throw new SaveFormatException("unsupported-format", $"不支持存档格式 {version}");
    }

    private static SaveFormatException Invalid() => new("invalid-save", "存档容器或头部损坏");
}

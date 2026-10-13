using System.Text.Json;
using System.Text.Unicode;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal static class ContentDocument
{
    internal static JsonDocument? Parse(byte[] bytes, LoadPackage package, string path)
    {
        if (!Utf8.IsValid(bytes))
        {
            package.Reject("invalid_json", path, "", "文件不是有效的 UTF-8。");
            return null;
        }
        try
        {
            var document = JsonDocument.Parse(bytes, StrictJson.DocumentOptions);
            var duplicate = DuplicateField(document.RootElement, "");
            if (duplicate is null) return document;
            document.Dispose();
            package.Reject("invalid_json", path, duplicate, "JSON 字段重复。");
            return null;
        }
        catch (JsonException exception)
        {
            package.Reject("invalid_json", path, "", exception.Message);
            return null;
        }
    }

    internal static string? DuplicateField(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var child = field + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                if (!names.Add(property.Name)) return child;
                var duplicate = DuplicateField(property.Value, child);
                if (duplicate is not null) return duplicate;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var duplicate = DuplicateField(item, field + "/" + index++);
                if (duplicate is not null) return duplicate;
            }
        }
        return null;
    }

    internal static string IoCode(C.ContentIoFailure failure) => failure switch
    {
        C.ContentIoFailure.Missing => "file_missing",
        C.ContentIoFailure.LimitExceeded => "file_too_large",
        C.ContentIoFailure.LinkedPath => "linked_path",
        C.ContentIoFailure.Inaccessible => "file_unreadable",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    internal static bool SafeEntry(string name) => name.Length > 0 && name is not "." and not ".." &&
        !name.Any(character => character is '/' or '\\' or ':' or '\0');
}

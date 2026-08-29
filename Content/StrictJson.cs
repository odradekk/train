using System.Text.Json;

namespace Train.Content;

internal static class StrictJson
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16
    };

    public static bool HasDuplicateProperties(byte[] utf8, DiagnosticBag diagnostics, string sourceFile)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var stack = new Stack<HashSet<string>>();
        var path = new Stack<string>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        stack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (stack.Count > 0)
                            stack.Pop();
                        if (path.Count > 0)
                            path.Pop();
                        break;
                    case JsonTokenType.StartArray:
                        path.Push("[]");
                        break;
                    case JsonTokenType.EndArray:
                        if (path.Count > 0)
                            path.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (stack.Count > 0 && !stack.Peek().Add(name))
                        {
                            diagnostics.Warn(ContentDiagnosticCodes.DuplicateProperty, "Duplicate JSON property.", JsonPointer(path, name));
                            return true;
                        }

                        path.Push(name);
                        break;
                }

                if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True
                    or JsonTokenType.False or JsonTokenType.Null)
                {
                    if (path.Count > 0)
                        path.Pop();
                }
            }
        }
        catch (JsonException exception)
        {
            diagnostics.Source = sourceFile;
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return true;
        }

        return false;
    }

    public static bool TryGetString(
        JsonElement element,
        DiagnosticBag diagnostics,
        string jsonPath,
        string code,
        out string value)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            diagnostics.Warn(code, "Expected a string.", jsonPath);
            value = "";
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    public static bool TryNormalizeText(
        string? value,
        int min,
        int max,
        DiagnosticBag diagnostics,
        string jsonPath,
        string code,
        out string normalized)
    {
        try
        {
            normalized = Train.Domain.Goods.GoodDefinition.NormalizeDisplayText(value ?? "", min, max, "value");
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics.Warn(code, "Text is empty or outside the allowed length.", jsonPath);
            normalized = "";
            return false;
        }
    }

    private static string JsonPointer(Stack<string> path, string name)
    {
        if (path.Count == 0)
            return "/" + name;
        var parts = path.Reverse().Where(part => part != "[]");
        return "/" + string.Join('/', parts.Append(name));
    }
}

using System.Text.Json;
using Train.Domain.Goods;

namespace Train.Content;

internal sealed class ParsedGood
{
    public string LocalId { get; }
    public GoodCategory Category { get; }
    public string Name { get; }
    public string Description { get; }
    public long BaseMonetaryValue { get; }
    public VoxelVisualDefinition Visual { get; }
    public string SourceFile { get; }

    public ParsedGood(
        string localId,
        GoodCategory category,
        string name,
        string description,
        long baseMonetaryValue,
        VoxelVisualDefinition visual,
        string sourceFile)
    {
        LocalId = localId;
        Category = category;
        Name = name;
        Description = description;
        BaseMonetaryValue = baseMonetaryValue;
        Visual = visual;
        SourceFile = sourceFile;
    }
}

internal static class GoodsJsonParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16
    };

    public static IReadOnlyList<ParsedGood> Parse(
        byte[] utf8,
        string @namespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        diagnostics.Source = sourceFile;
        if (utf8.Length > ContentLimits.GoodsFileMaxBytes)
        {
            diagnostics.Warn(ContentDiagnosticCodes.FileTooLarge, "Goods JSON exceeds the 1 MiB limit.");
            return Array.Empty<ParsedGood>();
        }

        if (HasDuplicateProperties(utf8, diagnostics, sourceFile))
            return Array.Empty<ParsedGood>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return Array.Empty<ParsedGood>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Goods file must be a JSON object.", "$");
                return Array.Empty<ParsedGood>();
            }

            return ParseDocument(document.RootElement, @namespace, sourceFile, diagnostics);
        }
    }

    private static IReadOnlyList<ParsedGood> ParseDocument(
        JsonElement root,
        string @namespace,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "schema_version" and not "goods")
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", "/" + property.Name);
                return Array.Empty<ParsedGood>();
            }
        }

        if (!root.TryGetProperty("schema_version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version) ||
            version != 1)
        {
            diagnostics.Warn(
                ContentDiagnosticCodes.SchemaVersionUnsupported,
                "Only schema_version 1 is supported.",
                "/schema_version");
            return Array.Empty<ParsedGood>();
        }

        if (!root.TryGetProperty("goods", out var goodsElement) || goodsElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "goods must be an array.", "/goods");
            return Array.Empty<ParsedGood>();
        }

        var goods = new List<ParsedGood>();
        var index = 0;
        foreach (var item in goodsElement.EnumerateArray())
        {
            var path = $"/goods/{index}";
            if (TryParseGood(item, @namespace, sourceFile, path, diagnostics, out var good))
                goods.Add(good);
            index++;
        }

        return goods;
    }

    private static bool TryParseGood(
        JsonElement element,
        string @namespace,
        string sourceFile,
        string jsonPath,
        DiagnosticBag diagnostics,
        out ParsedGood good)
    {
        good = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, "Each good must be an object.", jsonPath);
            return false;
        }

        string? localId = null;
        string? categoryText = null;
        string? name = null;
        string? description = null;
        long? value = null;
        JsonElement? visual = null;

        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "id":
                    if (!TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.LocalIdInvalid, out localId))
                        return false;
                    break;
                case "category":
                    if (!TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CategoryUnsupported, out categoryText))
                        return false;
                    break;
                case "name":
                    if (!TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.NameInvalid, out name))
                        return false;
                    break;
                case "description":
                    if (!TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.DescriptionInvalid, out description))
                        return false;
                    break;
                case "base_monetary_value":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var parsedValue))
                    {
                        diagnostics.Warn(ContentDiagnosticCodes.BaseMonetaryValueInvalid, "Base monetary value must be an integer.", fieldPath);
                        return false;
                    }

                    value = parsedValue;
                    break;
                case "visual":
                    visual = property.Value;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (localId is null || !GoodId.IsValidPart(localId))
        {
            diagnostics.Warn(ContentDiagnosticCodes.LocalIdInvalid, "Local id is invalid.", jsonPath + "/id");
            return false;
        }

        if (!GoodCategoryText.TryParse(categoryText, out var category))
        {
            diagnostics.Warn(ContentDiagnosticCodes.CategoryUnsupported, "Unsupported good category.", jsonPath + "/category");
            return false;
        }

        if (!TryNormalizeText(name, GoodDefinition.NameMinLength, GoodDefinition.NameMaxLength, diagnostics, jsonPath + "/name", ContentDiagnosticCodes.NameInvalid, out var normalizedName))
            return false;
        if (!TryNormalizeText(description, GoodDefinition.DescriptionMinLength, GoodDefinition.DescriptionMaxLength, diagnostics, jsonPath + "/description", ContentDiagnosticCodes.DescriptionInvalid, out var normalizedDescription))
            return false;

        if (value is null or < GoodDefinition.BaseMonetaryValueMin or > GoodDefinition.BaseMonetaryValueMax)
        {
            diagnostics.Warn(ContentDiagnosticCodes.BaseMonetaryValueInvalid, "Base monetary value is out of range.", jsonPath + "/base_monetary_value");
            return false;
        }

        if (visual is null || !TryParseVisual(visual.Value, jsonPath + "/visual", diagnostics, out var voxel))
        {
            if (visual is null)
                diagnostics.Warn(ContentDiagnosticCodes.VisualRequired, "natural_resource goods require a visual component.", jsonPath + "/visual");
            return false;
        }

        _ = @namespace;
        good = new ParsedGood(localId, category, normalizedName, normalizedDescription, value.Value, voxel, sourceFile);
        return true;
    }

    private static bool TryParseVisual(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out VoxelVisualDefinition visual)
    {
        visual = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.VisualRequired, "visual must be an object.", jsonPath);
            return false;
        }

        string? icon = null, top = null, side = null, bottom = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "icon":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out icon))
                        return false;
                    break;
                case "voxel_top":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out top))
                        return false;
                    break;
                case "voxel_side":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out side))
                        return false;
                    break;
                case "voxel_bottom":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out bottom))
                        return false;
                    break;
                default:
                    diagnostics.Warn(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (icon is null || top is null || side is null || bottom is null)
        {
            diagnostics.Warn(ContentDiagnosticCodes.VisualRequired, "visual requires icon, voxel_top, voxel_side, and voxel_bottom.", jsonPath);
            return false;
        }

        visual = new VoxelVisualDefinition(icon, top, side, bottom);
        return true;
    }

    private static bool TryGetResourcePath(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out string path)
    {
        path = "";
        if (!TryGetString(element, diagnostics, jsonPath, ContentDiagnosticCodes.PathInvalid, out var raw))
            return false;
        if (!ContentPath.TryNormalize(raw, out var normalized) || !ContentPath.IsPngPath(normalized))
        {
            diagnostics.Warn(ContentDiagnosticCodes.PathInvalid, "Visual path is not a safe relative PNG path.", jsonPath);
            return false;
        }

        path = normalized;
        return true;
    }

    private static bool TryGetString(
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

    private static bool TryNormalizeText(
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
            normalized = GoodDefinition.NormalizeDisplayText(value ?? "", min, max, "value");
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics.Warn(code, "Text is empty or outside the allowed length.", jsonPath);
            normalized = "";
            return false;
        }
    }

    private static bool HasDuplicateProperties(byte[] utf8, DiagnosticBag diagnostics, string sourceFile)
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

    private static string JsonPointer(Stack<string> path, string name)
    {
        if (path.Count == 0)
            return "/" + name;
        var parts = path.Reverse().Where(part => part != "[]");
        return "/" + string.Join('/', parts.Append(name));
    }
}

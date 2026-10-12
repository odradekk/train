using System.Text.Json;
using Train.Content.Voxels;

namespace Train.Content;

internal static class ContentJson
{
    public static T? ParseDocument<T>(byte[] utf8, string sourceFile, int schema,
        IReadOnlyCollection<string> allowedRootFields, DiagnosticBag diagnostics,
        Func<JsonElement, T?> parseRoot) where T : class
    {
        diagnostics.Source = sourceFile;
        if (utf8.Length > ContentLimits.JsonFileMaxBytes)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FileTooLarge, "Definition JSON exceeds the 1 MiB limit.");
            return null;
        }
        if (StrictJson.HasDuplicateProperties(utf8, diagnostics, sourceFile))
            return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, StrictJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Reject(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (!CheckFields(root, "", diagnostics, allowedRootFields.ToArray()))
                return null;
            if (!root.TryGetProperty("schema_version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != schema)
            {
                diagnostics.Reject(ContentDiagnosticCodes.SchemaVersionUnsupported, $"Only schema_version {schema} is supported.", "/schema_version");
                return null;
            }
            return parseRoot(root);
        }
    }

    public static IReadOnlyList<T>? ParseDefinitions<T>(byte[] utf8, string sourceFile, int schema,
        string arrayName, DiagnosticBag diagnostics, Func<JsonElement, string, T?> parseDefinition) where T : class
    {
        return ParseDocument(utf8, sourceFile, schema, new[] { "schema_version", arrayName }, diagnostics, root =>
        {
            var items = ReadArray(root, arrayName, "", diagnostics);
            if (items is null)
                return null;
            var definitions = new List<T>();
            var index = 0;
            foreach (var item in items.Value.EnumerateArray())
            {
                var definition = parseDefinition(item, $"/{arrayName}/{index++}");
                if (definition is null)
                    return null;
                definitions.Add(definition);
            }
            return definitions.AsReadOnly();
        });
    }

    public static bool CheckFields(JsonElement element, string path, DiagnosticBag diagnostics, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "Expected an object.", path.Length == 0 ? "$" : path);
            return false;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
            {
                diagnostics.Reject(ContentDiagnosticCodes.UnknownField, $"Unknown field '{property.Name}'.", FieldPath(path, property.Name));
                return false;
            }
        }
        return true;
    }

    public static JsonElement? ReadArray(JsonElement element, string name, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, name, path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Array)
            return field;
        diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "Expected an array.", FieldPath(path, name));
        return null;
    }

    public static string? ReadString(JsonElement element, string name, string path, DiagnosticBag diagnostics,
        string code = ContentDiagnosticCodes.FieldInvalid)
    {
        var field = ReadField(element, name, path, diagnostics, code);
        return field is not null && StrictJson.TryGetString(field.Value, diagnostics, FieldPath(path, name), code, out var value)
            ? value : null;
    }

    public static string? ReadLocalId(JsonElement element, string name, string path, DiagnosticBag diagnostics)
    {
        var value = ReadString(element, name, path, diagnostics, ContentDiagnosticCodes.LocalIdInvalid);
        if (value is null)
            return null;
        if (ContentIdentity.IsValidPart(value))
            return value;
        diagnostics.Reject(ContentDiagnosticCodes.LocalIdInvalid,
            "Expected a 1-64 character lowercase ASCII local identity without whitespace.", FieldPath(path, name));
        return null;
    }

    public static string? ReadFullIdentity(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        if (!StrictJson.TryGetString(element, diagnostics, path, ContentDiagnosticCodes.LocalIdInvalid, out var value))
            return null;
        if (ContentIdentity.IsValidFullId(value))
            return value;
        diagnostics.Reject(ContentDiagnosticCodes.LocalIdInvalid,
            "Expected a lowercase ASCII namespace:local-id identity without whitespace.", path);
        return null;
    }

    public static VoxelDefinitionId? ReadVoxelReference(JsonElement element, string packageNamespace, string path, DiagnosticBag diagnostics)
    {
        var raw = ReadString(element, "definition", path, diagnostics, ContentDiagnosticCodes.LocalIdInvalid);
        if (raw is null)
            return null;
        if (VoxelDefinitionId.TryResolve(raw, packageNamespace, out var id))
            return id;
        diagnostics.Reject(ContentDiagnosticCodes.LocalIdInvalid, "Expected a local or namespace:local-id voxel reference.", FieldPath(path, "definition"));
        return null;
    }

    public static int? ReadInt32(JsonElement element, string name, int min, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, name, path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var value) && value >= min)
            return value;
        diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, $"Expected an integer from {min} to Int32.MaxValue.", FieldPath(path, name));
        return null;
    }

    public static JsonElement? ReadField(JsonElement element, string name, string path, DiagnosticBag diagnostics,
        string code = ContentDiagnosticCodes.FieldInvalid)
    {
        if (element.TryGetProperty(name, out var value))
            return value;
        diagnostics.Reject(code, $"Required field '{name}' is missing.", FieldPath(path, name));
        return null;
    }

    private static string FieldPath(string path, string name) => path + "/" + name.Replace("~", "~0").Replace("/", "~1");
}

// 属于内容加载模块。
// 新领域格式共用严格文档、字段和标量读取；具体领域保留自己的结构与范围关系。
using System.Text.Json;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>将 JSON 的对象、数组和原子值逐层转换为领域值；失败只写调用方的包诊断。</summary>
internal static class ContentJson
{
    /// <summary>严格读取指定 schema 的定义数组；任一元素失败则返回 null，不保留部分文件。</summary>
    public static IReadOnlyList<T>? ParseDefinitions<T>(byte[] utf8, string sourceFile, int schema,
        string arrayName, DiagnosticBag diagnostics, Func<JsonElement, string, T?> parseDefinition) where T : class
    {
        diagnostics.Source = sourceFile;
        if (utf8.Length > ContentLimits.JsonFileMaxBytes)
        {
            diagnostics.Warn(ContentDiagnosticCodes.FileTooLarge, "Definition JSON exceeds the 1 MiB limit.");
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
            diagnostics.Warn(ContentDiagnosticCodes.InvalidJson, exception.Message);
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (!CheckFields(root, "", diagnostics, "schema_version", arrayName))
                return null;
            if (!root.TryGetProperty("schema_version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != schema)
            {
                diagnostics.Warn(ContentDiagnosticCodes.SchemaVersionUnsupported, $"Only schema_version {schema} is supported.", "/schema_version");
                return null;
            }
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
        }
    }

    /// <summary>检查对象形状及允许字段；缺失字段由实际读取该字段的函数报告。</summary>
    public static bool CheckFields(JsonElement element, string path, DiagnosticBag diagnostics, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Warn(ContentDiagnosticCodes.FieldInvalid, "Expected an object.", path);
            return false;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
            {
                diagnostics.Warn(ContentDiagnosticCodes.UnknownField, $"Unknown field '{property.Name}'.", FieldPath(path, property.Name));
                return false;
            }
        }
        return true;
    }

    /// <summary>读取必填数组，失败在该字段路径报告。</summary>
    public static JsonElement? ReadArray(JsonElement element, string name, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, name, path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Array)
            return field;
        diagnostics.Warn(ContentDiagnosticCodes.FieldInvalid, "Expected an array.", FieldPath(path, name));
        return null;
    }

    /// <summary>读取必填字符串；类型或缺失失败使用调用方提供的诊断类别。</summary>
    public static string? ReadString(JsonElement element, string name, string path, DiagnosticBag diagnostics,
        string code = ContentDiagnosticCodes.FieldInvalid)
    {
        var field = ReadField(element, name, path, diagnostics, code);
        return field is not null && StrictJson.TryGetString(field.Value, diagnostics, FieldPath(path, name), code, out var value)
            ? value : null;
    }

    /// <summary>读取并规范化必填局部身份，不允许完整命名空间身份。</summary>
    public static string? ReadLocalId(JsonElement element, string name, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, name, path, diagnostics, ContentDiagnosticCodes.LocalIdInvalid);
        return field is null ? null : ReadIdentityPart(field.Value, FieldPath(path, name), diagnostics);
    }

    /// <summary>读取一个身份部分字符串并规范化；用于局部 ID 与地貌标签。</summary>
    public static string? ReadIdentityPart(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        if (!StrictJson.TryGetString(element, diagnostics, path, ContentDiagnosticCodes.LocalIdInvalid, out var value))
            return null;
        var normalized = value.Trim();
        if (ContentIdentity.IsValidPart(normalized))
            return normalized;
        diagnostics.Warn(ContentDiagnosticCodes.LocalIdInvalid, "Expected a 1-64 character lowercase ASCII identity part.", path);
        return null;
    }

    /// <summary>读取 definition 字段并将局部货物引用补为当前包身份；不检查目标存在性。</summary>
    public static VoxelDefinitionId? ReadVoxelReference(JsonElement element, string packageNamespace, string path, DiagnosticBag diagnostics)
    {
        var raw = ReadString(element, "definition", path, diagnostics, ContentDiagnosticCodes.LocalIdInvalid);
        if (raw is null)
            return null;
        if (VoxelDefinitionId.TryResolve(raw, packageNamespace, out var id))
            return id;
        diagnostics.Warn(ContentDiagnosticCodes.LocalIdInvalid, "Expected a local or namespace:local-id voxel reference.", FieldPath(path, "definition"));
        return null;
    }

    /// <summary>读取范围为 min 至 Int32.MaxValue 的必填整数。</summary>
    public static int? ReadInt32(JsonElement element, string name, int min, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, name, path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var value) && value >= min)
            return value;
        diagnostics.Warn(ContentDiagnosticCodes.FieldInvalid, $"Expected an integer from {min} to Int32.MaxValue.", FieldPath(path, name));
        return null;
    }

    /// <summary>读取正 Int64 货币值。</summary>
    public static long? ReadPrice(JsonElement element, string name, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, name, path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out var value) && value > 0)
            return value;
        diagnostics.Warn(ContentDiagnosticCodes.FieldInvalid, "Expected a positive Int64 currency amount.", FieldPath(path, name));
        return null;
    }

    /// <summary>读取有限非负敏感度，正零和负零规范化为同一数值。</summary>
    public static double? ReadSensitivity(JsonElement element, string path, DiagnosticBag diagnostics)
    {
        var field = ReadField(element, "sensitivity", path, diagnostics);
        if (field is null)
            return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0)
            return value == 0 ? 0d : value;
        diagnostics.Warn(ContentDiagnosticCodes.FieldInvalid, "Expected a finite nonnegative sensitivity.", FieldPath(path, "sensitivity"));
        return null;
    }

    /// <summary>读取必填字段；缺失时报告该字段，不由后续类型检查重复报错。</summary>
    private static JsonElement? ReadField(JsonElement element, string name, string path, DiagnosticBag diagnostics,
        string code = ContentDiagnosticCodes.FieldInvalid)
    {
        if (element.TryGetProperty(name, out var value))
            return value;
        diagnostics.Warn(code, $"Required field '{name}' is missing.", FieldPath(path, name));
        return null;
    }

    /// <summary>将属性名追加为 JSON 指针片段，转义属性名自身的斜杠和波浪号。</summary>
    private static string FieldPath(string path, string name) => path + "/" + name.Replace("~", "~0").Replace("/", "~1");
}

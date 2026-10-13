using System.Text.Json;
using System.Text.RegularExpressions;
using Train.Content.Designs;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed class DefinitionJson
{
    private readonly LoadPackage _package;
    internal string Id { get; }

    internal DefinitionJson(JsonElement root, LoadPackage package, string path, string kind, int version)
    {
        _package = package;
        if (Int32(Required(root, "schema_version", ""), "/schema_version") != version)
            Fail("/schema_version", $"只支持 schema_version {version}。", "unsupported_format");
        var local = Text(Required(root, "id", ""), "/id");
        if (!ContentIdentity.IsValidPart(local) || local == "shared" || Path.GetFileName(path) != local + "." + kind + ".json")
            Fail("/id", "local id 必须有效且与文件名一致，不能是 shared。");
        Id = package.Metadata!.Id.Value + ":" + local;
    }

    internal string Reference(JsonElement value, string field)
    {
        var text = Text(value, field);
        var parts = text.Split(':');
        var ns = parts.Length == 1 ? _package.Metadata!.Id.Value : parts[0];
        if (parts.Length > 2 || !ContentIdentity.IsValidPart(parts[^1]) || parts[^1] == "shared" ||
            (ns != "core" && (!ModManifest.TryCanonicalUuid(ns, out var canonical) || canonical != ns)))
            Fail(field, "引用必须为 local id 或规范的 namespace:local-id。");
        if (ns != _package.Metadata!.Id.Value && !_package.Metadata.Dependencies.Any(d => d.Package.Value == ns))
            Fail(field, "引用的包必须在 dependencies 中声明。", "reference_missing");
        return ns + ":" + parts[^1];
    }

    internal static JsonElement Required(JsonElement value, string key, string field)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out _))
            Fail(Child(field, key), "缺少必填字段或父字段不是对象。");
        return value.GetProperty(key);
    }

    internal static void Object(JsonElement value, string field, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(field, "必须为对象。");
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal)) Fail(Child(field, property.Name), "未知字段。");
    }

    internal static void Array(JsonElement value, string field, int? length = null)
    {
        if (value.ValueKind != JsonValueKind.Array || (length is not null && value.GetArrayLength() != length))
            Fail(field, "必须为长度正确的数组。");
    }

    internal static IEnumerable<(JsonElement Value, string Field)> Items(JsonElement value, string field)
    {
        Array(value, field);
        var index = 0;
        foreach (var item in value.EnumerateArray()) yield return (item, field + "/" + index++);
    }

    internal static string Text(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String) Fail(field, "必须为字符串。");
        return value.GetString()!;
    }

    internal static C.DisplayText Display(JsonElement value, string field = "/display", params string[] extra)
    {
        Object(value, field, ["name", "description", .. extra]);
        return new(Trimmed("name", 100), Trimmed("description", 2000));
        string Trimmed(string key, int maximum)
        {
            var text = Text(Required(value, key, field), Child(field, key)).Trim();
            if (text.Length < 1 || text.Length > maximum) Fail(Child(field, key), $"去掉首尾空白后长度必须为 1–{maximum}。");
            return text;
        }
    }

    internal static int Int32(JsonElement value, string field, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            Fail(field, "必须为范围内的 Int32 整数。");
        return value.GetInt32();
    }

    internal static long Int64(JsonElement value, string field, long minimum = long.MinValue, long maximum = long.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < minimum || number > maximum)
            Fail(field, "必须为范围内的 Int64 整数。");
        return value.GetInt64();
    }

    internal static ulong UInt64(JsonElement value, string field, ulong minimum = 0)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number) || number < minimum)
            Fail(field, "必须为范围内的 UInt64 整数。");
        return value.GetUInt64();
    }

    internal static double Number(JsonElement value, string field, double minimum = double.MinValue, double maximum = double.MaxValue, bool positive = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
            number < minimum || number > maximum || (positive && number <= 0))
            Fail(field, "必须为范围内的有限数值。");
        var result = value.GetDouble();
        return result == 0 ? 0 : result;
    }

    internal static bool Boolean(JsonElement value, string field)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail(field, "必须为布尔值。");
        return value.GetBoolean();
    }

    internal static T EnumValue<T>(JsonElement value, string field) where T : struct, Enum
    {
        var text = Text(value, field);
        foreach (var candidate in Enum.GetValues<T>())
            if (DesignEvaluator.Wire(candidate) == text) return candidate;
        throw new DefinitionFormatException(field, "未知取值。", "invalid_field");
    }

    internal static void Unique<T>(IEnumerable<T> values, string field) where T : notnull
    {
        var found = new HashSet<T>();
        foreach (var value in values) if (!found.Add(value)) Fail(field, "数组不能重复。");
    }

    internal static string Asset(JsonElement value, string field, string extension)
    {
        var text = Text(value, field);
        if (!Regex.IsMatch(text, "\\A(shared/)?[a-z0-9][a-z0-9_-]*\\." + extension + "\\z", RegexOptions.CultureInvariant))
            Fail(field, "资产路径必须是同名目录文件或 shared/ 文件，且扩展名正确。");
        return text;
    }

    internal static K.SemanticRevision Semantic(string kind, string id, object value) => new(ContentHash.FromCanonicalJson(writer =>
        JsonSerializer.Serialize(writer, new { encoding_version = 1, kind, id, value })));

    internal static string Child(string field, string key) => field + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    internal static void Fail(string field, string message, string code = "invalid_field") => throw new DefinitionFormatException(field, message, code);
}

internal sealed class DefinitionFormatException(string field, string message, string code) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}

// Train.Core.Simulation 共享确定性文档的 UTF-8 写入和严格字段读取。
// 内部读取错误携带稳定路径，公共入口将其转换为完整失败结果。

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Train.Core.Simulation;

/// <summary>提供命令、恢复载荷和检查点共同使用的文档操作。</summary>
internal static class SimulationJson
{
    /// <summary>计算无 BOM、无尾换行的紧凑 UTF-8 JSON 文本。</summary>
    internal static string Format(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>计算文本 UTF-8 字节的大写十六进制 SHA-256。</summary>
    internal static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>读取独立存活的根对象，拒绝任何深度上的重复字段和非法字符串。</summary>
    internal static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        RequireObject(document.RootElement, "");
        CheckTree(document.RootElement, "");
        return document.RootElement.Clone();
    }

    /// <summary>检查一个对象恰好具有指定字段，不依赖字段枚举次序。</summary>
    internal static void CheckFields(JsonElement value, string path, params string[] names)
    {
        RequireObject(value, path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var fieldPath = path + "/" + Escape(property.Name);
            if (!seen.Add(property.Name))
                throw Invalid("duplicate_field", fieldPath);
            if (!names.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid("unknown_field", fieldPath);
        }
        foreach (var name in names)
            if (!seen.Contains(name))
                throw Invalid("missing_field", path + "/" + Escape(name));
    }

    /// <summary>检查值属于对象分支。</summary>
    internal static void RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("invalid_type", path);
    }

    /// <summary>读取完整范围内的 UInt64 整数，拒绝浮点文本和溢出。</summary>
    internal static ulong UInt64(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw Invalid("invalid_type", path);
        if (!value.TryGetUInt64(out var result))
            throw Invalid("invalid_uint64", path);
        return result;
    }

    /// <summary>读取完整范围内的 UInt32 整数。</summary>
    internal static uint UInt32(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw Invalid("invalid_type", path);
        if (!value.TryGetUInt32(out var result))
            throw Invalid("invalid_uint32", path);
        return result;
    }

    /// <summary>读取字符串字段；要求身份名时同时拒绝空字符串。</summary>
    internal static string String(JsonElement value, string path, bool nonEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid("invalid_type", path);
        string result;
        try
        {
            result = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument 可先接受未配对的 Unicode 转义，到读取字符串时才抛出此异常。
            throw Invalid("invalid_string", path);
        }
        if (nonEmpty && result.Length == 0)
            throw Invalid("empty_string", path);
        return result;
    }

    /// <summary>把字段名转为 JSON Pointer 中的一个片段。</summary>
    internal static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>检查全部后代的重复字段和 Unicode 字符串，返回稳定错误路径。</summary>
    internal static void CheckTree(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                string name;
                try { name = property.Name; }
                catch (InvalidOperationException) { throw Invalid("invalid_string", path); }
                var fieldPath = path + "/" + Escape(name);
                if (!seen.Add(name))
                    throw Invalid("duplicate_field", fieldPath);
                CheckTree(property.Value, fieldPath);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                CheckTree(item, path + "/" + (index++).ToString(CultureInfo.InvariantCulture));
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            _ = String(value, path);
        }
    }

    /// <summary>规范写入字段树；只对对象排序，数组保持原顺序。</summary>
    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string path = "")
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, path + "/" + Escape(property.Name));
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item, path + "/" + (index++).ToString(CultureInfo.InvariantCulture));
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(value.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, value, path);
                break;
            default:
                throw Invalid("invalid_type", path);
        }
    }

    /// <summary>按明确整数范围或有限 double 的 R 文本写入数值。</summary>
    private static void WriteNumber(Utf8JsonWriter writer, JsonElement value, string path)
    {
        var raw = value.GetRawText();
        // R 文本把 double 的负零写成 -0，必须保留其符号位。
        if (raw == "-0")
            writer.WriteRawValue(raw);
        else if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E'))
        {
            if (value.TryGetInt64(out var signed))
                writer.WriteNumberValue(signed);
            else if (value.TryGetUInt64(out var unsigned))
                writer.WriteNumberValue(unsigned);
            else
                throw Invalid("out_of_range", path);
        }
        else
        {
            if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw Invalid("non_finite_number", path);
            writer.WriteRawValue(number.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>构造可由公共读取入口返回的非法数据错误。</summary>
    internal static SimulationJsonException Invalid(string code, string path) =>
        new(new DeterminismError(DeterminismErrorKind.InvalidData, code, path));
}

/// <summary>在内部严格读取调用之间传递稳定字段错误。</summary>
internal sealed class SimulationJsonException(DeterminismError error) : Exception(error.Code)
{
    internal DeterminismError Error { get; } = error;
}

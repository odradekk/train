// 属于内容修订模块。
// 各格式写出自己的规范字段；共用 UTF-8 JSON 和 SHA-256 编码。
using System.Security.Cryptography;
using System.Text.Json;

namespace Train.Content;

/// <summary>计算调用方规范编码的 JSON 修订；不自行排序字段或引入依赖修订。</summary>
internal static class ContentHash
{
    /// <summary>返回原始字节的 SHA-256 大写十六进制值。</summary>
    public static string FromBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>调用 write 写出一个完整规范 JSON 值，返回其 UTF-8 字节的 SHA-256 大写十六进制值。</summary>
    public static string FromCanonicalJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            write(writer);
        return FromBytes(stream.ToArray());
    }
}

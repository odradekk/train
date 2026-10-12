using System.Security.Cryptography;
using System.Text.Json;

namespace Train.Content;

internal static class ContentHash
{
    public static string FromBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static string FromCanonicalJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            write(writer);
        return FromBytes(stream.ToArray());
    }
}

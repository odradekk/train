using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Content.Tests;

[TestClass]
public sealed class ContentHashTests
{
    [TestMethod]
    public void Canonical_json_hash_matches_sha256_across_buffer_boundaries_and_large_strings()
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
            WriteJson(writer);
        var expected = Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan));

        Assert.AreEqual(expected, ContentHash.FromCanonicalJson(WriteJson));

        static void WriteJson(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("large", new string('界', 8192));
            writer.WriteStartArray("numbers");
            for (var value = 0; value < 32768; value++) writer.WriteNumberValue(value);
            writer.WriteEndArray();
            writer.WriteString("tail", "末尾\n\"\\");
            writer.WriteEndObject();
        }
    }
}

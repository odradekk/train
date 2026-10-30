using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;

namespace Train.Godot.Tests.Saves;

[TestClass]
public sealed class SaveContainerTests
{
    internal static SaveDocument Document(string payload = " {\r\n \"中文\": \"🚂\" } \n") => new(
        new(6, Guid.NewGuid(), "中文存档", DateTimeOffset.UtcNow, new(12, 34), [new("fixture:mod", "1.2")]), payload, payload);

    internal static string Legacy(int version) => $$"""
        {"id":"e4a0ca31-b542-4f3e-bde2-bc4a27148a2b","base_layout":"fixture",
        "bounds":{"x":16,"y":8,"z":16},"chunk_size":16,
        "delta":{"added":[],"changed":[],"removed":[]},"display_name":"旧存档",
        "external_definitions":[],"save_format_version":{{version}},"seed":42,
        "generator_version":"blank-world-v1","next_entity_id":1,"transaction_count":0,"vehicles":[]}
        """;

    [TestMethod]
    public void Opaque_module_payloads_preserve_their_utf8_bytes()
    {
        var original = Document();
        var restored = SaveContainer.Decode(SaveContainer.Encode(original));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(original.CorePayload), Encoding.UTF8.GetBytes(restored.CorePayload));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(original.ContentPayload), Encoding.UTF8.GetBytes(restored.ContentPayload));
        Assert.AreEqual(original.Header.Id, restored.Header.Id);
    }

    [TestMethod]
    public void Encoding_preserves_default_serializer_text_for_opaque_payloads()
    {
        string[] payloads = ["", "{}", "中文 🚂 <>&\"\\\n\r\t\0", new string('x', 4096),
            new string('x', 4095) + "🚂tail", new string('x', 4095) + "\ud83dX",
            new string('x', 4095) + "\ude82X", "\ud83d", "\ude82", new string('\"', 12000)];
        foreach (var payload in payloads)
        foreach (var document in new[] { Document(payload) with { ContentPayload = "content:" + payload },
            Document(payload) with { CorePayload = "core:" + payload } })
        {
            var expected = JsonSerializer.Serialize(document);
            var actual = SaveContainer.Encode(document);
            Assert.AreEqual(expected, actual);
        }
    }

    [TestMethod]
    public void Header_reader_stops_before_payload_and_accepts_json_whitespace()
    {
        var original = Document("not module JSON");
        var json = SaveContainer.Encode(original).Replace("{\"Header\":", "{ \r\n \"Header\" : ");
        var payloadStart = json.IndexOf(",\"CorePayload\"", StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json[..payloadStart] + ",broken trailing payload"));
        var header = SaveContainer.ReadHeader(stream);
        Assert.AreEqual(original.Header.Id, header.Id);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(json[..payloadStart]), stream.Position);
        Assert.IsTrue(stream.Position < stream.Length);
    }

    [TestMethod]
    public void Legacy_versions_have_the_required_diagnostic()
    {
        for (var version = 2; version <= 5; version++)
        {
            var error = Assert.ThrowsExactly<SaveFormatException>(() => SaveContainer.Decode(Legacy(version)));
            Assert.AreEqual("基于全局体素世界的旧存档已不支持", error.Message);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Legacy(version)));
            Assert.AreEqual(error.Message, Assert.ThrowsExactly<SaveFormatException>(() => SaveContainer.ReadHeader(stream)).Message);
        }
    }

    [TestMethod]
    public void Header_format_uses_only_package_identity_version_and_position_numbers()
    {
        var original = Document();
        using var json = JsonDocument.Parse(SaveContainer.Encode(original));
        var header = json.RootElement.GetProperty("Header");
        var position = header.GetProperty("Position");
        CollectionAssert.AreEquivalent(new[] { "CompletedSteps", "Sequence" }, position.EnumerateObject().Select(field => field.Name).ToArray());
        Assert.AreEqual(12ul, position.GetProperty("CompletedSteps").GetUInt64());
        Assert.AreEqual(34ul, position.GetProperty("Sequence").GetUInt64());
        var package = header.GetProperty("Packages")[0];
        CollectionAssert.AreEquivalent(new[] { "Id", "Version" }, package.EnumerateObject().Select(field => field.Name).ToArray());
        Assert.AreEqual("fixture:mod", package.GetProperty("Id").GetString());
        Assert.AreEqual("1.2", package.GetProperty("Version").GetString());
        var decoded = SaveContainer.Decode(json.RootElement.GetRawText()).Header;
        Assert.AreEqual(original.Header.Position, decoded.Position);
        CollectionAssert.AreEqual(original.Header.Packages.ToArray(), decoded.Packages.ToArray());
    }

    [TestMethod]
    public void Invalid_versions_missing_fields_and_duplicate_fields_are_rejected()
    {
        var json = SaveContainer.Encode(Document());
        foreach (var broken in new[] { "not JSON", json.Replace("\"SaveFormatVersion\":6", "\"SaveFormatVersion\":7"),
            json.Replace("\"SaveFormatVersion\":6", "\"SaveFormatVersion\":6,\"SaveFormatVersion\":6"),
            json.Replace("\"Sequence\":34", "\"Other\":34"), System.Text.RegularExpressions.Regex.Replace(json, "\\\"Name\\\":\\\"[^\\\"]*\\\"", "\"Name\":\" \"") })
            Assert.ThrowsExactly<SaveFormatException>(() => SaveContainer.Decode(broken));
    }
}

using System.Collections.Immutable;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Saves;

[TestClass]
public sealed class SaveContainerTests
{
    internal static SaveDocument Document(string payload = " {\r\n \"中文\": \"🚂\" } \n") => new(
        new(6, Guid.NewGuid(), "中文存档", DateTimeOffset.UtcNow, new SimulationPosition(12, 34), []), payload, payload);

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
            var error = Assert.ThrowsExactly<SaveFormatException>(() => SaveContainer.Decode($"{{\"SaveFormatVersion\":{version}}}"));
            Assert.AreEqual("基于全局体素世界的旧存档已不支持", error.Message);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"SaveFormatVersion\":{version}}}"));
            Assert.AreEqual(error.Message, Assert.ThrowsExactly<SaveFormatException>(() => SaveContainer.ReadHeader(stream)).Message);
        }
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

using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Domain.Settlements;

namespace Train.Tests;

[TestClass]
public sealed class SettlementsJsonParserTests
{
    [TestMethod]
    public void Valid_city_parses_six_fields_and_qualified_resources()
    {
        var bag = new DiagnosticBag();
        var parsed = SettlementsJsonParser.Parse(
            Utf8(SettlementsFile(City("oak", resources: """["core:ferrous-ore"]"""))),
            "core",
            "cities.settlements.json",
            bag);

        Assert.AreEqual(0, bag.Items.Count);
        Assert.AreEqual(1, parsed.Count);
        var city = parsed[0];
        Assert.AreEqual("oak", city.LocalId);
        Assert.AreEqual("city", city.Type);
        Assert.AreEqual("橡木城", city.Name);
        Assert.AreEqual(new MapPosition(3, -2), city.Position);
        Assert.AreEqual(12, city.Population);
        CollectionAssert.AreEqual(new[] { new VoxelDefinitionId("core", "ferrous-ore") }, city.AvailableRawGoods.ToArray());
        Assert.AreEqual("cities.settlements.json", city.SourceFile);
    }

    [TestMethod]
    public void Strict_document_violations_yield_no_settlements()
    {
        var cases = new[]
        {
            "{ /* comment */ \"schema_version\": 1, \"settlements\": [] }",
            "{ \"schema_version\": 1, \"settlements\": [], }",
            "{ \"schema_version\": 1, \"settlements\": [], \"extra\": 1 }",
            "{ \"schema_version\": 1, \"schema_version\": 1, \"settlements\": [] }",
            "{ \"Schema_version\": 1, \"settlements\": [] }",
            "{ \"schema_version\": 2, \"settlements\": [] }",
            "{ \"schema_version\": 1.5, \"settlements\": [] }",
            "{ \"settlements\": [] }"
        };

        foreach (var json in cases)
        {
            var bag = new DiagnosticBag();
            var parsed = SettlementsJsonParser.Parse(Utf8(json), "core", "bad.settlements.json", bag);
            Assert.AreEqual(0, parsed.Count, json);
            Assert.IsTrue(bag.Items.Count > 0, json);
        }
    }

    [TestMethod]
    public void Field_boundaries_and_unsupported_type_are_rejected()
    {
        var invalids = new[]
        {
            City("Oak"),
            City("oak", type: "industrial"),
            City("oak", type: "City"),
            City("oak", name: " "),
            City("oak", population: "0"),
            City("oak", population: "-1"),
            City("oak", population: "1.5"),
            City("oak", position: """{"x":1}"""),
            City("oak", position: """{"x":1,"y":2,"z":0}"""),
            City("oak", resources: """["ferrous-ore"]"""),
            City("oak", resources: """["core:ferrous-ore","core:ferrous-ore"]"""),
            City("oak", extra: ",\"buildings\":{}")
        };

        foreach (var city in invalids)
        {
            var bag = new DiagnosticBag();
            var parsed = SettlementsJsonParser.Parse(Utf8(SettlementsFile(city)), "core", "bad.settlements.json", bag);
            Assert.AreEqual(0, parsed.Count, city);
            Assert.IsTrue(bag.Items.Count > 0, city);
        }
    }

    [TestMethod]
    public void Duplicate_and_unqualified_available_raw_goods_use_new_diagnostics()
    {
        var duplicate = new DiagnosticBag();
        Assert.AreEqual(0, SettlementsJsonParser.Parse(
            Utf8(SettlementsFile(City("oak", resources: """["core:ferrous-ore","core:ferrous-ore"]"""))),
            "core",
            "bad.settlements.json",
            duplicate).Count);
        Assert.IsTrue(duplicate.Items.Any(item =>
            item.Code == ContentDiagnosticCodes.DuplicateAvailableRawGood &&
            item.Message.Contains("available raw good", StringComparison.Ordinal)));

        var unqualified = new DiagnosticBag();
        Assert.AreEqual(0, SettlementsJsonParser.Parse(
            Utf8(SettlementsFile(City("oak", resources: """["ferrous-ore"]"""))),
            "core",
            "bad.settlements.json",
            unqualified).Count);
        Assert.IsTrue(unqualified.Items.Any(item =>
            item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference &&
            item.Message.Contains("Available raw good", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Legacy_natural_resources_field_is_unknown()
    {
        var bag = new DiagnosticBag();
        var json = """{"schema_version":1,"settlements":[{"id":"oak","type":"city","name":"橡木城","position":{"x":3,"y":-2},"population":12,"natural_resources":["core:ferrous-ore"]}]}""";
        var parsed = SettlementsJsonParser.Parse(Utf8(json), "core", "legacy.settlements.json", bag);
        Assert.AreEqual(0, parsed.Count);
        Assert.IsTrue(bag.Items.Any(item =>
            item.Code == ContentDiagnosticCodes.UnknownField &&
            item.JsonPath == "/settlements/0/natural_resources"));
    }

    [TestMethod]
    public void Name_is_trimmed_and_empty_resources_are_accepted()
    {
        var bag = new DiagnosticBag();
        var parsed = SettlementsJsonParser.Parse(
            Utf8(SettlementsFile(City("harbor", name: "  港湾  ", resources: "[]"))),
            "core",
            "ok.settlements.json",
            bag);

        Assert.AreEqual(0, bag.Items.Count);
        Assert.AreEqual(1, parsed.Count);
        Assert.AreEqual("港湾", parsed[0].Name);
        Assert.AreEqual(0, parsed[0].AvailableRawGoods.Count);
    }

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static string SettlementsFile(params string[] settlements) =>
        $"{{\"schema_version\":1,\"settlements\":[{string.Join(",", settlements)}]}}";

    private static string City(
        string id,
        string type = "city",
        string name = "橡木城",
        int? x = 3,
        int? y = -2,
        string? population = "12",
        string? position = null,
        string? resources = "[]",
        string extra = "")
    {
        var positionJson = position ?? $"{{\"x\":{x},\"y\":{y}}}";
        return $"{{\"id\":\"{id}\",\"type\":\"{type}\",\"name\":\"{name}\",\"position\":{positionJson},\"population\":{population},\"available_raw_goods\":{resources}{extra}}}";
    }
}

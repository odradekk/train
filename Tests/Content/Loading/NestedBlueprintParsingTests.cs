using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class NestedBlueprintParsingTests
{
    [TestMethod]
    public void Nested_copies_references_and_rotation_survive_serialization()
    {
        var root = Design("large_equipment");
        var child = Design("large_equipment");
        child["appearance"] = JsonNode.Parse("""{"behaviors":{"rotation":{"axis":"z","pivot":[1,-2,3],"range":[-37.5,89.25],"speed":12.5}}}""");
        child["layout"]!["composites"] = JsonNode.Parse("""[{"at":[1,2,3],"orientation":"pz_py","blueprint":"core:engine","paint":"#123456"}]""");
        AddCopy(root, child);
        var draft = Read(root);
        var copy = (C.NestedDesignItem.AssemblyCopy)draft.Layout.NestedItems.Single();
        Assert.AreEqual(new C.RotationAppearance(C.RotationAxis.Z, new(1, -2, 3), -37.5, 89.25, 12.5), ((C.DesignSpecification.Assembly)copy.Design.Specification).Rotation);
        var reference = (C.NestedDesignItem.FunctionalCompositeReference)copy.Design.Layout.NestedItems.Single();
        Assert.AreEqual("core:engine", reference.Definition.Value);
        Assert.AreEqual("#123456", reference.Paint!.Value.Value);
        RoundTrip(draft);
    }

    [TestMethod]
    public void Four_layers_are_accepted_and_fifth_is_rejected()
    {
        var root = Design("scene_model");
        var cursor = root;
        for (var i = 1; i < 4; i++)
        {
            var child = Design("scene_model");
            AddCopy(cursor, child);
            cursor = child;
        }
        Assert.IsInstanceOfType<C.NestedDesignItem.SceneModelCopy>(Read(root).Layout.NestedItems.Single());
        RoundTrip(Read(root));
        AddCopy(cursor, Design("scene_model"));
        Assert.ThrowsExactly<DefinitionFormatException>(() => Read(root));
    }

    [TestMethod]
    public void Copy_rejects_functional_specification_at_any_depth_and_category_mismatch()
    {
        var root = Design("large_equipment");
        var child = Design("large_equipment");
        var functional = Design("large_equipment");
        functional["simulation"] = JsonNode.Parse("""{"behaviors":{"control":{}}}""");
        AddCopy(child, functional);
        AddCopy(root, child);
        Assert.ThrowsExactly<DefinitionFormatException>(() => Read(root));
        var scene = Design("scene_model");
        AddCopy(scene, Design("large_equipment"));
        Assert.ThrowsExactly<DefinitionFormatException>(() => Read(scene));
    }

    [TestMethod]
    public void Functional_behaviors_round_trip_and_keep_voxel_validation_rules()
    {
        var root = Design("large_equipment");
        root["simulation"] = JsonNode.Parse("""{"behaviors":{"propulsion":{"traction":10,"rated_speed":20},"control":{},"storage":{"capacity":30},"production":{"work_rate":2},"extraction":{"work_rate":3},"construction":{"work_rate":4,"max_size_class":"extra_large"},"power":{"output":5},"weapon":{"damage":6,"interval":0.5},"platform":{}}}""");
        var draft = Read(root);
        Assert.AreEqual(9, ((C.DesignSpecification.FunctionalComposite)draft.Specification).Behaviors.Length);
        RoundTrip(draft);
        root["simulation"]!["behaviors"]!["weapon"]!["damage"] = 0;
        Assert.ThrowsExactly<DefinitionFormatException>(() => Read(root));
    }

    [TestMethod]
    public void Duplicate_origins_and_nested_functional_definition_are_rejected()
    {
        var root = Design("large_equipment");
        AddCopy(root, Design("large_equipment"));
        root["layout"]!["composites"]!.AsArray().Add(root["layout"]!["composites"]![0]!.DeepClone());
        Assert.ThrowsExactly<DefinitionFormatException>(() => Read(root));
        root["layout"]!["composites"]!.AsArray().RemoveAt(1);
        root["simulation"] = JsonNode.Parse("""{"behaviors":{"control":{}}}""");
        Assert.ThrowsExactly<DefinitionFormatException>(() => Read(root));
    }

    private static JsonObject Design(string category) => new()
    {
        ["category"] = category,
        ["display"] = new JsonObject { ["name"] = "design", ["description"] = "test" },
        ["layout"] = new JsonObject()
    };
    private static void AddCopy(JsonObject parent, JsonObject child) =>
        parent["layout"]!["composites"] = new JsonArray(new JsonObject
        {
            ["at"] = new JsonArray(0, 0, 0), ["design"] = child
        });
    private static C.DesignDraft Read(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return BlueprintParser.ReadDesign(document.RootElement);
    }
    private static void RoundTrip(C.DesignDraft draft) => Assert.AreEqual(Serialize(draft), Serialize(Read(JsonNode.Parse(Serialize(draft))!.AsObject())));
    private static string Serialize(C.DesignDraft draft)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            BlueprintParser.WriteDesign(writer, draft);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

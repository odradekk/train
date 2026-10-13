using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldDecodeTests
{
    [TestMethod]
    [DataRow("duplicate_node")]
    [DataRow("unordered_nodes")]
    [DataRow("unordered_source_noise")]
    [DataRow("unordered_capabilities")]
    [DataRow("missing_edge_node")]
    [DataRow("missing_vehicle_owner")]
    [DataRow("wrong_instance_kind")]
    [DataRow("missing_inventory_owner")]
    [DataRow("next_id_reused")]
    [DataRow("nonfinite_position")]
    [DataRow("negative_quantity")]
    [DataRow("null_collection")]
    [DataRow("missing_collection")]
    [DataRow("null_collection_element")]
    [DataRow("duplicate_nested_property")]
    [DataRow("unknown_nested_property")]
    [DataRow("reference_mismatch")]
    [DataRow("missing_world_field")]
    public void Corrupt_world_payload_is_rejected_without_changing_the_active_session(string corruption)
    {
        var factory = CoreFactory.Create();
        using var candidate = Success(factory.PrepareNew(Input(), WorldFixture.Content, WorldGenerationTests.Options));
        using var session = candidate.Activate();
        var original = Success(session.Capture(CapturePurpose.Save));
        var payload = original.Encode();
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(factory.DecodeState(payload));
        var root = JsonNode.Parse(payload)!.AsObject();
        var world = root["world"]!;
        var nodes = world["nodes"]!.AsArray();
        var instances = world["instances"]!.AsArray();
        var vehicle = instances.Single(i => i!["id"]!["$kind"]!.GetValue<string>() == "vehicle")!;
        switch (corruption)
        {
            case "duplicate_node": nodes[1]!["id"] = nodes[0]!["id"]!.DeepClone(); break;
            case "unordered_nodes":
                var first = nodes[0]!.DeepClone();
                nodes[0] = nodes[1]!.DeepClone(); nodes[1] = first;
                break;
            case "missing_edge_node": world["edges"]![0]!["end"]!["value"] = 999999; break;
            case "unordered_source_noise":
                world["source"]!["configuration"]!["elevation_noise"] = JsonNode.Parse("[{\"scale_meters\":16,\"amplitude\":0.5},{\"scale_meters\":8,\"amplitude\":0.5}]");
                break;
            case "unordered_capabilities":
                var capabilities = world["designs"]![0]!["parts"]![0]!["capabilities"]!.AsArray();
                var capability = capabilities[0]!.DeepClone();
                capabilities[0] = capabilities[1]!.DeepClone(); capabilities[1] = capability;
                break;
            case "missing_vehicle_owner": vehicle["details"]!["train"]!["value"] = 999999; break;
            case "wrong_instance_kind":
                vehicle["details"] = instances.First(i => i!["id"]!["$kind"]!.GetValue<string>() == "building")!["details"]!.DeepClone();
                break;
            case "missing_inventory_owner": world["inventories"]![0]!["owner"]!["id"]!["value"] = 999999; break;
            case "next_id_reused": world["next"]!["node"] = nodes.Count; break;
            case "nonfinite_position": nodes[0]!["position"]!["x"] = JsonNode.Parse("1e400"); break;
            case "negative_quantity": world["inventories"]!.AsArray().Single(i => i!["goods"]!.AsArray().Count > 0)!["goods"]![0]!["quantity"] = -1; break;
            case "null_collection": world["nodes"] = null; break;
            case "missing_collection": world.AsObject().Remove("nodes"); break;
            case "null_collection_element": nodes[0] = null; break;
            case "unknown_nested_property": nodes[0]!["position"]!["unexpected"] = true; break;
            case "reference_mismatch": root["references"]!["designs"]!.AsArray().Clear(); break;
            case "missing_world_field": world.AsObject().Remove("money"); break;
            case "duplicate_nested_property": break;
            default: Assert.Fail("Unknown corruption case."); break;
        }
        var corrupted = root.ToJsonString();
        if (corruption == "duplicate_nested_property")
        {
            var position = nodes[0]!["position"]!.ToJsonString();
            corrupted = corrupted.Replace(position, position.Insert(1, "\"x\":0,"), StringComparison.Ordinal);
        }
        var result = factory.DecodeState(corrupted);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result, corruption);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual("invalid_state", problem.Code, corruption);
        Assert.IsTrue(problem.Field!.StartsWith("/world", StringComparison.Ordinal)
            || problem.Field.StartsWith("/references", StringComparison.Ordinal), corruption);
        var after = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(payload, after.Encode());
        Assert.AreEqual(original.Checkpoint, after.Checkpoint);
    }

    [TestMethod]
    public void Raw_durability_above_the_design_maximum_survives_restore_and_queries_clamp_it()
    {
        var factory = CoreFactory.Create();
        using var candidate = Success(factory.PrepareNew(Input(), WorldFixture.Content, WorldGenerationTests.Options));
        using var session = candidate.Activate();
        var root = JsonNode.Parse(Success(session.Capture(CapturePurpose.Save)).Encode())!;
        var vehicle = root["world"]!["instances"]!.AsArray()
            .Single(i => i!["id"]!["$kind"]!.GetValue<string>() == "vehicle")!;
        vehicle["structure_durability"] = 123;
        vehicle["parts"]![0]!["current"] = 456;
        var decoded = Success(factory.DecodeState(root.ToJsonString()));
        using var restoredCandidate = Success(factory.PrepareRestore(decoded, WorldFixture.Content, WorldGenerationTests.Options));
        using var restored = restoredCandidate.Activate();
        var instance = ((Lookup<InstanceView>.Found)restored.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(new DurabilityView(100, 100), instance.Structure);
        Assert.AreEqual(new DurabilityView(100, 100), instance.Parts.Single().Durability);
        var savedAgain = Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(decoded.Encode(), savedAgain.Encode());
        var raw = JsonNode.Parse(savedAgain.Encode())!["world"]!["instances"]!.AsArray()
            .Single(i => i!["id"]!["$kind"]!.GetValue<string>() == "vehicle")!;
        Assert.AreEqual(123L, raw["structure_durability"]!.GetValue<long>());
        Assert.AreEqual(456L, raw["parts"]![0]!["current"]!.GetValue<long>());
    }

    private static WorldGenerationInput Input() => WorldFixture.Input with
    {
        Configuration = WorldFixture.Input.Configuration with { WidthMeters = 100 },
        Start = WorldFixture.Input.Start with
        {
            Trains = [WorldFixture.Input.Start.Trains[0] with { Inventory = [new(new GoodId.Voxel(WorldFixture.Material), 1)] }]
        }
    };

    private static T Success<T>(CoreResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<CoreResult<T>.Success>(result);
        return ((CoreResult<T>.Success)result).Value;
    }
}

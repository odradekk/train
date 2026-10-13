using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldDecodeTests
{
    [TestMethod]
    [DataRow("duplicate_node", null)]
    [DataRow("unordered_nodes", null)]
    [DataRow("unordered_source_noise", null)]
    [DataRow("unordered_capabilities", null)]
    [DataRow("missing_edge_node", null)]
    [DataRow("missing_vehicle_owner", null)]
    [DataRow("wrong_instance_kind", null)]
    [DataRow("missing_inventory_owner", null)]
    [DataRow("next_id_reused", null)]
    [DataRow("nonfinite_position", null)]
    [DataRow("negative_quantity", null)]
    [DataRow("null_collection", null)]
    [DataRow("missing_collection", null)]
    [DataRow("null_collection_element", null)]
    [DataRow("duplicate_nested_property", null)]
    [DataRow("unknown_nested_property", null)]
    [DataRow("reference_mismatch", null)]
    [DataRow("missing_world_field", null)]
    [DataRow("null_world", "/world")]
    [DataRow("excess_track_limit", "/world/edges")]
    [DataRow("zero_station_maximum", "/world/nodes/platforms")]
    [DataRow("zero_platform_maximum", "/world/nodes/platforms")]
    [DataRow("wrong_platform_count", "/world/nodes/platforms")]
    [DataRow("destroyed_station", "/world/nodes/platforms")]
    [DataRow("destroyed_platform_part", "/world/nodes/platforms")]
    [DataRow("wrong_station_kind", "/world/nodes/platforms")]
    [DataRow("missing_platform_capability", "/world/nodes/platforms")]
    [DataRow("missing_commission_design", "/world/source/configuration/start_constraints/required_affordable_commission_design")]
    [DataRow("missing_for_sale_design", "/world/source/configuration/npc_initialization/for_sale_vehicle_candidates")]
    [DataRow("missing_start_design", "/world/source/start/trains/vehicles")]
    [DataRow("missing_prebuilt_design", "/world/settlements/template/plots/prebuilt_design")]
    [DataRow("missing_founding_design", "/world/settlements/template/founding_buildings/design")]
    [DataRow("wrong_commission_design_kind", "/world/source/configuration/start_constraints/required_affordable_commission_design")]
    [DataRow("wrong_for_sale_design_kind", "/world/source/configuration/npc_initialization/for_sale_vehicle_candidates")]
    [DataRow("wrong_start_design_kind", "/world/source/start/trains/vehicles")]
    [DataRow("wrong_prebuilt_design_kind", "/world/settlements/template/plots/prebuilt_design")]
    [DataRow("wrong_founding_design_kind", "/world/settlements/template/founding_buildings/design")]
    public void Corrupt_world_payload_is_rejected_without_changing_the_active_session(string corruption, string? expectedField)
    {
        var factory = CoreFactory.Create();
        using var candidate = Prepare(InputWithInventory());
        using var session = candidate.Activate();
        var original = Success(session.Capture(CapturePurpose.Save));
        var payload = original.Encode();
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(factory.DecodeState(payload));
        var root = JsonNode.Parse(payload)!.AsObject();
        var world = root["world"]!;
        var nodes = world["nodes"]!.AsArray();
        var instances = world["instances"]!.AsArray();
        var vehicle = instances.Single(i => i!["id"]!["$kind"]!.GetValue<string>() == "vehicle")!;
        var station = instances.First(i => i!["id"]!["$kind"]!.GetValue<string>() == "building")!;
        var stationDesign = world["designs"]!.AsArray().Single(d =>
            d!["design"]!["value"]!.GetValue<string>() == station["design"]!["value"]!.GetValue<string>())!;
        var settlement = world["settlements"]![0]!;
        var settlementNode = nodes.Single(n => n!["id"]!["value"]!.GetValue<ulong>() == settlement["node"]!["value"]!.GetValue<ulong>())!;
        var configuration = world["source"]!["configuration"]!;
        var missingHash = new string('E', 64);
        var stationHash = station["design"]!["value"]!.GetValue<string>();
        var vehicleHash = vehicle["design"]!["value"]!.GetValue<string>();
        switch (corruption)
        {
            case "null_world": root["world"] = null; break;
            case "excess_track_limit": world["edges"]![0]!["track_limit"] = configuration["initial_railway"]!["maximum_tracks"]!.GetValue<int>() + 1; break;
            case "wrong_platform_count": settlementNode["platforms"] = settlementNode["platforms"]!.GetValue<int>() + 1; break;
            case "destroyed_station": station["structure_durability"] = 0; break;
            case "destroyed_platform_part": station["parts"]![0]!["current"] = 0; break;
            case "zero_station_maximum": stationDesign["max_structure_durability"] = 0; break;
            case "zero_platform_maximum": stationDesign["parts"]![0]!["max_durability"] = 0; break;
            case "wrong_station_kind": stationDesign["kind"]!["type"] = (int)BuildingKind.Warehouse; break;
            case "missing_platform_capability": stationDesign["parts"]![0]!["capabilities"]!.AsArray().Clear(); break;
            case "missing_commission_design": configuration["start_constraints"]!["required_affordable_commission_design"]!["value"] = missingHash; break;
            case "missing_for_sale_design": configuration["npc_initialization"]!["for_sale_vehicle_candidates"] = new JsonArray(new JsonObject { ["value"] = missingHash }); break;
            case "missing_start_design": world["source"]!["start"]!["trains"]![0]!["vehicles"]![0]!["value"] = missingHash; break;
            case "missing_prebuilt_design": settlement["template"]!["plots"]![0]!["prebuilt_design"]!["value"] = missingHash; break;
            case "missing_founding_design": settlement["template"]!["founding_buildings"] = new JsonArray(new JsonObject
                { ["plot_number"] = 1, ["design"] = new JsonObject { ["value"] = missingHash } }); break;
            case "wrong_commission_design_kind": configuration["start_constraints"]!["required_affordable_commission_design"]!["value"] = stationHash; break;
            case "wrong_for_sale_design_kind": configuration["npc_initialization"]!["for_sale_vehicle_candidates"] = new JsonArray(new JsonObject { ["value"] = stationHash }); break;
            case "wrong_start_design_kind": world["source"]!["start"]!["trains"]![0]!["vehicles"]![0]!["value"] = stationHash; break;
            case "wrong_prebuilt_design_kind": settlement["template"]!["plots"]![0]!["prebuilt_design"]!["value"] = vehicleHash; break;
            case "wrong_founding_design_kind": settlement["template"]!["founding_buildings"] = new JsonArray(new JsonObject
                { ["plot_number"] = 1, ["design"] = new JsonObject { ["value"] = vehicleHash } }); break;
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
        if (expectedField is not null) Assert.AreEqual(expectedField, problem.Field, corruption);
        var after = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(payload, after.Encode());
        Assert.AreEqual(original.Checkpoint, after.Checkpoint);
    }

    [TestMethod]
    public void Raw_durability_above_the_design_maximum_survives_restore_and_queries_clamp_it()
    {
        var factory = CoreFactory.Create();
        using var candidate = Prepare(InputWithInventory());
        using var session = candidate.Activate();
        var root = JsonNode.Parse(Success(session.Capture(CapturePurpose.Save)).Encode())!;
        var vehicle = root["world"]!["instances"]!.AsArray()
            .Single(i => i!["id"]!["$kind"]!.GetValue<string>() == "vehicle")!;
        vehicle["structure_durability"] = 123;
        vehicle["parts"]![0]!["current"] = 456;
        var decoded = Success(factory.DecodeState(root.ToJsonString()));
        using var restoredCandidate = Success(factory.PrepareRestore(decoded, Content, Options));
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

    private static WorldGenerationInput InputWithInventory() => Input with
    {
        Configuration = Input.Configuration with { WidthMeters = 100 },
        Start = Input.Start with
        {
            Trains = [Input.Start.Trains[0] with { Inventory = [new(new GoodId.Voxel(Material), 1)] }]
        }
    };

}

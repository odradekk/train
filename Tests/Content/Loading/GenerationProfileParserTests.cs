using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class GenerationProfileParserTests
{
    private const string Json = """
        {"schema_version":2,"id":"world","display":{"name":" 世界 ","description":"配置"},
        "configuration":{"width_meters":1000,"height_meters":2000,"minimum_node_spacing_meters":100,
        "candidate_probability":0.5,"elevation_noise":[{"scale_meters":200,"amplitude":1},{"scale_meters":100,"amplitude":0}],
        "moisture_noise":[],"river_noise":[],"lake_elevation_threshold":-2,"river_band_half_width":0,
        "terrain_bands":[{"terrain":"plains","minimum_elevation":-5,"maximum_elevation":10,"minimum_moisture":0,
        "maximum_moisture":1,"maximum_relief":0,"detour_multiplier":1,"maximum_tracks":3}],
        "terrain_sample_spacing_meters":10,"settlement_ratio":0.5,"lair_ratio":0,"hostile_settlement_ratio":0,
        "lair_weights":[],"resources":[{"good":"ore","allowed_terrain_types":["plains"],"probability":1,
        "minimum_reserve":0,"maximum_reserve":9223372036854775807,"renewable":false,"recovery_units_per_second":0,
        "regeneration_interval_seconds":100,"global_regeneration_remaining_reserve_ratio":0.25}],
        "initial_railway":{"maximum_tracks":3,"minimum_initial_tracks":0,"maximum_initial_tracks":2,"additional_cycle_edges":0},
        "budget":{"maximum_stage_retries":1,"maximum_world_retries":1,"maximum_candidates":9223372036854775807,"maximum_search_work":100},
        "npc_initialization":{"fill_inputs_and_living_targets":true,"output_reserve_hours":2,"minimum_for_sale_vehicles":0,
        "maximum_for_sale_vehicles":1,"for_sale_vehicle_candidates":["wagon","engine"]},
        "start_constraints":{"minimum_reachable_non_hostile_settlements":1,"minimum_reachable_empty_nodes":0,"require_station":true,
        "exclude_adjacent_lairs":false,"fully_loaded_purchase_rounds_per_train":2,"required_affordable_commission_design":"engine"},
        "rules":{"population":"people","market":"market","construction":"building","railway":"rail","combat":"battle"}},
        "start":{"initial_money":9223372036854775807,"trains":[{"vehicles":["wagon","engine","wagon"],
        "inventory":[{"voxel":"ore","quantity":9223372036854775807}]},{"vehicles":["engine"],"inventory":[]}]}}
        """;

    [TestMethod]
    public void Parse_preserves_resource_parameters_exact_integers_and_ordered_fleet()
    {
        var (parsed, package) = Parse(Json);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(0, package.Diagnostics.Count);
        Assert.AreEqual("世界", parsed.Display.Name);
        var resource = parsed.Configuration.Resources.Single();
        Assert.IsFalse(resource.Renewable);
        Assert.AreEqual(0d, resource.RecoveryUnitsPerSecond);
        Assert.AreEqual(100d, resource.RegenerationIntervalSeconds);
        Assert.AreEqual(0.25, resource.GlobalRegenerationRemainingReserveRatio);
        Assert.AreEqual(long.MaxValue, resource.MaximumReserve);
        Assert.AreEqual(long.MaxValue, parsed.Configuration.Budget.MaximumCandidates);
        Assert.AreEqual(long.MaxValue, parsed.Start.InitialMoney.Value);
        Assert.AreEqual(long.MaxValue, parsed.Start.Trains[0].Inventory.Single().Quantity);
        CollectionAssert.AreEqual(new[] { "core:wagon", "core:engine", "core:wagon" }, parsed.Start.Trains[0].Vehicles.Select(x => x.Value).ToArray());
        Assert.AreEqual("core:engine", parsed.Start.Trains[1].Vehicles.Single().Value);
        Assert.AreEqual("core:people", parsed.Configuration.Rules.Population.Value);
        Assert.AreEqual("core:rail", parsed.Configuration.Rules.Railway.Value);
        Assert.AreEqual("core:engine", parsed.Configuration.StartConstraints.RequiredAffordableCommissionDesign.Value);
        Assert.AreEqual(2d, parsed.Configuration.NpcInitialization.OutputReserveHours);
        Assert.IsTrue(parsed.Configuration.NpcInitialization.FillInputsAndLivingTargets);
    }

    [TestMethod]
    [DataRow("configuration/resources/0/global_regeneration_remaining_reserve_ratio", "-0.01")]
    [DataRow("configuration/resources/0/global_regeneration_remaining_reserve_ratio", "1.01")]
    [DataRow("configuration/resources/0/global_regeneration_remaining_reserve_ratio", "1e999")]
    [DataRow("configuration/resources/0/regeneration_interval_seconds", "0")]
    [DataRow("configuration/resources/0/recovery_units_per_second", "-1")]
    [DataRow("configuration/resources/0/minimum_reserve", "9223372036854775808")]
    [DataRow("configuration/resources/0/maximum_reserve", "-1")]
    [DataRow("configuration/candidate_probability", "2")]
    [DataRow("configuration/width_meters", "0")]
    [DataRow("configuration/budget/maximum_search_work", "0")]
    [DataRow("configuration/budget/maximum_stage_retries", "2147483648")]
    [DataRow("start/initial_money", "9223372036854775808")]
    [DataRow("start/trains/0/inventory/0/quantity", "1.5")]
    public void Parse_rejects_invalid_numbers_at_their_field(string path, string replacement)
    {
        var node = JsonNode.Parse(Json)!;
        Set(node, path, JsonNode.Parse(replacement));
        var (parsed, package) = Parse(node.ToJsonString());
        Assert.IsNull(parsed);
        Assert.AreEqual(C.PackageState.Invalid, package.State);
        Assert.AreEqual("/" + path, package.Diagnostics.Single().Field);
    }

    [TestMethod]
    [DataRow("configuration/resources/0", "global_regeneration_remaining_reserve_ratio")]
    [DataRow("configuration/resources/0", "renewable")]
    [DataRow("configuration", "npc_initialization")]
    [DataRow("configuration/npc_initialization", "fill_inputs_and_living_targets")]
    [DataRow("configuration/rules", "combat")]
    public void Parse_rejects_missing_required_inputs(string parent, string key)
    {
        var node = JsonNode.Parse(Json)!;
        Get(node, parent).AsObject().Remove(key);
        var (parsed, package) = Parse(node.ToJsonString());
        Assert.IsNull(parsed);
        Assert.AreEqual("/" + parent + "/" + key, package.Diagnostics.Single().Field);
    }

    [TestMethod]
    [DataRow("reserve")]
    [DataRow("terrain")]
    [DataRow("ratios")]
    [DataRow("tracks")]
    [DataRow("npc-range")]
    [DataRow("npc-empty")]
    [DataRow("lair-empty")]
    [DataRow("fleet-empty")]
    [DataRow("inventory-both")]
    [DataRow("inventory-neither")]
    public void Parse_rejects_cross_field_constraints(string mutation)
    {
        var node = JsonNode.Parse(Json)!;
        switch (mutation)
        {
            case "reserve": Set(node, "configuration/resources/0/minimum_reserve", JsonValue.Create(1)); Set(node, "configuration/resources/0/maximum_reserve", JsonValue.Create(0)); break;
            case "terrain": Set(node, "configuration/terrain_bands/0/minimum_elevation", JsonValue.Create(11)); break;
            case "ratios": Set(node, "configuration/lair_ratio", JsonValue.Create(0.6)); break;
            case "tracks": Set(node, "configuration/initial_railway/maximum_initial_tracks", JsonValue.Create(4)); break;
            case "npc-range": Set(node, "configuration/npc_initialization/minimum_for_sale_vehicles", JsonValue.Create(2)); break;
            case "npc-empty": Set(node, "configuration/npc_initialization/for_sale_vehicle_candidates", new JsonArray()); break;
            case "lair-empty": Set(node, "configuration/lair_ratio", JsonValue.Create(0.1)); break;
            case "fleet-empty": Set(node, "start/trains/0/vehicles", new JsonArray()); break;
            case "inventory-both": Get(node, "start/trains/0/inventory/0")["blueprint"] = "machine"; break;
            case "inventory-neither": Get(node, "start/trains/0/inventory/0").AsObject().Remove("voxel"); break;
        }
        var (parsed, package) = Parse(node.ToJsonString());
        Assert.IsNull(parsed);
        Assert.AreEqual(C.PackageState.Invalid, package.State);
    }

    [TestMethod]
    [DataRow("configuration/resources")]
    [DataRow("configuration/resources/0/allowed_terrain_types")]
    [DataRow("configuration/npc_initialization/for_sale_vehicle_candidates")]
    [DataRow("start/trains/0/inventory")]
    public void Parse_rejects_duplicate_unordered_entries(string path)
    {
        var node = JsonNode.Parse(Json)!;
        var array = Get(node, path).AsArray();
        array.Add(array[0]!.DeepClone());
        var (parsed, package) = Parse(node.ToJsonString());
        Assert.IsNull(parsed);
        Assert.AreEqual("/" + path, package.Diagnostics.Single().Field);
    }

    [TestMethod]
    public void Parse_retains_zero_ratio_lair_and_composite_inventory_for_loader_reference_checks()
    {
        var node = JsonNode.Parse(Json)!;
        Get(node, "configuration/lair_weights").AsArray().Add(JsonNode.Parse("{\"type\":\"nest\",\"weight\":3}"));
        Get(node, "start/trains/0/inventory").AsArray().Add(JsonNode.Parse("{\"blueprint\":\"machine\",\"quantity\":2}"));
        var (parsed, _) = Parse(node.ToJsonString());
        Assert.IsNotNull(parsed);
        Assert.AreEqual("core:nest", parsed.Configuration.LairWeights.Single().Type.Value);
        Assert.AreEqual(new K.GoodId.Composite(new("core:machine")), parsed.Start.Trains[0].Inventory[1].Good);
    }

    [TestMethod]
    public void Parse_preserves_both_resource_modes_and_rejects_duplicate_lair_types()
    {
        var node = JsonNode.Parse(Json)!;
        var resources = Get(node, "configuration/resources").AsArray();
        var renewable = resources[0]!.DeepClone();
        renewable["good"] = "wood";
        renewable["renewable"] = true;
        renewable["recovery_units_per_second"] = 4;
        resources.Add(renewable);
        var (parsed, _) = Parse(node.ToJsonString());
        Assert.IsNotNull(parsed);
        Assert.IsFalse(parsed.Configuration.Resources[0].Renewable);
        Assert.IsTrue(parsed.Configuration.Resources[1].Renewable);
        Assert.AreEqual(4d, parsed.Configuration.Resources[1].RecoveryUnitsPerSecond);
        Assert.AreEqual(100d, parsed.Configuration.Resources[1].RegenerationIntervalSeconds);
        var lairs = Get(node, "configuration/lair_weights").AsArray();
        lairs.Add(JsonNode.Parse("{\"type\":\"nest\",\"weight\":1}"));
        lairs.Add(JsonNode.Parse("{\"type\":\"core:nest\",\"weight\":2}"));
        var (rejected, package) = Parse(node.ToJsonString());
        Assert.IsNull(rejected);
        Assert.AreEqual("/configuration/lair_weights", package.Diagnostics.Single().Field);
    }

    [TestMethod]
    public void Parse_rejects_undeclared_dependencies_without_resolving_definition_existence()
    {
        var node = JsonNode.Parse(Json)!;
        Set(node, "configuration/rules/railway", JsonValue.Create("11111111-1111-4111-8111-111111111111:rail"));
        var (parsed, package) = Parse(node.ToJsonString());
        Assert.IsNull(parsed);
        Assert.AreEqual("reference_missing", package.Diagnostics.Single().Code);
        Assert.AreEqual("/configuration/rules/railway", package.Diagnostics.Single().Field);
    }

    [TestMethod]
    public void Parse_canonicalizes_unordered_inputs_but_keeps_duplicate_terrain_bands()
    {
        var node = JsonNode.Parse(Json)!;
        var bands = Get(node, "configuration/terrain_bands").AsArray();
        bands.Add(bands[0]!.DeepClone());
        var (parsed, _) = Parse(node.ToJsonString());
        Assert.IsNotNull(parsed);
        Assert.AreEqual(2, parsed.Configuration.TerrainBands.Length);
        CollectionAssert.AreEqual(new[] { 100d, 200d }, parsed.Configuration.ElevationNoise.Select(x => x.ScaleMeters).ToArray());
        CollectionAssert.AreEqual(new[] { "core:engine", "core:wagon" }, parsed.Configuration.NpcInitialization.ForSaleVehicleCandidates.Select(x => x.Value).ToArray());
    }

    internal static JsonObject Fixture() => JsonNode.Parse(Json)!.AsObject();

    private static (ParsedGenerationProfile? Profile, LoadPackage Package) Parse(string json)
    {
        var package = new LoadPackage(new("builtin"), "", true) { Metadata = new(new("core"), "Core", "0.0.56", [], []) };
        return (new GenerationProfileParser().Parse(Encoding.UTF8.GetBytes(json), package, "resources/generation-profiles/world.generation-profile.json"), package);
    }

    private static JsonNode Get(JsonNode node, string path)
    {
        foreach (var key in path.Split('/')) node = node is JsonArray array ? array[int.Parse(key)]! : node[key]!;
        return node;
    }

    private static void Set(JsonNode node, string path, JsonNode? value)
    {
        var slash = path.LastIndexOf('/');
        Get(node, path[..slash])[path[(slash + 1)..]] = value;
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;
using static Train.Content.Loading.DefinitionJson;

namespace Train.Content.Loading;

internal sealed record ParsedGenerationProfile(C.GenerationProfileId Id, C.DisplayText Display,
    ParsedGenerationConfiguration Configuration, ParsedNewGameStart Start, string Path);

internal sealed record ParsedGenerationConfiguration(double WidthMeters, double HeightMeters,
    double MinimumNodeSpacingMeters, double CandidateProbability,
    ImmutableArray<K.NoiseOctave> ElevationNoise, ImmutableArray<K.NoiseOctave> MoistureNoise,
    ImmutableArray<K.NoiseOctave> RiverNoise, double LakeElevationThreshold,
    double RiverBandHalfWidth, ImmutableArray<K.TerrainGenerationBand> TerrainBands,
    double TerrainSampleSpacingMeters, double SettlementRatio, double LairRatio,
    double HostileSettlementRatio, ImmutableArray<K.LairGenerationWeight> LairWeights,
    ImmutableArray<K.ResourceGenerationEntry> Resources, K.InitialRailwayConfiguration InitialRailway,
    K.GenerationBudget Budget, ParsedNpcInitialization NpcInitialization,
    ParsedStartConstraints StartConstraints, K.RuleSelection Rules);
internal sealed record ParsedNpcInitialization(bool FillInputsAndLivingTargets,
    double OutputReserveHours, int MinimumForSaleVehicles, int MaximumForSaleVehicles,
    ImmutableArray<C.BlueprintAddress> ForSaleVehicleCandidates);
internal sealed record ParsedStartConstraints(int MinimumReachableNonHostileSettlements,
    int MinimumReachableEmptyNodes, bool RequireStation, bool ExcludeAdjacentLairs,
    int FullyLoadedPurchaseRoundsPerTrain, C.BlueprintAddress RequiredAffordableCommissionDesign);
internal sealed record ParsedStartingTrain(ImmutableArray<C.BlueprintAddress> Vehicles,
    ImmutableArray<ParsedInventoryQuantity> Inventory);
internal sealed record ParsedNewGameStart(K.Money InitialMoney, ImmutableArray<ParsedStartingTrain> Trains);
internal sealed record ParsedInventoryQuantity(ParsedInventoryReference Reference, long Quantity);
internal abstract record ParsedInventoryReference
{
    private ParsedInventoryReference() { }
    internal sealed record Voxel(K.VoxelDefinitionId Id) : ParsedInventoryReference;
    internal sealed record Blueprint(C.BlueprintAddress Address) : ParsedInventoryReference;
}

internal static class GenerationProfileParser
{
    internal static ParsedGenerationProfile? Parse(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "display", "configuration", "start");
            var json = new DefinitionJson(root, package, path, "generation-profile", 2);
            var id = new C.GenerationProfileId(json.Id);
            subject = new C.ContentReference.Generation(id);
            return new(id, Display(Required(root, "display", "")),
                Configuration(Required(root, "configuration", ""), json),
                Start(Required(root, "start", ""), json), path);
        }
        catch (DefinitionFormatException exception)
        {
            package.Reject(exception.Code, path, exception.Field, exception.Message, subject);
            return null;
        }
    }

    private static ParsedGenerationConfiguration Configuration(JsonElement value, DefinitionJson json)
    {
        const string field = "/configuration";
        Object(value, field, "width_meters", "height_meters", "minimum_node_spacing_meters", "candidate_probability",
            "elevation_noise", "moisture_noise", "river_noise", "lake_elevation_threshold", "river_band_half_width",
            "terrain_bands", "terrain_sample_spacing_meters", "settlement_ratio", "lair_ratio", "hostile_settlement_ratio",
            "lair_weights", "resources", "initial_railway", "budget", "npc_initialization", "start_constraints", "rules");
        JsonElement Read(string key) => Required(value, key, field);
        double Positive(string key) => Number(Read(key), field + "/" + key, positive: true);
        double Ratio(string key) => Number(Read(key), field + "/" + key, 0, 1);
        var settlementRatio = Ratio("settlement_ratio");
        var lairRatio = Ratio("lair_ratio");
        if (settlementRatio + lairRatio > 1) Fail(field + "/lair_ratio", "聚居地和生物聚落比例之和不能超过 1。");
        var bands = Items(Read("terrain_bands"), field + "/terrain_bands").Select(item => Band(item.Value, item.Field, json))
            .OrderBy(x => x.MinimumElevation).ThenBy(x => x.MaximumElevation).ThenBy(x => x.MinimumMoisture)
            .ThenBy(x => x.MaximumMoisture).ThenBy(x => x.MaximumRelief).ThenBy(x => x.DetourMultiplier)
            .ThenBy(x => x.MaximumTracks).ThenBy(x => x.Terrain.Value, StringComparer.Ordinal).ToImmutableArray();
        if (bands.IsEmpty) Fail(field + "/terrain_bands", "地貌带至少需要一项。");
        var lairs = Items(Read("lair_weights"), field + "/lair_weights").Select(item =>
        {
            Object(item.Value, item.Field, "type", "weight");
            return new K.LairGenerationWeight(new(json.Reference(Required(item.Value, "type", item.Field), item.Field + "/type")),
                Int32(Required(item.Value, "weight", item.Field), item.Field + "/weight", 1));
        }).OrderBy(x => x.Type.Value, StringComparer.Ordinal).ToImmutableArray();
        Unique(lairs.Select(x => x.Type), field + "/lair_weights");
        if (lairRatio > 0 && lairs.IsEmpty) Fail(field + "/lair_weights", "生成生物聚落时必须提供类型权重。");
        var resources = Items(Read("resources"), field + "/resources").Select(item => Resource(item.Value, item.Field, json))
            .OrderBy(x => x.Good.Value, StringComparer.Ordinal).ToImmutableArray();
        Unique(resources.Select(x => x.Good), field + "/resources");
        return new(Positive("width_meters"), Positive("height_meters"), Positive("minimum_node_spacing_meters"),
            Ratio("candidate_probability"), NoiseParser.Parse(Read("elevation_noise"), field + "/elevation_noise"),
            NoiseParser.Parse(Read("moisture_noise"), field + "/moisture_noise"), NoiseParser.Parse(Read("river_noise"), field + "/river_noise"),
            Number(Read("lake_elevation_threshold"), field + "/lake_elevation_threshold"),
            Number(Read("river_band_half_width"), field + "/river_band_half_width", 0), bands,
            Positive("terrain_sample_spacing_meters"), settlementRatio, lairRatio, Ratio("hostile_settlement_ratio"), lairs,
            resources, Railway(Read("initial_railway")), Budget(Read("budget")), Npc(Read("npc_initialization"), json),
            Constraints(Read("start_constraints"), json), Rules(Read("rules"), json));
    }

    private static K.TerrainGenerationBand Band(JsonElement value, string field, DefinitionJson json)
    {
        Object(value, field, "terrain", "minimum_elevation", "maximum_elevation", "minimum_moisture", "maximum_moisture",
            "maximum_relief", "detour_multiplier", "maximum_tracks");
        double Read(string key, double minimum = double.MinValue) => Number(Required(value, key, field), field + "/" + key, minimum);
        var minimumElevation = Read("minimum_elevation");
        var maximumElevation = Read("maximum_elevation");
        var minimumMoisture = Read("minimum_moisture");
        var maximumMoisture = Read("maximum_moisture");
        if (minimumElevation > maximumElevation) Fail(field + "/maximum_elevation", "海拔范围上下界倒置。");
        if (minimumMoisture > maximumMoisture) Fail(field + "/maximum_moisture", "湿度范围上下界倒置。");
        return new(new(json.Reference(Required(value, "terrain", field), field + "/terrain")), minimumElevation, maximumElevation,
            minimumMoisture, maximumMoisture, Read("maximum_relief", 0), Read("detour_multiplier", 1),
            Int32(Required(value, "maximum_tracks", field), field + "/maximum_tracks", 1));
    }

    private static K.ResourceGenerationEntry Resource(JsonElement value, string field, DefinitionJson json)
    {
        Object(value, field, "good", "allowed_terrain_types", "probability", "minimum_reserve", "maximum_reserve", "renewable",
            "recovery_units_per_second", "regeneration_interval_seconds", "global_regeneration_remaining_reserve_ratio");
        JsonElement Read(string key) => Required(value, key, field);
        var terrains = Items(Read("allowed_terrain_types"), field + "/allowed_terrain_types")
            .Select(item => new K.TerrainTypeId(json.Reference(item.Value, item.Field)))
            .OrderBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        Unique(terrains, field + "/allowed_terrain_types");
        var minimum = Int64(Read("minimum_reserve"), field + "/minimum_reserve", 0);
        var maximum = Int64(Read("maximum_reserve"), field + "/maximum_reserve", 0);
        if (minimum > maximum) Fail(field + "/maximum_reserve", "储量范围上下界倒置。");
        return new(new(json.Reference(Read("good"), field + "/good")), terrains,
            Number(Read("probability"), field + "/probability", 0, 1), minimum, maximum,
            Boolean(Read("renewable"), field + "/renewable"), Number(Read("recovery_units_per_second"), field + "/recovery_units_per_second", 0),
            Number(Read("regeneration_interval_seconds"), field + "/regeneration_interval_seconds", positive: true),
            Number(Read("global_regeneration_remaining_reserve_ratio"), field + "/global_regeneration_remaining_reserve_ratio", 0, 1));
    }

    private static K.InitialRailwayConfiguration Railway(JsonElement value)
    {
        const string field = "/configuration/initial_railway";
        Object(value, field, "maximum_tracks", "minimum_initial_tracks", "maximum_initial_tracks", "additional_cycle_edges");
        int Read(string key, int minimum = 0) => Int32(Required(value, key, field), field + "/" + key, minimum);
        var maximumTracks = Read("maximum_tracks", 1);
        var minimum = Int32(Required(value, "minimum_initial_tracks", field), field + "/minimum_initial_tracks", 0, 2);
        var maximum = Int32(Required(value, "maximum_initial_tracks", field), field + "/maximum_initial_tracks", 0, 2);
        if (minimum > maximum || maximum > maximumTracks) Fail(field + "/maximum_initial_tracks", "初始轨数范围必须有序且不超过全局上限。");
        return new(maximumTracks, minimum, maximum, Read("additional_cycle_edges"));
    }

    private static K.GenerationBudget Budget(JsonElement value)
    {
        const string field = "/configuration/budget";
        Object(value, field, "maximum_stage_retries", "maximum_world_retries", "maximum_candidates", "maximum_search_work");
        return new(Int32(Required(value, "maximum_stage_retries", field), field + "/maximum_stage_retries", 1),
            Int32(Required(value, "maximum_world_retries", field), field + "/maximum_world_retries", 1),
            Int64(Required(value, "maximum_candidates", field), field + "/maximum_candidates", 1),
            Int64(Required(value, "maximum_search_work", field), field + "/maximum_search_work", 1));
    }

    private static ParsedNpcInitialization Npc(JsonElement value, DefinitionJson json)
    {
        const string field = "/configuration/npc_initialization";
        Object(value, field, "fill_inputs_and_living_targets", "output_reserve_hours", "minimum_for_sale_vehicles", "maximum_for_sale_vehicles", "for_sale_vehicle_candidates");
        JsonElement Read(string key) => Required(value, key, field);
        var minimum = Int32(Read("minimum_for_sale_vehicles"), field + "/minimum_for_sale_vehicles", 0);
        var maximum = Int32(Read("maximum_for_sale_vehicles"), field + "/maximum_for_sale_vehicles", 0);
        if (minimum > maximum) Fail(field + "/maximum_for_sale_vehicles", "现车数量范围上下界倒置。");
        var candidates = Items(Read("for_sale_vehicle_candidates"), field + "/for_sale_vehicle_candidates")
            .Select(item => Blueprint(json, item.Value, item.Field))
            .OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        Unique(candidates, field + "/for_sale_vehicle_candidates");
        if (maximum > 0 && candidates.IsEmpty) Fail(field + "/for_sale_vehicle_candidates", "需要现车时必须提供候选蓝图。");
        return new(Boolean(Read("fill_inputs_and_living_targets"), field + "/fill_inputs_and_living_targets"),
            Number(Read("output_reserve_hours"), field + "/output_reserve_hours", 0), minimum, maximum, candidates);
    }

    private static ParsedStartConstraints Constraints(JsonElement value, DefinitionJson json)
    {
        const string field = "/configuration/start_constraints";
        Object(value, field, "minimum_reachable_non_hostile_settlements", "minimum_reachable_empty_nodes", "require_station",
            "exclude_adjacent_lairs", "fully_loaded_purchase_rounds_per_train", "required_affordable_commission_design");
        JsonElement Read(string key) => Required(value, key, field);
        return new(Int32(Read("minimum_reachable_non_hostile_settlements"), field + "/minimum_reachable_non_hostile_settlements", 0),
            Int32(Read("minimum_reachable_empty_nodes"), field + "/minimum_reachable_empty_nodes", 0),
            Boolean(Read("require_station"), field + "/require_station"), Boolean(Read("exclude_adjacent_lairs"), field + "/exclude_adjacent_lairs"),
            Int32(Read("fully_loaded_purchase_rounds_per_train"), field + "/fully_loaded_purchase_rounds_per_train", 1),
            Blueprint(json, Read("required_affordable_commission_design"), field + "/required_affordable_commission_design"));
    }

    private static K.RuleSelection Rules(JsonElement value, DefinitionJson json)
    {
        const string field = "/configuration/rules";
        Object(value, field, "population", "market", "construction", "railway", "combat");
        K.RuleDefinitionId Read(string key) => new(json.Reference(Required(value, key, field), field + "/" + key));
        return new(Read("population"), Read("market"), Read("construction"), Read("railway"), Read("combat"));
    }

    private static ParsedNewGameStart Start(JsonElement value, DefinitionJson json)
    {
        const string field = "/start";
        Object(value, field, "initial_money", "trains");
        var trains = Items(Required(value, "trains", field), field + "/trains").Select(item =>
        {
            Object(item.Value, item.Field, "vehicles", "inventory");
            var vehicles = Items(Required(item.Value, "vehicles", item.Field), item.Field + "/vehicles")
                .Select(vehicle => Blueprint(json, vehicle.Value, vehicle.Field)).ToImmutableArray();
            if (vehicles.IsEmpty) Fail(item.Field + "/vehicles", "列车至少需要一辆车辆。");
            var inventory = Items(Required(item.Value, "inventory", item.Field), item.Field + "/inventory").Select(entry =>
            {
                Object(entry.Value, entry.Field, "voxel", "blueprint", "quantity");
                var voxel = entry.Value.TryGetProperty("voxel", out var reference);
                var blueprint = entry.Value.TryGetProperty("blueprint", out var blueprintReference);
                if (voxel == blueprint) Fail(entry.Field, "库存项必须且只能指定 voxel 或 blueprint。");
                var id = json.Reference(voxel ? reference : blueprintReference, entry.Field + (voxel ? "/voxel" : "/blueprint"));
                ParsedInventoryReference parsedReference = voxel ? new ParsedInventoryReference.Voxel(new(id))
                    : new ParsedInventoryReference.Blueprint(new(C.BlueprintLibrary.Packages, new(id)));
                return new ParsedInventoryQuantity(parsedReference, Int64(Required(entry.Value, "quantity", entry.Field), entry.Field + "/quantity", 1));
            }).OrderBy(x => x.Reference is ParsedInventoryReference.Voxel ? 0 : 1)
                .ThenBy(x => x.Reference is ParsedInventoryReference.Voxel v ? v.Id.Value : ((ParsedInventoryReference.Blueprint)x.Reference).Address.Id.Value,
                    StringComparer.Ordinal).ToImmutableArray();
            Unique(inventory.Select(x => x.Reference), item.Field + "/inventory");
            return new ParsedStartingTrain(vehicles, inventory);
        }).ToImmutableArray();
        return new(new(Int64(Required(value, "initial_money", field), field + "/initial_money", 0)), trains);
    }

    private static C.BlueprintAddress Blueprint(DefinitionJson json, JsonElement value, string field) =>
        new(C.BlueprintLibrary.Packages, new(json.Reference(value, field)));
}

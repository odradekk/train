using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal static class GenerationSemantics
{
    internal static object Configuration(K.ResolvedGenerationConfiguration value, Func<K.DesignHash, string> compositionIdentity) => new
    {
        width_meters = value.WidthMeters,
        height_meters = value.HeightMeters,
        minimum_node_spacing_meters = value.MinimumNodeSpacingMeters,
        candidate_probability = value.CandidateProbability,
        elevation_noise = Noise(value.ElevationNoise),
        moisture_noise = Noise(value.MoistureNoise),
        river_noise = Noise(value.RiverNoise),
        lake_elevation_threshold = value.LakeElevationThreshold,
        river_band_half_width = value.RiverBandHalfWidth,
        terrain_bands = value.TerrainBands.Distinct().OrderBy(x => x.MinimumElevation).ThenBy(x => x.MaximumElevation)
            .ThenBy(x => x.MinimumMoisture).ThenBy(x => x.MaximumMoisture).ThenBy(x => x.MaximumRelief)
            .ThenBy(x => x.DetourMultiplier).ThenBy(x => x.MaximumTracks).ThenBy(x => x.Terrain.Value, StringComparer.Ordinal)
            .Select(x => new
            {
                terrain = x.Terrain.Value,
                minimum_elevation = x.MinimumElevation,
                maximum_elevation = x.MaximumElevation,
                minimum_moisture = x.MinimumMoisture,
                maximum_moisture = x.MaximumMoisture,
                maximum_relief = x.MaximumRelief,
                detour_multiplier = x.DetourMultiplier,
                maximum_tracks = x.MaximumTracks
            }),
        terrain_sample_spacing_meters = value.TerrainSampleSpacingMeters,
        settlement_ratio = value.SettlementRatio,
        lair_ratio = value.LairRatio,
        hostile_settlement_ratio = value.HostileSettlementRatio,
        lair_weights = value.LairWeights.OrderBy(x => x.Type.Value, StringComparer.Ordinal)
            .Select(x => new { type = x.Type.Value, weight = x.Weight }),
        resources = value.Resources.OrderBy(x => x.Good.Value, StringComparer.Ordinal).Select(x => new
        {
            good = x.Good.Value,
            allowed_terrain_types = x.AllowedTerrainTypes.Select(t => t.Value).Order(StringComparer.Ordinal),
            probability = x.Probability,
            minimum_reserve = x.MinimumReserve,
            maximum_reserve = x.MaximumReserve,
            renewable = x.Renewable,
            recovery_units_per_second = x.RecoveryUnitsPerSecond,
            regeneration_interval_seconds = x.RegenerationIntervalSeconds,
            global_regeneration_remaining_reserve_ratio = x.GlobalRegenerationRemainingReserveRatio
        }),
        initial_railway = new
        {
            maximum_tracks = value.InitialRailway.MaximumTracks,
            minimum_initial_tracks = value.InitialRailway.MinimumInitialTracks,
            maximum_initial_tracks = value.InitialRailway.MaximumInitialTracks,
            additional_cycle_edges = value.InitialRailway.AdditionalCycleEdges
        },
        budget = new
        {
            maximum_stage_retries = value.Budget.MaximumStageRetries,
            maximum_world_retries = value.Budget.MaximumWorldRetries,
            maximum_candidates = value.Budget.MaximumCandidates,
            maximum_search_work = value.Budget.MaximumSearchWork
        },
        npc_initialization = new
        {
            fill_inputs_and_living_targets = value.NpcInitialization.FillInputsAndLivingTargets,
            output_reserve_hours = value.NpcInitialization.OutputReserveHours,
            minimum_for_sale_vehicles = value.NpcInitialization.MinimumForSaleVehicles,
            maximum_for_sale_vehicles = value.NpcInitialization.MaximumForSaleVehicles,
            for_sale_vehicle_candidates = value.NpcInitialization.ForSaleVehicleCandidates.Select(compositionIdentity).Order(StringComparer.Ordinal)
        },
        start_constraints = new
        {
            minimum_reachable_non_hostile_settlements = value.StartConstraints.MinimumReachableNonHostileSettlements,
            minimum_reachable_empty_nodes = value.StartConstraints.MinimumReachableEmptyNodes,
            require_station = value.StartConstraints.RequireStation,
            exclude_adjacent_lairs = value.StartConstraints.ExcludeAdjacentLairs,
            fully_loaded_purchase_rounds_per_train = value.StartConstraints.FullyLoadedPurchaseRoundsPerTrain,
            required_affordable_commission_design = compositionIdentity(value.StartConstraints.RequiredAffordableCommissionDesign)
        },
        rules = new
        {
            population = value.Rules.Population.Value,
            market = value.Rules.Market.Value,
            construction = value.Rules.Construction.Value,
            railway = value.Rules.Railway.Value,
            combat = value.Rules.Combat.Value
        }
    };

    internal static object Start(K.NewGameStartInput value, Func<K.DesignHash, string> compositionIdentity) => new
    {
        initial_money = value.InitialMoney.Value,
        trains = value.Trains.Select(train => new
        {
            vehicles = train.Vehicles.Select(compositionIdentity),
            inventory = train.Inventory.OrderBy(x => GoodKey(x.Good), StringComparer.Ordinal)
                .Select(x => new { good = Good(x.Good), quantity = x.Quantity })
        })
    };

    private static IEnumerable<object> Noise(IEnumerable<K.NoiseOctave> values) => values.OrderBy(x => x.ScaleMeters)
        .ThenBy(x => x.Amplitude).Select(x => (object)new { scale_meters = x.ScaleMeters, amplitude = x.Amplitude });

    private static object Good(K.GoodId value) => value switch
    {
        K.GoodId.Voxel v => new { kind = "voxel", id = v.Definition.Value },
        K.GoodId.Composite c => new { kind = "composite", id = c.Definition.Value },
        K.GoodId.Assembly a => new { kind = "assembly", id = a.Composition.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string GoodKey(K.GoodId value) => value switch
    {
        K.GoodId.Voxel v => "voxel:" + v.Definition.Value,
        K.GoodId.Composite c => "composite:" + c.Definition.Value,
        K.GoodId.Assembly a => "assembly:" + a.Composition.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

using System.Collections.Immutable;
using Train.Core.Contracts;
using static Train.Core.World.WorldReferences;

namespace Train.Core.World;

internal static class WorldInputs
{
    internal static void Validate(WorldGenerationInput input, CoreContentProjection content, CoreSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Runtime);
        foreach (var value in new[] { options.Runtime.GameBuild, options.Runtime.SimulationVersion, options.Runtime.OperatingSystem,
            options.Runtime.Architecture, options.Runtime.DotNetRuntime }) ArgumentNullException.ThrowIfNull(value);
        Hash(input.GenerationContentRevision.Value);
        Hash(content.Identity.Value);
        ArgumentNullException.ThrowIfNull(input.GeneratorVersion);
        var c = input.Configuration;
        ArgumentNullException.ThrowIfNull(c);
        Positive(c.WidthMeters); Positive(c.HeightMeters); Positive(c.MinimumNodeSpacingMeters);
        Ratio(c.CandidateProbability);
        foreach (var octave in Items(c.ElevationNoise).Concat(Items(c.MoistureNoise)).Concat(Items(c.RiverNoise)))
        { Positive(octave.ScaleMeters); Nonnegative(octave.Amplitude); }
        Finite(c.LakeElevationThreshold); Nonnegative(c.RiverBandHalfWidth);
        foreach (var band in Items(c.TerrainBands))
        {
            Identity(band.Terrain.Value);
            Finite(band.MinimumElevation); Finite(band.MaximumElevation);
            Finite(band.MinimumMoisture); Finite(band.MaximumMoisture);
            Require(band.MinimumElevation <= band.MaximumElevation && band.MinimumMoisture <= band.MaximumMoisture, "TerrainBands");
            Nonnegative(band.MaximumRelief); Finite(band.DetourMultiplier);
            Require(band.DetourMultiplier >= 1 && band.MaximumTracks > 0, "TerrainBands");
        }
        Require(!c.TerrainBands.IsEmpty, "TerrainBands");
        Positive(c.TerrainSampleSpacingMeters);
        Ratio(c.SettlementRatio); Ratio(c.LairRatio); Ratio(c.HostileSettlementRatio);
        Require(c.SettlementRatio + c.LairRatio <= 1, "SettlementRatio");
        Unique(Items(c.LairWeights), x => x.Type.Value);
        foreach (var lair in c.LairWeights) { Identity(lair.Type.Value); Require(lair.Weight > 0, "LairWeights"); }
        Unique(Items(c.Resources), x => x.Good.Value);
        foreach (var resource in c.Resources)
        {
            Identity(resource.Good.Value);
            Identities(resource.AllowedTerrainTypes);
            Ratio(resource.Probability);
            Require(resource.MinimumReserve >= 0 && resource.MaximumReserve >= resource.MinimumReserve, "Resources");
            Nonnegative(resource.RecoveryUnitsPerSecond); Positive(resource.RegenerationIntervalSeconds);
            Ratio(resource.GlobalRegenerationRemainingReserveRatio);
        }
        var railway = c.InitialRailway;
        ArgumentNullException.ThrowIfNull(railway);
        Require(railway.MaximumTracks > 0 && railway.MinimumInitialTracks >= 0 && railway.MaximumInitialTracks >= railway.MinimumInitialTracks &&
            railway.MaximumInitialTracks <= railway.MaximumTracks && railway.AdditionalCycleEdges >= 0, "InitialRailway");
        ArgumentNullException.ThrowIfNull(c.Budget);
        Require(c.Budget.MaximumStageRetries > 0 && c.Budget.MaximumWorldRetries > 0 && c.Budget.MaximumCandidates > 0 && c.Budget.MaximumSearchWork > 0, "Budget");
        var npc = c.NpcInitialization;
        ArgumentNullException.ThrowIfNull(npc);
        Nonnegative(npc.OutputReserveHours);
        Require(npc.MinimumForSaleVehicles >= 0 && npc.MaximumForSaleVehicles >= npc.MinimumForSaleVehicles, "NpcInitialization");
        Unique(Items(npc.ForSaleVehicleCandidates), x => x.Value);
        foreach (var design in npc.ForSaleVehicleCandidates) Hash(design.Value);
        var start = c.StartConstraints;
        ArgumentNullException.ThrowIfNull(start);
        Require(start.MinimumReachableNonHostileSettlements >= 0 && start.MinimumReachableEmptyNodes >= 0 && start.FullyLoadedPurchaseRoundsPerTrain > 0, "StartConstraints");
        Hash(start.RequiredAffordableCommissionDesign.Value);
        ArgumentNullException.ThrowIfNull(c.Rules);
        foreach (var rule in new[] { c.Rules.Population, c.Rules.Market, c.Rules.Construction, c.Rules.Railway, c.Rules.Combat }) Identity(rule.Value);
        ArgumentNullException.ThrowIfNull(input.Start);
        Require(input.Start.InitialMoney.Value >= 0, "InitialMoney");
        foreach (var train in Items(input.Start.Trains))
        {
            Require(!Items(train.Vehicles).IsEmpty, "Vehicles");
            foreach (var design in train.Vehicles) Hash(design.Value);
            Quantities(train.Inventory);
        }
        ValidateContent(content);
    }

    private static void ValidateContent(CoreContentProjection content)
    {
        Unique(Items(content.Goods), x => GoodKey(x.Good));
        Unique(Items(content.DesignGoods), x => GoodKey(x.Good));
        foreach (var good in content.Goods.Concat(content.DesignGoods))
        {
            Good(good.Good); Hash(good.Revision.Value);
            Require(good.BaseValue.Value >= 0, "BaseValue"); Nonnegative(good.MassTons);
            if (good.FuelEnergyPerUnit is { } energy) Positive(energy);
        }
        var goods = content.Goods.ToDictionary(x => GoodKey(x.Good), StringComparer.Ordinal);
        foreach (var good in content.DesignGoods)
            Require(!goods.TryGetValue(GoodKey(good.Good), out var other) || other == good, "DesignGoods");
        Unique(Items(content.Designs), x => x.Design.Value);
        foreach (var design in content.Designs)
        {
            Hash(design.Design.Value); Hash(design.Composition.Value);
            Require(design.MaxStructureDurability >= 0 && design.StructureHitWeight >= 0 && design.StructuralBaseValue.Value >= 0 && design.BillOfMaterialsItemCount >= 0, "Designs");
            Nonnegative(design.MassTons);
            switch (design.Kind)
            {
                case DesignKind.Vehicle vehicle: EnumValue(vehicle.Size); Require(vehicle.LengthCells > 0, "LengthCells"); break;
                case DesignKind.Building building:
                    EnumValue(building.PlotSize); EnumValue(building.Type);
                    Unique(Items(building.FacilityTags), x => x);
                    foreach (var tag in building.FacilityTags) EnumValue(tag);
                    break;
                case DesignKind.LargeEquipment equipment: Good(equipment.ManufacturedGood); break;
                default: throw new ArgumentException("Invalid design kind.");
            }
            Unique(Items(design.Parts), x => x.Id);
            foreach (var part in design.Parts)
            {
                Require(part.Id.Value >= 0 && part.MaxDurability >= 0 && part.HitWeight >= 0 && part.BaseValue.Value >= 0, "Parts");
                Reference(part.Definition); Good(part.RepairGood);
                foreach (var capability in Items(part.Capabilities)) Capability(capability);
            }
            Quantities(design.StructuralGoods); Quantities(design.BillOfMaterials);
            foreach (var unit in Items(design.ReturnUnits))
            {
                Good(unit.Good); Require(unit.Quantity > 0, "ReturnUnits");
                Unique(Items(unit.Parts), x => x);
                foreach (var part in unit.Parts) Require(part.Value >= 0, "ReturnUnits.Parts");
                Quantities(unit.StructuralGoods); Missing(unit.MissingReferences);
            }
            Missing(design.MissingReferences);
        }
        Unique(Items(content.Processes), x => ProcessKey(x.Details));
        foreach (var process in content.Processes)
        {
            Hash(process.Revision.Value); Require(process.WorkRequired > 0, "WorkRequired");
            Nonnegative(process.PowerPerWork); Nonnegative(process.LaborPerWork); EnumValue(process.RequiredFacilityTag);
            Identities(process.AllowedTerrainTypes);
            switch (process.Details)
            {
                case ProcessDetails.Production p: Identity(p.Id.Value); Quantities(p.Inputs); Quantities(p.Outputs); break;
                case ProcessDetails.Extraction p: Identity(p.Id.Value); Quantity(p.Extracts); break;
                case ProcessDetails.Recovery p: Identity(p.Id.Value); Quantities(p.Inputs); Quantities(p.Outputs); break;
                default: throw new ArgumentException("Invalid process details.");
            }
        }
        Unique(Items(content.PopulationRules), x => x.Id.Value);
        foreach (var rule in content.PopulationRules)
        {
            Identity(rule.Id.Value); Hash(rule.Revision.Value); Ratio(rule.LaborRatio);
            Require(rule.ConsumptionIntervalTicks > 0 && rule.StatisticsPeriodTicks > 0, "PopulationRules");
            Nonnegative(rule.BaseGrowthPerSecond); Nonnegative(rule.ImprovementGrowthPerSecond); Nonnegative(rule.MaximumLossPerSecond);
            Unique(Items(rule.Needs), x => x.Need);
            foreach (var need in rule.Needs) { EnumValue(need.Need); EnumValue(need.Class); Positive(need.AmountPerPersonSecond); Positive(need.Weight); }
            Unique(Items(rule.ConsumptionGoods), x => GoodKey(x.Good));
            foreach (var good in rule.ConsumptionGoods)
            {
                Good(good.Good); Require(good.SelectionOrder >= 0, "SelectionOrder"); Unique(Items(good.Satisfies), x => x.Need);
                foreach (var conversion in good.Satisfies) { EnumValue(conversion.Need); Positive(conversion.AmountPerGoodUnit); }
            }
        }
        Unique(Items(content.MarketRules), x => x.Id.Value);
        foreach (var rule in content.MarketRules)
        {
            Identity(rule.Id.Value); Hash(rule.Revision.Value);
            Nonnegative(rule.BasicReserveHours); Nonnegative(rule.ProductionReserveHours); Nonnegative(rule.ImprovementReserveHours);
            Prices(rule.DefaultGoodsPrices);
            Unique(Items(rule.GoodPrices), x => GoodKey(x.Good));
            foreach (var price in rule.GoodPrices) { Good(price.Good); Prices(price.Parameters); }
            Unique(Items(rule.VehiclePurchaseLimits), x => x.Size);
            foreach (var limit in rule.VehiclePurchaseLimits) { EnumValue(limit.Size); Require(limit.MaximumForSale >= 0, "MaximumForSale"); }
            Require(rule.VehicleBuyMultiplier.Value > 0 && rule.VehicleSellMultiplier.Value > 0 && rule.AutomaticEvaluationIntervalTicks > 0 &&
                rule.RecentReportLimit > 0 && rule.CommissionMoneyPerWork.Value >= 0 && rule.RentMoneyPerCapacityHour.Value >= 0 &&
                rule.RentableCapacityRatio.Value is >= 0 and <= 10000 && rule.LeaseDurationTicks > 0, "MarketRules");
        }
        Unique(Items(content.ConstructionRules), x => x.Id.Value);
        foreach (var rule in content.ConstructionRules)
        {
            Identity(rule.Id.Value); Hash(rule.Revision.Value);
            Require(rule.FoundingDurationTicks > 0 && rule.ConstructionWorkPerItem > 0 && rule.RefitWorkPerChangedItem > 0 &&
                rule.DeconstructionWorkPerItem > 0 && rule.RepairWorkPerDamagedPart > 0, "ConstructionRules");
            Nonnegative(rule.PowerPerWork); Nonnegative(rule.LaborPerWork);
        }
        Unique(Items(content.RailwayRules), x => x.Id.Value);
        foreach (var rule in content.RailwayRules)
        {
            Identity(rule.Id.Value); Hash(rule.Revision.Value); Identity(rule.RailGood.Value);
            Positive(rule.BrakingDeceleration); Positive(rule.MinimumAcceleration); Positive(rule.MinimumGapMeters);
            Positive(rule.RailUnitsPerMeter); Positive(rule.BuildSecondsPerMeter); Positive(rule.RepairSecondsPerBreak);
            Require(rule.RepairRailUnitsPerBreak > 0, "RepairRailUnitsPerBreak");
        }
        Unique(Items(content.CombatRules), x => x.Id.Value);
        foreach (var rule in content.CombatRules)
        {
            Identity(rule.Id.Value); Hash(rule.Revision.Value);
            Positive(rule.RetreatSeconds); Nonnegative(rule.MoraleDamageCoefficient); Ratio(rule.WeaponDestroyedMoraleLoss);
            Nonnegative(rule.MoraleRecoveryPerSecond); Ratio(rule.SurrenderThreshold); Positive(rule.NewLairIntervalSeconds);
        }
        Unique(Items(content.SettlementTypes), x => x.Id.Value);
        foreach (var type in content.SettlementTypes)
        {
            Identity(type.Id.Value); Hash(type.Revision.Value); Identities(type.PreferredTerrain);
            Require(type.Weight > 0 && type.MinimumInitialPopulation > 0 && type.MaximumInitialPopulation >= type.MinimumInitialPopulation, "SettlementTypes");
        }
        Unique(Items(content.SettlementTemplates), x => x.Id.Value);
        foreach (var template in content.SettlementTemplates)
        {
            Identity(template.Id.Value); Identity(template.Type.Value); Require(template.Weight > 0, "Templates.Weight");
            Unique(Items(template.Plots), x => x.Number);
            foreach (var plot in template.Plots)
            { Require(plot.Number > 0, "Plots.Number"); EnumValue(plot.Size); if (plot.PrebuiltDesign is { } design) Hash(design.Value); }
            Unique(Items(template.FoundingBuildings), x => x.PlotNumber);
            foreach (var building in template.FoundingBuildings) { Require(building.PlotNumber > 0, "FoundingBuildings"); Hash(building.Design.Value); }
            Quantities(template.SpecialInitialInventory);
            Unique(Items(template.ProductionPlans), x => (x.PlotNumber, x.Capability));
            foreach (var plan in template.ProductionPlans)
            { Require(plan.PlotNumber > 0, "ProductionPlans"); EnumValue(plan.Capability); Process(plan.Process); }
        }
        Unique(Items(content.Creatures), x => x.Id.Value);
        foreach (var creature in content.Creatures)
        {
            Identity(creature.Id.Value); Hash(creature.Revision.Value); Require(creature.HitPoints > 0, "HitPoints");
            Nonnegative(creature.SpeedMetersPerSecond); Ratio(creature.RetreatLossRatio); Positive(creature.RaidDurationSeconds);
            if (creature.Weapon is { } weapon) Capability(weapon);
        }
        Unique(Items(content.LairTypes), x => x.Id.Value);
        foreach (var lair in content.LairTypes)
        {
            Identity(lair.Id.Value); Hash(lair.Revision.Value);
            Require(lair.GenerationWeight > 0 && lair.MaximumMembers > 0 && lair.RoamingRadiusHops >= 0 && lair.RaidRadiusHops >= 0 && lair.ClearanceReward.Value >= 0, "LairTypes");
            Positive(lair.SpawnIntervalSeconds); Positive(lair.RaidIntervalSeconds); Positive(lair.RailDamageWorkSeconds);
            Unique(Items(lair.Members), x => x.Creature.Value);
            foreach (var member in lair.Members) { Identity(member.Creature.Value); Require(member.Weight > 0, "Members.Weight"); }
        }
        Missing(content.MissingReferences);
        Unique(content.PopulationRules.Select(x => x.Id).Concat(content.MarketRules.Select(x => x.Id))
            .Concat(content.ConstructionRules.Select(x => x.Id)).Concat(content.RailwayRules.Select(x => x.Id))
            .Concat(content.CombatRules.Select(x => x.Id)), x => x.Value);
    }

    internal static (WorldGenerationInput Input, CoreContentProjection Content) Normalize(
        WorldGenerationInput input, CoreContentProjection content, GenerationBudgetCounter budget)
    {
        var c = input.Configuration;
        var normalized = c with
        {
            ElevationNoise = Noise(c.ElevationNoise, budget), MoistureNoise = Noise(c.MoistureNoise, budget), RiverNoise = Noise(c.RiverNoise, budget),
            TerrainBands = Bands(c.TerrainBands, budget),
            LairWeights = Sorted(c.LairWeights, x => x.Type.Value, budget),
            Resources = Sorted(c.Resources, x => x.Good.Value, budget).Select(x => x with
                { AllowedTerrainTypes = Sorted(x.AllowedTerrainTypes, t => t.Value, budget) }).ToImmutableArray(),
            NpcInitialization = c.NpcInitialization with
                { ForSaleVehicleCandidates = Sorted(c.NpcInitialization.ForSaleVehicleCandidates, x => x.Value, budget) }
        };
        return (input with { Configuration = normalized, Start = input.Start with
            { Trains = input.Start.Trains.Select(x => x with { Inventory = Amounts(x.Inventory, budget) }).ToImmutableArray() } }, content with
        {
            Goods = Sorted(content.Goods, x => GoodKey(x.Good)),
            DesignGoods = Sorted(content.DesignGoods, x => GoodKey(x.Good)),
            Designs = Sorted(content.Designs, x => x.Design.Value).Select(NormalizeDesign).ToImmutableArray(),
            Processes = Sorted(content.Processes, x => ProcessKey(x.Details)),
            PopulationRules = Sorted(content.PopulationRules, x => x.Id.Value),
            MarketRules = Sorted(content.MarketRules, x => x.Id.Value),
            ConstructionRules = Sorted(content.ConstructionRules, x => x.Id.Value),
            RailwayRules = Sorted(content.RailwayRules, x => x.Id.Value),
            CombatRules = Sorted(content.CombatRules, x => x.Id.Value),
            SettlementTypes = Sorted(content.SettlementTypes, x => x.Id.Value).Select(x => x with
                { PreferredTerrain = Sorted(x.PreferredTerrain, t => t.Value) }).ToImmutableArray(),
            SettlementTemplates = Sorted(content.SettlementTemplates, x => x.Id.Value).Select(NormalizeTemplate).ToImmutableArray(),
            Creatures = Sorted(content.Creatures, x => x.Id.Value),
            LairTypes = Sorted(content.LairTypes, x => x.Id.Value).Select(x => x with
                { Members = Sorted(x.Members, m => m.Creature.Value) }).ToImmutableArray()
        });
    }

    internal static SettlementTemplateProjection NormalizeTemplate(SettlementTemplateProjection template) => template with
    {
        Plots = template.Plots.OrderBy(p => p.Number).ToImmutableArray(),
        FoundingBuildings = template.FoundingBuildings.OrderBy(p => p.PlotNumber).ToImmutableArray(),
        SpecialInitialInventory = Amounts(template.SpecialInitialInventory),
        ProductionPlans = template.ProductionPlans.OrderBy(p => p.PlotNumber).ThenBy(p => p.Capability).ToImmutableArray()
    };

    internal static DesignProjection NormalizeDesign(DesignProjection design) => design with
    {
        Kind = design.Kind is DesignKind.Building building ? building with
            { FacilityTags = building.FacilityTags.Order().ToImmutableArray() } : design.Kind,
        Parts = design.Parts.OrderBy(x => x.Id.Value).Select(x => x with
            { Capabilities = x.Capabilities.OrderBy(CapabilityOrder).ToImmutableArray() }).ToImmutableArray(),
        StructuralGoods = Amounts(design.StructuralGoods), BillOfMaterials = Amounts(design.BillOfMaterials),
        ReturnUnits = design.ReturnUnits.Select(x => x with
            { Parts = x.Parts.OrderBy(p => p.Value).ToImmutableArray(), StructuralGoods = Amounts(x.StructuralGoods),
                MissingReferences = MissingOrder(x.MissingReferences) })
            .Order(Comparer<DesignReturnUnit>.Create(CompareReturnUnits)).ToImmutableArray(),
        MissingReferences = MissingOrder(design.MissingReferences)
    };

    private static (int Kind, double First, double Second, long Count, int Size) CapabilityOrder(PartCapability capability) => capability switch
    {
        PartCapability.Propulsion p => (0, p.TractionKilonewtons, p.RatedSpeedMetersPerSecond, 0, 0),
        PartCapability.Control => (1, 0, 0, 0, 0),
        PartCapability.Storage p => (2, 0, 0, p.Capacity, 0),
        PartCapability.Production p => (3, p.WorkPerSecond, 0, 0, 0),
        PartCapability.Extraction p => (4, p.WorkPerSecond, 0, 0, 0),
        PartCapability.Construction p => (5, p.WorkPerSecond, 0, 0, (int)p.MaximumVehicleSize),
        PartCapability.Power p => (6, p.EnergyPerSecond, 0, 0, 0),
        PartCapability.Weapon p => (7, p.IntervalSeconds, 0, p.Damage, 0),
        PartCapability.Platform => (8, 0, 0, 0, 0),
        _ => throw new ArgumentException("Invalid part capability.")
    };

    private static ImmutableArray<MissingCoreReference> MissingOrder(ImmutableArray<MissingCoreReference> values) =>
        values.OrderBy(x => DefinitionKey(x.Reference), StringComparer.Ordinal).ThenBy(x => x.FieldPath, StringComparer.Ordinal).ToImmutableArray();

    private static int CompareReturnUnits(DesignReturnUnit left, DesignReturnUnit right)
    {
        var comparison = StringComparer.Ordinal.Compare(GoodKey(left.Good), GoodKey(right.Good));
        if (comparison != 0) return comparison;
        comparison = left.Quantity.CompareTo(right.Quantity);
        if (comparison != 0) return comparison;
        for (var i = 0; i < Math.Min(left.Parts.Length, right.Parts.Length); i++)
        {
            comparison = left.Parts[i].Value.CompareTo(right.Parts[i].Value);
            if (comparison != 0) return comparison;
        }
        comparison = left.Parts.Length.CompareTo(right.Parts.Length);
        if (comparison != 0) return comparison;
        for (var i = 0; i < Math.Min(left.StructuralGoods.Length, right.StructuralGoods.Length); i++)
        {
            comparison = StringComparer.Ordinal.Compare(GoodKey(left.StructuralGoods[i].Good), GoodKey(right.StructuralGoods[i].Good));
            if (comparison != 0) return comparison;
            comparison = left.StructuralGoods[i].Quantity.CompareTo(right.StructuralGoods[i].Quantity);
            if (comparison != 0) return comparison;
        }
        comparison = left.StructuralGoods.Length.CompareTo(right.StructuralGoods.Length);
        if (comparison != 0) return comparison;
        for (var i = 0; i < Math.Min(left.MissingReferences.Length, right.MissingReferences.Length); i++)
        {
            comparison = StringComparer.Ordinal.Compare(DefinitionKey(left.MissingReferences[i].Reference), DefinitionKey(right.MissingReferences[i].Reference));
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.MissingReferences[i].FieldPath, right.MissingReferences[i].FieldPath);
            if (comparison != 0) return comparison;
        }
        return left.MissingReferences.Length.CompareTo(right.MissingReferences.Length);
    }

    private static ImmutableArray<NoiseOctave> Noise(ImmutableArray<NoiseOctave> values, GenerationBudgetCounter budget)
    { budget.Sort(values.Length); return values.OrderBy(x => x.ScaleMeters).ThenBy(x => x.Amplitude).ToImmutableArray(); }
    private static ImmutableArray<TerrainGenerationBand> Bands(ImmutableArray<TerrainGenerationBand> values, GenerationBudgetCounter budget)
    {
        budget.Sort(values.Length);
        return values.Distinct().OrderBy(x => x.MinimumElevation).ThenBy(x => x.MaximumElevation)
            .ThenBy(x => x.MinimumMoisture).ThenBy(x => x.MaximumMoisture).ThenBy(x => x.MaximumRelief)
            .ThenBy(x => x.DetourMultiplier).ThenBy(x => x.MaximumTracks).ThenBy(x => x.Terrain.Value, StringComparer.Ordinal).ToImmutableArray();
    }
    private static ImmutableArray<T> Sorted<T>(ImmutableArray<T> values, Func<T, string> key, GenerationBudgetCounter? budget = null)
    { budget?.Sort(values.Length); return values.OrderBy(key, StringComparer.Ordinal).ToImmutableArray(); }
    private static ImmutableArray<GoodsQuantity> Amounts(ImmutableArray<GoodsQuantity> values, GenerationBudgetCounter? budget = null) => Sorted(values, x => GoodKey(x.Good), budget);
    private static ImmutableArray<T> Items<T>(ImmutableArray<T> values)
    {
        Require(!values.IsDefault, "Uninitialized array");
        foreach (var value in values) Require(value is not null, "Null array element");
        return values;
    }
    private static void Unique<T, TKey>(IEnumerable<T> values, Func<T, TKey> key) where TKey : notnull
    {
        var keys = new HashSet<TKey>();
        foreach (var value in values) Require(keys.Add(key(value)), "Duplicate identity");
    }
    private static void Quantities(ImmutableArray<GoodsQuantity> values)
    { Unique(Items(values), x => GoodKey(x.Good)); foreach (var value in values) Quantity(value); }
    private static void Quantity(GoodsQuantity value)
    { ArgumentNullException.ThrowIfNull(value); Good(value.Good); Require(value.Quantity > 0, "Quantity"); }
    private static void Prices(GoodsPriceParameters value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.Sensitivity.Value >= 0 && value.MinimumMultiplier.Value > 0 && value.MaximumMultiplier.Value >= value.MinimumMultiplier.Value &&
            value.Spread.Value is >= 0 and < 10000, "GoodsPriceParameters");
    }
    private static void Identities(ImmutableArray<TerrainTypeId> values)
    { Unique(Items(values), x => x.Value); foreach (var value in values) Identity(value.Value); }
    private static void Missing(ImmutableArray<MissingCoreReference> values)
    {
        foreach (var value in Items(values)) { Reference(value.Reference); ArgumentNullException.ThrowIfNull(value.FieldPath); }
    }
    private static void Good(GoodId good)
    {
        switch (good)
        {
            case GoodId.Voxel voxel: Identity(voxel.Definition.Value); break;
            case GoodId.Composite composite: Identity(composite.Definition.Value); break;
            case GoodId.Assembly assembly: Hash(assembly.Composition.Value); break;
            default: throw new ArgumentException("Invalid good identity.");
        }
    }
    private static string ProcessKey(ProcessDetails details) => details switch
    {
        ProcessDetails.Production x => "production:" + x.Id.Value,
        ProcessDetails.Extraction x => "production:" + x.Id.Value,
        ProcessDetails.Recovery x => "recovery:" + x.Id.Value,
        _ => throw new ArgumentException("Invalid process details.")
    };
    private static void Process(ProcessRef process)
    {
        switch (process)
        {
            case ProcessRef.Production p: Identity(p.Definition.Value); break;
            case ProcessRef.Recovery p: Identity(p.Definition.Value); break;
            default: throw new ArgumentException("Invalid process reference.");
        }
    }
    private static void Reference(CoreDefinitionReference reference)
    {
        switch (reference)
        {
            case CoreDefinitionReference.Voxel x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.Composite x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.Process x: Process(x.Id); break;
            case CoreDefinitionReference.Rule x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.SettlementType x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.Template x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.Terrain x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.Creature x: Identity(x.Id.Value); break;
            case CoreDefinitionReference.Lair x: Identity(x.Id.Value); break;
            default: throw new ArgumentException("Invalid definition reference.");
        }
    }
    private static void Capability(PartCapability capability)
    {
        switch (capability)
        {
            case PartCapability.Propulsion p: Positive(p.TractionKilonewtons); Positive(p.RatedSpeedMetersPerSecond); break;
            case PartCapability.Storage p: Require(p.Capacity > 0, "Storage.Capacity"); break;
            case PartCapability.Production p: Positive(p.WorkPerSecond); break;
            case PartCapability.Extraction p: Positive(p.WorkPerSecond); break;
            case PartCapability.Construction p: Positive(p.WorkPerSecond); EnumValue(p.MaximumVehicleSize); break;
            case PartCapability.Power p: Positive(p.EnergyPerSecond); break;
            case PartCapability.Weapon p: Require(p.Damage > 0, "Weapon.Damage"); Positive(p.IntervalSeconds); break;
            case PartCapability.Control or PartCapability.Platform: break;
            default: throw new ArgumentException("Invalid part capability.");
        }
    }
    private static void Identity(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var separator = value.IndexOf(':');
        Require(separator > 0 && separator < value.Length - 1 && separator == value.LastIndexOf(':') &&
            !value.Any(char.IsWhiteSpace) && value == value.ToLowerInvariant(), "Canonical identity");
    }
    private static void Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.Length == 64 && value.All(x => x is >= '0' and <= '9' or >= 'A' and <= 'F'), "Canonical SHA-256");
    }
    private static void EnumValue<T>(T value) where T : struct, Enum => Require(Enum.IsDefined(value), "Invalid enum");
    private static void Finite(double value) => Require(double.IsFinite(value), "Non-finite value");
    private static void Positive(double value) { Finite(value); Require(value > 0, "Positive value required"); }
    private static void Nonnegative(double value) { Finite(value); Require(value >= 0, "Nonnegative value required"); }
    private static void Ratio(double value) { Finite(value); Require(value is >= 0 and <= 1, "Ratio"); }
    private static void Require(bool condition, string field)
    { if (!condition) throw new ArgumentException("Invalid " + field + ".", field); }
}

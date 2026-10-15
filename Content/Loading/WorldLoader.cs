using System.Collections.Immutable;
using Train.Content.Designs;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record LoadedGenerationProfile(C.GenerationProfileId Id, C.DisplayText Display,
    K.ResolvedGenerationConfiguration Configuration, K.NewGameStartInput Start);
internal sealed record WorldDefinitions(ImmutableArray<C.TerrainAppearanceProjection> Terrains,
    ImmutableArray<LoadedSettlementType> Types, ImmutableArray<LoadedSettlementTemplate> Templates,
    ImmutableArray<ParsedRule> Rules, ImmutableArray<LoadedGenerationProfile> Profiles, ImmutableArray<ParsedProcess> Processes)
{
    internal static WorldDefinitions Empty { get; } = new([], [], [], [], [], []);
}

internal sealed class WorldLoader
{
    private readonly List<(LoadPackage Package, ParsedTerrain Value)> _terrains = [];
    private readonly List<(LoadPackage Package, LoadedSettlementType Value)> _types = [];
    private readonly List<(LoadPackage Package, ParsedSettlementTemplate Value)> _templates = [];
    private readonly List<(LoadPackage Package, ParsedRule Value)> _rules = [];
    private readonly List<(LoadPackage Package, ParsedGenerationProfile Value)> _profiles = [];
    private readonly List<(LoadPackage Package, ParsedProcess Value)> _processes = [];

    internal static bool Directory(string name) => name is "terrain-types" or "settlement-types" or "settlement-templates"
        or "generation-profiles" or "rules" or "railway-rules" or "combat-rules" or "processes";

    internal static string? Kind(string directory, string file) => directory switch
    {
        "terrain-types" => "terrain-type", "settlement-types" => "settlement-type",
        "settlement-templates" => "settlement-template", "generation-profiles" => "generation-profile",
        "railway-rules" => "railway-rule", "combat-rules" => "combat-rule", "processes" => "process",
        "rules" => new[] { "population-rule", "market-rule", "construction-rule" }
            .FirstOrDefault(kind => file.EndsWith("." + kind + ".json", StringComparison.Ordinal)),
        _ => null
    };

    internal void Parse(LoadPackage package, string kind, string path, byte[] bytes, AssetLoader assets)
    {
        switch (kind)
        {
            case "terrain-type": Add(TerrainParser.Parse(bytes, package, path), _terrains); break;
            case "settlement-template": Add(SettlementParser.Template(bytes, package, path), _templates); break;
            case "generation-profile": Add(GenerationProfileParser.Parse(bytes, package, path), _profiles); break;
            case "process": Add(ProcessParser.Parse(bytes, package, path), _processes); break;
            case "settlement-type":
                var type = SettlementParser.Type(bytes, package, path);
                if (type is null) break;
                var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(type.Core.Id));
                var symbol = assets.Load(package, "settlement-types", type.Core.Id.Value, subject,
                    type.MapSymbol, "/display/map_symbol", C.AssetKind.Png);
                if (symbol is not null && AssetLoader.MapSymbol(symbol, package, subject)) _types.Add((package, new(type, symbol.Revision)));
                break;
            default: Add(RuleParser.Parse(bytes, package, path, kind[..^5]), _rules); break;
        }
        void Add<T>(T? value, List<(LoadPackage Package, T Value)> target) where T : class
        {
            if (value is not null) target.Add((package, value));
        }
    }

    internal WorldDefinitions Resolve(List<LoadPackage> packages,
        List<(LoadPackage Package, LoadedVoxel Voxel)> voxels,
        List<(LoadPackage Package, C.BlueprintRecord Blueprint)> blueprints)
    {
        RejectDuplicates(_terrains, t => t.Id.Value, t => t.Path);
        RejectDuplicates(_types, t => t.Source.Core.Id.Value, t => t.Source.Path);
        RejectDuplicates(_templates, t => t.Id.Value, t => t.Path);
        RejectDuplicates(_rules, r => r.Id.Value, r => r.Path);
        RejectDuplicates(_profiles, p => p.Id.Value, p => p.Path);
        RejectDuplicates(_processes, p => p.Id, p => p.Path);
        var resolvedTerrains = new Dictionary<K.TerrainTypeId, C.TerrainAppearanceProjection>();
        var resolvedTemplates = new Dictionary<K.SettlementTemplateId, LoadedSettlementTemplate>();
        var resolvedProfiles = new Dictionary<C.GenerationProfileId, LoadedGenerationProfile>();
        int before;
        // 排除一个包会让其他包的引用失效；重复校验，直到可用包集合不再缩小。
        do
        {
            PackageLoader.ExcludeDependents(packages);
            before = packages.Count(p => Active(p));
            var voxelIndex = voxels.Where(v => Active(v.Package)).ToDictionary(v => v.Voxel.Definition.Definition);
            var blueprintIndex = blueprints.Where(b => Active(b.Package)).ToDictionary(b => b.Blueprint.Address.Id.Value, b => b.Blueprint, StringComparer.Ordinal);
            var terrainIndex = _terrains.Where(t => Active(t.Package)).ToDictionary(t => t.Value.Id, t => t.Value);
            var typeIndex = _types.Where(t => Active(t.Package)).ToDictionary(t => t.Value.Source.Core.Id);
            var ruleIndex = _rules.Where(r => Active(r.Package)).ToDictionary(r => r.Value.Id, r => r.Value);
            var known = voxelIndex.Keys.Select(k => k.Value).Concat(blueprintIndex.Keys)
                .Concat(terrainIndex.Keys.Select(k => k.Value)).Concat(typeIndex.Keys.Select(k => k.Value))
                .Concat(ruleIndex.Keys.Select(k => k.Value)).Concat(_templates.Where(t => Active(t.Package)).Select(t => t.Value.Id.Value))
                .Concat(_processes.Where(p => Active(p.Package)).Select(p => p.Value.Id))
                .Concat(_profiles.Where(p => Active(p.Package)).Select(p => p.Value.Id.Value)).ToHashSet(StringComparer.Ordinal);
            var references = new References(voxelIndex, blueprintIndex, known);
            foreach (var (package, terrain) in _terrains.Where(t => Active(t.Package)))
            {
                var loaded = ResolveTerrain(package, terrain, references);
                if (loaded is not null) resolvedTerrains[terrain.Id] = loaded;
            }
            foreach (var (package, type) in _types.Where(t => Active(t.Package)))
                foreach (var terrain in type.Source.Core.PreferredTerrain)
                    references.Need(package, type.Source.Path, new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(type.Source.Core.Id)),
                        terrain.Value, terrainIndex.ContainsKey(terrain), "/simulation/preferred_terrain");
            foreach (var (package, rule) in _rules.Where(r => Active(r.Package)))
                ResolveRule(package, rule, references);
            foreach (var (package, process) in _processes.Where(p => Active(p.Package)))
            {
                var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Process(process.Reference));
                foreach (var good in process.Goods)
                    references.Good(package, process.Path, subject, good.Id, good.Field);
                if (process.Projection.Details is K.ProcessDetails.Extraction extraction &&
                    voxelIndex.TryGetValue(((K.GoodId.Voxel)extraction.Extracts.Good).Definition, out var voxel) &&
                    voxel.Voxel.Definition.Good is { RawGood: false })
                    package.Reject("invalid_field", process.Path, "/simulation/extracts/voxel", "开采货物必须带 raw_good 标签。", subject);
                foreach (var terrain in process.Terrains)
                    references.Need(package, process.Path, subject, terrain.Id.Value, terrainIndex.ContainsKey(terrain.Id), terrain.Field);
            }
            foreach (var (package, source) in _templates.Where(t => Active(t.Package)))
            {
                var loaded = ResolveTemplate(package, source, references, typeIndex.ContainsKey(source.Type));
                if (loaded is not null) resolvedTemplates[source.Id] = loaded;
            }
            foreach (var (package, source) in _profiles.Where(p => Active(p.Package)))
            {
                var loaded = ResolveProfile(package, source, references, terrainIndex, ruleIndex);
                if (loaded is not null) resolvedProfiles[source.Id] = loaded;
            }
            PackageLoader.ExcludeDependents(packages);
        } while (packages.Count(p => Active(p)) < before);

        return new(_terrains.Where(t => Active(t.Package)).Select(t => resolvedTerrains[t.Value.Id]).OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _types.Where(t => Active(t.Package)).Select(t => t.Value).OrderBy(t => t.Source.Core.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _templates.Where(t => Active(t.Package)).Select(t => resolvedTemplates[t.Value.Id]).OrderBy(t => t.Core.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _rules.Where(r => Active(r.Package)).Select(r => r.Value).OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _profiles.Where(p => Active(p.Package)).Select(p => resolvedProfiles[p.Value.Id]).OrderBy(p => p.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _processes.Where(p => Active(p.Package)).Select(p => p.Value).OrderBy(p => p.Id, StringComparer.Ordinal).ToImmutableArray());
    }

    private static C.TerrainAppearanceProjection? ResolveTerrain(LoadPackage package, ParsedTerrain source, References references)
    {
        var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Terrain(source.Id));
        foreach (var (id, field) in new[] { (source.SurfaceVoxel, "/surface_voxel"), (source.SubsurfaceVoxel, "/subsurface_voxel"), (source.RockVoxel, "/rock_voxel") })
        {
            references.Need(package, source.Path, subject, id.Value, references.Voxels.ContainsKey(id), field);
            if (references.Voxels.TryGetValue(id, out var voxel) && voxel.Voxel.Definition.Good is not null)
                package.Reject("reference_wrong_kind", source.Path, field, "地貌分层必须引用场景体素。", subject);
        }
        var decorations = ImmutableArray.CreateBuilder<C.TerrainDecoration>();
        foreach (var decoration in source.Decorations)
            if (references.Scene(package, source.Path, subject, decoration.SceneModel, "/decorations") is { } model)
                decorations.Add(new(model.Design, decoration.Weight));
        var railwayModels = ImmutableArray.CreateBuilder<C.RailwaySceneModels>();
        foreach (var model in source.RailwayModels)
        {
            var bridge = references.Scene(package, source.Path, subject, model.Bridge, "/railway_models");
            var portal = references.Scene(package, source.Path, subject, model.TunnelPortal, "/railway_models");
            if (bridge is not null && portal is not null) railwayModels.Add(new(model.Tracks, bridge.Design, portal.Design));
        }
        return Active(package) ? new(source.Id, source.SurfaceVoxel, source.SubsurfaceVoxel, source.RockVoxel,
            source.LocalHeightNoise, decorations.ToImmutable(), source.DecorationTargetPerSquareMeter,
            railwayModels.ToImmutable(), source.Display) : null;
    }

    private static void ResolveRule(LoadPackage package, ParsedRule rule, References references)
    {
        var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Rule(rule.Id));
        foreach (var good in rule.Goods)
            references.Good(package, rule.Path, subject, good.Id, good.Field);
        if (rule is not ParsedRule.Population population) return;
        foreach (var consumption in population.Projection.ConsumptionGoods.Where(g => g.Satisfies.Any(c => c.Need == K.PopulationNeed.Food)))
        {
            var id = ((K.GoodId.Voxel)consumption.Good).Definition;
            if (references.Voxels.TryGetValue(id, out var voxel) && voxel.Voxel.Definition.Good is { RawGood: true })
                package.Reject("invalid_field", rule.Path, rule.Goods.First(g => g.Id == id).Field,
                    "满足 food 的货物不能是 Raw Good。", subject);
        }
    }

    private static LoadedSettlementTemplate? ResolveTemplate(LoadPackage package, ParsedSettlementTemplate source,
        References references, bool typePresent)
    {
        var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Template(source.Id));
        references.Need(package, source.Path, subject, source.Type.Value, typePresent, "/settlement_type");
        var scene = references.Scene(package, source.Path, subject, source.SceneModel, "/scene_model");
        if (scene is not null) ValidatePlotSpace(package, source, scene.Scene, subject);
        var plots = ImmutableArray.CreateBuilder<K.TemplatePlotProjection>();
        foreach (var plot in source.Plots)
        {
            if (plot.Building is not { } address) { plots.Add(new(plot.Number, plot.Size, null)); continue; }
            var design = references.Design(package, source.Path, subject, address, "/plots", vehicle: false);
            if (design?.Core?.Design.Kind is K.DesignKind.Building building && !PlotRules.Fits(building.PlotSize, plot.Size))
                package.Reject("invalid_field", source.Path, "/plots", "建筑类型或尺寸不能放入地块。", subject);
            if (design is not null) plots.Add(new(plot.Number, plot.Size, design.Design));
        }
        return Active(package) && scene is not null
            ? new(source, new(source.Id, source.Type, source.Weight, plots.ToImmutable(), [], [], []), scene) : null;
    }

    private static void ValidatePlotSpace(LoadPackage package, ParsedSettlementTemplate source,
        C.DesignSceneProjection scene, C.ContentReference subject)
    {
        (long MinX, long MaxX, long MinZ, long MaxZ)? range = scene.OccupiedBounds is { } occupied
            ? (occupied.Minimum.X, occupied.Maximum.X, occupied.Minimum.Z, occupied.Maximum.Z) : null;
        if (scene.RootFlatten is { } flatten)
            range = range is { } bounds
                ? (Math.Min(bounds.MinX, flatten.From.X), Math.Max(bounds.MaxX, flatten.To.X),
                    Math.Min(bounds.MinZ, flatten.From.Z), Math.Max(bounds.MaxZ, flatten.To.Z))
                : (flatten.From.X, flatten.To.X, flatten.From.Z, flatten.To.Z);
        var plots = source.Layout.Select(PlotRules.Footprint).ToArray();
        for (var index = 0; index < plots.Length; index++)
        {
            var plot = plots[index];
            var field = source.Plots[index].SourceField + "/at";
            if (range is not { } bounds || plot.MinX < bounds.MinX || plot.MaxX > bounds.MaxX
                || plot.MinZ < bounds.MinZ || plot.MaxZ > bounds.MaxZ)
                package.Reject("invalid_field", source.Path, field, "地块占地超出场景模型范围。", subject);
            for (var previous = 0; previous < index; previous++)
            {
                var other = plots[previous];
                if (plot.MinX <= other.MaxX && plot.MaxX >= other.MinX && plot.MinZ <= other.MaxZ && plot.MaxZ >= other.MinZ)
                    package.Reject("invalid_field", source.Path, field, "地块占地与其他地块重叠。", subject);
            }
        }
    }

    private static LoadedGenerationProfile? ResolveProfile(LoadPackage package, ParsedGenerationProfile source,
        References references, Dictionary<K.TerrainTypeId, ParsedTerrain> terrains, Dictionary<K.RuleDefinitionId, ParsedRule> rules)
    {
        var subject = new C.ContentReference.Generation(source.Id);
        var config = source.Configuration;
        foreach (var band in config.TerrainBands)
        {
            references.Need(package, source.Path, subject, band.Terrain.Value, terrains.ContainsKey(band.Terrain), "/configuration/terrain_bands");
            if (terrains.TryGetValue(band.Terrain, out var terrain))
            {
                var limit = Math.Min(band.MaximumTracks, config.InitialRailway.MaximumTracks);
                if (terrain.RailwayModels.Count(m => m.Tracks <= limit) != limit)
                    package.Reject("invalid_field", source.Path, "/configuration/terrain_bands", "地貌铁路模型未覆盖配置允许的全部非零轨数。", subject);
            }
        }
        foreach (var resource in config.Resources)
        {
            references.Good(package, source.Path, subject, resource.Good, "/configuration/resources");
            if (references.Voxels.TryGetValue(resource.Good, out var voxel) && voxel.Voxel.Definition.Good is { RawGood: false })
                package.Reject("reference_wrong_kind", source.Path, "/configuration/resources", "节点资源必须是 Raw Good。", subject);
            foreach (var terrain in resource.AllowedTerrainTypes)
                references.Need(package, source.Path, subject, terrain.Value, terrains.ContainsKey(terrain), "/configuration/resources");
        }
        foreach (var lair in config.LairWeights)
            references.Need(package, source.Path, subject, lair.Type.Value, false, "/configuration/lair_weights");
        Rule(config.Rules.Population, r => r is ParsedRule.Population, "population");
        Rule(config.Rules.Market, r => r is ParsedRule.Market, "market");
        Rule(config.Rules.Construction, r => r is ParsedRule.Construction, "construction");
        Rule(config.Rules.Railway, r => r is ParsedRule.Railway, "railway");
        Rule(config.Rules.Combat, r => r is ParsedRule.Combat, "combat");
        var commission = Vehicle(config.StartConstraints.RequiredAffordableCommissionDesign, "/configuration/start_constraints/required_affordable_commission_design");
        var candidates = config.NpcInitialization.ForSaleVehicleCandidates
            .Select(a => Vehicle(a, "/configuration/npc_initialization/for_sale_vehicle_candidates")).OfType<K.DesignHash>().ToImmutableArray();
        if (candidates.Distinct().Count() != candidates.Length)
            package.Reject("invalid_field", source.Path, "/configuration/npc_initialization/for_sale_vehicle_candidates",
                "待售载具候选解析后不能包含重复的设计哈希。", subject);
        var trains = ImmutableArray.CreateBuilder<K.StartingTrainProjection>();
        foreach (var train in source.Start.Trains)
        {
            var vehicles = train.Vehicles.Select(a => Vehicle(a, "/start/trains/vehicles")).OfType<K.DesignHash>().ToImmutableArray();
            var inventory = ImmutableArray.CreateBuilder<K.GoodsQuantity>();
            foreach (var quantity in train.Inventory)
                switch (quantity.Reference)
                {
                    case ParsedInventoryReference.Voxel good:
                        references.Good(package, source.Path, subject, good.Id, "/start/trains/inventory");
                        inventory.Add(new(new K.GoodId.Voxel(good.Id), quantity.Quantity));
                        break;
                    case ParsedInventoryReference.Blueprint blueprint:
                        references.Need(package, source.Path, subject, blueprint.Address.Id.Value, false, "/start/trains/inventory");
                        break;
                }
            trains.Add(new(vehicles, inventory.ToImmutable()));
        }
        if (!Active(package) || commission is null) return null;
        var npc = config.NpcInitialization;
        var start = config.StartConstraints;
        var configuration = new K.ResolvedGenerationConfiguration(config.WidthMeters, config.HeightMeters,
            config.MinimumNodeSpacingMeters, config.CandidateProbability, config.ElevationNoise, config.MoistureNoise,
            config.RiverNoise, config.LakeElevationThreshold, config.RiverBandHalfWidth, config.TerrainBands,
            config.TerrainSampleSpacingMeters, config.SettlementRatio, config.LairRatio, config.HostileSettlementRatio,
            config.LairWeights, config.Resources, config.InitialRailway, config.Budget,
            new(npc.FillInputsAndLivingTargets, npc.OutputReserveHours, npc.MinimumForSaleVehicles, npc.MaximumForSaleVehicles, candidates),
            new(start.MinimumReachableNonHostileSettlements, start.MinimumReachableEmptyNodes, start.RequireStation,
                start.ExcludeAdjacentLairs, start.FullyLoadedPurchaseRoundsPerTrain, commission.Value),
            config.Rules);
        return new(source.Id, source.Display, configuration, new(source.Start.InitialMoney, trains.ToImmutable()));
        void Rule(K.RuleDefinitionId id, Func<ParsedRule, bool> matches, string field)
            => references.Need(package, source.Path, subject, id.Value, rules.TryGetValue(id, out var rule) && matches(rule), "/configuration/rules/" + field);
        K.DesignHash? Vehicle(C.BlueprintAddress address, string field)
            => references.Design(package, source.Path, subject, address, field, vehicle: true)?.Design;
    }

    private sealed class References(
        Dictionary<K.VoxelDefinitionId, (LoadPackage Package, LoadedVoxel Voxel)> voxels,
        Dictionary<string, C.BlueprintRecord> blueprints, HashSet<string> known)
    {
        internal Dictionary<K.VoxelDefinitionId, (LoadPackage Package, LoadedVoxel Voxel)> Voxels => voxels;
        internal void Need(LoadPackage package, string path, C.ContentReference subject, string id, bool present, string field)
        {
            if (!present) package.Reject(known.Contains(id) ? "reference_wrong_kind" : "reference_missing", path, field, "引用不可用：" + id + "。", subject);
        }
        internal void Good(LoadPackage package, string path, C.ContentReference subject, K.VoxelDefinitionId id, string field)
        {
            Need(package, path, subject, id.Value, voxels.ContainsKey(id), field);
            if (voxels.TryGetValue(id, out var voxel) && voxel.Voxel.Definition.Good is null)
                package.Reject("reference_wrong_kind", path, field, "引用必须是可库存货物。", subject);
        }
        internal C.DesignEvaluation.Complete? Design(LoadPackage package, string path, C.ContentReference subject,
            C.BlueprintAddress address, string field, bool vehicle)
        {
            Need(package, path, subject, address.Id.Value, blueprints.ContainsKey(address.Id.Value), field);
            if (!blueprints.TryGetValue(address.Id.Value, out var blueprint)) return null;
            var design = (C.DesignEvaluation.Complete)blueprint.Evaluation;
            if (vehicle ? design.Core?.Design.Kind is K.DesignKind.Vehicle : design.Core?.Design.Kind is K.DesignKind.Building) return design;
            package.Reject("reference_wrong_kind", path, field, "蓝图类别不符。", subject);
            return null;
        }
        internal C.SceneModelProjection? Scene(LoadPackage package, string path, C.ContentReference subject,
            C.BlueprintAddress address, string field)
        {
            Need(package, path, subject, address.Id.Value, blueprints.ContainsKey(address.Id.Value), field);
            if (!blueprints.TryGetValue(address.Id.Value, out var blueprint)) return null;
            if (blueprint.Definition.Specification is C.DesignSpecification.SceneModel && blueprint.Evaluation is C.DesignEvaluation.Complete design)
                return new(design.Design, design.Scene, blueprint.Definition.Display);
            package.Reject("reference_wrong_kind", path, field, "引用必须是场景模型。", subject);
            return null;
        }
    }

    private static bool Active(LoadPackage package) => package.State == C.PackageState.Available;
    private static void RejectDuplicates<T>(List<(LoadPackage Package, T Value)> values, Func<T, string> id, Func<T, string> path)
    {
        foreach (var group in values.Where(v => Active(v.Package)).GroupBy(v => id(v.Value)).Where(g => g.Count() > 1))
            foreach (var (package, value) in group) package.Reject("duplicate_identity", path(value), "/id", "定义身份重复。");
    }
}

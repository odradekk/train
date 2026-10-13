using System.Collections.Immutable;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record WorldDefinitions(ImmutableArray<ParsedTerrain> Terrains,
    ImmutableArray<LoadedSettlementType> Types, ImmutableArray<LoadedSettlementTemplate> Templates,
    ImmutableArray<ParsedRule> Rules, ImmutableArray<ParsedGenerationProfile> Profiles)
{
    internal static WorldDefinitions Empty { get; } = new([], [], [], [], []);
}

internal sealed class WorldLoader
{
    private readonly List<(LoadPackage Package, ParsedTerrain Value)> _terrains = [];
    private readonly List<(LoadPackage Package, LoadedSettlementType Value)> _types = [];
    private readonly List<(LoadPackage Package, ParsedSettlementTemplate Value)> _templates = [];
    private readonly List<(LoadPackage Package, ParsedRule Value)> _rules = [];
    private readonly List<(LoadPackage Package, ParsedGenerationProfile Value)> _profiles = [];

    internal static bool Directory(string name) => name is "terrain-types" or "settlement-types" or "settlement-templates"
        or "generation-profiles" or "rules" or "railway-rules" or "combat-rules";

    internal static string? Kind(string directory, string file) => directory switch
    {
        "terrain-types" => "terrain-type", "settlement-types" => "settlement-type",
        "settlement-templates" => "settlement-template", "generation-profiles" => "generation-profile",
        "railway-rules" => "railway-rule", "combat-rules" => "combat-rule",
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
            case "generation-profile": Add(new GenerationProfileParser().Parse(bytes, package, path), _profiles); break;
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
        RejectDuplicates(_terrains, t => t.Appearance.Id.Value, t => t.Path);
        RejectDuplicates(_types, t => t.Source.Core.Id.Value, t => t.Source.Path);
        RejectDuplicates(_templates, t => t.Core.Id.Value, t => t.Path);
        RejectDuplicates(_rules, r => r.Id.Value, r => r.Path);
        RejectDuplicates(_profiles, p => p.Id.Value, p => p.Path);
        var resolvedTemplates = new Dictionary<K.SettlementTemplateId, LoadedSettlementTemplate>();
        var resolvedProfiles = new Dictionary<C.GenerationProfileId, ParsedGenerationProfile>();
        int before;
        do
        {
            PackageLoader.ExcludeDependents(packages);
            before = packages.Count(p => Active(p));
            var voxelIndex = voxels.Where(v => Active(v.Package)).ToDictionary(v => v.Voxel.Definition.Definition);
            var blueprintIndex = blueprints.Where(b => Active(b.Package)).ToDictionary(b => b.Blueprint.Address.Id.Value, b => b.Blueprint, StringComparer.Ordinal);
            var terrainIndex = _terrains.Where(t => Active(t.Package)).ToDictionary(t => t.Value.Appearance.Id);
            var typeIndex = _types.Where(t => Active(t.Package)).ToDictionary(t => t.Value.Source.Core.Id);
            var ruleIndex = _rules.Where(r => Active(r.Package)).ToDictionary(r => r.Value.Id, r => r.Value);
            var known = voxelIndex.Keys.Select(k => k.Value).Concat(blueprintIndex.Keys)
                .Concat(terrainIndex.Keys.Select(k => k.Value)).Concat(typeIndex.Keys.Select(k => k.Value))
                .Concat(ruleIndex.Keys.Select(k => k.Value)).Concat(_templates.Where(t => Active(t.Package)).Select(t => t.Value.Core.Id.Value))
                .Concat(_profiles.Where(p => Active(p.Package)).Select(p => p.Value.Id.Value)).ToHashSet(StringComparer.Ordinal);

            foreach (var (package, terrain) in _terrains.Where(t => Active(t.Package)))
            {
                var a = terrain.Appearance;
                var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Terrain(a.Id));
                foreach (var (id, field) in new[] { (a.SurfaceVoxel, "/surface_voxel"), (a.SubsurfaceVoxel, "/subsurface_voxel"), (a.RockVoxel, "/rock_voxel") })
                {
                    Need(package, terrain.Path, subject, id.Value, voxelIndex.ContainsKey(id), field);
                    if (voxelIndex.TryGetValue(id, out var voxel) && voxel.Voxel.Definition.Good is not null)
                        package.Reject("reference_wrong_kind", terrain.Path, field, "地貌分层必须引用场景体素。", subject);
                }
                foreach (var decoration in a.Decorations) Scene(package, terrain.Path, subject, decoration.SceneModel.Value, "/decorations");
                foreach (var model in a.RailwayModels)
                {
                    Scene(package, terrain.Path, subject, model.Bridge.Value, "/railway_models");
                    Scene(package, terrain.Path, subject, model.TunnelPortal.Value, "/railway_models");
                }
            }
            foreach (var (package, type) in _types.Where(t => Active(t.Package)))
                foreach (var terrain in type.Source.Core.PreferredTerrain)
                    Need(package, type.Source.Path, new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(type.Source.Core.Id)),
                        terrain.Value, terrainIndex.ContainsKey(terrain), "/simulation/preferred_terrain");
            foreach (var (package, rule) in _rules.Where(r => Active(r.Package)))
                foreach (var good in rule.Goods)
                    Good(package, rule.Path, new C.ContentReference.Core(new K.CoreDefinitionReference.Rule(rule.Id)), good.Id, good.Field);
            foreach (var (package, source) in _templates.Where(t => Active(t.Package)))
            {
                var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Template(source.Core.Id));
                Need(package, source.Path, subject, source.Core.Type.Value, typeIndex.ContainsKey(source.Core.Type), "/settlement_type");
                var scene = Scene(package, source.Path, subject, source.SceneModel.Id.Value, "/scene_model");
                var plots = ImmutableArray.CreateBuilder<K.TemplatePlotProjection>();
                foreach (var plot in source.Core.Plots)
                {
                    if (plot.PrebuiltDesign is not { } reference) { plots.Add(plot); continue; }
                    var design = Design(package, source.Path, subject, reference.Value, "/plots", vehicle: false);
                    if (design?.Core?.Design.Kind is K.DesignKind.Building building &&
                        ((building.Type == K.BuildingKind.Station) != (plot.Size == K.PlotSize.Station) ||
                         (building.Type != K.BuildingKind.Station && building.PlotSize > plot.Size)))
                        package.Reject("invalid_field", source.Path, "/plots", "建筑类型或尺寸不能放入地块。", subject);
                    if (design is not null) plots.Add(plot with { PrebuiltDesign = design.Design });
                }
                if (Active(package) && scene is not null)
                    resolvedTemplates[source.Core.Id] = new(source, source.Core with { Plots = plots.ToImmutable() }, scene);
            }
            foreach (var (package, source) in _profiles.Where(p => Active(p.Package)))
            {
                var subject = new C.ContentReference.Generation(source.Id);
                var config = source.Configuration;
                foreach (var band in config.TerrainBands)
                {
                    Need(package, source.Path, subject, band.Terrain.Value, terrainIndex.ContainsKey(band.Terrain), "/configuration/terrain_bands");
                    if (terrainIndex.TryGetValue(band.Terrain, out var terrain))
                    {
                        var limit = Math.Min(band.MaximumTracks, config.InitialRailway.MaximumTracks);
                        if (terrain.Value.Appearance.RailwayModels.Count(m => m.Tracks <= limit) != limit)
                            package.Reject("invalid_field", source.Path, "/configuration/terrain_bands", "地貌铁路模型未覆盖配置允许的全部非零轨数。", subject);
                    }
                }
                foreach (var resource in config.Resources)
                {
                    Good(package, source.Path, subject, resource.Good, "/configuration/resources");
                    if (voxelIndex.TryGetValue(resource.Good, out var voxel) && voxel.Voxel.Definition.Good is { RawGood: false })
                        package.Reject("reference_wrong_kind", source.Path, "/configuration/resources", "节点资源必须是 Raw Good。", subject);
                    foreach (var terrain in resource.AllowedTerrainTypes)
                        Need(package, source.Path, subject, terrain.Value, terrainIndex.ContainsKey(terrain), "/configuration/resources");
                }
                foreach (var lair in config.LairWeights) Need(package, source.Path, subject, lair.Type.Value, false, "/configuration/lair_weights");
                Rule(config.Rules.Population, r => r is ParsedRule.Population, "population");
                Rule(config.Rules.Market, r => r is ParsedRule.Market, "market");
                Rule(config.Rules.Construction, r => r is ParsedRule.Construction, "construction");
                Rule(config.Rules.Railway, r => r is ParsedRule.Railway, "railway");
                Rule(config.Rules.Combat, r => r is ParsedRule.Combat, "combat");
                var commission = Vehicle(config.StartConstraints.RequiredAffordableCommissionDesign, "/configuration/start_constraints/required_affordable_commission_design");
                var candidates = config.NpcInitialization.ForSaleVehicleCandidates.Select(h => Vehicle(h, "/configuration/npc_initialization/for_sale_vehicle_candidates")).ToImmutableArray();
                var trains = source.Start.Trains.Select(train => new K.StartingTrainProjection(
                    train.Vehicles.Select(h => Vehicle(h, "/start/trains/vehicles")).ToImmutableArray(), train.Inventory)).ToImmutableArray();
                foreach (var quantity in trains.SelectMany(t => t.Inventory))
                    if (quantity.Good is K.GoodId.Voxel good) Good(package, source.Path, subject, good.Definition, "/start/trains/inventory");
                    else Need(package, source.Path, subject, ((K.GoodId.Composite)quantity.Good).Definition.Value, false, "/start/trains/inventory");
                if (Active(package)) resolvedProfiles[source.Id] = source with
                {
                    Configuration = config with
                    {
                        NpcInitialization = config.NpcInitialization with { ForSaleVehicleCandidates = candidates },
                        StartConstraints = config.StartConstraints with { RequiredAffordableCommissionDesign = commission }
                    },
                    Start = source.Start with { Trains = trains }
                };
                void Rule(K.RuleDefinitionId id, Func<ParsedRule, bool> matches, string field)
                    => Need(package, source.Path, subject, id.Value, ruleIndex.TryGetValue(id, out var rule) && matches(rule), "/configuration/rules/" + field);
                K.DesignHash Vehicle(K.DesignHash reference, string field)
                    => Design(package, source.Path, subject, reference.Value, field, vehicle: true)?.Design ?? reference;
            }
            PackageLoader.ExcludeDependents(packages);

            void Need(LoadPackage package, string path, C.ContentReference subject, string id, bool present, string field)
            {
                if (!present) package.Reject(known.Contains(id) ? "reference_wrong_kind" : "reference_missing", path, field, "引用不可用：" + id + "。", subject);
            }
            void Good(LoadPackage package, string path, C.ContentReference subject, K.VoxelDefinitionId id, string field)
            {
                Need(package, path, subject, id.Value, voxelIndex.ContainsKey(id), field);
                if (voxelIndex.TryGetValue(id, out var voxel) && voxel.Voxel.Definition.Good is null)
                    package.Reject("reference_wrong_kind", path, field, "引用必须是可库存货物。", subject);
            }
            C.DesignEvaluation.Complete? Design(LoadPackage package, string path, C.ContentReference subject, string id, string field, bool vehicle)
            {
                Need(package, path, subject, id, blueprintIndex.ContainsKey(id), field);
                if (!blueprintIndex.TryGetValue(id, out var blueprint)) return null;
                var design = (C.DesignEvaluation.Complete)blueprint.Evaluation;
                if (vehicle ? design.Core?.Design.Kind is K.DesignKind.Vehicle : design.Core?.Design.Kind is K.DesignKind.Building) return design;
                package.Reject("reference_wrong_kind", path, field, "蓝图类别不符。", subject);
                return null;
            }
            C.SceneModelProjection? Scene(LoadPackage package, string path, C.ContentReference subject, string id, string field)
            {
                Need(package, path, subject, id, blueprintIndex.ContainsKey(id), field);
                if (!blueprintIndex.TryGetValue(id, out var blueprint)) return null;
                if (blueprint.Definition.Specification is C.DesignSpecification.SceneModel && blueprint.Evaluation is C.DesignEvaluation.Complete design)
                    return new(design.Design, design.Scene, blueprint.Definition.Display);
                package.Reject("reference_wrong_kind", path, field, "引用必须是场景模型。", subject);
                return null;
            }
        } while (packages.Count(p => Active(p)) < before);

        return new(_terrains.Where(t => Active(t.Package)).Select(t => t.Value).OrderBy(t => t.Appearance.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _types.Where(t => Active(t.Package)).Select(t => t.Value).OrderBy(t => t.Source.Core.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _templates.Where(t => Active(t.Package)).Select(t => resolvedTemplates[t.Value.Core.Id]).OrderBy(t => t.Core.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _rules.Where(r => Active(r.Package)).Select(r => r.Value).OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            _profiles.Where(p => Active(p.Package)).Select(p => resolvedProfiles[p.Value.Id]).OrderBy(p => p.Id.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    private static bool Active(LoadPackage package) => package.State == C.PackageState.Available;
    private static void RejectDuplicates<T>(List<(LoadPackage Package, T Value)> values, Func<T, string> id, Func<T, string> path)
    {
        foreach (var group in values.Where(v => Active(v.Package)).GroupBy(v => id(v.Value)).Where(g => g.Count() > 1))
            foreach (var (package, value) in group) package.Reject("duplicate_identity", path(value), "/id", "定义身份重复。");
    }
}

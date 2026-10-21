using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal static class WorldSemantics
{
    internal static string Core(ImmutableArray<LoadedVoxel> voxels, WorldDefinitions world,
        ImmutableArray<C.BlueprintRecord> blueprints, ImmutableArray<K.GoodDefinitionProjection> composites,
        ImmutableArray<K.CreatureProjection> creatures)
    {
        var value = JsonSerializer.SerializeToNode(new
        {
            encoding_version = 4,
            voxels = voxels.Select(v => new { id = v.Definition.Definition.Value, revision = v.Definition.Revision.Value }),
            rules = world.Rules.Select(r => new { id = r.Id.Value, revision = r.Revision.Value }),
            processes = world.Processes.Select(p => new { id = p.Id, revision = p.Projection.Revision.Value }),
            types = world.Types.Select(t => new { id = t.Source.Core.Id.Value, revision = t.Source.Core.Revision.Value }),
            templates = world.Templates.Select(t => Template(t, blueprints))
        })!;
        if (!composites.IsEmpty) value["composites"] = JsonSerializer.SerializeToNode(composites.Select(g => new
        { id = ((K.GoodId.Composite)g.Good).Definition.Value, revision = g.Revision.Value }));
        if (!creatures.IsEmpty) value["creatures"] = JsonSerializer.SerializeToNode(creatures.Select(creature => new
        { id = creature.Id.Value, revision = creature.Revision.Value }));
        if (!world.Lairs.IsEmpty) value["lairs"] = JsonSerializer.SerializeToNode(world.Lairs.Select(lair => new
        { id = lair.Core.Id.Value, revision = lair.Core.Revision.Value }));
        return Hash(value);
    }

    internal static K.SemanticRevision Generation(LoadedGenerationProfile profile, WorldDefinitions world,
        ImmutableArray<LoadedVoxel> voxels, ImmutableArray<C.BlueprintRecord> blueprints, ImmutableArray<K.GoodDefinitionProjection> definitionGoods)
    {
        var configuration = profile.Configuration;
        var selectedRules = new[] { configuration.Rules.Population, configuration.Rules.Market, configuration.Rules.Construction,
            configuration.Rules.Railway, configuration.Rules.Combat }.ToHashSet();
        var rules = world.Rules.Where(r => selectedRules.Contains(r.Id)).ToArray();
        var templates = world.Templates;
        var types = world.Types.Where(t => templates.Any(p => p.Core.Type == t.Source.Core.Id)).ToArray();
        var selectedProcesses = templates.SelectMany(t => t.Core.ProductionPlans).Select(p => p.Process).ToHashSet();
        var processes = world.Processes.Where(p => selectedProcesses.Contains(p.Reference)).ToArray();
        var designs = templates.SelectMany(t => t.Core.Plots).Select(p => p.PrebuiltDesign).OfType<K.DesignHash>()
            .Concat(templates.SelectMany(t => t.Core.FoundingBuildings).Select(b => b.Design))
            .Concat(templates.SelectMany(t => t.Source.Inventory).Select(q => q.Reference).OfType<ParsedInventoryReference.Blueprint>()
                .Select(r => ((C.DesignEvaluation.Complete)blueprints.First(b => b.Address == r.Address).Evaluation).Design))
            .Concat(profile.Start.Trains.SelectMany(t => t.Vehicles)).Concat(configuration.NpcInitialization.ForSaleVehicleCandidates)
            .Append(configuration.StartConstraints.RequiredAffordableCommissionDesign).ToHashSet();
        var goods = configuration.Resources.Select(r => r.Good)
            .Concat(rules.SelectMany(r => r.Goods).Select(g => g.Id))
            .Concat(processes.SelectMany(p => p.Goods).Select(g => g.Id))
            .Concat(templates.SelectMany(t => t.Core.SpecialInitialInventory).Select(q => q.Good).OfType<K.GoodId.Voxel>().Select(g => g.Definition))
            .Concat(profile.Start.Trains.SelectMany(t => t.Inventory).Select(q => q.Good).OfType<K.GoodId.Voxel>().Select(g => g.Definition)).ToHashSet();
        var composites = templates.SelectMany(t => t.Core.SpecialInitialInventory).Select(q => q.Good)
            .Concat(profile.Start.Trains.SelectMany(t => t.Inventory).Select(q => q.Good))
            .OfType<K.GoodId.Composite>().Select(g => g.Definition).ToHashSet();
        var byId = blueprints.ToDictionary(b => b.Address.Id.Value, StringComparer.Ordinal);
        foreach (var blueprint in blueprints.Where(b => designs.Contains(((C.DesignEvaluation.Complete)b.Evaluation).Design))) Include(blueprint.Definition);

        void Include(C.DesignDraft draft)
        {
            foreach (var cell in draft.Layout.Cells) goods.Add(draft.Layout.Palette[cell.PaletteIndex].Voxel);
            foreach (var (item, _) in DesignTraversal.Items(draft))
                if (DesignTraversal.Copy(item) is { } copy) Include(copy);
                else if (item is C.NestedDesignItem.FunctionalCompositeReference reference && composites.Add(reference.Definition))
                    Include(byId[reference.Definition.Value].Definition);
        }
        var terrainIds = configuration.TerrainBands.Select(b => b.Terrain)
            .Concat(configuration.Resources.SelectMany(r => r.AllowedTerrainTypes))
            .Concat(processes.SelectMany(p => p.Terrains).Select(t => t.Id))
            .Concat(types.SelectMany(t => t.Source.Core.PreferredTerrain)).Distinct().OrderBy(t => t.Value, StringComparer.Ordinal);
        // DesignHash includes display text and paint; this fingerprint records business composition instead.
        string Composition(K.DesignHash design) => blueprints.Select(b => (C.DesignEvaluation.Complete)b.Evaluation)
            .First(d => d.Design == design).Composition.Value;
        var value = JsonSerializer.SerializeToNode(new
        {
            encoding_version = 2, id = profile.Id.Value,
            configuration = GenerationSemantics.Configuration(configuration, Composition),
            start = GenerationSemantics.Start(profile.Start, Composition),
            terrains = terrainIds.Select(t => t.Value),
            voxels = voxels.Where(v => goods.Contains(v.Definition.Definition))
                .Select(v => new { id = v.Definition.Definition.Value, revision = v.Definition.Revision.Value }),
            rules = rules.Select(r => new { id = r.Id.Value, revision = r.Revision.Value }),
            types = types.Select(t => new { id = t.Source.Core.Id.Value, revision = t.Source.Core.Revision.Value }),
            templates = templates.Select(t => Template(t, blueprints))
        })!;
        if (processes.Length > 0) value["processes"] = JsonSerializer.SerializeToNode(processes.Select(p => new
        { id = p.Id, revision = p.Projection.Revision.Value }));
        if (composites.Count > 0) value["composites"] = JsonSerializer.SerializeToNode(definitionGoods
            .Where(g => g.Good is K.GoodId.Composite c && composites.Contains(c.Definition))
            .OrderBy(g => DesignEvaluator.GoodKey(g.Good), StringComparer.Ordinal)
            .Select(g => new { id = ((K.GoodId.Composite)g.Good).Definition.Value, revision = g.Revision.Value }));
        var lairs = configuration.LairWeights.Select(weight => weight.Type).Distinct()
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => world.Lairs.First(lair => lair.Core.Id == id)).ToArray();
        if (lairs.Length > 0) value["lairs"] = JsonSerializer.SerializeToNode(lairs.Select(lair => new
        { id = lair.Core.Id.Value, revision = lair.Core.Revision.Value }));
        return new(Hash(value));
    }

    private static object Template(LoadedSettlementTemplate template, ImmutableArray<C.BlueprintRecord> blueprints)
    {
        string Composition(C.BlueprintAddress address) => ((C.DesignEvaluation.Complete)blueprints.First(b => b.Address == address).Evaluation).Composition.Value;
        var value = JsonSerializer.SerializeToNode(new
        {
            id = template.Core.Id.Value, type = template.Core.Type.Value, weight = template.Core.Weight,
            platform_length_meters = template.Core.PlatformLengthMeters,
            plots = template.Source.Plots.OrderBy(p => p.Number).Select(p => new
            {
                number = p.Number, size = DesignEvaluator.Wire(p.Size), building = p.Building?.Id.Value,
                composition = p.Building is { } source ? Composition(source) : null
            })
        })!;
        // Omitted initialization fields keep historical empty-template input identities reproducible.
        if (!template.Source.Founding.IsEmpty) value["founding"] = JsonSerializer.SerializeToNode(template.Source.Founding.Select(b => new
        { plot = b.PlotNumber, building = b.Building.Id.Value, composition = Composition(b.Building) }));
        if (!template.Core.SpecialInitialInventory.IsEmpty) value["inventory"] = JsonSerializer.SerializeToNode(template.Core.SpecialInitialInventory.Select(q => new
        { good = DesignEvaluator.GoodKey(q.Good), quantity = q.Quantity }));
        if (!template.Source.ProductionPlans.IsEmpty) value["production_plans"] = JsonSerializer.SerializeToNode(template.Source.ProductionPlans.Select(p => new
        { plot = p.PlotNumber, capability = DesignEvaluator.Wire(p.Capability), process = p.Process, enabled = p.Enabled, priority = p.Priority }));
        return value;
    }

    private static string Hash(object value) => ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, value));
}

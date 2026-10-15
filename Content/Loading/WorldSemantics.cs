using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal static class WorldSemantics
{
    internal static string Core(ImmutableArray<LoadedVoxel> voxels, WorldDefinitions world,
        ImmutableArray<C.BlueprintRecord> blueprints, ImmutableArray<K.GoodDefinitionProjection> composites)
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
        var designs = templates.SelectMany(t => t.Core.Plots).Select(p => p.PrebuiltDesign).OfType<K.DesignHash>()
            .Concat(profile.Start.Trains.SelectMany(t => t.Vehicles)).Concat(configuration.NpcInitialization.ForSaleVehicleCandidates)
            .Append(configuration.StartConstraints.RequiredAffordableCommissionDesign).ToHashSet();
        var goods = configuration.Resources.Select(r => r.Good)
            .Concat(rules.SelectMany(r => r.Goods).Select(g => g.Id))
            .Concat(profile.Start.Trains.SelectMany(t => t.Inventory).Select(q => q.Good).OfType<K.GoodId.Voxel>().Select(g => g.Definition)).ToHashSet();
        var composites = new HashSet<K.CompositeDefinitionId>();
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
        if (composites.Count > 0) value["composites"] = JsonSerializer.SerializeToNode(definitionGoods
            .Where(g => g.Good is K.GoodId.Composite c && composites.Contains(c.Definition))
            .OrderBy(g => DesignEvaluator.GoodKey(g.Good), StringComparer.Ordinal)
            .Select(g => new { id = ((K.GoodId.Composite)g.Good).Definition.Value, revision = g.Revision.Value }));
        return new(Hash(value));
    }

    private static object Template(LoadedSettlementTemplate template, ImmutableArray<C.BlueprintRecord> blueprints) => new
    {
        id = template.Core.Id.Value, type = template.Core.Type.Value, weight = template.Core.Weight,
        plots = template.Source.Plots.OrderBy(p => p.Number).Select(p => new
        {
            number = p.Number, size = DesignEvaluator.Wire(p.Size), building = p.Building?.Id.Value,
            composition = p.Building is { } source
                ? ((C.DesignEvaluation.Complete)blueprints.First(b => b.Address == source).Evaluation).Composition.Value : null
        })
    };

    private static string Hash(object value) => ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, value));
}

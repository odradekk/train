using System.Collections.Immutable;
using System.Globalization;
using Train.Core.Contracts;
using Train.Core.Sessions;
using static Train.Core.World.WorldReferences;

namespace Train.Core.World;

internal static class WorldContentPublication
{
    internal static CommandEffect Execute(WorldState world, CoreContentProjection before, CoreContentProjection content)
    {
        var problems = ImmutableArray.CreateBuilder<CoreProblem>();
        var normalized = WorldInputs.NormalizeContent(content);
        var suppliedDesigns = normalized.Designs.ToDictionary(d => d.Design);
        var designs = ImmutableArray.CreateBuilder<DesignProjection>();
        foreach (var saved in world.Designs)
        {
            var path = $"World.Designs[{saved.Design.Value}]";
            if (!suppliedDesigns.TryGetValue(saved.Design, out var design))
            {
                Problem("content_missing", path, saved.Design.Value);
                continue;
            }
            if (saved.Composition != design.Composition) Problem("invalid_state", path + ".Composition", saved.Design.Value);
            if (!SameKind(saved.Kind, design.Kind)) Problem("invalid_state", path + ".Kind", saved.Design.Value);
            var parts = design.Parts.ToDictionary(p => p.Id);
            foreach (var part in saved.Parts)
                if (!parts.TryGetValue(part.Id, out var replacement) || part.Definition != replacement.Definition)
                    Problem("invalid_state", path + $".Parts[{part.Id.Value}].Definition", DefinitionKey(part.Definition));
            foreach (var part in design.Parts)
                if (!saved.Parts.Any(p => p.Id == part.Id))
                    Problem("invalid_state", path + $".Parts[{part.Id.Value}].Definition", DefinitionKey(part.Definition));
            foreach (var unit in design.ReturnUnits)
                foreach (var id in unit.Parts.Where(id => !parts.ContainsKey(id)))
                    Problem("invalid_state", path + $".ReturnUnits[{GoodKey(unit.Good)}].Parts[{id.Value}]", GoodKey(unit.Good));
            designs.Add(design);
        }

        var suppliedGoods = normalized.DesignGoods.ToDictionary(g => g.Good);
        var requiredGoods = world.DesignGoods.Select(g => g.Good).Concat(designs.SelectMany(DesignGoods)
            .Select(g => g.Good).OfType<GoodId.Assembly>()).Distinct();
        var goods = ImmutableArray.CreateBuilder<GoodDefinitionProjection>();
        foreach (var good in requiredGoods)
        {
            if (suppliedGoods.TryGetValue(good, out var specification))
                goods.Add(specification);
            else
                Problem("content_missing", $"World.DesignGoods[{GoodKey(good)}]", GoodKey(good));
        }
        var adapted = world with
        {
            Designs = designs.OrderBy(d => d.Design.Value, StringComparer.Ordinal).ToImmutableArray(),
            DesignGoods = goods.OrderBy(g => GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray(),
            DefinitionDependencies = []
        };
        adapted = adapted with { DefinitionDependencies = Dependencies(adapted, content) };
        var oldReferences = Create(world with
        {
            DefinitionDependencies = world.DefinitionDependencies.AddRange(Dependencies(world, before))
        }, before).Definitions;
        var alreadyMissing = oldReferences.Where(d => d.WasMissing).Select(d => (d.Definition, d.Field)).ToHashSet();
        var references = Create(adapted, content).Definitions;
        foreach (var reference in references)
        {
            // Retired consumption remains physical state; only current or executing goods must still exist.
            if (reference.Field.EndsWith(".PopulationState.ConsumptionRemaining", StringComparison.Ordinal)) continue;
            if (reference.WasMissing
                ? !alreadyMissing.Contains((reference.Definition, reference.Field))
                : !Exists(reference, content))
                Problem("content_missing", reference.Field, DefinitionKey(reference.Definition));
            if (reference.Definition is CoreDefinitionReference.Process process)
            {
                var old = before.Processes.SingleOrDefault(p => Matches(p.Details, process.Id));
                var current = content.Processes.SingleOrDefault(p => Matches(p.Details, process.Id));
                if (old is not null && current is not null
                    && (old.Details is ProcessDetails.Extraction) != (current.Details is ProcessDetails.Extraction))
                    Problem("content_missing", reference.Field, DefinitionKey(reference.Definition));
            }
        }

        if (problems.Count != 0)
            return new CommandEffect.Rejected(problems.OrderBy(p => p.Field, StringComparer.Ordinal)
                .ThenBy(p => p.RelatedIdentities[0], StringComparer.Ordinal).ToImmutableArray());

        var instances = adapted.Instances.ToDictionary(i => i.Id);
        var specifications = adapted.Designs.ToDictionary(d => d.Design);
        var platforms = adapted.Settlements.ToDictionary(s => s.Node, s => WorldState.CountPlatforms(s, instances, specifications));
        adapted = adapted with
        {
            Nodes = adapted.Nodes.Select(n => platforms.TryGetValue(n.Id, out var count) ? n with { Platforms = count } : n).ToImmutableArray()
        };
        return new CommandEffect.Accepted(content, [], adapted);

        void Problem(string code, string field, string identity) => problems.Add(new(code, field, Subject(field), [identity]));
    }

    private static bool SameKind(DesignKind before, DesignKind after) => (before, after) switch
    {
        (DesignKind.Vehicle a, DesignKind.Vehicle b) => a.Size == b.Size && a.LengthCells == b.LengthCells,
        (DesignKind.Building a, DesignKind.Building b) => a.PlotSize == b.PlotSize && a.Type == b.Type
            && a.FacilityTags.Order().SequenceEqual(b.FacilityTags.Order()),
        (DesignKind.LargeEquipment a, DesignKind.LargeEquipment b) => a.ManufacturedGood == b.ManufacturedGood,
        _ => false
    };

    private static bool Exists(RequiredDefinition reference, CoreContentProjection content) => reference.Definition switch
    {
        CoreDefinitionReference.Voxel v => content.Goods.Any(g => g.Good == new GoodId.Voxel(v.Id)),
        CoreDefinitionReference.Composite c => content.Goods.Any(g => g.Good == new GoodId.Composite(c.Id)),
        CoreDefinitionReference.Process p => content.Processes.Any(d => Matches(d.Details, p.Id)),
        CoreDefinitionReference.Rule r => reference.Field switch
        {
            "Configuration.Rules.Population" => content.PopulationRules.Any(d => d.Id == r.Id),
            "Configuration.Rules.Market" => content.MarketRules.Any(d => d.Id == r.Id),
            "Configuration.Rules.Construction" => content.ConstructionRules.Any(d => d.Id == r.Id),
            "Configuration.Rules.Railway" => content.RailwayRules.Any(d => d.Id == r.Id),
            "Configuration.Rules.Combat" => content.CombatRules.Any(d => d.Id == r.Id),
            _ => content.PopulationRules.Any(d => d.Id == r.Id) || content.MarketRules.Any(d => d.Id == r.Id)
                || content.ConstructionRules.Any(d => d.Id == r.Id) || content.RailwayRules.Any(d => d.Id == r.Id)
                || content.CombatRules.Any(d => d.Id == r.Id)
        },
        CoreDefinitionReference.SettlementType s => content.SettlementTypes.Any(d => d.Id == s.Id),
        CoreDefinitionReference.Lair l => content.LairTypes.Any(d => d.Id == l.Id),
        CoreDefinitionReference.Creature c => content.Creatures.Any(d => d.Id == c.Id),
        CoreDefinitionReference.Terrain => true,
        _ => false
    };

    private static WorldObjectId? Subject(string field)
    {
        var start = field.IndexOf('[');
        var end = field.IndexOf(']');
        if (start < 0 || end < start || !ulong.TryParse(field.AsSpan(start + 1, end - start - 1),
            NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        return field[..start] switch
        {
            "World.Nodes" => new WorldObjectId.Node(new(id)),
            "World.Edges" => new WorldObjectId.Edge(new(id)),
            "World.Settlements" => new WorldObjectId.Settlement(new(id)),
            "World.Lairs" => new WorldObjectId.Lair(new(id)),
            "World.Inventories" => new WorldObjectId.Inventory(new(id)),
            _ => null
        };
    }
}

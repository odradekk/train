using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldReferences
{
    internal static CoreReferenceManifest Create(WorldState? world, CoreContentProjection content)
    {
        if (world is null) return new([], [], [], [content.Identity]);
        var definitions = ImmutableArray.CreateBuilder<RequiredDefinition>();
        foreach (var node in world.Nodes)
        {
            Add(new CoreDefinitionReference.Terrain(node.Terrain.TypeId), $"World.Nodes[{node.Id.Value}].Terrain");
            foreach (var resource in node.Resources) Good(resource.Good, $"World.Nodes[{node.Id.Value}].Resources[{GoodKey(resource.Good)}]");
        }
        foreach (var edge in world.Edges) Add(new CoreDefinitionReference.Terrain(edge.Terrain.TypeId), $"World.Edges[{edge.Id.Value}].Terrain");
        foreach (var settlement in world.Settlements)
        {
            var path = $"World.Settlements[{settlement.Id.Value}]";
            Add(new CoreDefinitionReference.SettlementType(settlement.Type), path + ".Type");
            foreach (var plan in settlement.Template.ProductionPlans)
                Add(new CoreDefinitionReference.Process(plan.Process), path + $".Template.ProductionPlans[{plan.PlotNumber},{plan.Capability}].Process");
        }
        foreach (var lair in world.Lairs) Add(new CoreDefinitionReference.Lair(lair.Type), $"World.Lairs[{lair.Id.Value}].Type");
        foreach (var design in world.Designs)
        {
            var path = $"World.Designs[{design.Design.Value}]";
            if (design.Kind is DesignKind.LargeEquipment equipment) Good(equipment.ManufacturedGood, path + ".Kind.ManufacturedGood");
            foreach (var part in design.Parts)
            {
                Add(part.Definition, path + $".Parts[{part.Id.Value}].Definition");
                Good(part.RepairGood, path + $".Parts[{part.Id.Value}].RepairGood");
            }
            foreach (var good in design.StructuralGoods) Good(good.Good, path + $".StructuralGoods[{GoodKey(good.Good)}]");
            foreach (var good in design.BillOfMaterials) Good(good.Good, path + $".BillOfMaterials[{GoodKey(good.Good)}]");
            foreach (var unit in design.ReturnUnits)
            {
                Good(unit.Good, path + $".ReturnUnits[{GoodKey(unit.Good)}]");
                foreach (var good in unit.StructuralGoods) Good(good.Good, path + $".ReturnUnits.StructuralGoods[{GoodKey(good.Good)}]");
            }
        }
        foreach (var inventory in world.Inventories)
            foreach (var good in inventory.Goods) Good(good.Good, $"World.Inventories[{inventory.Id.Value}].Goods[{GoodKey(good.Good)}]");
        var config = world.Source.Configuration;
        var rules = config.Rules;
        Add(new CoreDefinitionReference.Rule(rules.Population), "Configuration.Rules.Population");
        Add(new CoreDefinitionReference.Rule(rules.Market), "Configuration.Rules.Market");
        Add(new CoreDefinitionReference.Rule(rules.Construction), "Configuration.Rules.Construction");
        Add(new CoreDefinitionReference.Rule(rules.Railway), "Configuration.Rules.Railway");
        Add(new CoreDefinitionReference.Rule(rules.Combat), "Configuration.Rules.Combat");
        foreach (var band in config.TerrainBands) Add(new CoreDefinitionReference.Terrain(band.Terrain), $"Configuration.TerrainBands[{band.Terrain.Value}].Terrain");
        foreach (var resource in config.Resources)
        {
            Add(new CoreDefinitionReference.Voxel(resource.Good), $"Configuration.Resources[{resource.Good.Value}].Good");
            foreach (var type in resource.AllowedTerrainTypes) Add(new CoreDefinitionReference.Terrain(type), $"Configuration.Resources[{resource.Good.Value}].AllowedTerrainTypes[{type.Value}]");
        }
        foreach (var weight in config.LairWeights) Add(new CoreDefinitionReference.Lair(weight.Type), $"Configuration.LairWeights[{weight.Type.Value}].Type");
        foreach (var definition in definitions.Select(d => d.Definition).Distinct().ToArray())
        {
            foreach (var (good, field) in DependencyGoods(definition, content)) Good(good, field);
            foreach (var (dependency, field, _) in DependencyDefinitions(definition, content)) Add(dependency, field);
        }
        var missing = content.MissingReferences.Select(m => m.Reference).ToHashSet();
        return new(world.Designs.Select(d => d.Design).OrderBy(d => d.Value, StringComparer.Ordinal).ToImmutableArray(),
            world.Settlements.OrderBy(s => s.Id.Value).Select(s => new OwnedSettlementTemplateReference(s.Id, s.Template.Id,
                s.Template.Plots.Select(p => new TemplatePlotReference(p.Number, p.Size)).ToImmutableArray())).ToImmutableArray(),
            definitions.Distinct().OrderBy(d => DefinitionKey(d.Definition), StringComparer.Ordinal).ThenBy(d => d.Field, StringComparer.Ordinal)
                .Select(d => d with { WasMissing = missing.Contains(d.Definition) }).ToImmutableArray(), [content.Identity]);

        void Add(CoreDefinitionReference definition, string field) => definitions.Add(new(definition, null, field, false));
        void Good(GoodId good, string field)
        {
            if (good is GoodId.Voxel voxel) Add(new CoreDefinitionReference.Voxel(voxel.Definition), field);
            if (good is GoodId.Composite composite) Add(new CoreDefinitionReference.Composite(composite.Definition), field);
        }
    }

    internal static IEnumerable<(GoodId Good, string Field)> DependencyGoods(CoreDefinitionReference reference,
        CoreContentProjection content)
    {
        if (reference is CoreDefinitionReference.Rule rule)
        {
            foreach (var population in content.PopulationRules.Where(r => r.Id == rule.Id))
                foreach (var good in population.ConsumptionGoods)
                    yield return (good.Good, $"Content.PopulationRules[{rule.Id.Value}].ConsumptionGoods");
            foreach (var market in content.MarketRules.Where(r => r.Id == rule.Id))
                foreach (var good in market.GoodPrices)
                    yield return (good.Good, $"Content.MarketRules[{rule.Id.Value}].GoodPrices");
            foreach (var railway in content.RailwayRules.Where(r => r.Id == rule.Id))
                yield return (new GoodId.Voxel(railway.RailGood), $"Content.RailwayRules[{rule.Id.Value}].RailGood");
        }
        if (reference is CoreDefinitionReference.Process process)
            foreach (var projection in content.Processes.Where(p => Matches(p.Details, process.Id)))
            {
                var path = $"Content.Processes[{DefinitionKey(reference)}]";
                var goods = projection.Details switch
                {
                    ProcessDetails.Production p => p.Inputs.Concat(p.Outputs),
                    ProcessDetails.Recovery p => p.Inputs.Concat(p.Outputs),
                    ProcessDetails.Extraction p => [p.Extracts],
                    _ => throw new InvalidOperationException("流程种类非法。")
                };
                foreach (var good in goods) yield return (good.Good, path + ".Details");
            }
    }

    internal static IEnumerable<(CoreDefinitionReference Definition, string Field, bool Exists)> DependencyDefinitions(
        CoreDefinitionReference reference, CoreContentProjection content)
    {
        if (reference is CoreDefinitionReference.Process process)
            foreach (var projection in content.Processes.Where(p => Matches(p.Details, process.Id)))
                foreach (var terrain in projection.AllowedTerrainTypes)
                    yield return (new CoreDefinitionReference.Terrain(terrain), $"Content.Processes[{DefinitionKey(reference)}].AllowedTerrainTypes", true);
        if (reference is CoreDefinitionReference.SettlementType type)
            foreach (var projection in content.SettlementTypes.Where(t => t.Id == type.Id))
                foreach (var terrain in projection.PreferredTerrain)
                    yield return (new CoreDefinitionReference.Terrain(terrain), $"Content.SettlementTypes[{type.Id.Value}].PreferredTerrain", true);
        if (reference is CoreDefinitionReference.Lair lair)
            foreach (var projection in content.LairTypes.Where(l => l.Id == lair.Id))
                foreach (var member in projection.Members)
                    yield return (new CoreDefinitionReference.Creature(member.Creature), $"Content.LairTypes[{lair.Id.Value}].Members",
                        content.Creatures.Any(c => c.Id == member.Creature));
    }

    internal static bool Matches(ProcessDetails details, ProcessRef reference) => (details, reference) switch
    {
        (ProcessDetails.Production p, ProcessRef.Production r) => p.Id == r.Definition,
        (ProcessDetails.Extraction p, ProcessRef.Production r) => p.Id == r.Definition,
        (ProcessDetails.Recovery p, ProcessRef.Recovery r) => p.Id == r.Definition,
        _ => false
    };

    internal static string GoodKey(GoodId good) => good switch
    {
        GoodId.Voxel v => "voxel:" + v.Definition.Value,
        GoodId.Composite c => "composite:" + c.Definition.Value,
        GoodId.Assembly a => "assembly:" + a.Composition.Value,
        _ => throw new ArgumentException("货物种类非法。", nameof(good))
    };

    internal static string DefinitionKey(CoreDefinitionReference definition) => definition switch
    {
        CoreDefinitionReference.Voxel v => "voxel:" + v.Id.Value,
        CoreDefinitionReference.Composite c => "composite:" + c.Id.Value,
        CoreDefinitionReference.Process { Id: ProcessRef.Production p } => "production:" + p.Definition.Value,
        CoreDefinitionReference.Process { Id: ProcessRef.Recovery r } => "recovery:" + r.Definition.Value,
        CoreDefinitionReference.Rule r => "rule:" + r.Id.Value,
        CoreDefinitionReference.SettlementType s => "settlement:" + s.Id.Value,
        CoreDefinitionReference.Template t => "template:" + t.Id.Value,
        CoreDefinitionReference.Terrain t => "terrain:" + t.Id.Value,
        CoreDefinitionReference.Creature c => "creature:" + c.Id.Value,
        CoreDefinitionReference.Lair l => "lair:" + l.Id.Value,
        _ => throw new ArgumentException("引用种类非法。", nameof(definition))
    };
}

using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldReferences
{
    internal static CoreReferenceManifest Create(WorldState world, CoreContentProjection content)
    {
        var usedDesigns = world.Designs.Select(d => d.Design).ToHashSet();
        var missing = content.MissingReferences.Concat(content.Designs.Where(d => usedDesigns.Contains(d.Design))
            .SelectMany(d => d.MissingReferences.Concat(d.ReturnUnits.SelectMany(u => u.MissingReferences))
                .Concat(d.Parts.Where(p => p.Missing).Select(p => new MissingCoreReference(p.Definition, "")))))
            .ToImmutableArray();
        var populationRule = world.Source.Configuration.Rules.Population;
        if (!content.PopulationRules.Any(r => r.Id == populationRule))
            missing = missing.Add(new(new CoreDefinitionReference.Rule(populationRule), "Configuration.Rules.Population"));
        var populationGoods = content.PopulationRules.Where(r => r.Id == populationRule).SelectMany(r => r.ConsumptionGoods).Select(g => g.Good)
            .Concat(world.Settlements.SelectMany(s => s.PopulationState.Period?.Rule.ConsumptionGoods.Select(g => g.Good) ?? []))
            .Concat(world.Settlements.SelectMany(s => s.PopulationState.ConsumptionRemaining).Select(r => r.Good));
        missing = missing.AddRange(populationGoods.Where(g => WorldGoods.DefinitionMissing(world, content, g))
            .Select(GoodReference).OfType<CoreDefinitionReference>()
            .Select(r => new MissingCoreReference(r, "")));
        return Create(world, content.Identity, missing);
    }

    internal static CoreReferenceManifest Create(WorldState world, CoreInputId identity,
        ImmutableArray<MissingCoreReference> missingReferences)
    {
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
            if (settlement.PopulationState.Period is { } period)
            {
                definitions.Add(new(new CoreDefinitionReference.Rule(period.Rule.Id), period.Rule.Revision,
                    path + ".PopulationState.Period.Rule", false));
                foreach (var good in period.Rule.ConsumptionGoods)
                    Good(good.Good, path + ".PopulationState.Period.Rule.ConsumptionGoods");
            }
            foreach (var remainder in settlement.PopulationState.ConsumptionRemaining)
                Good(remainder.Good, path + ".PopulationState.ConsumptionRemaining");
            foreach (var facility in settlement.ProductionFacilities)
            {
                var field = path + $".ProductionFacilities[{facility.Facility.Building.Value}:{facility.Facility.Capability}]";
                if (facility.Batch is { } batch)
                {
                    Add(new CoreDefinitionReference.Process(batch.Process), field + ".Batch.Process");
                    foreach (var good in batch.FrozenOutputs)
                        Good(good.Good, field + $".Batch.FrozenOutputs[{GoodKey(good.Good)}]");
                }
                foreach (var good in facility.OutputBuffer)
                    Good(good.Good, field + $".OutputBuffer[{GoodKey(good.Good)}]");
            }
        }
        foreach (var lair in world.Lairs) Add(new CoreDefinitionReference.Lair(lair.Type), $"World.Lairs[{lair.Id.Value}].Type");
        foreach (var job in world.ConstructionJobs)
        {
            var path = $"World.ConstructionJobs[{job.Id.Value}]";
            definitions.Add(new(new CoreDefinitionReference.Rule(job.Rule), job.StartedFromRevision, path + ".Rule", false));
            foreach (var good in job.InvestedGoods) Good(good.Good, path + ".InvestedGoods");
        }
        foreach (var job in world.FoundingJobs)
        {
            var path = $"World.FoundingJobs[{job.Id.Value}]";
            Add(new CoreDefinitionReference.SettlementType(job.Type), path + ".Type");
            definitions.Add(new(new CoreDefinitionReference.Rule(job.Rule), job.StartedFromRevision, path + ".Rule", false));
            foreach (var good in job.InvestedGoods) Good(good.Good, path + ".InvestedGoods");
        }
        foreach (var train in world.Trains)
        {
            if (train.Plan is not { } plan) continue;
            for (var index = 0; index < plan.Stops.Length; index++)
                if (plan.Stops[index] is PlanStop.Station station)
                    for (var ruleIndex = 0; ruleIndex < station.GoodsRules.Length; ruleIndex++)
                        Good(station.GoodsRules[ruleIndex].Good, $"World.Trains[{train.Id.Value}].Plan.Stops[{index}].GoodsRules[{ruleIndex}].Good");
        }
        foreach (var design in world.Designs)
        {
            var path = $"World.Designs[{design.Design.Value}]";
            foreach (var part in design.Parts) Add(part.Definition, path + $".Parts[{part.Id.Value}].Definition");
            foreach (var (good, field) in DesignGoods(design)) Good(good, path + field);
        }
        foreach (var inventory in world.Inventories)
        {
            foreach (var good in inventory.Goods) Good(good.Good, $"World.Inventories[{inventory.Id.Value}].Goods[{GoodKey(good.Good)}]");
            foreach (var reservation in inventory.Reservations)
                foreach (var good in reservation.ExpectedReturns)
                    Good(good.Good, $"World.Inventories[{inventory.Id.Value}].Reservations[{reservation.Job.Value}].ExpectedReturns[{GoodKey(good.Good)}]");
        }
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
        definitions.AddRange(world.DefinitionDependencies);
        var missing = missingReferences.Select(m => m.Reference).ToHashSet();
        return new(world.Designs.Select(d => d.Design).OrderBy(d => d.Value, StringComparer.Ordinal).ToImmutableArray(),
            world.Settlements.Select(s => new OwnedSettlementTemplateReference(s.Id, s.Template.Id,
                s.Template.Plots.Select(p => new TemplatePlotReference(p.Number, p.Size)).ToImmutableArray()))
                .Concat(world.FoundingJobs.Select(j => new OwnedSettlementTemplateReference(j.ReservedSettlementId, j.Template.Id,
                    j.Template.Plots.Select(p => new TemplatePlotReference(p.Number, p.Size)).ToImmutableArray())))
                .OrderBy(t => t.Owner.Value).ToImmutableArray(),
            definitions.Distinct().OrderBy(d => DefinitionKey(d.Definition), StringComparer.Ordinal).ThenBy(d => d.Field, StringComparer.Ordinal)
                .Select(d => d with { WasMissing = missing.Contains(d.Definition) }).ToImmutableArray(), [identity]);

        void Add(CoreDefinitionReference definition, string field) => definitions.Add(new(definition, null, field, false));
        void Good(GoodId good, string field)
        {
            if (GoodReference(good) is { } reference) Add(reference, field);
        }
    }

    internal static ImmutableArray<RequiredDefinition> Dependencies(WorldState world, CoreContentProjection content)
    {
        var result = ImmutableArray.CreateBuilder<RequiredDefinition>();
        foreach (var reference in Create(world, content).Definitions.Select(d => d.Definition).Distinct())
        {
            foreach (var (good, field) in DependencyGoods(reference, content))
            {
                if (GoodReference(good) is { } dependency) result.Add(new(dependency, null, field, false));
            }
            foreach (var (dependency, field, _) in DependencyDefinitions(reference, content)) result.Add(new(dependency, null, field, false));
        }
        return result.Distinct().OrderBy(d => DefinitionKey(d.Definition), StringComparer.Ordinal)
            .ThenBy(d => d.Field, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static WorldState RestorePopulationDependencies(WorldState world, CoreContentProjection content)
    {
        var selected = world.Source.Configuration.Rules.Population;
        var field = $"Content.PopulationRules[{selected.Value}].ConsumptionGoods";
        var current = DependencyGoods(new CoreDefinitionReference.Rule(selected), content)
            .Select(g => GoodReference(g.Good)).OfType<CoreDefinitionReference>()
            .Select(g => new RequiredDefinition(g, null, field, false));
        var dependencies = world.DefinitionDependencies.Where(d => d.Field != field).Concat(current).Distinct()
            .OrderBy(d => DefinitionKey(d.Definition), StringComparer.Ordinal).ThenBy(d => d.Field, StringComparer.Ordinal).ToImmutableArray();
        return dependencies.SequenceEqual(world.DefinitionDependencies) ? world : world with { DefinitionDependencies = dependencies };
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
                foreach (var good in ProcessGoods(projection.Details)) yield return (good, path + ".Details");
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

    internal static IEnumerable<(GoodId Good, string Field)> DesignGoods(DesignProjection design)
    {
        if (design.Kind is DesignKind.LargeEquipment equipment) yield return (equipment.ManufacturedGood, ".Kind.ManufacturedGood");
        foreach (var part in design.Parts) yield return (part.RepairGood, $".Parts[{part.Id.Value}].RepairGood");
        foreach (var good in design.StructuralGoods) yield return (good.Good, $".StructuralGoods[{GoodKey(good.Good)}]");
        foreach (var good in design.BillOfMaterials) yield return (good.Good, $".BillOfMaterials[{GoodKey(good.Good)}]");
        foreach (var unit in design.ReturnUnits)
        {
            yield return (unit.Good, $".ReturnUnits[{GoodKey(unit.Good)}]");
            foreach (var good in unit.StructuralGoods) yield return (good.Good, $".ReturnUnits.StructuralGoods[{GoodKey(good.Good)}]");
        }
    }

    internal static IEnumerable<GoodId> ProcessGoods(ProcessDetails details) => details switch
    {
        ProcessDetails.Production p => p.Inputs.Concat(p.Outputs).Select(g => g.Good),
        ProcessDetails.Recovery p => p.Inputs.Concat(p.Outputs).Select(g => g.Good),
        ProcessDetails.Extraction p => [p.Extracts.Good],
        _ => throw new ArgumentException("Invalid process details.")
    };

    internal static CoreDefinitionReference? GoodReference(GoodId good) => good switch
    {
        GoodId.Voxel v => new CoreDefinitionReference.Voxel(v.Definition),
        GoodId.Composite c => new CoreDefinitionReference.Composite(c.Definition),
        GoodId.Assembly => null,
        _ => throw new ArgumentException("Invalid good identity.")
    };

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

using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldOccupants
{
    internal static ImmutableArray<GeneratedOccupant> Build(ImmutableArray<CandidateNode> nodes,
        ImmutableArray<CandidateEdge> edges, WorldGenerationInput input, CoreContentProjection content,
        GenerationBudgetCounter budget, ulong attempt, bool preserveConnectivity)
    {
        budget.Stage = "occupants";
        var config = input.Configuration;
        var settlements = (int)Math.Floor(nodes.Length * config.SettlementRatio);
        var lairs = (int)Math.Floor(nodes.Length * config.LairRatio);
        if (settlements == 0) throw new GenerationFailure("constraint_unsatisfied", "Configuration.SettlementRatio");
        if (lairs > 0 && config.LairWeights.IsEmpty) throw new GenerationFailure("config_invalid", "Configuration.LairWeights");
        var result = Enumerable.Range(0, nodes.Length).Select(_ => (GeneratedOccupant)new GeneratedOccupant.Empty()).ToArray();
        budget.Sort(nodes.Length);
        var order = Enumerable.Range(0, nodes.Length).OrderBy(i => Random("occupant.rank", i)).ThenBy(i => i).ToArray();
        var hostileCount = (int)Math.Floor(settlements * config.HostileSettlementRatio);
        budget.Sort(settlements);
        var hostile = order.Take(settlements).OrderBy(i => Random("settlement.relation", i)).ThenBy(i => i).Take(hostileCount).ToHashSet();
        foreach (var index in order.Take(settlements))
        {
            budget.Candidate = nodes[index].Cell.ToString();
            var (type, template) = SettlementSelection.Choose(content, nodes[index].Terrain.TypeId,
                Random("settlement.type", index), type => Random("settlement.template", index, WorldRandom.Identity(type.Value)))!.Value;
            var population = WorldRandom.Integer(Random("settlement.population", index, WorldRandom.Identity(type.Id.Value)),
                type.MinimumInitialPopulation, type.MaximumInitialPopulation);
            var platforms = template.Plots.Where(p => p.PrebuiltDesign is not null).Select(p => content.Designs.Single(d => d.Design == p.PrebuiltDesign))
                .Where(d => d.Kind is DesignKind.Building { Type: BuildingKind.Station } && d.MaxStructureDurability > 0)
                .Sum(d => d.Parts.Count(p => p.MaxDurability > 0 && p.Capabilities.Any(c => c is PartCapability.Platform)));
            result[index] = new GeneratedOccupant.Settlement(type, template,
                hostile.Contains(index) ? SettlementRelation.Hostile : SettlementRelation.NonHostile, population, platforms);
        }
        var placed = 0;
        foreach (var index in order.Skip(settlements))
        {
            if (placed == lairs) break;
            budget.Candidate = nodes[index].Cell.ToString();
            if (preserveConnectivity) budget.CandidateCount();
            var lair = SettlementSelection.Weighted(config.LairWeights.ToArray(), l => l.Weight, Random("lair.type", index));
            result[index] = new GeneratedOccupant.Lair(lair.Type);
            if (preserveConnectivity)
            {
                var usable = WorldRailway.UsableEdges(edges, result, config.InitialRailway, budget);
                var traversal = WorldTraversal.Build(nodes.Length, usable.Select(i => (edges[i].A, edges[i].B)), budget);
                var reached = traversal.Reachable(order[0]).Nodes;
                budget.Work(settlements);
                if (!order.Take(settlements).All(reached.Contains))
                {
                    result[index] = new GeneratedOccupant.Empty();
                    continue;
                }
            }
            placed++;
        }
        if (placed != lairs) throw new GenerationFailure("constraint_unsatisfied", "Configuration.LairRatio");
        return result.ToImmutableArray();

        ulong Random(string purpose, int index, params ulong[] identity)
            => WorldRandom.Sample(input.WorldSeed, purpose, 4, budget.WorldRetry, attempt, [nodes[index].Cell.X, nodes[index].Cell.Y, .. identity]);
    }
}

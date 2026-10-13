using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldRailway
{
    internal static ImmutableArray<int> Build(ImmutableArray<CandidateNode> nodes, ImmutableArray<CandidateEdge> edges,
        ImmutableArray<GeneratedOccupant> occupants, WorldGenerationInput input, GenerationBudgetCounter budget)
    {
        budget.Stage = "railway";
        var config = input.Configuration.InitialRailway;
        var mandatory = Enumerable.Range(0, nodes.Length).Where(i => occupants[i] is GeneratedOccupant.Settlement).ToArray();
        if (mandatory.Length == 0) throw new GenerationFailure("constraint_unsatisfied", "Configuration.SettlementRatio");
        var minimumEmpty = input.Configuration.StartConstraints.MinimumReachableEmptyNodes;
        budget.Work(edges.Length);
        var usable = Enumerable.Range(0, edges.Length).Where(i => occupants[edges[i].A] is not GeneratedOccupant.Lair
            && occupants[edges[i].B] is not GeneratedOccupant.Lair
            && Math.Max(1, config.MinimumInitialTracks) <= Math.Min(2, Math.Min(config.MaximumInitialTracks, edges[i].TrackLimit))).ToArray();
        var traversal = WorldTraversal.Build(nodes.Length, usable.Select(i => (edges[i].A, edges[i].B)), budget);
        var component = traversal.Reachable(mandatory[0]);
        var selected = component.Nodes;
        if (mandatory.Any(i => !selected.Contains(i)))
            throw new GenerationFailure("constraint_unsatisfied", "Configuration.InitialRailway");
        if (component.Edges - selected.Count + 1 < config.AdditionalCycleEdges)
            throw new GenerationFailure("constraint_unsatisfied", "Configuration.InitialRailway.AdditionalCycleEdges");
        var empty = selected.Where(i => occupants[i] is GeneratedOccupant.Empty).ToArray();
        if (empty.Length < minimumEmpty)
            throw new GenerationFailure("constraint_unsatisfied", "Configuration.StartConstraints.MinimumReachableEmptyNodes");
        budget.Sort(empty.Length);
        empty = empty.OrderBy(i => nodes[i].Cell.Y).ThenBy(i => nodes[i].Cell.X).ToArray();
        var emptyCount = empty.Length;
        bool removed;
        do
        {
            removed = false;
            foreach (var node in empty)
            {
                budget.Work();
                if (!selected.Contains(node)) continue;
                budget.Candidate = "rail:remove:" + nodes[node].Cell;
                budget.CandidateCount();
                if (emptyCount == minimumEmpty) continue;
                selected.Remove(node);
                var remaining = traversal.Reachable(mandatory[0], selected);
                if (remaining.Nodes.Count == selected.Count && remaining.Edges - selected.Count + 1 >= config.AdditionalCycleEdges)
                { emptyCount--; removed = true; }
                else
                    selected.Add(node);
            }
        } while (removed);
        budget.Candidate = "";
        budget.Work(usable.Length);
        var selectedEdges = usable.Where(i => selected.Contains(edges[i].A) && selected.Contains(edges[i].B)).ToList();
        budget.Sort(selectedEdges.Count);
        selectedEdges.Sort((a, b) => WorldGraph.Compare(edges[a], edges[b]));
        var parents = Enumerable.Range(0, nodes.Length).ToArray();
        var tree = new HashSet<int>();
        foreach (var index in selectedEdges)
        {
            budget.Work();
            var a = Root(edges[index].A);
            var b = Root(edges[index].B);
            if (a != b) { parents[b] = a; tree.Add(index); }
        }
        var cycles = selectedEdges.Where(i => !tree.Contains(i)).Select(i => (Index: i, Score: Random("rail.cycle", edges[i]))).ToList();
        budget.Sort(cycles.Count);
        foreach (var cycle in cycles.OrderBy(c => c.Score).ThenBy(c => edges[c.Index].A).ThenBy(c => edges[c.Index].B).Take(config.AdditionalCycleEdges))
            tree.Add(cycle.Index);
        var tracks = new int[edges.Length];
        foreach (var index in tree.Order())
            tracks[index] = (int)WorldRandom.Integer(Random("rail.tracks", edges[index]), Math.Max(1, config.MinimumInitialTracks),
                Math.Min(2, Math.Min(config.MaximumInitialTracks, edges[index].TrackLimit)));
        return tracks.ToImmutableArray();

        int Root(int index)
        {
            while (parents[index] != index) { budget.Work(); index = parents[index]; }
            return index;
        }

        ulong Random(string purpose, CandidateEdge edge)
        {
            var a = nodes[edge.A].Cell;
            var b = nodes[edge.B].Cell;
            return WorldRandom.Sample(input.WorldSeed, purpose, 6, budget.WorldRetry, 0, a.X, a.Y, b.X, b.Y);
        }
    }
}

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
        var empty = Enumerable.Range(0, nodes.Length).Where(i => occupants[i] is GeneratedOccupant.Empty).ToArray();
        if (mandatory.Length == 0) throw new GenerationFailure("constraint_unsatisfied", "Configuration.SettlementRatio");
        var minimumEmpty = input.Configuration.StartConstraints.MinimumReachableEmptyNodes;
        if (minimumEmpty > empty.Length)
            throw new GenerationFailure("constraint_unsatisfied", "Configuration.StartConstraints.MinimumReachableEmptyNodes");
        var usable = Enumerable.Range(0, edges.Length).Where(i => occupants[edges[i].A] is not GeneratedOccupant.Lair
            && occupants[edges[i].B] is not GeneratedOccupant.Lair
            && Math.Max(1, config.MinimumInitialTracks) <= Math.Min(2, Math.Min(config.MaximumInitialTracks, edges[i].TrackLimit))).ToArray();
        HashSet<int>? selected = null;
        for (var count = minimumEmpty; count <= empty.Length && selected is null; count++)
            Search(0, count, []);
        if (selected is null) throw new GenerationFailure("constraint_unsatisfied", "Configuration.InitialRailway");
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

        bool Search(int begin, int remaining, List<int> additions)
        {
            budget.Work();
            if (remaining == 0)
            {
                budget.Candidate = "rail:" + string.Join("/", mandatory.Concat(additions).Order().Select(i => nodes[i].Cell));
                budget.CandidateCount();
                var choice = mandatory.Concat(additions).ToHashSet();
                var induced = usable.Where(i => choice.Contains(edges[i].A) && choice.Contains(edges[i].B)).Select(i => edges[i]).ToArray();
                budget.Work(usable.Length);
                if (induced.Length >= choice.Count - 1 + (long)config.AdditionalCycleEdges
                    && WorldGraph.Connected(nodes.Length, induced, choice, budget))
                { selected = choice; return true; }
                return false;
            }
            for (var i = begin; i <= empty.Length - remaining; i++)
            {
                additions.Add(empty[i]);
                if (Search(i + 1, remaining - 1, additions)) return true;
                additions.RemoveAt(additions.Count - 1);
            }
            return false;
        }

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

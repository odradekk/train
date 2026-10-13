namespace Train.Core.World;

internal sealed class WorldTraversal
{
    private readonly List<int>[] _adjacency;
    private readonly GenerationBudgetCounter _budget;

    private WorldTraversal(List<int>[] adjacency, GenerationBudgetCounter budget)
    { _adjacency = adjacency; _budget = budget; }

    internal static WorldTraversal Build(int count, IEnumerable<(int A, int B)> edges, GenerationBudgetCounter budget)
    {
        var adjacency = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in edges)
        {
            budget.Work();
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }
        return new(adjacency, budget);
    }

    internal (HashSet<int> Nodes, int Edges) Reachable(int root, HashSet<int>? allowed = null)
    {
        var reached = new HashSet<int> { root };
        var pending = new Queue<int>(reached);
        var incidentEdges = 0;
        while (pending.TryDequeue(out var node))
        {
            _budget.Work();
            foreach (var next in _adjacency[node])
            {
                _budget.Work();
                if (allowed is not null && !allowed.Contains(next)) continue;
                incidentEdges++;
                if (reached.Add(next)) pending.Enqueue(next);
            }
        }
        return (reached, incidentEdges / 2);
    }
}

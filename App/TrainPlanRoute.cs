using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.App;

internal static class TrainPlanRoute
{
    internal static ImmutableArray<EdgeId>? Find(WorldSummary world, NodeId start, NodeId target)
    {
        // 寻路、通行限制和等长路径顺序须与 Core/Railway/TrainRouting.cs 保持一致。
        var occupants = world.Nodes.ToDictionary(node => node.Id, node => node.Occupant);
        if (!occupants.ContainsKey(start) || !occupants.ContainsKey(target)) return null;
        var routes = new Dictionary<NodeId, (double Length, ImmutableArray<EdgeId> Edges)>
        {
            [start] = (0, [])
        };
        var visited = new HashSet<NodeId>();
        while (true)
        {
            NodeId? current = null;
            foreach (var (node, route) in routes)
            {
                if (visited.Contains(node)) continue;
                if (current is null || Compare(route, routes[current.Value]) < 0)
                    current = node;
            }
            if (current is null) return null;
            var best = routes[current.Value];
            if (current.Value == target) return best.Edges;
            visited.Add(current.Value);
            foreach (var edge in world.Edges)
            {
                NodeId next;
                if (edge.Start == current.Value) next = edge.End;
                else if (edge.End == current.Value) next = edge.Start;
                else continue;
                if (visited.Contains(next) || !HasIntactTrack(edge)) continue;
                if (next != target && occupants[next] is NodeOccupant.Lair or
                    NodeOccupant.Settlement { Relation: SettlementRelation.Hostile }) continue;
                var route = (best.Length + edge.LengthMeters, best.Edges.Add(edge.Id));
                if (!routes.TryGetValue(next, out var previous) || Compare(route, previous) < 0)
                    routes[next] = route;
            }
        }
    }

    internal static bool HasIntactTrack(EdgeMapItem edge)
    {
        for (var track = 1; track <= edge.Tracks; track++)
            if (!edge.Breaks.Any(item => item.Track == track)) return true;
        return false;
    }

    private static int Compare((double Length, ImmutableArray<EdgeId> Edges) first,
        (double Length, ImmutableArray<EdgeId> Edges) second)
    {
        var comparison = first.Length.CompareTo(second.Length);
        if (comparison != 0) return comparison;
        for (var index = 0; index < Math.Min(first.Edges.Length, second.Edges.Length); index++)
        {
            comparison = first.Edges[index].Value.CompareTo(second.Edges[index].Value);
            if (comparison != 0) return comparison;
        }
        return first.Edges.Length.CompareTo(second.Edges.Length);
    }
}

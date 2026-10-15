using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Railway;

internal static class TrainRouting
{
    internal static (ImmutableArray<EdgeId> Edges, double DistanceMeters)? FindRoute(
        ImmutableArray<NodeMapItem> nodes, ImmutableArray<EdgeMapItem> edges,
        NodeId start, NodeId target, double maximumSpeedMetersPerSecond)
    {
        var occupants = nodes.ToDictionary(node => node.Id, node => node.Occupant);
        var routes = new Dictionary<NodeId, (double TimeSeconds, double DistanceMeters, ImmutableArray<EdgeId> Edges)>
        {
            [start] = (0, 0, [])
        };
        var visited = new HashSet<NodeId>();

        while (true)
        {
            NodeId? current = null;
            foreach (var (node, route) in routes)
            {
                if (visited.Contains(node))
                    continue;
                if (current is null || CompareRoutes(route.TimeSeconds, route.Edges,
                    routes[current.Value].TimeSeconds, routes[current.Value].Edges) < 0)
                    current = node;
            }

            if (current is null)
                return null;

            var best = routes[current.Value];
            if (current.Value == target)
                return (best.Edges, best.DistanceMeters);

            visited.Add(current.Value);
            foreach (var edge in edges)
            {
                NodeId next;
                if (edge.Start == current.Value)
                    next = edge.End;
                else if (edge.End == current.Value)
                    next = edge.Start;
                else
                    continue;

                if (visited.Contains(next) || FindTrack(edge) is null)
                    continue;
                if (next != target && occupants[next] is (NodeOccupant.Lair or
                    NodeOccupant.Settlement { Relation: SettlementRelation.Hostile }))
                    continue;

                var time = best.TimeSeconds + edge.LengthMeters / maximumSpeedMetersPerSecond;
                var path = best.Edges.Add(edge.Id);
                if (!routes.TryGetValue(next, out var previous) ||
                    CompareRoutes(time, path, previous.TimeSeconds, previous.Edges) < 0)
                    routes[next] = (time, best.DistanceMeters + edge.LengthMeters, path);
            }
        }
    }

    internal static int? FindTrack(EdgeMapItem edge)
    {
        for (var track = 1; track <= edge.Tracks; track++)
        {
            if (edge.Breaks.Any(item => item.Track == track) ||
                edge.Works.Any(work => work.Kind == RailwayWorkKind.RepairBreak && work.Track == track))
                continue;
            return track;
        }
        return null;
    }

    private static int CompareRoutes(double firstTime, ImmutableArray<EdgeId> first,
        double secondTime, ImmutableArray<EdgeId> second)
    {
        var timeComparison = firstTime.CompareTo(secondTime);
        if (timeComparison != 0)
            return timeComparison;

        for (var index = 0; index < Math.Min(first.Length, second.Length); index++)
        {
            var identityComparison = first[index].Value.CompareTo(second[index].Value);
            if (identityComparison != 0)
                return identityComparison;
        }
        return first.Length.CompareTo(second.Length);
    }
}

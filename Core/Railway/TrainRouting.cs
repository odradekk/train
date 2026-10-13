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

    internal static double? PredictStopDistance(ImmutableArray<NodeMapItem> nodes,
        ImmutableArray<EdgeMapItem> edges, NodeId start, RunPlan plan, int nextStopIndex,
        double maximumSpeedMetersPerSecond)
    {
        var distance = 0.0;
        var current = start;
        var index = nextStopIndex;
        // 循环还须验证末项回到首项的路径，才能确认不存在后续无路停车点。
        var stopsToCheck = plan.Stops.Length + (plan.Loop ? 1 : 0);
        for (var checkedStops = 0; checkedStops < stopsToCheck; checkedStops++)
        {
            var stop = plan.Stops[index];
            var target = stop switch
            {
                PlanStop.Waypoint waypoint => waypoint.Node,
                PlanStop.Station station => station.Node,
                _ => throw new InvalidOperationException("Unknown plan stop.")
            };
            var route = FindRoute(nodes, edges, current, target, maximumSpeedMetersPerSecond);
            if (route is null)
                return distance;

            distance += route.Value.DistanceMeters;
            if (stop is PlanStop.Station || !plan.Loop && index == plan.Stops.Length - 1)
                return distance;

            current = target;
            index = (index + 1) % plan.Stops.Length;
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

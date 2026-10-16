using System.Numerics;
using Train.Core.Contracts;

namespace Train.Scenes;

internal sealed class NodeTrainPath
{
    private readonly NodeRailPath inlet, approach, platform, departure, path;
    private readonly NodeRailPath inbound;
    private readonly bool returnsToEntry;
    private readonly double a, p, d;
    private readonly EdgeId? nextEdge;
    internal NodeLayout.Entry? Entry { get; }
    internal NodeLayout.Stop Stop { get; }
    internal bool Returning { get; }
    internal double WaitingArc => inlet.Length;
    internal double StopArc => inlet.Length + approach.Length;
    internal double PlatformEndArc => StopArc + platform.Length;
    internal double Length => path.Length;

    internal NodeTrainPath(NodeLayout layout, NodeView dimensions, TrainPosition.Node position, NodeLayout.Stop stop)
    {
        a = dimensions.ApproachLengthMeters; p = dimensions.PlatformLengthMeters; d = dimensions.DepartureLengthMeters;
        nextEdge = position.NextEdge;
        Entry = layout.TrainEntry(position.EnteredFrom); Stop = stop;
        returnsToEntry = Entry is not null && position.EnteredFrom is not null && position.NextEdge == position.EnteredFrom;
        Returning = position.Phase == NodeTravelPhase.Departing && returnsToEntry;
        if (Entry is null)
        {
            var forward = layout.StationForward;
            var start = RailwayCoordinates.Add(stop.Center, -forward * (float)((a + p / 2) * RailwayCoordinates.CellsPerMeter));
            inlet = NodeRailPath.Straight(start, forward, a * RailwayCoordinates.CellsPerMeter);
            approach = NodeRailPath.Straight(inlet.Sample(inlet.Length).Position, forward, p * RailwayCoordinates.CellsPerMeter / 2);
            platform = NodeRailPath.Straight(stop.Center, forward, p * RailwayCoordinates.CellsPerMeter / 2);
            departure = NodeRailPath.Straight(platform.Sample(platform.Length).Position, forward, d * RailwayCoordinates.CellsPerMeter);
        }
        else
        {
            var entry = layout.Approach(Entry, stop);
            inlet = p == 0 ? NodeRailPath.Join(entry.Inlet, entry.Branch) : entry.Inlet;
            approach = p == 0 ? NodeRailPath.Straight(stop.Center, -Entry.Side * layout.StationForward, 0) : entry.Branch;
            if (returnsToEntry)
            {
                platform = approach.Reverse();
                departure = inlet.Reverse();
            }
            else
            {
                var tail = layout.StationTail(stop, -Entry.Side);
                var exit = position.NextEdge is { } next ? layout.Entries.FirstOrDefault(item => item.Edge == next) : null;
                var outbound = exit is null
                    ? NodeRailPath.Straight(tail.Sample(tail.Length).Position, -Entry.Side * layout.StationForward, d * RailwayCoordinates.CellsPerMeter)
                    : exit.Side == Entry.Side ? layout.TurnExit(-Entry.Side, stop, exit) : layout.Exit(exit, stop);
                platform = p == 0 ? NodeRailPath.Straight(stop.Center, -Entry.Side * layout.StationForward, 0) : tail;
                departure = p == 0 ? NodeRailPath.Join(tail, outbound) : outbound;
            }
        }
        inbound = NodeRailPath.Join(inlet, approach);
        path = NodeRailPath.Join(inlet, approach, platform, departure);
    }

    internal double Arc(double distance, double queueOffset = 0, bool queuedAtStop = false)
    {
        if (distance <= a) return distance / a * (WaitingArc - queueOffset);
        if (p > 0 && distance <= a + p / 2)
            return WaitingArc + (distance - a) / (p / 2) * approach.Length - (queuedAtStop ? queueOffset : 0);
        if (p > 0 && distance <= a + p)
            return StopArc + (distance - a - p / 2) / (p / 2) * platform.Length;
        return PlatformEndArc + (distance - a - p) / d * departure.Length;
    }

    internal RailwaySample Sample(double arc)
    {
        if (!returnsToEntry) return path.Sample(arc);
        if (!Returning) return inbound.Sample(arc);
        var value = inbound.Sample(2 * StopArc - arc);
        return value with { Forward = -value.Forward, Right = -value.Right };
    }

    internal bool SameRoute(NodeTrainPath other, double before, double after)
        => Entry == other.Entry && Stop == other.Stop && Returning == other.Returning &&
            (before <= a + p / 2 && after <= a + p / 2 ||
                nextEdge == other.nextEdge);
}

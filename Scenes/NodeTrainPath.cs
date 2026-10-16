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
    private readonly NodeLayout layout;
    private readonly Func<EdgeId, RailwayPath>? adjacent;
    private readonly NodeLayout.Entry? exit;
    internal NodeLayout.Entry? Entry { get; }
    internal NodeLayout.Stop Stop { get; }
    internal bool Returning { get; }
    internal double WaitingArc { get; }
    internal double StopArc => inlet.Length + approach.Length;
    internal double PlatformEndArc => returnsToEntry ? 2 * StopArc - WaitingArc : StopArc + platform.Length;
    internal double Length => returnsToEntry ? 2 * StopArc : path.Length;

    internal NodeTrainPath(NodeLayout layout, NodeView dimensions, TrainPosition.Node position, NodeLayout.Stop stop,
        double trainLengthCells, Func<EdgeId, RailwayPath>? adjacent)
    {
        this.layout = layout; this.adjacent = adjacent;
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
                exit = position.NextEdge is { } next ? layout.Entries.FirstOrDefault(item => item.Edge == next) : null;
                var outbound = exit is null
                    ? NodeRailPath.Straight(tail.Sample(tail.Length).Position, -Entry.Side * layout.StationForward, d * RailwayCoordinates.CellsPerMeter)
                    : exit.Side == Entry.Side ? layout.TurnExit(-Entry.Side, stop, exit) : layout.Exit(exit, stop);
                platform = p == 0 ? NodeRailPath.Straight(stop.Center, -Entry.Side * layout.StationForward, 0) : tail;
                departure = p == 0 ? NodeRailPath.Join(tail, outbound) : outbound;
            }
        }
        inbound = NodeRailPath.Join(inlet, approach);
        path = NodeRailPath.Join(inlet, approach, platform, departure);
        WaitingArc = WaitingPoint(inlet.Length, p > 0 ? trainLengthCells / 2 : 0);
    }

    internal static double WaitingPoint(double inletLength, double halfLength) =>
        halfLength < inletLength ? inletLength - halfLength : inletLength;

    internal double Arc(double distance, double queueOffset = 0)
    {
        if (distance <= a) return distance / a * (WaitingArc - queueOffset);
        if (p > 0 && distance <= a + p / 2)
            return WaitingArc + (distance - a) / (p / 2) * (StopArc - WaitingArc);
        if (p > 0 && distance <= a + p)
            return StopArc + (distance - a - p / 2) / (p / 2) * (PlatformEndArc - StopArc);
        return PlatformEndArc + (distance - a - p) / d * (Length - PlatformEndArc);
    }

    internal RailwaySample Sample(double arc)
    {
        if (Returning)
        {
            var incoming = SampleInbound(2 * StopArc - arc);
            return incoming with { Forward = -incoming.Forward, Right = -incoming.Right };
        }
        if (arc < 0) return SampleInbound(arc);
        if (returnsToEntry) return inbound.Sample(arc);
        if (arc <= Length || exit is null || adjacent is null) return path.Sample(arc);
        var boundary = layout.TrainBoundary(exit);
        var value = adjacent(exit.Edge).SampleFromNode(layout.Node, boundary, Length - arc);
        return value with { Forward = -value.Forward, Right = -value.Right };
    }

    private RailwaySample SampleInbound(double arc) => arc < 0 && Entry is not null && adjacent is not null
        ? adjacent(Entry.Edge).SampleFromNode(layout.Node, layout.TrainBoundary(Entry), arc)
        : inbound.Sample(arc);

    internal bool SameRoute(NodeTrainPath other, double before, double after)
        => Entry == other.Entry && Stop == other.Stop && Returning == other.Returning &&
            (before <= a + p / 2 && after <= a + p / 2 ||
                nextEdge == other.nextEdge);
}

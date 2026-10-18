using System.Numerics;
using Train.Core.Contracts;

namespace Train.Scenes;

internal sealed class NodeTrainPath
{
    private readonly NodeRailPath inlet, approach, platform, departure, path;
    private readonly NodeRailPath inbound, outbound;
    private readonly bool returnsToEntry;
    private readonly double a, p, d;
    private readonly EdgeId? nextEdge;
    private readonly int enteredTrack, nextTrack;
    private readonly double returnWaitingArc;
    private readonly NodeLayout layout;
    private readonly Func<EdgeId, RailwayPath>? adjacent;
    private readonly NodeLayout.Entry? exit;
    internal NodeLayout.Entry? Entry { get; }
    internal NodeLayout.Stop Stop { get; }
    internal bool Returning { get; }
    internal double WaitingArc { get; }
    internal double StopArc => inlet.Length + approach.Length;
    internal double PlatformEndArc => returnsToEntry ? StopArc + outbound.Length - returnWaitingArc : StopArc + platform.Length;
    internal double Length => path.Length;

    internal NodeTrainPath(NodeLayout layout, NodeView dimensions, TrainPosition.Node position, NodeLayout.Stop stop,
        double trainLengthCells, Func<EdgeId, RailwayPath>? adjacent)
    {
        this.layout = layout; this.adjacent = adjacent;
        a = dimensions.ApproachLengthMeters; p = dimensions.PlatformLengthMeters; d = dimensions.DepartureLengthMeters;
        nextEdge = position.NextEdge;
        Entry = layout.TrainEntry(position.EnteredFrom); Stop = stop;
        enteredTrack = Entry is null ? 1 : layout.TrainTrackNumber(Entry, position.EnteredTrack);
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
            var entry = layout.Approach(Entry, stop, enteredTrack);
            inlet = p == 0 ? NodeRailPath.Join(entry.Inlet, entry.Branch) : entry.Inlet;
            approach = p == 0 ? NodeRailPath.Straight(stop.Center, -Entry.Side * layout.StationForward, 0) : entry.Branch;
            exit = position.NextEdge is { } next ? layout.Entries.FirstOrDefault(item => item.Edge == next) : null;
            nextTrack = exit is null ? 1 : layout.TrainTrackNumber(exit, position.NextTrack);
            if (returnsToEntry)
            {
                var selected = layout.Approach(Entry, stop, nextTrack);
                var selectedInlet = p == 0 ? NodeRailPath.Join(selected.Inlet, selected.Branch) : selected.Inlet;
                platform = p == 0 ? NodeRailPath.Straight(stop.Center, Entry.Side * layout.StationForward, 0) : selected.Branch.Reverse();
                departure = selectedInlet.Reverse();
                returnWaitingArc = WaitingPoint(selectedInlet.Length, p > 0 ? trainLengthCells / 2 : 0);
            }
            else
            {
                var tail = layout.StationTail(stop, -Entry.Side);
                var exitRoute = exit is null
                    ? NodeRailPath.Straight(tail.Sample(tail.Length).Position, -Entry.Side * layout.StationForward, d * RailwayCoordinates.CellsPerMeter)
                    : exit.Side == Entry.Side ? layout.TurnExit(-Entry.Side, stop, exit, nextTrack) : layout.Exit(exit, stop, nextTrack);
                platform = p == 0 ? NodeRailPath.Straight(stop.Center, -Entry.Side * layout.StationForward, 0) : tail;
                departure = p == 0 ? NodeRailPath.Join(tail, exitRoute) : exitRoute;
            }
        }
        inbound = NodeRailPath.Join(inlet, approach);
        outbound = NodeRailPath.Join(platform, departure);
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
        if ((Returning || !returnsToEntry) && arc > Length && exit is not null && adjacent is not null)
        {
            var boundary = layout.TrainBoundary(exit, nextTrack);
            var value = adjacent(exit.Edge).SampleFromNode(layout.Node, boundary, Length - arc, nextTrack);
            return value with { Forward = -value.Forward, Right = -value.Right };
        }
        // A reversing consist uses one outgoing tangent even while its rear still extends past S.
        if (Returning) return outbound.Sample(arc - StopArc);
        if (arc < 0) return SampleInbound(arc);
        if (returnsToEntry) return inbound.Sample(arc);
        return path.Sample(arc);
    }

    private RailwaySample SampleInbound(double arc) => arc < 0 && Entry is not null && adjacent is not null
        ? adjacent(Entry.Edge).SampleFromNode(layout.Node, layout.TrainBoundary(Entry, enteredTrack), arc, enteredTrack)
        : inbound.Sample(arc);

    internal bool SameRoute(NodeTrainPath other, double before, double after)
        => Entry == other.Entry && enteredTrack == other.enteredTrack && Stop == other.Stop && Returning == other.Returning &&
            (before <= a + p / 2 && after <= a + p / 2 ||
                nextEdge == other.nextEdge && nextTrack == other.nextTrack);
}

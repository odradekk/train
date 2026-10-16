using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeBoundaryMembersTests
{
    [TestMethod]
    [DataRow(3)]
    [DataRow(5)]
    public void OutsideMembersUseIndependentRailwayAndInletOraclesAtBothEnds(int count)
    {
        // A curved, graded production railway, with contract-only in-memory fixtures.
        var snapshot = RailwayFixtures.Snapshot(lengthMeters: 800, directMeters: 600, relief: 1, tracks: 2);
        var railway = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var offset = RailwayCoordinates.TrackOffset(1, 2);
        var tangentError = 0.0;
        foreach (var node in new[] { railway.Start.Id, railway.End.Id })
        {
            var layout = NodeLayout.Build(snapshot, node);
            var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(node)).Value;
            var entry = layout.EntryFor(railway.Edge.Id);
            var boundary = layout.TrainBoundary(entry);
            var direction = node == railway.End.Id ? 1 : -1;
            var endpoint = direction > 0 ? railway.LengthCells : 0;
            var lineBoundary = railway.Sample(endpoint, offset);
            var incoming = new NodeTrainPath(layout, dimensions,
                new(node, entry.Edge, entry.Edge, NodeTravelPhase.Approaching, 0, null), layout.Bypass,
                count * 52, _ => railway);
            var returning = new NodeTrainPath(layout, dimensions,
                new(node, entry.Edge, entry.Edge, NodeTravelPhase.Departing, 0, null), layout.Bypass,
                count * 52, _ => railway);
            var inlet = layout.Approach(entry, layout.Bypass).Inlet;
            foreach (var member in Enumerable.Range(0, count / 2 + 1))
            {
                var delta = member * 52.0; // 26 m vehicles, at two cells per metre.
                var inward = Math.Abs(delta);
                var line = railway.Sample(railway.CenterArc(railway.TrackArc(endpoint, offset) - direction * inward, offset), offset);
                var expectedLine = new RailwaySample(
                    RailwayCoordinates.Add(boundary.Position, RailwayCoordinates.Relative(line.Position, lineBoundary.Position)),
                    direction * line.Forward, direction * line.Right);
                Near(expectedLine, incoming.Sample(-inward), $"{node} arrival member {member}");
                Near(Reverse(expectedLine), returning.Sample(returning.Length + inward), $"{node} return member {member}");
                var tangent = RailwayCoordinates.Add(boundary.Position, -boundary.Forward * (float)inward);
                tangentError = Math.Max(tangentError, Vector3.Distance(RailwayCoordinates.Relative(expectedLine.Position, tangent), Vector3.Zero));

                // The railway scene has no platform selection: its oracle is the shared inlet.
                // Members beyond the branch are deliberately outside this continuity contract (R9).
                Assert.IsTrue(inward <= inlet.Length, "Oracle samples must stay inside the shared inlet; beyond-branch members belong to R9.");
                var expectedNode = inlet.Sample(inward);
                expectedNode = expectedNode with
                {
                    Position = RailwayCoordinates.Add(lineBoundary.Position,
                        RailwayCoordinates.Relative(expectedNode.Position, inlet.Sample(0).Position)),
                    Forward = direction * expectedNode.Forward,
                    Right = direction * expectedNode.Right
                };
                var actual = railway.SampleWithNodes(endpoint + direction * inward, offset);
                Near(expectedNode, actual, $"{node} railway member {member}");
            }
        }
        Assert.IsTrue(tangentError > .01, $"Fixture must reject tangent-only continuation; error was {tangentError} cells.");
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(5)]
    public void OrdinaryDepartureMembersContinueOntoSelectedTrackOne(int count)
    {
        var snapshot = NodeFixtures.Snapshot(tracks: 2,
            content: NodeFixtures.Content(rules: new(RailwayFixtures.Rule, 80, .12)));
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(layout.Node)).Value;
        foreach (var entry in layout.Entries)
        foreach (var exit in layout.Entries.Where(item => item.Edge != entry.Edge))
        {
            var railway = new RailwayPath(snapshot, exit.Edge);
            var path = new NodeTrainPath(layout, dimensions,
                new(layout.Node, entry.Edge, exit.Edge, NodeTravelPhase.Departing, 0, null),
                layout.Stops[0], count * 52, edge => new RailwayPath(snapshot, edge));
            var direction = railway.Start.Id == layout.Node ? 1 : -1;
            var endpoint = direction > 0 ? 0 : railway.LengthCells;
            var offset = RailwayCoordinates.TrackOffset(1, railway.Edge.Tracks);
            var origin = railway.Sample(endpoint, offset);
            foreach (var member in Enumerable.Range(0, count / 2 + 1))
            {
                var delta = member * 52.0;
                var expected = railway.Sample(railway.CenterArc(railway.TrackArc(endpoint, offset) + direction * delta, offset), offset);
                expected = expected with
                {
                    Position = RailwayCoordinates.Add(layout.TrainBoundary(exit).Position,
                        RailwayCoordinates.Relative(expected.Position, origin.Position)),
                    Forward = direction * expected.Forward,
                    Right = direction * expected.Right
                };
                Near(expected, path.Sample(path.Length + delta), $"{entry.Edge}->{exit.Edge} member {member}");
            }
        }
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(5)]
    public void SignedMembersStayContinuousAcrossArrivalDepartureAndReturn(int count)
    {
        var snapshot = NodeFixtures.Snapshot(tracks: 2,
            content: NodeFixtures.Content(rules: new(RailwayFixtures.Rule, 80, .12)));
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(layout.Node)).Value;
        foreach (var boundaryEntry in layout.Entries)
        foreach (var stop in layout.Stops.Append(layout.Bypass).Distinct())
        {
            var railway = new RailwayPath(snapshot, boundaryEntry.Edge);
            var endpoint = railway.Start.Id == layout.Node ? 0 : railway.LengthCells;
            var incomingDirection = railway.Start.Id == layout.Node ? -1 : 1;
            var offset = RailwayCoordinates.TrackOffset(1, railway.Edge.Tracks);
            var common = layout.Approach(boundaryEntry, layout.Bypass).Inlet.Length;
            Assert.IsTrue(common >= count / 2 * 52,
                "This whole-train fixture must fit the shared inlet; members beyond its branch belong to R9.");
            var arrival = new NodeTrainPath(layout, dimensions,
                new(layout.Node, boundaryEntry.Edge, boundaryEntry.Edge, NodeTravelPhase.Approaching, 0, null),
                stop, count * 52, edge => new RailwayPath(snapshot, edge));
            Compare(arrival, 0, incomingDirection, "arrival");
            var returning = new NodeTrainPath(layout, dimensions,
                new(layout.Node, boundaryEntry.Edge, boundaryEntry.Edge, NodeTravelPhase.Departing, 0, null),
                stop, count * 52, edge => new RailwayPath(snapshot, edge));
            Compare(returning, returning.Length, -incomingDirection, "return");
            foreach (var other in layout.Entries.Where(entry => entry.Edge != boundaryEntry.Edge))
            {
                var departure = new NodeTrainPath(layout, dimensions,
                    new(layout.Node, other.Edge, boundaryEntry.Edge, NodeTravelPhase.Departing, 0, null),
                    stop, count * 52, edge => new RailwayPath(snapshot, edge));
                Compare(departure, departure.Length, -incomingDirection, "departure");
            }

            void Compare(NodeTrainPath path, double boundaryArc, int direction, string kind)
            {
                var nodeBoundary = path.Sample(boundaryArc);
                var lineBoundary = railway.Sample(endpoint, offset);
                foreach (var member in Enumerable.Range(-count / 2, count))
                {
                    var delta = member * 52.0;
                    var line = railway.SampleWithNodes(railway.CenterArc(
                        railway.TrackArc(endpoint, offset) + direction * delta, offset), offset);
                    var expected = line with
                    {
                        Position = RailwayCoordinates.Add(nodeBoundary.Position,
                            RailwayCoordinates.Relative(line.Position, lineBoundary.Position)),
                        Forward = direction * line.Forward,
                        Right = direction * line.Right
                    };
                    Near(expected, path.Sample(boundaryArc + delta),
                        $"{boundaryEntry.Edge} {kind} {stop.PlotNumber} signed member {member}");
                }
            }
        }
    }
    private static RailwaySample Reverse(RailwaySample sample) => sample with { Forward = -sample.Forward, Right = -sample.Right };

    private static void Near(RailwaySample expected, RailwaySample actual, string context)
    {
        Assert.IsTrue(RailwayCoordinates.Relative(actual.Position, expected.Position).Length() < .003, context + " position");
        Assert.IsTrue(Vector3.Distance(expected.Forward, actual.Forward) < .0001, context + " heading/grade");
        Assert.IsTrue(Vector3.Distance(expected.Right, actual.Right) < .0001, context + " right");
    }
}





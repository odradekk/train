using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeTrainPathTests
{
    [TestMethod]
    [DataRow(80.0, 80.0)]
    [DataRow(51.0, 137.0)]
    public void CoreBreakpointsMapToEntryWaitingStopPlatformEndAndExit(double a, double d)
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value with
        { ApproachLengthMeters = a, PlatformLengthMeters = 96, DepartureLengthMeters = d };
        var entry = layout.Entries[0]; var exit = layout.Entries[1]; var stop = layout.Stops[0];
        var position = new TrainPosition.Node(layout.Node, entry.Edge, 1, exit.Edge, null, NodeTravelPhase.Approaching, 0, 1);
        var path = new NodeTrainPath(layout, dimensions, position, stop, 8, null);
        Near(layout.TrainBoundary(entry).Position, path.Sample(path.Arc(0)).Position);
        var waiting = RailwayCoordinates.Add(layout.TrainBoundary(entry).Position, -entry.Outward * (float)(16 + entry.StraightLength - 4));
        Near(waiting, path.Sample(path.Arc(a)).Position);
        Near(stop.Center, path.Sample(path.Arc(a + 48)).Position);
        var tail = layout.StationTail(stop, -entry.Side);
        Near(tail.Sample(tail.Length).Position, path.Sample(path.Arc(a + 96)).Position);
        Near(layout.TrainBoundary(exit).Position, path.Sample(path.Arc(a + 96 + d)).Position);
        Assert.AreEqual(path.WaitingArc / 2, path.Arc(a / 2), .0001);
        Assert.AreEqual((path.WaitingArc + path.StopArc) / 2, path.Arc(a + 24), .0001);
        Assert.AreEqual((path.StopArc + path.PlatformEndArc) / 2, path.Arc(a + 72), .0001);
        Assert.AreEqual((path.PlatformEndArc + path.Length) / 2, path.Arc(a + 96 + d / 2), .0001);
        Assert.IsTrue(Vector3.Distance(path.Sample(path.Arc(0)).Forward, -entry.Outward) < .0001);
        Assert.IsTrue(Vector3.Distance(path.Sample(path.Arc(a + 96 + d)).Forward, exit.Outward) < .0001);
    }

    [TestMethod]
    public void ChangingOrRemovingNextEdgeDoesNotMoveTheApproach()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value with { PlatformLengthMeters = 96 };
        var position = new TrainPosition.Node(layout.Node, layout.Entries[0].Edge, 1, null, null, NodeTravelPhase.Approaching, 0, 1);
        var paths = new EdgeId?[] { null, layout.Entries[0].Edge, layout.Entries[1].Edge }
            .Select(edge => new NodeTrainPath(layout, dimensions, position with { NextEdge = edge }, layout.Stops[0], 0, null)).ToArray();
        foreach (var distance in new[] { 0.0, 40, 80, 104, 128 })
        foreach (var path in paths.Skip(1))
            Near(paths[0].Sample(paths[0].Arc(distance)).Position, path.Sample(path.Arc(distance)).Position);
    }

    [TestMethod]
    public void LongIncomingMembersUseTheCurrentExitOnlyBeyondT()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value;
        var entry = layout.EntryFor(new(2));
        var stop = layout.Stops[0];
        var distance = dimensions.ApproachLengthMeters + dimensions.PlatformLengthMeters / 2 - 1;
        var position = new TrainPosition.Node(layout.Node, entry.Edge, 1, null, null, NodeTravelPhase.Approaching, distance, 1);
        var unchanged = new NodeTrainPath(layout, dimensions, position, stop, 0, null);
        foreach (var next in new EdgeId?[] { null, layout.EntryFor(new(1)).Edge, entry.Edge })
        {
            var changed = new NodeTrainPath(layout, dimensions, position with { NextEdge = next }, stop, 0, null);
            var center = unchanged.Arc(distance);
            foreach (var memberOffset in new[] { -400.0, 0, unchanged.PlatformEndArc - center, 400 })
            {
                var arc = center + memberOffset;
                var before = RailwayVehicles.Pose(unchanged.Sample(arc), default, true);
                var after = RailwayVehicles.Pose(changed.Sample(arc), default, true);
                if (arc <= unchanged.PlatformEndArc || next is null || next == entry.Edge)
                {
                    Assert.IsTrue(Vector3.Distance(before.Position, after.Position) < .003);
                    Assert.IsTrue(Math.Abs(Quaternion.Dot(before.Rotation, after.Rotation)) > .99999);
                }
                else Assert.IsTrue(Vector3.Distance(before.Position, after.Position) > 1,
                    "A front member beyond T follows the selected exit rather than extending straight.");

                var moved = changed.Sample(changed.Arc(distance + .000001) + memberOffset);
                Assert.IsTrue(Vector3.Distance(after.Position, RailwayCoordinates.Relative(moved.Position, default)) < .003,
                    "With the same exit, an arbitrarily small progress change must stay continuous.");
            }
        }
    }

    [TestMethod]
    public void ReturnUsesTheSameLineBackThroughWWithIndependentDepartureScale()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value with
        { ApproachLengthMeters = 51, PlatformLengthMeters = 96, DepartureLengthMeters = 137 };
        var entry = layout.Entries[0]; var stop = layout.Stops[0];
        var position = new TrainPosition.Node(layout.Node, entry.Edge, 1, entry.Edge, null, NodeTravelPhase.Docked, 99, 1);
        var before = new NodeTrainPath(layout, dimensions, position, stop, 8, null);
        var after = new NodeTrainPath(layout, dimensions, position with { Phase = NodeTravelPhase.Departing, NextTrack = 1 }, stop, 8, null);
        Near(stop.Center, after.Sample(after.Arc(99)).Position);
        Near(before.Sample(before.Arc(51)).Position, after.Sample(after.Arc(147)).Position);
        Near(layout.TrainBoundary(entry).Position, after.Sample(after.Arc(284)).Position);
        foreach (var memberOffset in new[] { -20.0, 0, 20 })
        {
            var oldPose = RailwayVehicles.Pose(before.Sample(before.Arc(99) + memberOffset), default, true);
            var newPose = RailwayVehicles.Pose(after.Sample(after.Arc(99) - memberOffset), default, false);
            Assert.IsTrue(Vector3.Distance(oldPose.Position, newPose.Position) < .003);
            Assert.IsTrue(Math.Abs(Quaternion.Dot(oldPose.Rotation, newPose.Rotation)) > .99999);
        }
    }

    [TestMethod]
    public void OversizedConsistsKeepPositiveApproachProgressAndOnlyChangeMembersBeyondTheForkOnGrant()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value;
        var entry = layout.EntryFor(new(1));
        var inlet = layout.Approach(entry, layout.Bypass).Inlet.Length;
        var position = new TrainPosition.Node(layout.Node, entry.Edge, 1, new(2), null, NodeTravelPhase.Waiting,
            dimensions.ApproachLengthMeters, null);
        foreach (var halfLength in new[] { inlet, inlet + 52 })
        {
            var waiting = new NodeTrainPath(layout, dimensions, position, layout.Bypass, 2 * halfLength, null);
            var granted = new NodeTrainPath(layout, dimensions, position with { PlatformNumber = 1 }, layout.Stops[0], 2 * halfLength, null);
            Assert.AreEqual(inlet, waiting.WaitingArc, .000001);
            Assert.AreEqual(0, waiting.Arc(0), .000001);
            Assert.AreEqual(inlet / 2, waiting.Arc(dimensions.ApproachLengthMeters / 2), .000001);
            foreach (var arc in new[] { 0, inlet / 2, inlet })
            {
                var before = waiting.Sample(arc); var after = granted.Sample(arc);
                Near(before.Position, after.Position);
                Assert.IsTrue(Vector3.Distance(before.Forward, after.Forward) < .0001);
            }
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(waiting.Sample(inlet + 52).Position,
                granted.Sample(inlet + 52).Position), Vector3.Zero) > .01,
                "The accepted oversized-consist limitation must affect only members beyond the shared inlet.");
        }
    }

    [TestMethod]
    public void ZeroPlatformLengthCollapsesWSTAndStillReturnsToTheBoundary()
    {
        var snapshot = RailwayFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, snapshot.Core.World.Nodes[0].Id);
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(layout.Node)).Value with { PlatformLengthMeters = 0 };
        var edge = layout.Entries[0].Edge;
        var path = new NodeTrainPath(layout, dimensions,
            new(layout.Node, edge, 1, edge, 1, NodeTravelPhase.Departing, 80, null), layout.Bypass, 0, null);
        Near(layout.Bypass.Center, path.Sample(path.Arc(80)).Position);
        Near(layout.TrainBoundary(layout.Entries[0]).Position, path.Sample(path.Arc(160)).Position);
        Assert.IsTrue(double.IsFinite(path.Arc(120)));
    }

    private static void Near(ScenePoint expected, ScenePoint actual) =>
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(actual, expected), Vector3.Zero) < .003,
            $"Expected {expected}, actual {actual}");
}

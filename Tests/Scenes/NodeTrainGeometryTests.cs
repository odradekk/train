using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeTrainGeometryTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1000)]
    public void PlatformsAndBypassShareTheInletAndBranchOnlyAfterW(int shift)
    {
        var template = NodeFixtures.Layout with { Plots = NodeFixtures.Layout.Plots.Select(plot => plot with
        { At = plot.At with { X = plot.At.X + shift, Z = plot.At.Z + shift } }).ToImmutableArray() };
        var layout = NodeLayout.Build(NodeFixtures.Snapshot(tracks: 2, content: NodeFixtures.Content(layout: template)), NodeFixtures.NodeId);
        foreach (var entry in layout.Entries)
        foreach (var track in new[] { 1, 2 })
        {
            foreach (var stop in layout.Stops.Append(layout.Bypass))
            {
                var path = layout.Approach(entry, stop, track);
                Near(path.Inlet.Sample(path.Inlet.Length).Position, path.Branch.Sample(0).Position);
                Near(path.Branch.Sample(path.Branch.Length).Position, stop.Center);
                Assert.IsTrue(Vector3.Distance(path.Branch.Sample(path.Branch.Length).Forward, -entry.Side * layout.StationForward) < .0001);
            }
        }
    }

    [TestMethod]
    public void SameSideExitUsesAnExteriorTurnWithContinuousEndpointTangents()
    {
        var basis = NodeFixtures.Snapshot(tracks: 2);
        var snapshot = RailwayFixtures.ReplaceCore(basis, node: basis.Core.World.Nodes[2] with { Position = new(600, 100) });
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var entry = layout.Entries[0]; var exit = layout.Entries[1];
        Assert.AreEqual(entry.Side, exit.Side);
        foreach (var stop in layout.Stops.Append(layout.Bypass))
        foreach (var track in new[] { 1, 2 })
        {
            var tail = layout.StationTail(stop, -entry.Side);
            var turn = layout.TurnExit(-entry.Side, stop, exit, track);
            Near(tail.Sample(tail.Length).Position, turn.Sample(0).Position);
            Assert.IsTrue(Vector3.Distance(tail.Sample(tail.Length).Forward, turn.Sample(0).Forward) < .0001);
            Near(turn.Sample(turn.Length).Position, layout.TrainBoundary(exit, track).Position);
            Assert.IsTrue(Vector3.Distance(turn.Sample(turn.Length).Forward, exit.Outward) < .0001);
            Assert.IsTrue(turn.Length > Math.PI * RailwayFixtures.Rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter);
            for (var arc = 2.0; arc < turn.Length - 2; arc += 2)
            {
                var before = turn.Sample(arc - 1); var after = turn.Sample(arc + 1);
                Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(after.Position, before.Position), Vector3.Zero) <= 2.003,
                    $"Turn position discontinuity at {arc}");
                var bend = Math.Acos(Math.Clamp(Vector3.Dot(before.Forward, after.Forward), -1, 1));
                Assert.IsTrue(bend <= 2 / (RailwayFixtures.Rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter) + .001);
            }
        }
    }

    [TestMethod]
    public void BothEndsOfAMultitrackLineConnectToEachActualTrack()
    {
        var snapshot = RailwayFixtures.Snapshot(tracks: 3, trackLimit: 3);
        var line = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var edge = snapshot.Core.World.Edges.Single();
        foreach (var (node, arc) in new[] { (edge.Start, 0.0), (edge.End, line.LengthCells) })
        foreach (var track in new[] { 1, 2, 3 })
        {
            var layout = NodeLayout.Build(snapshot, node);
            var boundary = layout.TrainBoundary(layout.EntryFor(edge.Id), track);
            var lateral = RailwayCoordinates.Relative(line.Sample(arc, RailwayCoordinates.TrackOffset(track, 3)).Position,
                line.Sample(arc).Position);
            Near(RailwayCoordinates.Add(layout.EntryPoint(edge.Id), lateral), boundary.Position);
        }
    }

    [TestMethod]
    public void SettlementTurnsDependOnLayoutWhileFallbackUsesOnlyBuiltEntries()
    {
        var snapshot = NodeFixtures.Snapshot(tracks: 0);
        var opened = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[1] with { Tracks = 1 });
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var changed = NodeLayout.Build(opened, NodeFixtures.NodeId);
        Assert.IsNull(layout.TrainEntry(null));
        Assert.AreEqual(snapshot.Core.World.Edges[1].Id, changed.TrainEntry(null)!.Edge);
        Assert.IsTrue(layout.TurnRoutes().Any());
        Assert.AreEqual(layout.Inputs, changed.Inputs);
        Assert.AreEqual(layout.Bounds, changed.Bounds);
        CollectionAssert.AreEqual(layout.Entries.ToArray(), changed.Entries.ToArray());
        foreach (var exit in layout.Entries)
        {
            var turn = layout.TurnExit(-exit.Side, layout.Bypass, exit);
            Near(layout.TrainBoundary(exit).Position, turn.Sample(turn.Length).Position);
        }
    }

    [TestMethod]
    public void SlopedTrackLengthAndSamplingUseThreeDimensionalArcLength()
    {
        var track = new NodeLayout.Track(new(0, 0, 0), Vector3.UnitZ, Vector3.UnitX, 0, [(4, 0), (40, 0)], 5);
        var distance = 0.0; var previous = track.Sample(0).Position;
        const int count = 10000;
        for (var i = 1; i <= count; i++)
        {
            var point = track.Sample(track.Length * i / count).Position;
            distance += Vector3.Distance(RailwayCoordinates.Relative(point, previous), Vector3.Zero);
            previous = point;
        }
        Assert.AreEqual(track.Length, distance, .001);
        Near(new(0, 5, 44), track.Sample(track.Length).Position);
        Assert.IsTrue(track.Length > 44);
        Assert.IsTrue(Vector3.Distance(track.Sample(0).Forward, Vector3.UnitZ) < .0001);
        Assert.IsTrue(Vector3.Distance(track.Sample(track.Length).Forward, Vector3.UnitZ) < .0001);
    }

    private static void Near(ScenePoint expected, ScenePoint actual) =>
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(actual, expected), Vector3.Zero) < .003,
            $"Expected {expected}, actual {actual}");
}

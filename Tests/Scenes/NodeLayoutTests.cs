using System.Collections.Immutable;
using System.Numerics;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeLayoutTests
{
    [TestMethod]
    public void TrackCountAndTrainChangesDoNotMoveEntriesButCapacityChangesCan()
    {
        var basis = RailwayFixtures.Snapshot(tracks: 1, trackLimit: 3);
        var layout = NodeLayout.Build(basis, new(1));
        var moreTracks = RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[0] with { Tracks = 3 });
        Assert.AreEqual(layout.Inputs, NodeLayout.InputKey(moreTracks, new(1)));
        Assert.AreEqual(layout.EntryPoint(RailwayFixtures.Edge), NodeLayout.Build(moreTracks, new(1)).EntryPoint(RailwayFixtures.Edge));
        var station = RailwayFixtures.ReplaceCore(basis, node: basis.Core.World.Nodes[0] with { Platforms = 4 });
        Assert.AreNotEqual(layout.Inputs, NodeLayout.InputKey(station, new(1)));
        Assert.IsTrue(NodeLayout.Build(station, new(1)).HalfSize >= layout.HalfSize);
    }

    [TestMethod]
    [DataRow(0, 0.0)]
    [DataRow(2, 0.0)]
    [DataRow(2, 90.0)]
    [DataRow(4, 135.0)]
    public void ConnectionsMeetRadiusClearanceAndEndpointTangents(int platforms, double degrees)
    {
        var basis = RailwayFixtures.Snapshot(tracks: 2, trackLimit: 2);
        var angle = degrees * Math.PI / 180;
        var snapshot = RailwayFixtures.ReplaceCore(basis, node: basis.Core.World.Nodes[0] with { Platforms = platforms });
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with { Position = new(600 * Math.Sin(angle), 600 * Math.Cos(angle)) });
        var layout = NodeLayout.Build(snapshot, new(1));
        var entry = layout.EntryFor(RailwayFixtures.Edge);
        Assert.AreEqual(layout.HalfSize, Math.Max(Math.Abs(entry.Point.X), Math.Abs(entry.Point.Z)), 0.5);
        for (var station = 0; station < Math.Max(1, platforms); station++)
        foreach (var offset in new[] { -17.0, 0, 17 })
        {
            var curve = layout.Connection(entry.Edge, offset, station);
            var first = curve.Sample(0);
            Assert.IsTrue(Vector3.Distance(-entry.Outward, first.Forward) < 0.0001);
            var previous = first.Position;
            var before = previous;
            var steps = Math.Max(100, (int)Math.Ceiling(curve.Length));
            for (var i = 1; i <= steps; i++)
            {
                var point = curve.Sample(curve.Length * i / steps).Position;
                var a = new Vector2((float)(previous.X - before.X), (float)(previous.Z - before.Z));
                var b = new Vector2((float)(point.X - previous.X), (float)(point.Z - previous.Z));
                if (i > 1)
                {
                    var curvature = 2 * Math.Abs(a.X * b.Y - a.Y * b.X) / (a.Length() * b.Length() * (a + b).Length());
                    Assert.IsTrue(curvature <= 1 / 80.0 + 0.0001, $"Curvature {curvature}");
                }
                for (var p = 0; p < platforms; p++)
                {
                    var local = RailwayCoordinates.Relative(point, default);
                    var u = Vector3.Dot(local, layout.StationSample(0, 0).Right);
                    var v = Vector3.Dot(local, layout.StationForward);
                    var left = 40 * (p - (platforms - 1) / 2.0) + 16;
                    var dx = Math.Max(left - u, Math.Max(0, u - left - 8));
                    var dz = Math.Max(0, Math.Abs(v) - 64);
                    Assert.IsTrue(dx * dx + dz * dz >= 256 - 0.01);
                }
                before = previous; previous = point;
            }
            var end = curve.Sample(curve.Length);
            var extended = curve.Sample(curve.Length + 200);
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(extended.Position, end.Position), end.Forward * 200) < 0.001);
        }
    }

    [TestMethod]
    [DataRow("x", 0.0, 180.0)]
    [DataRow("z", 90.0, 270.0)]
    [DataRow("t", 0.0, 90.0, 180.0)]
    [DataRow("cross", 0.0, 90.0, 180.0, 270.0)]
    [DataRow("diagonal", 45.0, 225.0)]
    public void FiniteJunctionsHaveBoundedAnalyticLayouts(string name, params double[] bearings)
    {
        var snapshot = Junction(bearings);
        var layout = NodeLayout.Build(snapshot, new(1));
        var radius = RailwayFixtures.Rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter;
        // Two station tracks and reserved half-spacing lanes account for the fan-out, in addition to the turn.
        var stationSpan = NodeLayout.StationTrackSpacing;
        var laneHalfWidth = RailwayCoordinates.OutermostTrackOffset(2);
        var corridor = laneHalfWidth + RailwayCoordinates.BedBaseHalfWidth;
        var fanOut = Math.Sqrt(3) * (stationSpan + 2 * laneHalfWidth + 4);
        var sameSideSpan = layout.Entries.GroupBy(entry => (entry.Side, Math.Sign(entry.BendAngle)))
            .Max(group => group.Max(entry => entry.Separation));
        var bound = NodeLayout.PlatformLength / 2.0 + NodeLayout.PlatformClearance + NodeLayout.ApproachStraight +
            radius + fanOut + sameSideSpan * (1 + Math.Sqrt(3)) + corridor + NodeLayout.GroundMargin + 1 + VoxelGrid.ChunkSize;
        Assert.IsTrue(layout.HalfSize <= bound, $"{name}: half-size {layout.HalfSize}, bound {bound}");
        foreach (var entry in layout.Entries)
        {
            var endpoint = RailwayPath.Endpoint(snapshot, entry.Edge, layout.Node);
            Assert.AreEqual(Math.Round(entry.Point.X - endpoint.X), entry.Point.X - endpoint.X, 1e-8);
            Assert.AreEqual(Math.Round(entry.Point.Z - endpoint.Z), entry.Point.Z - endpoint.Z, 1e-8);
            foreach (var slot in new[] { -1, 0, 1 })
            for (var station = 0; station < 2; station++)
            {
                var track = layout.Connection(entry.Edge, slot, station);
                var end = track.Sample(track.Length);
                var expected = layout.StationSample(NodeLayout.StationX(station, 2), entry.Side * 80).Position;
                Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(end.Position, expected), Vector3.Zero) < .002,
                    $"{name} entry={entry.Edge} slot={slot} station={station}: end={end.Position}, expected={expected}");
                for (var arc = 0.0; arc <= track.Length; arc += 2)
                {
                    var point = track.Sample(arc).Position;
                    Assert.IsTrue(Math.Abs(point.X) <= layout.HalfSize + 1 && Math.Abs(point.Z) <= layout.HalfSize + 1);
                }
            }
        }
    }

    [TestMethod]
    [DataRow(0.000001)]
    [DataRow(0.1)]
    [DataRow(29.999)]
    [DataRow(30.001)]
    [DataRow(89.999)]
    public void NearParallelAndBranchAnglesRemainFinite(double angle)
    {
        var snapshot = Junction([angle, 180]);
        var layout = NodeLayout.Build(snapshot, new(1));
        foreach (var entry in layout.Entries)
        for (var station = 0; station < 2; station++)
        {
            var track = layout.Connection(entry.Edge, 0, station);
            Assert.IsTrue(double.IsFinite(track.Length) && track.Length < 2000);
            Assert.IsTrue(Vector3.Distance(track.Sample(0).Forward, -entry.Outward) < 1e-5);
            Assert.IsTrue(Vector3.Distance(track.Sample(track.Length).Forward, -entry.Side * layout.StationForward) < 1e-5);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SameSideEntriesHaveSeparateQueueStartsAndCheapEntriesMatchFullLayout(bool perpendicular)
    {
        var snapshot = Junction(perpendicular ? [0, 180, 0, 90, 90] : [0, 0]);
        var cheap = NodeLayout.BuildEntries(snapshot, new(1));
        var layout = NodeLayout.Build(snapshot, new(1));
        CollectionAssert.AreEqual(cheap.Entries.ToArray(), layout.Entries.ToArray());
        var first = layout.QueueSample(layout.Entries[^2], 0, -17).Position;
        var second = layout.QueueSample(layout.Entries[^1], 0, -17).Position;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(first, second), Vector3.Zero) >= 34 + 2 * RailwayCoordinates.BedBaseHalfWidth);
        var origin = new ScenePoint(64, -256, 128);
        var bounds = cheap.PanBounds(origin);
        var zero = layout.PanBounds(default);
        Assert.AreEqual(zero.Minimum - new Vector2(64, 128), bounds.Minimum);
        Assert.AreEqual(zero.Maximum - new Vector2(64, 128), bounds.Maximum);
    }

    [TestMethod]
    [DataRow(5.0, 10.0)]
    [DataRow(20.0, 25.0)]
    [DataRow(40.0, 45.0)]
    [DataRow(80.0, 85.0)]
    [DataRow(10.0, 5.0)]
    [DataRow(85.0, 80.0)]
    public void DifferentBearingsOnTheSameSideKeepQueueStartsSeparate(double firstBearing, double secondBearing)
    {
        var layout = NodeLayout.Build(Junction([firstBearing, secondBearing, 180, 180]), new(1));
        var first = layout.QueueSample(layout.Entries[0], 0, -17).Position;
        var second = layout.QueueSample(layout.Entries[1], 0, -17).Position;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(first, second), Vector3.Zero) > 55,
            $"bearings {firstBearing}, {secondBearing}: {first}, {second}");
        for (var i = 0; i < 8; i++)
        for (var j = 0; j < 8; j++)
        {
            var a = layout.QueueSample(layout.Entries[0], 52 + i * 108, -17);
            var b = layout.QueueSample(layout.Entries[1], 52 + j * 108, -17);
            var delta = RailwayCoordinates.Relative(b.Position, a.Position);
            var separated = new[] { a.Forward, a.Right, b.Forward, b.Right }.Any(axis =>
                Math.Abs(Vector3.Dot(delta, axis)) >= 52 * Math.Abs(Vector3.Dot(a.Forward, axis)) +
                    8 * Math.Abs(Vector3.Dot(a.Right, axis)) + 52 * Math.Abs(Vector3.Dot(b.Forward, axis)) +
                    8 * Math.Abs(Vector3.Dot(b.Right, axis)));
            Assert.IsTrue(separated, $"Queue bodies overlap: {firstBearing}/{secondBearing}, slots {i}/{j}");
        }
    }

    private static SceneWorldSnapshot Junction(double[] bearings)
    {
        var basis = RailwayFixtures.Snapshot(lengthMeters: 1200, directMeters: 1200, tracks: 2, trackLimit: 2);
        var center = basis.Core.World.Nodes[0] with { Platforms = 2 };
        var nodes = ImmutableArray.CreateBuilder<NodeMapItem>(); nodes.Add(center);
        var edges = ImmutableArray.CreateBuilder<EdgeMapItem>();
        for (var i = 0; i < bearings.Length; i++)
        {
            var radians = bearings[i] * Math.PI / 180;
            var other = center with { Id = new((ulong)i + 2), Platforms = 0,
                Position = new(1200 * Math.Cos(radians), 1200 * Math.Sin(radians)) };
            nodes.Add(other);
            edges.Add(basis.Core.World.Edges[0] with { Id = new((ulong)i + 1), End = other.Id });
        }
        return basis with { Core = new FixedCoreReadView
        {
            Position = basis.Core.Position, InputIdentity = basis.Core.InputIdentity,
            World = basis.Core.World with { Nodes = nodes.ToImmutable(), Edges = edges.ToImmutable() },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node,
                edges.Where(edge => edge.Start == node.Id || edge.End == node.Id).Select(edge => edge.Id).ToImmutableArray()))),
            Edges = edges.ToDictionary(edge => edge.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(edge, [], null)))
        } };
    }

}

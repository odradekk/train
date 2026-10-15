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
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TrackCountAndTrainChangesDoNotMoveEntriesButCapacityChangesCan()
    {
        var basis = RailwayFixtures.Snapshot(tracks: 1, trackLimit: 3);
        var layout = NodeLayout.Build(basis, new(1));
        var moreTracks = RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[0] with { Tracks = 3 });
        Assert.AreEqual(layout.Inputs, NodeLayout.Build(moreTracks, new(1)).Inputs);
        Assert.AreEqual(layout.EntryPoint(RailwayFixtures.Edge), NodeLayout.Build(moreTracks, new(1)).EntryPoint(RailwayFixtures.Edge));
        var station = RailwayFixtures.ReplaceCore(basis, node: basis.Core.World.Nodes[0] with { Platforms = 4 });
        Assert.AreNotEqual(layout.Inputs, NodeLayout.Build(station, new(1)).Inputs);
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
    [DataRow("x", 2, 2, 0.0, 180.0)]
    [DataRow("z", 2, 2, 90.0, 270.0)]
    [DataRow("t", 2, 2, 0.0, 90.0, 180.0)]
    [DataRow("cross", 2, 2, 0.0, 90.0, 180.0, 270.0)]
    [DataRow("diagonal", 2, 2, 45.0, 225.0)]
    [DataRow("three-platforms", 3, 2, 0.0, 180.0)]
    [DataRow("same-side-same-turn", 2, 2, 5.0, 10.0, 180.0, 180.0)]
    public void FiniteJunctionsHaveBoundedAnalyticLayouts(string name, int platformCount, int tracks, params double[] bearings)
    {
        var snapshot = Junction(bearings, platformCount, tracks);
        var layout = NodeLayout.Build(snapshot, new(1));
        var radius = RailwayFixtures.Rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter;
        // The approved budget includes a minimum-radius turn and a lateral crossing
        // with a 30-degree intermediate tangent; none of its inputs come from the layout.
        var directions = bearings.Select(degrees => new Vector2(
            (float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180))).ToArray();
        var alongX = directions.Sum(direction => Math.Acos(Math.Clamp(Math.Abs(direction.X), 0, 1))) <
            directions.Sum(direction => Math.Acos(Math.Clamp(Math.Abs(direction.Y), 0, 1))) - 1e-8;
        var sameSideEntries = directions.Select(direction =>
        {
            var side = (alongX ? direction.X : direction.Y) >= 0 ? 1 : -1;
            var lateral = alongX ? -side * direction.Y : side * direction.X;
            return (Side: side, Turn: lateral >= 0 ? 1 : -1);
        }).GroupBy(group => group).Max(group => group.Count());
        var platforms = snapshot.Core.World.Nodes.Single(node => node.Id == new NodeId(1)).Platforms;
        var trackLimit = snapshot.Core.World.Edges.Max(edge => edge.TrackLimit);
        var stationHalfExtent = 40 * Math.Max(0, platforms - 1) / 2.0;
        var outermostLaneOffset = 34 * Math.Max(0, trackLimit - 1) / 2.0;
        // Each extra entry needs both lane corridors, two 10.5-cell bed shoulders,
        // and a 4-cell lateral gap. The terrain adds 12 blend and 26.5 slope cells.
        var entrySpacing = (sameSideEntries - 1) * (2 * outermostLaneOffset + 2 * 10.5 + 4);
        var corridor = outermostLaneOffset + 10.5 + 12 + 26.5;
        var lateralShift = 2 * stationHalfExtent + 2 * outermostLaneOffset + entrySpacing;
        var lateralForwardSpan = radius + Math.Sqrt(3) * lateralShift;
        const double halfPlatformLength = 128 / 2.0, clearance = 16, approachStraight = 16, margin = 32;
        var bound = Math.Ceiling((halfPlatformLength + clearance + approachStraight + radius +
            lateralForwardSpan + corridor + entrySpacing + margin) / 32) * 32;
        TestContext.WriteLine($"{name}: half-size {layout.HalfSize}, bound {bound}, same-side/same-turn entries {sameSideEntries}");
        Assert.IsTrue(layout.HalfSize <= bound, $"{name}: half-size {layout.HalfSize}, bound {bound}");
        foreach (var entry in layout.Entries)
        {
            var endpoint = RailwayPath.Endpoint(snapshot, entry.Edge, layout.Node);
            Assert.AreEqual(Math.Round(entry.Point.X - endpoint.X), entry.Point.X - endpoint.X, 1e-8);
            Assert.AreEqual(Math.Round(entry.Point.Z - endpoint.Z), entry.Point.Z - endpoint.Z, 1e-8);
            foreach (var slot in new[] { -1, 0, 1 })
            for (var station = 0; station < platforms; station++)
            {
                var track = layout.Connection(entry.Edge, slot, station);
                var end = track.Sample(track.Length);
                var expected = layout.StationSample(NodeLayout.StationX(station, platforms), entry.Side * 80).Position;
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
    public void SameSideEntriesHaveSeparateInnerQueueStartsAndTranslatedPanBounds(bool perpendicular)
    {
        var snapshot = Junction(perpendicular ? [0, 180, 0, 90, 90] : [0, 0]);
        var layout = NodeLayout.Build(snapshot, new(1));
        foreach (var entry in layout.Entries)
        {
            var at = layout.QueueSample(entry, 0);
            var expected = RailwayCoordinates.Add(entry.Point, -entry.Outward * (float)(16 + entry.StraightLength));
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(at.Position, expected), Vector3.Zero) < .001);
            Assert.IsTrue(Vector3.Distance(at.Forward, -entry.Outward) < .001);
            var boundary = layout.QueueSample(entry, 16 + entry.StraightLength);
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(boundary.Position, entry.Point), Vector3.Zero) < .001);
            var overflow = layout.QueueSample(entry, 16 + entry.StraightLength + 104);
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(overflow.Position, entry.Point), entry.Outward * 104) < .001);
        }
        var first = layout.QueueSample(layout.Entries[^2], 0, -17).Position;
        var second = layout.QueueSample(layout.Entries[^1], 0, -17).Position;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(first, second), Vector3.Zero) >= 34 + 2 * RailwayCoordinates.BedBaseHalfWidth);
        AssertQueuesDoNotOverlap(layout, layout.Entries[^2], layout.Entries[^1]);
        var origin = new ScenePoint(64, -256, 128);
        var bounds = layout.PanBounds(origin);
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
        AssertQueuesDoNotOverlap(layout, layout.Entries[0], layout.Entries[1]);
    }

    private static void AssertQueuesDoNotOverlap(NodeLayout layout, NodeLayout.Entry first, NodeLayout.Entry second)
    {
        for (var i = 0; i < 8; i++)
        for (var j = 0; j < 8; j++)
        {
            var a = layout.QueueSample(first, 4 + 52 + i * 108, -17);
            var b = layout.QueueSample(second, 4 + 52 + j * 108, -17);
            var delta = RailwayCoordinates.Relative(b.Position, a.Position);
            var separated = new[] { a.Forward, a.Right, b.Forward, b.Right }.Any(axis =>
                Math.Abs(Vector3.Dot(delta, axis)) >= 52 * Math.Abs(Vector3.Dot(a.Forward, axis)) +
                    8 * Math.Abs(Vector3.Dot(a.Right, axis)) + 52 * Math.Abs(Vector3.Dot(b.Forward, axis)) +
                    8 * Math.Abs(Vector3.Dot(b.Right, axis)));
            Assert.IsTrue(separated, $"104-cell queue bodies overlap: entries {first.Edge}/{second.Edge}, slots {i}/{j}");
        }
    }

    private static SceneWorldSnapshot Junction(double[] bearings, int platforms = 2, int tracks = 2)
    {
        var basis = RailwayFixtures.Snapshot(lengthMeters: 1200, directMeters: 1200, tracks: tracks, trackLimit: tracks);
        var center = basis.Core.World.Nodes[0] with { Platforms = platforms };
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
                edges.Where(edge => edge.Start == node.Id || edge.End == node.Id).Select(edge => edge.Id).ToImmutableArray(), 80, 0, 80))),
            Edges = edges.ToDictionary(edge => edge.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(edge, [], null)))
        } };
    }

}

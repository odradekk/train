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
    public void TrackCountAndCoreCapacityChangesDoNotMoveEntries()
    {
        var basis = RailwayFixtures.Snapshot(tracks: 1, trackLimit: 3);
        var layout = NodeLayout.Build(basis, new(1));
        var moreTracks = RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[0] with { Tracks = 3 });
        Assert.AreEqual(layout.Inputs, NodeLayout.Build(moreTracks, new(1)).Inputs);
        Assert.AreEqual(layout.EntryPoint(RailwayFixtures.Edge), NodeLayout.Build(moreTracks, new(1)).EntryPoint(RailwayFixtures.Edge));
        var station = RailwayFixtures.ReplaceCore(basis, node: basis.Core.World.Nodes[0] with { Platforms = 4 });
        Assert.AreEqual(layout.Inputs, NodeLayout.Build(station, new(1)).Inputs);
        Assert.AreEqual(layout.EntryPoint(RailwayFixtures.Edge), NodeLayout.Build(station, new(1)).EntryPoint(RailwayFixtures.Edge));
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(90.0)]
    [DataRow(135.0)]
    public void ConnectionsMeetRadiusClearanceAndEndpointTangents(double degrees)
    {
        var basis = RailwayFixtures.Snapshot(tracks: 2, trackLimit: 2);
        var angle = degrees * Math.PI / 180;
        var snapshot = RailwayFixtures.ReplaceCore(basis, node: basis.Core.World.Nodes[1] with { Position = new(600 * Math.Sin(angle), 600 * Math.Cos(angle)) });
        var layout = NodeLayout.Build(snapshot, new(1));
        var entry = layout.EntryFor(RailwayFixtures.Edge);
        Assert.IsTrue(Math.Abs(Math.Abs(entry.Point.X) - layout.HalfSize) < 1 ||
            Math.Abs(Math.Abs(entry.Point.Z) - layout.HalfSize) < 1);
        for (var station = 0; station < Math.Max(1, layout.Platforms); station++)
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
                before = previous; previous = point;
            }
            var end = curve.Sample(curve.Length);
            var extended = curve.Sample(curve.Length + 200);
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(extended.Position, end.Position), end.Forward * 200) < 0.001);
        }
    }

    [TestMethod]
    [DataRow("x", 2, 0.0, 180.0)]
    [DataRow("z", 2, 90.0, 270.0)]
    [DataRow("t", 2, 0.0, 90.0, 180.0)]
    [DataRow("cross", 2, 0.0, 90.0, 180.0, 270.0)]
    [DataRow("diagonal", 2, 45.0, 225.0)]
    [DataRow("same-side-same-turn", 2, 5.0, 10.0, 180.0, 180.0)]
    public void FiniteJunctionsHaveBoundedAnalyticLayouts(string name, int tracks, params double[] bearings)
    {
        var snapshot = Junction(bearings, tracks: tracks);
        var layout = NodeLayout.Build(snapshot, new(1));
        var radius = RailwayFixtures.Rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter;
        // Start with the entry fan's analytic budget, then allow the exterior single-neck loop.
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
        var platforms = layout.Platforms;
        var trackLimit = snapshot.Core.World.Edges.Max(edge => edge.TrackLimit);
        var outermostLaneOffset = 34 * Math.Max(0, trackLimit - 1) / 2.0;
        // Each extra entry needs both lane corridors, two 10.5-cell bed shoulders,
        // and a 4-cell lateral gap. The terrain adds 12 blend and 26.5 slope cells.
        var entrySpacing = (sameSideEntries - 1) * (2 * outermostLaneOffset + 2 * 10.5 + 4);
        var corridor = outermostLaneOffset + 10.5 + 12 + 26.5;
        var lateralShift = 2 * outermostLaneOffset + entrySpacing;
        var lateralForwardSpan = radius + Math.Sqrt(3) * lateralShift;
        const double clearance = 16, approachStraight = 16, margin = 32;
        var bound = Math.Ceiling((clearance + approachStraight + radius +
            lateralForwardSpan + corridor + entrySpacing + margin) / 32) * 32;
        if (directions.Length > 0)
            bound = Math.Ceiling((bound + 10 * radius + approachStraight + margin +
                RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth + margin) / 32) * 32;
        TestContext.WriteLine($"{name}: half-size {layout.HalfSize}, bound {bound}, same-side/same-turn entries {sameSideEntries}");
        Assert.IsTrue(layout.HalfSize <= bound, $"{name}: half-size {layout.HalfSize}, bound {bound}");
        foreach (var entry in layout.Entries)
        {
            Assert.IsTrue(Math.Abs(Math.Abs(entry.Point.X) - layout.HalfSize) < 1 ||
                Math.Abs(Math.Abs(entry.Point.Z) - layout.HalfSize) < 1);
            var endpoint = RailwayPath.Endpoint(snapshot, entry.Edge, layout.Node);
            Assert.AreEqual(Math.Round(entry.Point.X - endpoint.X), entry.Point.X - endpoint.X, 1e-8);
            Assert.AreEqual(Math.Round(entry.Point.Z - endpoint.Z), entry.Point.Z - endpoint.Z, 1e-8);
            foreach (var slot in new[] { -1, 0, 1 })
            for (var station = 0; station < Math.Max(1, platforms); station++)
            {
                var track = layout.Connection(entry.Edge, slot, station);
                var end = track.Sample(track.Length);
                var expected = layout.StationSample(0, entry.Side * 16).Position;
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
        for (var station = 0; station < Math.Max(1, layout.Platforms); station++)
        {
            var track = layout.Connection(entry.Edge, 0, station);
            Assert.IsTrue(double.IsFinite(track.Length) && track.Length < 2000);
            Assert.IsTrue(Vector3.Distance(track.Sample(0).Forward, -entry.Outward) < 1e-5);
            Assert.IsTrue(Vector3.Distance(track.Sample(track.Length).Forward, -entry.Side * layout.StationForward) < 1e-5);
        }
    }

    [TestMethod]
    [DataRow(0.0, 0.0)]
    [DataRow(5.0, 10.0)]
    [DataRow(40.0, 45.0)]
    public void SameSideInletsStaySeparateAndExtendAlongTheirBoundaryTangents(double firstBearing, double secondBearing)
    {
        var layout = NodeLayout.Build(Junction([firstBearing, secondBearing, 180, 180]), new(1));
        var first = layout.Approach(layout.Entries[0], layout.Bypass).Inlet;
        var second = layout.Approach(layout.Entries[1], layout.Bypass).Inlet;
        var a = first.Sample(first.Length); var b = second.Sample(second.Length);
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(a.Position, b.Position), Vector3.Zero) > 55);
        foreach (var entry in layout.Entries)
        {
            var inlet = layout.Approach(entry, layout.Bypass).Inlet;
            var boundary = layout.TrainBoundary(entry);
            var overflow = inlet.Sample(-1000);
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(overflow.Position, boundary.Position), entry.Outward * 1000) < .001);
            Assert.IsTrue(Vector3.Distance(overflow.Forward, -entry.Outward) < .001);
        }
        var origin = new ScenePoint(64, -256, 128);
        var bounds = layout.PanBounds(origin); var zero = layout.PanBounds(default);
        Assert.AreEqual(zero.Minimum - new Vector2(64, 128), bounds.Minimum);
        Assert.AreEqual(zero.Maximum - new Vector2(64, 128), bounds.Maximum);
    }

    [TestMethod]
    [DataRow(new double[] { })]
    public void NodesWithoutAnyEdgesHaveNoTurnaround(double[] bearings)
    {
        var layout = NodeLayout.Build(Junction(bearings), new(1));
        Assert.IsFalse(layout.TurnRoutes().Any());
    }

    [TestMethod]
    [DataRow(new double[] { 0, 0, 180 })]
    [DataRow(new double[] { 5, 10, 180, 180 })]
    [DataRow(new double[] { 0, 90, 180, 270 })]
    public void TurnaroundsDoNotCrossEntryTracks(double[] bearings)
    {
        var snapshot = Junction(bearings);
        var layout = NodeLayout.Build(snapshot, new(1));
        foreach (var turn in layout.NewTurnRoutes())
        foreach (var entry in layout.Entries)
        foreach (var rail in layout.EntryRoutes(entry, 2))
        {
            var beforeTurn = turn.Sample(0).Position;
            for (var t = 8.0; t < turn.Length + 8; t += 8)
            {
                var afterTurn = turn.Sample(Math.Min(t, turn.Length)).Position;
                var beforeRail = rail.Sample(0).Position;
                for (var r = 8.0; r < rail.Length + 8; r += 8)
                {
                    var afterRail = rail.Sample(Math.Min(r, rail.Length)).Position;
                    AssertNoCrossing(beforeTurn, afterTurn, beforeRail, afterRail,
                        $"Turn crosses entry {entry.Edge} at turn arc {t}, rail arc {r}");
                    beforeRail = afterRail;
                }
                beforeTurn = afterTurn;
            }
        }
    }

    [TestMethod]
    [DataRow(new double[] { 0 })]
    [DataRow(new double[] { 0, 0, 180 })]
    [DataRow(new double[] { 5, 10, 180, 180 })]
    [DataRow(new double[] { 0, 90, 180, 270 })]
    public void NewTurnRailsHaveNoProperSelfIntersectionsAndStayInsideFinalBounds(double[] bearings)
    {
        var layout = NodeLayout.Build(Junction(bearings), new(1));
        var rails = layout.NewTurnRoutes().Select(rail =>
        {
            var points = new List<ScenePoint>();
            for (var arc = 0.0; arc < rail.Length; arc += 4) points.Add(rail.Sample(arc).Position);
            points.Add(rail.Sample(rail.Length).Position);
            foreach (var point in points)
            {
                Assert.IsTrue(point.X >= layout.Bounds.Minimum.X && point.X <= layout.Bounds.Maximum.X &&
                    point.Z >= layout.Bounds.Minimum.Y && point.Z <= layout.Bounds.Maximum.Y,
                    $"New turn point outside final bounds: {point}");
            }
            return points;
        }).ToArray();
        for (var rail = 0; rail < rails.Length; rail++)
        for (var segment = 1; segment < rails[rail].Count; segment++)
        for (var otherRail = rail; otherRail < rails.Length; otherRail++)
        for (var otherSegment = otherRail == rail ? segment + 2 : 1; otherSegment < rails[otherRail].Count; otherSegment++)
            AssertNoCrossing(rails[rail][segment - 1], rails[rail][segment],
                rails[otherRail][otherSegment - 1], rails[otherRail][otherSegment],
                $"New turn rails {rail}/{otherRail} intersect at segments {segment}/{otherSegment}");
        foreach (var entry in layout.Entries)
            Assert.IsTrue(Math.Min(Math.Min(Math.Abs(entry.Point.X - layout.Bounds.Minimum.X),
                Math.Abs(entry.Point.X - layout.Bounds.Maximum.X)), Math.Min(Math.Abs(entry.Point.Z - layout.Bounds.Minimum.Y),
                Math.Abs(entry.Point.Z - layout.Bounds.Maximum.Y))) <= .5);
    }

    private static void AssertNoCrossing(ScenePoint a, ScenePoint b, ScenePoint c, ScenePoint d, string message)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Z, b.Z) < Math.Min(c.Z, d.Z) || Math.Max(c.Z, d.Z) < Math.Min(a.Z, b.Z)) return;
        var ux = b.X - a.X; var uz = b.Z - a.Z; var vx = d.X - c.X; var vz = d.Z - c.Z;
        var denominator = ux * vz - uz * vx;
        if (Math.Abs(denominator) <= 1e-10 * Math.Sqrt((ux * ux + uz * uz) * (vx * vx + vz * vz))) return;
        var dx = c.X - a.X; var dz = c.Z - a.Z;
        var first = (dx * vz - dz * vx) / denominator;
        var second = (dx * uz - dz * ux) / denominator;
        var firstLength = Math.Sqrt(ux * ux + uz * uz);
        var secondLength = Math.Sqrt(vx * vx + vz * vz);
        // Float direction vectors can move a mathematically shared endpoint by a few micrometres.
        const double endpointTolerance = .0001;
        Assert.IsFalse(first * firstLength > endpointTolerance && (1 - first) * firstLength > endpointTolerance &&
            second * secondLength > endpointTolerance && (1 - second) * secondLength > endpointTolerance,
            $"{message}; fractions {first}/{second}; {a}->{b} vs {c}->{d}");
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

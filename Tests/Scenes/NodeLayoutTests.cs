using System.Numerics;
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
        Assert.AreEqual(layout.HalfSize, Math.Sqrt(entry.Point.X * entry.Point.X + entry.Point.Z * entry.Point.Z), 0.001);
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
                    var left = 40 * (p - (platforms - 1) / 2.0) + 16;
                    var dx = Math.Max(left - point.X, Math.Max(0, point.X - left - 8));
                    var dz = Math.Max(0, Math.Abs(point.Z) - 64);
                    Assert.IsTrue(dx * dx + dz * dz >= 256 - 0.01);
                }
                before = previous; previous = point;
            }
            var end = curve.Sample(curve.Length);
            var extended = curve.Sample(curve.Length + 200);
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(extended.Position, end.Position), end.Forward * 200) < 0.001);
        }
    }

}

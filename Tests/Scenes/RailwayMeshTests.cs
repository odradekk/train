using System.Numerics;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayMeshTests
{
    [TestMethod]
    [DataRow(129d, false)]
    [DataRow(129.25d, true)]
    public void ShortRailSegmentsOnlyPublishDrawableMeshes(double end, bool hasSleeper)
    {
        var meshes = RailwayMeshes.Build(arc => new(new(0, 0, arc), Vector3.UnitZ, Vector3.UnitX), 128, end, default);
        Assert.IsTrue(meshes.Any(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel }));
        Assert.AreEqual(hasSleeper, meshes.Any(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.Sleeper }));
        foreach (var mesh in meshes)
        {
            Assert.IsFalse(mesh.Vertices.IsEmpty, "Published railway meshes must be drawable by the host.");
            Assert.IsFalse(mesh.TriangleIndices.IsEmpty);
        }
    }

    [TestMethod]
    public void NodeRailAdapterKeepsTheSameThreeGaugesAtTheLineBoundary()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(8, 8, 0), new(1));
        var line = RailwayMeshes.Build(path, 0, default)[0];
        var node = RailwayMeshes.Build(arc => new(new(0, 0, arc), Vector3.UnitZ, Vector3.UnitX), 0, 16, default)[0];
        var lineRing = line.Vertices.Where(vertex => vertex.Position.Z == 0).Select(vertex => (vertex.Position.X, vertex.Position.Y)).Distinct().Order().ToArray();
        var nodeRing = node.Vertices.Where(vertex => vertex.Position.Z == 0).Select(vertex => (vertex.Position.X, vertex.Position.Y)).Distinct().Order().ToArray();
        CollectionAssert.AreEqual(lineRing, nodeRing);
        Assert.IsTrue(nodeRing.Any(point => point.X > 7.5));
        Assert.IsTrue(nodeRing.Any(point => point.X < -7.5));
    }

    [TestMethod]
    public void StraightRailsHaveThreeGaugesAndOneTopLevel()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(lengthMeters: 100, directMeters: 100, relief: 0), new EdgeId(1));
        var rails = RailwayMeshes.Build(path, 0, new(0, 0, 0))[0];
        var tops = rails.Vertices.Where(vertex => vertex.Normal.Y > .99f && Math.Abs(vertex.Position.Y) < .00001f).ToArray();
        Assert.IsTrue(tops.All(vertex => vertex.TextureUv.X == 1), "Every running surface must use the polished steel material.");
        Assert.IsTrue(rails.Vertices.Except(tops).All(vertex => vertex.TextureUv.X == 0), "Web, foot and cut ends must keep the rusty material.");
        foreach (var vertex in tops) Assert.AreEqual(0f, vertex.Position.Y, .00001f);
        foreach (var (gauge, height) in new[] { (3f, .3f), (7f, .4f), (15f, .6f) })
        foreach (var side in new[] { -1, 1 })
        {
            var center = side * gauge / 2;
            var left = center - height * .65f;
            var right = center + height * .65f;
            Assert.IsTrue(tops.Any(vertex => Math.Abs(vertex.Position.X - left) < .00001f));
            Assert.IsTrue(tops.Any(vertex => Math.Abs(vertex.Position.X - right) < .00001f));
            Assert.IsTrue(rails.Vertices.Any(vertex => Math.Abs(vertex.Position.X - left) < .00001f && Math.Abs(vertex.Position.Y + height) < .00001f));
        }
        Assert.AreEqual(6, tops.Select(vertex => MathF.Round(vertex.Position.X, 4)).Distinct().Count() / 2);
    }

    [TestMethod]
    public void SleepersUseGlobalSpacingAndKeepTheirDimensionsAcrossSegments()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(lengthMeters: 130, directMeters: 130, relief: 0), new EdgeId(1));
        var centers = new List<float>();
        for (var segment = 0; segment < path.SegmentCount; segment++)
        {
            var sleepers = RailwayMeshes.Build(path, segment, new(0, 0, 0))[1];
            var mainFaces = sleepers.TriangleIndices.Chunk(3)
                .Select(triangle => triangle.Select(index => sleepers.Vertices[index]).ToArray())
                .Where(triangle => triangle.All(vertex => Math.Abs(vertex.Normal.Y) > .99f) &&
                    (triangle.All(vertex => Math.Abs(vertex.Position.Y + 1) < .00001f) ||
                     triangle.All(vertex => Math.Abs(vertex.Position.Y + .6f) < .00001f)))
                .GroupBy(triangle => MathF.Round((triangle.Max(v => v.Position.Z) + triangle.Min(v => v.Position.Z)) / 2, 4));
            foreach (var sleeper in mainFaces.OrderBy(group => group.Key))
            {
                var triangles = sleeper.Select(triangle =>
                {
                    var points = triangle.Select(vertex => vertex.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
                    return (points[0], points[1], points[2]);
                }).ToArray();
                Assert.AreEqual(triangles.Length, triangles.Distinct().Count());
                var body = sleeper.SelectMany(triangle => triangle).Select(vertex => vertex.Position).ToArray();
                Assert.AreEqual(17f, body.Max(p => p.X) - body.Min(p => p.X), .00001f);
                Assert.AreEqual(.4f, body.Max(p => p.Y) - body.Min(p => p.Y), .00001f);
                Assert.AreEqual(.6f, body.Max(p => p.Z) - body.Min(p => p.Z), .0001f);
                centers.Add((body.Max(p => p.Z) + body.Min(p => p.Z)) / 2);
            }
        }
        Assert.AreEqual(174, centers.Count);
        for (var index = 0; index < centers.Count; index++) Assert.AreEqual(index * 1.5f, centers[index], .0001f);
    }

    [TestMethod]
    public void CurvedSlopingMeshesHaveUnitNormalsAndOutwardWinding()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(), new EdgeId(1));
        var meshes = RailwayMeshes.Build(path, 2, path.Sample(256).Position);
        foreach (var mesh in meshes)
        {
            foreach (var vertex in mesh.Vertices) Assert.AreEqual(1f, vertex.Normal.Length(), .00001f);
            foreach (var triangle in mesh.TriangleIndices.Chunk(3))
            {
                var a = mesh.Vertices[triangle[0]];
                var b = mesh.Vertices[triangle[1]];
                var c = mesh.Vertices[triangle[2]];
                var normal = Vector3.Normalize(Vector3.Cross(b.Position - a.Position, c.Position - a.Position));
                Assert.IsTrue(Vector3.Dot(normal, a.Normal) > .99999f);
            }
        }
        var sleepers = meshes[1];
        var sample = path.Sample(256.5);
        var up = Vector3.Normalize(Vector3.Cross(sample.Forward, sample.Right));
        var center = RailwayCoordinates.Relative(sample.Position, path.Sample(256).Position) - up * .8f;
        var mainFaces = sleepers.TriangleIndices.Chunk(3)
            .Select(triangle => triangle.Select(index => sleepers.Vertices[index]).ToArray())
            .Where(triangle => triangle.All(vertex => Math.Abs(Vector3.Dot(vertex.Position - center, sample.Forward)) < .301f) &&
                triangle.All(vertex => Math.Abs(Math.Abs(Vector3.Dot(vertex.Position - center, up)) - .2f) < .0001f) &&
                triangle.All(vertex => Math.Abs(Vector3.Dot(vertex.Normal, up)) > .999f) &&
                triangle.Any(vertex => Math.Abs(Math.Abs(Vector3.Dot(vertex.Position - center, sample.Right)) - 8.5f) < .0001f)).ToArray();
        Assert.IsTrue(mainFaces.Length > 0);
        foreach (var triangle in mainFaces)
        {
            var midpoint = triangle.Aggregate(Vector3.Zero, (sum, vertex) => sum + vertex.Position) / 3;
            Assert.IsTrue(Vector3.Dot(midpoint - center, triangle[0].Normal) > 0);
        }
        Assert.IsTrue(mainFaces.SelectMany(triangle => triangle).Any(vertex => Math.Abs(vertex.Normal.Y) is > .001f and < .99999f));
    }

    [TestMethod]
    public void SegmentCapsMeetAndShortLastSegmentEndsAtThePathEndpoint()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(lengthMeters: 100, directMeters: 100, relief: 0), new EdgeId(1));
        var first = RailwayMeshes.Build(path, 0, new(0, 0, 0))[0];
        var last = RailwayMeshes.Build(path, 1, new(0, 0, 0))[0];
        Assert.AreEqual(128f, first.Vertices.Max(v => v.Position.Z));
        Assert.AreEqual(128f, last.Vertices.Min(v => v.Position.Z));
        Assert.AreEqual(200f, last.Vertices.Max(v => v.Position.Z));
        Assert.IsTrue(last.Vertices.Any(vertex => vertex.Normal == Vector3.UnitZ));
        Assert.IsTrue(last.Vertices.Any(vertex => vertex.Normal == -Vector3.UnitZ));
        foreach (var vertex in last.Vertices.Where(vertex => Math.Abs(vertex.Normal.Z) > .99f))
            Assert.IsTrue(vertex.Position.Z == 128 && vertex.Normal.Z == -1 ||
                        vertex.Position.Z == 200 && vertex.Normal.Z == 1);
    }

    [TestMethod]
    public void ZeroTracksHasNoRailOrSleeperMeshes()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(tracks: 0), new EdgeId(1));
        Assert.AreEqual(0, RailwayMeshes.Build(path, 0, new(0, 0, 0)).Length);
    }
}

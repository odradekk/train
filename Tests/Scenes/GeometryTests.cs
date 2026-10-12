using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class GeometryTests
{
    [TestMethod]
    public void CubeTrianglesFaceOutwardWithMatchingNormalsAndValidIndices()
    {
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var meshes = Meshes(geometry);
        Assert.AreEqual(12, meshes.Sum(mesh => mesh.TriangleIndices.Length / 3));
        foreach (var mesh in meshes)
        for (var i = 0; i < mesh.TriangleIndices.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.TriangleIndices[i]];
            var b = mesh.Vertices[mesh.TriangleIndices[i + 1]];
            var c = mesh.Vertices[mesh.TriangleIndices[i + 2]];
            Assert.IsTrue(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0);
            Assert.AreEqual(a.Normal, b.Normal);
            Assert.AreEqual(a.Normal, c.Normal);
            Assert.AreEqual(1f, a.Normal.Length());
            Assert.IsTrue(Vector3.Dot(a.Normal, a.Position - new Vector3(0.5f)) > 0);
        }
    }

    [TestMethod]
    public void SharedFaceIsCulledAcrossPositiveAndNegativeChunkBoundaries()
    {
        foreach (var x in new[] { -33, -1, 31 })
        {
            var result = SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(new(x, 0, 0)), SceneFixtures.Cell(new(x + 1, 0, 0), 2)]));
            Assert.AreEqual(20, Meshes(result).Sum(mesh => mesh.TriangleIndices.Length / 3));
            var hit = (ScenePick.Hit)SceneFixtures.Frame(result).Pick(new(new(x - 2, 0.5f, 0.5f), Vector3.UnitX, 5));
            Assert.AreEqual(new GridPosition(x, 0, 0), hit.Value.Cell);
            Assert.AreEqual(2d, hit.Value.DistanceCells);
        }
    }

    [TestMethod]
    public void ShapesAndModelsOccupyWholePickCellWithoutOccludingCube()
    {
        var cube = SceneFixtures.Cube;
        var meshAppearance = cube with { Presentation = new VoxelPresentation.Mesh(new("model"), []) };
        foreach (var appearance in new[] { cube, meshAppearance })
        {
            var shaped = appearance == cube ? new VoxelShape("slope") : (VoxelShape?)null;
            var grid = new VoxelGrid([
                new(new(0, 0, 0), cube, null, null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), []),
                new(new(1, 0, 0), appearance, shaped, null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), [])]);
            Assert.AreEqual(12, DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.TriangleIndices.Length / 3));
            var hit = grid.Pick(new(new(3, 0.05f, 0.05f), -Vector3.UnitX, 3));
            Assert.IsNotNull(hit);
            Assert.AreEqual(new GridPosition(1, 0, 0), hit.Value.Cell.At);
            Assert.AreEqual(1d, hit.Value.Distance);
        }
    }

    [TestMethod]
    public void DdaHonorsDistanceAndSkipsCellsTouchedOnlyAtCorners()
    {
        var result = SceneFixtures.Build(SceneFixtures.Projection([
            SceneFixtures.Cell(new(1, 0, 0)), SceneFixtures.Cell(new(2, 2, 2), 2)]));
        var frame = SceneFixtures.Frame(result);
        var direction = Vector3.Normalize(Vector3.One);
        var ray = new SceneRay(new(-1, -1, -1), direction, 10);
        var hit = (ScenePick.Hit)frame.Pick(ray);
        Assert.AreEqual(new GridPosition(2, 2, 2), hit.Value.Cell);
        Assert.IsTrue(Math.Abs(hit.Value.DistanceCells - 3 * Math.Sqrt(3)) < 0.00001);
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(ray with { MaximumDistanceCells = 5 }));
        Assert.IsInstanceOfType<ScenePick.Miss>(frame.Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 1.9)));
    }

    [TestMethod]
    public void DdaSkipsEdgeOnlyContactAndFrameChoosesNearestObject()
    {
        var edgeGeometry = SceneFixtures.Build(SceneFixtures.Projection([
            SceneFixtures.Cell(new(1, 0, 0)), SceneFixtures.Cell(new(2, 2, 0), 2)]));
        var edgeHit = (ScenePick.Hit)SceneFixtures.Frame(edgeGeometry).Pick(
            new(new(-1, -1, 0.5f), Vector3.Normalize(new(1, 1, 0)), 10));
        Assert.AreEqual(new GridPosition(2, 2, 0), edgeHit.Value.Cell);
        var farther = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(5, 0, 0))]));
        var closer = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(2, 0, 0))]));
        var nearObject = closer.Objects[0] with { Id = new(100) };
        var combined = farther with
        {
            Objects = farther.Objects.Add(nearObject),
            Picking = farther.Picking.Add(closer.Picking[0] with { Object = nearObject.Id })
        };
        var hit = (ScenePick.Hit)SceneFixtures.Frame(combined).Pick(new(new(-1, 0.5f, 0.5f), Vector3.UnitX, 10));
        Assert.AreEqual(nearObject.Id, hit.Value.Object);
        Assert.AreEqual(3d, hit.Value.DistanceCells);
    }

    [TestMethod]
    public void RotatedNestedPickPreservesPartPathAndRootPose()
    {
        var nested = new NestedObjectProjection([0], new(4, 0, 0), VoxelOrientation.NxPy, new NestedObjectDetails.Assembly(null));
        var projection = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0), 7, path: [0])], [nested]);
        var root = new ScenePose(new(10, 0, 0), Quaternion.Identity);
        var frame = SceneFixtures.Frame(SceneFixtures.Build(projection, root));
        var hit = ((ScenePick.Hit)frame.Pick(new(new(12, 0.5f, 0.5f), Vector3.UnitX, 5))).Value;
        Assert.AreEqual(new PartId(7), ((PickSubject.WorldObject)hit.Subject).Part);
        CollectionAssert.AreEqual(new[] { 0 }, hit.NestedObjectPath.ToArray());
        Assert.AreEqual(new Vector3(14, 0.5f, 0.5f), hit.Point);
        Assert.AreEqual(-Vector3.UnitX, hit.Normal);
        Assert.AreEqual(root, ((Lookup<ScenePose>.Found)frame.FindPose(SceneFixtures.Subject)).Value);
    }

    [TestMethod]
    public void FogBeforeGeometryReportsNotReadyAndDecorationDoesNotIntercept()
    {
        var rendered = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(4, 0, 0))]));
        var cube = Meshes(rendered).Single();
        var inFront = cube.Vertices.Select(vertex => vertex with { Position = vertex.Position - new Vector3(3, 0, 0) }).ToImmutableArray();
        var decorations = Enum.GetValues<BuiltInMaterial>().Select((kind, i) => new SceneObject(new((ulong)(100 + i)), null,
            SceneFixtures.Identity, null, [new SceneVisual.Geometry(new(new SceneMaterial.BuiltIn(kind), inFront, cube.TriangleIndices), new(false, 0, 0, false))], [], []));
        var reference = new ContentReference.Design(new("unknown"));
        var marker = new SceneObject(new(200), null, SceneFixtures.Identity, null, [new SceneVisual.Missing(reference, false)], [], []);
        rendered = rendered with { Objects = rendered.Objects.AddRange(decorations).Add(marker) };
        var ray = new SceneRay(new(-1, 0.5f, 0.5f), Vector3.UnitX, 10);
        Assert.AreEqual(5d, ((ScenePick.Hit)SceneFixtures.Frame(rendered).Pick(ray)).Value.DistanceCells);
        Assert.IsInstanceOfType<ScenePick.NotReady>(SceneFixtures.Frame(rendered, [new(new(1, 0, 0), new(2, 1, 1))]).Pick(ray));
        Assert.IsInstanceOfType<ScenePick.Hit>(SceneFixtures.Frame(rendered, [new(new(8, 0, 0), new(9, 1, 1))]).Pick(ray));
    }

    private static SceneMesh[] Meshes(RenderedDesign rendered) => rendered.Objects.SelectMany(item => item.Visuals)
        .OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
}

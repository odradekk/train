using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class StandardShapeTests
{
    private static readonly string[] Shapes = ["cube", "half", "plate", "wedge", "corner-wedge", "beam", "cylinder"];

    [TestMethod]
    public void EveryShapeHasOutwardNondegenerateTrianglesInsideItsOrientedCell()
    {
        foreach (var shape in Shapes)
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        {
            var (right, up, forward) = Axes(orientation);
            Vector3 Local(Vector3 p) => new(Vector3.Dot(p - new Vector3(.5f), right) + .5f,
                Vector3.Dot(p - new Vector3(.5f), up) + .5f, Vector3.Dot(p - new Vector3(.5f), forward) + .5f);
            var meshes = Build(shape, orientation);
            Assert.IsTrue(meshes.Length > 0, $"{shape} {orientation}");
            var localVertices = meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => Local(vertex.Position)).ToArray();
            Assert.AreEqual(0f, localVertices.Min(p => p.Y), .00001f);
            Assert.AreEqual(shape == "half" ? .5f : shape == "plate" ? .125f : 1f, localVertices.Max(p => p.Y), .00001f);
            foreach (var mesh in meshes)
            {
                foreach (var vertex in mesh.Vertices)
                {
                    var p = Local(vertex.Position);
                    Assert.IsTrue(p.X >= -.00001f && p.Y >= -.00001f && p.Z >= -.00001f &&
                        p.X <= 1.00001f && p.Y <= 1.00001f && p.Z <= 1.00001f, $"{shape} {orientation}: {p}");
                    if (shape == "half") Assert.IsTrue(p.Y <= .50001f);
                    if (shape == "plate") Assert.IsTrue(p.Y <= .12501f);
                    if (shape == "wedge") Assert.IsTrue(p.Y + p.Z <= 1.00001f);
                    if (shape == "corner-wedge") Assert.IsTrue(p.Y + Math.Max(p.X, p.Z) <= 1.00001f);
                    if (shape == "beam") Assert.IsTrue(p.X >= .37499f && p.X <= .62501f && p.Z >= .37499f && p.Z <= .62501f);
                    if (shape == "cylinder") Assert.IsTrue(new Vector2(p.X - .5f, p.Z - .5f).Length() <= .50001f);
                    Assert.AreEqual(1f, vertex.Normal.Length(), .00001f);
                }
                for (var i = 0; i < mesh.TriangleIndices.Length; i += 3)
                {
                    var a = mesh.Vertices[mesh.TriangleIndices[i]];
                    var b = mesh.Vertices[mesh.TriangleIndices[i + 1]];
                    var c = mesh.Vertices[mesh.TriangleIndices[i + 2]];
                    var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
                    Assert.IsTrue(Vector3.Dot(cross, a.Normal) > .000001f, $"{shape} {orientation}");
                    var center = new Vector3(.5f, shape == "plate" ? .0625f : shape == "half" ? .25f : shape == "corner-wedge" ? .2f : .3f, .5f);
                    var localNormal = new Vector3(Vector3.Dot(a.Normal, right), Vector3.Dot(a.Normal, up), Vector3.Dot(a.Normal, forward));
                    Assert.IsTrue(Vector3.Dot(localNormal, Local((a.Position + b.Position + c.Position) / 3) - center) > 0);
                }
            }
        }
    }

    [TestMethod]
    public void MissingShapeUsesFirstAdvertisedShape()
    {
        var appearance = SceneFixtures.Cube with { Presentation = new VoxelPresentation.Cube([new("plate"), new("cube")],
            MaterialCategory.PaintedMetal, 1, new("tile"), new("tile"), new("tile"), new("tile")) };
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), content: SceneFixtures.Content(appearance: appearance));
        Assert.AreEqual(.125f, Meshes(geometry).SelectMany(mesh => mesh.Vertices).Max(vertex => vertex.Position.Y));
    }

    [TestMethod]
    public void CylinderHasTwelveFlatSidesAndFullHeight()
    {
        var vertices = Build("cylinder").SelectMany(mesh => mesh.Vertices).ToArray();
        var sideNormals = vertices.Where(vertex => Math.Abs(vertex.Normal.Y) < .00001f).Select(vertex => vertex.Normal).Distinct().ToArray();
        Assert.HasCount(12, sideNormals);
        Assert.AreEqual(0f, vertices.Min(vertex => vertex.Position.Y));
        Assert.AreEqual(1f, vertices.Max(vertex => vertex.Position.Y));
        Assert.IsTrue(vertices.Where(vertex => Math.Abs(vertex.Normal.Y) < .00001f)
            .All(vertex => Math.Abs(new Vector2(vertex.Position.X - .5f, vertex.Position.Z - .5f).Length() - .5f) < .00001f));
    }

    [TestMethod]
    public void RotatedWedgeOccludesOnlyItsFullBoundaryAcrossChunks()
    {
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        foreach (var coordinate in new[] { -33, -1, 31 })
        foreach (var full in new[] { false, true })
        {
            var (_, _, forward) = Axes(orientation);
            var wedgeAt = new GridPosition(coordinate, coordinate, coordinate);
            var direction = full ? -forward : forward;
            var cubeAt = new GridPosition(wedgeAt.X + (int)direction.X, wedgeAt.Y + (int)direction.Y, wedgeAt.Z + (int)direction.Z);
            var geometry = SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(wedgeAt, shape: new("wedge")) with { LocalOrientation = orientation },
                SceneFixtures.Cell(cubeAt, 2)]));
            var plane = new Vector3(wedgeAt.X, wedgeAt.Y, wedgeAt.Z) + new Vector3(.5f) + direction * .5f;
            var cubeFace = Meshes(geometry).SelectMany(mesh => mesh.Vertices).Any(vertex =>
                Vector3.Distance(vertex.Normal, -direction) < .00001f && Math.Abs(Vector3.Dot(vertex.Position - plane, direction)) < .00001f);
            Assert.AreEqual(!full, cubeFace, $"{orientation} {coordinate} {full}");
        }
    }

    [TestMethod]
    public void ShapesKeepWholeCellPickingAndLocalUvsWhenRootMoves()
    {
        foreach (var shape in Shapes)
        {
            var projection = SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0), shape: new(shape))]);
            var original = SceneFixtures.Build(projection);
            var hit = (ScenePick.Hit)SceneFixtures.Frame(original).Pick(new(new(-1, .9f, .9f), Vector3.UnitX, 3));
            Assert.AreEqual(new GridPosition(0, 0, 0), hit.Value.Cell);
            Assert.AreEqual(1d, hit.Value.DistanceCells);
            var moved = SceneFixtures.Build(projection, new(new(100, -20, 40), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f)));
            CollectionAssert.AreEqual(Meshes(original).SelectMany(mesh => mesh.Vertices).ToArray(), Meshes(moved).SelectMany(mesh => mesh.Vertices).ToArray());
        }
    }

    [TestMethod]
    public void WedgeSlopeSelectsTopOnTieAndUsesSquareProjection()
    {
        var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(4, front: true));
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        {
            var (right, up, forward) = Axes(orientation);
            var normal = Vector3.Normalize(up + forward);
            var meshes = Build("wedge", orientation, content);
            var slope = meshes.SelectMany(mesh => mesh.Vertices.Where(vertex => Vector3.Distance(vertex.Normal, normal) < .00001f)
                .Select(vertex => (Vertex: vertex, Texture: ((SceneMaterial.Voxel)mesh.Material).Texture))).ToArray();
            Assert.IsTrue(slope.Length > 0);
            Assert.IsTrue(slope.All(item => item.Texture == new AssetRevision("top")));
            foreach (var a in slope)
            foreach (var b in slope)
            {
                var delta = b.Vertex.Position - a.Vertex.Position;
                Assert.AreEqual(Vector3.Dot(delta, -right) / 4, b.Vertex.TextureUv.X - a.Vertex.TextureUv.X, .00001f);
                Assert.AreEqual(Vector3.Dot(delta, -forward) / 4, b.Vertex.TextureUv.Y - a.Vertex.TextureUv.Y, .00001f);
            }
        }
    }

    [TestMethod]
    public void WedgeFaceCoordinatesAndExposedEdgesUseSelectedSquareFace()
    {
        var vertices = DesignGeometry.Meshes(new VoxelGrid([
            new(new(0, 0, 0), new(VoxelOrientation.PxPy, SceneFixtures.Cube, new("wedge"), null,
                new PickSubject.Terrain(new WorldLocation.Node(new(1))), []))]), true)
            .OfType<SceneVisual.Geometry>().SelectMany(item => item.Mesh.Vertices)
            .Where(vertex => vertex.Normal.Y > .1f && vertex.Normal.Z > .1f).ToArray();
        Assert.IsTrue(vertices.Length > 0);
        foreach (var vertex in vertices)
        {
            Assert.AreEqual(new Vector2(vertex.Position.X, 1 - vertex.Position.Z), vertex.FaceUv);
            Assert.AreEqual(ExposedFaceEdges.MinimumU | ExposedFaceEdges.MaximumU |
                ExposedFaceEdges.MinimumV | ExposedFaceEdges.MaximumV, vertex.ExposedEdges);
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(8)]
    public void CubeAndWedgeShareTexturePhaseIncludingNegativeCoordinates(int span)
    {
        foreach (var coordinate in new[] { -33, -1, 0, 31 })
        {
            var content = SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(span));
            SceneVertex[] Vertices(string shape) => Meshes(SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(new(coordinate, coordinate, coordinate), shape: new(shape))]), content: content))
                .SelectMany(mesh => mesh.Vertices).ToArray();
            var cube = Vertices("cube").Where(vertex => vertex.Normal == Vector3.UnitY).ToArray();
            var wedge = Vertices("wedge").Where(vertex => vertex.Normal.Y > .1f && vertex.Normal.Z > .1f).ToArray();
            var shared = (from a in cube from b in wedge where a.Position == b.Position select (a, b)).ToArray();
            Assert.IsTrue(shared.Length >= 2);
            foreach (var (a, b) in shared) Assert.AreEqual(a.TextureUv, b.TextureUv);

            var joined = Meshes(SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(new(coordinate, coordinate, coordinate - 1)),
                SceneFixtures.Cell(new(coordinate, coordinate, coordinate), 2, shape: new("wedge"))]), content: content));
            var seam = joined.SelectMany(mesh => mesh.Vertices).Where(vertex =>
                vertex.Position.Y == coordinate + 1 && vertex.Position.Z == coordinate).ToArray();
            foreach (var vertex in seam.Where(vertex => vertex.Normal.Y > .1f && vertex.Normal.Z > .1f))
                Assert.IsTrue(seam.Any(other => other.Normal == Vector3.UnitY && other.Position == vertex.Position &&
                    (other.TextureUv == vertex.TextureUv || coordinate % span == 0 &&
                        other.TextureUv.X == vertex.TextureUv.X && Math.Abs(other.TextureUv.Y - vertex.TextureUv.Y) == 1)),
                    "Adjacent cube and wedge must share UVs, allowing the existing 1-to-0 repeat boundary.");
        }
    }

    [TestMethod]
    public void DistinctFrontOnPartialFaceDoesNotUseTileSpan()
    {
        SceneVertex[] Front(int span) => Build("beam", content: SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(span, front: true)))
            .Where(mesh => ((SceneMaterial.Voxel)mesh.Material).Texture == new AssetRevision("front"))
            .SelectMany(mesh => mesh.Vertices).ToArray();
        var single = Front(1);
        Assert.IsTrue(single.Length > 0);
        CollectionAssert.AreEqual(single.Select(vertex => vertex.TextureUv).ToArray(), Front(8).Select(vertex => vertex.TextureUv).ToArray());
        Assert.AreEqual(.25f, single.Max(vertex => vertex.TextureUv.X) - single.Min(vertex => vertex.TextureUv.X), .00001f);
    }

    [TestMethod]
    public void HorizontalCylinderTieUsesSideAndBoundaryTrianglesCullOnlyAgainstFullFaces()
    {
        var meshes = Build("cylinder", content: SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(4, front: true)));
        var tied = meshes.SelectMany(mesh => mesh.Vertices.Where(vertex => vertex.Normal.Z > .1f &&
                Math.Abs(Math.Abs(vertex.Normal.X) - vertex.Normal.Z) < .00001f)
            .Select(vertex => ((SceneMaterial.Voxel)mesh.Material).Texture)).ToArray();
        Assert.IsTrue(tied.Length > 0);
        Assert.IsTrue(tied.All(texture => texture == new AssetRevision("side")));
        foreach (var neighbor in new[] { "cube", "half" })
        {
            var geometry = SceneFixtures.Build(SceneFixtures.Projection([
                SceneFixtures.Cell(new(0, 0, 0), shape: new("wedge")),
                SceneFixtures.Cell(new(-1, 0, 0), 2, shape: new(neighbor))]));
            var boundary = Meshes(geometry).SelectMany(mesh => mesh.Vertices).Any(vertex =>
                vertex.Position.X == 0 && vertex.Normal == -Vector3.UnitX);
            Assert.AreEqual(neighbor == "half", boundary);
        }
    }

    private static SceneMesh[] Build(string shape, VoxelOrientation orientation = VoxelOrientation.PxPy, IContentView? content = null) =>
        Meshes(SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0), shape: new(shape)) with { LocalOrientation = orientation }]), content: content));
    private static SceneMesh[] Meshes(RenderedDesign rendered) => rendered.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
    private static (Vector3 Right, Vector3 Up, Vector3 Forward) Axes(VoxelOrientation orientation)
    {
        Vector3 Axis(string name) => name switch { "Px" => Vector3.UnitX, "Nx" => -Vector3.UnitX, "Py" => Vector3.UnitY, "Ny" => -Vector3.UnitY, "Pz" => Vector3.UnitZ, _ => -Vector3.UnitZ };
        var name = orientation.ToString();
        var right = Axis(name[..2]);
        var up = Axis(name[2..]);
        return (right, up, Vector3.Cross(right, up));
    }
}

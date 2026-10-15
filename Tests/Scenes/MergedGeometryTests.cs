using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class MergedGeometryTests
{
    [TestMethod]
    public void TerrainRectanglesCoverExposedCellsOnceAndPreserveTexturePhase()
    {
        foreach (var origin in new[] { -34, -3, 30, 1_000_001 })
        {
            var cells = (from x in Enumerable.Range(origin, 5)
                         from z in Enumerable.Range(-3, 4)
                         where x != origin + 2 || z != -1 // Removed cell leaves an exposed hole.
                         select Cell(new(x, -2, z))).ToArray();
            AssertCoverage(cells);
        }
    }

    [TestMethod]
    public void DifferentTextureAxesAndMaterialsRemainSeparate()
    {
        var cells = new[]
        {
            Cell(new(0, 0, 0)), Cell(new(1, 0, 0), VoxelOrientation.PzPy),
            Cell(new(2, 0, 0), paint: new("#ff0000")),
            Cell(new(3, 0, 0), glass: true),
            Cell(new(4, 0, 0)), Cell(new(5, 0, 0), span: 8)
        };
        AssertCoverage(cells);
        foreach (var mesh in DesignGeometry.Meshes(new([.. cells]), false).OfType<SceneVisual.Geometry>().Select(item => item.Mesh))
        foreach (var indices in mesh.TriangleIndices.Chunk(3))
        {
            var vertices = indices.Select(index => mesh.Vertices[index]).ToArray();
            if (vertices[0].Normal == Vector3.UnitY)
                Assert.IsTrue(vertices.Max(vertex => vertex.Position.X) - vertices.Min(vertex => vertex.Position.X) <= 1);
        }
    }

    [TestMethod]
    public void SolidRectangleCompressesButWearAndCellPickingRemainIndependent()
    {
        var cells = (from x in Enumerable.Range(0, 4) from z in Enumerable.Range(0, 3)
                     select Cell(new(x, 0, z))).ToArray();
        var grid = new VoxelGrid([.. cells]);
        var merged = DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().ToArray();
        Assert.IsTrue(merged.Sum(item => item.Mesh.Vertices.Length) < DesignGeometry.Meshes(grid, true)
            .OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length));
        foreach (var vertex in merged.SelectMany(item => item.Mesh.Vertices).Where(vertex => vertex.Normal == Vector3.UnitY))
            Assert.IsTrue(Vector2.Distance(new(vertex.Position.X / 4, 1 - vertex.Position.Z / 3), vertex.FaceUv) < 0.00001f);
        foreach (var cell in cells)
        {
            var hit = grid.Pick(new(new(cell.At.X + 0.5f, 3, cell.At.Z + 0.5f), -Vector3.UnitY, 4));
            Assert.IsNotNull(hit);
            Assert.AreEqual(cell.At, hit.Value.Cell.At);
            Assert.AreEqual(cell.Subject, hit.Value.Cell.Subject);
            CollectionAssert.AreEqual(cell.Path.ToArray(), hit.Value.Cell.Path.ToArray());
        }
        AssertCoverage(cells);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void FullChunkFacesMergeIncludingGlassFront(int normalAxis)
    {
        var cells = (from u in Enumerable.Range(0, 32) from v in Enumerable.Range(0, 32)
                     select Cell(normalAxis switch
                     {
                         0 => new(0, u, v), 1 => new(u, 0, v), _ => new(u, v, 0)
                     }, glass: true)).ToArray();
        var grid = new VoxelGrid([.. cells]);
        Assert.IsTrue(DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length) <
            DesignGeometry.Meshes(grid, true).OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length));
        AssertCoverage(cells);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(8)]
    public void EveryTextureOrientationPreservesCoverageAndPhase(int span)
    {
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        {
            var cells = (from x in Enumerable.Range(-4, 3) from z in Enumerable.Range(-4, 3)
                         select Cell(new(x, -2, z), orientation, span: span)).ToArray();
            var grid = new VoxelGrid([.. cells]);
            Assert.IsTrue(DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length) <
                DesignGeometry.Meshes(grid, true).OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length));
            AssertCoverage(cells);
        }
    }

    [TestMethod]
    [DataRow(-33)]
    [DataRow(-1)]
    [DataRow(31)]
    public void AllMeshTriangleEdgesHaveNoInteriorVerticesAcrossCornersMaterialsAndChunks(int origin)
    {
        var cells = (from x in Enumerable.Range(0, 5)
                     from z in Enumerable.Range(0, 3)
                     where x < 3 || z == 0
                     from y in Enumerable.Range(0, x == 1 && z == 0 ? 2 : 1)
                     select Cell(new(origin + x, y, origin + z),
                         x == 2 ? VoxelOrientation.PzPy : VoxelOrientation.PxPy,
                         paint: x >= 3 ? new("#ff0000") : null, glass: z == 2)).ToArray();
        var meshes = DesignGeometry.Meshes(new([.. cells]), false)
            .OfType<SceneVisual.Geometry>().Select(visual => visual.Mesh).ToArray();
        var positions = meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Position).ToHashSet();
        var edges = new HashSet<(Vector3 Start, Vector3 End)>();
        foreach (var mesh in meshes)
        foreach (var indices in mesh.TriangleIndices.Chunk(3))
        for (var corner = 0; corner < 3; corner++)
        {
            var start = mesh.Vertices[indices[corner]].Position;
            var end = mesh.Vertices[indices[(corner + 1) % 3]].Position;
            edges.Add((start, end));
        }
        foreach (var (start, end) in edges)
        {
            var direction = end - start;
            foreach (var point in positions)
            {
                if (point == start || point == end) continue;
                var offset = point - start;
                var along = Vector3.Dot(offset, direction);
                if (Vector3.Cross(offset, direction) == Vector3.Zero && along > 0 && along < direction.LengthSquared())
                    Assert.Fail($"Vertex {point} lies inside triangle edge {start} -> {end}; origin {origin}.");
            }
        }
    }

    [TestMethod]
    public void SingleCellKeepsFourVerticesAndTwoTrianglesPerFace()
    {
        var mesh = DesignGeometry.Meshes(new([Cell(new(0, 0, 0))]), false).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        Assert.AreEqual(24, mesh.Sum(item => item.Vertices.Length));
        Assert.AreEqual(36, mesh.Sum(item => item.TriangleIndices.Length));
        AssertCoverage([Cell(new(0, 0, 0))]);
    }

    private static SceneCell Cell(GridPosition at, VoxelOrientation orientation = VoxelOrientation.PxPy,
        PaintColor? paint = null, bool glass = false, int span = 4)
    {
        var appearance = SceneFixtures.TexturedCube(span, front: true);
        if (glass) appearance = appearance with
        {
            Presentation = ((VoxelPresentation.Cube)appearance.Presentation) with { Material = MaterialCategory.Glass }
        };
        return new(at, new(orientation, appearance, null, paint,
            new PickSubject.WorldObject(SceneFixtures.Subject, SceneFixtures.Design, new(at.X + 100)), [at.X, at.Z]));
    }

    private static void AssertCoverage(SceneCell[] cells)
    {
        var occupied = cells.ToDictionary(cell => cell.At);
        var faces = new Dictionary<(GridPosition Cell, Vector3 Normal), List<(SceneVertex A, SceneVertex B, SceneVertex C, SceneMaterial Material)>>();
        var normals = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (var cell in cells)
        foreach (var normal in normals)
            if (!occupied.ContainsKey(new(cell.At.X + (int)normal.X, cell.At.Y + (int)normal.Y, cell.At.Z + (int)normal.Z)))
                faces.Add((cell.At, normal), []);
        var minimumY = cells.Min(cell => cell.At.Y);
        var height = cells.Max(cell => cell.At.Y) + 1f - minimumY;
        foreach (var visual in DesignGeometry.Meshes(new([.. cells]), false).OfType<SceneVisual.Geometry>())
        {
            foreach (var vertex in visual.Mesh.Vertices)
            {
                Assert.AreEqual(ExposedFaceEdges.None, vertex.ExposedEdges);
                Assert.AreEqual(0f, vertex.Damage);
                Assert.AreEqual((vertex.Position.Y - minimumY) / height, vertex.RelativeHeight, 0.00001f);
                Assert.IsTrue(vertex.FaceUv.X is >= 0 and <= 1 && vertex.FaceUv.Y is >= 0 and <= 1);
            }
            foreach (var indices in visual.Mesh.TriangleIndices.Chunk(3))
            {
                Assert.HasCount(3, indices);
                Assert.IsTrue(indices.All(index => index >= 0 && index < visual.Mesh.Vertices.Length));
                var a = visual.Mesh.Vertices[indices[0]];
                var b = visual.Mesh.Vertices[indices[1]];
                var c = visual.Mesh.Vertices[indices[2]];
                var normal = a.Normal;
                Assert.IsTrue(normals.Contains(normal));
                Assert.AreEqual(normal, b.Normal);
                Assert.AreEqual(normal, c.Normal);
                Assert.AreEqual(Vector3.Dot(a.Position, normal), Vector3.Dot(b.Position, normal));
                Assert.AreEqual(Vector3.Dot(a.Position, normal), Vector3.Dot(c.Position, normal));
                Assert.IsTrue(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), normal) > 0);
                var u = normal.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
                var v = normal.Y != 0 ? Vector3.UnitZ : Vector3.UnitY;
                var low = Vector3.Min(a.Position, Vector3.Min(b.Position, c.Position));
                var high = Vector3.Max(a.Position, Vector3.Max(b.Position, c.Position));
                var layer = (int)Vector3.Dot(a.Position, Vector3.Abs(normal)) - (Vector3.Dot(normal, Vector3.One) > 0 ? 1 : 0);
                for (var x = (int)MathF.Floor(Vector3.Dot(low, u)); x < MathF.Ceiling(Vector3.Dot(high, u)); x++)
                for (var y = (int)MathF.Floor(Vector3.Dot(low, v)); y < MathF.Ceiling(Vector3.Dot(high, v)); y++)
                {
                    var at = normal.X != 0 ? new GridPosition(layer, y, x) : normal.Y != 0 ? new(x, layer, y) : new(x, y, layer);
                    var polygon = ClipToCell(a.Position, b.Position, c.Position, u, v, x, y);
                    if (PolygonArea(polygon) < 0.000001f) continue;
                    Assert.IsTrue(faces.TryGetValue((at, normal), out var triangles), "Triangle covers a hidden or absent cell face.");
                    var chunk = VoxelGrid.Chunk(at);
                    var minimum = new Vector3(chunk.X * 32f, chunk.Y * 32f, chunk.Z * 32f);
                    var maximum = minimum + new Vector3(32);
                    Assert.AreEqual(a.Position, Vector3.Clamp(a.Position, minimum, maximum), "Triangle crosses a chunk boundary.");
                    Assert.AreEqual(b.Position, Vector3.Clamp(b.Position, minimum, maximum), "Triangle crosses a chunk boundary.");
                    Assert.AreEqual(c.Position, Vector3.Clamp(c.Position, minimum, maximum), "Triangle crosses a chunk boundary.");
                    triangles!.Add((a, b, c, visual.Mesh.Material));
                }
            }
        }
        foreach (var (key, triangles) in faces)
        {
            var cell = occupied[key.Cell];
            var normal = key.Normal;
            var u = normal.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
            var v = normal.Y != 0 ? Vector3.UnitZ : Vector3.UnitY;
            var start = new Vector3(cell.At.X, cell.At.Y, cell.At.Z);
            var x = Vector3.Dot(start, u);
            var y = Vector3.Dot(start, v);
            var area = triangles.Sum(t => PolygonArea(ClipToCell(t.A.Position, t.B.Position, t.C.Position, u, v, x, y)));
            Assert.AreEqual(1f, area, 0.00001f, "Each original exposed face must be covered exactly once.");
            var cube = (VoxelPresentation.Cube)cell.Appearance!.Presentation;
            var name = cell.Orientation.ToString();
            var up = Axis(name[2..]);
            var forward = Vector3.Cross(Axis(name[..2]), up);
            var front = normal == forward && cube.Front != cube.Side;
            var texture = normal == up ? cube.Top : normal == -up ? cube.Bottom : front ? cube.Front : cube.Side;
            var imageUp = normal == up || normal == -up ? forward : up;
            var textureU = Vector3.Cross(imageUp, normal);
            var textureV = -imageUp;
            var span = front ? 1 : cube.TileSpan;
            foreach (var triangle in triangles)
                Assert.AreEqual(new SceneMaterial.Voxel(texture, cube.Material, cell.Paint), triangle.Material);
            // Dyadic offsets remain representable at the distant-coordinate fixture.
            foreach (var offset in new[] { new Vector2(.25f, .375f), new Vector2(.625f, .75f),
                Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY })
            {
                var point = start + u * offset.X + v * offset.Y + Vector3.Max(normal, Vector3.Zero);
                var hits = 0;
                var onEdge = false;
                foreach (var triangle in triangles)
                {
                    var a = triangle.A;
                    var b = triangle.B;
                    var c = triangle.C;
                    var ab = b.Position - a.Position;
                    var ac = c.Position - a.Position;
                    var ap = point - a.Position;
                    var denominator = Vector3.Dot(Vector3.Cross(ab, ac), normal);
                    var wb = Vector3.Dot(Vector3.Cross(ap, ac), normal) / denominator;
                    var wc = Vector3.Dot(Vector3.Cross(ab, ap), normal) / denominator;
                    var wa = 1 - wb - wc;
                    if (MathF.Min(wa, MathF.Min(wb, wc)) < -0.00001f) continue;
                    hits++;
                    onEdge |= MathF.Min(wa, MathF.Min(wb, wc)) < 0.00001f;
                    var uv = a.TextureUv * wa + b.TextureUv * wb + c.TextureUv * wc;
                    AssertPhase(Vector3.Dot(point, textureU) / span, uv.X);
                    AssertPhase(Vector3.Dot(point, textureV) / span, uv.Y);
                    var relativeHeight = a.RelativeHeight * wa + b.RelativeHeight * wb + c.RelativeHeight * wc;
                    Assert.AreEqual((point.Y - minimumY) / height, relativeHeight, 0.00001f);
                }
                Assert.IsTrue(hits > 0);
                if (!onEdge) Assert.AreEqual(1, hits);
            }
        }
    }

    private static List<Vector2> ClipToCell(Vector3 a, Vector3 b, Vector3 c, Vector3 u, Vector3 v, float x, float y)
    {
        // Work in cell-local coordinates so the shoelace sum is stable far from the origin.
        var polygon = new List<Vector2>
        {
            new(Vector3.Dot(a, u) - x, Vector3.Dot(a, v) - y),
            new(Vector3.Dot(b, u) - x, Vector3.Dot(b, v) - y),
            new(Vector3.Dot(c, u) - x, Vector3.Dot(c, v) - y)
        };
        foreach (var (axis, boundary, sign) in new[] { (0, 0f, 1f), (0, 1f, -1f), (1, 0f, 1f), (1, 1f, -1f) })
        {
            var clipped = new List<Vector2>();
            for (var i = 0; i < polygon.Count; i++)
            {
                var start = polygon[i];
                var end = polygon[(i + 1) % polygon.Count];
                var first = (axis == 0 ? start.X : start.Y) - boundary;
                var second = (axis == 0 ? end.X : end.Y) - boundary;
                if (first * sign >= 0) clipped.Add(start);
                if ((first * sign < 0) != (second * sign < 0))
                    clipped.Add(start + (end - start) * (first / (first - second)));
            }
            polygon = clipped;
        }
        return polygon;
    }

    private static float PolygonArea(List<Vector2> polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += a.X * b.Y - a.Y * b.X;
        }
        return MathF.Abs(sum) / 2;
    }

    private static Vector3 Axis(string axis) => axis switch
    {
        "Px" => Vector3.UnitX, "Nx" => -Vector3.UnitX,
        "Py" => Vector3.UnitY, "Ny" => -Vector3.UnitY,
        "Pz" => Vector3.UnitZ, "Nz" => -Vector3.UnitZ,
        _ => throw new ArgumentException(axis)
    };

    private static void AssertPhase(float expected, float actual) =>
        Assert.AreEqual(0f, (actual - expected) - MathF.Round(actual - expected), 0.00001f);
}


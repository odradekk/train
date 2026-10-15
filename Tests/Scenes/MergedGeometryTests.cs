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
        var top = DesignGeometry.Meshes(new([.. cells]), false).OfType<SceneVisual.Geometry>()
            .SelectMany(item => item.Mesh.Vertices.Chunk(4)).Where(quad => quad[0].Normal == Vector3.UnitY).ToArray();
        Assert.HasCount(6, top);
    }

    [TestMethod]
    public void SolidRectangleCompressesButWearAndCellPickingRemainIndependent()
    {
        var cells = (from x in Enumerable.Range(0, 4) from z in Enumerable.Range(0, 3)
                     select Cell(new(x, 0, z))).ToArray();
        var grid = new VoxelGrid([.. cells]);
        var merged = DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().ToArray();
        Assert.AreEqual(24, merged.Sum(item => item.Mesh.Vertices.Length));
        Assert.IsTrue(DesignGeometry.Meshes(grid, true).OfType<SceneVisual.Geometry>()
            .Sum(item => item.Mesh.Vertices.Length) > 24);
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
    public void FullChunkFacesMergeIncludingGlassFrontAndEveryTextureOrientation()
    {
        foreach (var normalAxis in Enumerable.Range(0, 3))
        {
            var cells = (from u in Enumerable.Range(0, 32) from v in Enumerable.Range(0, 32)
                         select Cell(normalAxis switch
                         {
                             0 => new(0, u, v), 1 => new(u, 0, v), _ => new(u, v, 0)
                         }, glass: true)).ToArray();
            Assert.AreEqual(24, DesignGeometry.Meshes(new([.. cells]), false)
                .OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length));
            AssertCoverage(cells);
        }
        foreach (var orientation in Enum.GetValues<VoxelOrientation>())
        foreach (var span in new[] { 1, 2, 4, 8 })
        {
            var cells = (from x in Enumerable.Range(-4, 3) from z in Enumerable.Range(-4, 3)
                         select Cell(new(x, -2, z), orientation, span: span)).ToArray();
            Assert.AreEqual(24, DesignGeometry.Meshes(new([.. cells]), false)
                .OfType<SceneVisual.Geometry>().Sum(item => item.Mesh.Vertices.Length));
            AssertCoverage(cells);
        }
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
        var occupied = cells.Select(cell => cell.At).ToHashSet();
        var counts = new Dictionary<(GridPosition, Vector3), int>();
        foreach (var visual in DesignGeometry.Meshes(new([.. cells]), false).OfType<SceneVisual.Geometry>())
        {
            var triangles = visual.Mesh.TriangleIndices.Chunk(3).Select(indices =>
            {
                Assert.HasCount(3, indices);
                Assert.IsTrue(indices.All(index => index >= 0 && index < visual.Mesh.Vertices.Length));
                var a = visual.Mesh.Vertices[indices[0]];
                var b = visual.Mesh.Vertices[indices[1]];
                var c = visual.Mesh.Vertices[indices[2]];
                Assert.AreEqual(a.Normal, b.Normal);
                Assert.AreEqual(a.Normal, c.Normal);
                Assert.IsTrue(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0);
                return (A: a.Position, B: b.Position, C: c.Position, a.Normal);
            }).ToArray();
            foreach (var quad in visual.Mesh.Vertices.Chunk(4))
            {
                Assert.HasCount(4, quad);
                CollectionAssert.AreEquivalent(new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY }, quad.Select(vertex => vertex.FaceUv).ToArray());
                var normal = quad[0].Normal;
                Assert.IsTrue(quad.All(vertex => vertex.Normal == normal && vertex.ExposedEdges == ExposedFaceEdges.None));
                var edgeU = quad[1].Position - quad[0].Position;
                var edgeV = quad[3].Position - quad[0].Position;
                Assert.IsTrue(Vector3.Dot(Vector3.Cross(edgeU, edgeV), normal) > 0);
                var covered = 0;
                foreach (var cell in cells)
                {
                    var adjacent = new GridPosition(cell.At.X + (int)normal.X, cell.At.Y + (int)normal.Y, cell.At.Z + (int)normal.Z);
                    if (occupied.Contains(adjacent)) continue;
                    var center = new Vector3(cell.At.X + 0.5f, cell.At.Y + 0.5f, cell.At.Z + 0.5f) + normal * 0.5f;
                    var delta = center - quad[0].Position;
                    var u = Vector3.Dot(delta, edgeU) / edgeU.LengthSquared();
                    var v = Vector3.Dot(delta, edgeV) / edgeV.LengthSquared();
                    if (Vector3.Dot(delta, normal) != 0 || u <= 0 || u >= 1 || v <= 0 || v >= 1) continue;
                    // Asymmetric interior samples avoid either legal rectangle diagonal.
                    foreach (var sign in new[] { -1, 1 })
                    {
                        var sample = center + Vector3.Normalize(edgeU) * (sign * 0.173f)
                            + Vector3.Normalize(edgeV) * (sign * 0.287f);
                        Assert.AreEqual(1, triangles.Count(triangle => triangle.Normal == normal &&
                            Vector3.Dot(sample - triangle.A, normal) == 0 &&
                            Vector3.Dot(Vector3.Cross(triangle.B - triangle.A, sample - triangle.A), normal) > 0 &&
                            Vector3.Dot(Vector3.Cross(triangle.C - triangle.B, sample - triangle.B), normal) > 0 &&
                            Vector3.Dot(Vector3.Cross(triangle.A - triangle.C, sample - triangle.C), normal) > 0));
                    }
                    covered++;
                    counts[(cell.At, normal)] = counts.GetValueOrDefault((cell.At, normal)) + 1;
                    var cube = (VoxelPresentation.Cube)cell.Appearance!.Presentation;
                    var name = cell.Orientation.ToString();
                    var up = Axis(name[2..]);
                    var forward = Vector3.Cross(Axis(name[..2]), up);
                    var front = normal == forward && cube.Front != cube.Side;
                    var texture = normal == up ? cube.Top : normal == -up ? cube.Bottom : front ? cube.Front : cube.Side;
                    Assert.AreEqual(new SceneMaterial.Voxel(texture, cube.Material, cell.Paint), visual.Mesh.Material);
                    var imageUp = normal == up || normal == -up ? forward : up;
                    var textureU = Vector3.Cross(imageUp, normal);
                    var textureV = -imageUp;
                    var span = front ? 1 : cube.TileSpan;
                    foreach (var cornerU in new[] { -0.5f, -0.125f, 0.5f })
                    foreach (var cornerV in new[] { -0.5f, 0.25f, 0.5f })
                    {
                        var point = center + Vector3.Normalize(edgeU) * cornerU + Vector3.Normalize(edgeV) * cornerV;
                        var offset = point - quad[0].Position;
                        var actual = quad[0].TextureUv + (quad[1].TextureUv - quad[0].TextureUv) * (Vector3.Dot(offset, edgeU) / edgeU.LengthSquared())
                            + (quad[3].TextureUv - quad[0].TextureUv) * (Vector3.Dot(offset, edgeV) / edgeV.LengthSquared());
                        AssertPhase(Vector3.Dot(point, textureU) / span, actual.X);
                        AssertPhase(Vector3.Dot(point, textureV) / span, actual.Y);
                    }
                    var chunk = VoxelGrid.Chunk(cell.At);
                    Assert.IsTrue(quad.All(vertex => vertex.Position.X >= chunk.X * 32f && vertex.Position.X <= (chunk.X + 1) * 32f &&
                        vertex.Position.Y >= chunk.Y * 32f && vertex.Position.Y <= (chunk.Y + 1) * 32f &&
                        vertex.Position.Z >= chunk.Z * 32f && vertex.Position.Z <= (chunk.Z + 1) * 32f));
                }
                Assert.AreEqual((int)(edgeU.Length() * edgeV.Length()), covered);
            }
        }
        foreach (var cell in cells)
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var adjacent = new GridPosition(cell.At.X + (int)normal.X, cell.At.Y + (int)normal.Y, cell.At.Z + (int)normal.Z);
            Assert.AreEqual(occupied.Contains(adjacent) ? 0 : 1, counts.GetValueOrDefault((cell.At, normal)));
        }
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


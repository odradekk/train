using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class CubeBatchTests
{
    [TestMethod]
    public void ObjectBatchesReduceResourcesWithoutChangingFacesOrCellPicking()
    {
        SceneVoxelProjection[] cells = [SceneFixtures.Cell(new(-33, 0, 0), 1),
            SceneFixtures.Cell(new(-32, 0, 0), 2) with { Paint = new("#ff0000") },
            SceneFixtures.Cell(new(31, 0, 0), 3), SceneFixtures.Cell(new(32, 0, 0), 4),
            SceneFixtures.Cell(new(64, 1, 0), 5) with { LocalOrientation = VoxelOrientation.PzPy }];
        var rendered = SceneFixtures.Build(SceneFixtures.Projection([.. cells]),
            content: SceneFixtures.Content(appearance: SceneFixtures.TexturedCube(4, front: true)));
        var grid = rendered.Picking.Single().Grid;
        var before = DesignGeometry.Meshes(grid, true).OfType<SceneVisual.Geometry>().ToArray();
        var after = rendered.Objects.Single(item => item.Parent is null).Visuals.OfType<SceneVisual.Geometry>().ToArray();

        Assert.IsTrue(after.Length < before.Length, "An object's compatible cube chunks should share fewer mesh resources.");
        AssertFaces(before, after);
        var frame = SceneFixtures.Frame(rendered);
        foreach (var cell in cells)
        {
            var ray = new SceneRay(new(cell.At.X + .5f, cell.At.Y + 3, cell.At.Z + .5f), -Vector3.UnitY, 4);
            var hit = ((ScenePick.Hit)frame.Pick(ray)).Value;
            Assert.AreEqual(cell.At, hit.Cell);
            Assert.AreEqual(grid.Find(cell.At)!.Value.Subject, hit.Subject);
            CollectionAssert.AreEqual(cell.ObjectPath.ToArray(), hit.NestedObjectPath.ToArray());
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void MaterialAndSurfaceBoundariesRemainSeparate(int changedField)
    {
        var first = Mesh(4);
        var surface = changedField switch
        {
            0 => first.Surface with { WearEnabled = false },
            1 => first.Surface with { WearSeed = 18 },
            2 => first.Surface with { Damage = .4f },
            _ => first.Surface with { Scorched = true }
        };
        var otherMaterial = new SceneMaterial.Voxel(new("other-tile"), MaterialCategory.Glass, new("#ff0000"));
        SceneVisual.Geometry[] source = [first, Mesh(4, surface: surface), Mesh(4),
            Mesh(4, otherMaterial), Mesh(4, otherMaterial)];
        var result = DesignGeometry.BatchCubes([.. source]).Cast<SceneVisual.Geometry>().ToArray();
        Assert.IsTrue(result.Length < source.Length);
        AssertFaces(source, result);
    }

    [TestMethod]
    public void EmptyAndSingletonInputsDoNotCreateReplacementResources()
    {
        Assert.IsTrue(DesignGeometry.BatchCubes([]).IsEmpty);
        var source = Mesh(4);
        var result = DesignGeometry.BatchCubes([source]);
        Assert.AreSame(source, result.Single());
    }

    [TestMethod]
    public void OversizedAndUncombinedMeshesKeepTheirExistingResources()
    {
        var first = Mesh(4);
        var oversized = Mesh(8193);
        var last = Mesh(4);
        SceneVisual[] source = [first, oversized, last];
        var result = DesignGeometry.BatchCubes([.. source]);
        Assert.IsTrue(result.Any(item => ReferenceEquals(item, oversized)), "An existing oversized mesh must remain unchanged.");
        Assert.AreSame(first, result[0]);
        Assert.AreSame(last, result[^1]);
        AssertFaces(source.Cast<SceneVisual.Geometry>().ToArray(), result.Cast<SceneVisual.Geometry>().ToArray());
    }

    [TestMethod]
    [DataRow(8191)]
    [DataRow(8192)]
    [DataRow(8193)]
    public void NewBatchesRespectTheVertexLimitIncludingItsBoundary(int total)
    {
        SceneVisual[] source = [Mesh(4096), Mesh(total - 4096)];
        var result = DesignGeometry.BatchCubes([.. source]).Cast<SceneVisual.Geometry>().ToArray();
        Assert.IsTrue(result.All(item => item.Mesh.Vertices.Length <= 8192));
        Assert.AreEqual(total, result.Sum(item => item.Mesh.Vertices.Length));
        if (total <= 8192) Assert.IsTrue(result.Length < source.Length);
        AssertFaces(source.Cast<SceneVisual.Geometry>().ToArray(), result);
    }

    private static SceneVisual.Geometry Mesh(int count, SceneMaterial? material = null, SurfaceState? surface = null) =>
        new(new(material ?? new SceneMaterial.Voxel(new("tile"), MaterialCategory.PaintedMetal, null),
            Enumerable.Range(0, count).Select(index => new SceneVertex(new(index, 2, 3), Vector3.UnitY,
                new(index / 7f, .3f), new(.4f, .5f), ExposedFaceEdges.MinimumU, .6f, .7f)).ToImmutableArray(),
            [0, 1, 2]), surface ?? new(true, 17, .2f, false));

    private static void AssertFaces(SceneVisual.Geometry[] before, SceneVisual.Geometry[] after)
    {
        var keys = before.Select(item => (item.Mesh.Material, item.Surface)).ToHashSet();
        Assert.IsTrue(keys.SetEquals(after.Select(item => (item.Mesh.Material, item.Surface))));
        foreach (var key in keys)
        {
            var old = before.Where(item => (item.Mesh.Material, item.Surface) == key).ToArray();
            var current = after.Where(item => (item.Mesh.Material, item.Surface) == key).ToArray();
            CollectionAssert.AreEqual(old.SelectMany(item => item.Mesh.Vertices).ToArray(),
                current.SelectMany(item => item.Mesh.Vertices).ToArray());
            CollectionAssert.AreEqual(old.SelectMany(item => item.Mesh.TriangleIndices.Select(index => item.Mesh.Vertices[index])).ToArray(),
                current.SelectMany(item => item.Mesh.TriangleIndices.Select(index => item.Mesh.Vertices[index])).ToArray());
        }
    }
}

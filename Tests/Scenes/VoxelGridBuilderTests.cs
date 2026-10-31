using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class VoxelGridBuilderTests
{
    [TestMethod]
    public void BuilderInputPreservesGridValuesAndPickingAfterTheBuilderChanges()
    {
        var first = new SceneCell(new(-33, -1, -17), new(VoxelOrientation.PxPy, SceneFixtures.Cube, null,
            null, new PickSubject.Terrain(new WorldLocation.Node(new(1))), []));
        ImmutableArray<SceneCell>[] cases = [[], [first, first with { At = new(32, 0, 16) },
            first with { At = new(-32, -1, -17) }, first with { At = new(0, 0, 0) }]];
        foreach (var cells in cases)
        {
            var builder = cells.ToBuilder();
            var expected = new VoxelGrid(cells);
            var actual = VoxelGrid.FromBuilder(builder);
            CollectionAssert.AreEqual(cells.ToArray(), builder.ToArray());
            AssertGrid();
            builder.Clear();
            builder.Add(first with { At = new(500, 500, 500) });
            AssertGrid();
            Assert.IsNull(actual.Find(new(500, 500, 500)));

            void AssertGrid()
            {
                CollectionAssert.AreEqual(expected.Cells.ToArray(), actual.Cells.ToArray());
                Assert.AreEqual(expected.Bounds, actual.Bounds);
                foreach (var cell in cells)
                {
                    var ray = new SceneRay(new(cell.At.X + .5f, cell.At.Y - 2, cell.At.Z + .5f), Vector3.UnitY, 4);
                    Assert.IsNotNull(actual.Pick(ray));
                    Assert.AreEqual(expected.Pick(ray), actual.Pick(ray));
                }
                if (cells.IsEmpty)
                    Assert.IsNull(actual.Pick(new(Vector3.Zero, Vector3.UnitY, 4)));
            }
        }
        var duplicate = ImmutableArray.Create(first, first).ToBuilder();
        Assert.AreEqual("cells", Assert.ThrowsExactly<ArgumentException>(() => VoxelGrid.FromBuilder(duplicate)).ParamName);
        Assert.AreEqual("cells", Assert.ThrowsExactly<ArgumentException>(() => new VoxelGrid(default(ImmutableArray<SceneCell>))).ParamName);
    }
}

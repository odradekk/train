using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class VoxelGridStorageTests
{
    [TestMethod]
    public void Array_budget_distinguishes_half_widths_with_the_same_palette()
    {
        var cells = Cells(256);
        cells = cells.SetItem(255, cells[255] with { At = new(-32, -32, -16) });
        var narrowHalf = new VoxelGrid([.. cells, cells[0] with { At = new(-1, -1, -17) }]);
        var wideHalves = new VoxelGrid([.. cells, cells[255] with { At = new(-1, -1, -17) }]);

        Assert.IsLessThan(wideHalves.ArrayBytes, narrowHalf.ArrayBytes,
            "A half that only uses byte-sized IDs must consume less of the idle cache budget.");
    }

    [TestMethod]
    [DataRow(255)]
    [DataRow(256)]
    [DataRow(65536)]
    public void Palette_ids_preserve_cell_properties_and_negative_coordinates(int count)
    {
        var cells = Cells(count);
        Equal(cells, new(cells));
    }

    [TestMethod]
    public void Adding_replacing_and_removing_properties_preserves_older_grids()
    {
        var cells = Cells(255);
        var original = new VoxelGrid(cells);
        var addedCell = Cells(256)[255];
        var addedCells = cells.Add(addedCell);
        var added = new VoxelGrid(addedCells, original, out var additions);
        CollectionAssert.AreEqual(new[] { addedCell.At }, additions.ToArray());

        var removedCells = cells.RemoveAt(0);
        var removed = new VoxelGrid(removedCells, added, out var removals);
        CollectionAssert.AreEquivalent(new[] { cells[0].At, addedCell.At }, removals.ToArray());

        var changedCell = removedCells[0] with
        { Properties = new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, null, [999999]) };
        var changedCells = removedCells.SetItem(0, changedCell);
        var changed = new VoxelGrid(changedCells, removed, out var replacements);
        CollectionAssert.AreEqual(new[] { changedCell.At }, replacements.ToArray());

        Equal(cells, original);
        Equal(addedCells, added);
        Equal(removedCells, removed);
        Equal(changedCells, changed);
        Assert.IsFalse(removed.Contains(cells[0].At));
        Assert.IsFalse(removed.Contains(addedCell.At));
        Assert.IsLessThan(added.ArrayBytes, removed.ArrayBytes);
    }

    private static ImmutableArray<SceneCell> Cells(int count) => Enumerable.Range(0, count).Select(index =>
        new SceneCell(new(-32 + (index & 31), -32 + ((index >> 5) & 31), -17 + (index >> 10)),
            new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, null, [index]))).ToImmutableArray();

    private static void Equal(ImmutableArray<SceneCell> expected, VoxelGrid grid)
    {
        CollectionAssert.AreEquivalent(expected.ToArray(), grid.Cells.ToArray());
        foreach (var cell in expected)
        {
            Assert.AreEqual(cell, grid.Find(cell.At));
            Assert.IsTrue(grid.Contains(cell.At));
        }
    }
}

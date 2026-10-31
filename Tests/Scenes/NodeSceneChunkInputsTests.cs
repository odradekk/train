using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSceneChunkInputsTests
{
    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void ChunkInputsIncludeBedsOnEachSideAndCornerOfTheOneCellHalo(int x, int z)
    {
        var prepared = Empty();
        var from = new GridColumn(x, z);
        var emptyInputs = prepared.ChunkInputs(from);
        var size = VoxelGrid.ChunkSize;
        GridColumn[] offsets = [new(-1, 0), new(size, 0), new(0, -1), new(0, size),
            new(-1, -1), new(-1, size), new(size, -1), new(size, size)];

        foreach (var offset in offsets)
        {
            var column = new GridColumn(x + offset.X, z + offset.Z);
            Assert.AreNotEqual(emptyInputs, WithBed(prepared, column).ChunkInputs(from),
                $"Bed at {column} must affect chunk {from}.");
        }
    }

    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void ChunkInputsIgnoreBedsBeyondTheOneCellHalo(int x, int z)
    {
        var prepared = Empty();
        var from = new GridColumn(x, z);
        var emptyInputs = prepared.ChunkInputs(from);
        var size = VoxelGrid.ChunkSize;
        GridColumn[] offsets = [new(-2, 0), new(size + 1, 0), new(0, -2), new(0, size + 1),
            new(-2, -1), new(-1, -2), new(-2, size), new(-1, size + 1),
            new(size + 1, -1), new(size, -2), new(size + 1, size), new(size, size + 1)];

        foreach (var offset in offsets)
        {
            var column = new GridColumn(x + offset.X, z + offset.Z);
            Assert.AreEqual(emptyInputs, WithBed(prepared, column).ChunkInputs(from),
                $"Bed at {column} must not affect chunk {from}.");
        }
    }

    private static NodeScene.Preparation Empty()
    {
        var layout = NodeFixtures.Layout;
        layout = layout with { SceneModel = layout.SceneModel with { Scene = SceneFixtures.Projection(
            [SceneFixtures.Cell(new(-64, 0, -64)), SceneFixtures.Cell(new(64, 0, 64))]) } };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, []);
        return NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId)) with { Beds = [] };
    }

    private static NodeScene.Preparation WithBed(NodeScene.Preparation prepared, GridColumn column)
    {
        return prepared with { Beds = new() { [column] = new(0, 7) } };
    }
}

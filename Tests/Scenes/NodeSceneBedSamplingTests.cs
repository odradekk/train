using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSceneBedSamplingTests
{
    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void LaterOffsetInTheSameColumnKeepsTheCloserBed(int x, int z)
    {
        var prepared = Empty();
        NodeScene.RasterizeBed(prepared.Beds, arc => Sample(x, z, 8, arc), 0.25);

        var part = prepared.BuildChunk(new(x, z));
        // Offsets -10 and -9.5 share this column, on opposite sides of the bed's top-width boundary.
        Assert.AreEqual(7d, part.Heights[new(x + 6, z + 16)]);
    }

    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void EqualDistanceOnAdjacentArcsKeepsTheEarlierHeight(int x, int z)
    {
        var prepared = Empty();
        NodeScene.RasterizeBed(prepared.Beds, arc => Sample(x, z, 8 + 8 * arc, arc), 0.5);
        var constant = Empty();
        NodeScene.RasterizeBed(constant.Beds, arc => Sample(x, z, 8, arc), 0.5);

        Assert.AreEqual(constant.ChunkInputs(new(x, z)), prepared.ChunkInputs(new(x, z)));
        NodeFixtures.AssertPartEqual(constant.BuildChunk(new(x, z)), prepared.BuildChunk(new(x, z)));
        Assert.AreEqual(7d, prepared.BuildChunk(new(x, z)).Heights[new(x + 16, z + 16)]);
    }

    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void EqualDistanceAcrossRoutesKeepsTheFirstRoute(int x, int z)
    {
        foreach (var firstHeight in new[] { 8d, 12d })
        {
            var prepared = Empty();
            var first = Empty();
            NodeScene.RasterizeBed(first.Beds, arc => Sample(x, z, firstHeight, arc), 0.5);
            NodeScene.RasterizeBed(prepared.Beds, arc => Sample(x, z, firstHeight, arc), 0.5);
            NodeScene.RasterizeBed(prepared.Beds, arc => Sample(x, z, 20 - firstHeight, arc), 0.5);

            Assert.AreEqual(first.ChunkInputs(new(x, z)), prepared.ChunkInputs(new(x, z)));
            NodeFixtures.AssertPartEqual(first.BuildChunk(new(x, z)), prepared.BuildChunk(new(x, z)));
        }
    }

    [TestMethod]
    [DataRow(32, 32)]
    [DataRow(-32, -32)]
    [DataRow(-32, 32)]
    [DataRow(32, -32)]
    public void EndpointBetweenArcStepsStillContributesTerrain(int x, int z)
    {
        var prepared = Empty();
        NodeScene.RasterizeBed(prepared.Beds, arc => Sample(x, z, 8, arc), 0.75);

        var column = new GridColumn(x + 16, z + 17);
        Assert.AreNotEqual(7d, Empty().BuildChunk(new(x, z)).Heights[column]);
        Assert.AreEqual(7d, prepared.BuildChunk(new(x, z)).Heights[column]);
    }

    private static RailwaySample Sample(int x, int z, double height, double arc) =>
        new(new ScenePoint(x + 16.25, height, z + 16.25 + arc), Vector3.UnitZ, Vector3.UnitX);

    private static NodeScene.Preparation Empty()
    {
        var layout = NodeFixtures.Layout;
        layout = layout with { SceneModel = layout.SceneModel with { Scene = SceneFixtures.Projection(
            [SceneFixtures.Cell(new(-64, 0, -64)), SceneFixtures.Cell(new(64, 0, 64))]) } };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, []);
        return NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId)) with { Beds = [] };
    }
}

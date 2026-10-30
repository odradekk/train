using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayMeshStorageTests
{
    [TestMethod]
    [DataRow(BuiltInMaterial.RailSteel, true)]
    [DataRow(BuiltInMaterial.Sleeper, false)]
    public void PlanarQuadsReuseTheirRepeatedCornersWithoutChangingExpandedAttributes(BuiltInMaterial material, bool railHead)
    {
        var builder = new RailwayMeshes.MeshBuilder(material);
        builder.Quad(Vector3.Zero, Vector3.UnitX, new(1, 1, 0), Vector3.UnitY, railHead);
        var mesh = builder.Build();
        var a = Vertex(Vector3.Zero, Vector3.UnitZ, railHead);
        var b = Vertex(Vector3.UnitX, Vector3.UnitZ, railHead);
        var c = Vertex(new(1, 1, 0), Vector3.UnitZ, railHead);
        var d = Vertex(Vector3.UnitY, Vector3.UnitZ, railHead);
        CollectionAssert.AreEqual(new[] { a, b, c, a, c, d }, Expanded(mesh));
        Assert.AreEqual(new SceneMaterial.BuiltIn(material), mesh.Material);
        Assert.AreEqual(mesh.TriangleIndices[0], mesh.TriangleIndices[3], "第一个公共角必须共用完整顶点。");
        Assert.AreEqual(mesh.TriangleIndices[2], mesh.TriangleIndices[4], "第二个公共角必须共用完整顶点。");
        Assert.AreEqual(mesh.TriangleIndices.Distinct().Count(), mesh.Vertices.Length, "不能留下不再被索引使用的重复顶点。");
    }

    [TestMethod]
    public void FoldedQuadsKeepDistinctNormalsAtBothSharedPositions()
    {
        var builder = new RailwayMeshes.MeshBuilder(BuiltInMaterial.RailSteel);
        builder.Quad(Vector3.Zero, Vector3.UnitX, new(1, 1, 0), new(0, 1, 1), true);
        var mesh = builder.Build();
        var foldedNormal = Vector3.Normalize(new(1, -1, 1));
        CollectionAssert.AreEqual(new[]
        {
            Vertex(Vector3.Zero, Vector3.UnitZ, true), Vertex(Vector3.UnitX, Vector3.UnitZ, true),
            Vertex(new(1, 1, 0), Vector3.UnitZ, true), Vertex(Vector3.Zero, foldedNormal, true),
            Vertex(new(1, 1, 0), foldedNormal, true), Vertex(new(0, 1, 1), foldedNormal, true)
        }, Expanded(mesh));
        Assert.AreNotEqual(mesh.TriangleIndices[0], mesh.TriangleIndices[3]);
        Assert.AreNotEqual(mesh.TriangleIndices[2], mesh.TriangleIndices[4]);
    }

    [TestMethod]
    public void AdjacentFacesKeepTheirOwnRailHeadUvsAndPreviouslyBuiltMeshesStayUnchanged()
    {
        var builder = new RailwayMeshes.MeshBuilder(BuiltInMaterial.RailSteel);
        builder.Quad(Vector3.Zero, Vector3.UnitX, new(1, 1, 0), Vector3.UnitY, true);
        var first = builder.Build();
        var expected = new[]
        {
            Vertex(Vector3.Zero, Vector3.UnitZ, true), Vertex(Vector3.UnitX, Vector3.UnitZ, true),
            Vertex(new(1, 1, 0), Vector3.UnitZ, true), Vertex(Vector3.Zero, Vector3.UnitZ, true),
            Vertex(new(1, 1, 0), Vector3.UnitZ, true), Vertex(Vector3.UnitY, Vector3.UnitZ, true)
        };
        builder.Quad(Vector3.Zero, Vector3.UnitX, new(1, 1, 0), Vector3.UnitY, false);
        var next = builder.Build();
        CollectionAssert.AreEqual(expected, Expanded(first));
        CollectionAssert.AreEqual(expected.Select(vertex => vertex with { TextureUv = Vector2.Zero }).ToArray(), Expanded(next));
    }

    private static SceneVertex Vertex(Vector3 point, Vector3 normal, bool railHead) =>
        new(point, normal, new(railHead ? 1 : 0, 0), Vector2.Zero, ExposedFaceEdges.None, 0, 0);

    private static SceneVertex[] Expanded(SceneMesh mesh) => mesh.TriangleIndices.Select(index => mesh.Vertices[index]).ToArray();
}

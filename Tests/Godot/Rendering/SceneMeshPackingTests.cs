using Microsoft.VisualStudio.TestTools.UnitTesting;
using GVector2 = global::Godot.Vector2;
using GVector3 = global::Godot.Vector3;
using Train.Content.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Godot.Tests.Rendering;

[TestClass]
public sealed class SceneMeshPackingTests
{
    [TestMethod]
    [DataRow(BuiltInMaterial.RailSteel)]
    [DataRow(BuiltInMaterial.Sleeper)]
    [DataRow(BuiltInMaterial.Water)]
    [DataRow(BuiltInMaterial.Construction)]
    public void PackingZeroBuiltInChannelsOmitsOnlyTheOptionalArrays(BuiltInMaterial kind)
    {
        var packed = PackedSceneMesh.Create(Triangle(new SceneMaterial.BuiltIn(kind)), CancellationToken.None);
        Assert.IsNull(packed.FaceUvs);
        Assert.IsNull(packed.Custom);
        CollectionAssert.AreEqual(new[] { GVector3.Zero, GVector3.Right, GVector3.Up }, packed.Positions);
        CollectionAssert.AreEqual(Enumerable.Repeat(GVector3.Back, 3).ToArray(), packed.Normals);
        CollectionAssert.AreEqual(new[] { GVector2.Zero, GVector2.Zero, GVector2.One }, packed.TextureUvs);
        CollectionAssert.AreEqual(new[] { 0, 2, 1 }, packed.Indices);
    }

    [TestMethod]
    [DataRow(nameof(SceneVertex.FaceUv))]
    [DataRow(nameof(SceneVertex.ExposedEdges))]
    [DataRow(nameof(SceneVertex.RelativeHeight))]
    [DataRow(nameof(SceneVertex.Damage))]
    public void PackingKeepsBothOptionalChannelsWhenAnyVertexNeedsThem(string field)
    {
        var source = Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel));
        var last = source.Vertices[^1];
        last = field switch
        {
            nameof(SceneVertex.FaceUv) => last with { FaceUv = new(0.25f, 0.75f) },
            nameof(SceneVertex.ExposedEdges) => last with { ExposedEdges = ExposedFaceEdges.MaximumV },
            nameof(SceneVertex.RelativeHeight) => last with { RelativeHeight = 0.12345679f },
            _ => last with { Damage = 0.625f }
        };
        var packed = PackedSceneMesh.Create(source with { Vertices = source.Vertices.SetItem(2, last) }, CancellationToken.None);
        Assert.IsNotNull(packed.FaceUvs);
        Assert.AreEqual(new GVector2(last.FaceUv.X, last.FaceUv.Y), packed.FaceUvs[^1]);
        Assert.IsNotNull(packed.Custom);
        CollectionAssert.AreEqual(new[] { 0f, 0f, 0f, 0f, 0f, 0f,
            (float)last.ExposedEdges, last.RelativeHeight, last.Damage }, packed.Custom);
    }

    [TestMethod]
    public void PackingVoxelMeshesKeepsTheirSurfaceChannelsEvenWhenTheCurrentValuesAreZero()
    {
        var packed = PackedSceneMesh.Create(Triangle(new SceneMaterial.Voxel(new("face"), MaterialCategory.PaintedMetal, null)),
            CancellationToken.None);
        Assert.IsNotNull(packed.FaceUvs);
        CollectionAssert.AreEqual(new GVector2[3], packed.FaceUvs);
        Assert.IsNotNull(packed.Custom);
        CollectionAssert.AreEqual(new float[9], packed.Custom);
    }

    [TestMethod]
    public void PackingCancelledBuiltInMeshesDoesNotPublishPackedArrays()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PackedSceneMesh.Create(
            Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper)), cancellation.Token));
    }

    private static SceneMesh Triangle(SceneMaterial material)
    {
        SceneVertex Vertex(NVector3 position, NVector2 uv) => new(position, NVector3.UnitZ, uv,
            NVector2.Zero, ExposedFaceEdges.None, 0, 0);
        return new(material, [Vertex(NVector3.Zero, NVector2.Zero), Vertex(NVector3.UnitX, NVector2.Zero),
            Vertex(NVector3.UnitY, NVector2.One)], [0, 1, 2]);
    }
}

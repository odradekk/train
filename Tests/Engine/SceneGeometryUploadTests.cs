using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class RenderingResourceTests
{
    [Test]
    public void Built_in_geometry_with_zero_surface_channels_uploads_no_optional_attributes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        SceneVertex Vertex(NVector3 position) => new(position, NVector3.UnitZ, NVector2.Zero,
            NVector2.Zero, ExposedFaceEdges.None, 0, 0);
        var source = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
            [Vertex(NVector3.Zero), Vertex(NVector3.UnitX), Vertex(NVector3.UnitY)], [0, 1, 2]);
        using var visual = resources.Prepare(new SceneVisual.Geometry(source, new(false, 0, 0, false)));
        var mesh = (ArrayMesh)Instance(visual).Mesh;
        using var arrays = mesh.SurfaceGetArrays(0);
        Require(arrays[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil &&
            arrays[(int)Mesh.ArrayType.Custom0].VariantType == Variant.Type.Nil,
            "全零内置网格不能上传省略的 UV2 或 CUSTOM0 数组。");
        Require((mesh.SurfaceGetFormat(0) & (Mesh.ArrayFormat.FormatTexUV2 | Mesh.ArrayFormat.FormatCustom0)) == 0,
            "原生格式不能声明未上传的可选属性。");
        Require(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length == source.Vertices.Length &&
            arrays[(int)Mesh.ArrayType.Index].AsInt32Array().SequenceEqual(new[] { 0, 2, 1 }),
            "省略可选通道不能改变几何或正面绕序。");
    }
}

using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;
using static Train.Engine.Tests.SceneDisplayTestDriver;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class RenderingResourceTests
{
    [Test]
    public async Task Geometry_uploads_outward_faces_and_all_vertex_and_surface_channels()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("channels");
        using var resources = new SceneResources(cache, Content(asset));
        var source = Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#80a0c0")));
        var surface = new SurfaceState(true, 0xfedcba9876543210UL, 0.375f, true);
        using var packing = new SceneMeshPacking([source], null, PackedSceneMesh.Create);
        await packing.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        using var visual = PrepareVisual(resources, cache, new SceneVisual.Geometry(source, surface), packing);
        var instance = Instance(visual);
        var mesh = (ArrayMesh)instance.Mesh;
        using var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var faceUv = arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        var custom = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        Require(vertices.Length == source.Vertices.Length && normals.Length == vertices.Length &&
            uv.Length == vertices.Length && faceUv.Length == vertices.Length && custom.Length == vertices.Length * 3 &&
            indices.Length == source.TriangleIndices.Length, "后台打包必须保留全部顶点、索引与表现通道。");
        var customFormat = ((ulong)mesh.SurfaceGetFormat(0) >> (int)Mesh.ArrayFormat.FormatCustom0Shift)
            & (ulong)Mesh.ArrayFormat.FormatCustomMask;
        Require(customFormat == (ulong)Mesh.ArrayCustomFormat.RgbFloat, "三个表现通道需要完整 float 精度。");
        var outward = (vertices[indices[2]] - vertices[indices[0]]).Cross(vertices[indices[1]] - vertices[indices[0]]).Normalized();
        Require(outward.Dot(Vector3.Back) > 0.999f, "正面绕序没有保持契约的外侧。");
        for (var index = 0; index < source.Vertices.Length; index++)
        {
            var expected = source.Vertices[index];
            Require(vertices[index].IsEqualApprox(new Vector3(expected.Position.X, expected.Position.Y, expected.Position.Z)), "格坐标被缩放或反射。");
            Require(normals[index].Dot(outward) > 0.999f, "上传法线不朝外。");
            Require(uv[index].IsEqualApprox(new Vector2(expected.TextureUv.X, expected.TextureUv.Y)), "TextureUv 丢失。");
            Require(faceUv[index].IsEqualApprox(new Vector2(expected.FaceUv.X, expected.FaceUv.Y)), "FaceUv 丢失。");
            Near(custom[index * 3], (int)expected.ExposedEdges);
            Near(custom[index * 3 + 1], expected.RelativeHeight);
            Near(custom[index * 3 + 2], expected.Damage);
        }
        for (var index = 0; index < indices.Length; index += 3)
            Require(indices[index] == source.TriangleIndices[index] &&
                indices[index + 1] == source.TriangleIndices[index + 2] &&
                indices[index + 2] == source.TriangleIndices[index + 1], "后台打包必须完整保留三角形顺序并转换绕序。");
        var material = Material(visual);
        AssertSurface(material, surface);
        Require(material.GetShaderParameter("has_paint").AsBool(), "几何涂装开关未上传。");
        Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#80a0c0")), "几何涂装颜色未上传。");
        var next = new SceneVisual.Geometry(source with
        {
            Material = new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#102030"))
        }, new(false, ulong.MaxValue, 0.75f, false));
        Require(visual.CanUpdate(next), "只改表面状态和涂装不应重建几何。");
        visual.Update(next);
        AssertSurface(material, next.Surface);
        Require(ReferenceEquals(instance.Mesh, mesh), "更新表面状态重建了网格。");
        Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#102030")), "更新没有替换涂装颜色。");
    }

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
        using var visual = PrepareVisual(resources, cache, new SceneVisual.Geometry(source, new(false, 0, 0, false)));
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

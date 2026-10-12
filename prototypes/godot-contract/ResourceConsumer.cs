using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.ContractSamples;

public static class ResourceConsumer
{
    // 一格 0.5 米，但一格就是一个 Godot 单位。SceneMesh 不再缩放；
    // GLB 原始米坐标才统一乘 2。+Z 仍为内容正面，无轴反射。
    public static Vector3 CellPosition(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
    public static Vector3 ModelMetersToCells(Vector3 meters) => meters * 2;

    public static ArrayMesh CreateMesh(SceneMesh source)
    {
        var vertices = source.Vertices.Select(v => CellPosition(v.Position)).ToArray();
        var normals = source.Vertices.Select(v => CellPosition(v.Normal)).ToArray();
        var uv = source.Vertices.Select(v => new Vector2(v.TextureUv.X, v.TextureUv.Y)).ToArray();
        var indices = source.TriangleIndices.ToArray();
        // 契约为外侧看 CCW；Godot 三角形正面为 CW。交换两点，保留外向法线。
        for (var i = 0; i < indices.Length; i += 3)
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        using var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public static ImageTexture CreatePngTexture(ContentAsset asset)
    {
        if (asset.Metadata is not AssetMetadata.Png)
            throw new ArgumentException("需要 PNG 资产。", nameof(asset));
        using var image = new Image();
        var error = image.LoadPngFromBuffer(asset.Bytes.ToArray());
        if (error != Error.Ok)
            throw new InvalidOperationException($"固定 PNG 字节解码失败：{error}");
        return ImageTexture.CreateFromImage(image);
    }
}

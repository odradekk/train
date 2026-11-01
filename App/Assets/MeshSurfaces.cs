using System.Diagnostics;
using Godot;

namespace Train.App.Assets;

// 绕开 Godot 4.7.2 C# 绑定：原生调用期间不保活托管参数。数组、默认的 blendShapes/lods
// 和临时 Variant 可能在调用中途被终结器释放，损坏原生堆（#839）。
// 所有 AddSurfaceFromArrays 都经过这里。类型放在 App/Assets，资产导出探针才能编译同一份源码。
// 升级到包含参数保活（上游 #124820）的 Godot 后可以去掉这层。
internal static class MeshSurfaces
{
    internal static void Add(ArrayMesh mesh, Mesh.PrimitiveType primitive, Mesh.ArrayFormat flags = default,
        Godot.Collections.Array? source = null, Vector3[]? vertices = null, Vector3[]? normals = null,
        Vector2[]? textureUvs = null, Vector2[]? faceUvs = null, float[]? custom = null,
        float[]? tangents = null, int[]? indices = null, Action<string, long, long>? diagnose = null)
    {
        var started = diagnose is null ? 0 : Stopwatch.GetTimestamp();
        using var arrays = source?.Duplicate() ?? new Godot.Collections.Array();
        if (source is null) arrays.Resize((int)Mesh.ArrayType.Max);
        var blendShapes = new Godot.Collections.Array<Godot.Collections.Array>();
        using var lods = new Godot.Collections.Dictionary();
        using var vertexValues = Pack(vertices);
        using var normalValues = Pack(normals);
        using var textureUvValues = Pack(textureUvs);
        using var faceUvValues = Pack(faceUvs);
        using var customValues = Pack(custom);
        using var tangentValues = Pack(tangents);
        using var indexValues = Pack(indices);
        Write(arrays, Mesh.ArrayType.Vertex, vertexValues);
        Write(arrays, Mesh.ArrayType.Normal, normalValues);
        Write(arrays, Mesh.ArrayType.TexUV, textureUvValues);
        Write(arrays, Mesh.ArrayType.TexUV2, faceUvValues);
        Write(arrays, Mesh.ArrayType.Custom0, customValues);
        Write(arrays, Mesh.ArrayType.Tangent, tangentValues);
        Write(arrays, Mesh.ArrayType.Index, indexValues);
        if (diagnose is not null)
            diagnose("Prepare.ManagedToGodotArrays", started, Stopwatch.GetTimestamp());
        started = diagnose is null ? 0 : Stopwatch.GetTimestamp();
        mesh.AddSurfaceFromArrays(primitive, arrays, blendShapes, lods, flags);
        if (diagnose is not null)
            diagnose("Prepare.AddSurfaceFromArrays", started, Stopwatch.GetTimestamp());
        // 泛型 Array<T> 没有 Dispose。调用后保活这个包装，它持有的原生数组才不会在调用期间被终结。
        GC.KeepAlive(blendShapes);
    }

    private static void Write(Godot.Collections.Array arrays, Mesh.ArrayType slot, Variant packed)
    {
        if (packed.VariantType != Variant.Type.Nil) arrays[(int)slot] = packed;
    }

    private static Variant Pack(Vector3[]? values) => values is null ? default : Variant.From(values);
    private static Variant Pack(Vector2[]? values) => values is null ? default : Variant.From(values);
    private static Variant Pack(float[]? values) => values is null ? default : Variant.From(values);
    private static Variant Pack(int[]? values) => values is null ? default : Variant.From(values);
}

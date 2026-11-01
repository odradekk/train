using Godot;
using Train.App.Assets;

namespace Train.Rendering;

internal sealed partial class SceneResources
{
    private static ArrayMesh CreateMesh(PackedSceneMesh source)
    {
        var mesh = new ArrayMesh();
        try
        {
            MeshSurfaces.Add(mesh, Mesh.PrimitiveType.Triangles,
                flags: source.Custom is null ? default :
                    (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift),
                vertices: source.Positions, normals: source.Normals, textureUvs: source.TextureUvs,
                faceUvs: source.FaceUvs, custom: source.Custom, indices: source.Indices,
                diagnose: SceneDisplayDiagnostics.Enabled ? SceneDisplayDiagnostics.SurfaceInterval : null);
            if (mesh.GetSurfaceCount() != 1) throw new InvalidOperationException("场景网格创建失败。");
            return mesh;
        }
        catch { mesh.Dispose(); throw; }
    }
}

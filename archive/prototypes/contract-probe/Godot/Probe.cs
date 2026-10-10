using Godot;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using Train.Godot.ContractSamples;
using NVector = System.Numerics.Vector3;
using FileAccess = Godot.FileAccess;

public partial class Probe : Node
{
    // 可抛弃原型补充最小模型适配；元数据是明确配置的固定夹具。
    private static Node CreateModel(ContentAsset asset, GltfState state)
    {
        if (asset.Metadata is not AssetMetadata.Glb) throw new ArgumentException("Expected GLB fixture", nameof(asset));
        using var document = new GltfDocument();
        var error = document.AppendFromBuffer(asset.Bytes.ToArray(), "", state);
        if (error != Error.Ok) throw new InvalidOperationException("Fixed GLB decode failed: " + error);
        return document.GenerateScene(state) ?? throw new InvalidOperationException("Fixed GLB scene missing");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        GD.Print("PASS " + name);
    }

    public override void _Ready()
    {
        try
        {
            GD.Print("ENGINE " + Engine.GetVersionInfo()["string"]);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Train")))
                GD.Print("ASSEMBLY " + assembly.FullName + " " + assembly.Location);
            var png = FileAccess.GetFileAsBytes("res://fixture.png");
            var glb = FileAccess.GetFileAsBytes("res://fixture.glb");
            Check(Convert.ToHexString(SHA256.HashData(glb)) == FileAccess.GetFileAsString("res://fixture.glb.sha256").Trim(), "res raw GLB SHA256 (PCK run uses Keep File)");
            Check(Convert.ToHexString(SHA256.HashData(png)) == FileAccess.GetFileAsString("res://fixture.png.sha256").Trim(), "res raw PNG SHA256");
            Check(DirAccess.MakeDirRecursiveAbsolute("user://mods") == Error.Ok, "isolated user mods directory");
            using (var file = FileAccess.Open("user://mods/fixture.glb", FileAccess.ModeFlags.Write)) file.StoreBuffer(glb);
            Check(FileAccess.GetFileAsBytes("user://mods/fixture.glb").SequenceEqual(glb), "user mods raw GLB");
            GD.Print("USER_DIR " + OS.GetUserDataDir());
            using var image = new Image();
            Check(image.LoadPngFromBuffer(png) == Error.Ok && image.GetWidth() == 2 && image.GetHeight() == 2, "PNG metadata 2x2");
            var asset = new ContentAsset(new AssetRevision("fixed-png"), png.ToImmutableArray(), new AssetMetadata.Png(2, 2), []);
            using var texture = ResourceConsumer.CreatePngTexture(asset);
            Check(texture.GetWidth() == 2 && texture.GetHeight() == 2, "existing ResourceConsumer texture from ContentAsset bytes");
            var broken = asset with { Bytes = ImmutableArray.Create<byte>(1, 2, 3) };
            bool rejected = false;
            try { using var invalid = ResourceConsumer.CreatePngTexture(broken); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "corrupt PNG rejected");
            using var document = new GltfDocument();
            using var state = new GltfState();
            var modelRevision = new AssetRevision(Convert.ToHexString(SHA256.HashData(glb)));
            var modelAsset = new ContentAsset(modelRevision, glb.ToImmutableArray(),
                new AssetMetadata.Glb(1, [AssetAlphaMode.Opaque], new(new(0, 0, 0), new(0.5, 0.5, 0)), false, false, false, false, false, false), []);
            // 固定后修改实际来源，确认适配使用 ContentAsset.Bytes。
            using (var changed = FileAccess.Open("user://mods/fixture.glb", FileAccess.ModeFlags.Write)) changed.StoreBuffer(new byte[] { 1, 2, 3 });
            Check(!FileAccess.GetFileAsBytes("user://mods/fixture.glb").SequenceEqual(modelAsset.Bytes), "GLB source changed after ContentAsset freeze");
            var root = CreateModel(modelAsset, state);
            Check(modelAsset.Revision == modelRevision && Convert.ToHexString(SHA256.HashData(modelAsset.Bytes.AsSpan())) == modelRevision.Value, "model ContentAsset revision still matches fixed original bytes");
            Check(GodotObject.IsInstanceValid(root), "GLB model created from ContentAsset despite changed source");
            var meshNode = root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Single();
            var bounds = meshNode.Mesh.GetAabb();
            GD.Print("GLB_BOUNDS " + bounds);
            // Godot 会给平面模型包围盒约 1e-5 的厚度；容差只针对这个引擎行为。
            Check(bounds.Position.DistanceTo(Vector3.Zero) < 0.0001f && bounds.Size.DistanceTo(new Vector3(0.5f, 0.5f, 0)) < 0.0001f, "GLB meter bounds with engine planar epsilon");
            using var glbArrays = meshNode.Mesh.SurfaceGetArrays(0);
            var modelVertices = glbArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Check(modelVertices.Length == 3 && modelVertices.Contains(new Vector3(0.5f, 0, 0)) && modelVertices.Contains(new Vector3(0, 0.5f, 0)), "GLB mesh node / surface / meter positions");
            Check(state.GetSkins().Count == 0 && state.GetAnimations().Count == 0 && state.GetCameras().Count == 0 && state.GetLights().Count == 0, "GLB no skins animation camera lights");
            Check(meshNode.Mesh.GetSurfaceCount() == 1, "GLB one surface");
            Check(ResourceConsumer.ModelMetersToCells(new Vector3(0.5f, 0.5f, 0.5f)) == Vector3.One, "model meter to cells no axis reflection");
            root.Free();
            Check(!GodotObject.IsInstanceValid(root), "temporary GLB scene freed");
            using var invalidState = new GltfState();
            Check(document.AppendFromBuffer(new byte[] {1, 2, 3}, "", invalidState) != Error.Ok, "corrupt GLB rejected");
            SceneVertex Vertex(NVector p) => new(p, NVector.UnitZ, System.Numerics.Vector2.Zero, System.Numerics.Vector2.Zero, ExposedFaceEdges.None, 0, 0);
            var source = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel), [Vertex(NVector.Zero), Vertex(NVector.UnitX), Vertex(NVector.UnitY)], [0, 1, 2]);
            using var mesh = ResourceConsumer.CreateMesh(source);
            using var arrays = mesh.SurfaceGetArrays(0);
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            GD.Print("ARRAYS indices=" + string.Join(",", indices) + " vertices=" + string.Join(",", vertices) + " normals=" + string.Join(",", normals));
            Check(indices.SequenceEqual(new[] {0, 2, 1}) && vertices[1] == Vector3.Right && vertices[2] == Vector3.Up && normals.All(n => n.DistanceTo(Vector3.Back) < 0.001f), "ArrayMesh reverses CCW to CW preserving cells and outward +Z normals");
            // 最小准备与切换情景；尚未迁移的生产帧安装器不在本次验证范围内。
            var old = ResourceConsumer.CreatePngTexture(asset);
            ImageTexture displayed = old;
            ImageTexture? candidate = null;
            try { candidate = ResourceConsumer.CreatePngTexture(broken); displayed = candidate; old.Dispose(); }
            catch (InvalidOperationException) { candidate?.Dispose(); }
            Check(ReferenceEquals(displayed, old) && displayed.GetWidth() == 2, "failed preparation retains previous complete resource");
            displayed.Dispose();
            GD.Print("PROBE_SUCCESS");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr(ex); GetTree().Quit(1); }
    }
}

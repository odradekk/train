using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests.Assets;

public class AssetResourceCacheTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void Face_textures_share_by_revision_keep_mipmaps_and_release_on_last_lease()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var oldAsset = Png("old", Colors.Red);
        using var first = cache.AcquireFaceTexture(oldAsset);
        using var second = cache.AcquireFaceTexture(oldAsset);
        var oldTexture = first.Value;
        Require(oldTexture.GetInstanceId() == second.Value.GetInstanceId(), "同修订应共享纹理资源。");
        using (var image = oldTexture.GetImage())
            Require(image.HasMipmaps(), "面纹理必须包含 mipmap。");
        using var next = cache.AcquireFaceTexture(Png("new", Colors.Blue));
        Require(oldTexture.GetInstanceId() != next.Value.GetInstanceId(), "新修订不能覆盖旧纹理。");
        using (var oldImage = oldTexture.GetImage())
        using (var newImage = next.Value.GetImage())
        {
            Require(oldImage.GetPixel(0, 0).IsEqualApprox(Colors.Red), "旧租约的像素被新修订覆盖。");
            Require(newImage.GetPixel(0, 0).IsEqualApprox(Colors.Blue), "新修订没有自己的像素。");
        }
        first.Dispose();
        first.Dispose();
        Require(GodotObject.IsInstanceValid(oldTexture), "第一个租约释放时仍被使用的纹理失效。");
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(oldTexture), "最后租约释放后纹理仍然存活。");
        Require(GodotObject.IsInstanceValid(next.Value), "释放旧修订影响了新修订。");
    }

    [Test]
    public void Cache_shutdown_rejects_new_acquires_and_preserves_existing_leases()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("shutdown", Colors.Green);
        using var lease = cache.AcquireFaceTexture(asset);
        var texture = lease.Value;
        using var model = cache.AcquireModel(Glb(dispatcher, "shutdown-model", GlbFixture.Create()));
        var mesh = model.Value.Mesh;
        cache.Dispose();
        cache.Dispose();
        Require(GodotObject.IsInstanceValid(texture) && GodotObject.IsInstanceValid(mesh),
            "关闭缓存不能提前销毁仍有租约的资源。");
        try
        {
            using var unexpected = cache.AcquireFaceTexture(asset);
            throw new InvalidOperationException("关闭后仍接收新纹理请求。");
        }
        catch (ObjectDisposedException) { }
        try
        {
            using var unexpected = cache.AcquireModel(Glb(dispatcher, "closed-model", GlbFixture.Create()));
            throw new InvalidOperationException("关闭后仍接收新模型请求。");
        }
        catch (ObjectDisposedException) { }
        lease.Dispose();
        model.Dispose();
        Require(!GodotObject.IsInstanceValid(texture) && !GodotObject.IsInstanceValid(mesh),
            "缓存关闭后最后租约未释放资源。");
    }

    [Test]
    public void Model_bakes_node_transforms_and_scales_meters_to_grids_without_axis_reflection()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Glb(dispatcher, "transformed", GlbFixture.Create(transformed: true));
        var metadata = (AssetMetadata.Glb)asset.Metadata;
        Near(metadata.Bounds.MinimumMeters, new(-14, 21, 42));
        Near(metadata.Bounds.MaximumMeters, new(4, 24, 62));
        using var lease = cache.AcquireModel(asset);
        var bounds = lease.Value.Mesh.GetAabb();
        Near(bounds.Position, new(-28, 42, 84));
        Near(bounds.End, new(8, 48, 124));
        Near(bounds.Position, Twice(metadata.Bounds.MinimumMeters));
        Near(bounds.End, Twice(metadata.Bounds.MaximumMeters));
        var alpha = new[] { AssetAlphaMode.Opaque, AssetAlphaMode.Mask, AssetAlphaMode.Blend };
        Require(lease.Value.Mesh.GetSurfaceCount() == 3 && lease.Value.Surfaces.Length == 3, "模型应保留三个表面。");
        for (var surface = 0; surface < 3; surface++)
        {
            SurfaceBounds(lease.Value.Mesh, surface, new(-28, 42, 84 + 16 * surface), new(8, 48, 92 + 16 * surface));
            Require(lease.Value.Surfaces[surface].AlphaMode == alpha[surface], "表面几何与透明方式未按 primitive 顺序对应。");
        }
        Require(Math.Abs(lease.Value.Surfaces[1].AlphaCutoff - 0.5f) < 0.0001, "Mask cutoff 未保留。");
        using var plain = cache.AcquireModel(Glb(dispatcher, "plain", GlbFixture.Create()));
        var vertices = plain.Value.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Require(vertices.Any(vertex => vertex.IsEqualApprox(new Vector3(4, 0, 0))), "正 X 顶点被翻转。");
        Require(vertices.Any(vertex => vertex.IsEqualApprox(new Vector3(0, 6, 2))), "正 Z 顶点被翻转。");
    }

    [Test]
    public void Mesh_instances_keep_each_nodes_transform_and_surface_metadata()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Glb(dispatcher, "instances", GlbFixture.Create(feature: "instances"));
        var metadata = (AssetMetadata.Glb)asset.Metadata;
        Require(metadata.MeshNodeCount == 2 && metadata.Surfaces.Length == 6, "两个 mesh 0 实例应报告两个网格节点和六个表面。");
        Near(metadata.Bounds.MinimumMeters, new(-5, 2, 3));
        Near(metadata.Bounds.MaximumMeters, new(3, 8, 11));
        using var lease = cache.AcquireModel(asset);
        Require(lease.Value.Mesh.GetSurfaceCount() == 6 && lease.Value.Surfaces.Length == 6, "模型丢失了第二个实例。");
        var translations = new[] { new LocalPoint(1, 2, 3), new LocalPoint(-4, 5, 6) };
        var alpha = new[] { AssetAlphaMode.Opaque, AssetAlphaMode.Mask, AssetAlphaMode.Blend };
        for (var instance = 0; instance < translations.Length; instance++)
        for (var surface = 0; surface < 3; surface++)
        {
            var translation = translations[instance];
            var index = instance * 3 + surface;
            SurfaceBounds(lease.Value.Mesh, index,
                new(2 * (translation.X - 1), 2 * translation.Y, 2 * (translation.Z + 2 * surface)),
                new(2 * (translation.X + 2), 2 * (translation.Y + 3), 2 * (translation.Z + 2 * surface + 1)));
            Require(metadata.Surfaces[index] == alpha[surface] && lease.Value.Surfaces[index].AlphaMode == alpha[surface],
                "实例表面的几何与透明方式不对应。");
        }
    }

    [Test]
    public void Mirrored_node_transform_preserves_front_faces_and_tangent_handedness()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var bytes = GlbFixture.Create(feature: "tangent", changeJson: json =>
            json["nodes"]![0]!["scale"] = System.Text.Json.Nodes.JsonNode.Parse("[-1,1,1]"));
        using var lease = cache.AcquireModel(Glb(dispatcher, "mirrored", bytes));
        using var arrays = lease.Value.Mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var normal = new Vector3(0, -1, 3).Normalized();
        // Godot 使用顺时针正面；镜像后的外法线仍为 (0,-1,3)，由夹具三顶点独立推出。
        var front = (vertices[indices[2]] - vertices[indices[0]]).Cross(vertices[indices[1]] - vertices[indices[0]]).Normalized();
        Require(front.Dot(normal) > 0.999f, "烘焙镜像节点变换后正面绕序反转。");
        Near(arrays[(int)Mesh.ArrayType.Normal].AsVector3Array()[0], new(0, -1 / Math.Sqrt(10), 3 / Math.Sqrt(10)));
        var tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
        Near(new Vector3(tangents[0], tangents[1], tangents[2]), new(-1, 0, 0));
        Require(tangents[3] == -1, "负行列式变换必须翻转 glTF 夹具原为 +1 的 tangent.w。");
        var stripBytes = GlbFixture.Create(changeJson: json =>
        {
            json["nodes"]![0]!["scale"] = System.Text.Json.Nodes.JsonNode.Parse("[-1,1,1]");
            foreach (var primitive in json["meshes"]![0]!["primitives"]!.AsArray())
            {
                primitive!["mode"] = 5;
                primitive.AsObject().Remove("indices");
            }
        });
        using var strip = cache.AcquireModel(Glb(dispatcher, "mirrored-strip", stripBytes));
        var primitiveType = strip.Value.Mesh.SurfaceGetPrimitiveType(0);
        using var stripArrays = strip.Value.Mesh.SurfaceGetArrays(0);
        var stripVertices = stripArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var stripIndices = stripArrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
            ? Enumerable.Range(0, stripVertices.Length).ToArray() : stripArrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        Require(primitiveType is Mesh.PrimitiveType.Triangles or Mesh.PrimitiveType.TriangleStrip, "返回的 primitive 不能绘制三角面。");
        var step = primitiveType == Mesh.PrimitiveType.Triangles ? 3 : 1;
        var faces = 0;
        for (var i = 0; i + 2 < stripIndices.Length; i += step)
        {
            var a = stripIndices[i];
            var b = stripIndices[i + 1];
            var c = stripIndices[i + 2];
            if (primitiveType == Mesh.PrimitiveType.TriangleStrip && i % 2 != 0) (a, b) = (b, a);
            var frontNormal = (stripVertices[c] - stripVertices[a]).Cross(stripVertices[b] - stripVertices[a]);
            if (frontNormal.LengthSquared() < 0.000001f) continue;
            faces++;
            // Godot 4.7.2 的 strip 保留输入绕序，原三顶点的顺时针正面为 (0,1,-3)。
            Require(frontNormal.Normalized().Dot(-normal) > 0.999f, "镜像 primitive 没有保留解码后的正面方向。");
        }
        Require(faces == 1, "夹具的一个可见三角面被丢弃或重复。");
    }

    [Test]
    public void Embedded_material_texture_factor_and_model_leases_have_owned_lifetimes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        var asset = Glb(dispatcher, "embedded", GlbFixture.Create(png: image.SavePngToBuffer(),
            baseColorFactor: [0.25f, 0.5f, 0.75f, 1], changeJson: json =>
                json["materials"]![1]!["pbrMetallicRoughness"] = json["materials"]![0]!["pbrMetallicRoughness"]!.DeepClone()));
        using var first = cache.AcquireModel(asset);
        using var second = cache.AcquireModel(asset);
        var model = first.Value;
        var meshId = model.Mesh.GetInstanceId();
        Require(model.Mesh.GetInstanceId() == second.Value.Mesh.GetInstanceId(), "同修订模型没有共享网格。");
        var texture = model.Surfaces[1].BaseColorTexture;
        Require(texture is not null && GodotObject.IsInstanceValid(texture), "内嵌基础色贴图未保留。");
        var textureId = texture!.GetInstanceId();
        Require(model.Surfaces[2].BaseColorTexture?.GetInstanceId() == texture.GetInstanceId(),
            "两个表面的同一张基础色贴图必须保留并共享。");
        Require(model.Surfaces[1].BaseColorFactor.IsEqualApprox(new Color(0.25f, 0.5f, 0.75f, 1)), "基础色系数未保留。");
        Require(model.Surfaces[0].BaseColorFactor.IsEqualApprox(Colors.White), "无材质表面的缺省基础色系数错误。");
        first.Dispose();
        Require(GodotObject.IsInstanceValid(model.Mesh) && GodotObject.IsInstanceValid(texture),
            "第一个模型租约释放导致共享网格或贴图失效。");
        second.Dispose();
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(model.Mesh) && !GodotObject.IsInstanceValid(texture) &&
            !GodotObject.IsInstanceIdValid(meshId) && !GodotObject.IsInstanceIdValid(textureId),
            "最后模型租约必须在主线程释放网格和内嵌贴图。");
    }

    [Test]
    public async Task Background_acquire_and_last_release_wait_for_main_thread_pumping()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("background", Colors.Red);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquire = Task.Run(() =>
        {
            started.SetResult();
            return cache.AcquireFaceTexture(asset);
        });
        await started.Task;
        var tree = TestScene.GetTree();
        for (var frame = 0; frame < 5; frame++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Require(!acquire.IsCompleted, "后台资源创建应等待主线程泵送。");
        await PumpUntil(dispatcher, acquire);
        using var lease = await acquire;
        var texture = lease.Value;
        Require(GodotObject.IsInstanceValid(texture), "泵送后纹理无效。");
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Task.Run(() =>
        {
            releasing.SetResult();
            lease.Dispose();
        });
        await releasing.Task;
        for (var frame = 0; frame < 5; frame++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Require(!release.IsCompleted, "后台资源释放应等待主线程泵送。");
        Require(GodotObject.IsInstanceValid(texture), "后台释放在主线程泵送前销毁资源。");
        await PumpUntil(dispatcher, release);
        Require(!GodotObject.IsInstanceValid(texture), "泵送最后释放后纹理仍存活。");
    }

    private async Task PumpUntil(GodotMainThreadDispatcher dispatcher, Task operation)
    {
        var tree = TestScene.GetTree();
        for (var frame = 0; frame < 120 && !operation.IsCompleted; frame++)
        {
            dispatcher.Pump();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        Require(operation.IsCompleted, "主线程泵送后后台资源操作仍未完成。");
        await operation;
    }

    private static ContentAsset Png(string revision, Color color)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(color);
        return new(new(revision), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(4, 4), []);
    }

    private static ContentAsset Glb(GodotMainThreadDispatcher dispatcher, string revision, ImmutableArray<byte> bytes)
    {
        var id = new AssetRevision(revision);
        var result = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, id, bytes);
        Require(result is AssetInspection.Success, $"GLB 夹具检查失败：{result}");
        return new(id, bytes, ((AssetInspection.Success)result).Metadata, []);
    }

    private static LocalPoint Twice(LocalPoint point) => new(point.X * 2, point.Y * 2, point.Z * 2);
    private static void SurfaceBounds(ArrayMesh mesh, int surface, LocalPoint minimum, LocalPoint maximum)
    {
        using var arrays = mesh.SurfaceGetArrays(surface);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Require(vertices.Length > 0, "表面没有顶点。");
        var bounds = new Aabb(vertices[0], Vector3.Zero);
        foreach (var vertex in vertices) bounds = bounds.Expand(vertex);
        Near(bounds.Position, minimum);
        Near(bounds.End, maximum);
    }
    private static void Near(Vector3 actual, LocalPoint expected) => Near(new LocalPoint(actual.X, actual.Y, actual.Z), expected);
    private static void Near(LocalPoint actual, LocalPoint expected) => Require(
        Math.Abs(actual.X - expected.X) < 0.0001 && Math.Abs(actual.Y - expected.Y) < 0.0001
        && Math.Abs(actual.Z - expected.Z) < 0.0001, $"坐标错误：{actual}，期望 {expected}。");
}

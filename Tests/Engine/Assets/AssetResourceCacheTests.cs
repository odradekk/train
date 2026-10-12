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
        Near(metadata.Bounds.MaximumMeters, new(4, 24, 46));
        using var lease = cache.AcquireModel(asset);
        var bounds = lease.Value.Mesh.GetAabb();
        Near(bounds.Position, new(-28, 42, 84));
        Near(bounds.End, new(8, 48, 92));
        Near(bounds.Position, Twice(metadata.Bounds.MinimumMeters));
        Near(bounds.End, Twice(metadata.Bounds.MaximumMeters));
        Require(lease.Value.Surfaces.Select(surface => surface.AlphaMode).SequenceEqual(
            new[] { AssetAlphaMode.Opaque, AssetAlphaMode.Mask, AssetAlphaMode.Blend }), "模型材料顺序错误。");
        Require(Math.Abs(lease.Value.Surfaces[1].AlphaCutoff - 0.5f) < 0.0001, "Mask cutoff 未保留。");
        using var plain = cache.AcquireModel(Glb(dispatcher, "plain", GlbFixture.Create()));
        var vertices = plain.Value.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Require(vertices.Any(vertex => vertex.IsEqualApprox(new Vector3(4, 0, 0))), "正 X 顶点被翻转。");
        Require(vertices.Any(vertex => vertex.IsEqualApprox(new Vector3(0, 6, 2))), "正 Z 顶点被翻转。");
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
        Require(model.Mesh.GetInstanceId() == second.Value.Mesh.GetInstanceId(), "同修订模型没有共享网格。");
        var texture = model.Surfaces[1].BaseColorTexture;
        Require(texture is not null && GodotObject.IsInstanceValid(texture), "内嵌基础色贴图未保留。");
        Require(model.Surfaces[2].BaseColorTexture?.GetInstanceId() == texture!.GetInstanceId(),
            "两个表面的同一张基础色贴图必须保留并共享。");
        Require(model.Surfaces[1].BaseColorFactor.IsEqualApprox(new Color(0.25f, 0.5f, 0.75f, 1)), "基础色系数未保留。");
        Require(model.Surfaces[0].BaseColorFactor.IsEqualApprox(Colors.White), "无材质表面的缺省基础色系数错误。");
        first.Dispose();
        Require(GodotObject.IsInstanceValid(model.Mesh) && GodotObject.IsInstanceValid(texture),
            "第一个模型租约释放导致共享网格或贴图失效。");
        second.Dispose();
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(model.Mesh) && !GodotObject.IsInstanceValid(texture),
            "最后模型租约未释放网格和内嵌贴图。");
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
    private static void Near(Vector3 actual, LocalPoint expected) => Near(new LocalPoint(actual.X, actual.Y, actual.Z), expected);
    private static void Near(LocalPoint actual, LocalPoint expected) => Require(
        Math.Abs(actual.X - expected.X) < 0.0001 && Math.Abs(actual.Y - expected.Y) < 0.0001
        && Math.Abs(actual.Z - expected.Z) < 0.0001, $"坐标错误：{actual}，期望 {expected}。");
}

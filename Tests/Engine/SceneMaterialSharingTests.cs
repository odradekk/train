using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Engine.Tests.Assets;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class SceneMaterialSharingTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void Voxel_material_keys_use_record_values_and_every_surface_field_even_without_wear()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("material-key");
        var otherAsset = Png("material-key-other");
        using var resources = new SceneResources(cache, Content(asset, otherAsset));
        var source = Geometry(asset);
        using var first = resources.Prepare(source);
        using var equal = resources.Prepare(Geometry(asset));
        Require(Material(first) == Material(equal), "参数相同的体素几何没有共享材质。");
        SceneVisual.Geometry[] different =
        [
            source with { Mesh = Triangle(new SceneMaterial.Voxel(otherAsset.Revision, MaterialCategory.PaintedMetal, new("#123456"))) },
            source with { Mesh = Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.Concrete, new("#123456"))) },
            source with { Mesh = Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, null)) },
            source with { Mesh = Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#abcdef"))) },
            source with { Surface = source.Surface with { WearEnabled = true } },
            source with { Surface = source.Surface with { WearSeed = 43 } },
            source with { Surface = source.Surface with { Damage = 0.5f } },
            source with { Surface = source.Surface with { Scorched = true } }
        ];
        var materials = new List<ShaderMaterial> { Material(first) };
        var visuals = new List<PreparedVisual>();
        try
        {
            foreach (var changed in different)
            {
                var visual = resources.Prepare(changed);
                visuals.Add(visual);
                Require(materials.All(material => material != Material(visual)), "材质键不同的对象不能共用材质。");
                materials.Add(Material(visual));
                using var twin = resources.Prepare(changed with { Surface = changed.Surface with { } });
                Require(Material(twin) == Material(visual), "包括开启磨损的对象在内，相同参数必须共享材质。");
            }
            var builtIn = source with { Mesh = Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper)) };
            using var a = resources.Prepare(builtIn);
            using var b = resources.Prepare(builtIn);
            Require(Material(a) != Material(b), "内建几何必须各自持有材质。");
            var missing = new SceneVisual.Missing(new ContentReference.Design(new("missing")), true);
            using var x = resources.Prepare(missing);
            using var y = resources.Prepare(missing);
            Require(Instance(x).MaterialOverride != Instance(y).MaterialOverride, "缺失标记必须各自持有材质。");
        }
        finally { foreach (var visual in visuals) visual.Dispose(); }
    }

    [Test]
    public void Sole_geometry_updates_keep_material_but_leave_the_shared_key_available()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("sole-update");
        using var resources = new SceneResources(cache, Content(asset));
        var source = Geometry(asset);
        using var first = resources.Prepare(source);
        var original = Material(first);
        var changed = source with { Surface = source.Surface with { Damage = 0.75f } };
        Require(first.CanUpdate(changed), "表面状态更新不应重建视觉对象。");
        first.Update(changed);
        Require(Material(first) == original, "单个使用者更新时必须保留原材质实例。");
        using var replacement = resources.Prepare(source);
        Require(Material(replacement) != original, "原键必须能创建参数未变的共享材质。");
        Surface(Material(replacement), source.Surface);
        first.Dispose();
        using var twin = resources.Prepare(Geometry(asset));
        Require(Material(twin) == Material(replacement), "释放旧私有材质不能删除同键的新缓存项。");
        replacement.Dispose();
        Require(GodotObject.IsInstanceValid(Material(twin)), "新缓存项仍有使用者时不能释放。");
        var last = Material(twin);
        twin.Dispose();
        Require(!GodotObject.IsInstanceValid(last), "最后一个共享租约必须释放材质。");
    }

    [Test]
    public void Shared_geometry_surface_and_paint_updates_isolate_once_and_stay_private()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("update-isolation");
        using var resources = new SceneResources(cache, Content(asset));
        var source = Geometry(asset);
        foreach (var paintOnly in new[] { false, true })
        {
            using var first = resources.Prepare(source);
            using var second = resources.Prepare(source);
            var shared = Material(second);
            Require(Material(first) == shared, "初始几何材质必须共享。");
            var changed = paintOnly
                ? source with { Mesh = source.Mesh with { Material = new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#abcdef")) } }
                : source with { Surface = new(true, ulong.MaxValue, 0.75f, true) };
            Require(first.CanUpdate(changed), "涂装和表面变化不应重建视觉对象。");
            first.Update(changed);
            var owned = Material(first);
            Require(owned != shared, "多个使用者中更新一个时必须复制它的材质。");
            Surface(owned, changed.Surface);
            Surface(shared, source.Surface);
            Paint(owned, paintOnly ? "#abcdef" : "#123456");
            Paint(shared, "#123456");
            first.Update(source);
            using var third = resources.Prepare(source);
            Require(Material(first) == owned && Material(third) == shared, "参数恢复原值后必须继续使用私有材质。");
            second.Dispose();
            third.Dispose();
            Require(!GodotObject.IsInstanceValid(shared) && GodotObject.IsInstanceValid(owned), "转为私有后不能继续持有旧共享材质租约。");
        }
    }

    [Test]
    public void Geometry_and_model_fog_take_sole_ownership_or_duplicate_and_keep_their_asset_lease()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var png = Png("fog-voxel");
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        var bytes = GlbFixture.Create(png: image.SavePngToBuffer());
        var inspection = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("fog-model"), bytes);
        Require(inspection is AssetInspection.Success, "模型夹具必须能够解码。");
        var glb = new ContentAsset(new("fog-model"), bytes, ((AssetInspection.Success)inspection).Metadata, []);
        using var resources = new SceneResources(cache, Content(png, glb));
        SceneVisual[] sources = [Geometry(png), new SceneVisual.Model(glb.Revision,
            [MaterialCategory.PaintedMetal, MaterialCategory.PaintedMetal, MaterialCategory.PaintedMetal],
            new("#123456"), new(false, 42, 0.25f, false), default)];
        foreach (var source in sources)
        foreach (var multiple in new[] { false, true })
        {
            using var first = resources.Prepare(source);
            using var second = multiple ? resources.Prepare(source) : null;
            var before = Materials(first);
            var shaders = before.Select(material => material.Shader).ToArray();
            var mesh = Instance(first).Mesh;
            var texture = Texture(before[source is SceneVisual.Model ? 1 : 0]);
            resources.ExemptConstructionFog(first, true);
            var owned = Materials(first);
            for (var index = 0; index < owned.Length; index++)
            {
                Require((owned[index] == before[index]) == !multiple, "雾豁免只在材质还有其他使用者时复制。");
                Require(owned[index].Shader != shaders[index], "每个表面都必须切换为跟随着色器。");
                Surface(owned[index], new(false, 42, 0.25f, false));
                Paint(owned[index], "#123456");
                if (multiple) Require(Materials(second!)[index] == before[index] && before[index].Shader == shaders[index], "雾豁免不能影响其他使用者。");
            }
            Require(Instance(first).Mesh == mesh && Texture(owned[source is SceneVisual.Model ? 1 : 0]) == texture,
                "雾豁免不能更换网格和纹理。");
            if (source is SceneVisual.Model)
            {
                Require(owned[1].GetShaderParameter("alpha_cutout").AsBool(), "雾豁免不能丢失模型的透明裁切。");
                Near(owned[1].GetShaderParameter("alpha_cutoff").AsSingle(), 0.5);
            }
            resources.ExemptConstructionFog(first, false);
            resources.ExemptConstructionFog(first, true);
            Require(Materials(first).SequenceEqual(owned), "反复切换雾豁免必须复用私有材质。");
            using var replacement = resources.Prepare(source);
            Require(Materials(replacement).Zip(owned).All(pair => pair.First != pair.Second), "原键不能再取得已经私有化的材质。");
            second?.Dispose();
            replacement.Dispose();
            Require(GodotObject.IsInstanceValid(texture) && GodotObject.IsInstanceValid(mesh), "私有视觉对象必须保留自己的纹理和模型租约。");
            if (multiple) Require(before.All(material => !GodotObject.IsInstanceValid(material)), "私有对象仍在使用时，最后一个共享使用者也必须释放原材质。");
            first.Dispose();
            Require(owned.All(material => !GodotObject.IsInstanceValid(material)) && !GodotObject.IsInstanceValid(texture), "私有视觉对象必须释放材质和纹理。");
            if (!multiple)
            {
                using var old = resources.Prepare(source);
                resources.ExemptConstructionFog(old, true);
                using var current = resources.Prepare(source);
                old.Dispose();
                using var twin = resources.Prepare(source);
                Require(Materials(current).SequenceEqual(Materials(twin)), "释放单使用者的雾豁免材质不能删除同键的新缓存项。");
            }
        }
    }

    [Test]
    public void Wheelsets_share_two_materials_and_fog_detaches_all_submeshes_without_rotation_detaching()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        var source = new SceneVisual.Wheelset(7, 1.25, 0.375, 0.7);
        using var first = resources.Prepare(source);
        using var second = resources.Prepare(source with { GaugeCells = 8, RotationRadians = 0 });
        var shared = WheelMaterials(first);
        Require(shared.Distinct().Count() == 2 && shared.SequenceEqual(WheelMaterials(second)), "所有轮对必须共享两份固定材质。");
        first.Update(source with { RotationRadians = -0.3 });
        Require(shared.SequenceEqual(WheelMaterials(first)), "轮对转动不能分离材质。");
        var normal = shared[0].Shader;
        resources.ExemptConstructionFog(first, true);
        var owned = WheelMaterials(first);
        Require(owned.Distinct().Count() == 2 && owned.Zip(shared).All(pair => pair.First != pair.Second), "雾豁免必须为整个轮对的两份材质各分离一次。");
        Require(owned.All(material => material.Shader != normal) && WheelMaterials(second).All(material => material.Shader == normal), "雾豁免必须影响本轮对的全部车轮和轮辐，且不能影响另一轮对。");
        resources.ExemptConstructionFog(first, false);
        Require(owned.SequenceEqual(WheelMaterials(first)) && owned.All(material => material.Shader == normal), "取消雾豁免后必须保留轮对的私有材质。");
        second.Dispose();
        Require(shared.All(material => !GodotObject.IsInstanceValid(material)) && owned.All(GodotObject.IsInstanceValid), "最后一个共享轮对必须只释放共享材质。");
        first.Dispose();
        Require(owned.All(material => !GodotObject.IsInstanceValid(material)), "私有轮对必须释放两份材质。");
    }

    [Test]
    public void Scene_resources_refuse_disposal_while_voxel_or_wheel_materials_are_still_shared()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("dispose-shared");
        foreach (var source in new SceneVisual[] { Geometry(asset), new SceneVisual.Wheelset(7, 1, 0.3, 0) })
        {
            using var resources = new SceneResources(cache, Content(asset));
            using var first = resources.Prepare(source);
            using var second = resources.Prepare(source);
            var materials = source is SceneVisual.Wheelset ? WheelMaterials(second) : Materials(second);
            var shader = materials[0].Shader;
            first.Dispose();
            var rejected = false;
            try { resources.Dispose(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && materials.All(GodotObject.IsInstanceValid) && GodotObject.IsInstanceValid(shader), "仍有共享租约时必须拒绝关闭，且不能销毁资源。");
            second.Dispose();
            Require(materials.All(material => !GodotObject.IsInstanceValid(material)), "最后一个视觉对象必须释放共享材质。");
            resources.Dispose();
            Require(!GodotObject.IsInstanceValid(shader), "最后一个使用者释放后，关闭必须释放着色器。");
        }
    }

    [Test]
    public void Private_models_and_wheels_keep_their_scene_resources_in_use_until_disposal()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var bytes = GlbFixture.Create();
        var inspection = (AssetInspection.Success)new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("private-model"), bytes);
        var asset = new ContentAsset(new("private-model"), bytes, inspection.Metadata, []);
        foreach (var source in new SceneVisual[] { new SceneVisual.Model(asset.Revision,
            [MaterialCategory.Concrete, MaterialCategory.Concrete, MaterialCategory.Concrete], null, new(false, 0, 0, false), default),
            new SceneVisual.Wheelset(7, 1, 0.3, 0) })
        {
            using var resources = new SceneResources(cache, Content(asset));
            using var visual = resources.Prepare(source);
            resources.ExemptConstructionFog(visual, true);
            var material = source is SceneVisual.Wheelset ? WheelMaterials(visual)[0] : Materials(visual)[0];
            var shader = material.Shader;
            var rejected = false;
            try { resources.Dispose(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && GodotObject.IsInstanceValid(shader), "私有材质仍在使用时不能关闭场景资源或销毁着色器。");
            visual.Dispose();
            resources.Dispose();
            Require(!GodotObject.IsInstanceValid(material) && !GodotObject.IsInstanceValid(shader), "最后一个私有使用者释放后必须能关闭场景资源。");
        }
    }

    private static SceneVisual.Geometry Geometry(ContentAsset asset) => new(Triangle(new SceneMaterial.Voxel(
        asset.Revision, MaterialCategory.PaintedMetal, new("#123456"))), new(false, 42, 0.25f, false));
    private static SceneMesh Triangle(SceneMaterial material) => new(material,
        [new(NVector3.Zero, NVector3.UnitZ, NVector2.Zero, NVector2.Zero, 0, 0, 0),
         new(NVector3.UnitX, NVector3.UnitZ, NVector2.UnitX, NVector2.UnitX, 0, 0, 0),
         new(NVector3.UnitY, NVector3.UnitZ, NVector2.UnitY, NVector2.UnitY, 0, 0, 0)], [0, 1, 2]);
    private static MeshInstance3D Instance(PreparedVisual visual) => visual.Node.GetChild<MeshInstance3D>(0);
    private static ShaderMaterial Material(PreparedVisual visual) => (ShaderMaterial)Instance(visual).MaterialOverride;
    private static ShaderMaterial[] Materials(PreparedVisual visual) => visual.Visual is SceneVisual.Model model
        ? [.. Enumerable.Range(0, model.Materials.Length).Select(index => (ShaderMaterial)Instance(visual).GetSurfaceOverrideMaterial(index))]
        : [Material(visual)];
    private static ShaderMaterial[] WheelMaterials(PreparedVisual visual) => [.. visual.Node.GetChildren().OfType<MeshInstance3D>()
        .SelectMany(wheel => new[] { wheel }.Concat(wheel.GetChildren().OfType<MeshInstance3D>()))
        .Select(instance => (ShaderMaterial)instance.MaterialOverride)];
    private static Texture2D Texture(ShaderMaterial material)
    {
        using var parameter = material.GetShaderParameter("base_texture");
        return parameter.As<Texture2D>();
    }
    private static void Surface(ShaderMaterial material, SurfaceState expected)
    {
        Require(material.GetShaderParameter("wear_enabled").AsBool() == expected.WearEnabled, "磨损开关发生变化。");
        var seed = material.GetShaderParameter("wear_seed").AsVector2I();
        Require(((ulong)unchecked((uint)seed.X) | ((ulong)unchecked((uint)seed.Y) << 32)) == expected.WearSeed, "磨损种子发生变化。");
        Near(material.GetShaderParameter("surface_damage").AsSingle(), expected.Damage);
        Require(material.GetShaderParameter("scorched").AsBool() == expected.Scorched, "焦痕状态发生变化。");
    }
    private static void Paint(ShaderMaterial material, string expected) => Require(material.GetShaderParameter("has_paint").AsBool() &&
        material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml(expected)), "涂装发生变化。");
    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < 0.001, $"期望 {expected}，实际为 {actual}。");
    private static FixedContentView Content(params ContentAsset[] assets) => new()
    {
        Assets = assets.ToDictionary(asset => asset.Revision, asset => (ContentLookup<ContentAsset>)new ContentLookup<ContentAsset>.Found(asset))
    };
    private static ContentAsset Png(string revision)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return new(new(revision), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(4, 4), []);
    }
}

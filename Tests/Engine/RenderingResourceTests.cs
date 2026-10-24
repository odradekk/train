using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Engine.Tests.Assets;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class RenderingResourceTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Display_delta_preserves_children_on_reparent_and_updates_inherited_fog_after_parent_changes()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = new SceneDisplay(cache, new FixedContentView());
        TestScene.AddChild(display.Root);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        var visual = new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)),
            new(false, 0, 0, false));
        var vehicle = new WorldObjectId.Instance(new InstanceId.Vehicle(new(7)));
        var parent = Object(1, null) with { Subject = vehicle, Lights = [new(new("#ffffff"), 3, 0, 1)] };
        var child = Object(2, new(1));
        var other = Object(3, null);
        var first = Frame(1, null, [parent, child, other]);
        using (var prepared = display.Prepare(first)!) prepared.Commit();
        display.UpdateConstructionFog([new(7)]);
        var childRoot = display.Objects[new(2)].Root;
        var childVisual = display.Objects[new(2)].Visuals[0];
        var followedShader = Material(childVisual).Shader;
        var oldParent = display.Objects[new(1)].Root;
        var light = oldParent.GetChildren().OfType<OmniLight3D>().Single();
        using (var abandoned = display.Prepare(Frame(2, first.Update.Frame, [parent with { Subject = null }]))!) { }
        display.UpdateConstructionFog([new(7)]);
        Require(ReferenceEquals(display.DisplayedFrame, first) && Material(childVisual).Shader == followedShader &&
            ReferenceEquals(childRoot.GetParent(), oldParent), "放弃已准备帧不能提前改变对象索引、层级或雾状态。");

        var invalid = Frame(2, first.Update.Frame, [], [new(1)]);
        var rejected = false;
        try { using var unexpected = display.Prepare(invalid); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(childRoot.GetParent(), oldParent) && GodotObject.IsInstanceValid(light),
            "删除仍有旧子对象的父必须在准备阶段失败，不能改变显示层级或释放旧灯光。");

        var changedParent = Frame(3, first.Update.Frame, [parent with { Subject = null }]);
        using (var prepared = display.Prepare(changedParent)!) prepared.Commit();
        display.UpdateConstructionFog([new(7)]);
        var normalShader = Material(childVisual).Shader;
        Require(normalShader != followedShader && ReferenceEquals(childRoot.GetParent(), oldParent),
            "父Subject改变时，即使子对象未Upsert也必须重算继承的雾状态。");

        var reparented = Frame(4, changedParent.Update.Frame, [child with { Parent = new(3) },
            other with { Subject = vehicle }], [new(1)]);
        using (var prepared = display.Prepare(reparented)!) prepared.Commit();
        display.UpdateConstructionFog([new(7)]);
        Require(ReferenceEquals(display.Objects[new(2)].Root, childRoot) &&
            ReferenceEquals(childRoot.GetParent(), display.Objects[new(3)].Root) &&
            Material(childVisual).Shader == followedShader && !GodotObject.IsInstanceValid(oldParent) &&
            !GodotObject.IsInstanceValid(light),
            "删除旧父并换父必须复用存活子对象，更新雾继承，并释放旧父和灯光。");
        display.UpdateConstructionFog([]);
        Require(Material(childVisual).Shader == normalShader, "跟随集合变化必须重算未Upsert的子对象。");
        var beforeObjects = display.Objects;
        var idle = Frame(5, reparented.Update.Frame, []);
        using (var prepared = display.Prepare(idle)!) prepared.Commit();
        Require(ReferenceEquals(beforeObjects, display.Objects) &&
            ReferenceEquals(display.Objects[new(2)].Root, childRoot),
            "没有对象变更或原点平移的增量必须复用对象表和节点，不能复制全部对象。");

        SceneObject Object(ulong id, SceneObjectId? parentId) => new(new(id), parentId,
            new(NVector3.Zero, System.Numerics.Quaternion.Identity), null, [visual], [], []);
        static FixedSceneFrame Frame(ulong revision, SceneFrameStamp? baseline, ImmutableArray<SceneObject> objects,
            ImmutableArray<SceneObjectId> removed = default) => new()
        {
            Update = new(baseline, new(new(Guid.Empty), new(1), new(2), revision),
                NVector3.Zero, objects, removed.IsDefault ? [] : removed, new([], []), []), Readiness = SceneReadiness.Ready
        };
    }

    [Test]
    public async Task Candidate_pending_work_tracks_latest_sources_without_accumulating_replacements()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = new SceneDisplay(cache, new FixedContentView());
        var visual = new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)),
            new(false, 0, 0, false));
        var unavailable = new SceneVisual.Model(new("unavailable-replaced-candidate"), [], null,
            new(false, 0, 0, false), default);
        var first = Update(1, Enumerable.Range(1, 10).Select(id => Object((ulong)id,
            id == 10 ? unavailable : visual)).ToImmutableArray());
        display.ReceiveCandidate(first);
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => (display.CandidateProgress(first.Frame)?.Prepared ?? 0) *
            (System.Diagnostics.Stopwatch.Frequency / 1000);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.CandidateProgress(first.Frame) is (4, 10) && display.PendingCandidateVisuals == 6,
            "预算中断后，待准备项必须与剩余来源一一对应。");
        display.ReceiveCandidate(first);
        Require(display.PendingCandidateVisuals == 6, "重复帧不能重复排队。");

        SceneUpdate latest = first;
        for (ulong revision = 2; revision <= 20; revision++)
        {
            latest = Update(revision, [Object(1, visual), Object(2, visual), Object(10, visual)]);
            display.ReceiveCandidate(latest);
            Require(display.CandidateProgress(latest.Frame) is (2, 3) && display.PendingCandidateVisuals == 1,
                "全量替换应删除撤销项、复用兼容资源，并只保留一次尚未准备的同键来源。");
        }
        await new UiTestInput(TestScene).Frames(1);
        display.Timestamp = () => 0;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.PreparedCandidate == latest.Frame && display.PendingCandidateVisuals == 0 &&
            display.CandidateProgress(latest.Frame) is (3, 3),
            "队列必须准备同键的最新来源，不能读取已被替换的不可用模型。");

        var delta = Update(21, [Object(11, visual)]) with { Base = latest.Frame, Removed = [new(10)] };
        display.ReceiveCandidate(delta);
        Require(display.CandidateProgress(delta.Frame) is (2, 3) && display.PendingCandidateVisuals == 1,
            "增量必须只撤销指定对象并排入新增视觉。");
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.PreparedCandidate == delta.Frame && display.PendingCandidateVisuals == 0,
            "增量完成后待准备队列必须为空。");
        display.CancelCandidate();
        Require(display.PendingCandidateVisuals == 0 && display.CandidateProgress(delta.Frame) is null,
            "取消候选必须清除全部待准备来源。");

        SceneObject Object(ulong id, SceneVisual source) => new(new(id), null,
            new(NVector3.Zero, System.Numerics.Quaternion.Identity), null, [source], [], []);
        static SceneUpdate Update(ulong revision, ImmutableArray<SceneObject> objects) =>
            new(null, new(new(Guid.Empty), new(1), new(2), revision), NVector3.Zero, objects, [], new([], []), []);
    }

    [Test]
    public void Followed_vehicle_fog_exemption_preserves_material_state_and_mesh_and_can_be_removed()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        var source = new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel)),
            new(true, 17, 0.25f, false));
        using var followed = resources.Prepare(source);
        using var other = resources.Prepare(source);
        var instance = Instance(followed);
        var mesh = instance.Mesh;
        var material = Material(followed);
        var originalShader = material.Shader;
        var damageMask = material.GetShaderParameter("damage_mask").AsGodotObject() as Texture2D;
        using var damageImage = damageMask?.GetImage();
        Require(damageMask is not null && damageMask.GetWidth() == 128 && damageMask.GetHeight() == 128 && damageImage?.HasMipmaps() == true,
            "场景材质必须加载带 Mipmap 的正式损伤遮罩。");

        // Godot 为每个带实例变量的实例在全局缓冲区预留 16 个 vec4；体素网格实例数量大，不能占用。
        Require(InstanceUniforms(instance).Length == 0, "体素网格不能使用实例变量。");
        resources.ExemptConstructionFog(followed, true);
        Require(InstanceUniforms(instance).Length == 0, "跟随的体素网格不能使用实例变量。");

        Require(material.Shader != originalShader && Material(other).Shader == originalShader,
            "雾豁免只能改变被跟随车辆的材质。");
        Require(ReferenceEquals(instance.Mesh, mesh), "雾豁免不应重建车辆网格。");
        AssertSurface(material, source.Surface);
        Require(material.GetShaderParameter("rail_steel").AsBool(), "切换着色器不能丢失材质参数。");
        Require(material.GetShaderParameter("damage_mask").AsGodotObject() == damageMask &&
            Material(other).GetShaderParameter("damage_mask").AsGodotObject() == damageMask,
            "普通和跟随材质必须共享同一份损伤遮罩。");
        var next = source with { Surface = new(false, 31, 0.75f, true) };
        followed.Update(next);
        AssertSurface(material, next.Surface);

        resources.ExemptConstructionFog(followed, false);

        Require(material.Shader == originalShader, "离开跟随目标后必须恢复普通构建雾遮挡。");
        AssertSurface(material, next.Surface);
        Require(ReferenceEquals(instance.Mesh, mesh), "移除豁免不应重建车辆网格。");
        followed.Dispose();
        other.Dispose();
        resources.Dispose();
        Require(!GodotObject.IsInstanceValid(damageMask), "释放场景资源必须释放损伤遮罩。");
    }

    [Test]
    public void Geometry_uploads_outward_faces_and_all_vertex_and_surface_channels()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("channels");
        using var resources = new SceneResources(cache, Content(asset));
        var source = Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.PaintedMetal, new("#80a0c0")));
        var surface = new SurfaceState(true, 0xfedcba9876543210UL, 0.375f, true);
        using var visual = resources.Prepare(new SceneVisual.Geometry(source, surface));
        var instance = Instance(visual);
        var mesh = (ArrayMesh)instance.Mesh;
        using var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var faceUv = arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        var custom = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var customFormat = ((ulong)mesh.SurfaceGetFormat(0) >> (int)Mesh.ArrayFormat.FormatCustom0Shift)
            & (ulong)Mesh.ArrayFormat.FormatCustomMask;
        Require(customFormat == (ulong)Mesh.ArrayCustomFormat.RgbaFloat, "表现通道需要完整 float 精度。");
        var outward = (vertices[indices[2]] - vertices[indices[0]]).Cross(vertices[indices[1]] - vertices[indices[0]]).Normalized();
        Require(outward.Dot(Vector3.Back) > 0.999f, "正面绕序没有保持契约的外侧。");
        for (var index = 0; index < source.Vertices.Length; index++)
        {
            var expected = source.Vertices[index];
            Require(vertices[index].IsEqualApprox(new Vector3(expected.Position.X, expected.Position.Y, expected.Position.Z)), "格坐标被缩放或反射。");
            Require(normals[index].Dot(outward) > 0.999f, "上传法线不朝外。");
            Require(uv[index].IsEqualApprox(new Vector2(expected.TextureUv.X, expected.TextureUv.Y)), "TextureUv 丢失。");
            Require(faceUv[index].IsEqualApprox(new Vector2(expected.FaceUv.X, expected.FaceUv.Y)), "FaceUv 丢失。");
            Near(custom[index * 4], (int)expected.ExposedEdges);
            Near(custom[index * 4 + 1], expected.RelativeHeight);
            Near(custom[index * 4 + 2], expected.Damage);
        }
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
    public void Model_overrides_preserve_surface_order_cutout_texture_and_height_plane()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        var bytes = GlbFixture.Create(png: image.SavePngToBuffer(), baseColorFactor: [0.25f, 0.5f, 0.75f, 0.8f],
            changeJson: json => json["materials"]![1]!["pbrMetallicRoughness"] =
                json["materials"]![0]!["pbrMetallicRoughness"]!.DeepClone());
        var inspection = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("render-model"), bytes);
        Require(inspection is AssetInspection.Success, "模型夹具无法解码。");
        var asset = new ContentAsset(new("render-model"), bytes, ((AssetInspection.Success)inspection).Metadata, []);
        using var resources = new SceneResources(cache, Content(asset));
        var source = new SceneVisual.Model(asset.Revision,
            [MaterialCategory.Concrete, MaterialCategory.PaintedMetal, MaterialCategory.Glass],
            new("#abcdef"), new(true, 0x8000000100000001UL, 0.25f, true), new(0.1f, 0.2f, 0.3f, 0.4f));
        using var first = resources.Prepare(source);
        using var second = resources.Prepare(source with { RelativeHeightPlane = new(0, 1, 0, -1) });
        var instance = Instance(first);
        var mesh = instance.Mesh;
        Require(mesh.GetInstanceId() == Instance(second).Mesh.GetInstanceId(), "显示实例绕过了模型缓存。");
        for (var index = 0; index < 3; index++)
        {
            var material = (ShaderMaterial)instance.GetSurfaceOverrideMaterial(index);
            AssertSurface(material, source.Surface);
            Require(InstanceUniforms(instance).SequenceEqual(["relative_height_plane"]), "模型高度平面没有启用。");
            Require(instance.GetInstanceShaderParameter("relative_height_plane").AsVector4().IsEqualApprox(new Vector4(0.1f, 0.2f, 0.3f, 0.4f)), "模型相对高度平面丢失。");
            Require(material.GetInstanceId() == Instance(second).GetSurfaceOverrideMaterial(index).GetInstanceId(),
                "不同高度平面的实例没有共享相同表面材质。");
            Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#abcdef")), "模型涂装丢失。");
            Require(material.GetShaderParameter("alpha_cutout").AsBool() == (index == 1), "模型 alpha mask 与表面次序不对应。");
        }
        var mask = (ShaderMaterial)instance.GetSurfaceOverrideMaterial(1);
        Near(mask.GetShaderParameter("alpha_cutoff").AsSingle(), 0.5f);
        Require(mask.GetShaderParameter("base_color").AsColor().IsEqualApprox(new Color(0.25f, 0.5f, 0.75f, 0.8f)), "模型基础色系数丢失。");
        var texture = mask.GetShaderParameter("base_texture").As<Texture2D>();
        using (var mipmapped = texture.GetImage()) Require(mipmapped.HasMipmaps(), "模型内嵌纹理没有 mipmap。");
        Require(texture.GetInstanceId() == ((ShaderMaterial)instance.GetSurfaceOverrideMaterial(2)).GetShaderParameter("base_texture").As<Texture2D>().GetInstanceId(), "同模型内嵌图片没有共享。");
        Require(!first.CanUpdate(source with { Paint = null }) &&
            !first.CanUpdate(source with { Surface = new(false, ulong.MaxValue, 0.5f, false) }),
            "涂装或表面状态变化必须重新准备共享材质。");
        var shared = (ShaderMaterial)Instance(second).GetSurfaceOverrideMaterial(0);
        var normalShader = shared.Shader;
        resources.ExemptConstructionFog(first, true);
        var privateMaterial = (ShaderMaterial)instance.GetSurfaceOverrideMaterial(0);
        Require(privateMaterial.GetInstanceId() != shared.GetInstanceId() && privateMaterial.Shader != normalShader &&
            shared.Shader == normalShader, "跟随模型必须私有化材质，不能豁免另一实例的构建雾。");
        Require(InstanceUniforms(instance).SequenceEqual(["relative_height_plane"]), "跟随模型丢失高度平面实例变量。");
        AssertSurface(privateMaterial, source.Surface);
        resources.ExemptConstructionFog(first, false);
        Require(privateMaterial.Shader == normalShader, "取消跟随必须恢复普通模型着色器。");
        resources.ExemptConstructionFog(first, true);
        Require(instance.GetSurfaceOverrideMaterial(0).GetInstanceId() == privateMaterial.GetInstanceId(),
            "再次跟随应复用私有材质，不能反复分配。");
        var next = source with { RelativeHeightPlane = new(0, 1, 0, -2) };
        Require(first.CanUpdate(next), "高度平面变化不应重建网格或材质。");
        first.Update(next);
        Require(instance.GetInstanceShaderParameter("relative_height_plane").AsVector4().IsEqualApprox(new Vector4(0, 1, 0, -2)), "模型高度平面没有更新。");
        Require(Instance(second).GetInstanceShaderParameter("relative_height_plane").AsVector4().IsEqualApprox(new Vector4(0, 1, 0, -1)),
            "更新实例高度影响了另一实例。");
        first.Dispose();
        Require(!GodotObject.IsInstanceValid(privateMaterial) && shared.Shader == normalShader,
            "释放跟随实例必须释放私有副本并保留另一实例的普通材质。");
        Require(GodotObject.IsInstanceValid(mesh) && GodotObject.IsInstanceValid(texture) && GodotObject.IsInstanceValid(mask),
            "释放一个模型实例销毁了另一实例的资源。");
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(texture) && !GodotObject.IsInstanceValid(mask),
            "最后模型显示实例没有释放共享材质、模型网格和内嵌纹理。");
    }

    [Test]
    public void Real_scene_model_projection_hands_bottom_center_and_ordered_surfaces_to_resources()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var bytes = GlbFixture.Create();
        var inspection = new GodotAssetInspector(dispatcher).Inspect(AssetKind.Glb, new("scene-model"), bytes);
        Require(inspection is AssetInspection.Success, "模型夹具无法解码。");
        var asset = new ContentAsset(new("scene-model"), bytes, ((AssetInspection.Success)inspection).Metadata, []);
        var voxel = new Train.Core.Contracts.VoxelDefinitionId("test:scene-model");
        var design = new Train.Core.Contracts.DesignHash("scene-model-design");
        ImmutableArray<MaterialCategory> categories =
            [MaterialCategory.Glass, MaterialCategory.Concrete, MaterialCategory.PaintedMetal];
        var content = new FixedContentView
        {
            Generation = new(1),
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
                { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) },
            VoxelAppearances = new Dictionary<Train.Core.Contracts.VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(new(voxel,
                    AllowedVoxelOrientations.All24, new VoxelCollision.None(),
                    new VoxelPresentation.Mesh(asset.Revision, categories), new(false, null)))
            }
        };
        var projection = new DesignSceneProjection(design, null, null, true, null, null,
            [new(new(2, 3, 4), voxel, VoxelOrientation.PxPy, null, new("#abcdef"), new(1),
                new DesignElementPath.Cell([], 0), [], new(2, 3, 4), VoxelOrientation.PxPy, false)], [], [], [], []);
        var draft = new DesignDraft(new("Model", ""), new DesignSpecification.SceneModel(null), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design, new("composition"), draft, null, projection, true, []);
        using var preview = ((SceneResult<IDesignPreview>.Success)Train.Scenes.ScenesFactory.Create()
            .PreviewDesign(content, evaluation)).Value;
        var frame = ((SceneResult<ISceneFrame>.Success)preview.Present()).Value;
        var projected = frame.Update.Upserts.Single(item => item.Visuals.OfType<SceneVisual.Model>().Any());
        var model = projected.Visuals.OfType<SceneVisual.Model>().Single();
        using var resources = new SceneResources(cache, content);
        using var visual = resources.Prepare(model);
        var instance = Instance(visual);
        Require(projected.Pose.Position == new NVector3(2.5f, 3, 4.5f), "Scenes 没有将模型原点放在格底面中心。");
        Require(visual.Node.Position.IsZeroApprox() && instance.Position.IsZeroApprox(), "资源交接额外移动了模型原点。");
        Require(instance.Mesh.GetSurfaceCount() == categories.Length, "GLB 表面数与 Scenes 材质列表不一致。");
        for (var index = 0; index < categories.Length; index++)
        {
            var material = (ShaderMaterial)instance.GetSurfaceOverrideMaterial(index);
            Require(material.GetShaderParameter("material_category").AsInt32() == (int)categories[index],
                "真实 Scenes 材质次序未传到对应 GLB 表面。");
            Require(material.GetShaderParameter("paint_color").AsColor().IsEqualApprox(Color.FromHtml("#abcdef")),
                "模型格涂装未传到 GLB 表面。");
        }
    }

    [Test]
    public void Display_leases_share_mesh_and_mipmapped_face_texture_until_last_visual()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("display-shared");
        using var resources = new SceneResources(cache, Content(asset));
        var source = new SceneVisual.Geometry(Triangle(new SceneMaterial.Voxel(asset.Revision, MaterialCategory.Organic, null)), new(false, 0, 0, false));
        using var first = resources.Prepare(source);
        using var second = resources.Prepare(source);
        var mesh = Instance(first).Mesh;
        var texture = Material(first).GetShaderParameter("base_texture").As<ImageTexture>();
        Require(mesh.GetInstanceId() == Instance(second).Mesh.GetInstanceId(), "同不可变几何没有共享 ArrayMesh。");
        Require(texture.GetInstanceId() == Material(second).GetShaderParameter("base_texture").As<ImageTexture>().GetInstanceId(), "显示实例没有共享面纹理。");
        using (var image = texture.GetImage()) Require(image.HasMipmaps(), "显示器使用了无 mipmap 的面纹理。");
        first.Node.Position = new(100, 200, -300);
        Require(first.CanUpdate(source), "对象移动破坏了几何复用。");
        first.Update(source with { Surface = new(true, 17, 0.1f, false) });
        Require(mesh.GetInstanceId() == Instance(first).Mesh.GetInstanceId(), "平移或状态更新重建了 ArrayMesh。");
        first.Dispose();
        Require(GodotObject.IsInstanceValid(mesh) && GodotObject.IsInstanceValid(texture), "共享资源在最后使用者结束前被释放。");
        second.Dispose();
        Require(!GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(texture), "最后显示租约没有释放共享资源。");
    }

    [Test]
    public void Missing_markers_prepare_known_cells_and_unknown_positions_and_release_owned_resources()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        var reference = new ContentReference.Design(new("missing-design"));
        foreach (var knownCell in new[] { true, false })
        {
            var source = new SceneVisual.Missing(reference, knownCell);
            using var visual = resources.Prepare(source);
            var node = visual.Node;
            var instance = Instance(visual);
            var mesh = (BoxMesh)instance.Mesh;
            var material = (StandardMaterial3D)instance.MaterialOverride;
            Require(node.GetParent() is null, "缺失标记在准备时不得接入显示树。");
            Require(instance.Position.IsEqualApprox(knownCell ? Vector3.One / 2 : Vector3.Zero),
                "已知格标记必须位于格中心，未知范围标记必须位于放置点。");
            Require(mesh.Size.X > 0 && mesh.Size.X <= 1 && mesh.Size.IsEqualApprox(Vector3.One * mesh.Size.X),
                "缺失标记必须是格尺度内的立方体。");
            Require(material.AlbedoColor.IsEqualApprox(Colors.Magenta) &&
                material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded,
                "缺失标记必须使用可识别的不受光照影响的洋红色。");
            Require(visual.CanUpdate(source) && !visual.CanUpdate(source with { KnownCell = !knownCell }),
                "缺失标记类型改变需要重新准备。");
            resources.ExemptConstructionFog(visual, true);
            Require(material.StencilMode == BaseMaterial3D.StencilModeEnum.Custom &&
                material.StencilFlags == (int)BaseMaterial3D.StencilFlagsEnum.Write && material.StencilReference == 1,
                "被跟随车辆的缺失标记也必须豁免构建雾。");
            resources.ExemptConstructionFog(visual, false);
            Require(material.StencilMode == BaseMaterial3D.StencilModeEnum.Disabled && material.AlbedoColor == Colors.Magenta,
                "取消跟随须恢复缺失标记的雾遮挡及原有外观。");
            visual.Dispose();
            Require(!GodotObject.IsInstanceValid(node) && !GodotObject.IsInstanceValid(instance) &&
                !GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(material),
                "缺失标记的节点、网格及材质必须一起释放。");
        }
    }

    [Test]
    public void Wheelset_uses_scene_dimensions_local_x_axis_and_supplied_rotation()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var resources = new SceneResources(cache, new FixedContentView());
        var source = new SceneVisual.Wheelset(7, 1.25, 0.375, 0.7);
        using var visual = resources.Prepare(source);
        var wheels = visual.Node.GetChildren().OfType<MeshInstance3D>().OrderBy(node => node.Position.X).ToArray();
        Require(wheels.Length == 2, "轮对需要两侧车轮。");
        for (var index = 0; index < wheels.Length; index++)
        {
            var cylinder = (CylinderMesh)wheels[index].Mesh;
            Near(cylinder.TopRadius, source.RadiusCells);
            Near(cylinder.BottomRadius, source.RadiusCells);
            Near(cylinder.Height, source.WidthCells);
            Near(wheels[index].Position.X, (index == 0 ? -1 : 1) * source.GaugeCells / 2);
            Require(Math.Abs(wheels[index].Basis.Y.Dot(Vector3.Right)) > 0.999f, "车轮轴不沿对象局部 X。");
        }
        Near(visual.Node.Rotation.X, source.RotationRadians);
        var original = wheels.Select(wheel => wheel.Mesh.GetInstanceId()).ToArray();
        visual.Update(source with { RotationRadians = -0.3 });
        Near(visual.Node.Rotation.X, -0.3);
        Require(wheels.Select(wheel => wheel.Mesh.GetInstanceId()).SequenceEqual(original), "轮对转动重建了网格。");
        Require(!visual.CanUpdate(source with { WidthCells = 0.5 }), "轮对尺寸变化必须重新准备资源。");
    }

    [Test]
    public async Task Material_categories_and_builtins_have_sampling_contract_and_render_without_shader_errors()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        var asset = Png("categories");
        using var output = new GodotOutputCapture();
        SceneResources? resources = null;
        var visuals = new List<PreparedVisual>();
        var viewport = new SubViewport { Size = new(128, 128), OwnWorld3D = true, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        OS.AddLogger(output);
        try
        {
            resources = new SceneResources(cache, Content(asset));
            TestScene.AddChild(viewport);
            var surfaceCode = Godot.FileAccess.GetFileAsString("res://Rendering/scene_surface.gdshaderinc");
            var responses = new HashSet<(float Roughness, float Metallic, float Specular)>();
            foreach (var category in Enum.GetValues<MaterialCategory>())
            {
                var visual = resources.Prepare(new SceneVisual.Geometry(Triangle(new SceneMaterial.Voxel(asset.Revision, category, null)), new(false, 0, 0, false)));
                visuals.Add(visual);
                var material = Material(visual);
                responses.Add((material.GetShaderParameter("material_roughness").AsSingle(),
                    material.GetShaderParameter("material_metallic").AsSingle(), material.GetShaderParameter("material_specular").AsSingle()));
                var sampler = Regex.Match(surfaceCode, @"uniform\s+sampler2D\s+base_texture\s*:\s*([^;]+)");
                Require(sampler.Success && new[] { "source_color", "filter_linear_mipmap_anisotropic", "repeat_enable" }.All(sampler.Groups[1].Value.Contains), "基础贴图采样约定丢失。");
                Require(!Regex.IsMatch(material.Shader.Code + surfaceCode, @"\bALPHA\s*="), "Demo 材质包含半透明输出。");
            }
            Require(responses.Count == Enum.GetValues<MaterialCategory>().Length, "材质类别没有独立光照响应。");
            var builtinMaterials = new Dictionary<BuiltInMaterial, ShaderMaterial>();
            foreach (var builtin in Enum.GetValues<BuiltInMaterial>())
            {
                var visual = resources.Prepare(new SceneVisual.Geometry(Triangle(new SceneMaterial.BuiltIn(builtin)), new(false, 0, 0, false)));
                visuals.Add(visual);
                builtinMaterials.Add(builtin, Material(visual));
            }
            Require(builtinMaterials.Where(pair => pair.Key != BuiltInMaterial.Water)
                .Select(pair => pair.Value.GetShaderParameter("base_color").AsColor()).Distinct().Count() == 3,
                "钢轨、轨枕和施工没有独立的固有色。");
            Require(builtinMaterials[BuiltInMaterial.Water].Shader.GetInstanceId() != builtinMaterials[BuiltInMaterial.RailSteel].Shader.GetInstanceId(),
                "水面没有使用自己的着色器。");
            for (var index = 0; index < visuals.Count; index++)
            {
                visuals[index].Node.Position = new(index % 4 * 1.2f - 2.4f, index / 4 * 1.2f - 1.2f, 0);
                viewport.AddChild(visuals[index].Node);
            }
            var camera = new Camera3D { Position = new(0, 0, 8), Current = true };
            viewport.AddChild(camera);
            viewport.AddChild(new DirectionalLight3D { Rotation = new(-0.3f, -0.3f, 0) });
            var tree = TestScene.GetTree();
            for (var frame = 0; frame < 4; frame++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            // headless 使用 dummy 渲染器；真实窗口运行同一测试时才断言实际出图。
            if (DisplayServer.GetName() != "headless")
            {
                using var rendered = viewport.GetTexture().GetImage();
                Require(!rendered.IsEmpty(), "SubViewport 没有渲染输出。");
                Require(Enumerable.Range(0, rendered.GetWidth() * rendered.GetHeight()).Any(index =>
                    rendered.GetPixel(index % rendered.GetWidth(), index / rendered.GetWidth()).A > 0.5f), "材质场景没有绘制几何。");
            }
            Require(!output.Contains("Shader compilation failed") && !output.Contains("SHADER ERROR") && !output.Contains("Shader error"), "场景材质发生着色器编译错误。");
        }
        finally
        {
            foreach (var visual in visuals) visual.Dispose();
            resources?.Dispose();
            viewport.Free();
            OS.RemoveLogger(output);
        }
    }

    private static void AssertSurface(ShaderMaterial material, SurfaceState expected)
    {
        Require(material.GetShaderParameter("wear_enabled").AsBool() == expected.WearEnabled, "WearEnabled 丢失。");
        var halves = material.GetShaderParameter("wear_seed").AsVector2I();
        var seed = (ulong)unchecked((uint)halves.X) | ((ulong)unchecked((uint)halves.Y) << 32);
        Require(seed == expected.WearSeed, "WearSeed 在着色器上传时丢失 64 位精度。");
        Near(material.GetShaderParameter("surface_damage").AsSingle(), expected.Damage);
        Require(material.GetShaderParameter("scorched").AsBool() == expected.Scorched, "Scorched 丢失。");
    }

    private static SceneMesh Triangle(SceneMaterial material) => new(material,
        [new(NVector3.Zero, NVector3.UnitZ, new NVector2(0.1f, 0.2f), NVector2.Zero, ExposedFaceEdges.MinimumU, 0.125f, 0.25f),
         new(NVector3.UnitX, NVector3.UnitZ, new NVector2(0.4f, 0.3f), NVector2.UnitX, ExposedFaceEdges.MaximumU, 0.5f, 0.375f),
         new(NVector3.UnitY, NVector3.UnitZ, new NVector2(3.3f, -2.2f), NVector2.UnitY, ExposedFaceEdges.MinimumV | ExposedFaceEdges.MaximumV, 0.875f, 0.75f)], [0, 1, 2]);

    private static MeshInstance3D Instance(PreparedVisual visual) => visual.Node.GetChildren().OfType<MeshInstance3D>().Single();
    private static string[] InstanceUniforms(GeometryInstance3D instance) =>
        [.. RenderingServer.InstanceGeometryGetShaderParameterList(instance.GetInstance()).Select(item => item["name"].AsString())];
    private static ShaderMaterial Material(PreparedVisual visual) => (ShaderMaterial)Instance(visual).MaterialOverride;
    private static FixedContentView Content(ContentAsset asset) => new()
    {
        Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [asset.Revision] = new ContentLookup<ContentAsset>.Found(asset) }
    };
    private static ContentAsset Png(string revision)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return new(new(revision), ImmutableArray.CreateRange(image.SavePngToBuffer()), new AssetMetadata.Png(4, 4), []);
    }
    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < 0.001, $"数值错误：{actual}，期望 {expected}。");
}

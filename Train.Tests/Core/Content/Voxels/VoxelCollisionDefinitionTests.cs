// 体素碰撞定义的纯内容契约：覆盖严格解析、发布校验与语义修订。
// 测试只使用内存内容源，不依赖 Godot、世界适配器或内置开发资源。
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Core.Content.Voxels;

namespace Train.Tests.Core.Content.Voxels;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
[TestCategory("scope:voxels")]
public sealed class VoxelCollisionDefinitionTests
{
    private static readonly VoxelDefinitionId Iron = new("core", "ferrous-ore");
    private static readonly VoxelDefinitionId Hollow = new("core", "hollow");
    private static readonly VoxelDefinitionId Ledge = new("core", "ledge");

    [TestMethod]
    public void Formal_loader_accepts_the_built_in_eccentric_ledge_example()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        AddLedge(source);
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(Ledge, out var entry));
        Assert.AreEqual(VoxelCollisionKind.LocalBox, entry.Definition.Collision.Kind);
        Assert.AreEqual(0.125, entry.Definition.Collision.OffsetX);
        Assert.AreEqual(0, entry.Definition.Collision.OffsetY);
        Assert.AreEqual(0.25, entry.Definition.Collision.OffsetZ);
        Assert.AreEqual(0.25, entry.Definition.Collision.SizeX);
        Assert.AreEqual(0.875, entry.Definition.Collision.SizeY);
        Assert.AreEqual(0.5, entry.Definition.Collision.SizeZ);
        Assert.AreEqual(VoxelOrientationConstraint.All24, entry.Definition.AllowedOrientations);
    }

    [TestMethod]
    public void None_and_full_box_keep_working_and_reject_local_box_fields()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "voxels/hollow.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("hollow", "空心", "说明", tagsJson: "[]", collisionJson: "{\"kind\":\"none\"}")));
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var snapshot = reloader.Catalog.Current;
        Assert.AreEqual(VoxelCollisionKind.FullBox, snapshot!.Entries[Iron].Definition.Collision.Kind);
        Assert.AreEqual(VoxelCollisionKind.None, snapshot.Entries[Hollow].Definition.Collision.Kind);

        foreach (var collision in new[]
                 {
                     "{\"kind\":\"none\",\"offset\":{\"x\":0.1,\"y\":0.1,\"z\":0.1}}",
                     "{\"kind\":\"full_box\",\"size\":{\"x\":0.5,\"y\":0.5,\"z\":0.5}}",
                     "{\"kind\":\"full_box\",\"extra\":1}"
                 })
        {
            source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", collisionJson: collision)));

            var rejected = reloader.Reload();

            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, collision);
            Assert.AreSame(snapshot, reloader.Catalog.Current);
        }
    }

    [TestMethod]
    public void Illegal_local_box_shapes_reject_built_in_publish_and_exclude_the_mod_package()
    {
        var invalids = new[]
        {
            ContentFixtures.LocalBoxCollision(offsetX: double.NaN),
            ContentFixtures.LocalBoxCollision(sizeY: double.PositiveInfinity),
            ContentFixtures.LocalBoxCollision(sizeX: 0),
            ContentFixtures.LocalBoxCollision(sizeZ: -0.1),
            ContentFixtures.LocalBoxCollision(offsetX: -0.01),
            ContentFixtures.LocalBoxCollision(offsetX: 0.8, sizeX: 0.3),
            ContentFixtures.LocalBoxCollision(offsetX: 0.5, sizeX: 1e-20),
            ContentFixtures.LocalBoxCollision(offsetX: 1, sizeX: 1e-20),
            "{\"kind\":\"local_box\"}",
            "{\"kind\":\"local_box\",\"offset\":{\"x\":0.1,\"y\":0.1,\"z\":0.1}}",
            "{\"kind\":\"localBox\",\"offset\":{\"x\":0.125,\"y\":0,\"z\":0.25},\"size\":{\"x\":0.25,\"y\":0.875,\"z\":0.5}}",
            "{\"kind\":\"local_box\",\"offset\":{\"x\":0.125,\"y\":0,\"z\":0.25,\"w\":1},\"size\":{\"x\":0.25,\"y\":0.875,\"z\":0.5}}",
            "{\"kind\":\"local_box\",\"offset\":{\"x\":0.125,\"y\":0,\"z\":0.25},\"size\":{\"x\":0.25,\"y\":0.875,\"z\":0.5},\"boxes\":[]}"
        };

        foreach (var collision in invalids)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.ValidFerrousOre();
            AddLedge(source, collision);
            var reloader = ContentFixtures.Create(source, session.Logger);

            var rejected = reloader.Reload();

            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, collision);
            Assert.IsNull(reloader.Catalog.Current);
        }

        using var modSession = LoggerSession.Create();
        var mixed = ModFixtures.CorePlusCopper();
        mixed.AddMod("broken-box", ModFixtures.OtherUuid);
        mixed.AddVoxelFile(ModFixtures.OtherUuid, "bad.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", tagsJson: "[]", icon: "box.png", voxel: "box.png",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0.8, sizeX: 0.3))));
        mixed.AddImage(ModFixtures.OtherUuid, "box.png", ContentFixtures.Square(16));
        var mods = ContentFixtures.Create(mixed, modSession.Logger);

        var isolated = mods.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, isolated.Status);
        Assert.IsTrue(mods.Catalog.Current!.TryGet(Iron, out _));
        Assert.IsFalse(mods.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.OtherUuid, "ledge"), out _));
    }

    [TestMethod]
    public void Adapter_underflow_sizes_reject_publish_and_exclude_the_mod_package()
    {
        Assert.IsTrue(VoxelCollision.TryCreateLocalBox(0, 0, 0.25, 1e-50, 0.875, 0.5, out _));
        Assert.IsTrue(VoxelCollision.TryCreateLocalBox(0, 0, 0.25, double.Epsilon, 0.875, 0.5, out _));

        foreach (var sizeX in new[] { 1e-50, double.Epsilon })
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.ValidFerrousOre();
            AddLedge(source, ContentFixtures.LocalBoxCollision(offsetX: 0, sizeX: sizeX));
            var reloader = ContentFixtures.Create(source, session.Logger);

            var rejected = reloader.Reload();

            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, $"size.x={sizeX:G17}");
            Assert.IsNull(reloader.Catalog.Current);
        }

        using var modSession = LoggerSession.Create();
        var mixed = ModFixtures.CorePlusCopper();
        mixed.AddMod("thin-underflow", ModFixtures.OtherUuid);
        mixed.AddVoxelFile(ModFixtures.OtherUuid, "bad.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", tagsJson: "[]", icon: "box.png", voxel: "box.png",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0, sizeX: 1e-50))));
        mixed.AddImage(ModFixtures.OtherUuid, "box.png", ContentFixtures.Square(16));
        var mods = ContentFixtures.Create(mixed, modSession.Logger);

        var isolated = mods.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, isolated.Status);
        Assert.IsTrue(mods.Catalog.Current!.TryGet(Iron, out _));
        Assert.IsFalse(mods.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.OtherUuid, "ledge"), out _));
    }

    [TestMethod]
    public void Local_box_kind_and_geometry_enter_canonical_semantic_revision()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        AddLedge(source);
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var baseline = reloader.Catalog.Current!.Entries[Ledge].SemanticRevision;

        source.AddVoxelFile("core", "voxels/ledge.voxels.json", """
            {"schema_version":1,"voxels":[{"allowed_orientations":"all_24","behaviors":[],"collision":{"size":{"z":0.5,"y":0.875,"x":0.25},"kind":"local_box","offset":{"z":0.25,"y":0,"x":0.125}},"description":"说明","id":"ledge","mass":1,"max_durability":100,"name":"台沿","presentation":{"icon":"iron.png","kind":"cube","voxel_bottom":"iron.png","voxel_side":"iron.png","voxel_top":"iron.png"},"tags":[],"value":10}]}
            """);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreEqual(baseline, reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);

        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            AddLedge(source);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            Assert.AreEqual(baseline, reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        AddLedge(source, name: "新台沿", description: "新说明");
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreEqual(baseline, reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);
        Assert.AreEqual("新台沿", reloader.Catalog.Current.Entries[Ledge].Definition.Name);

        source.AddImage("core", "iron.png", ContentFixtures.Square(16, 7));
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreEqual(baseline, reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);

        AddLedge(source, ContentFixtures.LocalBoxCollision(sizeX: 0.3), "新台沿", "新说明");
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreNotEqual(baseline, reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);

        var afterGeometry = reloader.Catalog.Current.Entries[Ledge].SemanticRevision;
        AddLedge(source, "{\"kind\":\"full_box\"}", "新台沿", "新说明");
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreNotEqual(afterGeometry, reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);
    }

    private static void AddLedge(
        MemoryContentSource source,
        string? collision = null,
        string name = "台沿",
        string description = "说明") =>
        source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "ledge",
                name,
                description,
                value: 10,
                tagsJson: "[]",
                collisionJson: collision ?? ContentFixtures.LocalBoxCollision())));
}

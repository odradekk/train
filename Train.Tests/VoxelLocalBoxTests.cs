using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class VoxelLocalBoxTests
{
    private static readonly VoxelDefinitionId Iron = new("core", "iron-ore");
    private static readonly VoxelDefinitionId Hollow = new("core", "hollow");
    private static readonly VoxelDefinitionId Ledge = new("core", "ledge");
    private static readonly WorldVoxelCoord Empty = new(0, 0, 0);

    // Hand-worked probes for the published eccentric example:
    // offset (0.125, 0, 0.25), size (0.25, 0.875, 0.5) in local cell units.
    private const double IdentityHitMinX = 0.18;
    private const double IdentityHitMinY = 0.08;
    private const double IdentityHitMinZ = 0.48;
    private const double IdentityHitMaxX = 0.22;
    private const double IdentityHitMaxY = 0.12;
    private const double IdentityHitMaxZ = 0.52;
    private const double IdentityMissMinX = 0.68;
    private const double IdentityMissMinY = 0.38;
    private const double IdentityMissMinZ = 0.48;
    private const double IdentityMissMaxX = 0.72;
    private const double IdentityMissMaxY = 0.42;
    private const double IdentityMissMaxZ = 0.52;
    private const double Yaw90HitMinX = 0.48;
    private const double Yaw90HitMinY = 0.38;
    private const double Yaw90HitMinZ = 0.18;
    private const double Yaw90HitMaxX = 0.52;
    private const double Yaw90HitMaxY = 0.42;
    private const double Yaw90HitMaxZ = 0.22;
    private const double TiltHitMinX = 0.18;
    private const double TiltHitMinY = 0.48;
    private const double TiltHitMinZ = 0.08;
    private const double TiltHitMaxX = 0.22;
    private const double TiltHitMaxY = 0.52;
    private const double TiltHitMaxZ = 0.12;

    [TestMethod]
    public void Formal_loader_accepts_the_built_in_eccentric_ledge_example()
    {
        using var harness = LocalBoxHarness.Create();
        Assert.IsTrue(harness.Reloader.Catalog.Current!.TryGet(Ledge, out var entry));
        Assert.AreEqual(VoxelCollisionKind.LocalBox, entry.Definition.Collision.Kind);
        Assert.AreEqual(0.125, entry.Definition.Collision.OffsetX);
        Assert.AreEqual(0, entry.Definition.Collision.OffsetY);
        Assert.AreEqual(0.25, entry.Definition.Collision.OffsetZ);
        Assert.AreEqual(0.25, entry.Definition.Collision.SizeX);
        Assert.AreEqual(0.875, entry.Definition.Collision.SizeY);
        Assert.AreEqual(0.5, entry.Definition.Collision.SizeZ);
        Assert.AreEqual(VoxelOrientationConstraint.All24, entry.Definition.AllowedOrientations);

        var fromDisk = File.ReadAllText(BuiltInLedgePath());
        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", fromDisk);
        ContentFixtures.AddSquareImages(
            harness.Source,
            "voxels/iron-ore/icon.png",
            "voxels/iron-ore/top.png",
            "voxels/iron-ore/side.png",
            "voxels/iron-ore/bottom.png");
        var reloaded = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, reloaded.Status);
        Assert.AreEqual(VoxelCollisionKind.LocalBox, harness.Reloader.Catalog.Current!.Entries[Ledge].Definition.Collision.Kind);
        Assert.AreEqual(0.125, harness.Reloader.Catalog.Current.Entries[Ledge].Definition.Collision.OffsetX);
        Assert.AreEqual(0.875, harness.Reloader.Catalog.Current.Entries[Ledge].Definition.Collision.SizeY);
    }

    [TestMethod]
    public void None_and_full_box_keep_working_and_reject_local_box_fields()
    {
        using var harness = LocalBoxHarness.Create();
        Assert.AreEqual(VoxelCollisionKind.FullBox, harness.Reloader.Catalog.Current!.Entries[Iron].Definition.Collision.Kind);
        Assert.AreEqual(VoxelCollisionKind.None, harness.Reloader.Catalog.Current.Entries[Hollow].Definition.Collision.Kind);

        foreach (var collision in new[]
                 {
                     "{\"kind\":\"none\",\"offset\":{\"x\":0.1,\"y\":0.1,\"z\":0.1}}",
                     "{\"kind\":\"full_box\",\"size\":{\"x\":0.5,\"y\":0.5,\"z\":0.5}}",
                     "{\"kind\":\"full_box\",\"extra\":1}"
                 })
        {
            harness.Source.AddVoxelFile("core", "voxels/iron-ore.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("iron-ore", "铁矿", "说明", collisionJson: collision)));
            var rejected = harness.Reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, collision);
            Assert.AreEqual(VoxelCollisionKind.FullBox, harness.Reloader.Catalog.Current!.Entries[Iron].Definition.Collision.Kind);
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
            "{\"kind\":\"local_box\"}",
            "{\"kind\":\"local_box\",\"offset\":{\"x\":0.1,\"y\":0.1,\"z\":0.1}}",
            "{\"kind\":\"localBox\",\"offset\":{\"x\":0.125,\"y\":0,\"z\":0.25},\"size\":{\"x\":0.25,\"y\":0.875,\"z\":0.5}}",
            "{\"kind\":\"local_box\",\"offset\":{\"x\":0.125,\"y\":0,\"z\":0.25,\"w\":1},\"size\":{\"x\":0.25,\"y\":0.875,\"z\":0.5}}",
            "{\"kind\":\"local_box\",\"offset\":{\"x\":0.125,\"y\":0,\"z\":0.25},\"size\":{\"x\":0.25,\"y\":0.875,\"z\":0.5},\"boxes\":[]}"
        };

        foreach (var collision in invalids)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.ValidIron();
            source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "说明", tagsJson: "[]", collisionJson: collision)));
            var builtIn = ContentFixtures.Create(source, session.Logger);
            var rejected = builtIn.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, collision);
            Assert.IsNull(builtIn.Catalog.Current);
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
    public void Identity_local_box_hits_only_inside_the_eccentric_volume()
    {
        using var harness = LocalBoxHarness.Create();
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(Empty, Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(Ledge, harness.Commands.Query(Empty).Voxel!.DefinitionId);
        Assert.AreEqual(VoxelOrientation.Identity, harness.Commands.Query(Empty).Voxel!.Orientation);

        var cell = harness.Commands.QueryCollision(Empty);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, cell.Status);
        Assert.AreEqual(VoxelCollisionKind.LocalBox, cell.Hits[0].Kind);
        Assert.AreEqual(Ledge, cell.Hits[0].DefinitionId);

        AssertHit(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);
        AssertMiss(harness.Commands, IdentityMissMinX, IdentityMissMinY, IdentityMissMinZ, IdentityMissMaxX, IdentityMissMaxY, IdentityMissMaxZ);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(new WorldVoxelCoord(4, 4, 4)).Status);
    }

    [TestMethod]
    public void Asymmetric_rotation_moves_the_hit_region_for_all_24_orientations()
    {
        using var harness = LocalBoxHarness.Create();
        var tilt = new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveZ);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Identity).Status);
        AssertHit(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);
        AssertMiss(harness.Commands, Yaw90HitMinX, Yaw90HitMinY, Yaw90HitMinZ, Yaw90HitMaxX, Yaw90HitMaxY, Yaw90HitMaxZ);
        AssertMiss(harness.Commands, TiltHitMinX, TiltHitMinY, TiltHitMinZ, TiltHitMaxX, TiltHitMaxY, TiltHitMaxZ);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(Empty).Status);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Yaw90).Status);
        AssertMiss(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);
        AssertHit(harness.Commands, Yaw90HitMinX, Yaw90HitMinY, Yaw90HitMinZ, Yaw90HitMaxX, Yaw90HitMaxY, Yaw90HitMaxZ);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(Empty).Status);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, tilt).Status);
        AssertMiss(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);
        AssertHit(harness.Commands, TiltHitMinX, TiltHitMinY, TiltHitMinZ, TiltHitMaxX, TiltHitMaxY, TiltHitMaxZ);

        foreach (var orientation in VoxelOrientation.All)
        {
            Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(Empty).Status);
            Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, orientation).Status);
            Assert.AreEqual(orientation, harness.Commands.Query(Empty).Voxel!.Orientation);
            Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(Empty).Status);
            // Independent of ToWorldCell: this eccentric box never covers the far +X+Y+Z corner.
            AssertMiss(harness.Commands, 0.88, 0.88, 0.88, 0.96, 0.96, 0.96);
        }

        Assert.AreEqual(24, VoxelOrientation.All.Count);
    }

    [TestMethod]
    public void Occupancy_survives_outside_the_box_and_clears_with_remove_or_lethal_damage()
    {
        using var harness = LocalBoxHarness.Create();
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(Empty, Iron, VoxelOrientation.Identity).Status);
        AssertMiss(harness.Commands, IdentityMissMinX, IdentityMissMinY, IdentityMissMinZ, IdentityMissMaxX, IdentityMissMaxY, IdentityMissMaxZ);
        Assert.IsTrue(harness.Commands.Query(Empty).IsOccupied);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(Empty).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Commands.QueryCollision(Empty).Status);
        AssertMiss(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Yaw90).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Commands.Damage(Empty, 25).Status);
        Assert.AreEqual(75, harness.Commands.Query(Empty).Voxel!.CurrentDurability);
        AssertHit(harness.Commands, Yaw90HitMinX, Yaw90HitMinY, Yaw90HitMinZ, Yaw90HitMaxX, Yaw90HitMaxY, Yaw90HitMaxZ);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Damage(Empty, 75).Status);
        Assert.IsFalse(harness.Commands.Query(Empty).IsOccupied);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Commands.QueryCollision(Empty).Status);
    }

    [TestMethod]
    public void Local_box_kind_and_geometry_enter_canonical_semantic_revision()
    {
        using var harness = LocalBoxHarness.Create();
        var baseline = harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision;

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", """
            {"schema_version":1,"voxels":[{"allowed_orientations":"all_24","behaviors":[],"collision":{"size":{"z":0.5,"y":0.875,"x":0.25},"kind":"local_box","offset":{"z":0.25,"y":0,"x":0.125}},"description":"说明","id":"ledge","mass":1,"max_durability":100,"name":"台沿","presentation":{"icon":"iron.png","kind":"cube","voxel_bottom":"iron.png","voxel_side":"iron.png","voxel_top":"iron.png"},"tags":[],"value":10}]}
            """);
        var reordered = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, reordered.Status);
        Assert.AreEqual(baseline, harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);

        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                    collisionJson: ContentFixtures.LocalBoxCollision())));
            var cultured = harness.Reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Committed, cultured.Status);
            Assert.AreEqual(baseline, harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "新台沿", "新说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision())));
        var display = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, display.Status);
        Assert.AreEqual(baseline, harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);
        Assert.AreEqual("新台沿", harness.Reloader.Catalog.Current.Entries[Ledge].Definition.Name);

        harness.Source.AddImage("core", "iron.png", ContentFixtures.Square(16, 7));
        var pixels = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, pixels.Status);
        Assert.AreEqual(baseline, harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "新台沿", "新说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(sizeX: 0.3))));
        var geometry = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, geometry.Status);
        Assert.AreNotEqual(baseline, harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);

        var afterGeometry = harness.Reloader.Catalog.Current.Entries[Ledge].SemanticRevision;
        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "新台沿", "新说明", value: 10, tagsJson: "[]", collisionJson: "{\"kind\":\"full_box\"}")));
        var kind = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, kind.Status);
        Assert.AreNotEqual(afterGeometry, harness.Reloader.Catalog.Current!.Entries[Ledge].SemanticRevision);
    }

    [TestMethod]
    public void Legal_kind_and_geometry_reload_applies_to_existing_voxels_and_rejects_keep_state()
    {
        using var harness = LocalBoxHarness.Create();
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Yaw90).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Commands.Damage(Empty, 20).Status);
        var generation = harness.Reloader.Catalog.Current!.Generation;
        AssertHit(harness.Commands, Yaw90HitMinX, Yaw90HitMinY, Yaw90HitMinZ, Yaw90HitMaxX, Yaw90HitMaxY, Yaw90HitMaxZ);

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]", collisionJson: "{\"kind\":\"full_box\"}")));
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreEqual(VoxelOrientation.Yaw90, harness.Commands.Query(Empty).Voxel!.Orientation);
        Assert.AreEqual(80, harness.Commands.Query(Empty).Voxel!.CurrentDurability);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(Empty).Status);
        Assert.AreEqual(VoxelCollisionKind.FullBox, harness.Commands.QueryCollision(
            IdentityMissMinX, IdentityMissMinY, IdentityMissMinZ, IdentityMissMaxX, IdentityMissMaxY, IdentityMissMaxZ).Hits[0].Kind);

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0.5, offsetY: 0.5, offsetZ: 0.5, sizeX: 0.375, sizeY: 0.375, sizeZ: 0.375))));
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreEqual(VoxelOrientation.Yaw90, harness.Commands.Query(Empty).Voxel!.Orientation);
        Assert.AreEqual(80, harness.Commands.Query(Empty).Voxel!.CurrentDurability);
        AssertMiss(harness.Commands, Yaw90HitMinX, Yaw90HitMinY, Yaw90HitMinZ, Yaw90HitMaxX, Yaw90HitMaxY, Yaw90HitMaxZ);
        AssertHit(harness.Commands, 0.28, 0.62, 0.62, 0.32, 0.68, 0.68);

        var snapshot = harness.Reloader.Catalog.Current;
        var afterLegal = snapshot.Generation;
        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0.8, sizeX: 0.4))));
        var illegal = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, illegal.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(afterLegal, illegal.CatalogVersion);
        AssertHit(harness.Commands, 0.28, 0.62, 0.62, 0.32, 0.68, 0.68);
        Assert.AreEqual(80, harness.Commands.Query(Empty).Voxel!.CurrentDurability);

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]", orientations: "none",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0.5, offsetY: 0.5, offsetZ: 0.5, sizeX: 0.375, sizeY: 0.375, sizeZ: 0.375))));
        var orientationConflict = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, orientationConflict.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(VoxelOrientation.Yaw90, harness.Commands.Query(Empty).Voxel!.Orientation);

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("stone", "石", "说明", tagsJson: "[]")));
        var missing = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, missing.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(Ledge, harness.Commands.Query(Empty).Voxel!.DefinitionId);
        Assert.AreNotEqual(generation, afterLegal);
    }

    [TestMethod]
    public void Console_collide_reports_local_box_hits_through_the_session()
    {
        using var harness = LocalBoxHarness.Create();
        var console = new VoxelConsoleCommands(harness.Session);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(Empty, Ledge, VoxelOrientation.Identity).Status);

        var hit = console.Execute(["collide", "0", "0", "0"]);
        StringAssert.Contains(hit, "Collision Hit");
        StringAssert.Contains(hit, "core:ledge");
        StringAssert.Contains(hit, "local_box");

        var inside = console.Execute(["collide", "0.18", "0.08", "0.48", "0.22", "0.12", "0.52"]);
        StringAssert.Contains(inside, "Collision Hit");
        var outside = console.Execute(["collide", "0.68", "0.38", "0.48", "0.72", "0.42", "0.52"]);
        StringAssert.Contains(outside, "Collision Miss");
        StringAssert.Contains(console.Execute(["collide", "NaN", "0", "0", "1", "1", "1"]), "InvalidQuery");
        StringAssert.Contains(console.Execute(["collide", "2147483647", "0", "0"]), "OutOfBounds");
        StringAssert.Contains(console.Execute(["collide", "foo", "0", "0"]), "Usage:");
    }

    [TestMethod]
    public void Existing_invalid_probe_and_extrema_contracts_stay_unchanged()
    {
        using var harness = LocalBoxHarness.Create();
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(default(VoxelCollisionBox)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(0, 0, 0, 0, 0, 0).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(double.NaN, 0, 0, 1, 1, 1).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.OutOfBounds, harness.Commands.QueryCollision(new WorldVoxelCoord(int.MaxValue, 0, 0)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.OutOfBounds, harness.Commands.QueryCollision(new WorldVoxelCoord(int.MinValue, 0, 0)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.OutOfBounds, harness.Commands.QueryCollision(32, 0, 0, 33, 1, 1).Status);
    }

    private static void AssertHit(
        VoxelWorldCommands commands,
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ)
    {
        var result = commands.QueryCollision(minX, minY, minZ, maxX, maxY, maxZ);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, result.Status, $"{minX},{minY},{minZ}->{maxX},{maxY},{maxZ}");
        Assert.AreEqual(Empty, result.Hits[0].Cell);
        Assert.AreEqual(VoxelCollisionKind.LocalBox, result.Hits[0].Kind);
    }

    private static void AssertMiss(
        VoxelWorldCommands commands,
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ)
    {
        var result = commands.QueryCollision(minX, minY, minZ, maxX, maxY, maxZ);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, result.Status, $"{minX},{minY},{minZ}->{maxX},{maxY},{maxZ}");
    }

    private static string BuiltInLedgePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "builtin", "core", "resources", "voxels", "ledge.voxels.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate builtin/core/resources/voxels/ledge.voxels.json.");
    }

    private sealed class LocalBoxHarness : IDisposable
    {
        private readonly LoggerSession _session;

        public VoxelWorldCommands Commands { get; }
        public VoxelDevelopmentSession Session { get; }
        public ContentReloader Reloader { get; }
        public MemoryContentSource Source { get; }

        private LocalBoxHarness(
            LoggerSession session,
            MemoryContentSource source,
            ContentReloader reloader,
            VoxelWorldCommands commands,
            VoxelDevelopmentSession voxelSession)
        {
            _session = session;
            Source = source;
            Reloader = reloader;
            Commands = commands;
            Session = voxelSession;
        }

        public static LocalBoxHarness Create()
        {
            var session = LoggerSession.Create();
            var source = ContentFixtures.ValidIron();
            source.AddVoxelFile("core", "voxels/hollow.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("hollow", "空心占格", "占格但不碰撞", tagsJson: "[]", collisionJson: "{\"kind\":\"none\"}")));
            source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "偏心局部盒", value: 10, tagsJson: "[]",
                    collisionJson: ContentFixtures.LocalBoxCollision())));
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
            reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
            return new LocalBoxHarness(
                session,
                source,
                reloader,
                voxelSession.Commands,
                voxelSession);
        }

        public void Dispose() => _session.Dispose();
    }
}

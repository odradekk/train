using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:voxels")]
public sealed class VoxelLocalBoxTests
{
    private static readonly VoxelDefinitionId Iron = new("core", "ferrous-ore");
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

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(sizeZ: 0.4))));
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);

        foreach (var orientation in VoxelOrientation.All)
        {
            Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(Empty).Status);
            Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, orientation).Status);
            Assert.AreEqual(orientation, harness.Commands.Query(Empty).Voxel!.Orientation);
            Assert.IsTrue(ExpectedAsymmetric24.TryGetValue(orientation.ToString(), out var expected), orientation.ToString());
            AssertHit(harness.Commands, expected.HitMinX, expected.HitMinY, expected.HitMinZ, expected.HitMaxX, expected.HitMaxY, expected.HitMaxZ);
            AssertMiss(harness.Commands, expected.MissMinX, expected.MissMinY, expected.MissMinZ, expected.MissMaxX, expected.MissMaxY, expected.MissMaxZ);
        }

        Assert.AreEqual(24, ExpectedAsymmetric24.Count);
        Assert.AreEqual(24, VoxelOrientation.All.Count);
    }

    [TestMethod]
    public void Collapsed_local_box_ends_reject_publish_and_keep_existing_hits()
    {
        using var harness = LocalBoxHarness.Create();
        var translated = new WorldVoxelCoord(1, 0, 0);
        var negative = new VoxelOrientation(VoxelAxis.NegativeX, VoxelAxis.PositiveY);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(translated, Ledge, negative).Status);
        AssertHit(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);
        AssertHit(harness.Commands, 1.68, 0.08, 0.48, 1.72, 0.12, 0.52, translated);
        AssertMiss(harness.Commands, 1.18, 0.08, 0.48, 1.22, 0.12, 0.52);
        var snapshot = harness.Reloader.Catalog.Current;
        var generation = snapshot!.Generation;

        foreach (var (offsetX, sizeX) in new[] { (0.5, 1e-20), (1.0, 1e-20) })
        {
            harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                    collisionJson: ContentFixtures.LocalBoxCollision(offsetX: offsetX, sizeX: sizeX))));
            var rejected = harness.Reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, $"offset.x={offsetX} size.x={sizeX}");
            Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
            Assert.AreEqual(generation, rejected.CatalogVersion);
            AssertHit(harness.Commands, IdentityHitMinX, IdentityHitMinY, IdentityHitMinZ, IdentityHitMaxX, IdentityHitMaxY, IdentityHitMaxZ);
            AssertHit(harness.Commands, 1.68, 0.08, 0.48, 1.72, 0.12, 0.52, translated);
            Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(Empty).Status);
            Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(translated).Status);
        }

        var thrown = Assert.ThrowsException<ArgumentException>(() =>
            VoxelCollision.CreateLocalBox(0.5, 0, 0.25, 1e-20, 0.875, 0.5));
        Assert.AreNotEqual("sizeX", thrown.ParamName);
    }

    [TestMethod]
    public void Thin_local_box_stays_a_hit_after_real_world_translation()
    {
        var identity = VoxelWorldIdentity.FromBounds(new VoxelWorldBounds(8, 8, 8), chunkSize: 8);
        using var harness = LocalBoxHarness.Create(identity);
        Assert.AreEqual(8, harness.Session.World.Bounds.SizeX);
        Assert.AreNotEqual(31, harness.Session.World.Bounds.SizeX - 1);

        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0, sizeX: 1e-20))));
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreEqual(1e-20, harness.Reloader.Catalog.Current!.Entries[Ledge].Definition.Collision.SizeX);

        var origin = Empty;
        var translated = new WorldVoxelCoord(1, 0, 0);
        var negative = new VoxelOrientation(VoxelAxis.NegativeX, VoxelAxis.PositiveY);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(origin, Ledge, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(translated, Ledge, negative).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(origin).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(translated).Status);
        AssertHit(harness.Commands, 0, 0.08, 0.48, 0.01, 0.12, 0.52, origin);
        AssertHit(harness.Commands, 1.99, 0.08, 0.48, 2, 0.12, 0.52, translated);
        AssertMiss(harness.Commands, 1.18, 0.08, 0.48, 1.22, 0.12, 0.52);

        var snapshot = harness.Reloader.Catalog.Current;
        var generation = snapshot.Generation;
        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0.5, sizeX: 1e-20))));
        var rejected = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(generation, rejected.CatalogVersion);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(origin).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(translated).Status);
        Assert.IsNotNull(VoxelCollision.CreateLocalBox(0, 0, 0.25, 1e-20, 0.875, 0.5));
    }

    [TestMethod]
    public void Representable_thin_boxes_keep_world_hits_when_underflow_reload_is_rejected()
    {
        using var harness = LocalBoxHarness.Create();
        harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0, sizeX: 1e-20))));
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreEqual(1e-20, harness.Reloader.Catalog.Current!.Entries[Ledge].Definition.Collision.SizeX);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(Empty, Ledge, VoxelOrientation.Yaw90).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Commands.Damage(Empty, 20).Status);
        Assert.AreEqual(VoxelOrientation.Yaw90, harness.Commands.Query(Empty).Voxel!.Orientation);
        Assert.AreEqual(80, harness.Commands.Query(Empty).Voxel!.CurrentDurability);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(Empty).Status);
        var snapshot = harness.Reloader.Catalog.Current;
        var generation = snapshot.Generation;
        foreach (var sizeX in new[] { 1e-50, double.Epsilon })
        {
            harness.Source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "说明", value: 10, tagsJson: "[]",
                    collisionJson: ContentFixtures.LocalBoxCollision(offsetX: 0, sizeX: sizeX))));
            var rejected = harness.Reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status, $"hot-reload size.x={sizeX:G17}");
            Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
            Assert.AreEqual(generation, rejected.CatalogVersion);
            Assert.AreEqual(1e-20, harness.Reloader.Catalog.Current!.Entries[Ledge].Definition.Collision.SizeX);
            Assert.AreEqual(VoxelOrientation.Yaw90, harness.Commands.Query(Empty).Voxel!.Orientation);
            Assert.AreEqual(80, harness.Commands.Query(Empty).Voxel!.CurrentDurability);
            Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(Empty).Status);
            Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(Empty, Iron, VoxelOrientation.Identity).Status);
        }

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

    // Independent expected geometry for offset (0.125,0,0.25) size (0.25,0.875,0.4).
    // World intervals come from the documented Right/Up/Forward axis map and 1-local
    // on a negative axis, not from ToWorldCell or OverlapsProbe.
    // Each Hit cube occupies a pocket that belongs to only that orientation's AABB, so
    // an axis swap or sign flip misses. Non-identity Miss cubes sit in the identity-only
    // pocket; identity Miss is the in-cell far corner outside the identity AABB.
    private static readonly Dictionary<string, (double HitMinX, double HitMinY, double HitMinZ, double HitMaxX, double HitMaxY, double HitMaxZ, double MissMinX, double MissMinY, double MissMinZ, double MissMaxX, double MissMaxY, double MissMaxZ)> ExpectedAsymmetric24 = new()
    {
        ["px_py"] = (0.13, 0.00, 0.25, 0.17, 0.04, 0.29, 0.68, 0.38, 0.48, 0.72, 0.42, 0.52),
        ["px_ny"] = (0.13, 0.88, 0.35, 0.17, 0.92, 0.39, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["px_pz"] = (0.13, 0.35, 0.00, 0.17, 0.39, 0.04, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["px_nz"] = (0.13, 0.25, 0.88, 0.17, 0.29, 0.92, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nx_py"] = (0.63, 0.00, 0.38, 0.67, 0.04, 0.42, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nx_ny"] = (0.63, 0.88, 0.38, 0.67, 0.92, 0.42, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nx_pz"] = (0.63, 0.38, 0.00, 0.67, 0.42, 0.04, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nx_nz"] = (0.63, 0.38, 0.88, 0.67, 0.42, 0.92, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["py_px"] = (0.00, 0.13, 0.35, 0.04, 0.17, 0.39, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["py_nx"] = (0.88, 0.13, 0.25, 0.92, 0.17, 0.29, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["py_pz"] = (0.25, 0.13, 0.00, 0.29, 0.17, 0.04, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["py_nz"] = (0.35, 0.13, 0.88, 0.39, 0.17, 0.92, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["ny_px"] = (0.00, 0.63, 0.38, 0.04, 0.67, 0.42, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["ny_nx"] = (0.88, 0.63, 0.38, 0.92, 0.67, 0.42, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["ny_pz"] = (0.35, 0.76, 0.00, 0.39, 0.80, 0.04, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["ny_nz"] = (0.25, 0.66, 0.88, 0.29, 0.70, 0.92, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["pz_px"] = (0.00, 0.25, 0.13, 0.04, 0.29, 0.17, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["pz_nx"] = (0.88, 0.35, 0.13, 0.92, 0.39, 0.17, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["pz_py"] = (0.35, 0.00, 0.13, 0.39, 0.04, 0.17, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["pz_ny"] = (0.25, 0.88, 0.13, 0.29, 0.92, 0.17, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nz_px"] = (0.00, 0.35, 0.76, 0.04, 0.39, 0.80, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nz_nx"] = (0.88, 0.25, 0.66, 0.92, 0.29, 0.70, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nz_py"] = (0.25, 0.00, 0.66, 0.29, 0.04, 0.70, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30),
        ["nz_ny"] = (0.35, 0.88, 0.76, 0.39, 0.92, 0.80, 0.13, 0.01, 0.26, 0.17, 0.05, 0.30)
    };

    private static void AssertHit(
        VoxelWorldCommands commands,
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ,
        WorldVoxelCoord? cell = null)
    {
        var result = commands.QueryCollision(minX, minY, minZ, maxX, maxY, maxZ);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, result.Status, $"{minX},{minY},{minZ}->{maxX},{maxY},{maxZ}");
        Assert.AreEqual(cell ?? Empty, result.Hits[0].Cell);
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

        public static LocalBoxHarness Create(VoxelWorldIdentity? identity = null)
        {
            var session = LoggerSession.Create();
            var source = ContentFixtures.ValidFerrousOre();
            source.AddVoxelFile("core", "voxels/hollow.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("hollow", "空心占格", "占格但不碰撞", tagsJson: "[]", collisionJson: "{\"kind\":\"none\"}")));
            source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "偏心局部盒", value: 10, tagsJson: "[]",
                    collisionJson: ContentFixtures.LocalBoxCollision())));
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, identity);
            reloader.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
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

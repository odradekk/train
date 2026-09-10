using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class CompositeDamageTests
{
    [TestMethod]
    public void Hollow_partial_damage_keeps_identity_and_does_not_advance_on_query()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(HollowShellFixture.DefinitionId);
        Assert.IsTrue(manufactured.Succeeded);
        var vehicleId = manufactured.VehicleId!.Value;
        var target = new LocalVoxelCoord(0, 1, 0);
        var partId = world.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == target).Id;
        var beforeCount = world.Session.TransactionCount;

        var damaged = world.Session.Damage(vehicleId, target, 25);
        Assert.IsTrue(damaged.Succeeded);
        Assert.AreEqual(CompositeDamageStatus.Damaged, damaged.Status);
        var query = world.Session.QueryComposite(vehicleId)!;
        Assert.AreEqual(168, query.Parts.Count);
        Assert.AreEqual(75, query.Parts.Single(part => part.Cell == target).CurrentDurability);
        Assert.AreEqual(partId, query.Parts.Single(part => part.Cell == target).Id);
        Assert.AreEqual(ConstructionStatus.Valid, query.Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, query.IdentityRetention);
        Assert.AreEqual(OperationalCondition.Unevaluated, query.OperationalCondition);
        Assert.AreEqual(0, world.Session.World.VoxelCount);
        Assert.AreEqual(beforeCount + 1, world.Session.TransactionCount);

        var beforeQuery = world.Session.CaptureAuthority();
        Assert.AreEqual(1, world.Session.QueryDamagedParts().Count);
        Assert.AreEqual(vehicleId, world.Session.QueryDamagedParts()[0].VehicleId);
        Assert.AreEqual(beforeQuery, world.Session.CaptureAuthority());
    }

    [TestMethod]
    public void Illegal_vehicle_damage_is_atomic_and_world_damage_stays_valid()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(HollowShellFixture.DefinitionId);
        var vehicleId = manufactured.VehicleId!.Value;
        world.Session.Add(new WorldVoxelCoord(2, 2, 2), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity);
        var before = world.Session.CaptureAuthority();

        Assert.AreEqual(CompositeDamageStatus.InvalidDamage, world.Session.Damage(vehicleId, new LocalVoxelCoord(0, 0, -1), 0).Status);
        Assert.AreEqual(before, world.Session.CaptureAuthority());
        Assert.AreEqual(CompositeDamageStatus.UnknownVehicle, world.Session.Damage(new EntityId(99), new LocalVoxelCoord(0, 0, -1), 25).Status);
        Assert.AreEqual(before, world.Session.CaptureAuthority());
        Assert.AreEqual(CompositeDamageStatus.Empty, world.Session.Damage(vehicleId, new LocalVoxelCoord(3, 3, 0), 25).Status);
        Assert.AreEqual(before, world.Session.CaptureAuthority());

        var worldDamage = world.Session.Damage(new WorldVoxelCoord(2, 2, 2), 25);
        Assert.IsTrue(worldDamage.Succeeded);
        Assert.AreEqual(75, world.Session.Query(new WorldVoxelCoord(2, 2, 2)).Voxel!.CurrentDurability);
        Assert.AreEqual(1, world.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Equal_value_bridge_break_uses_splitmix_and_does_not_mark_fragments_runnable()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(BridgeFixture.DefinitionId);
        Assert.AreEqual(55, manufactured.Parts!.Count);
        var originalId = manufactured.VehicleId!.Value;
        var leftCandidate = manufactured.Parts.Where(part => BridgeFixture.IsLeftCell(part.Cell)).Min(part => part.Id.Value);
        var rightCandidate = manufactured.Parts.Where(part => BridgeFixture.IsRightCell(part.Cell)).Min(part => part.Id.Value);
        Assert.AreEqual(2UL, leftCandidate);
        Assert.AreEqual(30UL, rightCandidate);
        Assert.AreEqual(0x99ca87e5117b95c4UL, SplitMix64.FractureScore(42, 2, 2));
        Assert.AreEqual(0x6ade052b213cd5a5UL, SplitMix64.FractureScore(42, 2, 30));

        var clock = Stopwatch.StartNew();
        var damaged = world.Session.Damage(originalId, BridgeFixture.BridgeCell, 100);
        clock.Stop();
        Console.WriteLine($"Bridge equal-value settlement {clock.Elapsed.TotalMilliseconds:0.000} ms status={damaged.Status}");
        Assert.IsTrue(damaged.Succeeded);

        var vehicles = world.Session.QueryComposites();
        Assert.AreEqual(2, vehicles.Count);
        Assert.AreEqual(originalId, vehicles[0].Id);
        Assert.AreEqual(27, vehicles[0].Parts.Count);
        Assert.IsTrue(vehicles[0].Parts.All(part => BridgeFixture.IsLeftCell(part.Cell)));
        Assert.AreEqual(ConstructionStatus.MissingContact, vehicles[0].Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, vehicles[0].IdentityRetention);
        Assert.AreEqual(OperationalCondition.Disabled, vehicles[0].OperationalCondition);
        Assert.AreEqual(1, vehicles[0].Contacts.Count);
        Assert.AreEqual(BridgeFixture.FrontBoundCell, vehicles[0].Contacts[0].BoundCell);

        Assert.AreEqual(new EntityId(57), vehicles[1].Id);
        Assert.AreEqual(27, vehicles[1].Parts.Count);
        Assert.IsTrue(vehicles[1].Parts.All(part => BridgeFixture.IsRightCell(part.Cell)));
        Assert.AreEqual(ConstructionStatus.MissingContact, vehicles[1].Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, vehicles[1].IdentityRetention);
        Assert.AreEqual(OperationalCondition.Disabled, vehicles[1].OperationalCondition);
        Assert.AreEqual(0, world.Session.World.VoxelCount);
        Assert.AreEqual(2UL, world.Session.TransactionCount);
        AssertNoSharedOwnership(world.Session);
    }

    [TestMethod]
    public void Heavier_right_core_inherits_the_original_vehicle_id()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(BridgeFixture.RightHeavyDefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        Assert.IsTrue(world.Session.Damage(originalId, BridgeFixture.BridgeCell, 100).Succeeded);
        var vehicles = world.Session.QueryComposites();
        Assert.AreEqual(2, vehicles.Count);
        Assert.AreEqual(originalId, vehicles[0].Id);
        Assert.IsTrue(vehicles[0].Parts.All(part => BridgeFixture.IsRightCell(part.Cell)));
        Assert.IsTrue(vehicles[1].Parts.All(part => BridgeFixture.IsLeftCell(part.Cell)));
        Assert.AreEqual(OperationalCondition.Disabled, vehicles[0].OperationalCondition);
        Assert.AreEqual(0, world.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Bridge_and_left_core_leave_only_the_right_composite()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(BridgeFixture.DefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        var leftIds = manufactured.Parts!.Where(part => BridgeFixture.IsLeftCell(part.Cell) && part.Cell != BridgeFixture.LeftCoreCell)
            .Select(part => part.Id.Value)
            .OrderBy(id => id)
            .ToArray();
        Assert.IsTrue(world.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);

        var vehicles = world.Session.QueryComposites();
        Assert.AreEqual(1, vehicles.Count);
        Assert.AreEqual(originalId, vehicles[0].Id);
        Assert.AreEqual(27, vehicles[0].Parts.Count);
        Assert.IsTrue(vehicles[0].Parts.All(part => BridgeFixture.IsRightCell(part.Cell)));
        Assert.AreEqual(26, world.Session.World.VoxelCount);
        foreach (var part in manufactured.Parts!.Where(part => BridgeFixture.IsLeftCell(part.Cell) && part.Cell != BridgeFixture.LeftCoreCell))
        {
            var worldCell = FragmentGridSnap.TransformCell(
                VoxelOrientation.Identity,
                part.Cell,
                new WorldVoxelCoord(8, 8, 8));
            var query = world.Session.Query(worldCell);
            Assert.IsTrue(query.IsOccupied, worldCell.ToString());
            Assert.AreEqual(part.DefinitionId, query.Voxel!.DefinitionId);
            Assert.AreEqual(100, query.Voxel.CurrentDurability);
        }

        CollectionAssert.DoesNotContain(
            world.Session.QueryComposites().SelectMany(item => item.Parts).Select(part => part.Id.Value).ToArray(),
            leftIds[0]);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, world.Session.QueryCollision(new WorldVoxelCoord(8, 8, 8)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, world.Session.QueryCollision(new WorldVoxelCoord(12, 8, 8)).Status);
        AssertNoSharedOwnership(world.Session);
    }

    [TestMethod]
    public void Destroying_both_cores_drops_fragments_without_a_surviving_composite()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(BridgeFixture.DefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        var damaged = world.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.RightCoreCell, 100)
        });
        Assert.AreEqual(CompositeDamageStatus.Removed, damaged.Status);
        Assert.AreEqual(0, world.Session.QueryComposites().Count);
        Assert.AreEqual(52, world.Session.World.VoxelCount);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, world.Session.QueryCollision(new WorldVoxelCoord(8, 8, 8)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, world.Session.QueryCollision(new WorldVoxelCoord(11, 9, 9)).Status);
        AssertNoSharedOwnership(world.Session);
    }

    [TestMethod]
    public void Reordered_multi_target_bridge_damage_matches_stable_authority()
    {
        using var first = Open();
        using var second = Open();
        var a = first.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var b = second.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(first.Session.Damage(new[]
        {
            new CompositeDamageTarget(a, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(a, new LocalVoxelCoord(0, 0, 0), 1)
        }).Succeeded);
        Assert.IsTrue(second.Session.Damage(new[]
        {
            new CompositeDamageTarget(b, new LocalVoxelCoord(0, 0, 0), 1),
            new CompositeDamageTarget(b, BridgeFixture.BridgeCell, 100)
        }).Succeeded);
        Assert.AreEqual(first.Session.CaptureAuthority(), second.Session.CaptureAuthority());
        Assert.AreEqual(99, first.Session.QueryComposites()[0].Parts.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0))).CurrentDurability);
    }

    [TestMethod]
    public void WorldSeed_42_replays_complete_authoritative_state()
    {
        using var first = Open();
        using var second = Open();
        RunBridgeSequence(first.Session);
        RunBridgeSequence(second.Session);
        Assert.AreEqual(42UL, first.Session.Identity.Seed);
        Assert.AreEqual(first.Session.CaptureAuthority(), second.Session.CaptureAuthority());
        Assert.AreEqual(first.Session.NextEntityId, second.Session.NextEntityId);
        Assert.AreEqual(first.Session.TransactionCount, second.Session.TransactionCount);
    }

    [TestMethod]
    public void Fragment_drop_covers_neighbor_search_and_all_or_nothing_destruction()
    {
        using var neighbor = Open();
        var manufactured = neighbor.Session.Manufacture(BridgeFixture.DefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        Assert.AreEqual(
            WorldCommandStatus.Added,
            neighbor.Session.Add(new WorldVoxelCoord(8, 8, 8), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Status);

        Assert.IsTrue(neighbor.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(1, neighbor.Session.QueryComposites().Count);
        Assert.AreEqual(new VoxelDefinitionId("core", "iron-ore"), neighbor.Session.Query(new WorldVoxelCoord(8, 8, 8)).Voxel!.DefinitionId);
        var shifted = new WorldVoxelCoord(8, 8, 9);
        Assert.IsTrue(neighbor.Session.Query(shifted).IsOccupied);
        Assert.AreEqual(new VoxelDefinitionId("core", "vehicle-shell"), neighbor.Session.Query(shifted).Voxel!.DefinitionId);
        AssertNoSharedOwnership(neighbor.Session);

        using var blocked = Open();
        var blockedId = blocked.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var leftRemainder = blocked.Session.QueryComposite(blockedId)!.Parts
            .Where(part => BridgeFixture.IsLeftCell(part.Cell) && part.Cell != BridgeFixture.LeftCoreCell)
            .Select(part => part.Cell)
            .ToArray();
        foreach (var offset in FragmentGridSnap.SearchOffsets)
        {
            foreach (var cell in leftRemainder)
            {
                var worldCell = FragmentGridSnap.TransformCell(
                    VoxelOrientation.Identity,
                    cell,
                    new WorldVoxelCoord(8 + offset.X, 8 + offset.Y, 8 + offset.Z));
                if (!blocked.Session.World.Bounds.Contains(worldCell) || blocked.Session.Query(worldCell).IsOccupied)
                    continue;
                blocked.Session.Add(worldCell, new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity);
            }
        }

        var occupiedBefore = blocked.Session.World.VoxelCount;
        Assert.IsTrue(blocked.Session.Damage(new[]
        {
            new CompositeDamageTarget(blockedId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(blockedId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(1, blocked.Session.QueryComposites().Count);
        Assert.AreEqual(occupiedBefore, blocked.Session.World.VoxelCount);
        AssertNoSharedOwnership(blocked.Session);
    }

    [TestMethod]
    public void Overlapping_parked_vehicle_blocks_fragment_ownership_transfer()
    {
        using var world = Open();
        var first = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var second = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(world.Session.Damage(new[]
        {
            new CompositeDamageTarget(first, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(first, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(first, BridgeFixture.RightCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(1, world.Session.QueryComposites().Count);
        Assert.AreEqual(second, world.Session.QueryComposites()[0].Id);
        Assert.AreEqual(0, world.Session.World.VoxelCount);
        AssertNoSharedOwnership(world.Session);
    }

    [TestMethod]
    public void Half_cell_anchor_snaps_the_whole_fragment()
    {
        using var world = Open();
        Assert.IsTrue(CompositeTransform.TryCreate(8.5, 8, 8, VoxelOrientation.Identity, out var pose));
        var manufactured = world.Session.Manufacture(BridgeFixture.DefinitionId, pose);
        var originalId = manufactured.VehicleId!.Value;
        Assert.IsTrue(world.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.RightCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(0, world.Session.QueryComposites().Count);
        Assert.IsTrue(world.Session.Query(new WorldVoxelCoord(9, 8, 8)).IsOccupied);
        Assert.IsFalse(world.Session.Query(new WorldVoxelCoord(8, 8, 8)).IsOccupied);
    }

    private static void RunBridgeSequence(VoxelDevelopmentSession session)
    {
        var first = session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        session.Damage(first, BridgeFixture.BridgeCell, 100);
        session.Reset();
        var second = session.Manufacture(BridgeFixture.RightHeavyDefinitionId).VehicleId!.Value;
        session.Damage(new[]
        {
            new CompositeDamageTarget(second, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(second, BridgeFixture.LeftCoreCell, 100)
        });
    }

    private static void AssertNoSharedOwnership(VoxelDevelopmentSession session)
    {
        var partIds = session.QueryComposites().SelectMany(item => item.Parts).Select(part => part.Id.Value).ToArray();
        Assert.AreEqual(partIds.Length, partIds.Distinct().Count());
        foreach (var vehicle in session.QueryComposites())
        {
            foreach (var part in vehicle.Parts)
            {
                var worldCell = FragmentGridSnap.TransformCell(
                    vehicle.Transform.Orientation,
                    part.Cell,
                    FragmentGridSnap.SnapAnchor(vehicle.Transform.OriginX, vehicle.Transform.OriginY, vehicle.Transform.OriginZ));
                Assert.IsFalse(session.Query(worldCell).IsOccupied, $"vehicle {vehicle.Id} part {part.Cell} also occupies {worldCell}");
            }
        }
    }

    private static SessionHandle Open()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        return new SessionHandle(
            new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites),
            logger);
    }

    private sealed class SessionHandle : IDisposable
    {
        public VoxelDevelopmentSession Session { get; }
        private readonly IDisposable _logger;

        public SessionHandle(VoxelDevelopmentSession session, IDisposable logger)
        {
            Session = session;
            _logger = logger;
        }

        public void Dispose()
        {
            Session.Detach();
            _logger.Dispose();
        }
    }
}

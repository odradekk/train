using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:composites")]
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
        world.Session.Add(new WorldVoxelCoord(2, 2, 2), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity);
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
            neighbor.Session.Add(new WorldVoxelCoord(8, 8, 8), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);

        Assert.IsTrue(neighbor.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(1, neighbor.Session.QueryComposites().Count);
        Assert.AreEqual(new VoxelDefinitionId("core", "ferrous-ore"), neighbor.Session.Query(new WorldVoxelCoord(8, 8, 8)).Voxel!.DefinitionId);
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
                blocked.Session.Add(worldCell, new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity);
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
    public void Changed_observers_never_see_partial_fragment_transfer()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(BridgeFixture.DefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        var notifications = 0;
        var dual = false;
        world.Session.Changed += () =>
        {
            notifications++;
            if (HasDualOwnership(world.Session))
                dual = true;
            var original = world.Session.QueryComposite(originalId);
            if (original is { Parts.Count: 55 } && world.Session.World.VoxelCount > 0)
                dual = true;
        };

        Assert.IsTrue(world.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(1, notifications);
        Assert.IsFalse(dual);
        Assert.AreEqual(1, world.Session.QueryComposites().Count);
        Assert.AreEqual(26, world.Session.World.VoxelCount);
        AssertNoSharedOwnership(world.Session);
    }

    [TestMethod]
    public void Dropped_parts_keep_runtime_id_and_state()
    {
        using var world = Open();
        var manufactured = world.Session.Manufacture(BridgeFixture.DefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        var leftCorner = manufactured.Parts!.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0)));
        Assert.IsTrue(world.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);

        var dropped = world.Session.Query(new WorldVoxelCoord(8, 8, 8));
        Assert.AreEqual(leftCorner.Id, dropped.Voxel!.RuntimeId);
        Assert.AreEqual(leftCorner.DefinitionId, dropped.Voxel.DefinitionId);
        Assert.AreEqual(100, dropped.Voxel.CurrentDurability);
        Assert.AreEqual(leftCorner.Orientation, dropped.Voxel.Orientation);
        Assert.IsFalse(world.Session.QueryComposites().SelectMany(item => item.Parts).Any(part => part.Id == leftCorner.Id));
        Assert.AreEqual(
            WorldCommandStatus.Added,
            world.Session.Add(new WorldVoxelCoord(1, 1, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);
        Assert.IsNull(world.Session.Query(new WorldVoxelCoord(1, 1, 1)).Voxel!.RuntimeId);
        AssertNoSharedOwnership(world.Session);
    }

    [TestMethod]
    public void Continuous_pose_manufacture_snaps_fragments_on_the_formal_path()
    {
        using var tie = Open();
        Assert.IsTrue(CompositeTransform.TryCreate(8, 8, 8, 1, 0, 1, 0, 1, 0, out var fortyFive));
        var tied = tie.Session.Manufacture(BridgeFixture.DefinitionId, fortyFive);
        var tiedQuery = tie.Session.QueryComposite(tied.VehicleId!.Value)!;
        Assert.AreEqual(Math.Sqrt(0.5), tiedQuery.Transform.RightX, 1e-12);
        Assert.AreEqual(0, tiedQuery.Transform.RightY, 1e-12);
        Assert.AreEqual(Math.Sqrt(0.5), tiedQuery.Transform.RightZ, 1e-12);
        Assert.AreEqual(VoxelOrientation.Identity, tiedQuery.Transform.Orientation);
        Assert.IsTrue(tie.Session.Damage(new[]
        {
            new CompositeDamageTarget(tied.VehicleId.Value, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(tied.VehicleId.Value, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(tied.VehicleId.Value, BridgeFixture.RightCoreCell, 100)
        }).Succeeded);
        Assert.AreEqual(52, tie.Session.World.VoxelCount);
        Assert.IsTrue(tie.Session.Query(new WorldVoxelCoord(8, 8, 8)).IsOccupied);
        Assert.AreEqual(VoxelOrientation.Identity, tie.Session.Query(new WorldVoxelCoord(8, 8, 8)).Voxel!.Orientation);
        Assert.IsTrue(tie.Session.Query(new WorldVoxelCoord(10, 10, 10)).IsOccupied);

        using var yaw = Open();
        Assert.IsTrue(CompositeTransform.TryCreate(8, 8, 8, 0.4, 0, 1, 0, 1, 0, out var towardYaw));
        var rotated = yaw.Session.Manufacture(BridgeFixture.DefinitionId, towardYaw);
        Assert.AreEqual(VoxelOrientation.Yaw90, yaw.Session.QueryComposite(rotated.VehicleId!.Value)!.Transform.Orientation);
        Assert.IsTrue(yaw.Session.Damage(new[]
        {
            new CompositeDamageTarget(rotated.VehicleId.Value, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(rotated.VehicleId.Value, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(rotated.VehicleId.Value, BridgeFixture.RightCoreCell, 100)
        }).Succeeded);
        var droppedCorner = yaw.Session.Query(new WorldVoxelCoord(7, 8, 8));
        Assert.IsTrue(droppedCorner.IsOccupied);
        Assert.AreEqual(VoxelOrientation.Yaw90, droppedCorner.Voxel!.Orientation);
        Assert.AreEqual(rotated.Parts!.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0))).Id, droppedCorner.Voxel.RuntimeId);
        Assert.IsTrue(yaw.Session.Query(new WorldVoxelCoord(7, 8, 10)).IsOccupied);
        Assert.AreEqual(52, yaw.Session.World.VoxelCount);
        AssertNoSharedOwnership(yaw.Session);
    }

    [TestMethod]
    public void Damage_results_follow_surviving_parts_and_ignore_unrelated_vehicles()
    {
        using var heavy = Open();
        var manufactured = heavy.Session.Manufacture(BridgeFixture.RightHeavyDefinitionId);
        var originalId = manufactured.VehicleId!.Value;
        var leftCorner = manufactured.Parts!.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0)));
        var damaged = heavy.Session.Damage(new[]
        {
            new CompositeDamageTarget(originalId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(originalId, new LocalVoxelCoord(0, 0, 0), 1)
        });
        Assert.AreEqual(CompositeDamageStatus.Damaged, damaged.Status);
        Assert.AreEqual(CompositeDamageStatus.Removed, damaged.Targets![0].Status);
        Assert.AreEqual(CompositeDamageStatus.Damaged, damaged.Targets[1].Status);
        Assert.AreEqual(99, damaged.Targets[1].RemainingDurability);
        Assert.AreEqual(new EntityId(57), damaged.Targets[1].VehicleId);
        Assert.AreEqual(leftCorner.Id, damaged.Targets[1].PartId);
        Assert.AreEqual(99, heavy.Session.QueryComposite(new EntityId(57))!.Parts.Single(part => part.Id == leftCorner.Id).CurrentDurability);

        using var world = Open();
        var first = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var second = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var corner = world.Session.QueryComposite(first)!.Parts.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0)));
        var destroyed = world.Session.Damage(new[]
        {
            new CompositeDamageTarget(first, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(first, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(first, BridgeFixture.RightCoreCell, 100),
            new CompositeDamageTarget(first, new LocalVoxelCoord(0, 0, 0), 1)
        });
        Assert.AreEqual(CompositeDamageStatus.Removed, destroyed.Status);
        Assert.IsNull(world.Session.QueryComposite(first));
        Assert.AreEqual(second, world.Session.QueryComposites().Single().Id);
        Assert.AreEqual(0, world.Session.World.VoxelCount);
        Assert.AreEqual(CompositeDamageStatus.Removed, destroyed.Targets!.Single(item => item.Cell.Equals(new LocalVoxelCoord(0, 0, 0))).Status);

        using var dropped = Open();
        var dropId = dropped.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        var dropCorner = dropped.Session.QueryComposite(dropId)!.Parts.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0)));
        var partial = dropped.Session.Damage(new[]
        {
            new CompositeDamageTarget(dropId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(dropId, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(dropId, new LocalVoxelCoord(0, 0, 0), 1)
        });
        Assert.AreEqual(CompositeDamageStatus.Damaged, partial.Status);
        var worldTarget = partial.Targets!.Single(item => item.Cell.Equals(new LocalVoxelCoord(0, 0, 0)));
        Assert.AreEqual(CompositeDamageStatus.Damaged, worldTarget.Status);
        Assert.IsNull(worldTarget.VehicleId);
        Assert.AreEqual(dropCorner.Id, worldTarget.PartId);
        Assert.AreEqual(99, worldTarget.RemainingDurability);
        Assert.AreEqual(new WorldVoxelCoord(8, 8, 8), worldTarget.WorldCell);
        Assert.AreEqual(dropCorner.Id, dropped.Session.Query(new WorldVoxelCoord(8, 8, 8)).Voxel!.RuntimeId);
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
        Assert.IsFalse(HasDualOwnership(session));
    }

    private static bool HasDualOwnership(VoxelDevelopmentSession session)
    {
        var vehicleIds = session.QueryComposites().SelectMany(item => item.Parts).Select(part => part.Id.Value).ToArray();
        if (vehicleIds.Length != vehicleIds.Distinct().Count())
            return true;
        var worldIds = session.World.OccupiedVoxels()
            .Select(item => item.Value.RuntimeId?.Value)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
        if (worldIds.Length != worldIds.Distinct().Count())
            return true;
        if (vehicleIds.Intersect(worldIds).Any())
            return true;
        foreach (var vehicle in session.QueryComposites())
        {
            foreach (var part in vehicle.Parts)
            {
                var worldCell = FragmentGridSnap.TransformCell(
                    vehicle.Transform.Orientation,
                    part.Cell,
                    FragmentGridSnap.SnapAnchor(vehicle.Transform.OriginX, vehicle.Transform.OriginY, vehicle.Transform.OriginZ));
                if (session.Query(worldCell).IsOccupied)
                    return true;
            }
        }

        return false;
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

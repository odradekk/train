using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class PhysicalRailTests
{
    private static readonly WorldVoxelCoord DamageCell = StraightRailFixture.FirstSegmentDamageCell;
    private static readonly RailInterval DamagedInterval = new(110, 111);

    [TestMethod]
    public void Fixture_plan_commits_geometry_occupancy_and_explicit_connection()
    {
        using var harness = RailHarness.Create();
        var before = harness.Session.CaptureRailAuthority();
        var constructed = harness.Session.ConstructRails();
        Assert.IsTrue(constructed.Succeeded);
        CollectionAssert.AreEqual(new[] { new EntityId(1), new EntityId(2) }, constructed.SegmentIds!.ToArray());
        Assert.AreEqual(3UL, harness.Session.NextEntityId);
        Assert.AreEqual(1UL, harness.Session.TransactionCount);
        Assert.AreEqual(1032, harness.Session.World.VoxelCount);
        Assert.AreEqual(1032, harness.Session.Rails.OccupiedCellCount);

        var query = harness.Session.QueryRails();
        Assert.AreEqual(2, query.Segments.Count);
        Assert.AreEqual(1, query.Connections.Count);
        Assert.AreEqual(257, query.SleeperCount);
        Assert.AreEqual(StraightRailFixture.FirstAlignment, query.Segments[0].Alignment);
        Assert.AreEqual(StraightRailFixture.SecondAlignment, query.Segments[1].Alignment);
        Assert.IsTrue(harness.Session.Rails.AreConnected(new EntityId(1), new EntityId(2)));
        AssertOccupies(harness, StraightRailFixture.EdgeCell);
        AssertOccupies(harness, StraightRailFixture.LeftRailSample);
        AssertOccupies(harness, StraightRailFixture.RightRailSample);
        AssertOccupies(harness, new WorldVoxelCoord(15, 1, 14));
        AssertOccupies(harness, new WorldVoxelCoord(16, 1, 15));
        AssertOccupies(harness, new WorldVoxelCoord(143, 1, 14));
        AssertOccupies(harness, new WorldVoxelCoord(144, 1, 14));
        AssertOccupies(harness, new WorldVoxelCoord(271, 1, 16));
        Assert.AreEqual(100, harness.Session.Query(StraightRailFixture.LeftRailSample).Voxel!.CurrentDurability);
        Assert.AreEqual(VoxelChunks.Of(new WorldVoxelCoord(15, 1, 15), 16), new ChunkCoord(0, 0, 0));
        Assert.AreEqual(VoxelChunks.Of(new WorldVoxelCoord(16, 1, 15), 16), new ChunkCoord(1, 0, 0));
        Assert.AreEqual(VoxelChunks.Of(new WorldVoxelCoord(144, 1, 14), 16), new ChunkCoord(9, 0, 0));
        var seamSupports = SeamSupports(harness, StraightRailFixture.SharedSeamCell);
        Assert.IsTrue(seamSupports.Select(item => item.SegmentId).Distinct().Count() >= 2, string.Join(';', seamSupports.Select(item => $"{item.SegmentId.Value}:{item.WorldX.Start}..{item.WorldX.End}")));
        Assert.AreEqual(before.WorldVoxelCount, 0);
        AssertPositiveVolumeBoundary();
    }

    [TestMethod]
    public void Adjacent_unconnected_segments_do_not_join_automatically()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(StraightRailFixture.CreateUnconnectedFixturePlan()).Succeeded);
        Assert.IsFalse(harness.Session.Rails.AreConnected(new EntityId(1), new EntityId(2)));
        Assert.AreEqual(0, harness.Session.QueryRails().Connections.Count);
    }

    [TestMethod]
    public void Independent_segments_in_the_same_plan_cannot_share_occupancy()
    {
        using var harness = RailHarness.Create();
        var fixture = StraightRailFixture.CreateFixturePlan();
        var overlapping = new RailConstructionPlan(
            new[] { fixture.Segments[0], fixture.Segments[0] },
            Array.Empty<PlannedRailConnection>());
        AssertRejected(harness, overlapping, RailEditStatus.IndependentLineConflict, voxels: 0);
        Assert.AreEqual(0, harness.Session.QueryRails().Segments.Count);
        Assert.AreEqual(0, harness.Session.QueryRails().Connections.Count);
    }

    [TestMethod]
    public void Destroyed_registered_cells_still_block_independent_occupancy()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var ids = harness.Session.Rails.Segments.Select(segment => segment.Id).ToArray();
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(DamageCell, 100).Status);
        AssertRejected(
            harness,
            new RailConstructionPlan(
                new[]
                {
                    new PlannedRailSegment(
                        new HorizontalStraightAlignment(110.1, 2, 16, 110.9, 2, 16),
                        new[] { new RailVolumeBox(110.1, 1.1, 15.1, 110.9, 1.9, 15.9) },
                        Array.Empty<RailVolumeBox>())
                },
                Array.Empty<PlannedRailConnection>()),
            RailEditStatus.IndependentLineConflict,
            voxels: 1031);
        CollectionAssert.AreEqual(ids, harness.Session.Rails.Segments.Select(segment => segment.Id).ToArray());
        Assert.IsTrue(harness.Session.Rails.Occupies(DamageCell));
        Assert.IsFalse(harness.Session.Query(DamageCell).IsOccupied);
    }

    [TestMethod]
    public void Noncoincident_or_wrong_end_connections_are_rejected()
    {
        using var harness = RailHarness.Create();
        var fixture = StraightRailFixture.CreateFixturePlan();
        AssertRejected(
            harness,
            new RailConstructionPlan(
                fixture.Segments,
                new[] { new PlannedRailConnection(0, RailEnd.Start, 1, RailEnd.End) }),
            RailEditStatus.InvalidPlan,
            voxels: 0);
        AssertRejected(
            harness,
            new RailConstructionPlan(
                fixture.Segments,
                new[] { new PlannedRailConnection(0, RailEnd.End, 1, RailEnd.End) }),
            RailEditStatus.InvalidPlan,
            voxels: 0);
        AssertRejected(
            harness,
            new RailConstructionPlan(
                fixture.Segments,
                new[] { new PlannedRailConnection(0, RailEnd.End, 1, (RailEnd)42) }),
            RailEditStatus.InvalidPlan,
            voxels: 0);

        Assert.IsTrue(harness.Session.ConstructRails(fixture).Succeeded);
        Assert.IsTrue(harness.Session.Rails.AreConnected(new EntityId(1), new EntityId(2)));
    }

    [TestMethod]
    public void Clearance_follows_the_planned_alignment()
    {
        using var harness = RailHarness.Create();
        var shifted = ShiftZ(StraightRailFixture.CreateFixturePlan(), 8);
        Assert.IsTrue(harness.Session.Add(
            new WorldVoxelCoord(80, 3, 24),
            new VoxelDefinitionId("core", "iron-ore"),
            VoxelOrientation.Identity).Succeeded);
        AssertRejected(harness, shifted, RailEditStatus.ClearanceConflict, voxels: 1);

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.Add(
            new WorldVoxelCoord(80, 3, 16),
            new VoxelDefinitionId("core", "iron-ore"),
            VoxelOrientation.Identity).Succeeded);
        Assert.IsTrue(harness.Session.ConstructRails(shifted).Succeeded);
        Assert.IsFalse(harness.Session.Rails.Occupies(new WorldVoxelCoord(80, 1, 15)));
        Assert.IsTrue(harness.Session.Rails.Occupies(new WorldVoxelCoord(80, 1, 23)));

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.Add(
            new WorldVoxelCoord(80, 3, 24),
            new VoxelDefinitionId("core", "iron-ore"),
            VoxelOrientation.Identity).Succeeded);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
    }

    [TestMethod]
    public void Committed_geometry_is_isolated_from_caller_mutations()
    {
        using var harness = RailHarness.Create();
        var rails = StraightRailFixture.CreateFixturePlan().Segments[0].RailVolumes.ToList();
        var plan = new RailConstructionPlan(
            new[]
            {
                StraightRailFixture.CreateFixturePlan().Segments[0] with { RailVolumes = rails }
            },
            Array.Empty<PlannedRailConnection>());
        Assert.IsTrue(harness.Session.ConstructRails(plan).Succeeded);
        var committedCount = harness.Session.Rails.Segments[0].RailVolumes.Count;
        Assert.IsTrue(committedCount > 0);
        rails.Clear();
        Assert.AreEqual(committedCount, harness.Session.Rails.Segments[0].RailVolumes.Count);
        Assert.AreEqual(520, harness.Session.World.VoxelCount);
        Assert.AreEqual(1UL, harness.Session.TransactionCount);

        var query = harness.Session.QueryRails();
        if (query.Occupancy[0].Supports is RailCellSupport[] supportArray)
            Array.Clear(supportArray);
        else if (query.Occupancy[0].Supports is IList<RailCellSupport> supports && !supports.IsReadOnly)
            supports.Clear();
        Assert.IsTrue(harness.Session.Rails.TryGetOccupancy(new WorldVoxelCoord(16, 1, 15), out var occupancy));
        Assert.IsTrue(occupancy.Supports.Count > 0);
    }

    [TestMethod]
    public void Second_nonoverlapping_plan_merges_existing_authority()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var firstIds = harness.Session.Rails.Segments.Select(segment => segment.Id).ToArray();
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(DamageCell, 100).Status);
        var damaged = harness.Session.Rails.TraversableWorldX(firstIds[0], harness.Session.World).ToArray();

        var second = harness.Session.ConstructRails(ShiftZ(StraightRailFixture.CreateFixturePlan(), 8));
        Assert.IsTrue(second.Succeeded);
        Assert.AreEqual(2063, harness.Session.World.VoxelCount);
        Assert.AreEqual(2063, harness.Session.Rails.OccupiedCellCount);
        Assert.AreEqual(4, harness.Session.Rails.Segments.Count);
        Assert.IsTrue(firstIds.All(id => harness.Session.Rails.Segments.Any(segment => segment.Id == id)));
        Assert.IsTrue(harness.Session.Query(new WorldVoxelCoord(80, 1, 15)).IsOccupied);
        Assert.IsTrue(harness.Session.Rails.Occupies(new WorldVoxelCoord(80, 1, 15)));
        Assert.IsTrue(harness.Session.Rails.Occupies(new WorldVoxelCoord(80, 1, 23)));
        Assert.IsFalse(harness.Session.Query(DamageCell).IsOccupied);
        CollectionAssert.AreEqual(
            damaged,
            harness.Session.Rails.TraversableWorldX(firstIds[0], harness.Session.World).ToArray());
        Assert.IsTrue(harness.Session.Rails.AreConnected(firstIds[0], firstIds[1]));
        Assert.IsTrue(harness.Session.Rails.AreConnected(new EntityId(3), new EntityId(4)));
        Assert.IsFalse(harness.Session.Rails.AreConnected(firstIds[0], new EntityId(3)));

        var repaired = harness.Session.RepairRails(new[] { DamageCell });
        Assert.IsTrue(repaired.Succeeded);
        CollectionAssert.AreEqual(firstIds, harness.Session.Rails.Segments.Take(2).Select(segment => segment.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { new RailInterval(16, 144) },
            harness.Session.Rails.TraversableWorldX(firstIds[0], harness.Session.World).ToArray());
        Assert.AreEqual(100, harness.Session.Query(DamageCell).Voxel!.CurrentDurability);
        Assert.AreEqual(2064, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Edge_cell_damage_clips_only_the_destroyed_volume()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var edge = new WorldVoxelCoord(110, 1, 14);
        var intact = new WorldVoxelCoord(110, 1, 17);
        var leftRail = new WorldVoxelCoord(110, 1, 15);
        var rightRail = new WorldVoxelCoord(110, 1, 16);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(edge, 100).Status);
        Assert.IsTrue(harness.Session.Query(intact).IsOccupied);
        Assert.IsTrue(harness.Session.Query(leftRail).IsOccupied);
        Assert.IsTrue(harness.Session.Query(rightRail).IsOccupied);

        var render = harness.Session.BuildTrackRenderModel();
        Assert.IsTrue(render.HasGapNear(edge));
        Assert.IsTrue(
            render.Sections.SelectMany(section => section.Solids).Any(solid =>
                solid.Kind == TrackSolidKind.Sleeper && solid.Volume.OverlapsCell(intact)),
            "The surviving sleeper half must remain visible.");
        Assert.IsTrue(
            render.Sections.SelectMany(section => section.Solids).Any(solid =>
                solid.Kind == TrackSolidKind.Rail && solid.Volume.OverlapsCell(leftRail)));
        Assert.IsTrue(
            render.Sections.SelectMany(section => section.Solids).Any(solid =>
                solid.Kind == TrackSolidKind.Rail && solid.Volume.OverlapsCell(rightRail)));

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(leftRail, 100).Status);
        var afterRail = harness.Session.BuildTrackRenderModel();
        Assert.IsTrue(afterRail.HasGapNear(leftRail));
        Assert.IsTrue(
            afterRail.Sections.SelectMany(section => section.Solids).Any(solid =>
                solid.Kind == TrackSolidKind.Rail && solid.Volume.OverlapsCell(rightRail)));
        Assert.IsTrue(
            afterRail.Sections.SelectMany(section => section.Solids).Any(solid =>
                solid.Kind == TrackSolidKind.Sleeper && solid.Volume.OverlapsCell(intact)));
    }

    [TestMethod]
    public void Local_refresh_keeps_unrelated_sections()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        harness.Session.TakePendingTrackChunks();
        var chunkSize = harness.Session.World.ChunkSize;
        var initial = TrackRenderModel.From(harness.Session.Rails, harness.Session.World, chunkSize);
        var farChunk = VoxelChunks.Of(StraightRailFixture.ChunkSeamCell, chunkSize);
        var edge = new WorldVoxelCoord(110, 1, 14);
        var zNeighbor = VoxelChunks.Of(new WorldVoxelCoord(110, 1, 17), chunkSize);
        var seam = StraightRailFixture.SharedSeamCell;
        var farSection = initial.Sections.Single(section => section.Chunk.Equals(farChunk));
        var zSection = initial.Sections.Single(section => section.Chunk.Equals(zNeighbor));

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(edge, 100).Status);
        var dirty = harness.Session.TakePendingTrackChunks();
        Assert.IsTrue(dirty.Contains(VoxelChunks.Of(edge, chunkSize)));
        Assert.IsFalse(dirty.Contains(farChunk));
        Assert.IsFalse(dirty.Contains(zNeighbor));
        Assert.IsTrue(dirty.Count < VoxelChunks.All(harness.Session.Identity).Count);

        var refreshed = initial.RefreshLocal(harness.Session.Rails, harness.Session.World, chunkSize, dirty);
        Assert.AreSame(farSection, refreshed.Model.Sections.Single(section => section.Chunk.Equals(farChunk)));
        Assert.AreSame(zSection, refreshed.Model.Sections.Single(section => section.Chunk.Equals(zNeighbor)));
        CollectionAssert.DoesNotContain(refreshed.RebuiltChunks.ToArray(), farChunk);
        CollectionAssert.DoesNotContain(refreshed.RetainedChunks.ToArray(), VoxelChunks.Of(edge, chunkSize));
        Assert.IsTrue(
            refreshed.Model.Sections.SelectMany(section => section.Solids).Any(solid =>
                solid.Kind == TrackSolidKind.Sleeper &&
                solid.Volume.OverlapsCell(new WorldVoxelCoord(110, 1, 17))));

        Assert.IsTrue(harness.Session.RepairRails(new[] { edge }).Succeeded);
        var afterRepair = harness.Session.TakePendingTrackChunks();
        Assert.IsTrue(afterRepair.Contains(VoxelChunks.Of(edge, chunkSize)));
        Assert.IsFalse(afterRepair.Contains(farChunk));

        var seamModel = TrackRenderModel.From(harness.Session.Rails, harness.Session.World, chunkSize);
        var beforeSeamFar = seamModel.Sections.Single(section => section.Chunk.Equals(farChunk));
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(seam, 100).Status);
        var seamDirty = harness.Session.TakePendingTrackChunks();
        Assert.IsTrue(seamDirty.Contains(VoxelChunks.Of(seam, chunkSize)));
        Assert.IsFalse(seamDirty.Contains(farChunk));
        var afterSeam = seamModel.RefreshLocal(harness.Session.Rails, harness.Session.World, chunkSize, seamDirty);
        Assert.AreSame(beforeSeamFar, afterSeam.Model.Sections.Single(section => section.Chunk.Equals(farChunk)));
    }

    [TestMethod]
    public void Illegal_plans_fail_atomically()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.Add(new WorldVoxelCoord(80, 1, 15), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Succeeded);
        AssertRejected(harness, StraightRailFixture.CreateFixturePlan(), RailEditStatus.WorldObstacle, voxels: 1);

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.Add(new WorldVoxelCoord(80, 1, 13), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Succeeded);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded, "y=1 beside the sleeper is occupancy, not swept clearance.");
        harness.Session.Reset();
        Assert.IsTrue(harness.Session.Add(new WorldVoxelCoord(80, 8, 16), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Succeeded);
        AssertRejected(harness, StraightRailFixture.CreateFixturePlan(), RailEditStatus.ClearanceConflict, voxels: 1);
        harness.Session.Reset();
        Assert.IsTrue(harness.Session.Add(new WorldVoxelCoord(80, 3, 16), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Succeeded);
        AssertRejected(harness, StraightRailFixture.CreateFixturePlan(), RailEditStatus.ClearanceConflict, voxels: 1);

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        AssertRejected(harness, StraightRailFixture.CreateIndependentOverlapPlan(), RailEditStatus.IndependentLineConflict);
        AssertRejected(harness, StraightRailFixture.CreateFixturePlan(), RailEditStatus.IndependentLineConflict);

        harness.Session.Reset();
        AssertRejected(
            harness,
            new RailConstructionPlan(Array.Empty<PlannedRailSegment>(), Array.Empty<PlannedRailConnection>()),
            RailEditStatus.InvalidPlan,
            voxels: 0);
    }

    [TestMethod]
    public void Exhausted_ids_or_transactions_do_not_consume_or_write()
    {
        using var harness = RailHarness.Create();
        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), ulong.MaxValue);
        AssertRejected(harness, StraightRailFixture.CreateFixturePlan(), RailEditStatus.EntityIdExhausted, voxels: 0);
        Assert.AreEqual(ulong.MaxValue, harness.Session.NextEntityId);
        Assert.AreEqual(0UL, harness.Session.TransactionCount);

        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), 1);
        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.TransactionCount), ulong.MaxValue - 1);
        AssertRejected(harness, StraightRailFixture.CreateFixturePlan(), RailEditStatus.TransactionExhausted, voxels: 0);
        Assert.AreEqual(1UL, harness.Session.NextEntityId);
        Assert.AreEqual(ulong.MaxValue - 1, harness.Session.TransactionCount);
    }

    [TestMethod]
    public void Damage_opens_supported_intervals_without_moving_geometry()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var firstId = harness.Session.Rails.Segments[0].Id;
        var alignment = harness.Session.Rails.Segments[0].Alignment;
        var destroyed = harness.Session.Damage(DamageCell, 100);
        Assert.AreEqual(WorldCommandStatus.Removed, destroyed.Status);
        Assert.IsFalse(harness.Session.Query(DamageCell).IsOccupied);
        Assert.AreEqual(alignment, harness.Session.Rails.Segments[0].Alignment);
        Assert.AreEqual(firstId, harness.Session.Rails.Segments[0].Id);
        CollectionAssert.AreEqual(
            new[] { new RailInterval(16, 110), new RailInterval(111, 144) },
            harness.Session.Rails.TraversableWorldX(firstId, harness.Session.World).ToArray());

        var stale = harness.Session.QueryDerivedRailGraph();
        Assert.IsFalse(stale.Current);
        Assert.IsFalse(stale.TreatsIntervalAsTraversable(firstId, DamagedInterval));
        Assert.IsFalse(stale.TreatsIntervalAsTraversable(firstId, new RailInterval(16, 110)));

        harness.Session.RebuildDerivedRailGraph();
        var current = harness.Session.QueryDerivedRailGraph();
        Assert.IsTrue(current.Current);
        Assert.IsFalse(current.TreatsIntervalAsTraversable(firstId, DamagedInterval));
        Assert.IsTrue(current.TreatsIntervalAsTraversable(firstId, new RailInterval(16, 110)));

        var render = harness.Session.BuildTrackRenderModel();
        Assert.IsTrue(render.HasGapNear(DamageCell));
        Assert.IsFalse(render.HasGapNear(StraightRailFixture.LeftRailSample));
    }

    [TestMethod]
    public void Shared_seam_cell_maps_to_both_segments()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var supports = SeamSupports(harness, StraightRailFixture.SharedSeamCell);
        CollectionAssert.AreEquivalent(
            new[] { new EntityId(1), new EntityId(2) },
            supports.Select(item => item.SegmentId).Distinct().ToArray());

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(StraightRailFixture.SharedSeamCell, 100).Status);
        var first = harness.Session.Rails.TraversableWorldX(new EntityId(1), harness.Session.World);
        var second = harness.Session.Rails.TraversableWorldX(new EntityId(2), harness.Session.World);
        Assert.IsFalse(first.Any(interval => interval.Start < 144 && interval.End > 144));
        Assert.IsFalse(second.Any(interval => interval.Start <= 144 && interval.End >= 144.1));
        Assert.IsTrue(second.Count >= 1, string.Join(',', second.Select(item => $"{item.Start}..{item.End}")));
    }

    [TestMethod]
    public void Repair_restores_identity_and_rejects_repeat_or_conflict()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var ids = harness.Session.Rails.Segments.Select(segment => segment.Id).ToArray();
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(DamageCell, 100).Status);
        var repaired = harness.Session.RepairRails(new[] { DamageCell });
        Assert.IsTrue(repaired.Succeeded);
        Assert.IsTrue(harness.Session.Query(DamageCell).IsOccupied);
        Assert.AreEqual(100, harness.Session.Query(DamageCell).Voxel!.CurrentDurability);
        CollectionAssert.AreEqual(ids, harness.Session.Rails.Segments.Select(segment => segment.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { new RailInterval(16, 144) },
            harness.Session.Rails.TraversableWorldX(ids[0], harness.Session.World).ToArray());
        Assert.AreEqual(RailRepairStatus.Intact, harness.Session.RepairRails(new[] { DamageCell }).Status);
        Assert.AreEqual(RailRepairStatus.UnknownCell, harness.Session.RepairRails(new[] { new WorldVoxelCoord(0, 0, 0) }).Status);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(DamageCell, 100).Status);
        Assert.AreEqual(
            WorldCommandStatus.Added,
            harness.Session.Add(DamageCell, new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Status);
        Assert.AreEqual(RailRepairStatus.Conflict, harness.Session.RepairRails(new[] { DamageCell }).Status);
        Assert.AreEqual(new VoxelDefinitionId("core", "iron-ore"), harness.Session.Query(DamageCell).Voxel!.DefinitionId);
        CollectionAssert.AreEqual(ids, harness.Session.Rails.Segments.Select(segment => segment.Id).ToArray());
    }

    [TestMethod]
    public void Railway_save_is_rejected_and_keeps_the_previous_file()
    {
        using var harness = RailHarness.Create();
        var empty = harness.Session.Save("Slice 4 Empty");
        Assert.AreEqual(WorldSaveStatus.Saved, empty.Status);
        var path = harness.Store.PathFor(empty.SaveId!.Value);
        var previous = File.ReadAllText(path);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var rejected = harness.Session.Save("Should Not Write");
        Assert.AreEqual(WorldSaveStatus.UnsupportedRailway, rejected.Status);
        Assert.IsFalse(rejected.Succeeded);
        Assert.AreEqual(previous, File.ReadAllText(path));
        Assert.AreEqual(empty.SaveId, rejected.SaveId);

        harness.Session.Reset();
        Assert.IsFalse(harness.Session.HasRailway);
        Assert.AreEqual(WorldSaveStatus.Saved, harness.Session.Save("Slice 4 Reset").Status);
    }

    [TestMethod]
    public void Cube_mesher_skips_rail_occupancy()
    {
        using var harness = RailHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.AreEqual(0, VoxelSurfaceMesher.Build(harness.Session.World).FaceCount);
    }

    [TestMethod]
    public void Console_constructs_queries_repairs_and_rejects_railway_save()
    {
        using var harness = RailHarness.Create();
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session),
            railCommands: new RailConsoleCommands(harness.Session));
        dispatcher.Execute("help");
        StringAssert.Contains(
            harness.Memory.Snapshot().Single(item => item.EventId == ConsoleCommandDispatcher.HelpEventId).Message,
            "rail");

        dispatcher.Execute("rail construct");
        Assert.IsTrue(harness.Session.HasRailway);
        var afterConstruct = Messages(harness, ConsoleCommandDispatcher.RailEventId);
        StringAssert.Contains(afterConstruct, "Committed");
        StringAssert.Contains(afterConstruct, "sleepers 257");

        dispatcher.Execute("rail query 80 1 14");
        StringAssert.Contains(Messages(harness, ConsoleCommandDispatcher.RailEventId), "occupied");

        dispatcher.Execute("voxel damage 110 1 15 100");
        dispatcher.Execute("rail query intervals");
        StringAssert.Contains(Messages(harness, ConsoleCommandDispatcher.RailEventId), "110");

        dispatcher.Execute("rail repair 110 1 15");
        StringAssert.Contains(Messages(harness, ConsoleCommandDispatcher.RailEventId), "Repaired");
        Assert.IsTrue(harness.Session.Query(DamageCell).IsOccupied);

        dispatcher.Execute("voxel save After Railway");
        StringAssert.Contains(Messages(harness, ConsoleCommandDispatcher.VoxelEventId), "UnsupportedRailway");
    }

    private static void AssertOccupies(RailHarness harness, WorldVoxelCoord cell)
    {
        Assert.IsTrue(harness.Session.Rails.Occupies(cell), cell.ToString());
        Assert.IsTrue(harness.Session.Query(cell).IsOccupied, cell.ToString());
        Assert.AreEqual(StraightRailFixture.RailDefinitionId, harness.Session.Query(cell).Voxel!.DefinitionId);
    }

    private static IReadOnlyList<RailCellSupport> SeamSupports(RailHarness harness, WorldVoxelCoord cell)
    {
        Assert.IsTrue(harness.Session.Rails.TryGetOccupancy(cell, out var occupancy));
        return occupancy.Supports;
    }

    private static void AssertPositiveVolumeBoundary()
    {
        var sleeper = StraightRailFixture.Sleeper(64);
        Assert.IsTrue(sleeper.OverlapsCell(new WorldVoxelCoord(80, 1, 14)), sleeper.ToString());
        Assert.IsFalse(sleeper.OverlapsCell(new WorldVoxelCoord(80, 1, 13)), "z=13");
        var left = StraightRailFixture.LeftRail(16, 144);
        Assert.IsTrue(left.OverlapsCell(new WorldVoxelCoord(80, 1, 15)), left.ToString());
        Assert.IsFalse(left.OverlapsCell(new WorldVoxelCoord(80, 1, 14)), "left vs z=14");
        var touching = new RailVolumeBox(80, 1.65, 14, 80, 1.85, 17);
        Assert.IsFalse(touching.OverlapsCell(new WorldVoxelCoord(80, 1, 14)), "zero-volume x");
    }

    private static void AssertRejected(
        RailHarness harness,
        RailConstructionPlan plan,
        RailEditStatus status,
        (int Voxels, ulong NextId, ulong Transactions)? expected = null,
        int? voxels = null)
    {
        var beforeVoxels = voxels ?? harness.Session.World.VoxelCount;
        var beforeId = harness.Session.NextEntityId;
        var beforeTx = harness.Session.TransactionCount;
        var snapshot = Snapshot(harness);
        var notifications = 0;
        harness.Session.Changed += Count;
        try
        {
            Assert.AreEqual(status, harness.Session.ConstructRails(plan).Status);
        }
        finally
        {
            harness.Session.Changed -= Count;
        }

        Assert.AreEqual(expected?.Voxels ?? beforeVoxels, harness.Session.World.VoxelCount);
        Assert.AreEqual(expected?.NextId ?? beforeId, harness.Session.NextEntityId);
        Assert.AreEqual(expected?.Transactions ?? beforeTx, harness.Session.TransactionCount);
        Assert.AreEqual(0, notifications);
        if (expected is null)
            AssertState(harness, snapshot);
        return;

        void Count() => notifications++;
    }

    private static RailConstructionPlan ShiftZ(RailConstructionPlan plan, double deltaZ) =>
        new(
            plan.Segments.Select(segment => segment with
            {
                Alignment = segment.Alignment with
                {
                    StartZ = segment.Alignment.StartZ + deltaZ,
                    EndZ = segment.Alignment.EndZ + deltaZ
                },
                RailVolumes = segment.RailVolumes
                    .Select(volume => volume with { MinZ = volume.MinZ + deltaZ, MaxZ = volume.MaxZ + deltaZ })
                    .ToArray(),
                SleeperVolumes = segment.SleeperVolumes
                    .Select(volume => volume with { MinZ = volume.MinZ + deltaZ, MaxZ = volume.MaxZ + deltaZ })
                    .ToArray()
            }).ToArray(),
            plan.Connections.ToArray());

    private static Dictionary<WorldVoxelCoord, WorldVoxel> Snapshot(RailHarness harness)
    {
        var snapshot = new Dictionary<WorldVoxelCoord, WorldVoxel>();
        foreach (var (position, voxel) in harness.Session.World.OccupiedVoxels())
            snapshot[position] = voxel;
        return snapshot;
    }

    private static void AssertState(RailHarness harness, Dictionary<WorldVoxelCoord, WorldVoxel> expected)
    {
        var actual = Snapshot(harness);
        Assert.AreEqual(expected.Count, actual.Count);
        foreach (var (position, voxel) in expected)
        {
            Assert.IsTrue(actual.TryGetValue(position, out var found), position.ToString());
            Assert.AreEqual(voxel.DefinitionId, found.DefinitionId);
            Assert.AreEqual(voxel.CurrentDurability, found.CurrentDurability);
        }
    }

    private static void SetCursor(VoxelDevelopmentSession session, string name, ulong value) =>
        typeof(VoxelDevelopmentSession).GetProperty(name)!.SetValue(session, value);

    private static string Messages(RailHarness harness, string eventId) =>
        string.Join('\n', harness.Memory.Snapshot().Where(item => item.EventId == eventId).Select(item => item.Message));

    private sealed class RailHarness : IDisposable
    {
        private readonly LoggerSession _logger;
        private readonly bool _ownsUserData;

        public string UserData { get; }
        public FileWorldSaveStore Store { get; }
        public VoxelDevelopmentSession Session { get; }
        public TrainLogger Logger => _logger.Logger;
        public MemoryLogSink Memory => _logger.Memory;

        private RailHarness(
            LoggerSession logger,
            string userData,
            FileWorldSaveStore store,
            VoxelDevelopmentSession session,
            bool ownsUserData)
        {
            _logger = logger;
            UserData = userData;
            Store = store;
            Session = session;
            _ownsUserData = ownsUserData;
        }

        public static RailHarness Create()
        {
            var logger = LoggerSession.Create();
            var userData = Directory.CreateTempSubdirectory("train-rails-").FullName;
            var store = new FileWorldSaveStore(userData);
            var source = RailPackage();
            var reloader = ContentFixtures.Create(source, logger.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var session = new VoxelDevelopmentSession(
                reloader.Catalog,
                VoxelWorldIdentity.Slice4,
                store);
            return new RailHarness(logger, userData, store, session, ownsUserData: true);
        }

        public static MemoryContentSource RailPackage()
        {
            var source = ContentFixtures.ValidIron();
            source.AddVoxelFile("core", "voxels/rail.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("rail", "钢轨", "正式直轨占格", value: 10, tagsJson: "[]")));
            return source;
        }

        public void Dispose()
        {
            Session.Detach();
            _logger.Dispose();
            if (_ownsUserData && Directory.Exists(UserData))
                Directory.Delete(UserData, true);
        }
    }
}

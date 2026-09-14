using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Behaviors;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class StaticTrainAssemblyTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void Formal_session_manufactures_and_assembles_a_locomotive_and_carriage_on_straight_rail()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var before = harness.Session.CaptureTrainAuthority();

        var assembled = harness.Session.AssembleStaticTrain();
        Assert.IsTrue(assembled.Succeeded, assembled.Status.ToString());
        Assert.AreNotEqual(before, harness.Session.CaptureTrainAuthority());

        var assemblies = harness.Session.QueryTrainAssemblies();
        Assert.AreEqual(1, assemblies.Count);
        var assembly = assemblies[0];
        Assert.AreEqual(assembled.AssemblyId, assembly.Id);
        Assert.AreEqual(2, assembly.VehicleIds.Count);
        Assert.AreEqual(1, assembly.Connections.Count);
        Assert.AreEqual(assembly.VehicleIds[0], assembly.ActiveLocomotiveId);
        Assert.AreEqual(RailHeading.AlongAlignment, assembly.Reference.Heading);
        Assert.AreEqual(harness.Session.Rails.Segments[0].Id, assembly.Reference.SegmentId);
        Assert.AreEqual(64.0, assembly.Reference.Distance, Tolerance);

        var loco = harness.Session.QueryComposite(assembly.VehicleIds[0])!;
        var carriage = harness.Session.QueryComposite(assembly.VehicleIds[1])!;
        Assert.AreEqual(StaticTrainFixture.LocomotiveId, loco.DefinitionId);
        Assert.AreEqual(StaticTrainFixture.CarriageId, carriage.DefinitionId);
        Assert.AreEqual(168, loco.Parts.Count);
        Assert.AreEqual(168, carriage.Parts.Count);
        Assert.AreEqual(VehicleSizeClass.Small, loco.SizeClass);
        Assert.AreEqual(new VehicleEnvelopeBox(0, 0, -1, 8, 6, 4), loco.Envelope);
        Assert.AreEqual(ConstructionStatus.Valid, loco.Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, loco.IdentityRetention);
        Assert.AreEqual(OperationalCondition.Operational, loco.OperationalCondition);
        Assert.AreEqual(ConstructionStatus.Valid, carriage.Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, carriage.IdentityRetention);
        Assert.AreEqual(OperationalCondition.Operational, carriage.OperationalCondition);
        CollectionAssert.AreEquivalent(
            new[] { CapabilityTypes.Propulsion, CapabilityTypes.Control, CapabilityTypes.TrackContact },
            loco.Capabilities.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { CapabilityTypes.TrackContact },
            carriage.Capabilities.ToArray());
        Assert.IsFalse(loco.Capabilities.Contains("traction"));

        AssertWorldContact(assembly.Vehicles[0].FrontContact, 80, 2, 16);
        AssertWorldContact(assembly.Vehicles[0].RearContact, 74, 2, 16);
        AssertWorldContact(assembly.Vehicles[1].FrontContact, 71, 2, 16);
        AssertWorldContact(assembly.Vehicles[1].RearContact, 65, 2, 16);
        Assert.AreEqual(6.0, DistanceX(assembly.Vehicles[0].FrontContact, assembly.Vehicles[0].RearContact), Tolerance);
        Assert.AreEqual(6.0, DistanceX(assembly.Vehicles[1].FrontContact, assembly.Vehicles[1].RearContact), Tolerance);
        Assert.AreEqual(1.0, StaticTrainFixture.CouplerGap, Tolerance);
        Assert.AreEqual(73.0, assembly.Vehicles[0].Transform.OriginX - 8, Tolerance);
        Assert.AreEqual(72.0, assembly.Vehicles[1].Transform.OriginX, Tolerance);
        Assert.IsTrue(assembly.Vehicles[0].Transform.IsValid);
        Assert.IsTrue(assembly.Vehicles[1].Transform.IsValid);
        Assert.AreEqual(-1.0, assembly.Vehicles[0].Transform.RightX, Tolerance);
        Assert.AreEqual(1.0, assembly.Vehicles[0].Transform.UpY, Tolerance);
        Assert.AreEqual(0, harness.Session.World.VoxelCount - 1032);

        var coupler = assembly.Connections[0];
        Assert.AreEqual(assembly.VehicleIds[0], coupler.FromVehicleId);
        Assert.AreEqual(StaticTrainFixture.LocomotiveRearCouplerCell, coupler.FromCell);
        Assert.AreEqual(assembly.VehicleIds[1], coupler.ToVehicleId);
        Assert.AreEqual(StaticTrainFixture.CarriageFrontCouplerCell, coupler.ToCell);
        Assert.AreNotEqual(default(EntityId), coupler.FromPartId);
        Assert.AreNotEqual(default(EntityId), coupler.ToPartId);
        Assert.AreEqual(loco.Parts.Single(part => part.Cell == StaticTrainFixture.LocomotiveRearCouplerCell).Id, coupler.FromPartId);
        Assert.AreNotEqual(loco.Contacts[0].BoundPartId, coupler.FromPartId);
        Assert.AreNotEqual(loco.Contacts[1].BoundPartId, coupler.FromPartId);
    }

    [TestMethod]
    public void Unknown_behaviors_and_illegal_parameters_are_rejected_by_the_catalog()
    {
        using var isolated = LoggerSession.Create();
        var unknown = CompositeFixtures.FormalFixturePackage();
        unknown.AddVoxelFile("core", "voxels/future.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("future", "未知行为", "应拒绝", value: 10, tagsJson: "[]", behaviorsJson: "[{\"type\":\"future\"}]")));
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(unknown, isolated.Logger).Reload().Status);

        using var illegal = LoggerSession.Create();
        var extra = CompositeFixtures.FormalFixturePackage();
        extra.AddVoxelFile("core", "voxels/illegal.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "illegal",
                "非法参数",
                "应拒绝",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: "[{\"type\":\"propulsion\",\"power\":1}]")));
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(extra, illegal.Logger).Reload().Status);
    }

    [TestMethod]
    public void Capabilities_come_from_live_registered_behaviors_not_blueprint_claims()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var loco = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.LocomotiveId);
        var propulsion = loco.Parts.Single(part => part.DefinitionId == StaticTrainFixture.PropulsionId);
        var before = harness.Session.CaptureTrainAuthority();
        Assert.IsTrue(harness.Session.Damage(loco.Id, propulsion.Cell, 100).Succeeded);
        var damaged = harness.Session.QueryComposite(loco.Id)!;
        Assert.AreEqual(IdentityRetentionStatus.Retained, damaged.IdentityRetention);
        Assert.AreEqual(ConstructionStatus.Valid, damaged.Construction);
        Assert.IsFalse(damaged.Capabilities.Contains(CapabilityTypes.Propulsion));
        Assert.IsTrue(damaged.Capabilities.Contains(CapabilityTypes.Control));
        Assert.AreEqual(OperationalCondition.Disabled, damaged.OperationalCondition);
        Assert.AreNotEqual(before.Composites.Vehicles.Single(item => item.Id == loco.Id).Capabilities, damaged.Capabilities);
    }

    [TestMethod]
    public void Destroying_the_locomotive_rear_coupler_keeps_identity_contacts_and_returns_damage_targets()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        var locoId = assembly.VehicleIds[0];
        var coupler = assembly.Connections[0];
        var beforeVehicles = harness.Session.QueryComposites().Count;
        var beforeWorld = harness.Session.World.VoxelCount;

        var damaged = harness.Session.Damage(locoId, coupler.FromCell, 100);
        Assert.IsTrue(damaged.Succeeded);
        Assert.AreEqual(coupler.FromPartId, damaged.Targets![0].PartId);
        var loco = harness.Session.QueryComposite(locoId)!;
        Assert.AreEqual(IdentityRetentionStatus.Retained, loco.IdentityRetention);
        Assert.AreEqual(ConstructionStatus.Valid, loco.Construction);
        Assert.AreEqual(2, loco.Contacts.Count);
        Assert.IsNull(loco.Parts.FirstOrDefault(part => part.Cell == coupler.FromCell));
        Assert.AreEqual(167, loco.Parts.Count);
        Assert.AreEqual(beforeVehicles, harness.Session.QueryComposites().Count);
        Assert.AreEqual(0, harness.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(beforeWorld, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Assembly_failures_leave_world_vehicles_and_counters_unchanged()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var empty = harness.Session.CaptureTrainAuthority();
        AssertRejected(
            harness,
            TrainAssemblyStatus.NoRailway,
            () => new VoxelDevelopmentSession(
                harness.Reloader.Catalog,
                VoxelWorldIdentity.Slice4,
                composites: harness.Reloader.Composites).AssembleStaticTrain());

        AssertRejected(
            harness,
            TrainAssemblyStatus.UnknownVehicle,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(new EntityId(99), new EntityId(100))),
            empty);

        Assert.IsTrue(harness.Session.Manufacture(HollowShellFixture.DefinitionId).Succeeded);
        var parked = harness.Session.CaptureTrainAuthority();
        var shell = harness.Session.QueryComposites()[0].Id;
        AssertRejected(
            harness,
            TrainAssemblyStatus.MissingPropulsion,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(shell, shell)),
            parked);

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.LocomotiveId).Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.CarriageId).Succeeded);
        var locoId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.LocomotiveId).Id;
        var carriageId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.CarriageId).Id;
        var manufactured = harness.Session.CaptureTrainAuthority();
        AssertRejected(
            harness,
            TrainAssemblyStatus.InvalidConnection,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(
                locoId,
                carriageId,
                HollowShellFixture.CoreCell,
                StaticTrainFixture.CarriageFrontCouplerCell)),
            manufactured);

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var formed = harness.Session.CaptureTrainAuthority();
        AssertRejected(harness, TrainAssemblyStatus.AlreadyAssembled, () => harness.Session.AssembleStaticTrain(), formed);
    }

    [TestMethod]
    public void Offset_only_railway_rejects_static_assembly_for_insufficient_support()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(ShiftZ(StraightRailFixture.CreateFixturePlan(), 8)).Succeeded);
        Assert.AreEqual(24.0, harness.Session.Rails.Segments[0].Alignment.StartZ, Tolerance);
        AssertRejected(harness, TrainAssemblyStatus.InsufficientSupport, () => harness.Session.AssembleStaticTrain());
        Assert.AreEqual(0, harness.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Same_world_x_rails_select_the_three_dimensional_supporting_segment()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(ShiftZ(StraightRailFixture.CreateFixturePlan(), 8)).Succeeded);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var offsetFirst = harness.Session.Rails.Segments[0];
        Assert.AreEqual(24.0, offsetFirst.Alignment.StartZ, Tolerance);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);

        var assembly = harness.Session.QueryTrainAssemblies().Single();
        var supporting = harness.Session.Rails.Segments.Single(segment =>
            Math.Abs(segment.Alignment.StartZ - StraightRailFixture.CenterZ) <= Tolerance &&
            Math.Abs(segment.Alignment.StartX - StraightRailFixture.LineStartX) <= Tolerance);
        Assert.AreEqual(supporting.Id, assembly.Reference.SegmentId);
        Assert.AreNotEqual(offsetFirst.Id, assembly.Reference.SegmentId);
        AssertWorldContact(assembly.Vehicles[0].FrontContact, 80, 2, 16);
        AssertWorldContact(assembly.Vehicles[1].FrontContact, 71, 2, 16);
    }

    [TestMethod]
    public void Damage_between_contacts_rejects_disconnected_path()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        var firstId = harness.Session.Rails.Segments[0].Id;
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Damage(new WorldVoxelCoord(77, 1, 15), 100).Status);
        var intervals = harness.Session.Rails.TraversableWorldX(firstId, harness.Session.World);
        Assert.IsFalse(intervals.Any(interval => interval.Start <= 65 && interval.End >= 80));
        Assert.IsTrue(intervals.Any(interval => interval.Start <= 65 && 65 <= interval.End));
        Assert.IsTrue(intervals.Any(interval => interval.Start <= 80 && 80 <= interval.End));
        AssertRejected(harness, TrainAssemblyStatus.DisconnectedPath, () => harness.Session.AssembleStaticTrain());
    }

    [TestMethod]
    public void Unconnected_segments_under_contacts_reject_disconnected_path()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(CreateSplitUnconnectedPlan()).Succeeded);
        Assert.AreEqual(2, harness.Session.Rails.Segments.Count);
        Assert.IsFalse(harness.Session.Rails.AreConnected(
            harness.Session.Rails.Segments[0].Id,
            harness.Session.Rails.Segments[1].Id));
        AssertRejected(harness, TrainAssemblyStatus.DisconnectedPath, () => harness.Session.AssembleStaticTrain());
    }

    [TestMethod]
    public void Explicitly_connected_technical_seams_at_contacts_still_form()
    {
        foreach (var seam in new[] { 72.0, 80.0, 74.0, 71.0, 65.0 })
            AssertConnectedSeamForms(seam, reverseSegments: false);

        AssertConnectedSeamForms(80.0, reverseSegments: true);
    }

    [TestMethod]
    public void Rebuilding_after_a_reference_contact_seam_keeps_authority()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(CreateConnectedSeamPlan(80, reverseSegments: false)).Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var before = harness.Session.CaptureTrainAuthority();
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        harness.Session.RebuildDerivedRailGraph();
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(assembly, harness.Session.QueryTrainAssemblies().Single());
        Assert.AreEqual(assembly.Reference, harness.Session.QueryTrainAssemblies().Single().Reference);
        AssertWorldContact(assembly.Vehicles[0].FrontContact, 80, 2, 16);
        AssertWorldContact(assembly.Vehicles[1].FrontContact, 71, 2, 16);
    }

    [TestMethod]
    public void Unconnected_shared_endpoint_at_a_contact_is_not_a_path()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails(CreateConnectedSeamPlan(80, reverseSegments: false, connect: false)).Succeeded);
        Assert.IsFalse(harness.Session.Rails.AreConnected(
            harness.Session.Rails.Segments[0].Id,
            harness.Session.Rails.Segments[1].Id));
        AssertRejected(harness, TrainAssemblyStatus.InsufficientSupport, () => harness.Session.AssembleStaticTrain());
    }

    [TestMethod]
    public void Destroying_a_locomotive_contact_keeps_vehicles_and_queryable_break()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var before = harness.Session.QueryTrainAssemblies().Single();
        var locoId = before.VehicleIds[0];
        var pose = before.Vehicles[0].Transform;

        var damaged = harness.Session.Damage(locoId, HollowShellFixture.RearBoundCell, 100);
        Assert.AreEqual(CompositeDamageStatus.Damaged, damaged.Status);
        var assemblies = harness.Session.QueryTrainAssemblies();
        Assert.AreEqual(0, assemblies.Count);
        var authority = harness.Session.CaptureTrainAuthority();
        Assert.AreEqual(0, authority.Assemblies.Count);
        var loco = harness.Session.QueryComposite(locoId)!;
        Assert.AreEqual(IdentityRetentionStatus.Retained, loco.IdentityRetention);
        Assert.AreEqual(OperationalCondition.Disabled, loco.OperationalCondition);
        Assert.AreEqual(1, loco.Contacts.Count);
        Assert.AreEqual(pose, loco.Transform);
        Assert.AreEqual(2, harness.Session.QueryComposites().Count);
        Assert.AreEqual(before.Vehicles[1].Transform, harness.Session.QueryComposite(before.VehicleIds[1])!.Transform);
    }

    [TestMethod]
    public void Destroying_a_carriage_contact_is_queryable_from_console()
    {
        using var harness = StaticTrainHarness.Create();
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session),
            new CompositeConsoleCommands(harness.Session),
            new RailConsoleCommands(harness.Session));
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var carriageId = harness.Session.QueryTrainAssemblies().Single().VehicleIds[1];

        Assert.AreEqual(
            CompositeDamageStatus.Damaged,
            harness.Session.Damage(carriageId, HollowShellFixture.RearBoundCell, 100).Status);
        Assert.AreEqual(0, harness.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(OperationalCondition.Disabled, harness.Session.QueryComposite(carriageId)!.OperationalCondition);

        dispatcher.Execute("rail query assembly");
        dispatcher.Execute($"composite query {carriageId.Value}");
        var railLog = Messages(harness, ConsoleCommandDispatcher.RailEventId);
        var compositeLog = Messages(harness, ConsoleCommandDispatcher.CompositeEventId);
        StringAssert.Contains(railLog, "Rail assembly none.");
        StringAssert.Contains(compositeLog, StaticTrainFixture.CarriageId.Value);
        StringAssert.Contains(compositeLog, "operational Disabled");
    }

    [TestMethod]
    public void AssembleStaticTrain_prechecks_entity_and_transaction_capacity()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        const ulong requiredIds = 2UL * (1UL + HollowShellFixture.CellCount) + 1UL;

        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), ulong.MaxValue);
        AssertRejected(harness, TrainAssemblyStatus.EntityIdExhausted, () => harness.Session.AssembleStaticTrain());
        Assert.AreEqual(ulong.MaxValue, harness.Session.NextEntityId);

        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), ulong.MaxValue - requiredIds + 1);
        AssertRejected(harness, TrainAssemblyStatus.EntityIdExhausted, () => harness.Session.AssembleStaticTrain());
        Assert.AreEqual(ulong.MaxValue - requiredIds + 1, harness.Session.NextEntityId);

        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.TransactionCount), ulong.MaxValue - 1);
        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), 3);
        AssertRejected(harness, TrainAssemblyStatus.TransactionExhausted, () => harness.Session.AssembleStaticTrain());
        Assert.AreEqual(ulong.MaxValue - 1, harness.Session.TransactionCount);
        Assert.AreEqual(3UL, harness.Session.NextEntityId);

        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.TransactionCount), 1);
        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), ulong.MaxValue - requiredIds);
        var assembled = harness.Session.AssembleStaticTrain();
        Assert.IsTrue(assembled.Succeeded, assembled.Status.ToString());
        Assert.AreEqual(ulong.MaxValue, harness.Session.NextEntityId);
        Assert.AreEqual(1, harness.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(2, harness.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Illegal_coupler_ends_are_rejected_without_forming()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.LocomotiveId).Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.CarriageId).Succeeded);
        var locoId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.LocomotiveId).Id;
        var carriageId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.CarriageId).Id;
        var manufactured = harness.Session.CaptureTrainAuthority();

        AssertRejected(
            harness,
            TrainAssemblyStatus.InvalidConnection,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(
                locoId,
                carriageId,
                new LocalVoxelCoord(0, 5, -1),
                new LocalVoxelCoord(7, 5, 3))),
            manufactured);
        AssertRejected(
            harness,
            TrainAssemblyStatus.InvalidConnection,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(
                locoId,
                carriageId,
                StaticTrainFixture.CarriageFrontCouplerCell,
                StaticTrainFixture.CarriageFrontCouplerCell)),
            manufactured);
        AssertRejected(
            harness,
            TrainAssemblyStatus.InvalidConnection,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(
                locoId,
                carriageId,
                StaticTrainFixture.LocomotiveRearCouplerCell,
                StaticTrainFixture.LocomotiveRearCouplerCell)),
            manufactured);

        Assert.IsTrue(harness.Session.Damage(locoId, StaticTrainFixture.LocomotiveRearCouplerCell, 100).Succeeded);
        var afterDamage = harness.Session.CaptureTrainAuthority();
        AssertRejected(
            harness,
            TrainAssemblyStatus.MissingConnection,
            () => harness.Session.AssembleTrain(new TrainAssemblyRequest(
                locoId,
                carriageId,
                StaticTrainFixture.LocomotiveRearCouplerCell,
                StaticTrainFixture.CarriageFrontCouplerCell)),
            afterDamage);

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.LocomotiveId).Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.CarriageId).Succeeded);
        locoId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.LocomotiveId).Id;
        carriageId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.CarriageId).Id;
        var formed = harness.Session.AssembleTrain(new TrainAssemblyRequest(
            locoId,
            carriageId,
            StaticTrainFixture.LocomotiveRearCouplerCell,
            StaticTrainFixture.CarriageFrontCouplerCell));
        Assert.IsTrue(formed.Succeeded, formed.Status.ToString());
        Assert.AreEqual(StaticTrainFixture.LocomotiveRearCouplerCell, harness.Session.QueryTrainAssemblies().Single().Connections[0].FromCell);
    }

    [TestMethod]
    public void Rebuilding_the_derived_graph_does_not_move_committed_assembly_state()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var before = harness.Session.CaptureTrainAuthority();
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        harness.Session.RebuildDerivedRailGraph();
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(assembly, harness.Session.QueryTrainAssemblies().Single());
        Assert.AreEqual(assembly.Reference, harness.Session.QueryTrainAssemblies().Single().Reference);
    }

    [TestMethod]
    public void Railway_and_assembly_save_is_rejected_and_parked_vehicle_save_still_works()
    {
        using var harness = StaticTrainHarness.Create();
        var empty = harness.Session.Save("Slice 4 Empty");
        Assert.AreEqual(WorldSaveStatus.Saved, empty.Status);
        var path = harness.Store.PathFor(empty.SaveId!.Value);
        var previous = File.ReadAllText(path);

        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var rejected = harness.Session.Save("Should Not Write");
        Assert.AreEqual(WorldSaveStatus.UnsupportedRailway, rejected.Status);
        Assert.AreEqual(previous, File.ReadAllText(path));

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.Manufacture(HollowShellFixture.DefinitionId).Succeeded);
        Assert.AreEqual(WorldSaveStatus.Saved, harness.Session.Save("Parked").Status);
        Assert.AreEqual(1, harness.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Console_uses_the_same_manufacture_assemble_and_query_entry()
    {
        using var harness = StaticTrainHarness.Create();
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session),
            new CompositeConsoleCommands(harness.Session),
            new RailConsoleCommands(harness.Session));

        dispatcher.Execute("rail construct");
        dispatcher.Execute("rail assemble");
        Assert.AreEqual(1, harness.Session.QueryTrainAssemblies().Count);
        dispatcher.Execute("rail query assembly");
        var assemblyLog = Messages(harness, ConsoleCommandDispatcher.RailEventId);
        StringAssert.Contains(assemblyLog, "assembly");
        StringAssert.Contains(assemblyLog, "active");
        StringAssert.Contains(assemblyLog, "80");
        StringAssert.Contains(assemblyLog, "Operational");

        dispatcher.Execute("composite query");
        var compositeLog = Messages(harness, ConsoleCommandDispatcher.CompositeEventId);
        StringAssert.Contains(compositeLog, StaticTrainFixture.LocomotiveId.Value);
        StringAssert.Contains(compositeLog, StaticTrainFixture.CarriageId.Value);
    }

    private static void AssertWorldContact(WorldContactQuery contact, double x, double y, double z)
    {
        Assert.AreEqual(x, contact.X, Tolerance, "x");
        Assert.AreEqual(y, contact.Y, Tolerance, "y");
        Assert.AreEqual(z, contact.Z, Tolerance, "z");
    }

    private static double DistanceX(WorldContactQuery left, WorldContactQuery right) =>
        Math.Abs(left.X - right.X);

    private static string Messages(StaticTrainHarness harness, string eventId) =>
        string.Join('\n', harness.Memory.Snapshot().Where(item => item.EventId == eventId).Select(item => item.Message));

    private static void AssertRejected(
        StaticTrainHarness harness,
        TrainAssemblyStatus expected,
        Func<TrainAssemblyResult> act,
        TrainAuthoritySnapshot? expectedAuthority = null)
    {
        var before = expectedAuthority ?? harness.Session.CaptureTrainAuthority();
        var notifications = 0;
        harness.Session.Changed += Count;
        try
        {
            Assert.AreEqual(expected, act().Status);
            Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
            Assert.AreEqual(0, notifications);
        }
        finally
        {
            harness.Session.Changed -= Count;
        }

        void Count() => notifications++;
    }

    private static void SetCursor(VoxelDevelopmentSession session, string name, ulong value) =>
        typeof(VoxelDevelopmentSession).GetProperty(name)!.SetValue(session, value);

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

    private static void AssertConnectedSeamForms(double seam, bool reverseSegments)
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(
            harness.Session.ConstructRails(CreateConnectedSeamPlan(seam, reverseSegments)).Succeeded,
            $"seam {seam} reverse={reverseSegments}");
        Assert.IsTrue(harness.Session.Rails.AreConnected(
            harness.Session.Rails.Segments[0].Id,
            harness.Session.Rails.Segments[1].Id));
        var assembled = harness.Session.AssembleStaticTrain();
        Assert.IsTrue(assembled.Succeeded, $"seam {seam} reverse={reverseSegments} {assembled.Status}");
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        var owner = harness.Session.Rails.Segments.Single(segment =>
            Math.Abs(segment.Alignment.EndX - seam) <= Tolerance &&
            Math.Abs(segment.Alignment.StartX - StraightRailFixture.LineStartX) <= Tolerance);
        if (Math.Abs(seam - StaticTrainFixture.LocomotiveFrontContactX) <= Tolerance)
        {
            Assert.AreEqual(owner.Id, assembly.Reference.SegmentId, $"seam {seam} reverse={reverseSegments}");
            Assert.AreEqual(
                StaticTrainFixture.AlongTrackDistance(StaticTrainFixture.LocomotiveFrontContactX, owner.Alignment),
                assembly.Reference.Distance,
                Tolerance);
        }

        Assert.AreEqual(RailHeading.AlongAlignment, assembly.Reference.Heading);
        AssertWorldContact(assembly.Vehicles[0].FrontContact, 80, 2, 16);
        AssertWorldContact(assembly.Vehicles[1].FrontContact, 71, 2, 16);
        Assert.AreEqual(2, harness.Session.QueryComposites().Count);
    }

    private static RailConstructionPlan CreateConnectedSeamPlan(
        double seam,
        bool reverseSegments,
        bool connect = true)
    {
        PlannedRailSegment Segment(double startX, double endX) =>
            new(
                new HorizontalStraightAlignment(
                    startX,
                    StraightRailFixture.CenterY,
                    StraightRailFixture.CenterZ,
                    endX,
                    StraightRailFixture.CenterY,
                    StraightRailFixture.CenterZ),
                new[] { StraightRailFixture.LeftRail(startX, endX), StraightRailFixture.RightRail(startX, endX) },
                Array.Empty<RailVolumeBox>());

        var first = Segment(StraightRailFixture.LineStartX, seam);
        var second = Segment(seam, StraightRailFixture.SeamX);
        var segments = reverseSegments ? new[] { second, first } : new[] { first, second };
        var connections = connect
            ? new[]
            {
                reverseSegments
                    ? new PlannedRailConnection(1, RailEnd.End, 0, RailEnd.Start)
                    : new PlannedRailConnection(0, RailEnd.End, 1, RailEnd.Start)
            }
            : Array.Empty<PlannedRailConnection>();
        return new RailConstructionPlan(segments, connections);
    }

    private static RailConstructionPlan CreateSplitUnconnectedPlan()
    {
        var first = new HorizontalStraightAlignment(
            StraightRailFixture.LineStartX,
            StraightRailFixture.CenterY,
            StraightRailFixture.CenterZ,
            72,
            StraightRailFixture.CenterY,
            StraightRailFixture.CenterZ);
        var second = new HorizontalStraightAlignment(
            74,
            StraightRailFixture.CenterY,
            StraightRailFixture.CenterZ,
            StraightRailFixture.SeamX,
            StraightRailFixture.CenterY,
            StraightRailFixture.CenterZ);
        return new RailConstructionPlan(
            new[]
            {
                new PlannedRailSegment(
                    first,
                    new[] { StraightRailFixture.LeftRail(first.StartX, first.EndX), StraightRailFixture.RightRail(first.StartX, first.EndX) },
                    StraightRailFixture.SleepersFor(first, includeSharedSeam: false)),
                new PlannedRailSegment(
                    second,
                    new[] { StraightRailFixture.LeftRail(second.StartX, second.EndX), StraightRailFixture.RightRail(second.StartX, second.EndX) },
                    StraightRailFixture.SleepersFor(second, includeSharedSeam: false))
            },
            Array.Empty<PlannedRailConnection>());
    }
}

internal sealed class StaticTrainHarness : IDisposable
{
    private readonly LoggerSession _logger;

    public string UserData { get; }
    public FileWorldSaveStore Store { get; }
    public MemoryContentSource Source { get; }
    public VoxelDevelopmentSession Session { get; }
    public ContentReloader Reloader { get; }
    public TrainLogger Logger => _logger.Logger;
    public MemoryLogSink Memory => _logger.Memory;

    private StaticTrainHarness(
        LoggerSession logger,
        string userData,
        FileWorldSaveStore store,
        MemoryContentSource source,
        ContentReloader reloader,
        VoxelDevelopmentSession session)
    {
        _logger = logger;
        UserData = userData;
        Store = store;
        Source = source;
        Reloader = reloader;
        Session = session;
    }

    public static StaticTrainHarness Create()
    {
        var logger = LoggerSession.Create();
        var userData = Directory.CreateTempSubdirectory("train-static-").FullName;
        var store = new FileWorldSaveStore(userData);
        var source = Package();
        var reloader = ContentFixtures.Create(source, logger.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var session = new VoxelDevelopmentSession(
            reloader.Catalog,
            VoxelWorldIdentity.Slice4,
            store,
            reloader.Composites);
        return new StaticTrainHarness(logger, userData, store, source, reloader, session);
    }

    public static MemoryContentSource Package()
    {
        var source = CompositeFixtures.FormalFixturePackage();
        source.AddVoxelFile("core", "voxels/rail.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("rail", "钢轨", "正式直轨占格", value: 10, tagsJson: "[]")));
        ContentFixtures.AddSquareImages(
            source,
            "voxels/ferrous-ore/icon.png",
            "voxels/ferrous-ore/top.png",
            "voxels/ferrous-ore/side.png",
            "voxels/ferrous-ore/bottom.png");
        source.AddVoxelFile("core", "voxels/propulsion.voxels.json", CompositeFixtures.BuiltinText("voxels", "propulsion.voxels.json"));
        source.AddVoxelFile("core", "voxels/control.voxels.json", CompositeFixtures.BuiltinText("voxels", "control.voxels.json"));
        source.AddVoxelFile("core", "voxels/track-contact.voxels.json", CompositeFixtures.BuiltinText("voxels", "track-contact.voxels.json"));
        source.AddVoxelFile("core", "voxels/locomotive-core.voxels.json", CompositeFixtures.BuiltinText("voxels", "locomotive-core.voxels.json"));
        source.AddVoxelFile("core", "voxels/carriage-core.voxels.json", CompositeFixtures.BuiltinText("voxels", "carriage-core.voxels.json"));
        source.AddCompositeFile("core", "composites/dev-locomotive.composites.json", CompositeFixtures.BuiltinText("composites", "dev-locomotive.composites.json"));
        source.AddCompositeFile("core", "composites/dev-carriage.composites.json", CompositeFixtures.BuiltinText("composites", "dev-carriage.composites.json"));
        return source;
    }

    public void Dispose()
    {
        Session.Detach();
        _logger.Dispose();
        if (Directory.Exists(UserData))
            Directory.Delete(UserData, true);
    }
}

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
        Assert.AreEqual(TrainAssemblyStatus.NoRailway, new VoxelDevelopmentSession(
            harness.Reloader.Catalog,
            VoxelWorldIdentity.Slice4,
            composites: harness.Reloader.Composites).AssembleStaticTrain().Status);

        Assert.AreEqual(TrainAssemblyStatus.UnknownVehicle, harness.Session.AssembleTrain(
            new TrainAssemblyRequest(new EntityId(99), new EntityId(100))).Status);
        Assert.AreEqual(empty, harness.Session.CaptureTrainAuthority());

        Assert.IsTrue(harness.Session.Manufacture(HollowShellFixture.DefinitionId).Succeeded);
        var parked = harness.Session.CaptureTrainAuthority();
        var shell = harness.Session.QueryComposites()[0].Id;
        Assert.AreEqual(
            TrainAssemblyStatus.MissingPropulsion,
            harness.Session.AssembleTrain(new TrainAssemblyRequest(shell, shell)).Status);
        Assert.AreEqual(parked, harness.Session.CaptureTrainAuthority());

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.LocomotiveId).Succeeded);
        Assert.IsTrue(harness.Session.Manufacture(StaticTrainFixture.CarriageId).Succeeded);
        var locoId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.LocomotiveId).Id;
        var carriageId = harness.Session.QueryComposites().Single(item => item.DefinitionId == StaticTrainFixture.CarriageId).Id;
        var manufactured = harness.Session.CaptureTrainAuthority();
        Assert.AreEqual(
            TrainAssemblyStatus.InvalidConnection,
            harness.Session.AssembleTrain(new TrainAssemblyRequest(
                locoId,
                carriageId,
                HollowShellFixture.CoreCell,
                StaticTrainFixture.CarriageFrontCouplerCell)).Status);
        Assert.AreEqual(manufactured, harness.Session.CaptureTrainAuthority());

        harness.Session.Reset();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var formed = harness.Session.CaptureTrainAuthority();
        Assert.AreEqual(TrainAssemblyStatus.AlreadyAssembled, harness.Session.AssembleStaticTrain().Status);
        Assert.AreEqual(formed, harness.Session.CaptureTrainAuthority());
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
}

internal sealed class StaticTrainHarness : IDisposable
{
    private readonly LoggerSession _logger;

    public string UserData { get; }
    public FileWorldSaveStore Store { get; }
    public VoxelDevelopmentSession Session { get; }
    public ContentReloader Reloader { get; }
    public TrainLogger Logger => _logger.Logger;
    public MemoryLogSink Memory => _logger.Memory;

    private StaticTrainHarness(
        LoggerSession logger,
        string userData,
        FileWorldSaveStore store,
        ContentReloader reloader,
        VoxelDevelopmentSession session)
    {
        _logger = logger;
        UserData = userData;
        Store = store;
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
        return new StaticTrainHarness(logger, userData, store, reloader, session);
    }

    public static MemoryContentSource Package()
    {
        var source = CompositeFixtures.FormalFixturePackage();
        source.AddVoxelFile("core", "voxels/rail.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("rail", "钢轨", "正式直轨占格", value: 10, tagsJson: "[]")));
        ContentFixtures.AddSquareImages(
            source,
            "voxels/iron-ore/icon.png",
            "voxels/iron-ore/top.png",
            "voxels/iron-ore/side.png",
            "voxels/iron-ore/bottom.png");
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

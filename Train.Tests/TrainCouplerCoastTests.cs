using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Core.Simulation;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:railway")]
public sealed class TrainCouplerCoastTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void IntegrateCoast_segments_at_zero_within_one_e_minus_8()
    {
        StraightTrainMotion.IntegrateCoast(4, 1, 4, out var speed, out var travel);
        Assert.AreEqual(0.0, speed, Tolerance);
        Assert.AreEqual(8.0, travel, Tolerance);

        var remaining = 4.0;
        var total = 0.0;
        for (var step = 0; step < 240; step++)
        {
            StraightTrainMotion.IntegrateCoast(remaining, 1, SimulationClock.StepDuration, out remaining, out var dx);
            total += dx;
        }

        Assert.AreEqual(0.0, remaining, Tolerance);
        Assert.AreEqual(8.0, total, Tolerance);
    }

    [TestMethod]
    public void Default_and_explicit_track_contact_share_a_semantic_revision()
    {
        using var omitted = LoggerSession.Create();
        using var explicitDefaults = LoggerSession.Create();
        var left = ContentFixtures.Create(StaticTrainHarness.Package(), omitted.Logger);
        var rightSource = StaticTrainHarness.Package();
        rightSource.AddVoxelFile("core", "voxels/track-contact.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "track-contact",
                "开发轨道接触件",
                "切片 4.2 轨道接触行为零件，绑定后接触锚点。能力由已注册 track_contact 行为推导。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: "[{\"type\":\"track_contact\",\"coast_deceleration\":1}]")));
        var right = ContentFixtures.Create(rightSource, explicitDefaults.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, left.Reload().Status);
        Assert.AreEqual(ContentReloadStatus.Committed, right.Reload().Status);
        Assert.IsTrue(left.Catalog.Current!.TryGet(StaticTrainFixture.TrackContactId, out var leftEntry));
        Assert.IsTrue(right.Catalog.Current!.TryGet(StaticTrainFixture.TrackContactId, out var rightEntry));
        Assert.AreEqual(leftEntry.SemanticRevision, rightEntry.SemanticRevision);
        Assert.AreEqual(1.0, leftEntry.Definition.TrackContact!.CoastDeceleration, Tolerance);
        Assert.AreEqual(1.0, rightEntry.Definition.TrackContact!.CoastDeceleration, Tolerance);
    }

    [TestMethod]
    public void Destroying_the_running_rear_coupler_splits_and_coasts_on_the_fixture_checkpoints()
    {
        using var harness = RunningAt300();
        var before = harness.Session.QueryTrainAssemblies().Single();
        AssertWorldContact(before.Vehicles[0].FrontContact, 96, 2, 16);
        AssertWorldContact(before.Vehicles[1].FrontContact, 87, 2, 16);
        Assert.AreEqual(4.0, before.Speed, Tolerance);

        var notifications = 0;
        TrainAuthoritySnapshot? observed = null;
        void Observe()
        {
            notifications++;
            observed = harness.Session.CaptureTrainAuthority();
        }

        harness.Session.Changed += Observe;
        var damaged = harness.Session.Damage(before.ActiveLocomotiveId!.Value, before.Connections[0].FromCell, 100);
        harness.Session.Changed -= Observe;
        Assert.IsTrue(damaged.Succeeded);
        Assert.AreEqual(before.Connections[0].FromPartId, damaged.Targets![0].PartId);
        Assert.AreEqual(1, notifications);
        Assert.IsNotNull(observed);
        Assert.AreEqual(2, observed!.Assemblies.Count);
        Assert.AreEqual(observed, harness.Session.CaptureTrainAuthority());

        var split = AssertSplit(harness, 96, 87, 4, 4);
        Assert.AreEqual(before.Id, split.Loco.Id);
        Assert.AreNotEqual(before.Id, split.Carriage.Id);
        Assert.IsNull(harness.Session.QueryComposite(before.VehicleIds[0])!.Parts.FirstOrDefault(part => part.Cell == before.Connections[0].FromCell));
        Assert.AreEqual(2, harness.Session.QueryComposites().Count);
        Assert.AreEqual(0, split.Loco.Connections.Count);
        Assert.AreEqual(0, split.Carriage.Connections.Count);
        Assert.AreNotEqual(split.Loco.Reference, split.Carriage.Reference);

        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        Assert.AreEqual(330UL, harness.Session.SimulationStep);
        AssertSplit(harness, 98, 88.875, 4, 3.5);

        Assert.AreEqual(TrainMotionStatus.UnknownAssembly, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);
        Assert.AreEqual(TrainMotionStatus.MissingPropulsion, harness.Session.StartTrain(RailHeading.AlongAlignment, assemblyId: split.Carriage.Id).Status);
        Assert.AreEqual(TrainMotionStatus.ReverseUnsupported, harness.Session.StartTrain(RailHeading.AgainstAlignment, assemblyId: split.Carriage.Id).Status);
        Assert.AreEqual(TrainMotionStatus.Started, harness.Session.StartTrain(RailHeading.AlongAlignment, assemblyId: split.Loco.Id).Status);

        Assert.IsTrue(harness.Session.AdvanceSimulation(210).Succeeded);
        var stopped = AssertSplit(harness, 112, 95, 4, 0);
        Assert.IsFalse(stopped.Carriage.Running);
        Assert.AreEqual(0.0, stopped.Carriage.Speed, Tolerance);
    }

    [TestMethod]
    public void Formal_save_at_330_resumes_the_same_authority_and_keeps_zero_after_coast_stop()
    {
        using var live = RunningAt300();
        BreakCoupler(live.Session);
        Assert.IsTrue(live.Session.AdvanceSimulation(30).Succeeded);
        var liveHash = WorldSaveAuthority.Fingerprint(live.Session);
        var liveAuthority = live.Session.CaptureTrainAuthority();
        var saved = live.Session.Save("Coupler Coast 330");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var reader = StaticTrainHarness.Create(live.UserData, live.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(330UL, reader.Session.SimulationStep);
        Assert.AreEqual(liveAuthority, reader.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHash, WorldSaveAuthority.Fingerprint(reader.Session));

        Assert.IsTrue(live.Session.AdvanceSimulation(210).Succeeded);
        Assert.IsTrue(reader.Session.AdvanceSimulation(210).Succeeded);
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(live.Session), WorldSaveAuthority.Fingerprint(reader.Session));
        Assert.AreEqual(0.0, reader.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is null).Speed, Tolerance);

        var stopped = reader.Session.Save("Coupler Coast Stopped");
        using var zero = StaticTrainHarness.Create(reader.UserData, reader.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, zero.Session.Load(stopped.SaveId!.Value).Status);
        Assert.AreEqual(0.0, zero.Session.QueryTrainAssemblies().Single(item => item.ActiveLocomotiveId is null).Speed, Tolerance);
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(reader.Session), WorldSaveAuthority.Fingerprint(zero.Session));
    }

    [TestMethod]
    public void New_assembly_id_exhaustion_rejects_coupler_damage_atomically()
    {
        using var harness = RunningAt300();
        var before = harness.Session.CaptureTrainAuthority();
        var next = harness.Session.NextEntityId;
        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), ulong.MaxValue);
        var notifications = 0;
        harness.Session.Changed += () => notifications++;
        var coupler = harness.Session.QueryTrainAssemblies().Single().Connections[0];
        var result = harness.Session.Damage(before.Assemblies[0].ActiveLocomotiveId!.Value, coupler.FromCell, 100);
        Assert.AreEqual(CompositeDamageStatus.EntityIdExhausted, result.Status);
        Assert.AreEqual(0, notifications);
        SetCursor(harness.Session, nameof(VoxelDevelopmentSession.NextEntityId), next);
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(1, harness.Session.QueryTrainAssemblies().Count);
    }

    [TestMethod]
    public void Identity_survives_contact_loss_but_the_coupled_consist_is_not_rebuilt()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(CompositeDamageStatus.Damaged, harness.Session.Damage(assembly.VehicleIds[0], HollowShellFixture.RearBoundCell, 100).Status);
        Assert.AreEqual(0, harness.Session.QueryTrainAssemblies().Count);
        var loco = harness.Session.QueryComposite(assembly.VehicleIds[0])!;
        Assert.AreEqual(IdentityRetentionStatus.Retained, loco.IdentityRetention);
        Assert.AreEqual(OperationalCondition.Disabled, loco.OperationalCondition);
        Assert.AreEqual(2, harness.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Legal_multi_assembly_and_single_vehicle_files_load_while_stolen_members_do_not()
    {
        using var harness = RunningAt300();
        BreakCoupler(harness.Session);
        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        var saved = harness.Session.Save("Split Legal");
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var previous = File.ReadAllText(path);
        var before = harness.Session.CaptureTrainAuthority();

        using var legal = StaticTrainHarness.Create(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, legal.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(2, legal.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(before, legal.Session.CaptureTrainAuthority());

        File.WriteAllText(path, Mutate(previous, root =>
        {
            var second = root["assemblies"]![1]!;
            second["vehicle_ids"] = root["assemblies"]![0]!["vehicle_ids"]!.DeepClone();
        }));
        Assert.AreEqual(WorldSaveStatus.InvalidDelta, harness.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        File.WriteAllText(path, previous);
    }

    [TestMethod]
    public void Console_targets_one_assembly_and_does_not_broadcast()
    {
        using var harness = RunningAt300();
        BreakCoupler(harness.Session);
        var split = AssertSplit(harness, 96, 87, 4, 4);
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session),
            new CompositeConsoleCommands(harness.Session),
            new RailConsoleCommands(harness.Session));
        dispatcher.Execute("rail start");
        dispatcher.Execute($"rail start assembly {split.Carriage.Id.Value}");
        dispatcher.Execute($"rail query assembly");
        dispatcher.Execute("rail query motion");
        var log = string.Join('\n', harness.Memory.Snapshot().Where(item => item.EventId == ConsoleCommandDispatcher.RailEventId).Select(item => item.Message));
        StringAssert.Contains(log, "UnknownAssembly");
        StringAssert.Contains(log, "MissingPropulsion");
        StringAssert.Contains(log, "active none");
        StringAssert.Contains(log, $"assembly {split.Loco.Id}");
        StringAssert.Contains(log, $"assembly {split.Carriage.Id}");
    }

    [TestMethod]
    public void Coast_deceleration_reload_keeps_speed_and_rejects_illegal_candidates()
    {
        using var harness = RunningAt300();
        BreakCoupler(harness.Session);
        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        var before = AssertSplit(harness, 98, 88.875, 4, 3.5);
        var revision = Revision(harness, StaticTrainFixture.TrackContactId);
        WriteTrackContact(harness, 1);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreEqual(revision, Revision(harness, StaticTrainFixture.TrackContactId));

        WriteTrackContact(harness, 2);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        var adapted = AssertSplit(harness, 98, 88.875, 4, 3.5);
        Assert.AreEqual(before.Carriage.Id, adapted.Carriage.Id);
        Assert.AreNotEqual(revision, Revision(harness, StaticTrainFixture.TrackContactId));

        var after = harness.Session.CaptureTrainAuthority();
        WriteTrackContact(harness, -1);
        Assert.AreEqual(ContentReloadStatus.Rejected, harness.Reloader.Reload().Status);
        Assert.AreEqual(after, harness.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void Queued_stop_on_a_coasting_assembly_keeps_identity_and_round_trips()
    {
        using var harness = RunningAt300();
        BreakCoupler(harness.Session);
        var split = AssertSplit(harness, 96, 87, 4, 4);
        Assert.AreEqual(TrainMotionStatus.Pending, harness.Session.StopTrain(310, split.Carriage.Id).Status);
        var queued = harness.Session.QueryScheduledTrainCommands().Single();
        Assert.AreEqual(split.Carriage.Id, queued.AssemblyId);
        Assert.AreEqual(310UL, queued.AtStep);
        var next = harness.Session.NextEntityId;
        var transactions = harness.Session.TransactionCount;

        var shell = harness.Session.QueryComposite(split.Loco.ActiveLocomotiveId!.Value)!.Parts.First(part => part.Cell.Y == 2);
        Assert.AreEqual(CompositeDamageStatus.Damaged, harness.Session.Damage(split.Loco.ActiveLocomotiveId.Value, shell.Cell, 1).Status);
        var after = AssertSplit(harness, 96, 87, 4, 4);
        Assert.AreEqual(split.Loco.Id, after.Loco.Id);
        Assert.AreEqual(split.Carriage.Id, after.Carriage.Id);
        Assert.AreEqual(next, harness.Session.NextEntityId);
        Assert.AreEqual(transactions + 1, harness.Session.TransactionCount);
        var pending = harness.Session.QueryScheduledTrainCommands().Single();
        Assert.AreEqual(split.Carriage.Id, pending.AssemblyId);
        Assert.AreEqual(310UL, pending.AtStep);

        AssertRoundTrip(harness, "Queued coast stop");
        Assert.IsTrue(harness.Session.AdvanceSimulation(11).Succeeded);
        Assert.AreEqual(311UL, harness.Session.SimulationStep);
        Assert.AreEqual(0, harness.Session.QueryScheduledTrainCommands().Count);
        var stopped = AssertSplit(harness, 96 + 11 * (4.0 / 60.0), 87 + CoastTravel(4, 10), 4, 0);
        Assert.IsFalse(stopped.Carriage.Running);
        AssertRoundTrip(harness, "Executed coast stop");
    }

    [TestMethod]
    public void Dissolving_an_assembly_keeps_its_queued_stop_until_it_is_rejected_at_its_own_position()
    {
        using var harness = RunningAt300();
        var assembly = harness.Session.QueryTrainAssemblies().Single();
        var queued = harness.Session.StopTrain(310, assembly.Id);
        Assert.AreEqual(TrainMotionStatus.Pending, queued.Status);
        Assert.AreEqual(1, harness.Session.QueryScheduledTrainCommands().Count);
        var before = harness.Session.CaptureTrainAuthority();

        Assert.AreEqual(
            CompositeDamageStatus.Damaged,
            harness.Session.Damage(assembly.ActiveLocomotiveId!.Value, HollowShellFixture.RearBoundCell, 100).Status);
        Assert.AreEqual(0, harness.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(assembly.Id, harness.Session.QueryScheduledTrainCommands().Single().AssemblyId);
        Assert.AreNotEqual(before, harness.Session.CaptureTrainAuthority());
        AssertRoundTrip(harness, "Dissolved queued stop");
        Assert.IsTrue(harness.Session.AdvanceSimulation(9).Succeeded);
        Assert.AreEqual(1, harness.Session.QueryScheduledTrainCommands().Count);
        Assert.IsTrue(harness.Session.AdvanceSimulation(2).Succeeded);
        var rejected = harness.Session.QueryEditLog().Single(record => record.Id == queued.EditId);
        Assert.AreEqual(WorldEditState.Rejected, rejected.State);
        Assert.AreEqual(nameof(TrainMotionStatus.UnknownAssembly), rejected.Status);
        Assert.AreEqual(new SimulationPosition(310, 0), rejected.ExecutedAt);
        Assert.AreEqual(311UL, harness.Session.SimulationStep);
        Assert.AreEqual(0, harness.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(0, harness.Session.QueryScheduledTrainCommands().Count);
    }

    [TestMethod]
    public void Split_keeps_queued_stop_on_the_inheritor_and_does_not_broadcast()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var original = harness.Session.QueryTrainAssemblies().Single();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.AreEqual(TrainMotionStatus.Pending, harness.Session.StopTrain(310, original.Id).Status);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);

        BreakCoupler(harness.Session);
        var split = AssertSplit(harness, 96, 87, 4, 4);
        Assert.AreEqual(original.Id, split.Loco.Id);
        var pending = harness.Session.QueryScheduledTrainCommands().Single();
        Assert.AreEqual(split.Loco.Id, pending.AssemblyId);
        Assert.AreNotEqual(split.Carriage.Id, pending.AssemblyId);

        Assert.IsTrue(harness.Session.AdvanceSimulation(11).Succeeded);
        var assemblies = harness.Session.QueryTrainAssemblies();
        var loco = assemblies.Single(item => item.Id == split.Loco.Id);
        var carriage = assemblies.Single(item => item.Id == split.Carriage.Id);
        AssertWorldContact(loco.Vehicles[0].FrontContact, 96 + 10 * (4.0 / 60.0), 2, 16);
        AssertWorldContact(carriage.Vehicles[0].FrontContact, 87 + CoastTravel(4, 11), 2, 16);
        Assert.AreEqual(0.0, loco.Speed, Tolerance);
        Assert.AreEqual(4 - 11.0 / 60.0, carriage.Speed, Tolerance);
        Assert.IsFalse(loco.Running);
        Assert.IsFalse(carriage.Running);
        Assert.AreEqual(0, harness.Session.QueryScheduledTrainCommands().Count);
    }

    [TestMethod]
    public void Destroying_control_drops_traction_and_coasts_with_round_trip()
    {
        using var harness = RunningAt300();
        var before = harness.Session.QueryTrainAssemblies().Single();
        var locoId = before.ActiveLocomotiveId!.Value;
        Assert.AreEqual(CompositeDamageStatus.Damaged, harness.Session.Damage(locoId, new LocalVoxelCoord(3, 0, 1), 100).Status);
        Assert.AreEqual(OperationalCondition.Disabled, harness.Session.QueryComposite(locoId)!.OperationalCondition);
        Assert.AreEqual(IdentityRetentionStatus.Retained, harness.Session.QueryComposite(locoId)!.IdentityRetention);
        var coasting = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(before.Id, coasting.Id);
        Assert.IsNull(coasting.ActiveLocomotiveId);
        Assert.IsFalse(coasting.Running);
        Assert.AreEqual(4.0, coasting.Speed, Tolerance);
        AssertWorldContact(coasting.Vehicles[0].FrontContact, 96, 2, 16);
        Assert.AreEqual(2, coasting.VehicleIds.Count);
        Assert.AreEqual(TrainMotionStatus.MissingControl, harness.Session.StartTrain(RailHeading.AlongAlignment, assemblyId: coasting.Id).Status);

        Assert.IsTrue(harness.Session.AdvanceSimulation(30).Succeeded);
        var after = harness.Session.QueryTrainAssemblies().Single();
        AssertWorldContact(after.Vehicles[0].FrontContact, 97.875, 2, 16);
        Assert.AreEqual(3.5, after.Speed, Tolerance);
        Assert.IsFalse(after.Running);
        Assert.IsNull(after.ActiveLocomotiveId);
        AssertRoundTrip(harness, "Missing control coast");

        var saved = harness.Session.Save("Illegal running control");
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var previous = File.ReadAllText(path);
        File.WriteAllText(path, Mutate(previous, root =>
        {
            root["assemblies"]![0]!["running"] = true;
            root["assemblies"]![0]!["active_locomotive_id"] = locoId.Value;
        }));
        Assert.AreEqual(WorldSaveStatus.InvalidDelta, harness.Session.Load(saved.SaveId!.Value).Status);
        File.WriteAllText(path, previous);

        WriteTrackContact(harness, 2);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        var adapted = harness.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(after.Id, adapted.Id);
        Assert.AreEqual(3.5, adapted.Speed, Tolerance);
        AssertWorldContact(adapted.Vehicles[0].FrontContact, 97.875, 2, 16);
    }

    [TestMethod]
    public void Rebuild_does_not_assemble_unrelated_manufactured_vehicles()
    {
        using var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        var original = harness.Session.QueryTrainAssemblies().Single();
        var manufactured = harness.Session.Manufacture(StaticTrainFixture.CarriageId, StaticTrainFixture.FacingPositiveX(200));
        Assert.AreEqual(ConstructionStatus.Valid, manufactured.Status);
        var extraId = manufactured.VehicleId!.Value;
        Assert.AreEqual(1, harness.Session.QueryTrainAssemblies().Count);
        var next = harness.Session.NextEntityId;
        var extraCell = harness.Session.QueryComposite(extraId)!.Parts.First(part => part.Cell.Y == 2).Cell;
        var locoShell = harness.Session.QueryComposite(original.ActiveLocomotiveId!.Value)!.Parts.First(part => part.Cell.Y == 2).Cell;

        Assert.AreEqual(
            CompositeDamageStatus.Damaged,
            harness.Session.Damage(new[]
            {
                new CompositeDamageTarget(original.ActiveLocomotiveId.Value, locoShell, 1),
                new CompositeDamageTarget(extraId, extraCell, 1)
            }).Status);
        var assemblies = harness.Session.QueryTrainAssemblies();
        Assert.AreEqual(1, assemblies.Count);
        Assert.AreEqual(original.Id, assemblies[0].Id);
        Assert.AreEqual(original.VehicleIds.Count, assemblies[0].VehicleIds.Count);
        Assert.IsFalse(assemblies[0].VehicleIds.Contains(extraId));
        Assert.AreEqual(3, harness.Session.QueryComposites().Count);
        Assert.AreEqual(next, harness.Session.NextEntityId);
        Assert.AreEqual(TrainMotionStatus.Started, harness.Session.StartTrain(RailHeading.AlongAlignment).Status);
    }

    private static StaticTrainHarness RunningAt300()
    {
        var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        return harness;
    }

    private static void BreakCoupler(VoxelDevelopmentSession session)
    {
        var assembly = session.QueryTrainAssemblies().Single();
        Assert.IsTrue(session.Damage(assembly.ActiveLocomotiveId!.Value, assembly.Connections[0].FromCell, 100).Succeeded);
    }

    private static (TrainAssemblyQuery Loco, TrainAssemblyQuery Carriage) AssertSplit(
        StaticTrainHarness harness,
        double locoFront,
        double carriageFront,
        double locoSpeed,
        double carriageSpeed)
    {
        var assemblies = harness.Session.QueryTrainAssemblies();
        Assert.AreEqual(2, assemblies.Count);
        var loco = assemblies.Single(item => item.ActiveLocomotiveId is not null);
        var carriage = assemblies.Single(item => item.ActiveLocomotiveId is null);
        AssertWorldContact(loco.Vehicles[0].FrontContact, locoFront, 2, 16);
        AssertWorldContact(carriage.Vehicles[0].FrontContact, carriageFront, 2, 16);
        Assert.AreEqual(locoSpeed, loco.Speed, Tolerance);
        Assert.AreEqual(carriageSpeed, carriage.Speed, Tolerance);
        Assert.IsTrue(loco.Running);
        Assert.IsFalse(carriage.Running);
        Assert.AreEqual(1, loco.VehicleIds.Count);
        Assert.AreEqual(1, carriage.VehicleIds.Count);
        return (loco, carriage);
    }

    private static void AssertWorldContact(WorldContactQuery contact, double x, double y, double z)
    {
        Assert.AreEqual(x, contact.X, Tolerance, "x");
        Assert.AreEqual(y, contact.Y, Tolerance, "y");
        Assert.AreEqual(z, contact.Z, Tolerance, "z");
    }

    private static void AssertRoundTrip(StaticTrainHarness harness, string name)
    {
        var before = harness.Session.CaptureTrainAuthority();
        var hash = WorldSaveAuthority.Fingerprint(harness.Session);
        var saved = harness.Session.Save(name);
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        using var reader = StaticTrainHarness.Create(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, reader.Session.CaptureTrainAuthority());
        Assert.AreEqual(hash, WorldSaveAuthority.Fingerprint(reader.Session));
        Assert.AreEqual(WorldSaveStatus.Loaded, harness.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(hash, WorldSaveAuthority.Fingerprint(harness.Session));
    }

    private static double CoastTravel(double speed, int steps)
    {
        var remaining = speed;
        var total = 0.0;
        for (var step = 0; step < steps; step++)
        {
            StraightTrainMotion.IntegrateCoast(remaining, 1, SimulationClock.StepDuration, out remaining, out var dx);
            total += dx;
        }

        return total;
    }

    private static void SetCursor(VoxelDevelopmentSession session, string name, ulong value) =>
        typeof(VoxelDevelopmentSession).GetProperty(name)!.SetValue(session, value);

    private static string Revision(StaticTrainHarness harness, VoxelDefinitionId id)
    {
        Assert.IsTrue(harness.Reloader.Catalog.Current!.TryGet(id, out var entry));
        return entry.SemanticRevision;
    }

    private static void WriteTrackContact(StaticTrainHarness harness, double coastDeceleration)
    {
        var behaviors = coastDeceleration < 0
            ? "[{\"type\":\"track_contact\",\"coast_deceleration\":-1}]"
            : $"[{{ \"type\":\"track_contact\",\"coast_deceleration\":{coastDeceleration.ToString(CultureInfo.InvariantCulture)} }}]";
        foreach (var (file, id, name, description, tags) in new[]
        {
            ("voxels/track-contact.voxels.json", "track-contact", "开发轨道接触件", "切片 4.2 轨道接触行为零件，绑定后接触锚点。能力由已注册 track_contact 行为推导。", "[]"),
            ("voxels/locomotive-core.voxels.json", "locomotive-core", "开发车头核心", "切片 4.2 车头身份核心。", "[\"identity_core\"]"),
            ("voxels/carriage-core.voxels.json", "carriage-core", "开发车厢核心", "切片 4.2 车厢身份核心，带 identity_core 标签和轨道接触行为。没有动力或控制。", "[\"identity_core\"]")
        })
        {
            harness.Source.AddVoxelFile("core", file, ContentFixtures.GoodsFile(
                ContentFixtures.Good(id, name, description, value: 10, tagsJson: tags, behaviorsJson: behaviors)));
        }
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }
}

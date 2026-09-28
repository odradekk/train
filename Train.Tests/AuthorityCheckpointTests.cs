using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class AuthorityCheckpointTests
{
    private const string GoldenDigest = "0D07A0C6A5820633DDE28DFC308E2A9FE2C70A9F5073DA694D98A03E09D3D8E9";

    private const string Golden =
        """{"authority_format_version":1,"seed":42,"generator_version":"fixture-v1","bounds":{"x":4,"y":4,"z":4},"chunk_size":16,"next_entity_id":8,"transaction_count":2,"simulation_step":3,"next_edit_id":4,"boundary_sequence":1,"simulation_fault":null,"world":[{"x":1,"y":0,"z":2,"definition_id":"core:a","orientation":"px_py","durability":75,"runtime_id":9},{"x":4,"y":4,"z":4,"definition_id":"core:a","orientation":"nz_py","durability":100}],"save_local_definitions":[{"revision":"local","id":"core:shell","name":"shell","description":"local shell","archetype_id":"core:arch","size_class":"small","voxels":[{"x":0,"y":1,"z":0,"definition_id":"core:part","orientation":"px_py"}],"contacts":[{"x":1.5,"y":-0.75,"z":0,"bound_x":1,"bound_y":0,"bound_z":1}]}],"vehicles":[{"id":1,"definition_id":"core:shell","last_used_revision":"def","archetype_id":"core:arch","last_used_archetype_revision":"arch","size_class":"small","save_local_revision":"local","transform":{"origin_x":0.1,"origin_y":-0,"origin_z":1.7976931348623157E+308,"right_x":1,"right_y":0,"right_z":0,"up_x":0,"up_y":1,"up_z":5E-324},"parts":[{"id":2,"x":0,"y":1,"z":0,"definition_id":"core:part","orientation":"px_py","durability":100,"last_used_revision":"part"},{"id":5,"x":1,"y":0,"z":0,"definition_id":"core:part","orientation":"nz_py","durability":80,"last_used_revision":"part"}],"contacts":[{"x":1.5,"y":-0.75,"z":1.0000000000000002,"bound_x":1,"bound_y":0,"bound_z":1},{"x":0,"y":0,"z":0,"bound_x":7,"bound_y":0,"bound_z":1}]}],"railway":{"sleeper_count":1,"segments":[{"id":1,"alignment":{"start_x":0,"start_y":0,"start_z":0,"end_x":2,"end_y":0,"end_z":0},"rail_volumes":[{"min_x":0,"min_y":0,"min_z":0,"max_x":1,"max_y":1,"max_z":1}],"sleeper_volumes":[]}],"connections":[{"from_segment_id":1,"from_end":"end","to_segment_id":2,"to_end":"start"},{"from_segment_id":2,"from_end":"end","to_segment_id":3,"to_end":"start"}],"occupancy":[{"x":65,"y":2,"z":19,"supports":[{"segment_id":1,"world_x_start":1.5,"world_x_end":2.5,"along_start":0,"along_end":1}]}],"destroyed":[{"x":65,"y":2,"z":19}]},"assemblies":[{"id":7,"active_locomotive_id":1,"vehicle_ids":[1,4],"connections":[{"from_vehicle_id":1,"from_part_id":2,"from_x":0,"from_y":1,"from_z":0,"to_vehicle_id":4,"to_part_id":3,"to_x":1,"to_y":0,"to_z":1}],"reference_segment_id":1,"distance":3.141592653589793,"heading":"along","speed":0,"running":false,"halt_reason":"forward_gap"}],"pending":[{"edit_id":1,"accepted_step":0,"accepted_sequence":0,"target_step":1,"edit":{"input_format_version":1,"kind":"voxel.add","position":{"x":60,"y":3,"z":20},"definition_id":"core:ferrous-ore","orientation":"px_py"}}]}""";

    [TestMethod]
    public void Handwritten_golden_matches_an_independent_digest_and_the_encoder()
    {
        var independent = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Golden)));
        Assert.AreEqual(2622, Encoding.UTF8.GetByteCount(Golden));
        Assert.AreEqual(GoldenDigest, independent);
        Assert.AreEqual(GoldenDigest, AuthorityCodec.Digest(Golden));

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.AreEqual(Golden, AuthorityCodec.Encode(UnorderedGolden()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.IsTrue(AuthorityCodec.TryDecode(Golden, out var decoded, out var status));
        Assert.AreEqual(AuthorityDecodeStatus.Decoded, status);
        Assert.AreEqual(Golden, AuthorityCodec.Encode(decoded));
        var swapped = Golden.Replace(
            WorldVoxel(1, 0, 2, 75, 9) + "," + WorldVoxel(4, 4, 4, 100, null),
            WorldVoxel(4, 4, 4, 100, null) + "," + WorldVoxel(1, 0, 2, 75, 9),
            StringComparison.Ordinal);
        Assert.AreNotEqual(Golden, swapped);
        Assert.IsTrue(AuthorityCodec.TryDecode(swapped, out var normalized, out status));
        Assert.AreEqual(AuthorityDecodeStatus.Decoded, status);
        Assert.AreEqual(Golden, AuthorityCodec.Encode(normalized));
    }

    [TestMethod]
    public void Ordered_relations_keep_order_and_non_finite_or_unknown_text_is_rejected()
    {
        var swappedContacts = Golden.Replace(
            "\"z\":1.0000000000000002,\"bound_x\":1,\"bound_y\":0,\"bound_z\":1},{\"x\":0",
            "\"z\":0,\"bound_x\":7,\"bound_y\":0,\"bound_z\":1},{\"x\":1.5",
            StringComparison.Ordinal);
        Assert.AreNotEqual(Golden, AuthorityCodec.Encode(DecodedWithSwappedContacts()));
        Assert.AreNotEqual(Golden, swappedContacts);

        var swappedVehicles = Golden.Replace("\"vehicle_ids\":[1,4]", "\"vehicle_ids\":[4,1]", StringComparison.Ordinal);
        Assert.IsTrue(AuthorityCodec.TryDecode(swappedVehicles, out var vehicles, out _));
        Assert.AreEqual(swappedVehicles, AuthorityCodec.Encode(vehicles));

        AssertDecode(Golden.Replace("\"authority_format_version\":1", "\"authority_format_version\":2", StringComparison.Ordinal), AuthorityDecodeStatus.UnknownFormat);
        AssertDecode(Golden.Replace("\"authority_format_version\":1,", "\"authority_format_version\":1,\"camera\":1,", StringComparison.Ordinal), AuthorityDecodeStatus.UnknownFormat);
        AssertDecode(Golden.Replace("\"seed\":42,", "\"seed\":42,\"seed\":43,", StringComparison.Ordinal), AuthorityDecodeStatus.UnknownFormat);
        AssertDecode(Golden.Replace("\"seed\":42,", "", StringComparison.Ordinal), AuthorityDecodeStatus.Invalid);
        AssertDecode(Golden.Replace("\"origin_x\":0.1", "\"origin_x\":NaN", StringComparison.Ordinal), AuthorityDecodeStatus.Invalid);
        AssertDecode(Golden.Replace("\"origin_x\":0.1", "\"origin_x\":1e400", StringComparison.Ordinal), AuthorityDecodeStatus.Invalid);
        AssertDecode(Golden.Replace("\"vehicles\":[{\"id\":1", "\"vehicles\":[{\"id\":0", StringComparison.Ordinal), AuthorityDecodeStatus.Invalid);
        AssertDecode(Golden.Replace("\"halt_reason\":\"forward_gap\"", "\"halt_reason\":\"parked\"", StringComparison.Ordinal), AuthorityDecodeStatus.UnknownFormat);
        AssertDecode(Golden.Replace("\"assemblies\":[{\"id\":7", "\"assemblies\":[{\"id\":0", StringComparison.Ordinal), AuthorityDecodeStatus.Invalid);

        var infinite = UnorderedGolden();
        var vehicle = infinite.Vehicles[0];
        var broken = infinite with
        {
            Vehicles = new[]
            {
                vehicle with { Transform = vehicle.Transform with { OriginX = double.PositiveInfinity } }
            }
        };
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => AuthorityCodec.Encode(broken));
    }

    [TestMethod]
    public void Field_catalog_covers_the_golden_and_names_the_excluded_surface()
    {
        Assert.IsTrue(AuthorityFieldCatalog.TopLevel.SequenceEqual(RootNames(Golden)));
        AssertNames(Golden);
        Assert.IsFalse(AuthorityFieldCatalog.Names.Intersect(AuthorityFieldCatalog.Excluded).Any());
    }

    [TestMethod]
    public void Diagnosis_names_the_earliest_checkpoint_object_and_distinguishes_faults()
    {
        var halt = CompareText(Golden, Golden.Replace("\"halt_reason\":\"forward_gap\"", "\"halt_reason\":\"support_lost\"", StringComparison.Ordinal));
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, halt.Kind);
        Assert.AreEqual(3, halt.CheckpointIndex);
        Assert.AreEqual("fixture-input", halt.Input);
        StringAssert.Contains(halt.Field, "halt_reason");
        StringAssert.Contains(halt.Detail, "合法故障停车");

        var faulted = Golden.Replace(
            "\"simulation_fault\":null",
            "\"simulation_fault\":{\"edit_id\":1,\"kind\":\"voxel.add\",\"step\":3,\"sequence\":1,\"exception_type\":\"InvalidOperationException\",\"message\":\"injected\"}",
            StringComparison.Ordinal);
        var fault = CompareText(Golden, faulted);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, fault.Kind);
        StringAssert.Contains(fault.Field, "simulation_fault");
        StringAssert.Contains(fault.Detail, "内部不变量故障");

        Assert.IsTrue(AuthorityCodec.TryDecode(Golden, out var model, out _));
        var missing = model with { Vehicles = Array.Empty<AuthorityVehicle>() };
        var gone = CompareText(Golden, AuthorityCodec.Encode(missing));
        Assert.AreEqual(AuthorityDifferenceKind.MissingObject, gone.Kind);
        StringAssert.Contains(gone.Object, "vehicle 1");
    }

    [TestMethod]
    public void Repeated_formal_sequence_matches_and_context_mismatches_are_classified()
    {
        using var content = PlaygroundHarness.Content();
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.AuthorityManufactureFractureId)!;
        var first = AuthorityValidation.Run(sequence, content.Catalog, content.Composites, content.Store);
        var second = AuthorityValidation.Run(sequence, content.Catalog, content.Composites, content.Store);
        Assert.IsTrue(first.Passed, string.Join(Environment.NewLine, first.Lines()));
        Assert.IsTrue(second.Passed, string.Join(Environment.NewLine, second.Lines()));
        Assert.IsTrue(AuthorityComparison.Compare(first.Observation!, second.Observation!).Equal);
        Assert.AreEqual(sequence.Steps.Count + 1, first.Observation!.Checkpoints.Count);
        Assert.AreEqual("establish", first.Observation.Checkpoints[0].Input);
        StringAssert.Contains(first.Observation.Checkpoints[^1].Canonical, "\"simulation_fault\":null");
        Assert.IsFalse(first.Observation.Checkpoints[^1].Canonical.Contains("save_id", StringComparison.Ordinal));
        AssertNames(first.Observation.Checkpoints[^1].Canonical);

        using var train = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        using var empty = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var source = AuthorityComparison.Compare(AuthorityCodec.Observe(train.Session), AuthorityCodec.Observe(empty.Session));
        Assert.AreEqual(AuthorityDifferenceKind.SourceMismatch, source.Kind);
        StringAssert.Contains(source.Detail, "来源");

        var observed = AuthorityCodec.Observe(train.Session, "build-a");
        Assert.AreEqual(AuthorityDifferenceKind.VersionMismatch, AuthorityComparison.Compare(observed, observed with { Context = observed.Context with { FormatVersion = 2 } }).Kind);
        Assert.AreEqual(
            AuthorityDifferenceKind.EnvironmentMismatch,
            AuthorityComparison.Compare(observed, observed with { Context = observed.Context with { Architecture = "Arm" } }).Kind);
        Assert.AreEqual(
            AuthorityDifferenceKind.EnvironmentMismatch,
            AuthorityComparison.Compare(observed, observed with { Context = observed.Context with { BuildId = "build-b" } }).Kind);
        Assert.AreEqual(
            AuthorityDifferenceKind.EnvironmentMismatch,
            AuthorityComparison.Compare(observed, observed with { Context = observed.Context with { BuildId = null } }).Kind);
        Assert.AreEqual(
            AuthorityDifferenceKind.EnvironmentMismatch,
            AuthorityComparison.Compare(observed, observed with { Context = observed.Context with { OperatingSystem = "other-os" } }).Kind);
        Assert.IsTrue(AuthorityComparison.Compare(
            observed,
            observed with { Context = observed.Context with { OperatingSystem = "Microsoft Windows 10.0.1", SaveId = "save" } }).Equal);
        var swappedWorld = Golden.Replace(
            WorldVoxel(1, 0, 2, 75, 9) + "," + WorldVoxel(4, 4, 4, 100, null),
            WorldVoxel(4, 4, 4, 100, null) + "," + WorldVoxel(1, 0, 2, 75, 9),
            StringComparison.Ordinal);
        var orderOnly = CompareText(Golden, swappedWorld);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, orderOnly.Kind);
        Assert.AreEqual("canonical", orderOnly.Field);
        StringAssert.Contains(string.Join('\n', observed.Lines()), AuthorityFieldCatalog.Guarantee);
    }

    [TestMethod]
    public void Content_adaptation_is_not_reported_as_a_determinism_failure()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var before = AuthorityCodec.Observe(playground.Session);
        WriteRail(playground, "钢轨显示");
        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);
        var display = AuthorityCodec.Observe(playground.Session);
        Assert.AreEqual(before.Context.ContentDigest, display.Context.ContentDigest);
        Assert.IsTrue(AuthorityComparison.Compare(before, display).Equal, AuthorityComparison.Compare(before, display).Detail);

        WriteRail(playground, "钢轨", 60);
        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);
        var adapted = AuthorityCodec.Observe(playground.Session);
        var difference = AuthorityComparison.Compare(before, adapted);
        Assert.AreEqual(AuthorityDifferenceKind.ContentAdaptation, difference.Kind);
        StringAssert.Contains(difference.Detail, "内容适配");
        Assert.AreNotEqual(before.Context.ContentDigest, adapted.Context.ContentDigest);
    }

    [TestMethod]
    public void Input_order_and_a_live_field_change_locate_the_earliest_difference()
    {
        using var content = PlaygroundHarness.Content();
        var left = AuthorityValidation.Run(Adds(new WorldVoxelCoord(10, 10, 10), new WorldVoxelCoord(11, 10, 10)), content.Catalog, content.Composites);
        var right = AuthorityValidation.Run(Adds(new WorldVoxelCoord(12, 10, 10), new WorldVoxelCoord(11, 10, 10)), content.Catalog, content.Composites);
        Assert.IsTrue(left.Passed, string.Join(Environment.NewLine, left.Lines()));
        Assert.IsTrue(right.Passed, string.Join(Environment.NewLine, right.Lines()));
        var order = AuthorityComparison.Compare(left.Observation!, right.Observation!);
        Assert.AreEqual(AuthorityDifferenceKind.MissingObject, order.Kind);
        Assert.AreEqual(1, order.CheckpointIndex);
        StringAssert.Contains(order.Input, "voxel.add");
        StringAssert.Contains(order.Object, "10,10,10");

        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var clean = AuthorityCodec.Document(AuthorityCodec.Capture(playground.Session));
        Assert.AreEqual(WorldCommandStatus.Damaged, playground.Session.Damage(StraightRailFixture.LeftRailSample, 1).Status);
        var damaged = AuthorityCodec.Document(AuthorityCodec.Capture(playground.Session));
        var cursor = CompareText(clean.Canonical, damaged.Canonical);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, cursor.Kind);
        Assert.AreEqual("next_edit_id", cursor.Field);
    }

    [TestMethod]
    public void Manufacture_refit_and_fracture_allocate_ids_only_on_success_and_survive_save()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var established = AuthorityCodec.Document(AuthorityCodec.Capture(session));
        Assert.AreEqual("Paused", session.PauseSimulation().Status.ToString());
        Assert.AreEqual("Resumed", session.ResumeSimulation().Status.ToString());
        Assert.AreEqual("Paused", session.PauseSimulation().Status.ToString());
        Assert.AreEqual(established.Digest, AuthorityCodec.Document(AuthorityCodec.Capture(session)).Digest);

        var saved = session.Save("Authority");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var afterSave = AuthorityCodec.Document(AuthorityCodec.Capture(session));
        Assert.AreEqual(established.Canonical, afterSave.Canonical);
        Assert.AreEqual(established.Digest, afterSave.Digest);
        Assert.IsFalse(afterSave.Canonical.Contains(saved.SaveId!.Value.ToString("D"), StringComparison.OrdinalIgnoreCase));

        var beforeShell = session.NextEntityId;
        Assert.AreEqual("Valid", Submit(session, new VehicleManufactureEdit(HollowShellFixture.DefinitionId, PlaygroundSequences.AuthorityShellPose)));
        var shell = session.Vehicles.Single(item => item.Id.Value == beforeShell);
        var addedBefore = session.NextEntityId;
        Assert.AreEqual("Valid", PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)).Apply(session));
        var removedId = session.Vehicles.Single(item => item.Id == shell.Id).PartAt(HollowShellRefit.FirstAdd)!.Id;
        var beforeRejection = session.NextEntityId;
        Assert.AreEqual("EnvelopeExceeded", PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId)).Apply(session));
        Assert.AreEqual(beforeRejection, session.NextEntityId);
        Assert.AreEqual("Valid", PlaygroundOperation.RefitLatest(PlaygroundRefit.Remove(HollowShellRefit.FirstAdd)).Apply(session));
        Assert.IsFalse(session.Vehicles.SelectMany(item => item.Parts).Any(part => part.Id == removedId));
        var beforeReuse = session.NextEntityId;
        Assert.AreEqual("Valid", PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)).Apply(session));
        var reused = session.Vehicles.Single(item => item.Id == shell.Id).PartAt(HollowShellRefit.FirstAdd)!.Id;
        Assert.AreEqual(beforeReuse, reused.Value);
        Assert.AreNotEqual(removedId, reused);
        Assert.IsTrue(reused.Value >= addedBefore);

        var beforeBridge = session.NextEntityId;
        Assert.AreEqual("Valid", Submit(session, new VehicleManufactureEdit(BridgeFixture.DefinitionId, PlaygroundSequences.AuthorityBridgePose)));
        var bridge = session.Vehicles.Single(item => item.Id.Value == beforeBridge);
        var beforeInvalid = session.NextEntityId;
        Assert.AreEqual("InvalidDamage", PlaygroundOperation.DamageLatest(BridgeFixture.BridgeCell, 0).Apply(session));
        Assert.AreEqual(beforeInvalid, session.NextEntityId);

        var left = bridge.Parts.Where(part => BridgeFixture.IsLeftCell(part.Cell)).Min(part => part.Id.Value);
        var right = bridge.Parts.Where(part => BridgeFixture.IsRightCell(part.Cell)).Min(part => part.Id.Value);
        var bridgePart = bridge.PartAt(BridgeFixture.BridgeCell)!.Id;
        var seed = session.Identity.Seed;
        var transaction = session.TransactionCount + 1;
        Assert.AreEqual(StraightRailFixture.Seed, seed);
        for (ulong extra = 0; extra < 8; extra++)
            SplitMix64.FractureScore(seed ^ extra, transaction, left);
        var primary = SplitMix64.SelectPrimaryCandidate(seed, transaction, new[] { right, left });
        var beforeFracture = session.NextEntityId;
        Assert.AreEqual("Damaged", PlaygroundOperation.DamageLatest(BridgeFixture.BridgeCell, 100).Apply(session));
        Assert.AreEqual(transaction, session.TransactionCount);
        Assert.AreEqual(beforeFracture + 1, session.NextEntityId);
        var kept = session.Vehicles.Single(item => item.Id == bridge.Id);
        var successor = session.Vehicles.Single(item => item.Id.Value == beforeFracture);
        Assert.IsTrue(kept.Parts.Any(part => part.Id.Value == primary));
        var secondary = primary == left ? right : left;
        Assert.IsTrue(successor.Parts.Any(part => part.Id.Value == secondary));
        Assert.AreNotEqual(beforeFracture, left);
        Assert.AreNotEqual(beforeFracture, right);
        Assert.IsFalse(session.Vehicles.SelectMany(item => item.Parts).Any(part => part.Id == bridgePart));
        Assert.IsNull(session.Fault);
        Assert.AreEqual(5, session.QueryComposites().Count);

        var fractured = AuthorityCodec.Document(AuthorityCodec.Capture(session));
        var fractureSave = session.Save("Fracture");
        Assert.AreEqual(WorldSaveStatus.Saved, fractureSave.Status);
        Assert.AreEqual(fractured.Digest, AuthorityCodec.Document(AuthorityCodec.Capture(session)).Digest);
        var loaded = new VoxelDevelopmentSession(playground.Catalog, VoxelWorldIdentity.Slice4, playground.Store, playground.Composites);
        try
        {
            Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Load(fractureSave.SaveId!.Value).Status);
            Assert.AreEqual(fractured.Canonical, AuthorityCodec.Encode(AuthorityCodec.Capture(loaded)));
            Assert.AreEqual(session.NextEntityId, loaded.NextEntityId);
            Assert.IsNotNull(loaded.Vehicles.Single(item => item.Id.Value == beforeFracture));
            Assert.IsNull(loaded.Fault);
        }
        finally
        {
            loaded.Detach();
        }
    }

    [TestMethod]
    public void Exhausted_entity_cursor_rejects_manufacture_without_consuming_the_cursor()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        var saved = session.Save("Exhausted");
        var path = playground.Store.PathFor(saved.SaveId!.Value);
        var token = $"\"next_entity_id\":{session.NextEntityId.ToString(CultureInfo.InvariantCulture)}";
        var json = File.ReadAllText(path);
        Assert.AreEqual(1, Count(json, token));
        File.WriteAllText(path, json.Replace(token, $"\"next_entity_id\":{ulong.MaxValue.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal));
        var loaded = new VoxelDevelopmentSession(playground.Catalog, VoxelWorldIdentity.Slice4, playground.Store, playground.Composites);
        try
        {
            Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Load(saved.SaveId!.Value).Status);
            Assert.AreEqual(ulong.MaxValue, loaded.NextEntityId);
            Assert.AreEqual(
                "EntityIdExhausted",
                Submit(loaded, new VehicleManufactureEdit(HollowShellFixture.DefinitionId, PlaygroundSequences.AuthorityShellPose)));
            Assert.AreEqual(ulong.MaxValue, loaded.NextEntityId);
        }
        finally
        {
            loaded.Detach();
        }
    }

    [TestMethod]
    public void Legal_halt_leaves_simulation_fault_empty_and_an_internal_fault_is_a_different_field()
    {
        using var content = PlaygroundHarness.Content();
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.StraightRailRunAndHaltId)!;
        var report = PlaygroundValidation.Run(sequence, content.Catalog, content.Composites, content.Store);
        Assert.IsTrue(report.Passed, string.Join(Environment.NewLine, report.Lines()));

        using var halted = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        Assert.AreEqual("Started", PlaygroundOperation.Start().Apply(halted.Session));
        Assert.AreEqual("Removed", PlaygroundOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Apply(halted.Session));
        Assert.AreEqual("Advanced", PlaygroundOperation.Advance(2000).Apply(halted.Session));
        Assert.AreEqual(TrainHaltReason.ForwardGap, halted.Session.QueryTrainAssemblies().Single().HaltReason);
        Assert.IsNull(halted.Session.Fault);
        var haltText = AuthorityCodec.Encode(AuthorityCodec.Capture(halted.Session));
        StringAssert.Contains(haltText, "\"halt_reason\":\"forward_gap\"");
        StringAssert.Contains(haltText, "\"simulation_fault\":null");

        using var faulted = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        faulted.Session.EditFaultProbe = _ => throw new InvalidOperationException("injected");
        PlaygroundOperation.Submit(new VoxelAddEdit(new WorldVoxelCoord(10, 10, 10), PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity)).Apply(faulted.Session);
        faulted.Session.EditFaultProbe = null;
        Assert.AreEqual("InvalidOperationException", faulted.Session.Fault!.ExceptionType);
        Assert.AreEqual(TrainHaltReason.None, faulted.Session.QueryTrainAssemblies().Single().HaltReason);
        var faultText = AuthorityCodec.Encode(AuthorityCodec.Capture(faulted.Session));
        StringAssert.Contains(faultText, "\"exception_type\":\"InvalidOperationException\"");
        StringAssert.Contains(faultText, "\"halt_reason\":\"none\"");
        Assert.AreNotEqual(haltText, faultText);
    }

    private static AuthorityStateModel UnorderedGolden()
    {
        var firstContact = new AuthorityContact(1.5, -0.75, 1.0000000000000002, 1, 0, 1);
        var secondContact = new AuthorityContact(0, 0, 0, 7, 0, 1);
        var localContact = new AuthorityContact(1.5, -0.75, 0, 1, 0, 1);
        var partTwo = new AuthorityPart(2, 0, 1, 0, "core:part", "px_py", 100, "part");
        var partFive = new AuthorityPart(5, 1, 0, 0, "core:part", "nz_py", 80, "part");
        return new AuthorityStateModel(
            42,
            "fixture-v1",
            4,
            4,
            4,
            16,
            8,
            2,
            3,
            4,
            1,
            null,
            new[]
            {
                new AuthorityVoxel(4, 4, 4, "core:a", "nz_py", 100, null),
                new AuthorityVoxel(1, 0, 2, "core:a", "px_py", 75, 9)
            },
            new[]
            {
                new AuthorityLocalDefinition(
                    "local",
                    "core:shell",
                    "shell",
                    "local shell",
                    "core:arch",
                    "small",
                    new[] { new AuthorityLocalVoxel(0, 1, 0, "core:part", "px_py") },
                    new[] { localContact })
            },
            new[]
            {
                new AuthorityVehicle(
                    1,
                    "core:shell",
                    "def",
                    "core:arch",
                    "arch",
                    "small",
                    "local",
                    new AuthorityTransform(0.1, double.NegativeZero, double.MaxValue, 1, 0, 0, 0, 1, double.Epsilon),
                    new[] { partFive, partTwo },
                    new[] { firstContact, secondContact })
            },
            new AuthorityRailway(
                1,
                new[]
                {
                    new AuthoritySegment(
                        1,
                        new AuthorityAlignment(0, 0, 0, 2, 0, 0),
                        new[] { new AuthorityVolume(0, 0, 0, 1, 1, 1) },
                        Array.Empty<AuthorityVolume>())
                },
                new[]
                {
                    new AuthorityLink(2, "end", 3, "start"),
                    new AuthorityLink(1, "end", 2, "start")
                },
                new[]
                {
                    new AuthorityOccupancy(65, 2, 19, new[] { new AuthoritySupport(1, 1.5, 2.5, 0, 1) })
                },
                new[] { new AuthorityCell(65, 2, 19) }),
            new[]
            {
                new AuthorityAssembly(
                    7,
                    1,
                    new ulong[] { 1, 4 },
                    new[] { new AuthorityCoupler(1, 2, 0, 1, 0, 4, 3, 1, 0, 1) },
                    1,
                    Math.PI,
                    "along",
                    0,
                    false,
                    "forward_gap")
            },
            new[]
            {
                new AuthorityPending(
                    1,
                    0,
                    0,
                    1,
                    "{\"input_format_version\":1,\"kind\":\"voxel.add\",\"position\":{\"x\":60,\"y\":3,\"z\":20},\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\"}")
            });
    }

    private static AuthorityStateModel DecodedWithSwappedContacts()
    {
        Assert.IsTrue(AuthorityCodec.TryDecode(Golden, out var model, out _));
        var vehicle = model.Vehicles[0];
        var contacts = vehicle.Contacts.Reverse().ToArray();
        return model with { Vehicles = new[] { vehicle with { Contacts = contacts } } };
    }

    private static PlaygroundSequence Adds(WorldVoxelCoord first, WorldVoxelCoord second) =>
        new(
            "authority-input-order",
            PlaygroundConfigurations.StraightRailEmptyId,
            new[]
            {
                new PlaygroundValidationStep(
                    PlaygroundOperation.Submit(new VoxelAddEdit(first, PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity)),
                    "Added",
                    Array.Empty<PlaygroundCheck>()),
                new PlaygroundValidationStep(
                    PlaygroundOperation.Submit(new VoxelAddEdit(second, PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity)),
                    "Added",
                    Array.Empty<PlaygroundCheck>())
            });

    private static AuthorityDifference CompareText(string expected, string actual)
    {
        var context = new AuthorityContext(
            1,
            null,
            new string('A', 64),
            Array.Empty<string>(),
            "fixed_configuration",
            "fixture",
            1,
            1,
            "fixture-v1",
            "Microsoft Windows",
            "X64",
            "runtime",
            AuthorityFieldCatalog.Guarantee,
            null);
        AuthorityCheckpoint Point(string text) => new(3, "fixture-input", new SimulationPosition(4, 5), text, AuthorityCodec.Digest(text));
        return AuthorityComparison.Compare(
            new AuthorityObservation(context, new[] { Point(expected) }),
            new AuthorityObservation(context, new[] { Point(actual) }));
    }

    private static void AssertDecode(string json, AuthorityDecodeStatus expected)
    {
        Assert.IsFalse(AuthorityCodec.TryDecode(json, out _, out var status));
        Assert.AreEqual(expected, status);
    }

    private static void AssertNames(string canonical)
    {
        Assert.IsTrue(AuthorityFieldCatalog.TopLevel.SequenceEqual(RootNames(canonical)));
        var names = PropertyNames(canonical);
        var unknown = names.Where(name => !AuthorityFieldCatalog.Names.Contains(name)).ToArray();
        Assert.AreEqual(0, unknown.Length, string.Join(",", unknown));
        Assert.IsFalse(names.Overlaps(AuthorityFieldCatalog.Excluded));
    }

    private static IReadOnlyList<string> RootNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static HashSet<string> PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(document.RootElement, names);
        return names;
    }

    private static void Collect(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Collect(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, names);
                break;
        }
    }

    private static string WorldVoxel(int x, int y, int z, int durability, ulong? runtime)
    {
        var suffix = runtime is { } id ? $",\"runtime_id\":{id.ToString(CultureInfo.InvariantCulture)}" : "";
        var orientation = runtime is null ? "nz_py" : "px_py";
        return $"{{\"x\":{x},\"y\":{y},\"z\":{z},\"definition_id\":\"core:a\",\"orientation\":\"{orientation}\",\"durability\":{durability}{suffix}}}";
    }

    private static string Submit(VoxelDevelopmentSession session, WorldEdit edit) =>
        PlaygroundOperation.Submit(edit).Apply(session);

    private static void WriteRail(PlaygroundHarness harness, string name, int maxDurability = 100) =>
        harness.Source.AddVoxelFile(
            "core",
            "voxels/rail.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good("rail", name, "正式直轨占格", value: 10, tagsJson: "[]", maxDurability: maxDurability)));

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}

using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
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
    private const string GoldenDigest = "A613F10786585533805FC8B2CA0EFC9C8CF771EB841212BE5869454CBF4E2073";

    private const string Golden =
        """{"authority_format_version":1,"seed":42,"generator_version":"fixture-v1","bounds":{"x":4,"y":4,"z":4},"chunk_size":16,"next_entity_id":8,"transaction_count":2,"simulation_step":3,"next_edit_id":4,"boundary_sequence":1,"simulation_fault":null,"world":[{"x":1,"y":0,"z":2,"definition_id":"core:a","orientation":"px_py","durability":75,"runtime_id":9},{"x":4,"y":4,"z":4,"definition_id":"core:a","orientation":"nz_py","durability":100}],"save_local_definitions":[{"revision":"local","id":"core:shell","archetype_id":"core:arch","size_class":"small","voxels":[{"x":0,"y":1,"z":0,"definition_id":"core:part","orientation":"px_py"}],"contacts":[{"x":1.5,"y":-0.75,"z":0,"bound_x":1,"bound_y":0,"bound_z":1}]}],"vehicles":[{"id":1,"definition_id":"core:shell","last_used_revision":"def","archetype_id":"core:arch","last_used_archetype_revision":"arch","size_class":"small","save_local_revision":"local","transform":{"origin_x":0.1,"origin_y":-0,"origin_z":1.7976931348623157E+308,"right_x":1,"right_y":0,"right_z":0,"up_x":0,"up_y":1,"up_z":5E-324},"parts":[{"id":2,"x":0,"y":1,"z":0,"definition_id":"core:part","orientation":"px_py","durability":100,"last_used_revision":"part"},{"id":5,"x":1,"y":0,"z":0,"definition_id":"core:part","orientation":"nz_py","durability":80,"last_used_revision":"part"}],"contacts":[{"x":1.5,"y":-0.75,"z":1.0000000000000002,"bound_x":1,"bound_y":0,"bound_z":1},{"x":0,"y":0,"z":0,"bound_x":7,"bound_y":0,"bound_z":1}]}],"railway":{"sleeper_count":1,"segments":[{"id":1,"alignment":{"start_x":0,"start_y":0,"start_z":0,"end_x":2,"end_y":0,"end_z":0},"rail_volumes":[{"min_x":0,"min_y":0,"min_z":0,"max_x":1,"max_y":1,"max_z":1}],"sleeper_volumes":[]}],"connections":[{"from_segment_id":1,"from_end":"end","to_segment_id":2,"to_end":"start"},{"from_segment_id":2,"from_end":"end","to_segment_id":3,"to_end":"start"}],"occupancy":[{"x":65,"y":2,"z":19,"supports":[{"segment_id":1,"world_x_start":1.5,"world_x_end":2.5,"along_start":0,"along_end":1}]}],"destroyed":[{"x":65,"y":2,"z":19}]},"assemblies":[{"id":7,"active_locomotive_id":1,"vehicle_ids":[1,4],"connections":[{"from_vehicle_id":1,"from_part_id":2,"from_x":0,"from_y":1,"from_z":0,"to_vehicle_id":4,"to_part_id":3,"to_x":1,"to_y":0,"to_z":1}],"reference_segment_id":1,"distance":3.141592653589793,"heading":"along","speed":0,"running":false,"halt_reason":"forward_gap"}],"pending":[{"edit_id":1,"accepted_step":0,"accepted_sequence":0,"target_step":1,"edit":{"input_format_version":1,"kind":"voxel.add","position":{"x":60,"y":3,"z":20},"definition_id":"core:ferrous-ore","orientation":"px_py"}}]}""";

    [TestMethod]
    public void Handwritten_golden_matches_an_independent_digest_and_the_encoder()
    {
        var independent = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Golden)));
        Assert.AreEqual(2579, Encoding.UTF8.GetByteCount(Golden));
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
        AssertDecode(Golden.Replace("\"id\":\"core:shell\",", "\"id\":\"core:shell\",\"name\":\"shell\",\"description\":\"local shell\",", StringComparison.Ordinal), AuthorityDecodeStatus.UnknownFormat);
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
        var unspecified = observed with { Context = observed.Context with { BuildId = null } };
        var missingBuild = AuthorityComparison.Compare(unspecified, unspecified);
        Assert.AreEqual(AuthorityDifferenceKind.EnvironmentMismatch, missingBuild.Kind);
        Assert.AreEqual("build", missingBuild.Field);
        StringAssert.Contains(missingBuild.Detail, "构建身份缺失");
        var arm = observed with { Context = observed.Context with { Architecture = "Arm64" } };
        var unsupported = AuthorityComparison.Compare(arm, arm);
        Assert.AreEqual(AuthorityDifferenceKind.EnvironmentMismatch, unsupported.Kind);
        Assert.AreEqual("architecture", unsupported.Field);
        StringAssert.Contains(unsupported.Detail, "Windows x64");
        var location = typeof(AuthorityEnvironment).Assembly.Location;
        var independentBuild = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
        Assert.AreEqual(independentBuild, AuthorityCodec.Observe(train.Session).Context.BuildId);
        Assert.AreEqual(64, independentBuild.Length);
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
    public void Cross_process_sequence_resumes_pending_inputs_at_the_same_authority_boundaries()
    {
        using var content = PlaygroundHarness.Content();
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.CrossProcessComparisonId)!;
        var continuous = AuthorityValidation.Run(sequence, content.Catalog, content.Composites, content.Store);
        Assert.IsTrue(continuous.Passed, string.Join(Environment.NewLine, continuous.Lines()));
        Assert.AreEqual(13, continuous.Observation!.Checkpoints.Count);
        Assert.AreEqual(new SimulationPosition(0, 8), continuous.Observation.Checkpoints[8].Boundary);

        Assert.AreEqual(PlaygroundEstablishStatus.Established,
            PlaygroundSession.TryEstablish(sequence.ConfigurationId, content.Catalog, content.Composites, content.Store, out var interrupted));
        try
        {
            for (var index = 0; index < 8; index++)
                Assert.AreEqual(sequence.Steps[index].ExpectedStatus, sequence.Steps[index].Operation.Apply(interrupted!));
            Assert.AreEqual(4, interrupted!.QueryPendingEdits().Count);
            var saved = interrupted.Save("Cross process sequence");
            Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

            using var restarted = PlaygroundHarness.Content(userData: content.UserData);
            var resumed = new VoxelDevelopmentSession(restarted.Catalog, VoxelWorldIdentity.Slice4,
                restarted.Store, restarted.Composites);
            restarted.Attach(resumed);
            Assert.AreEqual(WorldSaveStatus.Loaded, resumed.Load(saved.SaveId!.Value).Status);
            Assert.IsTrue(resumed.SimulationPaused);
            for (var index = 8; index < sequence.Steps.Count; index++)
            {
                var step = sequence.Steps[index];
                var input = step.Operation.InputIdentity(resumed);
                Assert.AreEqual(step.ExpectedStatus, step.Operation.Apply(resumed));
                var expected = continuous.Observation.Checkpoints[index + 1];
                var actual = AuthorityCodec.Checkpoint(resumed, index + 1, input);
                var difference = AuthorityComparison.Compare(
                    new AuthorityObservation(continuous.Observation.Context, new[] { expected }),
                    new AuthorityObservation(AuthorityCodec.Context(resumed, null), new[] { actual }));
                Assert.IsTrue(difference.Equal, string.Join(Environment.NewLine, difference.Lines()));
            }
        }
        finally
        {
            interrupted?.Detach();
        }
    }

    [TestMethod]
    public void Evidence_preserves_the_origin_checkpoint_and_classifies_tampered_context()
    {
        using var content = PlaygroundHarness.Content();
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.AuthorityManufactureFractureId)!;
        var first = AuthorityValidation.Run(sequence, content.Catalog, content.Composites, content.Store, "process-one");
        var second = AuthorityValidation.Run(sequence, content.Catalog, content.Composites, content.Store, "process-one");
        Assert.IsTrue(first.Passed, string.Join(Environment.NewLine, first.Lines()));
        Assert.IsTrue(second.Passed, string.Join(Environment.NewLine, second.Lines()));
        var origin = first.Observation!.Checkpoints[^1];
        Assert.AreNotEqual(0, origin.Index);
        var evidence = AuthorityCodec.EncodeEvidence(first.Observation.Context, origin);
        Assert.IsTrue(AuthorityCodec.TryReadEvidence(evidence, out var context, out var checkpoint));
        Assert.AreEqual(origin.Index, checkpoint.Index);
        Assert.AreEqual(origin.Input, checkpoint.Input);
        Assert.AreEqual(origin.Boundary, checkpoint.Boundary);
        var actual = new AuthorityObservation(second.Observation!.Context, new[] { second.Observation.Checkpoints[^1] });
        Assert.IsTrue(AuthorityComparison.Compare(new AuthorityObservation(context, new[] { checkpoint }), actual).Equal);

        AuthorityDifference Tamper(string json)
        {
            Assert.IsTrue(AuthorityCodec.TryReadEvidence(json, out var tamperedContext, out var tamperedCheckpoint));
            return AuthorityComparison.Compare(
                new AuthorityObservation(tamperedContext, new[] { tamperedCheckpoint }),
                actual);
        }

        var build = Tamper(evidence.Replace("\"build\":\"process-one\"", "\"build\":\"other-build\"", StringComparison.Ordinal));
        Assert.AreEqual(AuthorityDifferenceKind.EnvironmentMismatch, build.Kind);
        Assert.AreEqual("build", build.Field);
        var contentDigest = Tamper(evidence.Replace(
            $"\"content_digest\":\"{context.ContentDigest}\"",
            $"\"content_digest\":\"{new string('B', 64)}\"",
            StringComparison.Ordinal));
        Assert.AreEqual(AuthorityDifferenceKind.ContentAdaptation, contentDigest.Kind);
        Assert.AreEqual("content", contentDigest.Field);
        var source = Tamper(evidence.Replace(
            "\"configuration_id\":\"straight-rail-train\"",
            "\"configuration_id\":\"straight-rail-empty\"",
            StringComparison.Ordinal));
        Assert.AreEqual(AuthorityDifferenceKind.SourceMismatch, source.Kind);
        Assert.AreEqual("source", source.Field);
        var shifted = checkpoint with
        {
            Canonical = checkpoint.Canonical.Replace("\"seed\":4", "\"seed\":5", StringComparison.Ordinal)
        };
        var field = AuthorityComparison.Compare(
            new AuthorityObservation(context, new[] { shifted }),
            actual);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, field.Kind);
        Assert.AreEqual("seed", field.Field);
        Assert.AreEqual(origin.Index, field.CheckpointIndex);
        Assert.AreEqual(origin.Input, field.Input);
    }

    [TestMethod]
    public void Save_local_display_text_is_not_an_authority_difference()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.AreEqual("Valid", Submit(session, new VehicleManufactureEdit(HollowShellFixture.DefinitionId, PlaygroundSequences.AuthorityShellPose)));
        Assert.AreEqual("Valid", PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)).Apply(session));
        var prior = AuthorityCodec.Observe(session, "tested-head", "establish");
        Assert.IsFalse(prior.Checkpoints[0].Canonical.Contains("\"name\"", StringComparison.Ordinal));
        Assert.IsFalse(prior.Checkpoints[0].Canonical.Contains("\"description\"", StringComparison.Ordinal));
        var saved = session.Save("display");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var path = playground.Store.PathFor(saved.SaveId!.Value);
        var original = File.ReadAllText(path);
        var display = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        Assert.IsTrue(display["save_local_definitions"]!.AsArray().Count > 0);
        foreach (var item in display["save_local_definitions"]!.AsArray())
        {
            item!["name"] = "仅显示名称";
            item["description"] = "仅显示描述";
        }

        File.WriteAllText(path, display.ToJsonString());
        var loaded = session.Load(saved.SaveId.Value);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Status);
        var afterDisplay = AuthorityCodec.Observe(session, "tested-head", "establish");
        Assert.AreEqual(prior.Context.ContentDigest, afterDisplay.Context.ContentDigest);
        Assert.IsTrue(AuthorityComparison.Compare(prior, afterDisplay).Equal, AuthorityComparison.Compare(prior, afterDisplay).Detail);

        Assert.IsTrue(AuthorityCodec.TryDecode(prior.Checkpoints[0].Canonical, out var model, out var decoded));
        Assert.AreEqual(AuthorityDecodeStatus.Decoded, decoded);
        var definition = model.SaveLocalDefinitions[0];
        var voxel = definition.Voxels[0];
        var moved = definition with
        {
            Voxels = new[] { voxel with { X = voxel.X + 1 } }.Concat(definition.Voxels.Skip(1)).ToArray()
        };
        var altered = AuthorityCodec.Encode(model with
        {
            SaveLocalDefinitions = new[] { moved }.Concat(model.SaveLocalDefinitions.Skip(1)).ToArray()
        });
        var difference = AuthorityComparison.Compare(
            prior,
            prior with
            {
                Checkpoints = new[]
                {
                    prior.Checkpoints[0] with { Canonical = altered, Digest = AuthorityCodec.Digest(altered) }
                }
            });
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, difference.Kind);
        StringAssert.Contains(difference.Field, "x");
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
    public void Rejected_command_order_and_checkpoint_identity_are_compared_before_state()
    {
        using var content = PlaygroundHarness.Content();
        var catalog = content.Catalog;
        var composites = content.Composites;
        PlaygroundValidationStep Step(int damage) => new(
            PlaygroundOperation.Damage(new WorldVoxelCoord(-1, 0, 0), damage),
            "OutOfBounds",
            Array.Empty<PlaygroundCheck>());
        var left = AuthorityValidation.Run(
            new PlaygroundSequence("left", PlaygroundConfigurations.StraightRailTrainId, new[] { Step(1), Step(2) }),
            catalog,
            composites,
            buildId: "tested-head");
        var right = AuthorityValidation.Run(
            new PlaygroundSequence("right", PlaygroundConfigurations.StraightRailTrainId, new[] { Step(2), Step(1) }),
            catalog,
            composites,
            buildId: "tested-head");
        Assert.IsTrue(left.Passed, string.Join(Environment.NewLine, left.Lines()));
        Assert.IsTrue(right.Passed, string.Join(Environment.NewLine, right.Lines()));
        var order = AuthorityComparison.Compare(left.Observation!, right.Observation!);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, order.Kind);
        Assert.AreEqual("input", order.Field);
        Assert.AreEqual(1, order.CheckpointIndex);
        Assert.AreEqual("damage -1,0,0 1", left.Sequence.Steps[0].Operation);
        Assert.AreEqual("damage -1,0,0 2", right.Sequence.Steps[0].Operation);
        Assert.AreEqual(
            "{\"kind\":\"damage_voxel\",\"at_step\":null,\"position\":{\"x\":-1,\"y\":0,\"z\":0},\"amount\":1}",
            order.Expected);
        Assert.AreEqual(
            "{\"kind\":\"damage_voxel\",\"at_step\":null,\"position\":{\"x\":-1,\"y\":0,\"z\":0},\"amount\":2}",
            order.Actual);
        Assert.AreNotEqual(left.Sequence.Steps[0].Operation, left.Observation!.Checkpoints[1].Input);
        StringAssert.Contains(order.Detail, "语义输入");

        using var harness = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var original = AuthorityCodec.Observe(harness.Session, "tested-head");
        var changedInput = original with
        {
            Checkpoints = new[] { original.Checkpoints[0] with { Input = "different-input" } }
        };
        var input = AuthorityComparison.Compare(original, changedInput);
        Assert.AreEqual("input", input.Field);
        Assert.AreEqual(original.Checkpoints[0].Input, input.Expected);
        Assert.AreEqual("different-input", input.Actual);

        var changedBoundary = original with
        {
            Checkpoints = new[] { original.Checkpoints[0] with { Boundary = new SimulationPosition(999, 99) } }
        };
        var boundary = AuthorityComparison.Compare(original, changedBoundary);
        Assert.AreEqual("boundary", boundary.Field);
        Assert.AreEqual(original.Checkpoints[0].Boundary.ToString(), boundary.Expected);
        Assert.AreEqual(new SimulationPosition(999, 99).ToString(), boundary.Actual);

        var changedIndex = original with
        {
            Checkpoints = new[] { original.Checkpoints[0] with { Index = 4 } }
        };
        var index = AuthorityComparison.Compare(original, changedIndex);
        Assert.AreEqual("index", index.Field);
        Assert.AreEqual("0", index.Expected);
        Assert.AreEqual("4", index.Actual);

        var saved = harness.Session.Save("identity");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var before = AuthorityCodec.Observe(harness.Session, "tested-head", "establish");
        Assert.AreEqual(WorldSaveStatus.Loaded, harness.Session.Load(saved.SaveId!.Value).Status);
        var restored = AuthorityCodec.Observe(harness.Session, "tested-head", "establish");
        Assert.AreEqual(before.Checkpoints[0].Boundary, restored.Checkpoints[0].Boundary);
        Assert.IsTrue(AuthorityComparison.Compare(before, restored).Equal, AuthorityComparison.Compare(before, restored).Detail);
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
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, order.Kind);
        Assert.AreEqual("input", order.Field);
        Assert.AreEqual(1, order.CheckpointIndex);
        StringAssert.Contains(order.Expected, "\"x\":10");
        StringAssert.Contains(order.Actual, "\"x\":12");
        StringAssert.Contains(order.Detail, "语义输入");

        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var clean = AuthorityCodec.Document(AuthorityCodec.Capture(playground.Session));
        Assert.AreEqual(WorldCommandStatus.Damaged, playground.Session.Damage(StraightRailFixture.LeftRailSample, 1).Status);
        var damaged = AuthorityCodec.Document(AuthorityCodec.Capture(playground.Session));
        var cursor = CompareText(clean.Canonical, damaged.Canonical);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, cursor.Kind);
        Assert.AreEqual("next_edit_id", cursor.Field);
    }

    [TestMethod]
    public void Rejected_edits_with_different_payloads_are_not_the_same_input()
    {
        using var content = PlaygroundHarness.Content();
        var catalog = content.Catalog;
        var composites = content.Composites;
        var left = AuthorityValidation.Run(RejectedVoxel(-1), catalog, composites, buildId: "tested-head");
        var right = AuthorityValidation.Run(RejectedVoxel(-2), catalog, composites, buildId: "tested-head");
        var again = AuthorityValidation.Run(RejectedVoxel(-1), catalog, composites, buildId: "tested-head");
        Assert.IsTrue(left.Passed, string.Join(Environment.NewLine, left.Lines()));
        Assert.IsTrue(right.Passed, string.Join(Environment.NewLine, right.Lines()));
        Assert.AreEqual("edit voxel.add", left.Sequence.Steps[0].Operation);
        const string LeftPayload =
            "{\"kind\":\"edit\",\"at_step\":null,\"edit\":{\"input_format_version\":1,\"kind\":\"voxel.add\",\"position\":{\"x\":-1,\"y\":0,\"z\":0},\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\"}}";
        const string RightPayload =
            "{\"kind\":\"edit\",\"at_step\":null,\"edit\":{\"input_format_version\":1,\"kind\":\"voxel.add\",\"position\":{\"x\":-2,\"y\":0,\"z\":0},\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\"}}";
        Assert.AreEqual(LeftPayload, left.Observation!.Checkpoints[1].Input);
        Assert.AreEqual(RightPayload, right.Observation!.Checkpoints[1].Input);
        Assert.AreNotEqual(left.Sequence.Steps[0].Operation, left.Observation.Checkpoints[1].Input);
        Assert.AreEqual(left.Observation.Checkpoints[1].Digest, right.Observation.Checkpoints[1].Digest);
        var difference = AuthorityComparison.Compare(left.Observation, right.Observation);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, difference.Kind);
        Assert.AreEqual("input", difference.Field);
        Assert.AreEqual(LeftPayload, difference.Expected);
        Assert.AreEqual(RightPayload, difference.Actual);
        Assert.IsTrue(AuthorityComparison.Compare(left.Observation, again.Observation!).Equal);

        var refusedLeft = AuthorityValidation.Run(RefusedRefit(1), catalog, composites, buildId: "tested-head");
        var refusedRight = AuthorityValidation.Run(RefusedRefit(2), catalog, composites, buildId: "tested-head");
        Assert.IsTrue(refusedLeft.Passed, string.Join(Environment.NewLine, refusedLeft.Lines()));
        Assert.IsTrue(refusedRight.Passed, string.Join(Environment.NewLine, refusedRight.Lines()));
        Assert.AreEqual(refusedLeft.Observation!.Checkpoints[1].Digest, refusedRight.Observation!.Checkpoints[1].Digest);
        var refused = AuthorityComparison.Compare(refusedLeft.Observation, refusedRight.Observation);
        Assert.AreEqual(AuthorityDifferenceKind.FieldMismatch, refused.Kind);
        Assert.AreEqual("input", refused.Field);
        StringAssert.Contains(refused.Expected!, "\"resolved\":null");
        StringAssert.Contains(refused.Expected, "\"x\":1");
        StringAssert.Contains(refused.Actual!, "\"x\":2");
        StringAssert.Contains(refusedLeft.Sequence.Steps[0].Operation, "refit latest");
        Assert.AreNotEqual(refusedLeft.Sequence.Steps[0].Operation, refusedLeft.Observation.Checkpoints[1].Input);

        using var train = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var latest = train.Session.QueryComposites().OrderByDescending(item => item.Id.Value).First();
        var oriented = PlaygroundOperation.RefitLatest(
            PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, HollowShellRefit.MarkerOrientation));
        var otherCell = PlaygroundOperation.RefitLatest(
            PlaygroundRefit.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity));
        var orientedIdentity = oriented.InputIdentity(train.Session);
        var otherIdentity = otherCell.InputIdentity(train.Session);
        Assert.AreNotEqual(orientedIdentity, otherIdentity);
        StringAssert.Contains(orientedIdentity, $"\"vehicle_id\":{latest.Id.Value.ToString(CultureInfo.InvariantCulture)}");
        StringAssert.Contains(orientedIdentity, HollowShellRefit.MarkerOrientation.ToString());
        StringAssert.Contains(otherIdentity, "\"x\":4");
        var scheduled = PlaygroundOperation.Submit(
            new VoxelAddEdit(new WorldVoxelCoord(-1, 0, 0), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity),
            9);
        var immediate = PlaygroundOperation.Submit(
            new VoxelAddEdit(new WorldVoxelCoord(-1, 0, 0), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity));
        Assert.AreNotEqual(immediate.InputIdentity(train.Session), scheduled.InputIdentity(train.Session));
        StringAssert.Contains(scheduled.InputIdentity(train.Session), "\"at_step\":9");
        StringAssert.Contains(immediate.InputIdentity(train.Session), "\"at_step\":null");
    }

    [TestMethod]
    public void Damage_latest_records_the_vehicle_resolved_before_the_command()
    {
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.AuthorityManufactureFractureId)!;
        using var harness = PlaygroundHarness.Establish(sequence.ConfigurationId);
        foreach (var step in sequence.Steps.Take(sequence.Steps.Count - 1))
            Assert.AreEqual(step.ExpectedStatus, step.Operation.Apply(harness.Session), step.Operation.Describe());
        var last = sequence.Steps[^1];
        var before = last.Operation.InputIdentity(harness.Session);
        Assert.AreEqual(last.ExpectedStatus, last.Operation.Apply(harness.Session));
        Assert.AreNotEqual(before, last.Operation.InputIdentity(harness.Session));
        StringAssert.Contains(before, "\"kind\":\"damage_latest\"");
        StringAssert.Contains(before, "\"amount\":100");
        StringAssert.Contains(before, "\"x\":3,\"y\":1,\"z\":1");
        StringAssert.Contains(before, "\"vehicle_id\":");
        Assert.AreNotEqual(last.Operation.Describe(), before);

        var report = AuthorityValidation.Run(sequence, harness.Catalog, harness.Composites, buildId: "tested-head");
        Assert.IsTrue(report.Passed, string.Join(Environment.NewLine, report.Lines()));
        Assert.AreEqual(before, report.Observation!.Checkpoints[^1].Input);
        Assert.AreEqual(last.Operation.Describe(), report.Sequence.Steps[^1].Operation);
    }

    [TestMethod]
    public void Evidence_rejects_non_integral_or_out_of_range_versions()
    {
        using var train = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var observed = AuthorityCodec.Observe(train.Session, "build-a");
        var evidence = AuthorityCodec.EncodeEvidence(observed.Context, observed.Checkpoints[0]);
        foreach (var property in new[] { "evidence_format_version", "format_version", "configuration_version" })
        {
            foreach (var value in new[] { "1e100", "1.5", "2147483648" })
            {
                var bad = ReplaceJsonNumber(evidence, property, value);
                Assert.IsFalse(AuthorityCodec.TryReadEvidence(bad, out _, out _), $"{property}={value}");
            }
        }
    }

    [TestMethod]
    public void Base_fingerprint_is_compared_as_source_not_as_content_or_canonical_state()
    {
        using var train = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var fingerprint = train.Session.WorldSource!.BaseFingerprint;
        Assert.AreEqual(64, fingerprint.Length);
        var observed = AuthorityCodec.Observe(train.Session, "tested-head");
        Assert.AreEqual(fingerprint, observed.Context.BaseFingerprint);
        Assert.IsFalse(observed.Checkpoints[0].Canonical.Contains("base_fingerprint", StringComparison.Ordinal));
        var evidence = AuthorityCodec.EncodeEvidence(observed.Context, observed.Checkpoints[0]);
        Assert.IsTrue(AuthorityCodec.TryReadEvidence(evidence, out var context, out _));
        Assert.AreEqual(fingerprint, context.BaseFingerprint);

        var changed = observed with { Context = observed.Context with { BaseFingerprint = new string('A', 64) } };
        var difference = AuthorityComparison.Compare(observed, changed);
        Assert.AreEqual(AuthorityDifferenceKind.SourceMismatch, difference.Kind);
        Assert.AreEqual("source", difference.Field);
        StringAssert.Contains(difference.Detail, "来源");
        StringAssert.Contains(difference.Expected, fingerprint);
        StringAssert.Contains(difference.Actual, new string('A', 64));

        var missing = observed with { Context = observed.Context with { BaseFingerprint = null } };
        Assert.AreEqual(AuthorityDifferenceKind.SourceMismatch, AuthorityComparison.Compare(observed, missing).Kind);
        Assert.IsFalse(AuthorityCodec.TryReadEvidence(
            evidence.Replace($"\"base_fingerprint\":\"{fingerprint}\"", "\"base_fingerprint\":\"not-a-fingerprint\"", StringComparison.Ordinal),
            out _,
            out _));
    }

    [TestMethod]
    public void Build_identity_rejects_a_file_from_another_module()
    {
        var assembly = typeof(AuthorityEnvironment).Assembly;
        var location = assembly.Location;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
        var foreign = typeof(Assert).Assembly.Location;
        Assert.AreNotEqual(location, foreign);
        Assert.IsNull(AuthorityEnvironment.TryIdentifyBuild(assembly, new[] { foreign }).Id);
        var matched = AuthorityEnvironment.TryIdentifyBuild(assembly, new[] { foreign, location });
        Assert.AreEqual(hash, matched.Id);
        Assert.AreEqual(Path.GetFullPath(location), matched.File);

        var junk = Path.Combine(Path.GetTempPath(), "train-authority-not-a-module-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(junk, "not a pe");
        try
        {
            Assert.IsNull(AuthorityEnvironment.TryIdentifyBuild(assembly, new[] { junk }).Id);
        }
        finally
        {
            File.Delete(junk);
        }

        var loadedBytes = File.ReadAllBytes(location);
        var context = new AssemblyLoadContext("authority-build-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var loaded = context.LoadFromStream(new MemoryStream(loadedBytes));
            Assert.AreEqual(string.Empty, loaded.Location);
            Assert.IsNull(AuthorityEnvironment.TryIdentifyBuild(loaded, new[] { foreign }).Id);
            Assert.AreEqual(hash, AuthorityEnvironment.TryIdentifyBuild(loaded, new[] { foreign, location }).Id);
        }
        finally
        {
            context.Unload();
        }
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

    private static PlaygroundSequence RejectedVoxel(int x) =>
        new(
            "payload-" + x.ToString(CultureInfo.InvariantCulture),
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                new PlaygroundValidationStep(
                    PlaygroundOperation.Submit(new VoxelAddEdit(
                        new WorldVoxelCoord(x, 0, 0),
                        new VoxelDefinitionId("core", "ferrous-ore"),
                        VoxelOrientation.Identity)),
                    "OutOfBounds",
                    Array.Empty<PlaygroundCheck>())
            });

    private static PlaygroundSequence RefusedRefit(int x) =>
        new(
            "refit-" + x.ToString(CultureInfo.InvariantCulture),
            PlaygroundConfigurations.StraightRailEmptyId,
            new[]
            {
                new PlaygroundValidationStep(
                    PlaygroundOperation.RefitLatest(PlaygroundRefit.Remove(new LocalVoxelCoord(x, 0, 0))),
                    PlaygroundOperation.NoVehicleStatus,
                    Array.Empty<PlaygroundCheck>())
            });

    private static string ReplaceJsonNumber(string json, string property, string value)
    {
        var key = $"\"{property}\":";
        var start = json.IndexOf(key, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, property);
        start += key.Length;
        var end = start;
        while (end < json.Length && json[end] != ',' && json[end] != '}')
            end++;
        return string.Concat(json.AsSpan(0, start), value, json.AsSpan(end));
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
            "fixture-build",
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

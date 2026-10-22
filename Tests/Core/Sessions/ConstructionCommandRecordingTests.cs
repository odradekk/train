using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class ConstructionCommandRecordingTests
{
    [TestMethod]
    public void Vehicle_repair_has_a_fixed_vector_and_preserves_source_order()
    {
        const string payload = """{"input_format_version":2,"kind":"construction.start_owned","request":{"kind":"repair_vehicle","facility":{"building":7,"capability":"construction"},"vehicle":8,"sources":[4,2],"priority":-3}}""";
        var command = new CoreCommand.StartOwnedJob(new JobRequest.Repair(new(new(7), CapabilityKind.Construction),
            new InstanceId.Vehicle(new(8)), [new(4), new(2)], -3));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
        Assert.IsInstanceOfType<CoreResult<DecodedCommand>.Failure>(CoreCommandEncoding.Decode(payload.Replace("[4,2]", "[4,0]")));
        Assert.IsInstanceOfType<CoreResult<DecodedCommand>.Failure>(CoreCommandEncoding.Decode(payload.Replace("\"priority\":-3", "\"priority\":-3,\"extra\":1")));
        const string building = """{"input_format_version":2,"kind":"construction.start_owned","request":{"kind":"repair_building","facility":{"building":7,"capability":"construction"},"building":8,"sources":[4,2],"priority":-3}}""";
        var buildingRepair = new CoreCommand.StartOwnedJob(new JobRequest.Repair(new(new(7), CapabilityKind.Construction),
            new InstanceId.Building(new(8)), [new(4), new(2)], -3));
        Assert.AreEqual(building, CoreCommandEncoding.Encode(buildingRepair));
        Assert.AreEqual(building, CoreCommandEncoding.Encode(Decode(building)));
    }

    [TestMethod]
    public void Commission_vehicle_repair_has_a_fixed_vector_with_payment_after_request()
    {
        const string payload = """{"input_format_version":2,"kind":"construction.commission_vehicle_work","request":{"kind":"repair_vehicle","facility":{"building":7,"capability":"construction"},"vehicle":8,"sources":[4,2],"priority":-3},"maximum_payment":123}""";
        var command = new CoreCommand.CommissionVehicleWork(new JobRequest.Repair(new(new(7), CapabilityKind.Construction),
            new InstanceId.Vehicle(new(8)), [new(4), new(2)], -3), new(123));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
    }

    [TestMethod]
    [DataRow("construction.commission_vehicle_work", "")]
    [DataRow("construction.start_owned", ",\"maximum_payment\":123")]
    public void Missing_or_extraneous_maximum_payment_is_rejected(string kind, string payment)
    {
        var payload = """{"input_format_version":2,"kind":"KIND","request":{"kind":"repair_vehicle","facility":{"building":7,"capability":"construction"},"vehicle":8,"sources":[4,2],"priority":-3}PAYMENT}"""
            .Replace("KIND", kind).Replace("PAYMENT", payment);
        var result = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", result.Problems.Single().Code);
        Assert.AreEqual("/maximum_payment", result.Problems.Single().Field);
    }

    [TestMethod]
    public void Build_vehicle_round_trips_the_complete_normalized_design_and_assembly_goods()
    {
        var input = Input('D');
        var command = new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(new(new(7), CapabilityKind.Construction), input, [new(4), new(2)], 9));
        var payload = CoreCommandEncoding.Encode(command);
        var decoded = (JobRequest.BuildVehicle)((CoreCommand.StartOwnedJob)Decode(payload)).Request;
        Assert.AreEqual(input.InputIdentity, decoded.Design.InputIdentity);
        Assert.AreEqual(input.Design.Design, decoded.Design.Design.Design);
        Assert.AreEqual(input.Design.CanConstruct, decoded.Design.Design.CanConstruct);
        Assert.AreEqual(input.Design.ReturnUnits.Length, decoded.Design.Design.ReturnUnits.Length);
        Assert.AreEqual(input.AssemblyGoods.Single(), decoded.Design.AssemblyGoods.Single());
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
        Assert.IsInstanceOfType<CoreResult<DecodedCommand>.Failure>(CoreCommandEncoding.Decode(payload.Replace("\"can_construct\":true", "\"can_construct\":true,\"extra\":1")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Accepted_rejected_and_faulted_design_inputs_are_retained_only_as_recording_dependencies(bool apply)
    {
        using var session = new CoreSession(WorldFixture.State());
        foreach (var identity in new[] { 'D', 'E', 'F' })
        {
            CoreCommand command = apply
                ? new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(8)), new(new(new string('A', 64)), new(new string('B', 64)), Input(identity), [], [],
                    [new(new GoodId.Voxel(new("fixture:removed-material")), 1, [], [], [])], 2))
                : new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(new(new(7), CapabilityKind.Construction), Input(identity), [], 0));
            session.ExecuteCommand(command, CommandSource.AutomationTest, _ => identity switch
            {
                'D' => new CommandEffect.Accepted(session.State.Content, []),
                'E' => new CommandEffect.Rejected([new("invalid_state", "test", null, [])]),
                _ => throw new InvalidOperationException("test fault")
            });
        }
        var recording = session.ReadRecording();
        Assert.AreEqual(3, recording.Commands.Length);
        foreach (var manifest in new[] { recording.RequiredReferences, recording.FaultSegments.Single().RequiredReferences })
            foreach (var identity in new[] { 'D', 'E', 'F' })
            {
                Assert.IsTrue(manifest.Inputs.Contains(Input(identity).InputIdentity));
                Assert.IsTrue(manifest.Designs.Contains(Input(identity).Design.Design));
                Assert.IsTrue(manifest.Definitions.Any(definition => definition.Definition ==
                    new CoreDefinitionReference.Voxel(new("fixture:recording-material"))));
            }
        if (apply)
            Assert.IsTrue(recording.RequiredReferences.Definitions.Any(definition => definition.Definition ==
                new CoreDefinitionReference.Voxel(new("fixture:removed-material"))));
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Diagnostic));
        Assert.IsFalse(saved.References.Designs.Contains(Input('E').Design.Design));
        Assert.IsFalse(saved.References.Designs.Contains(Input('F').Design.Design));
    }

    [TestMethod]
    public void Design_changes_round_trip_complete_targets_and_deltas()
    {
        var change = new DesignChangeProjection(new(new string('A', 64)), new(new string('B', 64)), Input('D'), [], [],
            [new(new GoodId.Voxel(new("fixture:removed-material")), 1, [], [], [])], 2);
        CoreCommand[] commands =
        [
            new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(8)), change),
            new CoreCommand.ApplyInstanceDesign(new InstanceId.Building(new(8)), change),
            new CoreCommand.StartOwnedJob(new JobRequest.Refit(new(new(7), CapabilityKind.Construction), new InstanceId.Vehicle(new(8)), change, [new(4), new(2)], -3)),
            new CoreCommand.CommissionVehicleWork(new JobRequest.Refit(new(new(7), CapabilityKind.Construction), new InstanceId.Vehicle(new(8)), change, [new(4), new(2)], -3), new(123))
        ];
        foreach (var command in commands)
        {
            var payload = CoreCommandEncoding.Encode(command);
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
            var decoded = Decode(payload) switch
            {
                CoreCommand.ApplyInstanceDesign apply => apply.Change,
                CoreCommand.StartOwnedJob { Request: JobRequest.Refit refit } => refit.Change,
                CoreCommand.CommissionVehicleWork { Request: JobRequest.Refit refit } => refit.Change,
                _ => throw new AssertFailedException()
            };
            Assert.AreEqual(change.Before, decoded.Before);
            Assert.AreEqual(change.Target.InputIdentity, decoded.Target.InputIdentity);
            Assert.AreEqual(change.RemovedItems.Single().Good, decoded.RemovedItems.Single().Good);
            Assert.AreEqual(change.ChangedItemCount, decoded.ChangedItemCount);
            var prefix = command is CoreCommand.ApplyInstanceDesign { Instance: InstanceId.Building }
                ? "{\"input_format_version\":2,\"kind\":\"instance.apply_design\",\"building\":8,\"change\":{"
                : command is CoreCommand.ApplyInstanceDesign
                ? "{\"input_format_version\":2,\"kind\":\"instance.apply_design\",\"vehicle\":8,\"change\":{"
                : command is CoreCommand.StartOwnedJob
                    ? "{\"input_format_version\":2,\"kind\":\"construction.start_owned\",\"request\":{\"kind\":\"refit_vehicle\",\"facility\":{\"building\":7,\"capability\":\"construction\"},\"vehicle\":8,\"change\":{"
                    : "{\"input_format_version\":2,\"kind\":\"construction.commission_vehicle_work\",\"request\":{\"kind\":\"refit_vehicle\",\"facility\":{\"building\":7,\"capability\":\"construction\"},\"vehicle\":8,\"change\":{";
            StringAssert.StartsWith(payload, prefix);
            if (command is CoreCommand.ApplyInstanceDesign { Instance: InstanceId.Vehicle })
            {
                var both = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload.Replace("\"vehicle\":8", "\"vehicle\":8,\"building\":8"));
                Assert.AreEqual("/building", both.Problems.Single().Field);
            }
            var invalid = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload.Replace("\"changed_item_count\":2", "\"changed_item_count\":2,\"changed_item_count\":2"));
            Assert.AreEqual(command is CoreCommand.ApplyInstanceDesign ? "/change" : "/request/change", invalid.Problems.Single().Field);
            Assert.IsInstanceOfType<CoreResult<DecodedCommand>.Failure>(CoreCommandEncoding.Decode(payload.Replace("\"changed_item_count\":2", "\"changed_item_count\":-1")));
            Assert.IsInstanceOfType<CoreResult<DecodedCommand>.Failure>(CoreCommandEncoding.Decode(payload.Replace("\"changed_item_count\":2", "\"changed_item_count\":2,\"extra\":0")));
        }
    }

    [TestMethod]
    public void Dismantle_and_pause_have_fixed_vectors_and_precise_failures()
    {
        const string dismantle = """{"input_format_version":2,"kind":"construction.start_owned","request":{"kind":"dismantle_vehicle","facility":{"building":7,"capability":"construction"},"vehicle":8,"priority":-3}}""";
        const string building = """{"input_format_version":2,"kind":"construction.start_owned","request":{"kind":"dismantle_building","facility":{"building":7,"capability":"construction"},"building":8,"priority":-3}}""";
        const string pause = """{"input_format_version":2,"kind":"construction.set_deconstruction_paused","job":8,"paused":true}""";
        Assert.AreEqual(dismantle, CoreCommandEncoding.Encode(new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(new(new(7), CapabilityKind.Construction), new InstanceId.Vehicle(new(8)), -3))));
        Assert.AreEqual(building, CoreCommandEncoding.Encode(new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(new(new(7), CapabilityKind.Construction), new InstanceId.Building(new(8)), -3))));
        Assert.AreEqual(pause, CoreCommandEncoding.Encode(new CoreCommand.SetDeconstructionPaused(new(8), true)));
        Assert.AreEqual(dismantle, CoreCommandEncoding.Encode(Decode(dismantle)));
        Assert.AreEqual(building, CoreCommandEncoding.Encode(Decode(building)));
        Assert.AreEqual(pause, CoreCommandEncoding.Encode(Decode(pause)));
        foreach (var (payload, field) in new[]
        {
            (pause.Replace("\"job\":8", "\"job\":0"), "/job"),
            (pause.Replace("true", "0"), "/paused"),
            (pause.Replace(",\"paused\":true", ""), "/paused"),
            (pause.Replace("\"job\":8", "\"job\":8,\"job\":9"), "/job"),
            (dismantle.Replace("\"priority\":-3", "\"sources\":[],\"priority\":-3"), "/request/sources")
        })
        {
            var result = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
            Assert.AreEqual("invalid_state", result.Problems.Single().Code);
            Assert.AreEqual(field, result.Problems.Single().Field);
        }
    }

    [TestMethod]
    public void Commission_rejects_a_building_request_kind()
    {
        const string payload = """{"input_format_version":2,"kind":"construction.commission_vehicle_work","request":{"kind":"build_building"},"maximum_payment":1}""";
        var result = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", result.Problems.Single().Code);
        Assert.AreEqual("/request/kind", result.Problems.Single().Field);
    }

    [TestMethod]
    public void Building_payloads_match_handwritten_canonical_json()
    {
        var design = BuildingDesign();
        var change = new DesignChangeProjection(new(new string('A', 64)), new(new string('B', 64)), design, [], [], [], 0);
        (CoreCommand Command, string Payload)[] cases =
        [
            (new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(new(new(7), CapabilityKind.Construction), new(3), design, [new(4)], 1)),
                BuildPayload("build_building", "\"plot\":3,")),
            (new CoreCommand.StartOwnedJob(new JobRequest.BuildEquipment(new(new(7), CapabilityKind.Construction), design, [new(4)], 1)),
                BuildPayload("build_equipment", "")),
            (new CoreCommand.StartOwnedJob(new JobRequest.Refit(new(new(7), CapabilityKind.Construction), new InstanceId.Building(new(8)), change, [new(4)], 1)),
                RefitPayload()),
            (new CoreCommand.ApplyInstanceDesign(new InstanceId.Building(new(8)), change), ApplyPayload())
        ];
        foreach (var (command, payload) in cases)
        {
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
        }
    }

    [TestMethod]
    public void Vehicle_payloads_match_handwritten_canonical_json()
    {
        var design = BuildingDesign();
        var change = new DesignChangeProjection(new(new string('A', 64)), new(new string('B', 64)), design, [], [], [], 0);
        const string facility = "\"facility\":{\"building\":7,\"capability\":\"construction\"}";
        var build = "{\"kind\":\"build_vehicle\"," + facility + ",\"design\":" + DesignJson() + ",\"sources\":[4],\"priority\":1}";
        var refit = "{\"kind\":\"refit_vehicle\"," + facility + ",\"vehicle\":8,\"change\":" + ChangeJson() + ",\"sources\":[4],\"priority\":1}";
        (CoreCommand Command, string Payload)[] cases =
        [
            (new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(new(new(7), CapabilityKind.Construction), design, [new(4)], 1)),
                "{\"input_format_version\":2,\"kind\":\"construction.start_owned\",\"request\":" + build + "}"),
            (new CoreCommand.CommissionVehicleWork(new JobRequest.BuildVehicle(new(new(7), CapabilityKind.Construction), design, [new(4)], 1), new(123)),
                "{\"input_format_version\":2,\"kind\":\"construction.commission_vehicle_work\",\"request\":" + build + ",\"maximum_payment\":123}"),
            (new CoreCommand.StartOwnedJob(new JobRequest.Refit(new(new(7), CapabilityKind.Construction), new InstanceId.Vehicle(new(8)), change, [new(4)], 1)),
                "{\"input_format_version\":2,\"kind\":\"construction.start_owned\",\"request\":" + refit + "}"),
            (new CoreCommand.CommissionVehicleWork(new JobRequest.Refit(new(new(7), CapabilityKind.Construction), new InstanceId.Vehicle(new(8)), change, [new(4)], 1), new(123)),
                "{\"input_format_version\":2,\"kind\":\"construction.commission_vehicle_work\",\"request\":" + refit + ",\"maximum_payment\":123}"),
            (new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(8)), change),
                "{\"input_format_version\":2,\"kind\":\"instance.apply_design\",\"vehicle\":8,\"change\":" + ChangeJson() + "}")
        ];
        foreach (var (command, payload) in cases)
        {
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
        }
    }

    private static string BuildPayload(string kind, string plot) =>
        "{\"input_format_version\":2,\"kind\":\"construction.start_owned\",\"request\":{\"kind\":\"" + kind
        + "\",\"facility\":{\"building\":7,\"capability\":\"construction\"}," + plot + "\"design\":" + DesignJson()
        + ",\"sources\":[4],\"priority\":1}}";

    private static string RefitPayload() =>
        "{\"input_format_version\":2,\"kind\":\"construction.start_owned\",\"request\":{\"kind\":\"refit_building\",\"facility\":{\"building\":7,\"capability\":\"construction\"},\"building\":8,\"change\":"
        + ChangeJson() + ",\"sources\":[4],\"priority\":1}}";

    private static string ApplyPayload() =>
        "{\"input_format_version\":2,\"kind\":\"instance.apply_design\",\"building\":8,\"change\":" + ChangeJson() + "}";

    private static string Hash(char digit) => "{\"value\":\"" + new string(digit, 64) + "\"}";

    private static string ChangeJson() =>
        "{\"before\":" + Hash('A') + ",\"before_composition\":" + Hash('B') + ",\"target\":" + DesignJson()
        + ",\"retained_parts\":[],\"added_goods\":[],\"removed_items\":[],\"changed_item_count\":0}";

    private static string DesignJson() =>
        "{\"input_identity\":" + Hash('3') + ",\"design\":{\"design\":" + Hash('1') + ",\"composition\":" + Hash('2')
        + ",\"kind\":{\"$kind\":\"building\",\"plot_size\":0,\"type\":0,\"facility_tags\":[]},\"parts\":[{\"id\":{\"value\":0},\"definition\":{\"$kind\":\"voxel\",\"id\":{\"value\":\"fixture:material\"}},\"repair_good\":{\"$kind\":\"voxel\",\"definition\":{\"value\":\"fixture:material\"}},\"max_durability\":100,\"hit_weight\":1,\"base_value\":1,\"capabilities\":[],\"missing\":false}],\"max_structure_durability\":100,\"structure_hit_weight\":1,\"mass_tons\":1,\"structural_base_value\":1,\"structural_goods\":[],\"bill_of_materials\":[{\"good\":{\"$kind\":\"voxel\",\"definition\":{\"value\":\"fixture:material\"}},\"quantity\":2}],\"bill_of_materials_item_count\":2,\"return_units\":[],\"can_construct\":true,\"missing_references\":[]},\"assembly_goods\":[]}";

    private static DesignInput BuildingDesign() => new(new(new string('3', 64)), new(new(new string('1', 64)), new(new string('2', 64)),
        new DesignKind.Building(PlotSize.Small, BuildingKind.Production, []),
        [new(new(0), new CoreDefinitionReference.Voxel(new("fixture:material")), new GoodId.Voxel(new("fixture:material")),
            100, 1, new(1), [], false)],
        100, 1, 1, new(1), [], [new(new GoodId.Voxel(new("fixture:material")), 2)], 2, [], true, []), []);

    [TestMethod]
    public void Build_building_and_equipment_round_trip_and_remain_recording_dependencies()
    {
        var input = Input('D');
        CoreCommand[] commands =
        [
            new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(new(new(7), CapabilityKind.Construction), new(3), input, [new(4)], 1)),
            new CoreCommand.StartOwnedJob(new JobRequest.BuildEquipment(new(new(7), CapabilityKind.Construction), input, [new(4)], 1))
        ];
        foreach (var command in commands)
        {
            var payload = CoreCommandEncoding.Encode(command);
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(payload)));
            StringAssert.Contains(payload, command is CoreCommand.StartOwnedJob { Request: JobRequest.BuildBuilding }
                ? "\"kind\":\"build_building\"" : "\"kind\":\"build_equipment\"");
        }
        using var session = new CoreSession(WorldFixture.State());
        foreach (var command in commands)
            session.ExecuteCommand(command, CommandSource.AutomationTest, _ => new CommandEffect.Accepted(session.State.Content, []));
        Assert.IsTrue(session.ReadRecording().RequiredReferences.Designs.Contains(input.Design.Design));
        Assert.IsTrue(session.ReadRecording().RequiredReferences.Inputs.Contains(input.InputIdentity));
    }

    private static DesignInput Input(char identity)
    {
        var design = WorldFixture.Content.Designs.First(design => design.Kind is DesignKind.Vehicle) with
        {
            Design = new(new string(identity, 64)),
            BillOfMaterials = [new(new GoodId.Voxel(new("fixture:recording-material")), 3)]
        };
        return new(new(new string(identity, 64)), design,
            [new(new GoodId.Assembly(design.Composition), WorldFixture.Revision, new(5), 2, false, null)]);
    }

    private static CoreCommand Decode(string payload) => WorldFixture.Success(CoreCommandEncoding.Decode(payload)).Command!;
}

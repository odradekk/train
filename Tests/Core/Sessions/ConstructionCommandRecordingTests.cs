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
        Assert.ThrowsExactly<NotImplementedException>(() => CoreCommandEncoding.Encode(new CoreCommand.StartOwnedJob(
            new JobRequest.Repair(new(new(7), CapabilityKind.Construction), new InstanceId.Building(new(8)), [], 0))));
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
    public void Accepted_rejected_and_faulted_design_inputs_are_retained_only_as_recording_dependencies()
    {
        using var session = new CoreSession(WorldFixture.State());
        foreach (var identity in new[] { 'D', 'E', 'F' })
        {
            var command = new CoreCommand.StartOwnedJob(new JobRequest.BuildVehicle(new(new(7), CapabilityKind.Construction), Input(identity), [], 0));
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
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Diagnostic));
        Assert.IsFalse(saved.References.Designs.Contains(Input('E').Design.Design));
        Assert.IsFalse(saved.References.Designs.Contains(Input('F').Design.Design));
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

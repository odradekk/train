using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreFormationEncodingTests
{
    [TestMethod]
    public void Replacement_decodes_vehicle_order_and_canonical_fields()
    {
        const string payload = """{"ordered_vehicles":[9,2,7],"train":18446744073709551615,"kind":"train.replace_formation","input_format_version":2}""";
        var decoded = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value;
        Assert.IsNull(decoded.InputIdentity);
        var command = (CoreCommand.ReplaceTrainFormation)decoded.Command!;
        Assert.AreEqual(new TrainId(ulong.MaxValue), command.Train);
        CollectionAssert.AreEqual(new ulong[] { 9, 2, 7 }, command.OrderedVehicles.Select(v => v.Value).ToArray());
        Assert.AreEqual("""{"input_format_version":2,"kind":"train.replace_formation","train":18446744073709551615,"ordered_vehicles":[9,2,7]}""", CoreCommandEncoding.Encode(command));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("[2]")]
    [DataRow("[2,2]")]
    public void Replacement_preserves_business_inputs_for_session_validation(string vehicles)
    {
        var payload = "{\"input_format_version\":2,\"kind\":\"train.replace_formation\",\"train\":1,\"ordered_vehicles\":" + vehicles + "}";
        var decoded = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value;
        Assert.IsInstanceOfType<CoreCommand.ReplaceTrainFormation>(decoded.Command);
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(decoded.Command!));
    }

    [TestMethod]
    [DataRow("\"train\":0,\"ordered_vehicles\":[2]", "/train")]
    [DataRow("\"train\":-1,\"ordered_vehicles\":[2]", "/train")]
    [DataRow("\"train\":1.5,\"ordered_vehicles\":[2]", "/train")]
    [DataRow("\"train\":18446744073709551616,\"ordered_vehicles\":[2]", "/train")]
    [DataRow("\"ordered_vehicles\":[2]", "/train")]
    [DataRow("\"train\":1", "/ordered_vehicles")]
    [DataRow("\"train\":1,\"ordered_vehicles\":null", "/ordered_vehicles")]
    [DataRow("\"train\":1,\"ordered_vehicles\":[2,0]", "/ordered_vehicles/1")]
    [DataRow("\"train\":1,\"ordered_vehicles\":[-1]", "/ordered_vehicles/0")]
    [DataRow("\"train\":1,\"ordered_vehicles\":[1.5]", "/ordered_vehicles/0")]
    [DataRow("\"train\":1,\"ordered_vehicles\":[18446744073709551616]", "/ordered_vehicles/0")]
    [DataRow("\"train\":1,\"train\":2,\"ordered_vehicles\":[2]", "/train")]
    [DataRow("\"train\":1,\"ordered_vehicles\":[2],\"ordered_vehicles\":[3]", "/ordered_vehicles")]
    [DataRow("\"train\":1,\"ordered_vehicles\":[2],\"extra\":0", "/extra")]
    public void Replacement_rejects_malformed_or_ambiguous_fields(string fields, string field)
    {
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(
            "{\"input_format_version\":2,\"kind\":\"train.replace_formation\"," + fields + "}");
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Replacement_requires_initialized_arrays_and_nonzero_identities_for_encoding()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.ReplaceTrainFormation(new(1), default)));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.ReplaceTrainFormation(new(1), [new(0)])));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.ReplaceTrainFormation(new(0), [new(1)])));
    }
}

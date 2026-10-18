using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class FoundingCommandEncodingTests
{
    [TestMethod]
    public void Founding_has_a_fixed_vector_and_preserves_full_ulong_identities()
    {
        const string payload = """{"input_format_version":2,"kind":"settlement.found","node":18446744073709551615,"material_source":18446744073709551614}""";
        var command = new CoreCommand.FoundSettlement(new(ulong.MaxValue), new(ulong.MaxValue - 1));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
        Assert.AreEqual(command, Decode(payload));
        Assert.AreEqual(command, Decode("""{"material_source":18446744073709551614,"node":18446744073709551615,"kind":"settlement.found","input_format_version":2}"""));
    }

    [TestMethod]
    [DataRow("\"material_source\":2", "/node")]
    [DataRow("\"node\":1", "/material_source")]
    [DataRow("\"node\":1,\"node\":3,\"material_source\":2", "/node")]
    [DataRow("\"node\":1,\"material_source\":2,\"material_source\":3", "/material_source")]
    [DataRow("\"node\":1,\"material_source\":2,\"extra\":3", "/extra")]
    [DataRow("\"node\":0,\"material_source\":2", "/node")]
    [DataRow("\"node\":1,\"material_source\":0", "/material_source")]
    [DataRow("\"node\":-1,\"material_source\":2", "/node")]
    [DataRow("\"node\":1,\"material_source\":-1", "/material_source")]
    [DataRow("\"node\":18446744073709551616,\"material_source\":2", "/node")]
    [DataRow("\"node\":1,\"material_source\":18446744073709551616", "/material_source")]
    [DataRow("\"node\":1.5,\"material_source\":2", "/node")]
    [DataRow("\"node\":1,\"material_source\":1.5", "/material_source")]
    [DataRow("\"node\":null,\"material_source\":2", "/node")]
    [DataRow("\"node\":1,\"material_source\":\"2\"", "/material_source")]
    public void Founding_payload_fields_are_strict(string fields, string field)
    {
        var payload = "{\"input_format_version\":2,\"kind\":\"settlement.found\"," + fields + "}";
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Zero_founding_identities_cannot_be_encoded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.FoundSettlement(new(0), new(1))));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.FoundSettlement(new(1), new(0))));
    }

    private static CoreCommand Decode(string payload)
    {
        var decoded = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value;
        Assert.IsNull(decoded.InputIdentity);
        return decoded.Command!;
    }
}

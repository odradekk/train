// Train.Tests.Core.Simulation 验证命令 v2 的规范文本、摘要和严格读取契约。
// 预期 JSON 是手写协议向量；摘要仅由标准 SHA-256 对这些字面文本计算。

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationCommandEncodingTests
{
    private readonly ResourceCommandCodec _codec = new();

    public static IEnumerable<object[]> CanonicalCommands()
    {
        yield return [new NoChangeCommand(), "{\"input_format_version\":2,\"kind\":\"fixture.no_change\"}", "360A66E43D7828BE95C4EEF9D923A04986D8D37B0ECD077AA9023276EB9BFEA3"];
        yield return [new CreateEntityCommand(), "{\"input_format_version\":2,\"kind\":\"fixture.create_entity\"}", "15A034BB1282EEF9F312DDCDFDD7C66AEF67304EF160F721E4189BFB9C84D250"];
        yield return [new DeleteEntityCommand(7), "{\"input_format_version\":2,\"kind\":\"fixture.delete_entity\",\"entity_id\":7}", "37412D8F4E8D51CB88C5828B94CDDB8403F0B285E5E08F5E63E75D0F160E8099"];
        yield return [new CreateThenRejectCommand(), "{\"input_format_version\":2,\"kind\":\"fixture.create_then_reject\"}", "674EA3FB68A79B74BE70ABD9DF08B0E57B15CDF9D0C02B83A37C9933FA02DC2B"];
        yield return [new InvalidCandidateCommand(), "{\"input_format_version\":2,\"kind\":\"fixture.invalid_candidate\"}", "5C03F55CDCD32A9707274436B69979806112CF56EC66A6DED19861E810BA1455"];
    }

    [DataTestMethod]
    [DynamicData(nameof(CanonicalCommands), DynamicDataSourceType.Method)]
    public void Each_resource_kind_has_a_fixed_canonical_text_and_payload_hash(
        CoreCommand command, string expectedText, string expectedSha256)
    {
        var encoded = _codec.Encode(command);

        Assert.AreEqual(expectedText, encoded.CanonicalText);
        Assert.AreEqual(expectedSha256, encoded.Sha256);
        Assert.AreEqual(Sha256(expectedText), expectedSha256);
    }

    [TestMethod]
    public void Scalars_sets_and_sequences_have_fixed_field_order_and_semantics()
    {
        var command = new EncodingProbeCommand(long.MinValue, 0.84551240822557006, "车\"厢\n", true,
            ImmutableHashSet.Create(9UL, 2UL), ImmutableArray.Create(9UL, 2UL));
        const string expected = "{\"input_format_version\":2,\"kind\":\"fixture.encoding_probe\",\"signed\":-9223372036854775808,\"ratio\":0.8455124082255701,\"label\":\"车\\\"厢\\n\",\"enabled\":true,\"entity_ids\":[2,9],\"route\":[9,2]}";
        const string expectedSha256 = "C9EBD33B7F3EC6A26879FBA7ABA1D91BB115E2A828351E86E055D03D6561E37A";

        var encoded = _codec.Encode(command);

        Assert.AreEqual(expected, encoded.CanonicalText);
        Assert.AreEqual(expectedSha256, encoded.Sha256);
        Assert.AreEqual(Sha256(expected), expectedSha256);
    }

    [TestMethod]
    public void Decode_accepts_arbitrary_field_order_and_reencoding_restores_canonical_order()
    {
        const string reordered = "{\"route\":[9,2],\"kind\":\"fixture.encoding_probe\",\"enabled\":true,\"entity_ids\":[9,2],\"label\":\"车\\\"厢\\n\",\"ratio\":0.84551240822557006,\"input_format_version\":2,\"signed\":-9223372036854775808}";
        const string expected = "{\"input_format_version\":2,\"kind\":\"fixture.encoding_probe\",\"signed\":-9223372036854775808,\"ratio\":0.8455124082255701,\"label\":\"车\\\"厢\\n\",\"enabled\":true,\"entity_ids\":[2,9],\"route\":[9,2]}";

        var decoded = AssertSuccess<EncodingProbeCommand>(_codec.Decode(reordered));

        Assert.AreEqual(expected, _codec.Encode(decoded).CanonicalText);
    }

    [TestMethod]
    public void Version_one_is_an_unknown_format()
    {
        var failure = AssertFailure(_codec.Decode("{\"input_format_version\":1,\"kind\":\"fixture.no_change\"}"));
        AssertError(failure, DeterminismErrorKind.UnknownFormat, "unsupported_input_format", "/input_format_version");
    }

    [TestMethod]
    public void An_unknown_kind_is_an_unknown_format()
    {
        var failure = AssertFailure(_codec.Decode("{\"input_format_version\":2,\"kind\":\"fixture.future\"}"));
        Assert.AreEqual(DeterminismErrorKind.UnknownFormat, failure.Kind);
        Assert.AreEqual("/kind", failure.Path);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Code));
    }

    public static IEnumerable<object[]> InvalidEnvelopes()
    {
        yield return ["{\"kind\":\"fixture.no_change\"}", DeterminismErrorKind.UnknownFormat, "/input_format_version"];
        yield return ["{\"input_format_version\":2}", DeterminismErrorKind.UnknownFormat, "/kind"];
        yield return ["{", DeterminismErrorKind.InvalidData, ""];
        yield return ["[]", DeterminismErrorKind.InvalidData, ""];
        yield return ["{\"input_format_version\":2.0,\"kind\":\"fixture.no_change\"}", DeterminismErrorKind.InvalidData, "/input_format_version"];
        yield return ["{\"input_format_version\":2,\"kind\":7}", DeterminismErrorKind.InvalidData, "/kind"];
        yield return ["{\"input_format_version\":2,\"kind\":\"\\uD800\"}", DeterminismErrorKind.InvalidData, "/kind"];
    }

    [DataTestMethod]
    [DynamicData(nameof(InvalidEnvelopes), DynamicDataSourceType.Method)]
    public void Missing_malformed_or_wrongly_typed_envelopes_return_stable_errors(
        string json, DeterminismErrorKind expectedKind, string expectedPath)
    {
        var failure = AssertFailure(_codec.Decode(json));

        Assert.AreEqual(expectedKind, failure.Kind);
        Assert.AreEqual(expectedPath, failure.Path);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Code));
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.no_change\",\"extra\":1}", "/extra"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.delete_entity\"}", "/entity_id"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.delete_entity\",\"entity_id\":\"7\"}", "/entity_id"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.delete_entity\",\"entity_id\":0}", "/entity_id"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.delete_entity\",\"entity_id\":18446744073709551616}", "/entity_id"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.encoding_probe\",\"signed\":9223372036854775808,\"ratio\":1.5,\"label\":\"x\",\"enabled\":true,\"entity_ids\":[1],\"route\":[2]}", "/signed"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.encoding_probe\",\"signed\":1,\"ratio\":1e400,\"label\":\"x\",\"enabled\":true,\"entity_ids\":[1],\"route\":[2]}", "/ratio"];
        yield return ["{\"input_format_version\":2,\"kind\":\"fixture.encoding_probe\",\"signed\":1,\"ratio\":1.5,\"label\":\"x\",\"enabled\":true,\"entity_ids\":[0],\"route\":[2]}", "/entity_ids/0"];
    }

    [DataTestMethod]
    [DynamicData(nameof(InvalidDocuments), DynamicDataSourceType.Method)]
    public void Known_format_field_type_and_range_errors_are_invalid_data(string json, string expectedPath)
    {
        var failure = AssertFailure(_codec.Decode(json));
        Assert.AreEqual(DeterminismErrorKind.InvalidData, failure.Kind);
        Assert.AreEqual(expectedPath, failure.Path);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Code));
    }

    [TestMethod]
    public void Duplicate_public_or_domain_fields_are_invalid_data()
    {
        foreach (var (json, path) in new[]
                 {
                     ("{\"input_format_version\":2,\"input_format_version\":2,\"kind\":\"fixture.no_change\"}", "/input_format_version"),
                     ("{\"input_format_version\":2,\"kind\":\"fixture.delete_entity\",\"entity_id\":7,\"entity_id\":8}", "/entity_id")
                 })
        {
            var failure = AssertFailure(_codec.Decode(json));
            Assert.AreEqual(DeterminismErrorKind.InvalidData, failure.Kind);
            Assert.AreEqual(path, failure.Path);
            Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Code));
        }
    }

    private static T AssertSuccess<T>(DeterminismResult<CoreCommand> result) where T : CoreCommand
    {
        Assert.IsInstanceOfType<DeterminismResult<CoreCommand>.Success>(result);
        Assert.IsInstanceOfType<T>(((DeterminismResult<CoreCommand>.Success)result).Value);
        return (T)((DeterminismResult<CoreCommand>.Success)result).Value;
    }

    private static DeterminismError AssertFailure(DeterminismResult<CoreCommand> result)
    {
        Assert.IsInstanceOfType<DeterminismResult<CoreCommand>.Failure>(result);
        return ((DeterminismResult<CoreCommand>.Failure)result).Error;
    }

    private static void AssertError(DeterminismError actual, DeterminismErrorKind kind, string code, string path)
    {
        Assert.AreEqual(kind, actual.Kind);
        Assert.AreEqual(code, actual.Code);
        Assert.AreEqual(path, actual.Path);
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

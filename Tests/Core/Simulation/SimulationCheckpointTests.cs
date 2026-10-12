// Train.Core.Tests.Simulation 描述规范检查点文档和首差异比较的函数例子。
// 文档及 SHA-256 预期均为独立字面，比较例子不信任调用方摘要。

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationCheckpointTests
{
    private const string CheckpointText = "{\"checkpoint_format_version\":1,\"objects\":[{\"kind\":\"fixture.entity\",\"id\":10,\"fields\":{\"holding\":3}},{\"kind\":\"fixture.entity\",\"id\":20,\"fields\":{\"holding\":null}},{\"kind\":\"fixture.resources\",\"id\":0,\"fields\":{\"demands\":[{\"allow_partial\":true,\"amount\":3,\"entity_id\":10,\"priority\":0,\"request_id\":1},{\"allow_partial\":false,\"amount\":4,\"entity_id\":20,\"priority\":2,\"request_id\":1}],\"stock\":9}},{\"kind\":\"simulation\",\"id\":0,\"fields\":{\"boundary_sequence\":2,\"completed_steps\":4,\"fault\":null,\"generator_version\":\"test-generator-v1\",\"next_command_id\":3,\"next_entity_id\":21,\"transaction_count\":6,\"world_seed\":42}}]}";
    private const string CheckpointSha = "92A4352EDCDDC8803191ADB3366CC247B3ED047231DAF73E14209B2E81D0FD07";

    [TestMethod]
    public void Projection_preserves_entities_dictionary_presence_and_all_demands()
    {
        var objects = new ResourceStateCodec().ProjectDomainForTest(SimulationStateCodecTests.State());
        Assert.AreEqual(3, objects.Length);
        Assert.AreEqual("fixture.resources", objects[0].Identity.Kind);
        Assert.AreEqual("{\"demands\":[{\"allow_partial\":true,\"amount\":3,\"entity_id\":10,\"priority\":0,\"request_id\":1},{\"allow_partial\":false,\"amount\":4,\"entity_id\":20,\"priority\":2,\"request_id\":1}],\"stock\":9}", objects[0].Fields.GetRawText());
        Assert.AreEqual("{\"holding\":3}", objects[1].Fields.GetRawText());
        Assert.AreEqual("{\"holding\":null}", objects[2].Fields.GetRawText());
    }

    [TestMethod]
    public void CreateCheckpoint_matches_independent_document_and_standard_sha256_vectors()
    {
        var point = new ResourceStateCodec().CreateCheckpoint(SimulationStateCodecTests.State());
        Assert.AreEqual(new SimulationPosition(4, 2), point.Position);
        Assert.AreEqual(CheckpointText, point.CanonicalText);
        Assert.AreEqual(CheckpointSha, point.Sha256);
    }

    [TestMethod]
    public void Decode_recomputes_the_digest_and_position_from_the_document()
    {
        var point = ((DeterminismResult<AuthoritativeCheckpoint>.Success)SimulationCheckpoints.Decode(CheckpointText)).Value;
        Assert.AreEqual(new SimulationPosition(4, 2), point.Position);
        Assert.AreEqual(CheckpointSha, point.Sha256);
    }

    [TestMethod]
    public void All_scalar_clauses_and_nested_objects_have_a_fixed_canonical_document()
    {
        const string inputFields = "{\"z\":[null,true,false,-9223372036854775808,18446744073709551615,0.84551240822557006,\"车\\n厢\"],\"a\":{\"z\":2,\"a\":1}}";
        const string expectedFields = "{\"a\":{\"a\":1,\"z\":2},\"z\":[null,true,false,-9223372036854775808,18446744073709551615,0.8455124082255701,\"车\\n厢\"]}";
        var input = AddEntity(CheckpointText, 2, inputFields);
        var expected = AddEntity(CheckpointText, 2, expectedFields);
        var result = SimulationCheckpoints.Decode(input);

        Assert.IsInstanceOfType<DeterminismResult<AuthoritativeCheckpoint>.Success>(result);
        var point = ((DeterminismResult<AuthoritativeCheckpoint>.Success)result).Value;
        Assert.AreEqual(expected, point.CanonicalText);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), point.Sha256);
    }

    [DataTestMethod]
    [DataRow("{\"checkpoint_format_version\":2,\"objects\":[]}", "unsupported_checkpoint_format", "/checkpoint_format_version")]
    [DataRow("{\"checkpoint_format_version\":1,\"objects\":[],\"extra\":0}", "unknown_field", "/extra")]
    [DataRow("{\"checkpoint_format_version\":1}", "missing_field", "/objects")]
    [DataRow("{\"checkpoint_format_version\":1,\"objects\":[{\"kind\":\"x\",\"id\":0,\"fields\":{}},{\"kind\":\"x\",\"id\":0,\"fields\":{}}]}", "duplicate_identity", "/objects/1")]
    [DataRow("{\"checkpoint_format_version\":1,\"objects\":[{\"kind\":\"x\",\"id\":0,\"fields\":{\"value\":1e999}}]}", "non_finite_number", "/objects/0/fields/value")]
    [DataRow("{\"checkpoint_format_version\":1,\"objects\":[{\"kind\":\"x\",\"id\":0,\"fields\":{\"\\uD800\":true}}]}", "invalid_string", "/objects/0/fields")]
    public void Decode_rejects_invalid_checkpoint_documents(string json, string code, string path)
    {
        var error = ((DeterminismResult<AuthoritativeCheckpoint>.Failure)SimulationCheckpoints.Decode(json)).Error;
        Assert.AreEqual(code, error.Code); Assert.AreEqual(path, error.Path);
    }

    [TestMethod]
    public void Equivalent_object_enumeration_is_canonicalized_before_comparison()
    {
        var shuffled = CheckpointText.Replace("[{\"kind\":\"fixture.entity\",\"id\":10,\"fields\":{\"holding\":3}},{\"kind\":\"fixture.entity\",\"id\":20,\"fields\":{\"holding\":null}}", "[{\"kind\":\"fixture.entity\",\"id\":20,\"fields\":{\"holding\":null}},{\"kind\":\"fixture.entity\",\"id\":10,\"fields\":{\"holding\":3}}", StringComparison.Ordinal);
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(SimulationCheckpoints.Compare(Trace(CheckpointText), Trace(shuffled)));
    }

    [TestMethod]
    public void Array_order_is_authoritative_and_reports_its_first_index()
    {
        const string first = "{\"allow_partial\":true,\"amount\":3,\"entity_id\":10,\"priority\":0,\"request_id\":1}";
        const string second = "{\"allow_partial\":false,\"amount\":4,\"entity_id\":20,\"priority\":2,\"request_id\":1}";
        var changed = CheckpointText.Replace($"[{first},{second}]", $"[{second},{first}]", StringComparison.Ordinal);
        var difference = Different(SimulationCheckpoints.Compare(Trace(CheckpointText), Trace(changed)));
        Assert.AreEqual(new CheckpointObjectId("fixture.resources", 0), difference.Object);
        Assert.AreEqual("/demands/0/allow_partial", difference.FieldPath);
    }

    [TestMethod]
    public void Comparison_uses_actual_fields_when_supplied_digests_are_equal()
    {
        var changedText = CheckpointText.Replace("\"stock\":9", "\"stock\":8", StringComparison.Ordinal);
        var left = new AuthoritativeCheckpoint(new(4, 2), CheckpointText, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var right = new AuthoritativeCheckpoint(new(4, 2), changedText, left.Sha256);
        Assert.AreEqual("/stock", Different(SimulationCheckpoints.Compare(Trace(left), Trace(right))).FieldPath);
    }

    [TestMethod]
    public void Missing_field_is_distinct_from_an_existing_json_null()
    {
        var missing = CheckpointText.Replace("{\"holding\":null}", "{}", StringComparison.Ordinal);
        var difference = Different(SimulationCheckpoints.Compare(Trace(CheckpointText), Trace(missing)));
        Assert.AreEqual("null", difference.LeftValue); Assert.IsNull(difference.RightValue);
    }

    [TestMethod]
    public void Earliest_difference_uses_position_then_kind_id_and_ordinal_field_name()
    {
        var later = CheckpointText.Replace("\"stock\":9", "\"stock\":8", StringComparison.Ordinal);
        var earlier = CheckpointText
            .Replace("\"holding\":3", "\"holding\":4", StringComparison.Ordinal)
            .Replace("\"holding\":null", "\"holding\":8", StringComparison.Ordinal)
            .Replace("\"stock\":9", "\"stock\":7", StringComparison.Ordinal);
        var left = Trace(Point(3, 0, CheckpointText), Point(4, 2, CheckpointText));
        var right = Trace(Point(3, 0, earlier), Point(4, 2, later));
        var difference = Different(SimulationCheckpoints.Compare(left, right));
        Assert.AreEqual(new SimulationPosition(3, 0), difference.Position);
        Assert.AreEqual(new CheckpointObjectId("fixture.entity", 10), difference.Object);
        Assert.AreEqual("/holding", difference.FieldPath);
    }

    [TestMethod]
    public void Object_numeric_id_precedes_later_numeric_ids()
    {
        var left = AddEntity(CheckpointText, 2, "{\"holding\":1}");
        var right = AddEntity(CheckpointText.Replace("\"holding\":3", "\"holding\":4", StringComparison.Ordinal), 2, "{\"holding\":2}");
        var difference = Different(SimulationCheckpoints.Compare(Trace(left), Trace(right)));
        Assert.AreEqual(new CheckpointObjectId("fixture.entity", 2), difference.Object);
        Assert.AreEqual("/holding", difference.FieldPath);
    }

    [TestMethod]
    public void Nested_objects_use_ordinal_fields_then_array_indices_recursively()
    {
        var left = AddEntity(CheckpointText, 2, "{\"nested\":{\"z\":[0,1],\"a\":true},\"text\":\"例子\"}");
        var right = AddEntity(CheckpointText, 2, "{\"nested\":{\"z\":[0,2],\"a\":false},\"text\":\"不同\"}");
        var difference = Different(SimulationCheckpoints.Compare(Trace(left), Trace(right)));
        Assert.AreEqual("/nested/a", difference.FieldPath);
        Assert.AreEqual("true", difference.LeftValue);
        Assert.AreEqual("false", difference.RightValue);
    }

    [TestMethod]
    public void Nested_array_difference_reports_the_first_changed_index()
    {
        var left = AddEntity(CheckpointText, 2, "{\"nested\":{\"values\":[0,1,2]}}");
        var right = AddEntity(CheckpointText, 2, "{\"nested\":{\"values\":[0,9,8]}}");
        var difference = Different(SimulationCheckpoints.Compare(Trace(left), Trace(right)));
        Assert.AreEqual("/nested/values/1", difference.FieldPath);
        Assert.AreEqual("1", difference.LeftValue);
        Assert.AreEqual("9", difference.RightValue);
    }

    [TestMethod]
    public void Missing_object_reports_its_identity_and_empty_field_path()
    {
        var withoutEntity20 = CheckpointText.Replace(",{\"kind\":\"fixture.entity\",\"id\":20,\"fields\":{\"holding\":null}}", "", StringComparison.Ordinal);
        var difference = Different(SimulationCheckpoints.Compare(Trace(CheckpointText), Trace(withoutEntity20)));
        Assert.AreEqual(new CheckpointObjectId("fixture.entity", 20), difference.Object);
        Assert.AreEqual("", difference.FieldPath);
        Assert.AreEqual("{\"holding\":null}", difference.LeftValue);
        Assert.IsNull(difference.RightValue);
    }

    [TestMethod]
    public void A_missing_sampling_point_reports_no_object_or_field()
    {
        var difference = Different(SimulationCheckpoints.Compare(Trace(Point(4, 2, CheckpointText)), Trace()));
        Assert.IsNull(difference.Object); Assert.AreEqual("", difference.FieldPath);
        Assert.AreEqual(CheckpointText, difference.LeftValue); Assert.IsNull(difference.RightValue);
    }

    [DataTestMethod]
    [DataRow("source", "source_mismatch", "/identity/source_sha256")]
    [DataRow("build", "build_mismatch", "/identity/build_sha256")]
    [DataRow("content", "semantic_content_mismatch", "/identity/semantic_content_sha256")]
    [DataRow("environment", "environment_mismatch", "/identity/environment")]
    [DataRow("missing_source", "missing_identity", "/identity/source_sha256")]
    [DataRow("invalid_build", "invalid_identity", "/identity/build_sha256")]
    public void Missing_or_incompatible_identity_is_not_comparable(string field, string code, string path)
    {
        var rightIdentity = field switch
        {
            "source" => Identity() with { SourceSha256 = Digest('B') },
            "build" => Identity() with { BuildSha256 = Digest('B') },
            "content" => Identity() with { SemanticContentSha256 = Digest('B') },
            "missing_source" => Identity() with { SourceSha256 = "" },
            "invalid_build" => Identity() with { BuildSha256 = "xyz" },
            _ => Identity() with { Environment = new("linux", "x64", "10.0.0") }
        };
        var result = (CheckpointComparison.NotComparable)SimulationCheckpoints.Compare(Trace(), new(rightIdentity, ImmutableArray<AuthoritativeCheckpoint>.Empty));
        Assert.AreEqual(code, result.Error.Code); Assert.AreEqual(path, result.Error.Path);
    }

    private static CheckpointDifference Different(CheckpointComparison value) => ((CheckpointComparison.Different)value).Difference;

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-a-runtime")]
    public void Matching_invalid_runtime_identity_is_not_evidence_of_equality(string runtime)
    {
        var identity = Identity() with { Environment = new("windows", "x64", runtime) };
        var trace = new CheckpointTrace(identity, ImmutableArray<AuthoritativeCheckpoint>.Empty);
        var result = (CheckpointComparison.NotComparable)SimulationCheckpoints.Compare(trace, trace);
        Assert.AreEqual(DeterminismErrorKind.IncompatibleContext, result.Error.Kind);
        Assert.AreEqual("/identity/environment/dotnet_runtime", result.Error.Path);
    }
    private static AuthoritativeCheckpoint Point(ulong step, ulong sequence, string text)
    {
        var synchronized = text
            .Replace("\"boundary_sequence\":2", $"\"boundary_sequence\":{sequence}", StringComparison.Ordinal)
            .Replace("\"completed_steps\":4", $"\"completed_steps\":{step}", StringComparison.Ordinal)
            .Replace("\"transaction_count\":6", $"\"transaction_count\":{step + 2}", StringComparison.Ordinal);
        return new(new(step, sequence), synchronized, Digest('A'));
    }
    private static string AddEntity(string text, ulong id, string fields) => text.Replace(
        "\"objects\":[", $"\"objects\":[{{\"kind\":\"fixture.entity\",\"id\":{id},\"fields\":{fields}}},", StringComparison.Ordinal);
    private static CheckpointTrace Trace(params AuthoritativeCheckpoint[] points) => new(Identity(), points.ToImmutableArray());
    private static CheckpointTrace Trace(string text) => Trace(Point(4, 2, text));
    private static DeterminismIdentity Identity() => new(Digest('A'), Digest('A'), Digest('A'), new("windows", "x64", "10.0.0"));
    private static string Digest(char value) => new(value, 64);
}

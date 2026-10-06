// Core 内容模块测试：验证内容发现、解析与重载的无引擎契约。
// 测试输入保持最小，并与旧世界运行态隔离。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
public sealed class ModManifestTests
{
    [TestMethod]
    public void Valid_manifest_trims_text_and_accepts_optional_description()
    {
        Assert.IsTrue(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(
                ModFixtures.SampleUuid,
                name: "  铜矿扩展  ",
                version: "1.2.3-alpha.1+build.7",
                authors: new[] { "  作者甲  ", "Author-B" },
                description: "  说明  ")),
            "pretty/mod.json",
            out var withDescription,
            out _));
        Assert.AreEqual(ModFixtures.SampleUuid, withDescription!.Id);
        Assert.AreEqual("铜矿扩展", withDescription.Name);
        Assert.AreEqual("1.2.3-alpha.1+build.7", withDescription.Version);
        CollectionAssert.AreEqual(new[] { "作者甲", "Author-B" }, withDescription.Authors.ToArray());
        Assert.AreEqual("说明", withDescription.Description);

        Assert.IsTrue(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, description: null)),
            "pretty/mod.json",
            out var omitted,
            out _));
        Assert.AreEqual("", omitted!.Description);

        Assert.IsTrue(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, description: "   ")),
            "pretty/mod.json",
            out var blank,
            out _));
        Assert.AreEqual("", blank!.Description);
    }

    [TestMethod]
    public void Strict_schema_rejects_unknown_duplicate_and_malformed_manifests()
    {
        var cases = new[]
        {
            "{ /* comment */ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"], }",
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"], \"extra\": 1 }",
            "{ \"schema_version\": 1, \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            "{ \"Schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            "{ \"schema_version\": 2, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            "{ \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\" }"
        };

        foreach (var json in cases)
        {
            Assert.IsFalse(ModManifest.TryParse(Utf8(json), "bad/mod.json", out var manifest, out var diagnostics), json);
            Assert.IsNull(manifest, json);
            Assert.IsTrue(diagnostics.Count > 0, json);
        }
    }

    [TestMethod]
    public void Canonical_non_nil_uuid_is_required()
    {
        var invalids = new[]
        {
            "00000000-0000-0000-0000-000000000000",
            "550E8400-E29B-41D4-A716-446655440000",
            "550e8400e29b41d4a716446655440000",
            "{550e8400-e29b-41d4-a716-446655440000}",
            "urn:uuid:550e8400-e29b-41d4-a716-446655440000",
            "not-a-uuid",
            ""
        };

        foreach (var id in invalids)
        {
            Assert.IsFalse(
                ModManifest.TryParse(Utf8(ModFixtures.Manifest(id)), "bad/mod.json", out var manifest, out var diagnostics),
                id);
            Assert.IsNull(manifest, id);
            Assert.IsTrue(diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UuidInvalid), id);
        }

        Assert.IsTrue(ModManifest.TryCanonicalUuid(ModFixtures.SampleUuid, out var canonical));
        Assert.AreEqual(ModFixtures.SampleUuid, canonical);
        Assert.IsFalse(ModManifest.TryCanonicalUuid("00000000-0000-0000-0000-000000000000", out _));
    }

    [TestMethod]
    public void SemVer_2_0_accepts_prerelease_and_build_and_rejects_invalid_forms()
    {
        var valid = new[]
        {
            "0.0.0",
            "1.0.0",
            "1.2.3",
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-0.3.7",
            "1.0.0-x.7.z.92",
            "1.0.0-alpha+001",
            "1.0.0+20130313144700",
            "1.0.0-beta+exp.sha.5114f85",
            "1.0.0-alpha.beta",
            "1.0.0-rc.1+build.1"
        };
        foreach (var version in valid)
            Assert.IsTrue(ModManifest.TryParse(Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, version: version)), "ok/mod.json", out _, out _), version);

        var invalid = new[]
        {
            "1",
            "1.0",
            "v1.0.0",
            "01.0.0",
            "1.00.0",
            "1.0.0-",
            "1.0.0+",
            "1.0.0-01",
            "1.0.0-alpha.",
            "1.0.0+build.",
            "1.0.0_beta",
            "1.0.0-",
            ""
        };
        foreach (var version in invalid)
        {
            Assert.IsFalse(
                ModManifest.TryParse(Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, version: version)), "bad/mod.json", out _, out var diagnostics),
                version);
            Assert.IsTrue(diagnostics.Any(item => item.Code == ContentDiagnosticCodes.VersionInvalid), version);
        }
    }

    [TestMethod]
    public void Unicode_name_author_and_description_boundaries_are_enforced()
    {
        Assert.IsTrue(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(
                ModFixtures.SampleUuid,
                name: new string('名', 100),
                authors: new[] { new string('作', 100) },
                description: new string('说', 4000))),
            "ok/mod.json",
            out _,
            out _));

        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, name: " ")),
            "bad/mod.json",
            out _,
            out var nameDiagnostics));
        Assert.IsTrue(nameDiagnostics.Any(item => item.Code == ContentDiagnosticCodes.NameInvalid));

        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, name: new string('名', 101))),
            "bad/mod.json",
            out _,
            out _));
        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, authors: new[] { new string('作', 101) })),
            "bad/mod.json",
            out _,
            out _));
        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, description: new string('说', 4001))),
            "bad/mod.json",
            out _,
            out _));
    }

    [TestMethod]
    public void Authors_must_be_one_to_sixteen_unique_trimmed_names()
    {
        var sixteen = Enumerable.Range(1, 16).Select(i => $"作者{i}").ToArray();
        Assert.IsTrue(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, authors: sixteen)),
            "ok/mod.json",
            out var parsed,
            out _));
        Assert.AreEqual(16, parsed!.Authors.Count);

        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, authors: sixteen.Append("extra").ToArray())),
            "bad/mod.json",
            out _,
            out var tooMany));
        Assert.IsTrue(tooMany.Any(item => item.Code == ContentDiagnosticCodes.AuthorsInvalid));

        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, authors: Array.Empty<string>())),
            "bad/mod.json",
            out _,
            out _));
        Assert.IsFalse(ModManifest.TryParse(
            Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, authors: new[] { "Alice", "  Alice  " })),
            "bad/mod.json",
            out _,
            out var duplicates));
        Assert.IsTrue(duplicates.Any(item => item.Code == ContentDiagnosticCodes.AuthorsInvalid));
    }

    [TestMethod]
    public void Missing_required_fields_each_produce_a_bounded_diagnostic()
    {
        AssertMissing(
            "{ \"schema_version\": 1, \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            ContentDiagnosticCodes.UuidInvalid,
            "/id");
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            ContentDiagnosticCodes.NameInvalid,
            "/name");
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"authors\": [\"A\"] }",
            ContentDiagnosticCodes.VersionInvalid,
            "/version");
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\" }",
            ContentDiagnosticCodes.AuthorsInvalid,
            "/authors");
    }

    [TestMethod]
    public void Wrong_types_and_invalid_values_each_produce_their_own_diagnostic()
    {
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": 1, \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            ContentDiagnosticCodes.UuidInvalid,
            "/id");
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": 1, \"version\": \"1.0.0\", \"authors\": [\"A\"] }",
            ContentDiagnosticCodes.NameInvalid,
            "/name");
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": 1, \"authors\": [\"A\"] }",
            ContentDiagnosticCodes.VersionInvalid,
            "/version");
        AssertMissing(
            "{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": \"A\" }",
            ContentDiagnosticCodes.AuthorsInvalid,
            "/authors");

        Assert.IsFalse(ModManifest.TryParse(
            Utf8("{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"version\": \"1.0.0\", \"authors\": [\"A\"], \"extra\": true }"),
            "bad/mod.json",
            out var manifest,
            out var diagnostics));
        Assert.IsNull(manifest);
        Assert.IsTrue(diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnknownField && item.JsonPath == "/extra"));
        Assert.IsTrue(diagnostics.Any(item => item.Code == ContentDiagnosticCodes.NameInvalid && item.JsonPath == "/name"));
    }

    private static void AssertMissing(string json, string code, string jsonPath)
    {
        Assert.IsFalse(ModManifest.TryParse(Utf8(json), "bad/mod.json", out var manifest, out var diagnostics), json);
        Assert.IsNull(manifest, json);
        Assert.IsTrue(diagnostics.Any(item =>
            item.Code == code &&
            item.Source == "bad/mod.json" &&
            item.JsonPath == jsonPath), json);
    }

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

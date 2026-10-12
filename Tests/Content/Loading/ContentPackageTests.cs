using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentPackageTests
{
    private const string A = "11111111-1111-4111-8111-111111111111";
    private const string B = "22222222-2222-4222-8222-222222222222";
    private const string D = "33333333-3333-4333-8333-333333333333";

    [TestMethod]
    [DataRow("duplicate", "invalid_json")]
    [DataRow("unknown", "invalid_field")]
    [DataRow("uuid", "invalid_field")]
    [DataRow("newline", "invalid_field")]
    [DataRow("malformed", "invalid_json")]
    public void Discover_rejects_strict_manifest_errors(string mutation, string code)
    {
        var fixture = new ContentFixture();
        var json = Manifest(A, "1.0.0");
        json = mutation switch
        {
            "duplicate" => json.Insert(1, "\"schema_version\":2,"),
            "unknown" => json.Insert(1, "\"extra\":true,"),
            "uuid" => json.Replace(A, A.ToUpperInvariant().Replace("11111111", "AAAAAAAA")),
            "newline" => json.Replace(A, A + "\\n", StringComparison.Ordinal),
            _ => "{"
        };
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json);
        var status = Discover(fixture).Single(p => !Equals(p.Metadata?.Id.Value, "core"));
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(d => d.Code == code));
    }

    [TestMethod]
    public void Duplicate_uuid_rejects_both_packages_and_their_dependent()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "first", A);
        AddMod(fixture, "second", A);
        AddMod(fixture, "dependent", B, dependencies: [(A, "1.0.0")]);
        var statuses = Discover(fixture);
        var duplicates = statuses.Where(p => p.Metadata?.Id.Value == A).ToArray();
        Assert.AreEqual(2, duplicates.Length);
        Assert.IsTrue(duplicates.All(p => p.State == C.PackageState.Invalid && p.Diagnostics.Any(d => d.Code == "duplicate_identity")));
        AssertRejected(statuses, B, C.PackageState.DependencyUnavailable, "dependency_missing");
    }

    [TestMethod]
    public void Dependency_uuid_cannot_have_a_trailing_line_feed()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "source", A);
        AddMod(fixture, "dependent", B, dependencies: [(A + "\n", "1.0.0")]);
        AssertRejected(Discover(fixture), B, C.PackageState.Invalid, "invalid_field");
    }

    [TestMethod]
    [DataRow("1.0.0-alpha.2", "1.0.0-alpha.10", false)]
    [DataRow("1.0.0-alpha.10", "1.0.0-alpha.2", true)]
    [DataRow("1.0.0-1", "1.0.0-alpha", false)]
    [DataRow("1.0.0", "1.0.0-rc.1", true)]
    [DataRow("1.0.0+installed", "1.0.0+required", true)]
    [DataRow("999999999999999999999999999999.0.0", "999999999999999999999999999998.0.0", true)]
    [DataRow("1.0.0-999999999999999999999999999998", "1.0.0-999999999999999999999999999999", false)]
    public void Dependency_versions_follow_semver_precedence(string installed, string minimum, bool available)
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "source", A, installed);
        AddMod(fixture, "dependent", B, dependencies: [(A, minimum)]);
        var status = Discover(fixture).Single(p => p.Metadata?.Id.Value == B);
        Assert.AreEqual(available ? C.PackageState.Available : C.PackageState.DependencyUnavailable, status.State);
        Assert.AreEqual(!available, status.Diagnostics.Any(d => d.Code == "dependency_version"));
    }

    [TestMethod]
    [DataRow(false, "dependency_missing")]
    [DataRow(true, "dependency_version")]
    public void Unavailable_dependency_cascades_to_downstream_packages(bool installed, string code)
    {
        var fixture = new ContentFixture();
        if (installed) AddMod(fixture, "source", A, "1.0.0");
        AddMod(fixture, "middle", B, dependencies: [(A, "2.0.0")]);
        AddMod(fixture, "last", D, dependencies: [(B, "1.0.0")]);
        var statuses = Discover(fixture);
        AssertRejected(statuses, B, C.PackageState.DependencyUnavailable, code);
        AssertRejected(statuses, D, C.PackageState.DependencyUnavailable, "dependency_missing");
    }

    [TestMethod]
    public void Cycle_members_and_downstream_package_have_distinct_diagnostics()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "first", A, dependencies: [(B, "1.0.0")]);
        AddMod(fixture, "second", B, dependencies: [(A, "1.0.0")]);
        AddMod(fixture, "last", D, dependencies: [(A, "1.0.0")]);
        var statuses = Discover(fixture);
        AssertRejected(statuses, A, C.PackageState.DependencyUnavailable, "dependency_cycle");
        AssertRejected(statuses, B, C.PackageState.DependencyUnavailable, "dependency_cycle");
        AssertRejected(statuses, D, C.PackageState.DependencyUnavailable, "dependency_missing");
    }

    [TestMethod]
    public void Dependencies_precede_dependents_regardless_of_directory_enumeration()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "a-dependent", A, dependencies: [(D, "1.0.0")]);
        AddMod(fixture, "z-source", D);
        var normal = Discover(fixture).Select(p => p.Metadata!.Id.Value).ToArray();
        fixture.ReverseEnumeration = true;
        var reversed = Discover(fixture).Select(p => p.Metadata!.Id.Value).ToArray();
        CollectionAssert.AreEqual(new[] { "core", D, A }, normal);
        CollectionAssert.AreEqual(normal, reversed);
    }

    [TestMethod]
    public void Disabled_mod_does_not_parse_its_invalid_voxel_or_read_assets()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "disabled", A);
        fixture.Installation = fixture.Installation with { EnabledMods = [] };
        fixture.AddFile(ContentFixture.Mods, "disabled/resources/voxels/bad.voxel.json", "{");
        using var candidate = fixture.Load();
        var status = candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == A);
        Assert.AreEqual(C.PackageState.Disabled, status.State);
        Assert.AreEqual(0, status.Diagnostics.Length);
        Assert.AreEqual(0, candidate.View.Voxels.Length);
    }

    [TestMethod]
    [DataRow(false, "invalid_field")]
    [DataRow(true, "unsupported_format")]
    public void Invalid_mod_content_excludes_the_package_and_dependents(bool unsupported, string code)
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "source", A);
        AddMod(fixture, "dependent", B, dependencies: [(A, "1.0.0")]);
        if (unsupported) fixture.AddDirectory(ContentFixture.Mods, "source/resources/blueprints");
        else fixture.AddFile(ContentFixture.Mods, "source/resources/voxels/bad.voxel.json", "{\"schema_version\":3}");
        using var candidate = fixture.Load();
        AssertRejected(candidate.Report.Packages, A, C.PackageState.Invalid, code);
        AssertRejected(candidate.Report.Packages, B, C.PackageState.DependencyUnavailable, "dependency_missing");
        Assert.AreEqual(0, candidate.View.Voxels.Length);
    }

    [TestMethod]
    public void Unsupported_builtin_definition_directory_fails_the_entire_load()
    {
        var fixture = new ContentFixture();
        fixture.AddDirectory(ContentFixture.BuiltIn, "resources/blueprints");
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d => d.Code == "unsupported_format"));
    }

    [TestMethod]
    public void Accepted_mod_is_projected_and_asset_failure_excludes_it_and_its_dependent()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        AddMod(fixture, "source", A);
        AddMod(fixture, "dependent", B, dependencies: [(A, "1.0.0")]);
        fixture.AddFile(ContentFixture.Mods, "source/resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
        fixture.AddAsset(ContentFixture.Mods, "source/resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, "source/resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
        using var original = fixture.Load();
        CollectionAssert.AreEquivalent(new[] { "core:block", A + ":block" }, original.View.Voxels.Select(voxel => voxel.Definition.Value).ToArray());
        Assert.AreEqual(2, original.View.Core.Goods.Length);
        fixture.SetReadFailure(ContentFixture.Mods, "source/resources/voxels/block/tile.png", C.ContentIoFailure.Missing);
        using var changed = fixture.Load();
        AssertRejected(changed.Report.Packages, A, C.PackageState.Invalid, "file_missing");
        AssertRejected(changed.Report.Packages, B, C.PackageState.DependencyUnavailable, "dependency_missing");
        Assert.AreEqual("core:block", changed.View.Voxels.Single().Definition.Value);
        Assert.AreEqual(2, original.View.Voxels.Length);
    }

    [TestMethod]
    public void Invalid_utf8_manifest_isolated_from_other_packages()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "valid", B);
        var bytes = System.Text.Encoding.UTF8.GetBytes(Manifest(A, "1.0.0"));
        var name = System.Text.Encoding.UTF8.GetBytes("\"name\":\"");
        var offset = bytes.AsSpan().IndexOf(name) + name.Length;
        bytes[offset] = 0xFF;
        fixture.AddFile(ContentFixture.Mods, "invalid/mod.json", bytes);
        var statuses = Discover(fixture);
        Assert.AreEqual(C.PackageState.Available, statuses.Single(p => p.Metadata?.Id.Value == B).State);
        Assert.IsTrue(statuses.Any(p => p.State == C.PackageState.Invalid && p.Diagnostics.Any(d => d.Code == "invalid_json")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_enabled_mods_are_reported_even_when_mods_root_is_missing(bool rootMissing)
    {
        var fixture = new ContentFixture();
        fixture.Installation = fixture.Installation with { EnabledMods = [new(A), new(B)] };
        if (rootMissing) fixture.SetEnumerationFailure(ContentFixture.Mods, "", C.ContentIoFailure.Missing);
        var statuses = Discover(fixture);
        Assert.AreEqual(C.PackageState.Available, statuses.Single(p => p.Metadata?.Id.Value == "core").State);
        var missing = statuses.Where(p => p.Metadata is null).ToArray();
        Assert.AreEqual(2, missing.Length);
        foreach (var id in new[] { A, B })
        {
            var status = missing.Single(p => p.Diagnostics.Any(d => d.Message.Contains(id, StringComparison.Ordinal)));
            Assert.AreEqual(C.PackageState.DependencyUnavailable, status.State);
            Assert.AreEqual(1, status.Diagnostics.Length);
            Assert.AreEqual("dependency_missing", status.Diagnostics[0].Code);
            Assert.AreEqual(C.ContentDiagnosticSeverity.Error, status.Diagnostics[0].Severity);
        }
        using var candidate = fixture.Load();
        Assert.AreEqual(2, candidate.Report.Packages.Count(p => p.Metadata is null));
    }

    [TestMethod]
    public void Cross_edge_cycle_marks_every_member_and_excludes_downstream()
    {
        const string last = "44444444-4444-4444-8444-444444444444";
        var fixture = new ContentFixture();
        AddMod(fixture, "first", A, dependencies: [(B, "1.0.0"), (D, "1.0.0")]);
        AddMod(fixture, "second", B, dependencies: [(A, "1.0.0")]);
        AddMod(fixture, "third", D, dependencies: [(B, "1.0.0")]);
        AddMod(fixture, "last", last, dependencies: [(D, "1.0.0")]);
        var statuses = Discover(fixture);
        foreach (var id in new[] { A, B, D })
            AssertRejected(statuses, id, C.PackageState.DependencyUnavailable, "dependency_cycle");
        AssertRejected(statuses, last, C.PackageState.DependencyUnavailable, "dependency_missing");
    }

    [TestMethod]
    public void Manifest_dependency_fields_keep_original_array_indices()
    {
        var fixture = new ContentFixture();
        var json = Manifest(A, "1.0.0").Replace("\"dependencies\":[]",
            "\"dependencies\":[false,{\"id\":\"core\",\"name\":false}]", StringComparison.Ordinal);
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json);
        var status = Discover(fixture).Single(p => p.Metadata?.Id.Value == A);
        var diagnostics = status.Diagnostics;
        Assert.IsTrue(diagnostics.Any(d => d.Field == "/dependencies/0"));
        Assert.IsTrue(diagnostics.Any(d => d.Field == "/dependencies/1/min_version"));
        Assert.IsTrue(diagnostics.Any(d => d.Field == "/dependencies/1/name"));
    }

    [TestMethod]
    [DataRow(false, "dependency_missing", "/dependencies/1")]
    [DataRow(true, "dependency_version", "/dependencies/1/min_version")]
    public void Dependency_failures_identify_the_matching_manifest_entry(bool installed, string code, string field)
    {
        var fixture = new ContentFixture();
        if (installed) AddMod(fixture, "source", B);
        AddMod(fixture, "dependent", A, dependencies: [("core", "0.0.0"), (B, "2.0.0")]);
        var diagnostic = Discover(fixture).Single(p => p.Metadata?.Id.Value == A).Diagnostics.Single(d => d.Code == code);
        Assert.AreEqual(field, diagnostic.Field);
    }

    [TestMethod]
    public void Duplicate_dependency_fields_include_the_array_index()
    {
        var fixture = new ContentFixture();
        var json = Manifest(A, "1.0.0", (B, "1.0.0")).Replace("\"min_version\":\"1.0.0\"",
            "\"min_version\":\"1.0.0\",\"min_version\":\"2.0.0\"", StringComparison.Ordinal);
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json);
        var diagnostic = Discover(fixture).SelectMany(package => package.Diagnostics).Single(d => d.Code == "invalid_json");
        Assert.AreEqual("/dependencies/0/min_version", diagnostic.Field);
    }

    private static string Manifest(string id, string version, params (string Id, string Minimum)[] dependencies) =>
        JsonSerializer.Serialize(new
        {
            schema_version = 2, id, name = "测试包", version, authors = new[] { "作者" },
            dependencies = dependencies.Select(d => new { id = d.Id, min_version = d.Minimum }).ToArray()
        });

    private static void AddMod(ContentFixture fixture, string folder, string id, string version = "1.0.0",
        params (string Id, string Minimum)[] dependencies)
    {
        fixture.AddFile(ContentFixture.Mods, folder + "/mod.json", Manifest(id, version, dependencies));
        fixture.AddDirectory(ContentFixture.Mods, folder + "/resources");
        fixture.Installation = fixture.Installation with { EnabledMods = fixture.Installation.EnabledMods.Add(new(id)) };
    }

    private static ImmutableArray<C.PackageStatus> Discover(ContentFixture fixture)
    {
        var result = ContentFactory.Create().Discover(fixture.Installation, fixture);
        Assert.IsInstanceOfType<C.ContentResult<ImmutableArray<C.PackageStatus>>.Success>(result, result.ToString());
        return ((C.ContentResult<ImmutableArray<C.PackageStatus>>.Success)result).Value;
    }

    private static void AssertRejected(ImmutableArray<C.PackageStatus> statuses, string id, C.PackageState state, string code)
    {
        var status = statuses.Single(p => p.Metadata?.Id.Value == id);
        Assert.AreEqual(state, status.State);
        Assert.IsTrue(status.Diagnostics.Any(d => d.Code == code), string.Join(",", status.Diagnostics.Select(d => d.Code)));
    }
}

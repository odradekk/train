using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [DataRow("schema_version", "unsupported_format")]
    [DataRow("schema_version_missing", "unsupported_format")]
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
            "schema_version" => json.Replace("\"schema_version\":2", "\"schema_version\":1", StringComparison.Ordinal),
            "schema_version_missing" => json.Replace("\"schema_version\":2,", "", StringComparison.Ordinal),
            _ => "{"
        };
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json);
        var status = Discover(fixture).Single(p => !Equals(p.Metadata?.Id.Value, "core"));
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(d => d.Code == code));
        if (code == "unsupported_format")
        {
            var diagnostic = status.Diagnostics.Single(d => d.Code == code);
            Assert.AreEqual("/schema_version", diagnostic.Field);
            Assert.AreEqual(new C.ContentSourceLocation(new(A), "pack/mod.json"), diagnostic.Source);
        }
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
    [DataRow("1.2.3-alpha.1+build.7")]
    [DataRow("1.0.0+001")]
    public void Manifest_text_normalizes_at_valid_length_and_author_count_boundaries(string version)
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(Manifest(A, version))!;
        var name = new string('名', 100);
        var authors = Enumerable.Range(1, 16).Select(i => new string('作', 98) + i.ToString("D2")).ToArray();
        json["name"] = " " + name + " ";
        json["authors"] = new JsonArray(authors.Select(author => (JsonNode?)JsonValue.Create(" " + author + " ")).ToArray());
        json["description"] = " " + new string('说', 4000) + " ";
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json.ToJsonString());
        fixture.Installation = fixture.Installation with { EnabledMods = [new(A)] };

        var status = Discover(fixture).Single(p => p.Metadata?.Id.Value == A);
        Assert.AreEqual(C.PackageState.Available, status.State);
        Assert.AreEqual(name, status.Metadata!.Name);
        Assert.AreEqual(version, status.Metadata.Version);
        CollectionAssert.AreEqual(authors, status.Metadata.Authors.ToArray());
    }

    [TestMethod]
    [DataRow("name", "\" \"", "/name")]
    [DataRow("name", "false", "/name")]
    [DataRow("name", null, "/name")]
    [DataRow("authors", "[]", "/authors")]
    [DataRow("authors", "false", "/authors")]
    [DataRow("authors", null, "/authors")]
    [DataRow("authors", "[\"作者\",\" 作者 \"]", "/authors/1")]
    [DataRow("authors", "[false]", "/authors/0")]
    [DataRow("version", "\"01.0.0\"", "/version")]
    [DataRow("version", "\"1.0.0-01\"", "/version")]
    [DataRow("version", "\"1.0.0-\"", "/version")]
    [DataRow("version", "\"1.0.0+\"", "/version")]
    [DataRow("version", "\"1.0.0-alpha.\"", "/version")]
    [DataRow("version", "\"1\"", "/version")]
    [DataRow("version", "\"1.0\"", "/version")]
    [DataRow("version", "\"v1.0.0\"", "/version")]
    [DataRow("version", "\"1.00.0\"", "/version")]
    [DataRow("version", "\"1.0.0+build.\"", "/version")]
    [DataRow("version", "\"1.0.0_beta\"", "/version")]
    [DataRow("version", "\"\"", "/version")]
    [DataRow("version", "false", "/version")]
    [DataRow("version", null, "/version")]
    [DataRow("description", "false", "/description")]
    public void Manifest_invalid_text_and_authors_report_their_field(string field, string? replacement, string pointer)
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(Manifest(A, "1.0.0"))!.AsObject();
        if (replacement is null) json.Remove(field);
        else json[field] = JsonNode.Parse(replacement);
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json.ToJsonString());

        var status = Discover(fixture).Single(p => p.Metadata?.Id.Value == A);
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(d => d.Code == "invalid_field" && d.Field == pointer));
        Assert.AreEqual(new C.ContentSourceLocation(new(A), "pack/mod.json"),
            status.Diagnostics.First(d => d.Code == "invalid_field" && d.Field == pointer).Source);
    }

    [TestMethod]
    [DataRow("name", "/name")]
    [DataRow("author", "/authors/0")]
    [DataRow("author_count", "/authors")]
    [DataRow("description", "/description")]
    public void Manifest_rejects_text_and_author_counts_above_the_documented_limits(string mutation, string pointer)
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(Manifest(A, "1.0.0"))!;
        switch (mutation)
        {
            case "name": json["name"] = new string('名', 101); break;
            case "author": json["authors"] = new JsonArray(new string('作', 101)); break;
            case "author_count": json["authors"] = new JsonArray(Enumerable.Range(1, 17).Select(i => (JsonNode?)JsonValue.Create("作者" + i)).ToArray()); break;
            case "description": json["description"] = new string('说', 4001); break;
        }
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json.ToJsonString());
        var status = Discover(fixture).Single(p => p.Metadata?.Id.Value == A);
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(d => d.Code == "invalid_field" && d.Field == pointer));
    }

    [TestMethod]
    public void Invalid_version_still_reserves_its_uuid_and_rejects_a_valid_duplicate()
    {
        var fixture = new ContentFixture();
        AddMod(fixture, "valid", A);
        AddMod(fixture, "invalid", A, "01.0.0");
        var duplicates = Discover(fixture).Where(p => p.Metadata?.Id.Value == A).ToArray();
        Assert.AreEqual(2, duplicates.Length);
        Assert.IsTrue(duplicates.All(p => p.State == C.PackageState.Invalid && p.Diagnostics.Any(d => d.Code == "duplicate_identity")));
    }

    [TestMethod]
    [DataRow("\"00000000-0000-0000-0000-000000000000\"")]
    [DataRow("42")]
    [DataRow(null)]
    public void Manifest_requires_a_non_nil_uuid_string(string? id)
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(Manifest(A, "1.0.0"))!.AsObject();
        if (id is null) json.Remove("id");
        else json["id"] = JsonNode.Parse(id);
        fixture.AddFile(ContentFixture.Mods, "pack/mod.json", json.ToJsonString());
        var status = Discover(fixture).Single(p => p.Metadata?.Id.Value != "core");
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.IsTrue(status.Diagnostics.Any(d => d.Code == "invalid_field" && d.Field == "/id"));
    }

    [TestMethod]
    public void Renaming_a_mod_folder_preserves_package_definition_and_business_identity()
    {
        using var original = LoadFolder("first");
        using var renamed = LoadFolder("renamed");
        CollectionAssert.AreEqual(original.View.Packages.Select(p => p.Metadata!.Id).ToArray(), renamed.View.Packages.Select(p => p.Metadata!.Id).ToArray());
        Assert.AreEqual(A + ":block", original.View.Voxels.Single().Definition.Value);
        CollectionAssert.AreEqual(original.View.Voxels.Select(v => v.Definition).ToArray(), renamed.View.Voxels.Select(v => v.Definition).ToArray());
        Assert.AreEqual(original.View.Core.Identity, renamed.View.Core.Identity);

        static C.IContentCandidate LoadFolder(string folder)
        {
            var fixture = new ContentFixture();
            AddMod(fixture, folder, A);
            fixture.AddFile(ContentFixture.Mods, folder + "/resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
            fixture.AddAsset(ContentFixture.Mods, folder + "/resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
            fixture.AddAsset(ContentFixture.Mods, folder + "/resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
            return fixture.Load();
        }
    }

    [TestMethod]
    [DataRow("resources/voxels", true)]
    [DataRow("resources/voxels/block.voxel.json", true)]
    [DataRow("resources", false)]
    [DataRow("resources/voxels", false)]
    public void Unsafe_entries_and_unreadable_definition_directories_reject_the_whole_builtin_package(string path, bool linked)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        if (linked)
        {
            var slash = path.LastIndexOf('/');
            fixture.SetEntry(ContentFixture.BuiltIn, slash < 0 ? "" : path[..slash],
                new(path[(slash + 1)..], C.ContentEntryKind.Link));
        }
        else fixture.SetEnumerationFailure(ContentFixture.BuiltIn, path, C.ContentIoFailure.Inaccessible);
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(d =>
            d.Code == (linked ? "linked_path" : "file_unreadable") && d.Source?.Path == path));
    }

    [TestMethod]
    [DataRow("linked_directory", "linked_path")]
    [DataRow("missing_manifest", "file_missing")]
    [DataRow("unreadable_manifest", "file_unreadable")]
    public void Unsafe_mod_packages_are_isolated_and_report_their_io_failure(string failure, string code)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        if (failure == "linked_directory")
            fixture.SetEntry(ContentFixture.Mods, "", new("pack", C.ContentEntryKind.Link));
        else fixture.SetReadFailure(ContentFixture.Mods, "pack/mod.json",
            failure == "missing_manifest" ? C.ContentIoFailure.Missing : C.ContentIoFailure.Inaccessible);

        using var candidate = fixture.Load();
        Assert.AreEqual(C.PackageState.Available, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == "core").State);
        Assert.AreEqual("core:block", candidate.View.Voxels.Single().Definition.Value);
        var status = candidate.Report.Packages.Single(p => p.Metadata is null);
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.AreEqual(ContentFixture.Mods, status.Root);
        var diagnostic = status.Diagnostics.Single();
        Assert.AreEqual(code, diagnostic.Code);
        Assert.AreEqual("", diagnostic.Field);
        Assert.IsNull(diagnostic.Source);
    }

    [TestMethod]
    public void Inaccessible_mods_root_reports_its_failure_and_keeps_builtin_content_available()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        fixture.SetEnumerationFailure(ContentFixture.Mods, "", C.ContentIoFailure.Inaccessible);

        using var candidate = fixture.Load();
        Assert.AreEqual(C.PackageState.Available, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == "core").State);
        Assert.AreEqual("core:block", candidate.View.Voxels.Single().Definition.Value);
        var status = candidate.Report.Packages.Single(p => p.Metadata is null);
        Assert.AreEqual(C.PackageState.Invalid, status.State);
        Assert.AreEqual(ContentFixture.Mods, status.Root);
        var diagnostic = status.Diagnostics.Single();
        Assert.AreEqual("file_unreadable", diagnostic.Code);
        Assert.AreEqual("", diagnostic.Field);
        Assert.IsNull(diagnostic.Source);
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
        if (unsupported) fixture.AddDirectory(ContentFixture.Mods, "source/resources/markets");
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
        fixture.AddDirectory(ContentFixture.BuiltIn, "resources/markets");
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

// Core 内容模块测试。
// 验证 Mod 候选筛选、清单身份、冲突隔离和扫描计数。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Content.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
public sealed class ModDiscoveryTests
{
    [TestMethod]
    public void Direct_child_directories_are_candidates_and_files_are_ignored()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            new ModFolderCandidate("readme.txt", "memory://mods/readme.txt", isDirectory: false, manifestBytes: System.Text.Encoding.UTF8.GetBytes("nope")),
            new ModFolderCandidate(
                "pretty-name",
                "memory://mods/pretty-name",
                isDirectory: true,
                manifestBytes: Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid)))
        });

        Assert.AreEqual(1, scan.CandidateCount);
        Assert.AreEqual(1, scan.AcceptedCount);
        Assert.AreEqual(ModFixtures.SampleUuid, scan.Packages.Single().Namespace);
        Assert.AreEqual("memory://mods/pretty-name/resources", scan.Packages.Single().ResourcesRoot);
        Assert.AreEqual(ContentPackageKind.Mod, scan.Packages.Single().Kind);
    }

    [TestMethod]
    public void Folder_name_is_not_identity_and_uuid_stays_stable_across_renames()
    {
        var first = ModDiscovery.Scan(new[]
        {
            Valid("Pretty Name", ModFixtures.SampleUuid)
        });
        var renamed = ModDiscovery.Scan(new[]
        {
            Valid("renamed-folder", ModFixtures.SampleUuid)
        });

        Assert.AreEqual(first.Packages.Single().Namespace, renamed.Packages.Single().Namespace);
        Assert.AreEqual(ModFixtures.SampleUuid, renamed.Packages.Single().Namespace);
        Assert.AreNotEqual(first.Packages.Single().ResourcesRoot, renamed.Packages.Single().ResourcesRoot);
        Assert.AreNotEqual("Pretty Name", renamed.Packages.Single().Namespace);
    }

    [TestMethod]
    public void Duplicate_uuids_disable_every_conflicting_package()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            Valid("alpha", ModFixtures.SampleUuid),
            Valid("beta", ModFixtures.SampleUuid),
            Valid("unique", ModFixtures.OtherUuid)
        });

        Assert.AreEqual(3, scan.CandidateCount);
        Assert.AreEqual(1, scan.AcceptedCount);
        Assert.AreEqual(ModFixtures.OtherUuid, scan.Packages.Single().Namespace);
        Assert.AreEqual(2, scan.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.DuplicateModUuid &&
            item.Severity == ContentDiagnosticSeverity.Warning));
        Assert.IsFalse(scan.Packages.Any(package => package.Namespace == ModFixtures.SampleUuid));
    }

    [TestMethod]
    public void Missing_or_unsafe_manifests_are_isolated()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            new ModFolderCandidate("empty", "memory://mods/empty", isDirectory: true, manifestMissing: true),
            new ModFolderCandidate("linked", "memory://mods/linked", isDirectory: true, isLink: true, manifestBytes: Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid))),
            new ModFolderCandidate(
                "unsafe-manifest",
                "memory://mods/unsafe-manifest",
                isDirectory: true,
                manifestUnsafe: true,
                manifestBytes: Utf8(ModFixtures.Manifest(ModFixtures.OtherUuid))),
            Valid("ok", ModFixtures.SampleUuid)
        });

        Assert.AreEqual(1, scan.AcceptedCount);
        Assert.AreEqual(ModFixtures.SampleUuid, scan.Packages.Single().Namespace);
        Assert.IsTrue(scan.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.ModManifestMissing));
        Assert.IsTrue(scan.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
    }

    [TestMethod]
    public void Valid_and_same_uuid_invalid_version_disable_every_declaration()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            Valid("good", ModFixtures.SampleUuid),
            new ModFolderCandidate(
                "bad-version",
                "memory://mods/bad-version",
                isDirectory: true,
                manifestBytes: Utf8(ModFixtures.Manifest(ModFixtures.SampleUuid, version: "01.0.0")))
        });

        Assert.AreEqual(0, scan.AcceptedCount);
        Assert.AreEqual(2, scan.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.DuplicateModUuid));
        Assert.IsTrue(scan.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.VersionInvalid));
    }

    [TestMethod]
    public void Two_fully_valid_duplicates_are_both_disabled()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            Valid("alpha", ModFixtures.SampleUuid),
            Valid("beta", ModFixtures.SampleUuid)
        });

        Assert.AreEqual(0, scan.AcceptedCount);
        Assert.AreEqual(2, scan.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.DuplicateModUuid));
    }

    [TestMethod]
    public void Ambiguous_id_does_not_disable_an_unrelated_valid_package()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            Valid("ok", ModFixtures.SampleUuid),
            new ModFolderCandidate(
                "broken",
                "memory://mods/broken",
                isDirectory: true,
                manifestBytes: Utf8("{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }")),
            new ModFolderCandidate(
                "wrong-case",
                "memory://mods/wrong-case",
                isDirectory: true,
                manifestBytes: Utf8("{ \"schema_version\": 1, \"Id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }")),
            new ModFolderCandidate(
                "missing-id",
                "memory://mods/missing-id",
                isDirectory: true,
                manifestBytes: Utf8("{ \"schema_version\": 1, \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }"))
        });

        Assert.AreEqual(1, scan.AcceptedCount);
        Assert.AreEqual(ModFixtures.SampleUuid, scan.Packages.Single().Namespace);
        Assert.AreEqual(0, scan.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.DuplicateModUuid));
    }

    [TestMethod]
    public void Accepted_package_retains_normalized_manifest_metadata()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            new ModFolderCandidate(
                "pretty-name",
                "memory://mods/pretty-name",
                isDirectory: true,
                manifestBytes: Utf8(ModFixtures.Manifest(
                    ModFixtures.SampleUuid,
                    name: "  铜矿扩展  ",
                    version: "1.2.3-alpha.1+build.7",
                    authors: new[] { "  作者甲  " },
                    description: "  说明  ")))
        });

        var package = scan.Packages.Single();
        Assert.IsNotNull(package.Mod);
        Assert.AreEqual(ModFixtures.SampleUuid, package.Mod!.Id);
        Assert.AreEqual("铜矿扩展", package.Mod.Name);
        Assert.AreEqual("1.2.3-alpha.1+build.7", package.Mod.Version);
        CollectionAssert.AreEqual(new[] { "作者甲" }, package.Mod.Authors.ToArray());
        Assert.AreEqual("说明", package.Mod.Description);
        Assert.IsTrue(((System.Collections.IList)package.Mod.Authors).IsReadOnly);
    }

    [TestMethod]
    public void Invalid_manifest_does_not_produce_a_package()
    {
        var scan = ModDiscovery.Scan(new[]
        {
            new ModFolderCandidate(
                "bad",
                "memory://mods/bad",
                isDirectory: true,
                manifestBytes: Utf8(ModFixtures.Manifest("not-a-uuid")))
        });

        Assert.AreEqual(0, scan.AcceptedCount);
        Assert.AreEqual(0, scan.Packages.Count);
        Assert.IsTrue(scan.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UuidInvalid));
    }

    private static ModFolderCandidate Valid(string folder, string uuid) =>
        new(folder, "memory://mods/" + folder, isDirectory: true, manifestBytes: Utf8(ModFixtures.Manifest(uuid)));

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

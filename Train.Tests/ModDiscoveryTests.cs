using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Tests;

[TestClass]
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
        Assert.AreEqual(2, scan.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.DuplicateModUuid));
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

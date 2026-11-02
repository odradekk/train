using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Authoring;
using Train.Content.Contracts;
using Train.Content.ContractSupport;

namespace Train.Godot.Tests.Authoring;

[TestClass]
[TestCategory("integration")]
public sealed class LocalAuthoringPackagesTests
{
    [TestMethod]
    public void Mod_list_keeps_directory_identity_and_reads_only_each_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-authoring-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "named-folder", "resources"));
        File.WriteAllText(Path.Combine(root, "named-folder", "mod.json"), "manifest-only");
        File.WriteAllText(Path.Combine(root, "named-folder", "resources", "unreadable.bin"), "asset");
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        File.WriteAllText(Path.Combine(root, "broken", "mod.json"), "broken");
        var id = new PackageId("b879db2b-f5c2-474f-a362-0fa169ad4037");
        var factory = new ScriptedContentFactory
        {
            DiscoverCall = (installation, files) =>
            {
                var entries = ((ContentIoResult<ImmutableArray<ContentFileEntry>>.Success)files.Enumerate(new(installation.Mods, ""))).Value;
                var entry = entries.Single();
                var bytes = ((ContentIoResult<ImmutableArray<byte>>.Success)files.Read(new(installation.Mods, entry.Name + "/mod.json"), 100)).Value;
                Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Failure>(
                    files.Read(new(installation.Mods, entry.Name + "/resources/unreadable.bin"), 100));
                Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure>(
                    files.Enumerate(new(installation.Mods, entry.Name + "/resources")));
                return new ContentResult<ImmutableArray<PackageStatus>>.Success([
                    entry.Name == "broken"
                        ? new(null, installation.Mods, PackageState.Invalid, [new("invalid_json", ContentDiagnosticSeverity.Error, "discover", null, null, "", null, "损坏的 manifest")])
                        : new(new(id, "本地包", "0.1.0", ["作者"], []), installation.Mods, PackageState.Disabled, [])]);
            }
        };
        try
        {
            using var heldAsset = new FileStream(Path.Combine(root, "named-folder", "resources", "unreadable.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var sources = LocalAuthoringPackages.Discover(factory, root, []);
            Assert.AreEqual(2, sources.Length);
            var valid = sources.Single(source => source.Package == id);
            Assert.AreEqual(Path.Combine(root, "named-folder"), valid.Directory);
            Assert.AreEqual("本地包", valid.Name);
            var invalid = sources.Single(source => source.Package is null);
            Assert.AreEqual(Path.Combine(root, "broken"), invalid.Directory);
            Assert.AreEqual(PackageState.Invalid, invalid.State);
            Assert.AreEqual("invalid_json", invalid.Diagnostics.Single().Code);
        }
        finally { Directory.Delete(root, true); }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Content.Tests;

[TestClass]
public sealed class ContentPackageWalkTests
{
    [TestMethod]
    public void Linked_directory_invalidates_the_package_and_does_not_contribute_files()
    {
        var tree = new Dictionary<string, ContentTreeEntry[]>(StringComparer.Ordinal)
        {
            ["res://root"] =
            [
                new("safe.voxels.json", IsDirectory: false, IsLink: false),
                new("linked-dir", IsDirectory: true, IsLink: true)
            ],
            ["res://root/linked-dir"] =
            [
                new("hidden.voxels.json", IsDirectory: false, IsLink: false)
            ]
        };

        var discovery = ContentPackageWalk.Discover("res://root", path => tree.GetValueOrDefault(path), ".json");

        CollectionAssert.AreEqual(new[] { "safe.voxels.json" }, discovery.Files.ToArray());
        Assert.IsTrue(discovery.Invalid);
        Assert.IsTrue(discovery.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
        Assert.IsFalse(discovery.Files.Any(file => file.Contains("hidden", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Linked_goods_file_invalidates_the_package_instead_of_being_skipped()
    {
        var tree = new Dictionary<string, ContentTreeEntry[]>(StringComparer.Ordinal)
        {
            ["res://root"] =
            [
                new("ok.voxels.json", IsDirectory: false, IsLink: false),
                new("alias.voxels.json", IsDirectory: false, IsLink: true)
            ]
        };

        var discovery = ContentPackageWalk.Discover("res://root", path => tree.GetValueOrDefault(path), ".json");

        CollectionAssert.AreEqual(new[] { "ok.voxels.json" }, discovery.Files.ToArray());
        Assert.IsTrue(discovery.Invalid);
        Assert.IsTrue(discovery.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
    }

    [TestMethod]
    public void Unlistable_directory_invalidates_the_package_instead_of_skipping_the_subtree()
    {
        var tree = new Dictionary<string, ContentTreeEntry[]>(StringComparer.Ordinal)
        {
            ["res://root"] =
            [
                new("visible.voxels.json", IsDirectory: false, IsLink: false),
                new("blocked", IsDirectory: true, IsLink: false)
            ]
        };

        var discovery = ContentPackageWalk.Discover(
            "res://root",
            path => path == "res://root" ? tree[path] : null,
            ".json");

        CollectionAssert.AreEqual(new[] { "visible.voxels.json" }, discovery.Files.ToArray());
        Assert.IsTrue(discovery.Invalid);
        Assert.IsTrue(discovery.Diagnostics.Any(item =>
            item.Code == ContentDiagnosticCodes.UnsafeLinkedPath &&
            item.Severity == ContentDiagnosticSeverity.Error &&
            item.JsonPath == "res://root/blocked"));
    }

    [TestMethod]
    public void Json_discovery_collects_all_lowercase_formats_recursively_in_ordinal_order()
    {
        var tree = new Dictionary<string, ContentTreeEntry[]>(StringComparer.Ordinal)
        {
            ["res://root"] =
            [
                new("z.market-tables.json", IsDirectory: false, IsLink: false),
                new("notes.json", IsDirectory: false, IsLink: false),
                new("upper.JSON", IsDirectory: false, IsLink: false),
                new("script.cs", IsDirectory: false, IsLink: false),
                new("nested", IsDirectory: true, IsLink: false),
                new("a.voxels.json", IsDirectory: false, IsLink: false)
            ],
            ["res://root/nested"] =
            [
                new("town.settlement-types.json", IsDirectory: false, IsLink: false),
                new("blueprint.composites.json", IsDirectory: false, IsLink: false)
            ]
        };

        var discovery = ContentPackageWalk.Discover(
            "res://root",
            path => tree.GetValueOrDefault(path),
            ".json");

        CollectionAssert.AreEqual(
            new[]
            {
                "a.voxels.json",
                "nested/blueprint.composites.json",
                "nested/town.settlement-types.json",
                "notes.json",
                "z.market-tables.json"
            },
            discovery.Files.ToArray());
        Assert.IsFalse(discovery.Invalid);
        Assert.AreEqual(0, discovery.Diagnostics.Count);
    }
}

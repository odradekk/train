// Core 内容模块测试。
// 验证内容包遍历遇到链接或不可列举目录时使整个包失效。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
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

        var discovery = ContentPackageWalk.Discover("res://root", path => tree.GetValueOrDefault(path));

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

        var discovery = ContentPackageWalk.Discover("res://root", path => tree.GetValueOrDefault(path));

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
            path => path == "res://root" ? tree[path] : null);

        CollectionAssert.AreEqual(new[] { "visible.voxels.json" }, discovery.Files.ToArray());
        Assert.IsTrue(discovery.Invalid);
        Assert.IsTrue(discovery.Diagnostics.Any(item =>
            item.Code == ContentDiagnosticCodes.UnsafeLinkedPath &&
            item.JsonPath == "res://root/blocked"));
    }
}

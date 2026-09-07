using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Tests;

[TestClass]
public sealed class IsolatedBuiltInContentTests
{
    [TestMethod]
    public void ExternalContent_reads_the_physical_overlay_without_touching_a_separate_worktree_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-isolated-builtin-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "worktree", "voxels");
        var isolated = Path.Combine(root, "isolated", "voxels");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(isolated);
        var original = ContentFixtures.GoodsFile(ContentFixtures.Good("iron-ore", "铁矿", "说明"));
        var mutated = ContentFixtures.GoodsFile(ContentFixtures.Good("iron-ore", "隔离铁矿", "说明", maxDurability: 150));
        File.WriteAllText(Path.Combine(worktree, "iron-ore.voxels.json"), original);
        File.WriteAllText(Path.Combine(isolated, "iron-ore.voxels.json"), original);

        try
        {
            File.WriteAllText(Path.Combine(isolated, "iron-ore.voxels.json"), mutated);
            var listed = ExternalContent.ListVoxelFiles(Path.Combine(root, "isolated"));
            Assert.IsFalse(listed.Invalid);
            Assert.IsTrue(listed.Files.Contains("voxels/iron-ore.voxels.json"));
            var read = ExternalContent.ReadFile(Path.Combine(root, "isolated"), "voxels/iron-ore.voxels.json", ContentLimits.JsonFileMaxBytes);
            Assert.AreEqual(mutated, System.Text.Encoding.UTF8.GetString(read.Bytes!));
            Assert.AreEqual(original, File.ReadAllText(Path.Combine(worktree, "iron-ore.voxels.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

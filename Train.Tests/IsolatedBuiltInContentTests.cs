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
        var original = ContentFixtures.GoodsFile(ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明"));
        var mutated = ContentFixtures.GoodsFile(ContentFixtures.Good("ferrous-ore", "隔离亚铁矿", "说明", maxDurability: 150));
        File.WriteAllText(Path.Combine(worktree, "ferrous-ore.voxels.json"), original);
        File.WriteAllText(Path.Combine(isolated, "ferrous-ore.voxels.json"), original);

        try
        {
            File.WriteAllText(Path.Combine(isolated, "ferrous-ore.voxels.json"), mutated);
            var listed = ExternalContent.ListVoxelFiles(Path.Combine(root, "isolated"));
            Assert.IsFalse(listed.Invalid);
            Assert.IsTrue(listed.Files.Contains("voxels/ferrous-ore.voxels.json"));
            var read = ExternalContent.ReadFile(Path.Combine(root, "isolated"), "voxels/ferrous-ore.voxels.json", ContentLimits.JsonFileMaxBytes);
            Assert.AreEqual(mutated, System.Text.Encoding.UTF8.GetString(read.Bytes!));
            Assert.AreEqual(original, File.ReadAllText(Path.Combine(worktree, "ferrous-ore.voxels.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

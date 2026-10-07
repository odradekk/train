// Core 内容模块测试：验证内容发现、解析与重载的无引擎契约。
// 测试输入保持最小，并与旧世界运行态隔离。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
public sealed class IsolatedBuiltInContentTests
{
    [TestMethod]
    public void ExternalContent_reads_voxels_without_loading_retired_settlement_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-isolated-builtin-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "worktree", "voxels");
        var isolated = Path.Combine(root, "isolated", "voxels");
        var retired = Path.Combine(root, "isolated", "settlements");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(isolated);
        Directory.CreateDirectory(retired);
        var original = ContentFixtures.GoodsFile(ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明"));
        var mutated = ContentFixtures.GoodsFile(ContentFixtures.Good("ferrous-ore", "隔离亚铁矿", "说明", maxDurability: 150));
        File.WriteAllText(Path.Combine(worktree, "ferrous-ore.voxels.json"), original);
        File.WriteAllText(Path.Combine(isolated, "ferrous-ore.voxels.json"), original);
        File.WriteAllText(Path.Combine(retired, "legacy.settlements.json"), "{}");

        try
        {
            File.WriteAllText(Path.Combine(isolated, "ferrous-ore.voxels.json"), mutated);
            var listed = ExternalContent.ListVoxelFiles(Path.Combine(root, "isolated"));
            Assert.IsFalse(listed.Invalid);
            Assert.IsTrue(listed.Files.Contains("voxels/ferrous-ore.voxels.json"));
            Assert.IsFalse(listed.Files.Any(path => path.EndsWith(".settlements.json", StringComparison.Ordinal)));
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

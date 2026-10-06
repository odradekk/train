// Core 内容测试验证蓝图尺寸档的 schema 边界。
// 未注册的尺寸档必须使所属内置包发布失败。

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
public sealed class BlueprintSchemaTests
{
    [TestMethod]
    public void Unknown_size_class_is_rejected_by_the_parser()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "voxels/vehicle-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("vehicle-shell", "壳体", "说明", value: 10, tagsJson: "[]")));
        source.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "说明", value: 10, tagsJson: "[\"identity_core\"]")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddCompositeFile(
            "core",
            "composites/bad.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype(), CompositeFixtures.Composite(size: "huge")));
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Rejected, reloader.Reload().Status);
    }
}

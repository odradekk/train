// Core 内容测试验证蓝图尺寸档和布局的 schema 边界。
// 无效内置蓝图必须拒绝整次发布。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Tests.Core.Logging;

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
        var source = CompositeFixtures.VehiclePackage();
        source.AddCompositeFile("core", "composites/dev-train.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Composite(size: "huge")));
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(source, session.Logger).Reload().Status);
    }

    [DataTestMethod]
    [DataRow("empty")]
    [DataRow("duplicate")]
    [DataRow("orientation")]
    public void Invalid_layout_rejects_the_built_in_catalog(string invalidCase)
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        var voxels = invalidCase switch
        {
            "empty" => "",
            "duplicate" => string.Join(",", CompositeFixtures.Voxel(0, 0, 0), CompositeFixtures.Voxel(0, 0, 0, CompositeFixtures.ControlId)),
            "orientation" => CompositeFixtures.Voxel(0, 0, 0, CompositeFixtures.ControlId, "px_pz"),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };
        if (invalidCase == "orientation")
            CompositeFixtures.AddVoxel(source, CompositeFixtures.ControlId, "控制", 20, 2,
                behaviorsJson: "[{\"type\":\"control\"}]", orientations: "four_way");
        source.AddCompositeFile("core", "composites/dev-train.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Composite(voxels: voxels)));

        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(source, session.Logger).Reload().Status);
    }
}

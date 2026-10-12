using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Content.Tests;

namespace Train.Content.Tests;

[TestClass]
public sealed class BlueprintSchemaTests
{
    [TestMethod]
    public void Unknown_size_class_is_rejected_by_the_parser()
    {
        var session = new RecordingContentLogger();
        var source = CompositeFixtures.VehiclePackage();
        source.AddDefinitionFile("core", "composites/dev-train.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Composite(size: "huge")));
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(source, session).Reload().Status);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("duplicate")]
    [DataRow("orientation")]
    public void Invalid_layout_rejects_the_built_in_catalog(string invalidCase)
    {
        var session = new RecordingContentLogger();
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
        source.AddDefinitionFile("core", "composites/dev-train.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Composite(voxels: voxels)));

        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(source, session).Reload().Status);
    }
}

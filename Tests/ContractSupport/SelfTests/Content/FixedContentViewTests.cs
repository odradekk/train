using Train.Core.Contracts;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractSupport.Tests.Content;

[TestClass]
public sealed class FixedContentViewTests
{
    [TestMethod]
    public void ExplicitDefaultValueIsConfigured()
    {
        var core = new CoreContentProjection(new("input"), [], [], [], [], [], [], [], [], [], [], [], [], [], []);
        var view = new FixedContentView { Generation = new ContentGeneration(0), Core = core };
        Assert.AreEqual(new ContentGeneration(0), view.Generation);
        Assert.AreSame(core, view.Core);
    }

    [TestMethod]
    public void UnconfiguredPropertyAndQueryFailExplicitly()
    {
        var view = new FixedContentView();
        AssertUnconfigured(() => _ = view.Generation);
        AssertUnconfigured(() => _ = view.Core);
        AssertUnconfigured(() => view.FindDesign(new DesignHash("design")));
        AssertUnconfigured(() => view.FindVoxelAppearance(new VoxelDefinitionId("voxel")));
        AssertUnconfigured(() => _ = view.PersistentRevision);
        AssertUnconfigured(() => _ = view.Packages);
        AssertUnconfigured(() => view.FindAsset(new AssetRevision("asset")));
    }

    [TestMethod]
    public void ExplicitMissingIsRetainedAndConfigurationIsCopied()
    {
        var id = new DesignHash("design");
        var missing = new ContentLookup<DesignEvaluation>.Missing([]);
        var entries = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [id] = missing };
        var view = new FixedContentView { Designs = entries };
        entries.Clear();
        Assert.AreSame(missing, view.FindDesign(id));
        Assert.AreSame(missing, view.FindDesign(id));
        AssertUnconfigured(() => view.FindDesign(new DesignHash("other")));
    }

    [TestMethod]
    public void FoundVoxelAppearanceReturnsFixedObject()
    {
        var id = new VoxelDefinitionId("voxel");
        var appearance = new VoxelAppearanceProjection(id, AllowedVoxelOrientations.All24,
            new VoxelCollision.None(), new VoxelPresentation.Mesh(new("mesh"), []), new(false, null));
        var found = new ContentLookup<VoxelAppearanceProjection>.Found(appearance);
        var view = new FixedContentView
        {
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [id] = found }
        };
        Assert.AreSame(found, view.FindVoxelAppearance(id));
        Assert.AreSame(appearance, ((ContentLookup<VoxelAppearanceProjection>.Found)view.FindVoxelAppearance(id)).Value);
        AssertUnconfigured(() => view.FindVoxelAppearance(new("other")));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}

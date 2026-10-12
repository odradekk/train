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
        var view = new FixedContentView { Generation = new ContentGeneration(0) };
        Assert.AreEqual(new ContentGeneration(0), view.Generation);
        Assert.AreEqual(view.Generation, view.Generation);
    }

    [TestMethod]
    public void UnconfiguredPropertyAndQueryFailExplicitly()
    {
        var view = new FixedContentView();
        AssertUnconfigured(() => _ = view.Generation);
        AssertUnconfigured(() => view.FindAsset(new AssetRevision("asset")));
    }

    [TestMethod]
    public void ExplicitMissingIsRetainedAndConfigurationIsCopied()
    {
        var id = new AssetRevision("asset");
        var missing = new ContentLookup<ContentAsset>.Missing([]);
        var entries = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [id] = missing };
        var view = new FixedContentView { Assets = entries };
        entries.Clear();
        Assert.AreSame(missing, view.FindAsset(id));
        Assert.AreSame(missing, view.FindAsset(id));
        AssertUnconfigured(() => view.FindAsset(new AssetRevision("other")));
    }

    [TestMethod]
    public void FoundAssetReturnsFixedObject()
    {
        var id = new AssetRevision("png");
        var asset = new ContentAsset(id, [1, 2], new AssetMetadata.Png(1, 1), []);
        var found = new ContentLookup<ContentAsset>.Found(asset);
        var view = new FixedContentView
        {
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [id] = found },
            Packages = []
        };
        Assert.AreSame(found, view.FindAsset(id));
        Assert.AreSame(asset, ((ContentLookup<ContentAsset>.Found)view.FindAsset(id)).Value);
        Assert.IsTrue(view.Packages.IsEmpty);
        AssertUnconfigured(() => _ = new FixedContentView().Packages);
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}

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
        AssertUnconfigured(() => view.FindTerrain(new("terrain")));
        AssertUnconfigured(() => view.FindRailwayAppearance(new("railway")));
        AssertUnconfigured(() => view.FindDefinitionDisplay(new CoreDefinitionReference.SettlementType(new("town"))));
    }

    [TestMethod]
    public void TerrainAndRailwayAppearancesRetainFixedInputAndCopyConfiguration()
    {
        var terrainId = new TerrainTypeId("terrain");
        var railwayId = new RuleDefinitionId("railway");
        var terrain = new ContentLookup<TerrainAppearanceProjection>.Found(new(terrainId,
            new("surface"), new("subsurface"), new("rock"), [], [], 0, [], new("Terrain", "")));
        var railway = new ContentLookup<RailwayAppearanceProjection>.Found(new(railwayId, 100, 0.05));
        var missing = new ContentLookup<TerrainAppearanceProjection>.Missing([]);
        var terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        {
            [terrainId] = terrain,
            [new("missing")] = missing
        };
        var railways = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
        {
            [railwayId] = railway
        };
        var view = new FixedContentView { Terrains = terrains, RailwayAppearances = railways };
        terrains.Clear();
        railways.Clear();
        Assert.AreSame(terrain, view.FindTerrain(terrainId));
        Assert.AreSame(missing, view.FindTerrain(new("missing")));
        Assert.AreSame(railway, view.FindRailwayAppearance(railwayId));
        AssertUnconfigured(() => view.FindTerrain(new("other")));
        AssertUnconfigured(() => view.FindRailwayAppearance(new("other")));
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

    [TestMethod]
    public void GoodsRetainExplicitResultsAndCopyConfiguration()
    {
        var id = new GoodId.Voxel(new("good"));
        var found = new ContentLookup<GoodPresentation>.Found(new(id, new(new DisplayText("Good", ""), null, null, false), null, false));
        var missing = new ContentLookup<GoodPresentation>.Missing([]);
        var goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [id] = found, [new GoodId.Voxel(new("missing"))] = missing };
        var view = new FixedContentView { Goods = goods };
        goods.Clear();
        Assert.AreSame(found, view.FindGood(id));
        Assert.AreSame(missing, view.FindGood(new GoodId.Voxel(new("missing"))));
        AssertUnconfigured(() => view.FindGood(new GoodId.Voxel(new("other"))));
        AssertUnconfigured(() => new FixedContentView().FindGood(id));
    }

    [TestMethod]
    public void DefinitionDisplaysRetainExplicitResultsAndCopyConfiguration()
    {
        var id = new CoreDefinitionReference.Process(new ProcessRef.Production(new("process")));
        var missingId = new CoreDefinitionReference.Process(new ProcessRef.Production(new("missing")));
        var found = new ContentLookup<ContentDisplay>.Found(new(new("Process", ""), null, null, false));
        var missing = new ContentLookup<ContentDisplay>.Missing([]);
        var displays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>> { [id] = found, [missingId] = missing };
        var view = new FixedContentView { DefinitionDisplays = displays };
        displays.Clear();
        Assert.AreSame(found, view.FindDefinitionDisplay(id));
        Assert.AreSame(missing, view.FindDefinitionDisplay(missingId));
        AssertUnconfigured(() => view.FindDefinitionDisplay(new CoreDefinitionReference.Process(new ProcessRef.Production(new("other")))));
        AssertUnconfigured(() => new FixedContentView().FindDefinitionDisplay(id));
    }

    [TestMethod]
    public void SettlementLayoutsCopyExplicitResultsAndRejectUnknownInputs()
    {
        var absent = new ContentLookup<SettlementLayoutProjection>.NotFound();
        var layouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> { [new(1)] = absent };
        var view = new FixedContentView { SettlementLayouts = layouts };
        layouts.Clear();
        Assert.AreSame(absent, view.FindSettlementLayout(new(1)));
        AssertUnconfigured(() => view.FindSettlementLayout(new(2)));
        AssertUnconfigured(() => new FixedContentView().FindSettlementLayout(new(1)));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}

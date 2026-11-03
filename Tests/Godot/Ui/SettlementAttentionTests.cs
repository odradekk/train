using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class SettlementAttentionTests
{
    [TestMethod]
    public void OnlyEnabledFacilitiesOutsideRunningAndDisabledNeedAttention()
    {
        foreach (var state in Enum.GetValues<ProductionState>())
        foreach (var enabled in new[] { false, true })
        {
            var settlement = Settlement() with { Facilities = [Facility(1, state, enabled)] };
            var result = SettlementAttention.Read(View(), Content(), settlement);
            Assert.AreEqual(enabled && state is not (ProductionState.Running or ProductionState.Disabled) ? 1 : 0,
                result.Buildings.Count, $"{state}, enabled={enabled}");
        }
    }

    [TestMethod]
    public void BuildingIssuesGroupFacilitiesAndKeepEachReasonOnce()
    {
        var settlement = Settlement() with { Facilities = [Facility(7, ProductionState.WaitingForInputs),
            Facility(7, ProductionState.WaitingForInputs) with { Key = new(new(7), CapabilityKind.Extraction) },
            Facility(7, ProductionState.OutputBlocked) with { Key = new(new(7), CapabilityKind.Construction) },
            Facility(8, ProductionState.Locked)] };
        var result = SettlementAttention.Read(View(), Content(), settlement);
        Assert.AreEqual(2, result.Buildings.Count);
        Assert.AreEqual(new BuildingId(7), result.Buildings[0].Building);
        Assert.AreEqual("建筑内容缺失停工：等待原料；产出无法入库", result.Buildings[0].Text);
        Assert.AreEqual(new BuildingId(8), result.Buildings[1].Building);
    }

    [TestMethod]
    public void ShortagesUseInventoryAndStrictRestockTargetIncludingMissingInventory()
    {
        var good = new GoodId.Voxel(new("fixture:food"));
        var settlement = Settlement() with { Market = Settlement().Market with { Goods = [new(good, 900, 5, 0, 0, 0, 0, [])] } };
        foreach (var quantity in new long?[] { 4, 5, 6, null })
        {
            var view = new FixedCoreReadView { Inventories = quantity is { } amount
                ? new Dictionary<InventoryId, Lookup<InventoryView>> { [new(1)] = new Lookup<InventoryView>.Found(
                    new(new(1), new InventoryOwner.Settlement(new(1)), new WorldLocation.Node(new(1)),
                        InventoryAccess.ReadWrite, 100, amount, 0, 100 - amount, 0, [new(good, amount, false)], [])) } : new Dictionary<InventoryId, Lookup<InventoryView>> { [new(1)] = new Lookup<InventoryView>.Missing() } };
            var result = SettlementAttention.Read(view, Content(), settlement);
            Assert.AreEqual(quantity is null or < 5 ? 1 : 0, result.Goods.Count);
            if (result.Goods.Count == 0) continue;
            Assert.AreEqual(good, result.Goods[0].Good);
            StringAssert.Contains(result.Goods[0].Text, $"{quantity ?? 0} / 5");
        }
        var emptyInventory = new FixedCoreReadView { Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
        { [new(1)] = new Lookup<InventoryView>.Found(new(new(1), new InventoryOwner.Settlement(new(1)),
            new WorldLocation.Node(new(1)), InventoryAccess.ReadWrite, 100, 0, 0, 100, 0, [], [])) } };
        Assert.AreEqual(1, SettlementAttention.Read(emptyInventory, Content(), settlement).Goods.Count);
    }

    [TestMethod]
    public void SitesIncludeWorkingAndBlockedBuildingsButExcludeVehicleJobsAndMissingJobs()
    {
        var settlement = Settlement() with { Plots = [.. Enumerable.Range(1, 4).Select(id =>
            new PlotView(new((ulong)id), id, PlotSize.Small, [], new PlotOccupant.Construction(new((ulong)id))))] };
        ConstructionJobView Job(ulong id, ConstructionState state, ConstructionTarget target) => new(new(id),
            ConstructionKind.Construction, settlement.Id, new(new(9), CapabilityKind.Construction), target,
            state, 0, 100, 50, .5, 5, [], [], []);
        var view = new FixedCoreReadView { Inventories = new Dictionary<InventoryId, Lookup<InventoryView>> { [new(1)] = new Lookup<InventoryView>.Missing() }, Jobs = new Dictionary<JobId, Lookup<JobView>>
        {
            [new(1)] = new Lookup<JobView>.Found(new JobView.Construction(Job(1, ConstructionState.Working, new ConstructionTarget.NewBuilding(new(1), new("fixture:building"))))),
            [new(2)] = new Lookup<JobView>.Found(new JobView.Construction(Job(2, ConstructionState.ResourceBlocked, new ConstructionTarget.NewBuilding(new(2), new("fixture:building"))))),
            [new(4)] = new Lookup<JobView>.Missing(),
            [new(3)] = new Lookup<JobView>.Found(new JobView.Construction(Job(3, ConstructionState.Working, new ConstructionTarget.NewVehicle(new("fixture:vehicle")))))
        } };
        var result = SettlementAttention.Read(view, Content(), settlement);
        Assert.AreEqual(2, result.Sites.Count);
        StringAssert.Contains(result.Sites[0], "50%");
        Assert.AreNotEqual(result.Sites[0], result.Sites[1]);
    }

    private static FixedCoreReadView View() => new()
    {
        Inventories = new Dictionary<InventoryId, Lookup<InventoryView>> { [new(1)] = new Lookup<InventoryView>.Missing() },
        Instances = new ulong[] { 1, 7, 8 }.ToDictionary(id => (InstanceId)new InstanceId.Building(new(id)),
            _ => (Lookup<InstanceView>)new Lookup<InstanceView>.Missing())
    };

    private static FixedContentView Content() => new()
    {
        Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
        { [new GoodId.Voxel(new("fixture:food"))] = new ContentLookup<GoodPresentation>.Missing([]) },
        Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        { [new("fixture:building")] = new ContentLookup<DesignEvaluation>.Missing([]) }
    };

    private static FacilityView Facility(ulong building, ProductionState state, bool enabled = true) =>
        new(new(new(building), CapabilityKind.Production), null, enabled, 0, state, 1, 0, [], null, [], [], []);

    private static SettlementView Settlement()
    {
        var rule = new RuleStatusView(new("fixture:rule"), null, []);
        return new(new(1), new(1), new("fixture:town"), null, SettlementRelation.PlayerOwned,
            new(1), [], [], 0, 1, 0, [], [], [], new(0, 0, 0, 0),
            new(rule, 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
            new(rule, new(0, 0, 0, 0, 0, 0, 0), []), []);
    }
}

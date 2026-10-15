using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Settlements;
using Train.Core.World;
using static Train.Core.Tests.Settlements.SettlementFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class BuildingCapabilitiesTests
{
    [TestMethod]
    public void Intact_parts_add_capabilities_but_compatibility_tags_do_not()
    {
        var state = State();
        var world = state.World;
        var design = world.Designs.Single(d => d.Kind is DesignKind.Building);
        design = design with
        {
            Kind = ((DesignKind.Building)design.Kind) with { FacilityTags = [FacilityTag.Agriculture, FacilityTag.Extraction] },
            Parts = design.Parts.Add(design.Parts[0] with { Id = new(1) })
        };
        world = world with
        {
            Designs = world.Designs.Select(d => d.Design == design.Design ? design : d).ToImmutableArray(),
            Instances = world.Instances.Select(i => i.Id is InstanceId.Building ? i with
                { Parts = i.Parts.Add(new(new(1), 1)) } : i).ToImmutableArray()
        };
        Assert.AreEqual(new BuildingCapabilities(600, 800, 1000, 60), BuildingCapabilities.Read(world, state.Content, new(1)));
        world = world with { Designs = world.Designs.Select(d => d.Design == design.Design ? d with
            { Parts = d.Parts.Select(p => p with { Capabilities = [] }).ToImmutableArray() } : d).ToImmutableArray() };
        Assert.AreEqual(new BuildingCapabilities(0, 0, 0, 0), BuildingCapabilities.Read(world, state.Content, new(1)));
    }

    [TestMethod]
    [DataRow("structure")]
    [DataRow("part")]
    [DataRow("saved_missing")]
    [DataRow("definition_missing")]
    [DataRow("current_missing")]
    public void Storage_and_building_capabilities_use_the_same_effective_parts(string reason)
    {
        var state = State();
        var world = state.World;
        var content = state.Content;
        var building = world.Instances.Single(i => i.Id == new InstanceId.Building(new(1)));
        if (reason == "structure") building = building with { StructureDurability = 0 };
        if (reason == "part") building = building with { Parts = [new(new(0), 0)] };
        world = world with { Instances = world.Instances.Select(i => i.Id == building.Id ? building : i).ToImmutableArray() };
        if (reason == "saved_missing") world = world with { Designs = world.Designs.Select(d => d.Design == building.Design ? d with
            { Parts = d.Parts.Select(p => p with { Missing = true }).ToImmutableArray() } : d).ToImmutableArray() };
        if (reason == "definition_missing") content = content with { Goods = content.Goods.Where(g => g.Good != Material).ToImmutableArray() };
        if (reason == "current_missing") content = content with { Designs = content.Designs.Select(d => d.Design == building.Design ? d with
            { Parts = d.Parts.Select(p => p with { Missing = true }).ToImmutableArray() } : d).ToImmutableArray() };
        Assert.AreEqual(new BuildingCapabilities(0, 0, 0, 0), BuildingCapabilities.Read(world, content, new(1)));
        var expectedStorage = reason is "structure" or "part" ? 100L : 0L;
        Assert.AreEqual(expectedStorage, InventoryStorage.Settlement(world, content, world.Settlements.Single(), 0).View.PhysicalCapacity);
    }
}

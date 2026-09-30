using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Domain.Settlements;

namespace Train.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:settlements")]
public sealed class SettlementRegistryTests
{
    [TestMethod]
    public void Apply_adds_constructs_and_exposes_query_api()
    {
        var registry = new SettlementRegistry();
        var oak = Candidate("core:oak", "橡木城", 2, 4, 20, new VoxelDefinitionId("core", "ferrous-ore"));

        registry.Apply(new[] { oak });

        Assert.AreEqual(1, registry.Count);
        Assert.IsTrue(registry.TryGet(oak.Id, out var live));
        Assert.AreEqual("橡木城", live.Name);
        Assert.AreEqual(new SettlementTypeId("city"), live.Type);
        Assert.AreEqual(new MapPosition(2, 4), live.Position);
        Assert.AreEqual(20, live.Population);
        Assert.IsTrue(live.AvailableRawGoodIds.Contains(new VoxelDefinitionId("core", "ferrous-ore")));
        Assert.AreEqual(1, registry.Items.Count);
        Assert.AreSame(live, registry.Items.Single());
    }

    [TestMethod]
    public void Apply_changes_mutate_in_place_so_prior_references_observe_new_values()
    {
        var registry = new SettlementRegistry();
        var oak = Candidate("core:oak", "橡木城", 0, 0, 10);
        registry.Apply(new[] { oak });
        Assert.IsTrue(registry.TryGet(oak.Id, out var held));

        registry.Apply(new[] { Candidate("core:oak", "港湾", 8, -3, 40, new VoxelDefinitionId("core", "ferrous-ore")) });

        Assert.AreSame(held, registry.Items.Single());
        Assert.AreEqual("港湾", held.Name);
        Assert.AreEqual(new MapPosition(8, -3), held.Position);
        Assert.AreEqual(40, held.Population);
        Assert.IsTrue(held.AvailableRawGoodIds.Contains(new VoxelDefinitionId("core", "ferrous-ore")));
    }

    [TestMethod]
    public void Apply_removes_missing_ids_and_keeps_unrelated_entities()
    {
        var registry = new SettlementRegistry();
        var oak = Candidate("core:oak", "橡木城", 0, 0, 10);
        var pine = Candidate("core:pine", "松木城", 1, 1, 11);
        registry.Apply(new[] { oak, pine });

        registry.Apply(new[] { pine });

        Assert.AreEqual(1, registry.Count);
        Assert.IsFalse(registry.TryGet(oak.Id, out _));
        Assert.IsTrue(registry.TryGet(pine.Id, out var remaining));
        Assert.AreEqual("松木城", remaining.Name);
    }

    [TestMethod]
    public void Apply_can_relocate_an_existing_settlement()
    {
        var registry = new SettlementRegistry();
        var oak = Candidate("core:oak", "橡木城", 1, 1, 10);
        registry.Apply(new[] { oak });
        Assert.IsTrue(registry.TryGet(oak.Id, out var held));

        registry.Apply(new[] { Candidate("core:oak", "橡木城", 9, 9, 10) });

        Assert.AreSame(held, registry.Items.Single());
        Assert.AreEqual(new MapPosition(9, 9), held.Position);
    }

    private static SettlementCandidate Candidate(
        string id,
        string name,
        int x,
        int y,
        int population,
        params VoxelDefinitionId[] resources) =>
        new(
            new SettlementId(id),
            new SettlementTypeId("city"),
            name,
            new MapPosition(x, y),
            population,
            resources.ToHashSet());
}

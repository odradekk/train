using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class NodeVoxelCountTests
{
    [TestMethod]
    public void CountsTemplateAndEveryInstanceAndCreatureAtTheSelectedNode()
    {
        var location = new WorldLocation.Node(new(2));
        InstanceId[] ids = [new InstanceId.Building(new(1)), new InstanceId.Building(new(2)),
            new InstanceId.Vehicle(new(1)), new InstanceId.Vehicle(new(2))];
        var core = new FixedCoreReadView
        {
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [location] = new Lookup<LocationView>.Found(new(location, [], [.. ids], [new(1)], [],
                    new NodeOccupant.Settlement(new(1), SettlementRelation.PlayerOwned)))
            },
            Instances = ids.ToDictionary(id => id, id => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(
                new(id, new(id is InstanceId.Building ? "building" : "vehicle"), default,
                    location, default, null!, default, null!, [], []))),
            CreatureGroups = new Dictionary<CreatureGroupId, Lookup<CreatureGroupView>>
            {
                [new(1)] = new Lookup<CreatureGroupView>.Found(new(new(1), null, location, null, default, 0,
                    [new(new(1), new("creature"), 10, 10), new(new(2), new("creature"), 10, 10)]))
            }
        };
        var content = new FixedContentView
        {
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(new(new(1), new("template"),
                    new(new("ground"), Scene(3), new("ground", "")), [], new("template", "")))
            },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            {
                [new("building")] = new ContentLookup<DesignEvaluation>.Found(
                    new DesignEvaluation.Complete(new("building"), default, null!, null, Scene(5), true, [])),
                [new("vehicle")] = new ContentLookup<DesignEvaluation>.Found(
                    new DesignEvaluation.Complete(new("vehicle"), default, null!, null, Scene(7), true, []))
            },
            Creatures = new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
            {
                [new("creature")] = new ContentLookup<CreatureAppearanceProjection>.Found(
                    new(new("creature"), Scene(11), new("creature", "")))
            }
        };

        // 模板 3 + 两栋建筑各 5 + 两辆车各 7 + 两只生物各 11。
        Assert.AreEqual(49L, NodeVoxelCount.Read(core, content, new(2)));
    }

    [TestMethod]
    public void ANodeWithoutCompoundsHasZeroCells()
    {
        var core = At(new NodeOccupant.Empty());
        Assert.AreEqual(0L, NodeVoxelCount.Read(core, new FixedContentView(), new(2)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrIncompleteTemplateLeavesTheCountUnknown(bool present)
    {
        var content = new FixedContentView
        {
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = present ? new ContentLookup<SettlementLayoutProjection>.Found(new(new(1), new("template"),
                    new(new("ground"), Scene(3) with { OccupancyComplete = false }, new("ground", "")), [], new("template", "")))
                    : new ContentLookup<SettlementLayoutProjection>.NotFound()
            }
        };
        Assert.IsNull(NodeVoxelCount.Read(At(new NodeOccupant.Settlement(new(1), default)), content, new(2)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrIncompleteInstanceDesignLeavesTheCountUnknown(bool present)
    {
        var location = new WorldLocation.Node(new(2));
        var id = new InstanceId.Vehicle(new(1));
        var core = new FixedCoreReadView
        {
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            { [location] = new Lookup<LocationView>.Found(new(location, [], [id], [], [], new NodeOccupant.Empty())) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            { [id] = new Lookup<InstanceView>.Found(new(id, new("design"), default, location, default, null!, default, null!, [], [])) }
        };
        var content = new FixedContentView
        {
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            {
                [new("design")] = present ? new ContentLookup<DesignEvaluation>.Found(new DesignEvaluation.Complete(
                    new("design"), default, null!, null, Scene(5) with { OccupancyComplete = false }, true, []))
                    : new ContentLookup<DesignEvaluation>.NotFound()
            }
        };
        Assert.IsNull(NodeVoxelCount.Read(core, content, new(2)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrIncompleteCreatureModelLeavesTheCountUnknown(bool present)
    {
        var location = new WorldLocation.Node(new(2));
        var core = new FixedCoreReadView
        {
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            { [location] = new Lookup<LocationView>.Found(new(location, [], [], [new(1)], [], new NodeOccupant.Empty())) },
            CreatureGroups = new Dictionary<CreatureGroupId, Lookup<CreatureGroupView>>
            { [new(1)] = new Lookup<CreatureGroupView>.Found(new(new(1), null, location, null, default, 0,
                [new(new(1), new("creature"), 10, 10)])) }
        };
        var content = new FixedContentView
        {
            Creatures = new Dictionary<CreatureDefinitionId, ContentLookup<CreatureAppearanceProjection>>
            {
                [new("creature")] = present ? new ContentLookup<CreatureAppearanceProjection>.Found(
                    new(new("creature"), Scene(11) with { OccupancyComplete = false }, new("creature", "")))
                    : new ContentLookup<CreatureAppearanceProjection>.NotFound()
            }
        };
        Assert.IsNull(NodeVoxelCount.Read(core, content, new(2)));
    }

    [TestMethod]
    public void ALairContributesItsSceneModel()
    {
        var location = new WorldLocation.Node(new(2));
        var core = new FixedCoreReadView
        {
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [location] = new Lookup<LocationView>.Found(new(location, [], [], [], [], new NodeOccupant.Lair(new(1))))
            },
            Lairs = new Dictionary<LairId, Lookup<LairView>>
            {
                [new(1)] = new Lookup<LairView>.Found(new(new(1), new(2), new("lair"), []))
            }
        };
        var content = new FixedContentView
        {
            Lairs = new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>
            {
                [new("lair")] = new ContentLookup<LairAppearanceProjection>.Found(new(new("lair"),
                    new(new("model"), Scene(7), new("model", "")), null!))
            }
        };
        Assert.AreEqual(7L, NodeVoxelCount.Read(core, content, new(2)));
    }

    private static FixedCoreReadView At(NodeOccupant occupant)
    {
        var location = new WorldLocation.Node(new(2));
        return new FixedCoreReadView
        {
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            { [location] = new Lookup<LocationView>.Found(new(location, [], [], [], [], occupant)) }
        };
    }

    private static DesignSceneProjection Scene(int count) => new(null, null, null, true, null, null,
        Enumerable.Range(0, count).Select(x => new SceneVoxelProjection(new(x, 0, 0), new("voxel"),
            VoxelOrientation.PxPy, null, null, new(1), null!, [], new(x, 0, 0), VoxelOrientation.PxPy, false)).ToImmutableArray(),
        [], [], [], []);
}

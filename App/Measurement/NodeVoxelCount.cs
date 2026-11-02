using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Measurement;

internal static class NodeVoxelCount
{
    /// <summary>按节点内每份内容模型的完整设计计数；不含程序地形、轨道和道床。
    /// Decision note: 2026-11-01-node-memory-remaining-gap
    /// </summary>
    internal static long? Read(ICoreReadView core, IContentView content, NodeId node)
    {
        if (core.FindLocation(new WorldLocation.Node(node)) is not Lookup<LocationView>.Found location) return null;
        long? total = 0;
        if (location.Value.Occupant is NodeOccupant.Settlement settlement)
            total += content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout
                ? Cells(layout.Value.SceneModel.Scene) : null;
        else if (location.Value.Occupant is NodeOccupant.Lair lair)
            total += core.FindLair(lair.Id) is Lookup<LairView>.Found found &&
                content.FindLair(found.Value.Type) is ContentLookup<LairAppearanceProjection>.Found appearance
                    ? Cells(appearance.Value.SceneModel.Scene) : null;

        var designs = new Dictionary<DesignHash, long?>();
        foreach (var id in location.Value.Instances)
        {
            if (core.FindInstance(id) is not Lookup<InstanceView>.Found instance) return null;
            if (!designs.TryGetValue(instance.Value.Design, out var count))
            {
                count = content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found
                    { Value: DesignEvaluation.Complete design } ? Cells(design.Scene) : null;
                designs.Add(instance.Value.Design, count);
            }
            total += count;
        }

        var creatures = new Dictionary<CreatureDefinitionId, long?>();
        foreach (var id in location.Value.CreatureGroups)
        {
            if (core.FindCreatureGroup(id) is not Lookup<CreatureGroupView>.Found group) return null;
            foreach (var creature in group.Value.Creatures)
            {
                if (!creatures.TryGetValue(creature.Definition, out var count))
                {
                    count = content.FindCreature(creature.Definition) is ContentLookup<CreatureAppearanceProjection>.Found appearance
                        ? Cells(appearance.Value.Scene) : null;
                    creatures.Add(creature.Definition, count);
                }
                total += count;
            }
        }
        return total;
    }

    private static long? Cells(DesignSceneProjection scene) => scene.OccupancyComplete ? scene.Voxels.Length : null;
}

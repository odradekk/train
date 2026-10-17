using Train.Core.Contracts;

namespace Train.Core.World;

internal static class InstanceCapabilities
{
    internal static IEnumerable<PartProjection> EffectiveParts(WorldState world, CoreContentProjection content, InstanceState instance,
        Func<PartProjection, bool>? include = null)
    {
        var design = world.Designs.Single(d => d.Design == instance.Design);
        if (Math.Min(instance.StructureDurability, design.MaxStructureDurability) <= 0) yield break;
        var current = content.Designs.SingleOrDefault(d => d.Design == design.Design);
        foreach (var part in design.Parts)
        {
            if (include is not null && !include(part)) continue;
            if (DefinitionMissing(world, content, current, part)
                || Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) <= 0) continue;
            yield return part;
        }
    }

    internal static bool DefinitionMissing(WorldState world, CoreContentProjection content, DesignHash design, PartProjection part)
        => DefinitionMissing(world, content, content.Designs.SingleOrDefault(d => d.Design == design), part);

    private static bool DefinitionMissing(WorldState world, CoreContentProjection content, DesignProjection? current, PartProjection part)
    {
        GoodId? definitionGood = part.Definition switch
        {
            CoreDefinitionReference.Voxel v => new GoodId.Voxel(v.Id),
            CoreDefinitionReference.Composite c => new GoodId.Composite(c.Id),
            _ => null
        };
        return part.Missing
            || definitionGood is not null && WorldGoods.DefinitionMissing(world, content, definitionGood)
            || current is not null && (current.MissingReferences.Any(m => m.Reference == part.Definition)
                || current.Parts.Any(p => p.Id == part.Id && p.Missing));
    }
}

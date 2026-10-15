using Train.Core.Contracts;

namespace Train.Core.World;

internal static class InstanceCapabilities
{
    internal static IEnumerable<PartProjection> EffectiveParts(WorldState world, CoreContentProjection content, InstanceState instance)
    {
        var design = world.Designs.Single(d => d.Design == instance.Design);
        if (Math.Min(instance.StructureDurability, design.MaxStructureDurability) <= 0) yield break;
        var current = content.Designs.SingleOrDefault(d => d.Design == design.Design);
        foreach (var part in design.Parts)
        {
            GoodId? definitionGood = part.Definition switch
            {
                CoreDefinitionReference.Voxel v => new GoodId.Voxel(v.Id),
                CoreDefinitionReference.Composite c => new GoodId.Composite(c.Id),
                _ => null
            };
            if (part.Missing || Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) <= 0
                || definitionGood is not null && WorldGoods.DefinitionMissing(world, content, definitionGood)
                || current is not null && (current.MissingReferences.Any(m => m.Reference == part.Definition)
                    || current.Parts.Any(p => p.Id == part.Id && p.Missing))) continue;
            yield return part;
        }
    }
}

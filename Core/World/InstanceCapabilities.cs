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
        for (var index = 0; index < design.Parts.Length; index++)
        {
            var part = design.Parts[index];
            if (include is not null && !include(part)) continue;
            if (DefinitionMissing(content, current, part)
                || Math.Min(instance.Parts[index].Current, part.MaxDurability) <= 0) continue;
            yield return part;
        }
    }

    internal static bool DefinitionMissing(WorldState world, CoreContentProjection content, DesignHash design, PartProjection part)
        => DefinitionMissing(content, content.Designs.SingleOrDefault(d => d.Design == design), part);

    internal static bool DefinitionMissing(CoreContentProjection content, DesignProjection? current, PartProjection part)
    {
        if (part.Missing) return true;
        if (part.Definition is CoreDefinitionReference.Voxel or CoreDefinitionReference.Composite)
        {
            foreach (var missing in content.MissingReferences)
                if (missing.Reference == part.Definition) return true;
            var found = false;
            foreach (var good in content.Goods)
                if ((part.Definition, good.Good) switch
                    {
                        (CoreDefinitionReference.Voxel definition, GoodId.Voxel id) => definition.Id == id.Definition,
                        (CoreDefinitionReference.Composite definition, GoodId.Composite id) => definition.Id == id.Definition,
                        _ => false
                    })
                {
                    found = true;
                    break;
                }
            if (!found) return true;
        }
        if (current is null) return false;
        foreach (var missing in current.MissingReferences)
            if (missing.Reference == part.Definition) return true;
        foreach (var specification in current.Parts)
            if (specification.Id == part.Id) return specification.Missing;
        return false;
    }
}

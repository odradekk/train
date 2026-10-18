using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Construction;

internal static class ConstructionReturns
{
    internal static ImmutableArray<DesignReturnUnit> AllItems(DesignProjection design)
    {
        var nestedParts = design.ReturnUnits.SelectMany(u => u.Parts).ToHashSet();
        var items = design.ReturnUnits.ToBuilder();
        foreach (var part in design.Parts.Where(p => !nestedParts.Contains(p.Id)))
            items.Add(new(part.RepairGood, 1, [part.Id], [], []));
        foreach (var good in design.StructuralGoods)
        {
            var quantity = checked(good.Quantity - design.ReturnUnits.SelectMany(u => u.StructuralGoods)
                .Where(g => g.Good == good.Good).Sum(g => g.Quantity));
            if (quantity > 0) items.Add(new(good.Good, quantity, [], [new(good.Good, quantity)], []));
        }
        return WorldInputs.NormalizeReturns(items.ToImmutable());
    }

    internal static (ImmutableArray<GoodsQuantity> Goods, long DamagedParts) Calculate(WorldState world,
        CoreContentProjection content, InstanceState instance, ImmutableArray<DesignReturnUnit> items)
    {
        var design = world.Designs.Single(d => d.Design == instance.Design);
        var returned = new Dictionary<GoodId, long>();
        var structural = new Dictionary<GoodId, long>();
        var damaged = new HashSet<PartId>();
        foreach (var item in items)
        {
            if (!item.MissingReferences.IsEmpty || WorldGoods.DefinitionMissing(world, content, item.Good)) continue;
            var parts = item.Parts.Select(id => design.Parts.Single(p => p.Id == id)).ToArray();
            if (parts.Any(p => InstanceCapabilities.DefinitionMissing(world, content, design.Design, p)
                    || instance.Parts.Single(d => d.Id == p.Id).Current == 0)) continue;
            if (item.Good is GoodId.Voxel && item.Parts.IsEmpty)
                structural[item.Good] = checked(structural.GetValueOrDefault(item.Good) + item.Quantity);
            else
            {
                returned[item.Good] = checked(returned.GetValueOrDefault(item.Good) + item.Quantity);
                foreach (var part in parts)
                    if (Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) < part.MaxDurability)
                        damaged.Add(part.Id);
            }
        }
        foreach (var (good, count) in structural)
        {
            var quantity = design.MaxStructureDurability == 0 ? 0 : checked((long)((Int128)count
                * Math.Min(instance.StructureDurability, design.MaxStructureDurability) / design.MaxStructureDurability));
            if (quantity > 0) returned[good] = checked(returned.GetValueOrDefault(good) + quantity);
        }
        return (returned.OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
            .Select(g => new GoodsQuantity(g.Key, g.Value)).ToImmutableArray(), damaged.Count);
    }
}

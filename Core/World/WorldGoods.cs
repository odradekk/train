using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldGoods
{
    internal static bool DefinitionMissing(WorldState world, CoreContentProjection content, GoodId good)
    {
        if (WorldReferences.GoodReference(good) is { } reference
            && content.MissingReferences.Any(m => m.Reference == reference)) return true;
        return Definition(world, content, good) is null;
    }

    internal static GoodDefinitionProjection? Definition(WorldState world, CoreContentProjection content, GoodId good)
        => good is GoodId.Assembly ? world.DesignGoods.SingleOrDefault(g => g.Good == good)
            : content.Goods.SingleOrDefault(g => g.Good == good);
}

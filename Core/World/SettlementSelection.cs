using Train.Core.Contracts;

namespace Train.Core.World;

internal static class SettlementSelection
{
    internal static (SettlementTypeProjection Type, SettlementTemplateProjection Template)? Choose(
        CoreContentProjection content, TerrainTypeId terrain, ulong typeRandom,
        Func<SettlementTypeId, ulong> templateRandom)
    {
        var types = content.SettlementTypes.Where(t => content.SettlementTemplates.Any(p => p.Type == t.Id))
            .OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToArray();
        if (types.Length == 0) return null;
        var preferred = types.Where(t => t.PreferredTerrain.IsEmpty || t.PreferredTerrain.Contains(terrain)).ToArray();
        var type = Weighted(preferred.Length == 0 ? types : preferred, t => t.Weight, typeRandom);
        var templates = content.SettlementTemplates.Where(t => t.Type == type.Id)
            .OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToArray();
        return (type, Weighted(templates, t => t.Weight, templateRandom(type.Id)));
    }

    internal static T Weighted<T>(T[] values, Func<T, int> weight, ulong random)
    {
        var selected = WorldRandom.Integer(random, 0, values.Sum(v => (long)weight(v)) - 1);
        foreach (var value in values)
        {
            selected -= weight(value);
            if (selected < 0) return value;
        }
        throw new InvalidOperationException("权重选择没有结果。");
    }
}

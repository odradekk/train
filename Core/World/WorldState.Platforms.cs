using Train.Core.Contracts;

namespace Train.Core.World;

internal sealed partial record WorldState
{
    internal static int CountPlatforms(SettlementState settlement, IReadOnlyDictionary<InstanceId, InstanceState> instances,
        IReadOnlyDictionary<DesignHash, DesignProjection> designs)
    {
        var platforms = 0;
        foreach (var plot in settlement.Plots)
        {
            if (plot.Building is not { } building) continue;
            var instance = instances[new InstanceId.Building(building)];
            var design = designs[instance.Design];
            if (design.Kind is not DesignKind.Building { Type: BuildingKind.Station }
                || Math.Min(instance.StructureDurability, design.MaxStructureDurability) == 0) continue;
            platforms += design.Parts.Count(part => part.Capabilities.Any(c => c is PartCapability.Platform)
                && Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) > 0);
        }
        return platforms;
    }
}

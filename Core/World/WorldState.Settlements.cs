using System.Collections.Immutable;
using Train.Core.Combat;
using Train.Core.Settlements;
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

    internal static SettlementState CreateSettlement(SettlementId id, NodeId node, SettlementTypeId type,
        SettlementRelation relation, long population, SettlementTemplateProjection template,
        PlotId firstPlot, BuildingId firstBuilding, InventoryId inventory)
    {
        var building = firstBuilding.Value;
        var plots = template.Plots.Select((plot, index) => new PlotState(new(checked(firstPlot.Value + (ulong)index)),
            plot.Number, plot.Size, plot.PrebuiltDesign is null ? null : new BuildingId(building++))).ToImmutableArray();
        var facilities = template.ProductionPlans.Where(p => p.Capability is CapabilityKind.Production or CapabilityKind.Extraction)
            .Select(p => (Plan: p, Building: plots.Single(plot => plot.TemplateNumber == p.PlotNumber).Building))
            .Where(p => p.Building is not null).OrderBy(p => p.Building!.Value.Value).ThenBy(p => p.Plan.Capability)
            .Select(p => new ProductionFacilityState(new(p.Building!.Value, p.Plan.Capability), null, [], 0, [], 0, 0, 0))
            .ToImmutableArray();
        return new(id, node, type, relation, population, template, plots, inventory, [], 0, facilities,
            PopulationState.Initial, CombatRules.DefaultSettlementEngagement);
    }
}

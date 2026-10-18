using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Production;

internal static class ProductionCommands
{
    internal static void Validate(CoreCommand.SetProductionPlan command)
    {
        ArgumentNullException.ThrowIfNull(command.Facility);
        ArgumentNullException.ThrowIfNull(command.Process);
        if (command.Facility.Building.Value == 0)
            throw new ArgumentException("建筑编号必须非零。", nameof(command));
        if (!Enum.IsDefined(command.Facility.Capability) || command.Facility.Capability == CapabilityKind.Construction)
            throw new ArgumentException("生产计划只能使用加工或开采能力。", nameof(command));
        var definition = command.Process switch
        {
            ProcessRef.Production production => production.Definition.Value,
            ProcessRef.Recovery recovery => recovery.Definition.Value,
            _ => throw new ArgumentException("流程种类无效。", nameof(command))
        };
        if (!WorldFormat.IsIdentity(definition))
            throw new ArgumentException("流程定义身份无效。", nameof(command));
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand.SetProductionPlan command)
    {
        var facility = command.Facility;
        var subject = new WorldObjectId.Instance(new InstanceId.Building(facility.Building));
        var settlement = world.Settlements.FirstOrDefault(s => s.Plots.Any(p => p.Building == facility.Building));
        var instance = world.Instances.SingleOrDefault(i => i.Id == new InstanceId.Building(facility.Building));
        if (settlement is null || instance is null || !settlement.Plots.Any(p => p.Building == facility.Building))
            return Reject("object_missing", "Facility");
        if (settlement.Relation != SettlementRelation.PlayerOwned || instance.Ownership != SettlementRelation.PlayerOwned)
            return Reject("not_owned", "Facility");
        var design = world.Designs.Single(d => d.Design == instance.Design);
        if (Math.Min(instance.StructureDurability, design.MaxStructureDurability) <= 0)
            return Reject("instance_destroyed", "Facility");
        if (!design.Parts.Any(part => part.Capabilities.Any(capability => facility.Capability == CapabilityKind.Production
            ? capability is PartCapability.Production : capability is PartCapability.Extraction)))
            return Reject("conditions_not_met", "Facility.Capability");
        var process = ProductionQueries.Process(content, command.Process);
        if (process is null) return Reject("content_missing", "Process");
        if ((process.Details is ProcessDetails.Extraction) != (facility.Capability == CapabilityKind.Extraction))
            return Reject("conditions_not_met", "Process");
        if (design.Kind is not DesignKind.Building building || !building.FacilityTags.Contains(process.RequiredFacilityTag))
            return Reject("conditions_not_met", "Facility.Tags");

        var plot = settlement.Plots.Single(p => p.Building == facility.Building);
        var plans = settlement.Template.ProductionPlans
            .Where(p => p.PlotNumber != plot.TemplateNumber || p.Capability != facility.Capability)
            .Append(new TemplateProductionPlan(plot.TemplateNumber, facility.Capability, command.Process, command.Enabled, command.Priority))
            .OrderBy(p => p.PlotNumber).ThenBy(p => p.Capability).ToImmutableArray();
        var facilities = settlement.ProductionFacilities;
        if (!facilities.Any(f => f.Facility == facility))
            facilities = facilities.Add(new ProductionFacilityState(facility, null, [], 0, [], 0, 0, 0));
        var prepared = world with
        {
            Settlements = world.Settlements.Select(s => s.Id != settlement.Id ? s : s with
            {
                Template = s.Template with { ProductionPlans = plans },
                ProductionFacilities = facilities.OrderBy(f => f.Facility.Building.Value).ThenBy(f => f.Facility.Capability).ToImmutableArray()
            }).ToImmutableArray()
        };
        return new CommandEffect.Accepted(content, [], prepared with { DefinitionDependencies = WorldReferences.Dependencies(prepared, content) });

        CommandEffect Reject(string code, string field) => new CommandEffect.Rejected([new(code, field, subject, [])]);
    }
}

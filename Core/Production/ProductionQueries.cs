using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Settlements;
using Train.Core.World;

namespace Train.Core.Production;

internal static class ProductionQueries
{
    internal static FacilityView? Find(WorldState world, CoreContentProjection content, FacilityKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Building.Value == 0 || !Enum.IsDefined(key.Capability))
            throw new ArgumentException("设施须提供非零建筑编号和有效能力种类。", nameof(key));
        if (key.Capability == CapabilityKind.Construction)
            throw new NotImplementedException($"FindFacility {key.Capability} 尚未实现。");
        var settlement = world.Settlements.FirstOrDefault(s => s.Plots.Any(p => p.Building == key.Building));
        if (settlement is null) return null;
        var instance = world.Instances.Single(i => i.Id == new InstanceId.Building(key.Building));
        var design = world.Designs.Single(d => d.Design == instance.Design);
        var plot = settlement.Plots.Single(p => p.Building == key.Building);
        var plan = settlement.Template.ProductionPlans.SingleOrDefault(p => p.PlotNumber == plot.TemplateNumber
            && p.Capability == key.Capability);
        if (plan is null && !design.Parts.Any(p => p.Capabilities.Any(c => key.Capability == CapabilityKind.Production
            ? c is PartCapability.Production : c is PartCapability.Extraction))) return null;
        var runtime = settlement.ProductionFacilities.SingleOrDefault(f => f.Facility == key);
        var capability = Capability(world, content, key);
        var process = plan is null ? null : Process(content, plan.Process);
        var state = ProductionState.Running;
        string? problem = null;
        if (plan is not null && process is null) { state = ProductionState.ProcessMissing; problem = "content_missing"; }
        else if (plan is null || !plan.Enabled) state = ProductionState.Disabled;
        else if (capability == 0) { state = ProductionState.ZeroCapability; problem = "capability_zero"; }
        else if ((process!.Details is ProcessDetails.Extraction) != (key.Capability == CapabilityKind.Extraction)
            || design.Kind is not DesignKind.Building building || !building.FacilityTags.Contains(process.RequiredFacilityTag)
            || !process.AllowedTerrainTypes.IsEmpty && !process.AllowedTerrainTypes.Contains(world.Nodes.Single(n => n.Id == settlement.Node).Terrain.TypeId))
        { state = ProductionState.ConditionsNotMet; problem = "conditions_not_met"; }
        else if (runtime is { OutputBuffer.IsEmpty: false }) { state = ProductionState.OutputBlocked; problem = "output_blocked"; }
        else if (runtime?.Batch is null && (!Inputs(process.Details).All(input =>
            !WorldGoods.DefinitionMissing(world, content, input.Good)
            && world.Inventories.Single(i => i.Id == settlement.Inventory).Goods.Any(g => g.Good == input.Good && g.Quantity >= input.Quantity))
            || process.Details is ProcessDetails.Extraction extraction
                && (WorldGoods.DefinitionMissing(world, content, extraction.Extracts.Good)
                    || !world.Nodes.Single(n => n.Id == settlement.Node).Resources.Any(r =>
                        r.Good == extraction.Extracts.Good && r.Quantity >= extraction.Extracts.Quantity))))
        { state = ProductionState.WaitingForInputs; problem = "inputs_missing"; }
        var batch = runtime?.Batch;
        return new(key, plan?.Process, plan?.Enabled ?? false, plan?.Priority ?? 0, state, capability,
            runtime?.LastActualWorkPerSecond ?? 0, runtime?.LastLimits ?? [],
            batch is null ? null : new(batch.Process, batch.StartedFromRevision, batch.FrozenWorkRequired,
                batch.ProgressWork, batch.ProgressWork / batch.FrozenWorkRequired, batch.FrozenOutputs,
                process is not null && (process.Revision != batch.StartedFromRevision
                    || process.WorkRequired != batch.FrozenWorkRequired || process.PowerPerWork != batch.FrozenPowerPerWork
                    || process.LaborPerWork != batch.FrozenLaborPerWork
                    || !Outputs(process.Details).OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).SequenceEqual(batch.FrozenOutputs))),
            runtime?.OutputBuffer ?? [], [], problem is null ? []
                : [new(problem, "Facility", new WorldObjectId.Instance(new InstanceId.Building(key.Building)), [])]);
    }

    internal static double Capability(WorldState world, CoreContentProjection content, FacilityKey key)
    {
        var capabilities = BuildingCapabilities.Read(world, content, key.Building);
        return key.Capability == CapabilityKind.Extraction ? capabilities.Extraction : capabilities.Production;
    }

    internal static ProcessProjection? Process(CoreContentProjection content, ProcessRef reference)
        => content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Process(reference))
            ? null : content.Processes.SingleOrDefault(p => WorldReferences.Matches(p.Details, reference));

    internal static ImmutableArray<GoodsQuantity> Inputs(ProcessDetails details) => details switch
    {
        ProcessDetails.Production p => p.Inputs,
        ProcessDetails.Recovery p => p.Inputs,
        _ => []
    };

    internal static ImmutableArray<GoodsQuantity> Outputs(ProcessDetails details) => details switch
    {
        ProcessDetails.Production p => p.Outputs,
        ProcessDetails.Recovery p => p.Outputs,
        ProcessDetails.Extraction p => [p.Extracts],
        _ => []
    };

    internal static ImmutableArray<CoreEvent> StateChanges(WorldState before, CoreContentProjection beforeContent,
        WorldState after, CoreContentProjection afterContent, SettlementId id)
    {
        var settlement = after.Settlements.Single(s => s.Id == id);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        foreach (var key in settlement.Plots.Where(p => p.Building is not null).OrderBy(p => p.Building!.Value.Value)
            .SelectMany(p => new[] { new FacilityKey(p.Building!.Value, CapabilityKind.Production),
                new FacilityKey(p.Building!.Value, CapabilityKind.Extraction) }))
        {
            var old = Find(before, beforeContent, key);
            var current = Find(after, afterContent, key);
            if (old is not null && current is not null && old.State != current.State)
                events.Add(new CoreEvent.ProductionStateChanged(key, old.State, current.State, current.Blockers));
        }
        return events.ToImmutable();
    }
}

using System.Collections.Immutable;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Settlements;
using Train.Core.World;

namespace Train.Core.Production;

internal static class ProductionQueries
{
    internal static FacilityView? Find(WorldState world, CoreContentProjection content, FacilityKey key)
    {
        if (Evaluate(world, content, key) is not { } result) return null;
        var (plan, runtime, capability, _, state, problem) = result;
        var batch = runtime?.Batch;
        var batchProcess = batch is null ? null : Process(content, batch.Process);
        return new(key, plan?.Process, plan?.Enabled ?? false, plan?.Priority ?? 0, state, capability,
            runtime?.LastActualWorkPerSecond ?? 0, runtime?.LastLimits ?? [],
            batch is null ? null : new(batch.Process, batch.StartedFromRevision, batch.FrozenWorkRequired,
                batch.ProgressWork, batch.ProgressWork / batch.FrozenWorkRequired, batch.FrozenOutputs,
                batchProcess is not null && (batchProcess.Revision != batch.StartedFromRevision
                    || batchProcess.WorkRequired != batch.FrozenWorkRequired || batchProcess.PowerPerWork != batch.FrozenPowerPerWork
                    || batchProcess.LaborPerWork != batch.FrozenLaborPerWork
                    || !Outputs(batchProcess.Details).OrderBy(g => g.Good, WorldReferences.GoodComparer).SequenceEqual(batch.FrozenOutputs))),
            runtime?.OutputBuffer ?? [], [], Blockers(key, problem));
    }

    private readonly record struct FacilityEvaluation(TemplateProductionPlan? Plan, ProductionFacilityState? Runtime,
        double Capability, ProcessProjection? Process, ProductionState State, string? Problem);

    internal static bool CanStart(WorldState world, CoreContentProjection content, FacilityKey key, double capability)
        => Evaluate(world, content, key, capability)?.State == ProductionState.Running;

    private static FacilityEvaluation? Evaluate(WorldState world, CoreContentProjection content, FacilityKey key,
        double? knownCapability = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Building.Value == 0 || !Enum.IsDefined(key.Capability))
            throw new ArgumentException("设施须提供非零建筑编号和有效能力种类。", nameof(key));
        if (key.Capability == CapabilityKind.Construction)
            throw new NotImplementedException($"FindFacility {key.Capability} 尚未实现。");
        var settlement = world.Settlements.FirstOrDefault(s => s.Plots.Any(p => p.Building == key.Building));
        if (settlement is null) return null;
        var plot = settlement.Plots.Single(p => p.Building == key.Building);
        return Evaluate(world, content, settlement, plot, key, knownCapability);
    }

    private static FacilityEvaluation? Evaluate(WorldState world, CoreContentProjection content,
        SettlementState settlement, PlotState plot, FacilityKey key, double? knownCapability)
    {
        var id = new InstanceId.Building(key.Building);
        var instance = world.Instances.Single(i => i.Id == id);
        var design = world.Designs.Single(d => d.Design == instance.Design);
        var plan = settlement.Template.ProductionPlans.SingleOrDefault(p => p.PlotNumber == plot.TemplateNumber
            && p.Capability == key.Capability);
        if (plan is null && !design.Parts.Any(p => p.Capabilities.Any(c => key.Capability == CapabilityKind.Production
            ? c is PartCapability.Production : c is PartCapability.Extraction))) return null;
        var runtime = settlement.ProductionFacilities.SingleOrDefault(f => f.Facility == key);
        var capability = knownCapability ?? Capability(world, content, key);
        var process = plan is null ? null : Process(content, plan.Process);
        var batchProcessMissing = runtime?.Batch is { } active && Process(content, active.Process) is null;
        var state = ProductionState.Running;
        string? problem = null;
        if ((plan is not null && process is null) || batchProcessMissing) { state = ProductionState.ProcessMissing; problem = "content_missing"; }
        else if (plan is null) state = ProductionState.Disabled;
        else if (ConstructionJobs.InstanceLocked(world, id)) { state = ProductionState.Locked; problem = "instance_locked"; }
        else if (!plan.Enabled) state = ProductionState.Disabled;
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
        return new(plan, runtime, capability, process, state, problem);
    }

    private static ImmutableArray<CoreProblem> Blockers(FacilityKey key, string? problem) => problem is null ? []
        : [new(problem, "Facility", new WorldObjectId.Instance(new InstanceId.Building(key.Building)), [])];

    internal static double Capability(WorldState world, CoreContentProjection content, FacilityKey key)
    {
        var capabilities = BuildingCapabilities.Read(world, content, key.Building);
        return key.Capability == CapabilityKind.Extraction ? capabilities.Extraction : capabilities.Production;
    }

    internal static ImmutableArray<(FacilityView View, ProcessProjection? Process, double Rate)> Potential(
        WorldState world, CoreContentProjection content, SettlementState settlement)
    {
        var inputs = PopulationOperations.ReadInputs(world, content);
        var labor = PopulationOperations.Problems(world, settlement, inputs).IsEmpty
            ? settlement.Population * inputs.Current!.LaborRatio : 0;
        var energy = SettlementResources.GeneratedEnergy(world, content, settlement);
        var fuel = SettlementResources.Fuels(world, content).FirstOrDefault();
        var result = ImmutableArray.CreateBuilder<(FacilityView, ProcessProjection?, double)>();
        foreach (var runtime in settlement.ProductionFacilities.Select(f => (Runtime: f, View: Find(world, content, f.Facility)!))
            .OrderBy(f => f.View.Priority).ThenBy(f => f.View.Key.Building.Value).ThenBy(f => f.View.Key.Capability))
        {
            var view = runtime.View;
            var process = view.Process is null ? null : Process(content, view.Process);
            double rate = 0;
            if (view.State is ProductionState.Running or ProductionState.WaitingForInputs or ProductionState.OutputBlocked)
            {
                var demand = new FacilityRequest.Work(view.Key, view.Priority, view.CapabilityWorkPerSecond,
                    process!.PowerPerWork, process.LaborPerWork);
                rate = SettlementResources.AllocateWork(demand, energy, double.PositiveInfinity, labor).WorkPerSecond;
                energy = Math.Max(0, energy - rate * process.PowerPerWork * SimulationTime.StepSeconds);
                labor = Math.Max(0, labor - rate * process.LaborPerWork);
            }
            var consumption = ImmutableArray.CreateBuilder<GoodsRateView>();
            if (rate > 0)
            {
                consumption.AddRange(Inputs(process!.Details).Select(g => new GoodsRateView(g.Good, rate / process.WorkRequired * g.Quantity)));
                if (fuel is not null && process.PowerPerWork > 0)
                    consumption.Add(new(fuel.Good, rate * process.PowerPerWork / fuel.FuelEnergyPerUnit!.Value));
            }
            var rates = consumption.GroupBy(g => g.Good).OrderBy(g => g.Key, WorldReferences.GoodComparer)
                .Select(g => new GoodsRateView(g.Key, g.Sum(r => r.UnitsPerSecond))).ToImmutableArray();
            result.Add((view with { PotentialConsumption = rates }, process, rate));
        }
        return result.ToImmutable();
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
        // 建筑身份不跨聚落迁移，改前地块只需在同一聚落内查找。
        var previousSettlement = before.Settlements.FirstOrDefault(s => s.Id == id);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        // 不可变输入仍是同一份时，边界两侧的能力相同；只在本次比较中复用。
        var sameCapabilities = ReferenceEquals(beforeContent, afterContent) && before.Instances == after.Instances
            && before.Designs == after.Designs && before.DesignGoods == after.DesignGoods
            && before.ConstructionJobs == after.ConstructionJobs;
        foreach (var plot in settlement.Plots.Where(p => p.Building is not null).OrderBy(p => p.Building!.Value.Value))
        {
            var previousPlot = previousSettlement?.Plots.SingleOrDefault(p => p.Building == plot.Building);
            BuildingCapabilities? oldCapabilities = null;
            BuildingCapabilities? currentCapabilities = null;
            ReadOnlySpan<CapabilityKind> kinds = [CapabilityKind.Production, CapabilityKind.Extraction];
            foreach (var capability in kinds)
            {
                // 两边都没有计划时，设施只可能不存在或为 Disabled，不会产生状态变化。
                if (!settlement.Template.ProductionPlans.Any(p => p.PlotNumber == plot.TemplateNumber && p.Capability == capability)
                    && (previousPlot is null || !previousSettlement!.Template.ProductionPlans.Any(p => p.PlotNumber == previousPlot.TemplateNumber
                        && p.Capability == capability))) continue;
                var key = new FacilityKey(plot.Building!.Value, capability);
                if (previousPlot is not null)
                    oldCapabilities ??= BuildingCapabilities.Read(before, beforeContent, key.Building);
                currentCapabilities ??= sameCapabilities && oldCapabilities is { } previousCapabilities
                    ? previousCapabilities : BuildingCapabilities.Read(after, afterContent, key.Building);
                var old = previousPlot is null ? null : Evaluate(before, beforeContent, previousSettlement!, previousPlot, key,
                    capability == CapabilityKind.Extraction ? oldCapabilities?.Extraction : oldCapabilities?.Production);
                var current = Evaluate(after, afterContent, settlement, plot, key, capability == CapabilityKind.Extraction
                    ? currentCapabilities.Value.Extraction : currentCapabilities.Value.Production);
                if (old is { } previous && current is { } next && previous.State != next.State)
                    events.Add(new CoreEvent.ProductionStateChanged(key, previous.State, next.State, Blockers(key, next.Problem)));
            }
        }
        return events.ToImmutable();
    }
}

using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Settlements;
using Train.Core.World;

namespace Train.Core.Production;

internal static class ProductionOperations
{
    internal static ImmutableArray<FacilityRequest> Requests(WorldState world,
        CoreContentProjection content, SettlementId id)
    {
        var settlement = world.Settlements.Single(s => s.Id == id);
        if (settlement.Template.ProductionPlans.IsEmpty) return [];
        var plans = settlement.Template.ProductionPlans.Where(p => p.Capability is CapabilityKind.Production or CapabilityKind.Extraction)
            .Select(p => (Plan: p, Building: settlement.Plots.Single(plot => plot.TemplateNumber == p.PlotNumber).Building))
            .Where(p => p.Building is not null).OrderBy(p => p.Plan.Priority).ThenBy(p => p.Building!.Value.Value)
            .ThenBy(p => p.Plan.Capability).ToArray();
        if (plans.Length == 0) return [];
        var facilities = settlement.ProductionFacilities.ToDictionary(f => f.Facility);
        var requests = ImmutableArray.CreateBuilder<FacilityRequest>();
        foreach (var (plan, building) in plans)
        {
            var key = new FacilityKey(building!.Value, plan.Capability);
            var runtime = facilities[key];
            var view = ProductionQueries.Find(world, content, key)!;
            var process = ProductionQueries.Process(content, plan.Process);
            if (runtime.Batch is { } batch)
            {
                if (process is null || view.CapabilityWorkPerSecond == 0) continue;
                var rate = Math.Min(view.CapabilityWorkPerSecond,
                    (batch.FrozenWorkRequired - batch.ProgressWork) / SimulationTime.StepSeconds);
                requests.Add(new FacilityRequest.Work(key, plan.Priority, rate, batch.FrozenPowerPerWork, batch.FrozenLaborPerWork));
            }
            else if (view.State == ProductionState.Running)
                requests.Add(new FacilityRequest.Batch(key, plan.Priority, ProductionQueries.Inputs(process!.Details),
                    (process.Details as ProcessDetails.Extraction)?.Extracts));
        }

        return requests.ToImmutable();
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Complete(WorldState before,
        CoreContentProjection content, SettlementId id, ImmutableArray<FacilityRequest> requests,
        SettlementResolution resolution, ulong step)
    {
        var world = resolution.World;
        var settlement = world.Settlements.Single(s => s.Id == id);
        if (requests.IsEmpty && settlement.ProductionFacilities.All(f => f.OutputBuffer.IsEmpty
            && f.LastActualWorkPerSecond == 0 && f.LastLimits.IsEmpty && f.LastPowerDemand == 0 && f.LastActualSupply == 0))
            return (world, []);
        var plans = settlement.Template.ProductionPlans.Where(p => p.Capability is CapabilityKind.Production or CapabilityKind.Extraction)
            .Select(p => (Plan: p, Building: settlement.Plots.Single(plot => plot.TemplateNumber == p.PlotNumber).Building))
            .Where(p => p.Building is not null).OrderBy(p => p.Plan.Priority).ThenBy(p => p.Building!.Value.Value)
            .ThenBy(p => p.Plan.Capability).ToArray();
        if (plans.Length == 0) return (world, []);
        var facilities = settlement.ProductionFacilities.ToDictionary(f => f.Facility);
        var accepted = resolution.AcceptedBatches.ToHashSet();
        var allocations = resolution.Work.ToDictionary(a => a.Facility);
        var workRequests = requests.OfType<FacilityRequest.Work>().ToDictionary(r => r.Facility);
        var completed = new List<(FacilityKey Key, ProductionBatchState Batch)>();
        foreach (var (plan, building) in plans)
        {
            var key = new FacilityKey(building!.Value, plan.Capability);
            var runtime = facilities[key] with
            {
                LastActualWorkPerSecond = 0, LastLimits = [], LastPowerDemand = 0, LastActualSupply = 0
            };
            if (accepted.Contains(key))
            {
                var process = ProductionQueries.Process(content, plan.Process)!;
                runtime = runtime with { Batch = new(plan.Process, process.Revision,
                    ProductionQueries.Outputs(process.Details).OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray(),
                    process.WorkRequired, process.PowerPerWork, process.LaborPerWork, 0) };
            }
            else if (allocations.TryGetValue(key, out var allocation))
            {
                var batch = runtime.Batch!;
                var request = workRequests[key];
                var capability = ProductionQueries.Capability(before, content, key);
                var limits = allocation.Limits;
                if (allocation.WorkPerSecond == capability) limits = limits.Insert(0, WorkLimit.Capability);
                var progress = Math.Min(batch.FrozenWorkRequired,
                    batch.ProgressWork + allocation.WorkPerSecond * SimulationTime.StepSeconds);
                runtime = runtime with
                {
                    Batch = batch with { ProgressWork = progress },
                    LastActualWorkPerSecond = allocation.WorkPerSecond, LastLimits = limits,
                    LastPowerDemand = request.MaximumWorkPerSecond * batch.FrozenPowerPerWork,
                    LastActualSupply = allocation.WorkPerSecond * batch.FrozenPowerPerWork
                };
                if (progress >= batch.FrozenWorkRequired)
                {
                    runtime = runtime with { Batch = null, OutputBuffer = batch.FrozenOutputs };
                    completed.Add((key, batch));
                }
            }
            facilities[key] = runtime;
        }

        // 入库在全部批次请求仲裁后提交，不把本步产出再用作本步原料。
        settlement = world.Settlements.Single(s => s.Id == id);
        var inventory = world.Inventories.Single(i => i.Id == settlement.Inventory);
        var goods = inventory.Goods.ToDictionary(g => g.Good, g => g.Quantity);
        var available = InventoryStorage.Settlement(world, content, settlement, checked(step + 1)).Uncommitted;
        foreach (var (plan, building) in plans)
        {
            var key = new FacilityKey(building!.Value, plan.Capability);
            var runtime = facilities[key];
            var remaining = ImmutableArray.CreateBuilder<GoodsQuantity>();
            foreach (var output in runtime.OutputBuffer)
            {
                var units = Math.Min(output.Quantity, available);
                if (units > 0) goods[output.Good] = checked(goods.GetValueOrDefault(output.Good) + units);
                available -= units;
                if (units < output.Quantity) remaining.Add(output with { Quantity = output.Quantity - units });
            }
            facilities[key] = runtime with { OutputBuffer = remaining.ToImmutable() };
        }
        world = world with
        {
            Settlements = world.Settlements.Select(s => s.Id == id ? s with
            {
                ProductionFacilities = facilities.Values.OrderBy(f => f.Facility.Building.Value)
                    .ThenBy(f => f.Facility.Capability).ToImmutableArray()
            } : s).ToImmutableArray(),
            Inventories = world.Inventories.Select(i => i.Id == inventory.Id ? i with
            {
                Goods = goods.OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
                    .Select(g => new GoodsQuantity(g.Key, g.Value)).ToImmutableArray()
            } : i).ToImmutableArray()
        };
        var events = completed.Select(c => (CoreEvent)new CoreEvent.ProductionCompleted(c.Key, c.Batch.Process,
            c.Batch.StartedFromRevision, c.Batch.FrozenOutputs, !facilities[c.Key].OutputBuffer.IsEmpty)).ToImmutableArray();
        return (world, events);
    }
}
